using System.Globalization;
using Mailsink.Imap.Protocol;

namespace Mailsink.Imap.Fetch;

internal enum FetchKind
{
    Flags,
    InternalDate,
    Rfc822Size,
    Uid,
    Envelope,
    BodyStructure,
    Body,
    Section,
    Rfc822,
    Rfc822Header,
    Rfc822Text,
    ModSeq,
}

internal enum SectionText { None, Header, HeaderFields, HeaderFieldsNot, Text, Mime }

internal sealed record SectionSpec(IReadOnlyList<int> Path, SectionText Text, IReadOnlyList<string> Fields)
{
    public static readonly SectionSpec Whole = new([], SectionText.None, []);

    /// <summary>The canonical section string echoed in the response, e.g. <c>1.2.HEADER.FIELDS (SUBJECT FROM)</c>.</summary>
    public string Format()
    {
        var parts = Path.Select(p => p.ToString(CultureInfo.InvariantCulture)).ToList();
        var text = Text switch
        {
            SectionText.Header => "HEADER",
            SectionText.HeaderFields => $"HEADER.FIELDS ({string.Join(' ', Fields)})",
            SectionText.HeaderFieldsNot => $"HEADER.FIELDS.NOT ({string.Join(' ', Fields)})",
            SectionText.Text => "TEXT",
            SectionText.Mime => "MIME",
            _ => null,
        };
        if (text is not null)
        {
            parts.Add(text);
        }

        return string.Join('.', parts);
    }
}

internal sealed record FetchItem(FetchKind Kind, SectionSpec? Section = null, bool Peek = false, long? PartialOffset = null, long? PartialLength = null)
{
    public bool NeedsContent => Kind is FetchKind.Section or FetchKind.Rfc822 or FetchKind.Rfc822Header or FetchKind.Rfc822Text;

    public bool NeedsMessage => NeedsContent || Kind is FetchKind.Envelope or FetchKind.BodyStructure or FetchKind.Body;

    /// <summary>True for items that implicitly set <c>\Seen</c>.</summary>
    public bool SetsSeen => (Kind == FetchKind.Section && !Peek) || Kind is FetchKind.Rfc822 or FetchKind.Rfc822Text;
}

internal static class FetchItemParser
{
    public static IReadOnlyList<FetchItem> Parse(ImapValue value)
    {
        if (value is ImapAtom atom)
        {
            switch (atom.Value.ToUpperInvariant())
            {
                case "ALL":
                    return [new(FetchKind.Flags), new(FetchKind.InternalDate), new(FetchKind.Rfc822Size), new(FetchKind.Envelope)];
                case "FAST":
                    return [new(FetchKind.Flags), new(FetchKind.InternalDate), new(FetchKind.Rfc822Size)];
                case "FULL":
                    return [new(FetchKind.Flags), new(FetchKind.InternalDate), new(FetchKind.Rfc822Size), new(FetchKind.Envelope), new(FetchKind.Body)];
                default:
                    return [ParseOne(atom.Value)];
            }
        }

        if (value is ImapList list)
        {
            return [.. list.Items.Select(i => i is ImapAtom a ? ParseOne(a.Value) : throw new ImapParseException("Invalid FETCH item"))];
        }

        throw new ImapParseException("Invalid FETCH items");
    }

    public static FetchItem ParseOne(string text)
    {
        var upper = text.ToUpperInvariant();
        switch (upper)
        {
            case "FLAGS": return new(FetchKind.Flags);
            case "INTERNALDATE": return new(FetchKind.InternalDate);
            case "RFC822.SIZE": return new(FetchKind.Rfc822Size);
            case "UID": return new(FetchKind.Uid);
            case "ENVELOPE": return new(FetchKind.Envelope);
            case "BODYSTRUCTURE": return new(FetchKind.BodyStructure);
            case "BODY": return new(FetchKind.Body);
            case "RFC822": return new(FetchKind.Rfc822);
            case "RFC822.HEADER": return new(FetchKind.Rfc822Header);
            case "RFC822.TEXT": return new(FetchKind.Rfc822Text);
            case "MODSEQ": return new(FetchKind.ModSeq);
        }

        bool peek;
        string rest;
        if (upper.StartsWith("BODY.PEEK[", StringComparison.Ordinal))
        {
            peek = true;
            rest = text[9..];
        }
        else if (upper.StartsWith("BODY[", StringComparison.Ordinal))
        {
            peek = false;
            rest = text[4..];
        }
        else
        {
            throw new ImapParseException($"Unsupported FETCH item {text}");
        }

        var close = rest.LastIndexOf(']');
        if (close < 0)
        {
            throw new ImapParseException("Invalid section");
        }

        var section = ParseSection(rest[1..close]);
        var partial = rest[(close + 1)..];
        long? offset = null, length = null;
        if (partial.Length > 0)
        {
            if (partial[0] != '<' || partial[^1] != '>')
            {
                throw new ImapParseException("Invalid partial");
            }

            var inner = partial[1..^1].Split('.');
            offset = long.Parse(inner[0], NumberStyles.None, CultureInfo.InvariantCulture);
            if (inner.Length > 1)
            {
                length = long.Parse(inner[1], NumberStyles.None, CultureInfo.InvariantCulture);
            }
        }

        return new FetchItem(FetchKind.Section, section, peek, offset, length);
    }

    public static SectionSpec ParseSection(string section)
    {
        if (section.Length == 0)
        {
            return SectionSpec.Whole;
        }

        var path = new List<int>();
        var pos = 0;
        while (pos < section.Length && char.IsAsciiDigit(section[pos]))
        {
            var start = pos;
            while (pos < section.Length && char.IsAsciiDigit(section[pos]))
            {
                pos++;
            }

            var n = int.Parse(section.AsSpan(start, pos - start), CultureInfo.InvariantCulture);
            if (n == 0)
            {
                throw new ImapParseException("Invalid section part 0");
            }

            path.Add(n);
            if (pos < section.Length && section[pos] == '.')
            {
                pos++;
            }
            else
            {
                break;
            }
        }

        var text = section[pos..].Trim();
        var upper = text.ToUpperInvariant();
        if (upper.Length == 0)
        {
            return new SectionSpec(path, SectionText.None, []);
        }

        if (upper is "HEADER")
        {
            return new SectionSpec(path, SectionText.Header, []);
        }

        if (upper is "TEXT")
        {
            return new SectionSpec(path, SectionText.Text, []);
        }

        if (upper is "MIME")
        {
            if (path.Count == 0)
            {
                throw new ImapParseException("MIME requires a part number");
            }

            return new SectionSpec(path, SectionText.Mime, []);
        }

        var not = upper.StartsWith("HEADER.FIELDS.NOT", StringComparison.Ordinal);
        if (!not && !upper.StartsWith("HEADER.FIELDS", StringComparison.Ordinal))
        {
            throw new ImapParseException($"Invalid section {section}");
        }

        var open = text.IndexOf('(');
        var closeParen = text.LastIndexOf(')');
        if (open < 0 || closeParen < open)
        {
            throw new ImapParseException("Invalid header field list");
        }

        var fields = text[(open + 1)..closeParen]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim('"').ToUpperInvariant())
            .ToList();
        return new SectionSpec(path, not ? SectionText.HeaderFieldsNot : SectionText.HeaderFields, fields);
    }
}
