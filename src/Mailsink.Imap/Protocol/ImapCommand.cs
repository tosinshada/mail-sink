using System.Globalization;
using System.Text;

namespace Mailsink.Imap.Protocol;

internal abstract record ImapValue
{
    /// <summary>The value as an IMAP astring (atom, quoted string or literal), or null for lists.</summary>
    public virtual string? AsString() => null;

    public bool IsAtom(string value) => this is ImapAtom atom && atom.Value.Equals(value, StringComparison.OrdinalIgnoreCase);
}

internal sealed record ImapAtom(string Value) : ImapValue
{
    public override string AsString() => Value;

    public override string ToString() => Value;
}

internal sealed record ImapQuoted(string Value) : ImapValue
{
    public override string AsString() => Value;
}

internal sealed record ImapLiteral(byte[] Data) : ImapValue
{
    public override string AsString() => Encoding.UTF8.GetString(Data);
}

internal sealed record ImapList(IReadOnlyList<ImapValue> Items) : ImapValue;

internal sealed record ImapCommand(string Tag, string Name, bool IsUid, IReadOnlyList<ImapValue> Args)
{
    public string Arg(int index) =>
        index < Args.Count && Args[index].AsString() is { } s ? s : throw new ImapParseException($"Missing argument {index + 1}");

    public ImapValue? ArgAt(int index) => index < Args.Count ? Args[index] : null;
}

internal sealed class ImapParseException(string message, string? tag = null) : Exception(message)
{
    public string? Tag { get; } = tag;
}

/// <summary>
/// Hand-written parser for one complete IMAP command. The buffer already contains every literal's bytes
/// (the reader resolved <c>{n}</c> / <c>{n+}</c> continuations), so literals are read inline.
/// </summary>
internal static class ImapParser
{
    private const int MaxDepth = 32;

    public static ImapCommand Parse(ReadOnlySpan<byte> buffer)
    {
        var pos = 0;
        var tag = ReadTag(buffer, ref pos);
        if (tag.Length == 0)
        {
            throw new ImapParseException("Missing tag");
        }

        try
        {
            ExpectSpace(buffer, ref pos);
            var name = ReadAtomText(buffer, ref pos).ToUpperInvariant();
            if (name.Length == 0)
            {
                throw new ImapParseException("Missing command");
            }

            var isUid = false;
            if (name == "UID")
            {
                ExpectSpace(buffer, ref pos);
                name = ReadAtomText(buffer, ref pos).ToUpperInvariant();
                isUid = true;
            }

            var args = ReadValues(buffer, ref pos, closing: null, depth: 0);
            return new ImapCommand(tag, name, isUid, args);
        }
        catch (ImapParseException ex) when (ex.Tag is null)
        {
            throw new ImapParseException(ex.Message, tag);
        }
    }

    /// <summary>Parses a standalone argument string (used for AUTHENTICATE continuation and tests).</summary>
    public static IReadOnlyList<ImapValue> ParseValues(ReadOnlySpan<byte> buffer)
    {
        var pos = 0;
        return ReadValues(buffer, ref pos, null, 0);
    }

    private static List<ImapValue> ReadValues(ReadOnlySpan<byte> buffer, ref int pos, byte? closing, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new ImapParseException("Nesting too deep");
        }

        var values = new List<ImapValue>();
        while (true)
        {
            while (pos < buffer.Length && buffer[pos] is (byte)' ' or (byte)'\r' or (byte)'\n')
            {
                pos++;
            }

            if (pos >= buffer.Length)
            {
                if (closing is not null)
                {
                    throw new ImapParseException("Unterminated list");
                }

                return values;
            }

            var c = buffer[pos];
            if (closing is { } close && c == close)
            {
                pos++;
                return values;
            }

            switch (c)
            {
                case (byte)'(':
                    pos++;
                    values.Add(new ImapList(ReadValues(buffer, ref pos, (byte)')', depth + 1)));
                    break;
                case (byte)')':
                    throw new ImapParseException("Unexpected )");
                case (byte)'"':
                    values.Add(new ImapQuoted(ReadQuoted(buffer, ref pos)));
                    break;
                case (byte)'{':
                    values.Add(new ImapLiteral(ReadLiteral(buffer, ref pos)));
                    break;
                case (byte)'~' when pos + 1 < buffer.Length && buffer[pos + 1] == (byte)'{':
                    pos++;
                    values.Add(new ImapLiteral(ReadLiteral(buffer, ref pos)));
                    break;
                default:
                    values.Add(new ImapAtom(ReadAtomText(buffer, ref pos)));
                    break;
            }
        }
    }

    private static string ReadTag(ReadOnlySpan<byte> buffer, ref int pos)
    {
        var start = pos;
        while (pos < buffer.Length && buffer[pos] is not ((byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'(' or (byte)')' or (byte)'{' or (byte)'"' or (byte)'*' or (byte)'%' or (byte)'+'))
        {
            pos++;
        }

        return Encoding.ASCII.GetString(buffer[start..pos]);
    }

    /// <summary>Reads an atom. A <c>[</c> starts a bracketed run (e.g. <c>BODY[HEADER.FIELDS (A B)]</c>) that may contain spaces and parens.</summary>
    private static string ReadAtomText(ReadOnlySpan<byte> buffer, ref int pos)
    {
        var start = pos;
        while (pos < buffer.Length)
        {
            var c = buffer[pos];
            if (c == (byte)'[')
            {
                var depth = 0;
                while (pos < buffer.Length)
                {
                    if (buffer[pos] == (byte)'[')
                    {
                        depth++;
                    }
                    else if (buffer[pos] == (byte)']' && --depth == 0)
                    {
                        pos++;
                        break;
                    }
                    else if (buffer[pos] is (byte)'\r' or (byte)'\n')
                    {
                        throw new ImapParseException("Unterminated [");
                    }

                    pos++;
                }

                continue;
            }

            if (c is (byte)' ' or (byte)'(' or (byte)')' or (byte)'\r' or (byte)'\n' or (byte)'"' or (byte)'{' || c < 0x20)
            {
                break;
            }

            pos++;
        }

        return Encoding.UTF8.GetString(buffer[start..pos]);
    }

    private static string ReadQuoted(ReadOnlySpan<byte> buffer, ref int pos)
    {
        pos++; // opening quote
        var bytes = new List<byte>();
        while (pos < buffer.Length)
        {
            var c = buffer[pos++];
            if (c == (byte)'"')
            {
                return Encoding.UTF8.GetString([.. bytes]);
            }

            if (c == (byte)'\\' && pos < buffer.Length)
            {
                c = buffer[pos++];
            }
            else if (c is (byte)'\r' or (byte)'\n')
            {
                throw new ImapParseException("Unterminated quoted string");
            }

            bytes.Add(c);
        }

        throw new ImapParseException("Unterminated quoted string");
    }

    private static byte[] ReadLiteral(ReadOnlySpan<byte> buffer, ref int pos)
    {
        pos++; // {
        var start = pos;
        while (pos < buffer.Length && char.IsAsciiDigit((char)buffer[pos]))
        {
            pos++;
        }

        if (!int.TryParse(buffer[start..pos], NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            throw new ImapParseException("Invalid literal");
        }

        if (pos < buffer.Length && buffer[pos] is (byte)'+' or (byte)'-')
        {
            pos++;
        }

        if (pos >= buffer.Length || buffer[pos] != (byte)'}')
        {
            throw new ImapParseException("Invalid literal");
        }

        pos++;
        if (pos + 1 < buffer.Length && buffer[pos] == (byte)'\r' && buffer[pos + 1] == (byte)'\n')
        {
            pos += 2;
        }
        else if (pos < buffer.Length && buffer[pos] == (byte)'\n')
        {
            pos++;
        }
        else
        {
            throw new ImapParseException("Literal must be followed by CRLF");
        }

        if (pos + length > buffer.Length)
        {
            throw new ImapParseException("Literal truncated");
        }

        var data = buffer.Slice(pos, length).ToArray();
        pos += length;
        return data;
    }

    private static void ExpectSpace(ReadOnlySpan<byte> buffer, ref int pos)
    {
        if (pos >= buffer.Length || buffer[pos] != (byte)' ')
        {
            throw new ImapParseException("Expected space");
        }

        pos++;
    }

    /// <summary>
    /// If <paramref name="line"/> ends with a literal announcement (<c>{n}</c>, <c>{n+}</c>, <c>~{n}</c>),
    /// returns its length and whether it is synchronizing.
    /// </summary>
    public static bool TryGetTrailingLiteral(ReadOnlySpan<byte> line, out long length, out bool synchronizing)
    {
        length = 0;
        synchronizing = true;
        if (line.Length < 3 || line[^1] != (byte)'}')
        {
            return false;
        }

        var open = line.LastIndexOf((byte)'{');
        if (open < 0)
        {
            return false;
        }

        var inner = line[(open + 1)..^1];
        if (inner.Length > 0 && inner[^1] is (byte)'+' or (byte)'-')
        {
            synchronizing = false;
            inner = inner[..^1];
        }

        return inner.Length > 0 && long.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out length);
    }
}
