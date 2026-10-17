using System.Globalization;
using MimeKit;
using Mailsink.Core.Domain;
using Mailsink.Imap.Commands;
using Mailsink.Imap.Protocol;
using Mailsink.Imap.Session;

namespace Mailsink.Imap.Search;

internal abstract record SearchKey;

internal sealed record AllKey : SearchKey;

internal sealed record FlagKey(SystemFlags Flag, bool Set) : SearchKey;

internal sealed record KeywordKey(string Keyword, bool Set) : SearchKey;

internal sealed record RecentKey(bool Recent) : SearchKey;

internal sealed record NewKey : SearchKey;

internal sealed record HeaderKey(string Field, string Value) : SearchKey;

internal sealed record TextKey(string Value, bool IncludeHeaders) : SearchKey;

internal enum DateComparison { Before, On, Since }

internal sealed record InternalDateKey(DateComparison Comparison, DateTime Date) : SearchKey;

internal sealed record SentDateKey(DateComparison Comparison, DateTime Date) : SearchKey;

internal sealed record SizeKey(bool Larger, long Size) : SearchKey;

internal sealed record UidSetKey(SequenceSet Set) : SearchKey;

internal sealed record SeqSetKey(SequenceSet Set) : SearchKey;

internal sealed record ModSeqKey(ulong ModSeq) : SearchKey;

internal sealed record NotKey(SearchKey Key) : SearchKey;

internal sealed record OrKey(SearchKey Left, SearchKey Right) : SearchKey;

internal sealed record AndKey(IReadOnlyList<SearchKey> Keys) : SearchKey;

internal sealed record SearchRequest(SearchKey Key, IReadOnlyList<string>? ReturnOptions);

internal static class SearchParser
{
    public static SearchRequest Parse(IReadOnlyList<ImapValue> args)
    {
        var i = 0;
        IReadOnlyList<string>? returnOptions = null;
        if (i < args.Count && args[i].IsAtom("RETURN"))
        {
            i++;
            returnOptions = args.ElementAtOrDefault(i) is ImapList options
                ? [.. options.Items.Select(o => o.AsString()?.ToUpperInvariant() ?? "")]
                : throw new ImapParseException("Invalid RETURN options");
            i++;
        }

        if (i < args.Count && args[i].IsAtom("CHARSET"))
        {
            var charset = args.ElementAtOrDefault(i + 1)?.AsString() ?? "";
            if (!charset.Equals("UTF-8", StringComparison.OrdinalIgnoreCase) && !charset.Equals("US-ASCII", StringComparison.OrdinalIgnoreCase))
            {
                throw new BadCharsetException();
            }

            i += 2;
        }

        var keys = new List<SearchKey>();
        while (i < args.Count)
        {
            keys.Add(ParseKey(args, ref i));
        }

        if (keys.Count == 0)
        {
            throw new ImapParseException("Missing search criteria");
        }

        return new SearchRequest(keys.Count == 1 ? keys[0] : new AndKey(keys), returnOptions);
    }

    private static SearchKey ParseKey(IReadOnlyList<ImapValue> args, ref int i)
    {
        var value = args[i++];
        if (value is ImapList list)
        {
            var inner = 0;
            var keys = new List<SearchKey>();
            while (inner < list.Items.Count)
            {
                keys.Add(ParseKey(list.Items, ref inner));
            }

            return new AndKey(keys);
        }

        var atom = value.AsString() ?? throw new ImapParseException("Invalid search key");
        string Next(ref int index) =>
            index < args.Count ? args[index++].AsString() ?? throw new ImapParseException("Invalid search argument") : throw new ImapParseException($"Missing argument for {atom}");

        DateTime NextDate(ref int index) =>
            CommandHelpers.TryParseDate(Next(ref index), out var d) ? d : throw new ImapParseException("Invalid date");

        switch (atom.ToUpperInvariant())
        {
            case "ALL": return new AllKey();
            case "ANSWERED": return new FlagKey(SystemFlags.Answered, true);
            case "DELETED": return new FlagKey(SystemFlags.Deleted, true);
            case "DRAFT": return new FlagKey(SystemFlags.Draft, true);
            case "FLAGGED": return new FlagKey(SystemFlags.Flagged, true);
            case "SEEN": return new FlagKey(SystemFlags.Seen, true);
            case "UNANSWERED": return new FlagKey(SystemFlags.Answered, false);
            case "UNDELETED": return new FlagKey(SystemFlags.Deleted, false);
            case "UNDRAFT": return new FlagKey(SystemFlags.Draft, false);
            case "UNFLAGGED": return new FlagKey(SystemFlags.Flagged, false);
            case "UNSEEN": return new FlagKey(SystemFlags.Seen, false);
            case "RECENT": return new RecentKey(true);
            case "OLD": return new RecentKey(false);
            case "NEW": return new NewKey();
            case "KEYWORD": return new KeywordKey(Next(ref i), true);
            case "UNKEYWORD": return new KeywordKey(Next(ref i), false);
            case "BCC": return new HeaderKey("Bcc", Next(ref i));
            case "CC": return new HeaderKey("Cc", Next(ref i));
            case "FROM": return new HeaderKey("From", Next(ref i));
            case "TO": return new HeaderKey("To", Next(ref i));
            case "SUBJECT": return new HeaderKey("Subject", Next(ref i));
            case "HEADER":
            {
                var field = Next(ref i);
                return new HeaderKey(field, Next(ref i));
            }

            case "BODY": return new TextKey(Next(ref i), false);
            case "TEXT": return new TextKey(Next(ref i), true);
            case "BEFORE": return new InternalDateKey(DateComparison.Before, NextDate(ref i));
            case "ON": return new InternalDateKey(DateComparison.On, NextDate(ref i));
            case "SINCE": return new InternalDateKey(DateComparison.Since, NextDate(ref i));
            case "SENTBEFORE": return new SentDateKey(DateComparison.Before, NextDate(ref i));
            case "SENTON": return new SentDateKey(DateComparison.On, NextDate(ref i));
            case "SENTSINCE": return new SentDateKey(DateComparison.Since, NextDate(ref i));
            case "LARGER": return new SizeKey(true, long.Parse(Next(ref i), CultureInfo.InvariantCulture));
            case "SMALLER": return new SizeKey(false, long.Parse(Next(ref i), CultureInfo.InvariantCulture));
            case "UID": return new UidSetKey(SequenceSet.Parse(Next(ref i)));
            case "NOT": return new NotKey(ParseKey(args, ref i));
            case "OR":
            {
                var left = ParseKey(args, ref i);
                return new OrKey(left, ParseKey(args, ref i));
            }

            case "MODSEQ":
            {
                // MODSEQ [entry-name entry-type] mod-sequence
                var text = Next(ref i);
                if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var modseq))
                {
                    Next(ref i);
                    modseq = ulong.Parse(Next(ref i), CultureInfo.InvariantCulture);
                }

                return new ModSeqKey(modseq);
            }

            default:
                return SequenceSet.TryParse(atom, out var set) ? new SeqSetKey(set) : throw new ImapParseException($"Unknown search key {atom}");
        }
    }
}

internal sealed class BadCharsetException() : Exception("Unsupported charset");

/// <summary>Evaluates a search key over the selected mailbox. Header fields use parsed summaries; HEADER/BODY/TEXT load the message.</summary>
internal sealed class SearchEngine(ImapSession session)
{
    private readonly Dictionary<MessageId, CapturedMessage> _messages = [];
    private readonly Dictionary<MessageId, MimeMessage?> _parsed = [];

    public async Task<IReadOnlyList<uint>> SearchAsync(SelectedMailboxView view, SearchKey key, CancellationToken ct)
    {
        var uids = view.Uids.ToList();
        if (NeedsMetadata(key))
        {
            var found = await session.Services.Store.GetMessagesAsync([.. uids.Select(u => view.Entry(u).Message).Distinct()], ct);
            foreach (var (id, message) in found)
            {
                _messages[id] = message;
            }
        }

        var allMsns = Enumerable.Range(1, view.Count).Select(i => (uint)i).ToList();
        var result = new List<uint>();
        foreach (var uid in uids)
        {
            if (await MatchesAsync(view, key, uid, allMsns, ct))
            {
                result.Add(uid);
            }
        }

        return result;
    }

    private static bool NeedsMetadata(SearchKey key) => key switch
    {
        HeaderKey or SentDateKey => true,
        NotKey n => NeedsMetadata(n.Key),
        OrKey o => NeedsMetadata(o.Left) || NeedsMetadata(o.Right),
        AndKey a => a.Keys.Any(NeedsMetadata),
        _ => false,
    };

    private async ValueTask<bool> MatchesAsync(SelectedMailboxView view, SearchKey key, uint uid, List<uint> allMsns, CancellationToken ct)
    {
        var entry = view.Entry(uid);
        switch (key)
        {
            case AllKey:
                return true;
            case FlagKey f:
                return ((entry.Flags & f.Flag) != 0) == f.Set;
            case KeywordKey k:
                return entry.Keywords.Contains(k.Keyword, StringComparer.OrdinalIgnoreCase) == k.Set;
            case RecentKey r:
                return view.IsRecent(uid) == r.Recent;
            case NewKey:
                return view.IsRecent(uid) && (entry.Flags & SystemFlags.Seen) == 0;
            case SizeKey s:
                return s.Larger ? entry.Size > s.Size : entry.Size < s.Size;
            case InternalDateKey d:
                return Compare(entry.InternalDate.UtcDateTime.Date, d.Comparison, d.Date);
            case SentDateKey d:
                return _messages.TryGetValue(entry.Message, out var sm) && sm.Summary.Date is { } sent && Compare(sent.Date, d.Comparison, d.Date);
            case UidSetKey u:
                return u.Set.Resolve(view.Uids).Contains(uid);
            case SeqSetKey s:
                return s.Set.Resolve(allMsns).Contains((uint)view.MsnOf(uid));
            case ModSeqKey m:
                return entry.ModSeq >= m.ModSeq;
            case NotKey n:
                return !await MatchesAsync(view, n.Key, uid, allMsns, ct);
            case OrKey o:
                return await MatchesAsync(view, o.Left, uid, allMsns, ct) || await MatchesAsync(view, o.Right, uid, allMsns, ct);
            case AndKey a:
                foreach (var inner in a.Keys)
                {
                    if (!await MatchesAsync(view, inner, uid, allMsns, ct))
                    {
                        return false;
                    }
                }

                return true;
            case HeaderKey h:
                return await MatchesHeaderAsync(entry.Message, h, ct);
            case TextKey t:
                return await MatchesTextAsync(entry.Message, t, ct);
            default:
                return false;
        }
    }

    private async ValueTask<bool> MatchesHeaderAsync(MessageId id, HeaderKey key, CancellationToken ct)
    {
        if (_messages.TryGetValue(id, out var message))
        {
            var summary = message.Summary;
            string? text = key.Field.ToUpperInvariant() switch
            {
                "FROM" => summary.From?.ToString(),
                "TO" => string.Join(", ", summary.To),
                "CC" => string.Join(", ", summary.Cc),
                "BCC" => string.Join(", ", summary.Bcc),
                "SUBJECT" => summary.Subject,
                _ => null,
            };
            if (text is not null || key.Field.ToUpperInvariant() is "FROM" or "TO" or "CC" or "BCC" or "SUBJECT")
            {
                return Contains(text, key.Value);
            }
        }

        var parsed = await LoadAsync(id, ct);
        if (parsed is null)
        {
            return false;
        }

        var values = parsed.Headers.Where(h => h.Field.Equals(key.Field, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
        return values.Count > 0 && (key.Value.Length == 0 || values.Any(v => Contains(v, key.Value)));
    }

    private async ValueTask<bool> MatchesTextAsync(MessageId id, TextKey key, CancellationToken ct)
    {
        var parsed = await LoadAsync(id, ct);
        if (parsed is null)
        {
            return false;
        }

        if (key.IncludeHeaders && parsed.Headers.Any(h => Contains(h.Value, key.Value)))
        {
            return true;
        }

        return Contains(parsed.TextBody, key.Value)
               || Contains(parsed.HtmlBody, key.Value)
               || parsed.BodyParts.OfType<TextPart>().Any(p => Contains(p.Text, key.Value));
    }

    private async ValueTask<MimeMessage?> LoadAsync(MessageId id, CancellationToken ct)
    {
        if (!_parsed.TryGetValue(id, out var parsed))
        {
            try
            {
                parsed = await session.Services.Store.LoadParsedAsync(id, ct);
            }
            catch (MailStoreException)
            {
                parsed = null;
            }

            _parsed[id] = parsed;
        }

        return parsed;
    }

    private static bool Compare(DateTime value, DateComparison comparison, DateTime date) => comparison switch
    {
        DateComparison.Before => value < date.Date,
        DateComparison.On => value == date.Date,
        _ => value >= date.Date,
    };

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
