using System.Text;
using Mailsink.Core.Domain;
using Mailsink.Imap.Protocol;
using Mailsink.Imap.Session;

namespace Mailsink.Imap.Commands;

internal sealed class CapabilityCommand : IImapCommandHandler
{
    public string Name => "CAPABILITY";

    public ImapState AllowedStates => ImapState.Any;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        await session.WriteLineAsync("* CAPABILITY " + session.Capabilities, ct);
        return ImapResult.Ok("CAPABILITY completed");
    }
}

internal sealed class NoopCommand(string name) : IImapCommandHandler
{
    public string Name => name;

    public ImapState AllowedStates => name == "CHECK" ? ImapState.Selected : ImapState.Any;

    public ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct) =>
        ValueTask.FromResult(ImapResult.Ok($"{name} completed"));
}

internal sealed class LogoutCommand : IImapCommandHandler
{
    public string Name => "LOGOUT";

    public ImapState AllowedStates => ImapState.Any;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        await session.WriteLineAsync("* BYE Mailsink logging out", ct);
        session.Unselect();
        session.Logout();
        return ImapResult.Ok("LOGOUT completed");
    }
}

internal sealed class IdCommand : IImapCommandHandler
{
    public string Name => "ID";

    public ImapState AllowedStates => ImapState.Any;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        if (command.ArgAt(0) is ImapList list)
        {
            var pairs = new StringBuilder();
            for (var i = 0; i + 1 < list.Items.Count; i += 2)
            {
                pairs.Append(list.Items[i].AsString()).Append('=').Append(list.Items[i + 1].AsString()).Append(' ');
            }

            session.Transcript.Info("client ID: " + pairs.ToString().Trim());
        }

        await session.WriteLineAsync("* ID (\"name\" \"Mailsink\" \"vendor\" \"Mailsink\" \"support-url\" \"https://github.com/mailsink\" \"version\" \"1.0\")", ct);
        return ImapResult.Ok("ID completed");
    }
}

internal sealed class EnableCommand : IImapCommandHandler
{
    // Extensions this server can switch on. None of the ENABLE-able extensions are implemented yet, so
    // ENABLE succeeds and reports nothing enabled, which RFC 5161 permits.
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "ENABLE";

    public ImapState AllowedStates => ImapState.Authenticated;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        var enabled = command.Args.Select(a => a.AsString() ?? "").Where(Supported.Contains).ToList();
        foreach (var e in enabled)
        {
            session.Enabled.Add(e);
        }

        await session.WriteLineAsync("* ENABLED" + (enabled.Count > 0 ? " " + string.Join(' ', enabled) : ""), ct);
        return ImapResult.Ok("ENABLE completed");
    }
}

internal sealed class NamespaceCommand : IImapCommandHandler
{
    public string Name => "NAMESPACE";

    public ImapState AllowedStates => ImapState.AuthenticatedOrSelected;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        await session.WriteLineAsync("* NAMESPACE ((\"\" \"/\")) NIL NIL", ct);
        return ImapResult.Ok("NAMESPACE completed");
    }
}

internal sealed class StartTlsCommand : IImapCommandHandler
{
    public string Name => "STARTTLS";

    public ImapState AllowedStates => ImapState.NotAuthenticated;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        if (!session.StartTlsAvailable)
        {
            return ImapResult.No("TLS not available");
        }

        await session.WriteLineAsync($"{command.Tag} OK Begin TLS negotiation now", ct);
        await session.Connection.UpgradeToTlsAsync(ct);
        session.Transcript.Info("TLS negotiated");
        session.Transcript.Secure = true;
        return SkipTagged.Instance;
    }
}

/// <summary>Marker result: the handler already wrote its tagged response.</summary>
internal static class SkipTagged
{
    public static readonly ImapResult Instance = new(ImapStatus.Ok, "\0skip");
}

internal sealed class LoginCommand : IImapCommandHandler
{
    public string Name => "LOGIN";

    public ImapState AllowedStates => ImapState.NotAuthenticated;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        if (session.LoginDisabled)
        {
            return ImapResult.No("LOGIN disabled until TLS is negotiated", "PRIVACYREQUIRED");
        }

        var user = command.Arg(0);
        var password = command.Arg(1);
        var result = await session.Services.Accounts.AuthenticateAsync(user, password, "imap", ct);
        if (!result.Success)
        {
            return ImapResult.No("Authentication failed", "AUTHENTICATIONFAILED");
        }

        await session.LoginAsync(result.Address!, ct);
        return ImapResult.Ok("Logged in", "CAPABILITY " + session.Capabilities);
    }
}

internal sealed class AuthenticateCommand : IImapCommandHandler
{
    public string Name => "AUTHENTICATE";

    public ImapState AllowedStates => ImapState.NotAuthenticated;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        if (session.LoginDisabled)
        {
            return ImapResult.No("Authentication disabled until TLS is negotiated", "PRIVACYREQUIRED");
        }

        var mechanism = command.Arg(0).ToUpperInvariant();
        var initial = command.ArgAt(1)?.AsString();

        async Task<string?> Challenge(string challenge)
        {
            await session.WriteLineAsync("+ " + challenge, ct);
            var line = await session.ReadContinuationAsync(ct);
            return line is null || line.Trim() == "*" ? null : line.Trim();
        }

        string? username;
        string? secret;
        var token = false;
        switch (mechanism)
        {
            case "PLAIN":
            {
                var response = initial is not null ? (initial == "=" ? "" : initial) : await Challenge("");
                if (response is null)
                {
                    return ImapResult.Bad("Authentication cancelled");
                }

                var fields = Decode(response)?.Split('\0');
                if (fields is not { Length: 3 })
                {
                    return ImapResult.Bad("Invalid PLAIN response");
                }

                username = fields[1];
                secret = fields[2];
                break;
            }

            case "LOGIN":
            {
                var user = initial ?? await Challenge("VXNlcm5hbWU6");
                var pass = user is null ? null : await Challenge("UGFzc3dvcmQ6");
                if (user is null || pass is null)
                {
                    return ImapResult.Bad("Authentication cancelled");
                }

                username = Decode(user);
                secret = Decode(pass);
                break;
            }

            case "XOAUTH2":
            {
                var response = initial ?? await Challenge("");
                var decoded = response is null ? null : Decode(response);
                if (decoded is null || !TryParseXOAuth2(decoded, out username, out secret))
                {
                    return ImapResult.Bad("Invalid XOAUTH2 response");
                }

                token = true;
                break;
            }

            default:
                return ImapResult.No("Unsupported authentication mechanism");
        }

        if (username is null || secret is null)
        {
            return ImapResult.Bad("Invalid response");
        }

        var result = token
            ? await session.Services.Accounts.AuthenticateTokenAsync(username, secret, "imap", ct)
            : await session.Services.Accounts.AuthenticateAsync(username, secret, "imap", ct);
        if (!result.Success)
        {
            return ImapResult.No("Authentication failed", "AUTHENTICATIONFAILED");
        }

        await session.LoginAsync(result.Address!, ct);
        return ImapResult.Ok("Authenticated", "CAPABILITY " + session.Capabilities);
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

    private static bool TryParseXOAuth2(string decoded, out string username, out string token)
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
                token = part[5..].StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? part[12..] : part[5..];
            }
        }

        return username.Length > 0;
    }
}

internal sealed class UnselectCommand(string name) : IImapCommandHandler
{
    public string Name => name;

    public ImapState AllowedStates => ImapState.Selected;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        var view = session.Selected!;
        if (name == "CLOSE" && !view.ReadOnly)
        {
            // CLOSE expunges silently.
            await session.Services.Store.ExpungeAsync(view.Id, null, ct);
        }

        session.Unselect();
        return ImapResult.Ok($"{name} completed");
    }
}

internal static class FlagFormatting
{
    public static string PermanentFlags(bool readOnly) => readOnly ? "PERMANENTFLAGS ()" : "PERMANENTFLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft \\*)";

    public static string Flags(IEnumerable<string> keywords) =>
        "(\\Answered \\Flagged \\Deleted \\Seen \\Draft" + string.Concat(keywords.Select(k => " " + k)) + ")";

    public static SystemFlags Visible(SystemFlags flags) => flags & ~SystemFlags.Recent;
}
