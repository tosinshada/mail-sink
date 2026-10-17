using System.Globalization;
using System.Text;

namespace Mailsink.Imap.Protocol;

/// <summary>An IMAP sequence set such as <c>1:3,5,7:*</c>. <c>*</c> resolves to the largest number in use.</summary>
internal sealed class SequenceSet
{
    private readonly List<(uint Start, uint End)> _ranges;

    private SequenceSet(List<(uint, uint)> ranges) => _ranges = ranges;

    /// <summary>Marker for <c>*</c> before resolution.</summary>
    public const uint Star = uint.MaxValue;

    public static bool TryParse(string text, out SequenceSet set)
    {
        set = new SequenceSet([]);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var part in text.Split(','))
        {
            var colon = part.IndexOf(':');
            if (colon < 0)
            {
                if (!TryParseNumber(part, out var n))
                {
                    return false;
                }

                set._ranges.Add((n, n));
            }
            else
            {
                if (!TryParseNumber(part[..colon], out var a) || !TryParseNumber(part[(colon + 1)..], out var b))
                {
                    return false;
                }

                set._ranges.Add((a, b));
            }
        }

        return true;
    }

    public static SequenceSet Parse(string text) =>
        TryParse(text, out var set) ? set : throw new ImapParseException("Invalid sequence set");

    /// <summary>Numbers from <paramref name="available"/> (sorted ascending) that the set selects.</summary>
    public IReadOnlyList<uint> Resolve(IReadOnlyList<uint> available)
    {
        if (available.Count == 0)
        {
            return [];
        }

        var max = available[^1];
        var result = new SortedSet<uint>();
        foreach (var (rawStart, rawEnd) in _ranges)
        {
            var a = rawStart == Star ? max : rawStart;
            var b = rawEnd == Star ? max : rawEnd;
            var lo = Math.Min(a, b);
            var hi = Math.Max(a, b);
            var index = LowerBound(available, lo);
            for (var i = index; i < available.Count && available[i] <= hi; i++)
            {
                result.Add(available[i]);
            }
        }

        return [.. result];
    }

    /// <summary>Formats numbers compactly: <c>1:3,5,7:9</c>.</summary>
    public static string Format(IEnumerable<uint> numbers)
    {
        var sorted = numbers.Distinct().Order().ToList();
        var sb = new StringBuilder();
        for (var i = 0; i < sorted.Count; i++)
        {
            var start = sorted[i];
            var end = start;
            while (i + 1 < sorted.Count && sorted[i + 1] == end + 1)
            {
                end = sorted[++i];
            }

            if (sb.Length > 0)
            {
                sb.Append(',');
            }

            sb.Append(start.ToString(CultureInfo.InvariantCulture));
            if (end != start)
            {
                sb.Append(':').Append(end.ToString(CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    /// <summary>Formats numbers in the given order (COPYUID needs source/destination pairs to line up).</summary>
    public static string FormatOrdered(IReadOnlyList<uint> numbers) =>
        string.Join(',', numbers.Select(n => n.ToString(CultureInfo.InvariantCulture)));

    private static bool TryParseNumber(string text, out uint value)
    {
        if (text == "*")
        {
            value = Star;
            return true;
        }

        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
    }

    private static int LowerBound(IReadOnlyList<uint> list, uint value)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (list[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
