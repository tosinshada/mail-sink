using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mailsink.Core.Accounts;
using Mailsink.Core.Diagnostics;
using Mailsink.Core.Domain;
using Mailsink.Core.Networking;
using Mailsink.Core.Options;
using Mailsink.Core.Storage;

namespace Mailsink.Pop3;

public sealed record Pop3Services(IMailStore Store, IAccountService Accounts, TranscriptStore Transcripts, MetricsCollector Metrics);

/// <summary>RFC 1939 POP3 over the user's INBOX, with CAPA, UIDL, TOP, STLS (RFC 2595) and AUTH PLAIN/LOGIN (RFC 5034).</summary>
internal sealed class Pop3Session(ProtocolConnection connection, Pop3Services services, MailsinkOptions options)
{
    private const int MaxLine = 8 * 1024;

    private readonly SessionTranscript _transcript = services.Transcripts.Start("pop3", connection.ClientIp);
    private string? _pendingUser;
    private string? _user;
    private Mailbox? _inbox;
    private List<MailboxEntry> _maildrop = [];
    private readonly HashSet<int> _deleted = [];

    private bool LoginDisabled => options.Auth.RequireTls && !connection.IsSecure;

    public async Task RunAsync(CancellationToken stopping)
    {
        using var _ = services.Metrics.TrackSession("pop3");
        _transcript.Secure = connection.IsSecure;
        try
        {
            await ReplyAsync($"+OK Mailsink POP3 ready <{Guid.NewGuid():N}@{options.Hostname}>", stopping);
            while (!stopping.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                timeout.CancelAfter(TimeSpan.FromMinutes(10));
                var line = await connection.ReadLineAsync(MaxLine, timeout.Token);
                if (line is null)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(line);
                var space = text.IndexOf(' ');
                var verb = (space < 0 ? text : text[..space]).ToUpperInvariant();
                var argument = space < 0 ? "" : text[(space + 1)..];
                _transcript.Client(verb is "PASS" or "AUTH" ? $"{verb} ****" : text);

                if (!await DispatchAsync(verb, argument, stopping))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            // Inactivity timeout: no UPDATE state, deletions are discarded.
        }
        finally
        {
            _transcript.End();
        }
    }

    private async Task<bool> DispatchAsync(string verb, string argument, CancellationToken ct)
    {
        var authenticated = _user is not null;
        switch (verb)
        {
            case "CAPA":
                await CapaAsync(ct);
                return true;
            case "QUIT":
                if (authenticated)
                {
                    await CommitDeletesAsync(ct);
                }

                await ReplyAsync("+OK Mailsink POP3 server signing off", ct);
                return false;
            case "NOOP" when authenticated:
                await ReplyAsync("+OK", ct);
                return true;
            case "STLS" when !authenticated:
                if (connection.IsSecure || !connection.CanStartTls)
                {
                    await ReplyAsync("-ERR TLS not available", ct);
                    return true;
                }

                await ReplyAsync("+OK Begin TLS negotiation", ct);
                await connection.UpgradeToTlsAsync(ct);
                _transcript.Secure = true;
                _pendingUser = null;
                return true;
            case "USER" when !authenticated:
                if (LoginDisabled)
                {
                    await ReplyAsync("-ERR [AUTH] Use STLS first", ct);
                    return true;
                }

                _pendingUser = argument.Trim();
                await ReplyAsync("+OK", ct);
                return true;
            case "PASS" when !authenticated:
                if (_pendingUser is null)
                {
                    await ReplyAsync("-ERR USER first", ct);
                    return true;
                }

                await LoginAsync(_pendingUser, argument, ct);
                return true;
            case "AUTH" when !authenticated:
                await AuthAsync(argument, ct);
                return true;
            case "STAT" when authenticated:
                var live = Live().ToList();
                await ReplyAsync($"+OK {live.Count} {live.Sum(e => e.Entry.Size)}", ct);
                return true;
            case "LIST" when authenticated:
                await ListAsync(argument, uidl: false, ct);
                return true;
            case "UIDL" when authenticated:
                await ListAsync(argument, uidl: true, ct);
                return true;
            case "RETR" when authenticated:
                await RetrieveAsync(argument, null, ct);
                return true;
            case "TOP" when authenticated:
            {
                var parts = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var lines))
                {
                    await ReplyAsync("-ERR Syntax: TOP msg n", ct);
                    return true;
                }

                await RetrieveAsync(parts[0], lines, ct);
                return true;
            }

            case "DELE" when authenticated:
                if (TryGet(argument, out var index, out _))
                {
                    _deleted.Add(index);
                    await ReplyAsync($"+OK message {index + 1} deleted", ct);
                }
                else
                {
                    await ReplyAsync("-ERR no such message", ct);
                }

                return true;
            case "RSET" when authenticated:
                _deleted.Clear();
                await ReplyAsync($"+OK maildrop has {_maildrop.Count} messages", ct);
                return true;
            default:
                await ReplyAsync("-ERR Unknown or out-of-state command", ct);
                return true;
        }
    }

    private async Task CapaAsync(CancellationToken ct)
    {
        var lines = new List<string> { "+OK Capability list follows", "TOP", "UIDL", "RESP-CODES", "AUTH-RESP-CODE", "PIPELINING" };
        if (!LoginDisabled)
        {
            lines.Add("USER");
            lines.Add("SASL PLAIN LOGIN");
        }

        if (!connection.IsSecure && connection.CanStartTls)
        {
            lines.Add("STLS");
        }

        lines.Add("IMPLEMENTATION Mailsink");
        lines.Add(".");
        await ReplyAsync(string.Join("\r\n", lines), ct);
    }

    private async Task AuthAsync(string argument, CancellationToken ct)
    {
        if (LoginDisabled)
        {
            await ReplyAsync("-ERR [AUTH] Use STLS first", ct);
            return;
        }

        var parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            await ReplyAsync("+OK\r\nPLAIN\r\nLOGIN\r\n.", ct);
            return;
        }

        var mechanism = parts[0].ToUpperInvariant();
        var initial = parts.Length > 1 ? parts[1] : null;
        if (mechanism == "PLAIN")
        {
            var response = initial ?? await ChallengeAsync("", ct);
            var fields = Decode(response)?.Split('\0');
            if (fields is not { Length: 3 })
            {
                await ReplyAsync("-ERR Invalid PLAIN response", ct);
                return;
            }

            await LoginAsync(fields[1], fields[2], ct);
        }
        else if (mechanism == "LOGIN")
        {
            var user = Decode(initial ?? await ChallengeAsync("VXNlcm5hbWU6", ct));
            var pass = Decode(await ChallengeAsync("UGFzc3dvcmQ6", ct));
            if (user is null || pass is null)
            {
                await ReplyAsync("-ERR Invalid LOGIN response", ct);
                return;
            }

            await LoginAsync(user, pass, ct);
        }
        else
        {
            await ReplyAsync("-ERR Unsupported mechanism", ct);
        }
    }

    private async Task LoginAsync(string username, string password, CancellationToken ct)
    {
        var result = await services.Accounts.AuthenticateAsync(username, password, "pop3", ct);
        if (!result.Success)
        {
            _pendingUser = null;
            await ReplyAsync("-ERR [AUTH] Authentication failed", ct);
            return;
        }

        _user = result.Address!;
        _transcript.User = _user;
        _inbox = await services.Store.GetMailboxAsync(_user, "INBOX", ct);
        _maildrop = _inbox is null ? [] : [.. await services.Store.GetEntriesAsync(_inbox.Id, ct)];
        await ReplyAsync($"+OK {_user} has {_maildrop.Count} messages", ct);
    }

    private async Task ListAsync(string argument, bool uidl, CancellationToken ct)
    {
        string Format(int index, MailboxEntry entry) =>
            $"{index + 1} {(uidl ? entry.Message.Value.ToString("N") : entry.Size.ToString(CultureInfo.InvariantCulture))}";

        if (!string.IsNullOrWhiteSpace(argument))
        {
            if (TryGet(argument, out var index, out var entry))
            {
                await ReplyAsync("+OK " + Format(index, entry), ct);
            }
            else
            {
                await ReplyAsync("-ERR no such message", ct);
            }

            return;
        }

        var lines = new List<string> { "+OK" };
        lines.AddRange(Live().Select(x => Format(x.Index, x.Entry)));
        lines.Add(".");
        await ReplyAsync(string.Join("\r\n", lines), ct);
    }

    private async Task RetrieveAsync(string argument, int? topLines, CancellationToken ct)
    {
        if (!TryGet(argument, out _, out var entry))
        {
            await ReplyAsync("-ERR no such message", ct);
            return;
        }

        byte[] raw;
        try
        {
            raw = await services.Store.ReadRawAsync(entry.Message, ct);
        }
        catch (MailStoreException)
        {
            await ReplyAsync("-ERR message no longer available", ct);
            return;
        }

        if (topLines is { } n)
        {
            raw = Top(raw, n);
        }
        else if (_inbox is not null && (entry.Flags & SystemFlags.Seen) == 0)
        {
            await services.Store.StoreFlagsAsync(_inbox.Id, [entry.Uid], FlagOperation.Add, SystemFlags.Seen, [], ct);
        }

        _transcript.Server($"+OK ({raw.Length} octets)");
        connection.Write(Encoding.ASCII.GetBytes($"+OK {raw.Length} octets\r\n"));
        WriteDotStuffed(raw);
        connection.Write(".\r\n"u8);
        await connection.FlushAsync(ct);
    }

    private async Task CommitDeletesAsync(CancellationToken ct)
    {
        if (_inbox is null || _deleted.Count == 0)
        {
            return;
        }

        var uids = _deleted.Select(i => _maildrop[i].Uid).ToList();
        await services.Store.StoreFlagsAsync(_inbox.Id, uids, FlagOperation.Add, SystemFlags.Deleted, [], ct);
        await services.Store.ExpungeAsync(_inbox.Id, uids, ct);
    }

    private IEnumerable<(int Index, MailboxEntry Entry)> Live() =>
        _maildrop.Select((e, i) => (i, e)).Where(x => !_deleted.Contains(x.Item1));

    private bool TryGet(string argument, out int index, out MailboxEntry entry)
    {
        entry = null!;
        index = -1;
        if (!int.TryParse(argument.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > _maildrop.Count || _deleted.Contains(n - 1))
        {
            return false;
        }

        index = n - 1;
        entry = _maildrop[index];
        return true;
    }

    private void WriteDotStuffed(byte[] raw)
    {
        var start = 0;
        while (start < raw.Length)
        {
            var lf = Array.IndexOf(raw, (byte)'\n', start);
            var end = lf < 0 ? raw.Length : lf + 1;
            if (raw[start] == (byte)'.')
            {
                connection.Write("."u8);
            }

            connection.Write(raw.AsSpan(start, end - start));
            start = end;
        }

        if (raw.Length > 0 && raw[^1] != (byte)'\n')
        {
            connection.Write("\r\n"u8);
        }
    }

    private static byte[] Top(byte[] raw, int lines)
    {
        var headerEnd = raw.AsSpan().IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0)
        {
            return raw;
        }

        var position = headerEnd + 4;
        for (var i = 0; i < lines && position < raw.Length; i++)
        {
            var lf = Array.IndexOf(raw, (byte)'\n', position);
            position = lf < 0 ? raw.Length : lf + 1;
        }

        return raw[..position];
    }

    private async Task<string> ChallengeAsync(string challenge, CancellationToken ct)
    {
        await ReplyAsync("+ " + challenge, ct);
        var line = await connection.ReadLineAsync(MaxLine, ct);
        _transcript.Client("****");
        return line is null ? "" : Encoding.ASCII.GetString(line).Trim();
    }

    private static string? Decode(string base64)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task ReplyAsync(string reply, CancellationToken ct)
    {
        _transcript.Server(reply.Split("\r\n")[0]);
        await connection.WriteLineAsync(reply, ct);
    }
}

public sealed class Pop3ListenerService(
    IOptions<MailsinkOptions> options,
    CertificateProvider certificates,
    EndpointRegistry registry,
    Pop3Services services,
    ILoggerFactory loggerFactory) : IHostedService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("Mailsink.Pop3");
    private TcpServer? _server;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (!o.Pop3.Enabled)
        {
            return Task.CompletedTask;
        }

        var address = ListenerAddress.Parse(o.BindAddress);
        var definition = new ListenerDefinition("pop3", "pop3", address, o.Pop3.Port, false, certificates.Certificate);
        _server = new TcpServer(definition, (connection, ct) => new Pop3Session(connection, services, o).RunAsync(ct), _logger);
        _server.Start();
        registry.Register(new EndpointInfo("pop3", "pop3", _server.Port, false, address.ToString()));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null)
        {
            await _server.StopAsync(TimeSpan.FromSeconds(5));
            registry.Remove("pop3");
        }
    }
}

public static class Pop3ServiceCollectionExtensions
{
    public static IServiceCollection AddMailsinkPop3(this IServiceCollection services)
    {
        services.AddSingleton<Pop3Services>();
        services.AddHostedService<Pop3ListenerService>();
        return services;
    }
}
