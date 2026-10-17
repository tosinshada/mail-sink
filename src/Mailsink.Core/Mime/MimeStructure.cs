using System.Text;
using MimeKit;

namespace Mailsink.Core.Mime;

/// <summary>A raw header field: name plus the raw (still folded) value bytes range, and the whole field range.</summary>
public readonly record struct RawHeaderField(string Name, int FieldStart, int FieldEnd, int ValueStart)
{
    /// <summary>Unfolded value as text (UTF-8, falling back to Latin-1 bytes for invalid sequences).</summary>
    public string GetValue(ReadOnlySpan<byte> raw)
    {
        var text = MimeText.Decode(raw[ValueStart..FieldEnd]);
        return MimeText.Unfold(text).Trim();
    }
}

/// <summary>
/// One MIME entity with byte offsets into the raw message. Offsets are exact, so IMAP <c>BODY[section]</c>
/// responses serve the original bytes rather than re-serialized content.
/// </summary>
public sealed class MimeNode
{
    public required int Start { get; init; }
    public required int BodyStart { get; init; }
    public required int End { get; init; }
    public required IReadOnlyList<RawHeaderField> Headers { get; init; }
    public required ContentType ContentType { get; init; }
    public List<MimeNode> Children { get; } = [];

    /// <summary>For <c>message/rfc822</c> parts, the encapsulated message.</summary>
    public MimeNode? Message { get; set; }

    public bool IsMultipart => ContentType.MediaType.Equals("multipart", StringComparison.OrdinalIgnoreCase);

    public bool IsMessage =>
        ContentType.MediaType.Equals("message", StringComparison.OrdinalIgnoreCase)
        && (ContentType.MediaSubtype.Equals("rfc822", StringComparison.OrdinalIgnoreCase)
            || ContentType.MediaSubtype.Equals("global", StringComparison.OrdinalIgnoreCase));

    public bool IsText => ContentType.MediaType.Equals("text", StringComparison.OrdinalIgnoreCase);

    public int BodyLength => End - BodyStart;

    public string? GetHeader(ReadOnlySpan<byte> raw, string name)
    {
        foreach (var h in Headers)
        {
            if (h.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return h.GetValue(raw);
            }
        }

        return null;
    }

    public int CountBodyLines(ReadOnlySpan<byte> raw)
    {
        var body = raw[BodyStart..End];
        if (body.IsEmpty)
        {
            return 0;
        }

        var lines = body.Count((byte)'\n');
        return body[^1] == (byte)'\n' ? lines : lines + 1;
    }
}

public static class MimeStructureParser
{
    private const int MaxDepth = 32;
    private static readonly ContentType TextPlain = new("text", "plain");
    private static readonly ContentType MessageRfc822 = new("message", "rfc822");

    public static MimeNode Parse(ReadOnlySpan<byte> raw) => ParseEntity(raw, 0, raw.Length, TextPlain, 0);

    private static MimeNode ParseEntity(ReadOnlySpan<byte> raw, int start, int end, ContentType defaultType, int depth)
    {
        var headers = new List<RawHeaderField>();
        var pos = start;
        var bodyStart = end;
        string? currentName = null;
        int currentStart = 0, currentValueStart = 0;

        while (pos < end)
        {
            var lineEnd = IndexOfLineEnd(raw, pos, end, out var lineEndWithBreak);
            var line = raw[pos..lineEnd];

            if (line.IsEmpty)
            {
                // Blank line: end of headers.
                if (currentName is not null)
                {
                    headers.Add(new RawHeaderField(currentName, currentStart, pos, currentValueStart));
                    currentName = null;
                }

                bodyStart = lineEndWithBreak;
                break;
            }

            if ((line[0] == (byte)' ' || line[0] == (byte)'\t') && currentName is not null)
            {
                // Folded continuation.
                pos = lineEndWithBreak;
                continue;
            }

            if (currentName is not null)
            {
                headers.Add(new RawHeaderField(currentName, currentStart, pos, currentValueStart));
                currentName = null;
            }

            var colon = line.IndexOf((byte)':');
            if (colon > 0)
            {
                currentName = Encoding.ASCII.GetString(line[..colon]).Trim();
                currentStart = pos;
                currentValueStart = pos + colon + 1;
            }
            else if (headers.Count == 0 && depth > 0 && !LooksLikeHeader(line))
            {
                // Part without a header block: everything is body.
                bodyStart = start;
                break;
            }

            pos = lineEndWithBreak;
        }

        if (currentName is not null)
        {
            headers.Add(new RawHeaderField(currentName, currentStart, end, currentValueStart));
        }

        var contentType = defaultType;
        foreach (var h in headers)
        {
            if (h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                var value = raw[h.ValueStart..h.FieldEnd].ToArray();
                if (ContentType.TryParse(ParserOptions.Default, value, out var parsed))
                {
                    contentType = parsed;
                }

                break;
            }
        }

        var node = new MimeNode
        {
            Start = start,
            BodyStart = Math.Min(bodyStart, end),
            End = end,
            Headers = headers,
            ContentType = contentType,
        };

        if (depth >= MaxDepth)
        {
            return node;
        }

        if (node.IsMultipart && contentType.Boundary is { Length: > 0 } boundary)
        {
            var childDefault = contentType.MediaSubtype.Equals("digest", StringComparison.OrdinalIgnoreCase) ? MessageRfc822 : TextPlain;
            foreach (var (partStart, partEnd) in SplitMultipart(raw, node.BodyStart, end, boundary))
            {
                node.Children.Add(ParseEntity(raw, partStart, partEnd, childDefault, depth + 1));
            }
        }
        else if (node.IsMessage)
        {
            node.Message = ParseEntity(raw, node.BodyStart, end, TextPlain, depth + 1);
        }

        return node;
    }

    private static bool LooksLikeHeader(ReadOnlySpan<byte> line)
    {
        foreach (var b in line)
        {
            if (b == (byte)':')
            {
                return true;
            }

            if (b <= 32 || b >= 127)
            {
                return false;
            }
        }

        return false;
    }

    private static List<(int Start, int End)> SplitMultipart(ReadOnlySpan<byte> raw, int start, int end, string boundary)
    {
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var parts = new List<(int, int)>();
        var pos = start;
        var partStart = -1;

        while (pos < end)
        {
            var lineEnd = IndexOfLineEnd(raw, pos, end, out var next);
            var line = raw[pos..lineEnd];

            if (line.StartsWith(delimiter))
            {
                var rest = line[delimiter.Length..];
                var isClose = rest.StartsWith("--"u8);
                if (isClose)
                {
                    rest = rest[2..];
                }

                if (IsWhitespace(rest))
                {
                    if (partStart >= 0)
                    {
                        // The CRLF before the delimiter belongs to the delimiter.
                        var partEnd = pos;
                        if (partEnd > partStart && raw[partEnd - 1] == (byte)'\n')
                        {
                            partEnd--;
                            if (partEnd > partStart && raw[partEnd - 1] == (byte)'\r')
                            {
                                partEnd--;
                            }
                        }

                        parts.Add((partStart, Math.Max(partStart, partEnd)));
                    }

                    if (isClose)
                    {
                        return parts;
                    }

                    partStart = next;
                }
            }

            pos = next;
        }

        if (partStart >= 0 && partStart <= end)
        {
            parts.Add((partStart, end));
        }

        return parts;
    }

    private static bool IsWhitespace(ReadOnlySpan<byte> span)
    {
        foreach (var b in span)
        {
            if (b != (byte)' ' && b != (byte)'\t')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns the index of the line content end (before CR/LF) and the start of the next line.</summary>
    private static int IndexOfLineEnd(ReadOnlySpan<byte> raw, int pos, int end, out int next)
    {
        var lf = raw[pos..end].IndexOf((byte)'\n');
        if (lf < 0)
        {
            next = end;
            return end;
        }

        var lineEnd = pos + lf;
        next = lineEnd + 1;
        if (lineEnd > pos && raw[lineEnd - 1] == (byte)'\r')
        {
            lineEnd--;
        }

        return lineEnd;
    }
}

public static class MimeText
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    public static string Unfold(string value) => value.Replace("\r\n", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);

    /// <summary>Converts bare LF (and lone CR) line endings to CRLF. IMAP and SMTP need CRLF everywhere.</summary>
    public static byte[] NormalizeLineEndings(ReadOnlySpan<byte> raw)
    {
        var needs = false;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == (byte)'\n' && (i == 0 || raw[i - 1] != (byte)'\r'))
            {
                needs = true;
                break;
            }

            if (raw[i] == (byte)'\r' && (i + 1 >= raw.Length || raw[i + 1] != (byte)'\n'))
            {
                needs = true;
                break;
            }
        }

        if (!needs)
        {
            return raw.ToArray();
        }

        var output = new List<byte>(raw.Length + 256);
        for (var i = 0; i < raw.Length; i++)
        {
            var b = raw[i];
            if (b == (byte)'\r')
            {
                output.Add((byte)'\r');
                output.Add((byte)'\n');
                if (i + 1 < raw.Length && raw[i + 1] == (byte)'\n')
                {
                    i++;
                }
            }
            else if (b == (byte)'\n')
            {
                output.Add((byte)'\r');
                output.Add((byte)'\n');
            }
            else
            {
                output.Add(b);
            }
        }

        return [.. output];
    }
}
