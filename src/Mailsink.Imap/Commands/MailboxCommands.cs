using System.Text.RegularExpressions;
using Mailsink.Core.Delivery;
using Mailsink.Core.Domain;
using Mailsink.Core.Storage;
using Mailsink.Imap.Protocol;
using Mailsink.Imap.Session;

namespace Mailsink.Imap.Commands;

internal sealed class SelectCommand(bool readOnly) : IImapCommandHandler
{
    public string Name => readOnly ? "EXAMINE" : "SELECT";

    public ImapState AllowedStates => ImapState.AuthenticatedOrSelected;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        var name = CommandHelpers.MailboxName(command, 0);
        var mailbox = await session.Services.Store.GetMailboxAsync(session.User!, name, ct);
        if (mailbox is null)
        {
            session.Unselect();
            return ImapResult.No("Mailbox does not exist", "NONEXISTENT");
        }

        await session.SelectAsync(mailbox, readOnly, ct);
        var view = session.Selected!;
        var w = new ImapWriter();
        w.Untagged().Raw("FLAGS ").Raw(FlagFormatting.Flags(view.Keywords)).Line();
        w.Untagged().Raw($"OK [{FlagFormatting.PermanentFlags(readOnly)}] Flags permitted").Line();
        w.Untagged().Number(view.Count).Raw(" EXISTS").Line();
        w.Untagged().Number(view.RecentCount).Raw(" RECENT").Line();
        var firstUnseen = view.Uids.FirstOrDefault(u => (view.Entry(u).Flags & SystemFlags.Seen) == 0);
        if (firstUnseen != 0)
        {
            w.Untagged().Raw("OK [UNSEEN ").Number(view.MsnOf(firstUnseen)).Raw("] First unseen").Line();
        }

        w.Untagged().Raw("OK [UIDVALIDITY ").Number(view.Mailbox.UidValidity).Raw("] UIDs valid").Line();
        w.Untagged().Raw("OK [UIDNEXT ").Number(view.Mailbox.UidNext).Raw("] Predicted next UID").Line();
        w.Untagged().Raw("OK [HIGHESTMODSEQ ").Number(view.Mailbox.HighestModSeq).Raw("] Highest").Line();
        await session.SendAsync(w, ct);
        return ImapResult.Ok($"{Name} completed", readOnly ? "READ-ONLY" : "READ-WRITE");
    }
}

internal sealed class CreateCommand : IImapCommandHandler
{
    public string Name => "CREATE";

    public ImapState AllowedStates => ImapState.AuthenticatedOrSelected;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        var name = StoreHelpers.NormalizeMailboxName(CommandHelpers.MailboxName(command, 0));
        var store = session.Services.Store;
        if (await store.GetMailboxAsync(session.User!, name, ct) is not null)
        {
            return ImapResult.No("Mailbox already exists", "ALREADYEXISTS");
        }

        // Create missing superior mailboxes (RFC 3501 §6.3.3).
        var parts = name.Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            var parent = string.Join('/', parts[..i]);
            if (await store.GetMailboxAsync(session.User!, parent, ct) is null)
            {
                await store.CreateMailboxAsync(session.User!, parent, SpecialUse.None, ct);
            }
        }

        var use = SpecialUse.None;
        if (command.ArgAt(1) is ImapList { Items: [var useAtom, ImapList uses] } && useAtom.IsAtom("USE"))
        {
            use = uses.Items.Select(u => u.AsString()).FirstOrDefault() switch
            {
                "\\Sent" => SpecialUse.Sent,
                "\\Drafts" => SpecialUse.Drafts,
                "\\Trash" => SpecialUse.Trash,
                "\\Junk" => SpecialUse.Junk,
                "\\Archive" => SpecialUse.Archive,
                "\\All" => SpecialUse.All,
                "\\Flagged" => SpecialUse.Flagged,
                _ => SpecialUse.None,
            };
        }

        await store.CreateMailboxAsync(session.User!, name, use, ct);
        return ImapResult.Ok("CREATE completed");
    }
}

internal sealed class DeleteCommand : IImapCommandHandler
{
    public string Name => "DELETE";

    public ImapState AllowedStates => ImapState.AuthenticatedOrSelected;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        var mailbox = await session.Services.Store.GetMailboxAsync(session.User!, CommandHelpers.MailboxName(command, 0), ct);
        if (mailbox is null)
        {
            return ImapResult.No("Mailbox does not exist", "NONEXISTENT");
        }

        if (session.Selected?.Id == mailbox.Id)
        {
            session.Unselect();
        }

        await session.Services.Store.DeleteMailboxAsync(mailbox.Id, ct);
        return ImapResult.Ok("DELETE completed");
    }
}

internal sealed class RenameCommand : IImapCommandHandler
{
    public string Name => "RENAME";

    public ImapState AllowedStates => ImapState.AuthenticatedOrSelected;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        var mailbox = await session.Services.Store.GetMailboxAsync(session.User!, CommandHelpers.MailboxName(command, 0), ct);
        if (mailbox is null)
        {
            return ImapResult.No("Mailbox does not exist", "NONEXISTENT");
        }

        await session.Services.Store.RenameMailboxAsync(mailbox.Id, CommandHelpers.MailboxName(command, 1), ct);
        return ImapResult.Ok("RENAME completed");
    }
}

internal sealed class SubscribeCommand(bool subscribe) : IImapCommandHandler
{
    public string Name => subscribe ? "SUBSCRIBE" : "UNSUBSCRIBE";

    public ImapState AllowedStates => ImapState.AuthenticatedOrSelected;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        var mailbox = await session.Services.Store.GetMailboxAsync(session.User!, CommandHelpers.MailboxName(command, 0), ct);
        if (mailbox is null)
        {
            return subscribe ? ImapResult.No("Mailbox does not exist", "NONEXISTENT") : ImapResult.Ok("UNSUBSCRIBE completed");
        }

        await session.Services.Store.SetSubscribedAsync(mailbox.Id, subscribe, ct);
        return ImapResult.Ok($"{Name} completed");
    }
}

/// <summary>LIST (with LIST-EXTENDED selection/return options and LIST-STATUS) and LSUB.</summary>
internal sealed class ListCommand(bool lsub) : IImapCommandHandler
{
    public string Name => lsub ? "LSUB" : "LIST";

    public ImapState AllowedStates => ImapState.AuthenticatedOrSelected;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        var index = 0;
        var selectSubscribed = lsub;
        var selectSpecialUse = false;
        if (!lsub && command.ArgAt(0) is ImapList selection)
        {
            index = 1;
            foreach (var option in selection.Items.Select(i => i.AsString()?.ToUpperInvariant()))
            {
                selectSubscribed |= option == "SUBSCRIBED";
                selectSpecialUse |= option == "SPECIAL-USE";
            }
        }

        var reference = ModifiedUtf7.Decode(command.Arg(index));
        var patternValue = command.ArgAt(index + 1) ?? throw new ImapParseException("Missing mailbox pattern");
        var patterns = patternValue is ImapList list
            ? [.. list.Items.Select(i => ModifiedUtf7.Decode(i.AsString() ?? ""))]
            : new List<string> { ModifiedUtf7.Decode(patternValue.AsString() ?? "") };

        var returnSubscribed = false;
        List<string>? statusItems = null;
        if (command.ArgAt(index + 2) is ImapAtom { Value: var ret } && ret.Equals("RETURN", StringComparison.OrdinalIgnoreCase)
            && command.ArgAt(index + 3) is ImapList returnOptions)
        {
            for (var i = 0; i < returnOptions.Items.Count; i++)
            {
                var option = returnOptions.Items[i].AsString()?.ToUpperInvariant();
                returnSubscribed |= option == "SUBSCRIBED";
                if (option == "STATUS" && i + 1 < returnOptions.Items.Count && returnOptions.Items[i + 1] is ImapList items)
                {
                    statusItems = [.. items.Items.Select(x => x.AsString()?.ToUpperInvariant() ?? "")];
                    i++;
                }
            }
        }

        if (!lsub && patterns is [""])
        {
            await session.WriteLineAsync("* LIST (\\Noselect) \"/\" \"\"", ct);
            return ImapResult.Ok("LIST completed");
        }

        var mailboxes = await session.Services.Store.ListMailboxesAsync(session.User!, ct);
        var names = mailboxes.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);

        // Superiors that exist only implicitly (a/b exists, a does not) are listed as \Noselect.
        var implied = mailboxes.SelectMany(m => Ancestors(m.Name)).Where(a => !names.ContainsKey(a)).Distinct(StringComparer.OrdinalIgnoreCase);
        var candidates = mailboxes.Select(m => (m.Name, Mailbox: (Mailbox?)m)).Concat(implied.Select(a => (Name: a, Mailbox: (Mailbox?)null))).ToList();

        var regexes = patterns.Select(p => ToRegex(Combine(reference, p))).ToList();
        var w = new ImapWriter();
        foreach (var (name, mailbox) in candidates.OrderBy(c => StoreHelpers.IsInbox(c.Name) ? 0 : 1).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!regexes.Any(r => r.IsMatch(name)))
            {
                continue;
            }

            if (selectSubscribed && mailbox is not { Subscribed: true })
            {
                continue;
            }

            if (selectSpecialUse && mailbox is not { SpecialUse: not SpecialUse.None })
            {
                continue;
            }

            var attributes = new List<string>();
            if (mailbox is null)
            {
                attributes.Add("\\Noselect");
            }

            if (!lsub)
            {
                attributes.Add(candidates.Any(c => StoreHelpers.IsChildOf(c.Name, name)) ? "\\HasChildren" : "\\HasNoChildren");
                if (mailbox is not null && FlagNames.SpecialUseAttribute(mailbox.SpecialUse) is { } special)
                {
                    attributes.Add(special);
                }

                if ((returnSubscribed || selectSubscribed) && mailbox is { Subscribed: true })
                {
                    attributes.Add("\\Subscribed");
                }
            }

            w.Untagged().Raw(Name).Raw(" (").Raw(string.Join(' ', attributes)).Raw(") \"/\" ").Mailbox(name).Line();

            if (statusItems is not null && mailbox is not null)
            {
                await StatusCommand.WriteStatusAsync(session, w, mailbox, statusItems, ct);
            }
        }

        await session.SendAsync(w, ct);
        return ImapResult.Ok($"{Name} completed");
    }

    private static IEnumerable<string> Ancestors(string name)
    {
        var parts = name.Split('/');
        for (var i = 1; i < parts.Length; i++)
        {
            yield return string.Join('/', parts[..i]);
        }
    }

    private static string Combine(string reference, string pattern)
    {
        if (reference.Length == 0 || pattern.StartsWith('/'))
        {
            return pattern.TrimStart('/');
        }

        return reference.EndsWith('/') ? reference + pattern : reference + "/" + pattern;
    }

    private static Regex ToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("%", "[^/]*", StringComparison.Ordinal);

        // INBOX is case-insensitive; everything else is matched case-insensitively too for client friendliness.
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}

internal sealed class StatusCommand : IImapCommandHandler
{
    public string Name => "STATUS";

    public ImapState AllowedStates => ImapState.AuthenticatedOrSelected;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        var mailbox = await session.Services.Store.GetMailboxAsync(session.User!, CommandHelpers.MailboxName(command, 0), ct);
        if (mailbox is null)
        {
            return ImapResult.No("Mailbox does not exist", "NONEXISTENT");
        }

        if (command.ArgAt(1) is not ImapList items)
        {
            return ImapResult.Bad("Missing status items");
        }

        var w = new ImapWriter();
        await WriteStatusAsync(session, w, mailbox, [.. items.Items.Select(i => i.AsString()?.ToUpperInvariant() ?? "")], ct);
        await session.SendAsync(w, ct);
        return ImapResult.Ok("STATUS completed");
    }

    public static async Task WriteStatusAsync(ImapSession session, ImapWriter w, Mailbox mailbox, IReadOnlyList<string> items, CancellationToken ct)
    {
        var fresh = await session.Services.Store.GetMailboxAsync(mailbox.Id, ct) ?? mailbox;
        var stats = await session.Services.Store.GetMailboxStatsAsync(mailbox.Id, ct);
        var values = new List<string>();
        foreach (var item in items)
        {
            switch (item)
            {
                case "MESSAGES": values.Add($"MESSAGES {stats.Messages}"); break;
                case "RECENT": values.Add("RECENT 0"); break;
                case "UIDNEXT": values.Add($"UIDNEXT {fresh.UidNext}"); break;
                case "UIDVALIDITY": values.Add($"UIDVALIDITY {fresh.UidValidity}"); break;
                case "UNSEEN": values.Add($"UNSEEN {stats.Unseen}"); break;
                case "SIZE": values.Add($"SIZE {stats.Size}"); break;
                case "HIGHESTMODSEQ": values.Add($"HIGHESTMODSEQ {fresh.HighestModSeq}"); break;
                case "DELETED": break;
                default: throw new ImapParseException($"Unknown STATUS item {item}");
            }
        }

        w.Untagged().Raw("STATUS ").Mailbox(fresh.Name).Raw(" (").Raw(string.Join(' ', values)).Raw(")").Line();
    }
}

internal sealed class AppendCommand : IImapCommandHandler
{
    public string Name => "APPEND";

    public ImapState AllowedStates => ImapState.AuthenticatedOrSelected;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        var store = session.Services.Store;
        var mailbox = await store.GetMailboxAsync(session.User!, CommandHelpers.MailboxName(command, 0), ct);
        if (mailbox is null)
        {
            return ImapResult.No("Mailbox does not exist", "TRYCREATE");
        }

        var flags = SystemFlags.None;
        var keywords = new List<string>();
        var internalDate = DateTimeOffset.UtcNow;
        byte[]? message = null;
        for (var i = 1; i < command.Args.Count; i++)
        {
            switch (command.Args[i])
            {
                case ImapList list:
                    (flags, keywords) = CommandHelpers.ParseFlags(list.Items);
                    break;
                case ImapQuoted quoted when CommandHelpers.TryParseDateTime(quoted.Value, out var date):
                    internalDate = date;
                    break;
                case ImapLiteral literal:
                    message = literal.Data;
                    break;
                default:
                    return ImapResult.Bad("Invalid APPEND arguments");
            }
        }

        if (message is null)
        {
            return ImapResult.Bad("Missing message literal");
        }

        if (session.Options.Imap.OutboxSubmission && (mailbox.SpecialUse == SpecialUse.Outbox || mailbox.Name.Equals("Outbox", StringComparison.OrdinalIgnoreCase)))
        {
            // Non-standard, opt-in: APPEND to Outbox submits the message as if sent over SMTP.
            using var stream = new MemoryStream(message);
            var mime = await MimeKit.MimeMessage.LoadAsync(stream, ct);
            var envelope = SubmissionService.EnvelopeFromHeaders(mime, MessageSource.Submission, session.User);
            var submitted = await session.Services.Submission.SubmitRawAsync(message, envelope with { SessionId = session.Transcript.Id },
                new SubmissionOptions(MessageSource.Submission), ct);
            session.Transcript.LinkMessage(submitted.Message.Id.ToString());
            return ImapResult.Ok("APPEND completed (message submitted)");
        }

        var captured = await store.CaptureAsync(message, new Envelope(null, [], MessageSource.ImapAppend, AuthUser: session.User,
            ClientIp: session.Connection.ClientIp, Secure: session.Connection.IsSecure, SessionId: session.Transcript.Id), inCaptureView: false, ct);
        var entry = await store.AppendAsync(mailbox.Id, captured.Id, flags, keywords, internalDate, ct);
        session.Transcript.LinkMessage(captured.Id.ToString());
        return ImapResult.Ok("APPEND completed", $"APPENDUID {mailbox.UidValidity} {entry.Uid}");
    }
}

internal sealed class IdleCommand : IImapCommandHandler
{
    public string Name => "IDLE";

    public ImapState AllowedStates => ImapState.AuthenticatedOrSelected;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        await session.WriteLineAsync("+ idling", ct);
        await session.FlushUpdatesAsync(allowExpunge: true, ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(session.Options.Imap.IdleTimeout);
        var read = session.ReadLineAsync(timeout.Token).AsTask();
        try
        {
            while (true)
            {
                var wait = session.WaitForEventAsync(timeout.Token).AsTask();
                var completed = await Task.WhenAny(read, wait);
                if (completed == read)
                {
                    var line = await read;
                    if (line is null)
                    {
                        session.Logout();
                        return SkipTagged.Instance;
                    }

                    var text = System.Text.Encoding.ASCII.GetString(line).Trim();
                    session.Transcript.Client(text);
                    return text.Equals("DONE", StringComparison.OrdinalIgnoreCase)
                        ? ImapResult.Ok("IDLE terminated")
                        : ImapResult.Bad("Expected DONE");
                }

                if (!await wait)
                {
                    await read;
                    return ImapResult.Ok("IDLE terminated");
                }

                await session.FlushUpdatesAsync(allowExpunge: true, ct);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }
}
