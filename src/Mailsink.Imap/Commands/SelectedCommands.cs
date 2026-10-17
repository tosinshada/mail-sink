using System.Globalization;
using Mailsink.Core.Domain;
using Mailsink.Imap.Fetch;
using Mailsink.Imap.Protocol;
using Mailsink.Imap.Search;
using Mailsink.Imap.Session;

namespace Mailsink.Imap.Commands;

internal sealed class FetchCommand : IImapCommandHandler
{
    private const int FlushThreshold = 256 * 1024;

    public string Name => "FETCH";

    public ImapState AllowedStates => ImapState.Selected;

    public bool SupportsUidPrefix => true;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        var view = session.Selected!;
        var uids = CommandHelpers.ResolveUids(view, command.Arg(0), command.IsUid);
        var items = FetchItemParser.Parse(command.ArgAt(1) ?? throw new ImapParseException("Missing FETCH items")).ToList();
        if (command.IsUid && items.All(i => i.Kind != FetchKind.Uid))
        {
            items.Insert(0, new FetchItem(FetchKind.Uid));
        }

        var store = session.Services.Store;
        var setsSeen = !view.ReadOnly && items.Any(i => i.SetsSeen);
        var wantsFlags = items.Any(i => i.Kind == FetchKind.Flags);
        IReadOnlyDictionary<MessageId, CapturedMessage> messages = new Dictionary<MessageId, CapturedMessage>();
        if (items.Any(i => i.NeedsMessage))
        {
            messages = await store.GetMessagesAsync([.. uids.Select(u => view.Entry(u).Message).Distinct()], ct);
        }

        var w = new ImapWriter();
        foreach (var uid in uids)
        {
            if (!view.TryGetEntry(uid, out var entry))
            {
                continue;
            }

            ParsedRaw? content = null;
            if (items.Any(i => i.NeedsContent))
            {
                try
                {
                    content = await session.Services.MessageCache.GetAsync(entry.Message, ct);
                }
                catch (Core.Domain.MailStoreException)
                {
                    continue; // expunged elsewhere; the client will learn at the next safe point
                }
            }

            var flagsChanged = false;
            if (setsSeen && (entry.Flags & SystemFlags.Seen) == 0)
            {
                var updated = await store.StoreFlagsAsync(view.Id, [uid], FlagOperation.Add, SystemFlags.Seen, [], ct);
                if (updated.Count > 0)
                {
                    entry = updated[0];
                    view.Update(entry);
                    flagsChanged = true;
                }
            }

            messages.TryGetValue(entry.Message, out var message);
            w.Untagged().Number(view.MsnOf(uid)).Raw(" FETCH (");
            var first = true;
            foreach (var item in items)
            {
                if (!first)
                {
                    w.Space();
                }

                first = false;
                WriteItem(w, item, view, entry, message, content);
            }

            if (flagsChanged && !wantsFlags)
            {
                w.Raw(" FLAGS ").Flags(entry.Flags, entry.Keywords, view.IsRecent(uid));
            }

            w.Raw(")").Line();
            if (w.Length > FlushThreshold)
            {
                await session.SendAsync(w, ct);
            }
        }

        if (w.Length > 0)
        {
            await session.SendAsync(w, ct);
        }

        return ImapResult.Ok("FETCH completed");
    }

    private static void WriteItem(ImapWriter w, FetchItem item, SelectedMailboxView view, MailboxEntry entry, CapturedMessage? message, ParsedRaw? content)
    {
        switch (item.Kind)
        {
            case FetchKind.Flags:
                w.Raw("FLAGS ").Flags(entry.Flags, entry.Keywords, view.IsRecent(entry.Uid));
                break;
            case FetchKind.InternalDate:
                w.Raw("INTERNALDATE \"").Raw(ImapWriter.FormatInternalDate(entry.InternalDate)).Raw("\"");
                break;
            case FetchKind.Rfc822Size:
                w.Raw("RFC822.SIZE ").Number(entry.Size);
                break;
            case FetchKind.Uid:
                w.Raw("UID ").Number(entry.Uid);
                break;
            case FetchKind.ModSeq:
                w.Raw("MODSEQ (").Number(entry.ModSeq).Raw(")");
                break;
            case FetchKind.Envelope:
                w.Raw("ENVELOPE ").Raw(message?.Imap.Envelope ?? "(NIL NIL NIL NIL NIL NIL NIL NIL NIL NIL)");
                break;
            case FetchKind.BodyStructure:
                w.Raw("BODYSTRUCTURE ").Raw(message?.Imap.BodyStructure ?? "(\"TEXT\" \"PLAIN\" NIL NIL NIL \"7BIT\" 0 0)");
                break;
            case FetchKind.Body:
                w.Raw("BODY ").Raw(message?.Imap.Body ?? "(\"TEXT\" \"PLAIN\" NIL NIL NIL \"7BIT\" 0 0)");
                break;
            case FetchKind.Rfc822:
                w.Raw("RFC822 ").Literal(content!.Raw);
                break;
            case FetchKind.Rfc822Header:
                w.Raw("RFC822.HEADER ").Literal(SectionExtractor.HeaderBlock(content!.Raw, content.Root));
                break;
            case FetchKind.Rfc822Text:
                w.Raw("RFC822.TEXT ").Literal(SectionExtractor.Extract(content!, new SectionSpec([], SectionText.Text, [])));
                break;
            case FetchKind.Section:
            {
                var data = SectionExtractor.Extract(content!, item.Section!);
                w.Raw("BODY[").Raw(item.Section!.Format()).Raw("]");
                if (item.PartialOffset is { } offset)
                {
                    w.Raw("<").Number(offset).Raw(">");
                    var start = (int)Math.Min(offset, data.Length);
                    var length = (int)Math.Min(item.PartialLength ?? long.MaxValue, data.Length - start);
                    data = data.AsSpan(start, length).ToArray();
                }

                w.Space().Literal(data);
                break;
            }
        }
    }
}

internal sealed class StoreCommand : IImapCommandHandler
{
    public string Name => "STORE";

    public ImapState AllowedStates => ImapState.Selected;

    public bool SupportsUidPrefix => true;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        var view = session.Selected!;
        if (view.ReadOnly)
        {
            return ImapResult.No("Mailbox is read-only", "READ-ONLY");
        }

        var index = 1;
        if (command.ArgAt(index) is ImapList)
        {
            index++; // (UNCHANGEDSINCE n) modifier: accepted, not enforced
        }

        var item = command.Arg(index).ToUpperInvariant();
        var silent = item.EndsWith(".SILENT", StringComparison.Ordinal);
        var op = item switch
        {
            _ when item.StartsWith("+FLAGS", StringComparison.Ordinal) => FlagOperation.Add,
            _ when item.StartsWith("-FLAGS", StringComparison.Ordinal) => FlagOperation.Remove,
            _ when item.StartsWith("FLAGS", StringComparison.Ordinal) => FlagOperation.Replace,
            _ => throw new ImapParseException($"Invalid STORE item {item}"),
        };

        var (flags, keywords) = CommandHelpers.ParseFlags(command.Args.Skip(index + 1));
        var uids = CommandHelpers.ResolveUids(view, command.Arg(0), command.IsUid);
        var updated = await session.Services.Store.StoreFlagsAsync(view.Id, uids, op, flags, keywords, ct);

        var w = new ImapWriter();
        foreach (var entry in updated)
        {
            view.Update(entry);
            if (silent)
            {
                continue;
            }

            w.Untagged().Number(view.MsnOf(entry.Uid)).Raw(" FETCH (FLAGS ").Flags(entry.Flags, entry.Keywords, view.IsRecent(entry.Uid));
            if (command.IsUid)
            {
                w.Raw(" UID ").Number(entry.Uid);
            }

            w.Raw(")").Line();
        }

        if (w.Length > 0)
        {
            await session.SendAsync(w, ct);
        }

        return ImapResult.Ok("STORE completed");
    }
}

internal sealed class SearchCommand : IImapCommandHandler
{
    public string Name => "SEARCH";

    public ImapState AllowedStates => ImapState.Selected;

    public bool SupportsUidPrefix => true;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        var view = session.Selected!;
        SearchRequest request;
        try
        {
            request = SearchParser.Parse(command.Args);
        }
        catch (BadCharsetException)
        {
            return ImapResult.No("Unsupported charset", "BADCHARSET (US-ASCII UTF-8)");
        }

        var uids = await new SearchEngine(session).SearchAsync(view, request.Key, ct);
        var numbers = command.IsUid ? uids : [.. uids.Select(u => (uint)view.MsnOf(u))];

        if (request.ReturnOptions is { } options)
        {
            var all = options.Count == 0 || options.Contains("ALL");
            var parts = new List<string>();
            if (numbers.Count > 0)
            {
                if (options.Contains("MIN"))
                {
                    parts.Add("MIN " + numbers.Min().ToString(CultureInfo.InvariantCulture));
                }

                if (options.Contains("MAX"))
                {
                    parts.Add("MAX " + numbers.Max().ToString(CultureInfo.InvariantCulture));
                }

                if (all)
                {
                    parts.Add("ALL " + SequenceSet.Format(numbers));
                }
            }

            if (options.Contains("COUNT"))
            {
                parts.Add("COUNT " + numbers.Count.ToString(CultureInfo.InvariantCulture));
            }

            var line = $"* ESEARCH (TAG \"{command.Tag}\")" + (command.IsUid ? " UID" : "") + (parts.Count > 0 ? " " + string.Join(' ', parts) : "");
            await session.WriteLineAsync(line, ct);
        }
        else
        {
            await session.WriteLineAsync("* SEARCH" + string.Concat(numbers.Select(n => " " + n.ToString(CultureInfo.InvariantCulture))), ct);
        }

        return ImapResult.Ok("SEARCH completed");
    }
}

internal sealed class CopyCommand(bool move) : IImapCommandHandler
{
    public string Name => move ? "MOVE" : "COPY";

    public ImapState AllowedStates => ImapState.Selected;

    public bool SupportsUidPrefix => true;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        var view = session.Selected!;
        if (move && view.ReadOnly)
        {
            return ImapResult.No("Mailbox is read-only", "READ-ONLY");
        }

        var target = await session.Services.Store.GetMailboxAsync(session.User!, CommandHelpers.MailboxName(command, 1), ct);
        if (target is null)
        {
            return ImapResult.No("Mailbox does not exist", "TRYCREATE");
        }

        var uids = CommandHelpers.ResolveUids(view, command.Arg(0), command.IsUid);
        if (uids.Count == 0)
        {
            return ImapResult.Ok($"{Name} completed (no messages)");
        }

        var result = await session.Services.Store.CopyAsync(view.Id, uids, target.Id, move, ct);
        string? code = result.SourceUids.Count > 0
            ? $"COPYUID {result.UidValidity} {SequenceSet.FormatOrdered(result.SourceUids)} {SequenceSet.FormatOrdered(result.DestinationUids)}"
            : null;

        if (!move)
        {
            return ImapResult.Ok("COPY completed", code);
        }

        if (code is not null)
        {
            await session.WriteLineAsync($"* OK [{code}] Moved", ct);
        }

        await CommandHelpers.WriteExpungesAsync(session, view, result.SourceUids, ct);
        return ImapResult.Ok("MOVE completed");
    }
}

internal sealed class ExpungeCommand : IImapCommandHandler
{
    public string Name => "EXPUNGE";

    public ImapState AllowedStates => ImapState.Selected;

    public bool SupportsUidPrefix => true;

    public async ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct)
    {
        var view = session.Selected!;
        if (view.ReadOnly)
        {
            return ImapResult.No("Mailbox is read-only", "READ-ONLY");
        }

        IReadOnlyCollection<uint>? only = command.IsUid ? CommandHelpers.ResolveUids(view, command.Arg(0), isUid: true) : null;
        var removed = await session.Services.Store.ExpungeAsync(view.Id, only, ct);
        await CommandHelpers.WriteExpungesAsync(session, view, removed, ct);
        return ImapResult.Ok("EXPUNGE completed");
    }
}
