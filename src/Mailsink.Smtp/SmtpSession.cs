using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Mailsink.Core.Accounts;
using Mailsink.Core.Chaos;
using Mailsink.Core.Delivery;
using Mailsink.Core.Diagnostics;
using Mailsink.Core.Domain;
using Mailsink.Core.Networking;
using Mailsink.Core.Options;
using Mailsink.Core.Storage;

namespace Mailsink.Smtp;

public enum SmtpEndpointKind { Capture, Submission, Smtps }

/// <summary>Services shared by every SMTP session.</summary>
public sealed record SmtpServices(
    IMailStore Store,
    LocalDeliveryAgent Delivery,
    IAccountService Accounts,
    IChaosEngine Chaos,
    TranscriptStore Transcripts,
    MetricsCollector Metrics,
    ILogger Logger);

/// <summary>
/// One SMTP conversation (RFC 5321) with ESMTP extensions PIPELINING, SIZE, 8BITMIME, SMTPUTF8,
/// ENHANCEDSTATUSCODES, STARTTLS and AUTH PLAIN/LOGIN/XOAUTH2. Every stage can be faulted by the chaos engine.
/// </summary>
internal sealed class SmtpSession
{
    private const int MaxCommandLine = 64 * 1024;
    private const int MaxDataLine = 1024 * 1024;
    private const int MaxErrors = 25;

    private readonly ProtocolConnection _connection;
    private readonly SmtpEndpointKind _kind;
    private readonly SmtpEndpointOptions _endpoint;
    private readonly MailsinkOptions _options;
    private readonly SmtpServices _services;
    private readonly SessionTranscript _transcript;

    private string? _helo;
    private bool _extended;
    private string? _authUser;
    private string? _mailFrom;
    private readonly List<string> _recipients = [];
    private int _errors;

    public SmtpSession(ProtocolConnection connection, SmtpEndpointKind kind, SmtpEndpointOptions endpoint, MailsinkOptions options, SmtpServices services)
    {
        _connection = connection;
        _kind = kind;
        _endpoint = endpoint;
        _options = options;
        _services = services;
        _transcript = services.Transcripts.Start(kind == SmtpEndpointKind.Capture ? "smtp" : "submission", connection.ClientIp);
        _transcript.Secure = connection.IsSecure;
    }

    private bool StartTlsOffered => !_connection.IsSecure && _connection.CanStartTls && _endpoint.StartTls != TlsMode.None;

    private bool TlsRequired => !_connection.IsSecure && (_endpoint.StartTls == TlsMode.Required);

    private bool AuthAllowed => _connection.IsSecure || !_options.Auth.RequireTls;

    public async Task RunAsync(CancellationToken stopping)
    {
        using var _ = _services.Metrics.TrackSession("smtp");
        try
        {
            if (await ApplyChaosAsync(ChaosStage.Connect, new ChaosContext(_connection.ClientIp), stopping, closeOnResponse: true))
            {
                return;
            }

            await ReplyAsync($"220 {_options.Hostname} ESMTP Mailsink ready", stopping);

            while (!stopping.IsCancellationRequested)
            {
                var line = await ReadLineAsync(MaxCommandLine, stopping);
                if (line is null)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(line);
                var (verb, argument) = Split(text);
                _transcript.Client(verb == "AUTH" ? RedactAuth(text) : text);

                var keepGoing = await DispatchAsync(verb, argument, stopping);
                if (!keepGoing)
                {
                    break;
                }

                if (_errors >= MaxErrors)
                {
                    await ReplyAsync("421 4.7.0 Too many errors, closing connection", stopping);
                    break;
                }
            }
        }
        catch (TimeoutException)
        {
            await TryReplyAsync("421 4.4.2 Timeout, closing connection");
        }
        catch (LineTooLongException)
        {
            await TryReplyAsync("500 5.5.6 Line too long");
        }
        finally
        {
            _transcript.End();
        }
    }

    private async Task<bool> DispatchAsync(string verb, string argument, CancellationToken ct)
    {
        switch (verb)
        {
            case "EHLO":
            case "HELO":
                await HeloAsync(verb == "EHLO", argument, ct);
                return true;
            case "STARTTLS":
                return await StartTlsAsync(ct);
            case "AUTH":
                await AuthAsync(argument, ct);
                return true;
            case "MAIL":
                await MailAsync(argument, ct);
                return true;
            case "RCPT":
                await RcptAsync(argument, ct);
                return true;
            case "DATA":
                await DataAsync(ct);
                return true;
            case "RSET":
                ResetTransaction();
                await ReplyAsync("250 2.0.0 Ok", ct);
                return true;
            case "NOOP":
                await ReplyAsync("250 2.0.0 Ok", ct);
                return true;
            case "VRFY":
                await ReplyAsync("252 2.5.2 Cannot VRFY user, but will accept message", ct);
                return true;
            case "HELP":
                await ReplyAsync("214 2.0.0 Commands: HELO EHLO MAIL RCPT DATA RSET NOOP QUIT VRFY STARTTLS AUTH", ct);
                return true;
            case "QUIT":
                await ReplyAsync("221 2.0.0 Bye", ct);
                return false;
            case "":
                return await ErrorAsync("500 5.5.2 Empty command", ct);
            default:
                return await ErrorAsync("502 5.5.2 Command not recognized", ct);
        }
    }

    private async Task HeloAsync(bool extended, string argument, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            await ErrorAsync("501 5.5.4 Syntax: " + (extended ? "EHLO" : "HELO") + " hostname", ct);
            return;
        }

        if (await ApplyChaosAsync(ChaosStage.Helo, new ChaosContext(_connection.ClientIp), ct))
        {
            return;
        }

        _helo = argument.Trim();
        _extended = extended;
        ResetTransaction();

        if (!extended)
        {
            await ReplyAsync($"250 {_options.Hostname}", ct);
            return;
        }

        var lines = new List<string>
        {
            $"{_options.Hostname} greets {_helo}",
            "PIPELINING",
            "SIZE " + _options.Smtp.MaxMessageSize.ToString(CultureInfo.InvariantCulture),
            "8BITMIME",
            "SMTPUTF8",
            "ENHANCEDSTATUSCODES",
        };
        if (StartTlsOffered)
        {
            lines.Add("STARTTLS");
        }

        if (AuthAllowed && _authUser is null)
        {
            lines.Add("AUTH PLAIN LOGIN XOAUTH2");
        }

        lines.Add("HELP");
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            sb.Append("250").Append(i == lines.Count - 1 ? ' ' : '-').Append(lines[i]);
            if (i < lines.Count - 1)
            {
                sb.Append("\r\n");
            }
        }

        await ReplyAsync(sb.ToString(), ct);
    }

    private async Task<bool> StartTlsAsync(CancellationToken ct)
    {
        if (!StartTlsOffered)
        {
            return await ErrorAsync(_connection.IsSecure ? "503 5.5.1 TLS already active" : "454 4.7.0 TLS not available", ct);
        }

        await ReplyAsync("220 2.0.0 Ready to start TLS", ct);
        await _connection.UpgradeToTlsAsync(ct);
        _transcript.Info("TLS negotiated");
        _transcript.Secure = true;

        // RFC 3207: discard all knowledge obtained before TLS.
        _helo = null;
        _extended = false;
        ResetTransaction();
        return true;
    }

    private async Task AuthAsync(string argument, CancellationToken ct)
    {
        if (_helo is null || !_extended)
        {
            await ErrorAsync("503 5.5.1 Send EHLO first", ct);
            return;
        }

        if (_authUser is not null)
        {
            await ErrorAsync("503 5.5.1 Already authenticated", ct);
            return;
        }

        if (!AuthAllowed)
        {
            await ErrorAsync("538 5.7.11 Encryption required for requested authentication mechanism", ct);
            return;
        }

        if (_mailFrom is not null)
        {
            await ErrorAsync("503 5.5.1 AUTH not permitted during a mail transaction", ct);
            return;
        }

        var parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            await ErrorAsync("501 5.5.4 Syntax: AUTH mechanism", ct);
            return;
        }

        var mechanism = parts[0].ToUpperInvariant();
        var initial = parts.Length > 1 ? parts[1] : null;
        string? username;
        string? secret;
        var isToken = false;

        switch (mechanism)
        {
            case "PLAIN":
            {
                var response = initial is null or "=" ? (initial == "=" ? "" : await ChallengeAsync("", ct)) : initial;
                if (response is null || !TryDecodeBase64(response, out var decoded))
                {
                    await ErrorAsync("501 5.5.2 Cannot decode response", ct);
                    return;
                }

                var fields = decoded.Split('\0');
                if (fields.Length != 3)
                {
                    await ErrorAsync("501 5.5.2 Invalid PLAIN response", ct);
                    return;
                }

                username = fields[1];
                secret = fields[2];
                break;
            }

            case "LOGIN":
            {
                var userResponse = initial ?? await ChallengeAsync("VXNlcm5hbWU6", ct);
                if (userResponse is null || !TryDecodeBase64(userResponse, out var user))
                {
                    await ErrorAsync("501 5.5.2 Cannot decode response", ct);
                    return;
                }

                var passResponse = await ChallengeAsync("UGFzc3dvcmQ6", ct);
                if (passResponse is null || !TryDecodeBase64(passResponse, out var pass))
                {
                    await ErrorAsync("501 5.5.2 Cannot decode response", ct);
                    return;
                }

                username = user;
                secret = pass;
                break;
            }

            case "XOAUTH2":
            {
                var response = initial ?? await ChallengeAsync("", ct);
                if (response is null || !TryDecodeBase64(response, out var decoded) || !TryParseXOAuth2(decoded, out username, out secret))
                {
                    await ErrorAsync("501 5.5.2 Invalid XOAUTH2 response", ct);
                    return;
                }

                isToken = true;
                break;
            }

            default:
                await ErrorAsync("504 5.5.4 Unrecognized authentication type", ct);
                return;
        }

        if (await ApplyChaosAsync(ChaosStage.Auth, new ChaosContext(_connection.ClientIp, User: username), ct))
        {
            return;
        }

        var result = isToken
            ? await _services.Accounts.AuthenticateTokenAsync(username, secret, "smtp", ct)
            : await _services.Accounts.AuthenticateAsync(username, secret, "smtp", ct);
        if (!result.Success)
        {
            await ErrorAsync("535 5.7.8 Authentication credentials invalid", ct);
            return;
        }

        _authUser = result.Address;
        _transcript.User = result.Address;
        await ReplyAsync("235 2.7.0 Authentication successful", ct);
    }

    private async Task MailAsync(string argument, CancellationToken ct)
    {
        if (_helo is null)
        {
            await ErrorAsync("503 5.5.1 Send HELO/EHLO first", ct);
            return;
        }

        if (TlsRequired)
        {
            await ErrorAsync("530 5.7.0 Must issue a STARTTLS command first", ct);
            return;
        }

        if (_endpoint.RequireAuth && _authUser is null)
        {
            await ErrorAsync("530 5.7.0 Authentication required", ct);
            return;
        }

        if (_mailFrom is not null)
        {
            await ErrorAsync("503 5.5.1 Nested MAIL command", ct);
            return;
        }

        if (!TryParsePath(argument, "FROM:", out var address, out var parameters))
        {
            await ErrorAsync("501 5.5.4 Syntax: MAIL FROM:<address>", ct);
            return;
        }

        foreach (var parameter in parameters)
        {
            if (parameter.StartsWith("SIZE=", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(parameter.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var declared)
                && declared > _options.Smtp.MaxMessageSize)
            {
                await ErrorAsync("552 5.3.4 Message size exceeds fixed maximum message size", ct);
                return;
            }
        }

        if (await ApplyChaosAsync(ChaosStage.MailFrom, new ChaosContext(_connection.ClientIp, Sender: address, User: _authUser), ct))
        {
            return;
        }

        _mailFrom = address;
        await ReplyAsync("250 2.1.0 Ok", ct);
    }

    private async Task RcptAsync(string argument, CancellationToken ct)
    {
        if (_mailFrom is null)
        {
            await ErrorAsync("503 5.5.1 Need MAIL command", ct);
            return;
        }

        if (!TryParsePath(argument, "TO:", out var address, out _) || address.Length == 0)
        {
            await ErrorAsync("501 5.5.4 Syntax: RCPT TO:<address>", ct);
            return;
        }

        if (_recipients.Count >= _options.Smtp.MaxRecipients)
        {
            await ReplyAsync("452 4.5.3 Too many recipients", ct);
            return;
        }

        if (await ApplyChaosAsync(ChaosStage.RcptTo, new ChaosContext(_connection.ClientIp, _mailFrom, address, _authUser), ct))
        {
            return;
        }

        _recipients.Add(address);
        await ReplyAsync("250 2.1.5 Ok", ct);
    }

    private async Task DataAsync(CancellationToken ct)
    {
        if (_mailFrom is null)
        {
            await ErrorAsync("503 5.5.1 Need MAIL command", ct);
            return;
        }

        if (_recipients.Count == 0)
        {
            await ErrorAsync("554 5.5.1 No valid recipients", ct);
            return;
        }

        await ReplyAsync("354 End data with <CR><LF>.<CR><LF>", ct);

        using var buffer = new MemoryStream();
        var tooLarge = false;
        while (true)
        {
            var line = await ReadLineAsync(MaxDataLine, ct) ?? throw new IOException("Connection closed during DATA");
            if (line is [(byte)'.'])
            {
                break;
            }

            if (tooLarge)
            {
                continue;
            }

            var content = line.Length > 0 && line[0] == (byte)'.' ? line.AsSpan(1) : line.AsSpan();
            buffer.Write(content);
            buffer.Write("\r\n"u8);
            if (buffer.Length > _options.Smtp.MaxMessageSize)
            {
                tooLarge = true;
            }
        }

        _transcript.Info($"({buffer.Length.ToString(CultureInfo.InvariantCulture)} bytes of message data)");

        if (tooLarge)
        {
            ResetTransaction();
            await ErrorAsync("552 5.3.4 Message size exceeds fixed maximum message size", ct);
            return;
        }

        foreach (var recipient in _recipients)
        {
            if (await ApplyChaosAsync(ChaosStage.Data, new ChaosContext(_connection.ClientIp, _mailFrom, recipient, _authUser), ct))
            {
                ResetTransaction();
                return;
            }
        }

        var source = _kind == SmtpEndpointKind.Capture && _authUser is null ? MessageSource.Smtp : MessageSource.Submission;
        var envelope = new Envelope(_mailFrom, [.. _recipients], source, _helo, _connection.ClientIp, _connection.IsSecure, _authUser, _transcript.Id);
        var captured = await _services.Store.CaptureAsync(buffer.ToArray(), envelope, inCaptureView: true, ct);
        await _services.Delivery.DeliverAsync(captured, envelope, _endpoint.SaveToSent && _authUser is not null, ct);
        _transcript.LinkMessage(captured.Id.ToString());
        _services.Logger.LogDebug("Captured {Id} from {From} to {Count} recipient(s)", captured.Id, _mailFrom, _recipients.Count);

        ResetTransaction();
        await ReplyAsync($"250 2.0.0 Ok: queued as {captured.Id}", ct);
    }

    private void ResetTransaction()
    {
        _mailFrom = null;
        _recipients.Clear();
    }

    /// <summary>Applies the first matching chaos rule. Returns true when the command was answered (or the connection dropped).</summary>
    private async Task<bool> ApplyChaosAsync(ChaosStage stage, ChaosContext context, CancellationToken ct, bool closeOnResponse = false)
    {
        var outcome = _services.Chaos.Evaluate(stage, context);
        if (outcome is null)
        {
            return false;
        }

        _transcript.Info($"chaos rule {outcome.RuleId:N} matched at {stage}");
        if (outcome.Delay > TimeSpan.Zero)
        {
            await Task.Delay(outcome.Delay, ct);
        }

        if (outcome.Drop)
        {
            throw new IOException("Connection dropped by chaos rule");
        }

        if (outcome.Response is { } response)
        {
            await ReplyAsync($"{response.Code.ToString(CultureInfo.InvariantCulture)} {response.Text}", ct);
            if (closeOnResponse)
            {
                return true;
            }

            if (response.Code is >= 400)
            {
                _errors++;
            }

            return true;
        }

        return false;
    }

    private async Task<string?> ChallengeAsync(string challenge, CancellationToken ct)
    {
        await ReplyAsync("334 " + challenge, ct);
        var line = await ReadLineAsync(MaxCommandLine, ct);
        _transcript.Client("****");
        if (line is null)
        {
            return null;
        }

        var text = Encoding.ASCII.GetString(line).Trim();
        return text == "*" ? null : text;
    }

    private async Task<byte[]?> ReadLineAsync(int max, CancellationToken stopping)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(_options.Smtp.CommandTimeout);
        try
        {
            return await _connection.ReadLineAsync(max, timeout.Token);
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    private async Task ReplyAsync(string reply, CancellationToken ct)
    {
        _transcript.Server(reply);
        await _connection.WriteLineAsync(reply, ct);
    }

    private async Task<bool> ErrorAsync(string reply, CancellationToken ct)
    {
        _errors++;
        await ReplyAsync(reply, ct);
        return true;
    }

    private async Task TryReplyAsync(string reply)
    {
        try
        {
            await ReplyAsync(reply, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Connection already gone.
        }
    }

    internal static (string Verb, string Argument) Split(string line)
    {
        var trimmed = line.TrimEnd();
        var space = trimmed.IndexOf(' ');
        return space < 0
            ? (trimmed.ToUpperInvariant(), "")
            : (trimmed[..space].ToUpperInvariant(), trimmed[(space + 1)..].Trim());
    }

    internal static bool TryParsePath(string argument, string prefix, out string address, out IReadOnlyList<string> parameters)
    {
        address = "";
        parameters = [];
        if (!argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = argument[prefix.Length..].TrimStart();
        string remainder;
        if (rest.StartsWith('<'))
        {
            var close = rest.IndexOf('>');
            if (close < 0)
            {
                return false;
            }

            address = rest[1..close];
            remainder = rest[(close + 1)..];
        }
        else
        {
            var space = rest.IndexOf(' ');
            address = space < 0 ? rest : rest[..space];
            remainder = space < 0 ? "" : rest[space..];
        }

        // Source routes (<@a,@b:user@host>) are obsolete; keep only the mailbox.
        var colon = address.IndexOf(':');
        if (address.StartsWith('@') && colon > 0)
        {
            address = address[(colon + 1)..];
        }

        parameters = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return true;
    }

    private static string RedactAuth(string line)
    {
        var parts = line.Split(' ', 3);
        return parts.Length >= 3 ? $"{parts[0]} {parts[1]} ****" : line;
    }

    private static bool TryDecodeBase64(string value, out string decoded)
    {
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            return true;
        }
        catch (FormatException)
        {
            decoded = "";
            return false;
        }
    }

    internal static bool TryParseXOAuth2(string decoded, out string username, out string token)
    {
        username = "";
        token = "";
        foreach (var part in decoded.Split('\u0001', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("user=", StringComparison.OrdinalIgnoreCase))
            {
                username = part[5..];
            }
            else if (part.StartsWith("auth=", StringComparison.OrdinalIgnoreCase))
            {
                var value = part[5..];
                token = value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;
            }
        }

        return username.Length > 0;
    }
}
