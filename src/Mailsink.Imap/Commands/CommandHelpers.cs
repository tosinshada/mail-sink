using System.Globalization;
using Mailsink.Core.Domain;
using Mailsink.Imap.Protocol;
using Mailsink.Imap.Session;

namespace Mailsink.Imap.Commands;

internal static class CommandHelpers
{
    /// <summary>Resolves a sequence set (MSNs, or UIDs for UID commands) to UIDs in MSN order.</summary>
    public static IReadOnlyList<uint> ResolveUids(SelectedMailboxView view, string setText, bool isUid)
    {
        var set = SequenceSet.Parse(setText);
        if (isUid)
        {
            return set.Resolve(view.Uids);
        }

        var msns = set.Resolve([.. Enumerable.Range(1, view.Count).Select(i => (uint)i)]);
        return [.. msns.Select(m => view.UidAt((int)m))];
    }

    /// <summary>Parses a flag list (or a run of flag atoms) into system flags and keywords.</summary>
    public static (SystemFlags Flags, List<string> Keywords) ParseFlags(IEnumerable<ImapValue> values)
    {
        var flags = SystemFlags.None;
        var keywords = new List<string>();
        foreach (var value in values)
        {
            if (value is ImapList list)
            {
                var (f, k) = ParseFlags(list.Items);
                flags |= f;
                keywords.AddRange(k);
                continue;
            }

            var text = value.AsString() ?? throw new ImapParseException("Invalid flag");
            if (text.StartsWith('\\'))
            {
                if (FlagNames.TryParseSystem(text, out var flag))
                {
                    flags |= flag & ~SystemFlags.Recent;
                }
                else if (text != "\\*")
                {
                    throw new ImapParseException($"Unknown system flag {text}");
                }
            }
            else if (ImapWriter.IsAtom(text))
            {
                keywords.Add(text);
            }
            else
            {
                throw new ImapParseException($"Invalid keyword {text}");
            }
        }

        return (flags, keywords);
    }

    public static string MailboxName(ImapCommand command, int index) => ModifiedUtf7.Decode(command.Arg(index));

    public static bool TryParseDate(string text, out DateTime date) =>
        DateTime.TryParseExact(text.Trim(), ["d-MMM-yyyy", "dd-MMM-yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>Parses an IMAP date-time such as <c>17-Oct-2026 10:00:00 +0000</c> (day may be space-padded).</summary>
    public static bool TryParseDateTime(string text, out DateTimeOffset value)
    {
        value = default;
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[2].Length != 5 || !TryParseDate(parts[0], out var date)
            || !TimeSpan.TryParseExact(parts[1], "hh\\:mm\\:ss", CultureInfo.InvariantCulture, out var time))
        {
            return false;
        }

        var sign = parts[2][0] == '-' ? -1 : 1;
        if (!int.TryParse(parts[2].AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var hh)
            || !int.TryParse(parts[2].AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var mm))
        {
            return false;
        }

        value = new DateTimeOffset(date + time, sign * new TimeSpan(hh, mm, 0));
        return true;
    }

    public static async Task WriteExpungesAsync(ImapSession session, SelectedMailboxView view, IEnumerable<uint> uids, CancellationToken ct)
    {
        var w = new ImapWriter();
        foreach (var uid in uids.Where(view.Contains).OrderByDescending(u => u))
        {
            w.Untagged().Number(view.MsnOf(uid)).Raw(" EXPUNGE").Line();
            view.Remove(uid);
        }

        if (w.Length > 0)
        {
            await session.SendAsync(w, ct);
        }
    }
}
