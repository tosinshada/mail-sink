using Microsoft.Extensions.Caching.Memory;
using Mailsink.Core.Domain;
using Mailsink.Core.Mime;
using Mailsink.Core.Storage;

namespace Mailsink.Imap.Fetch;

/// <summary>Raw bytes plus their MIME offset tree, cached per message for FETCH section requests.</summary>
internal sealed record ParsedRaw(byte[] Raw, MimeNode Root);

public sealed class ImapMessageCache(IMailStore store) : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 64L * 1024 * 1024 });

    internal async Task<ParsedRaw> GetAsync(MessageId id, CancellationToken ct)
    {
        if (_cache.TryGetValue(id, out ParsedRaw? cached) && cached is not null)
        {
            return cached;
        }

        var raw = await store.ReadRawAsync(id, ct);
        var parsed = new ParsedRaw(raw, MimeStructureParser.Parse(raw));
        _cache.Set(id, parsed, new MemoryCacheEntryOptions { Size = Math.Max(1, raw.Length), SlidingExpiration = TimeSpan.FromMinutes(5) });
        return parsed;
    }

    public void Dispose() => _cache.Dispose();
}

/// <summary>Resolves IMAP section specs (<c>1.2.MIME</c>, <c>HEADER.FIELDS (…)</c>, <c>TEXT</c>…) to exact raw bytes.</summary>
internal static class SectionExtractor
{
    public static byte[] Extract(ParsedRaw message, SectionSpec section)
    {
        var raw = message.Raw.AsSpan();
        var root = message.Root;

        MimeNode? part = root;
        if (section.Path.Count > 0)
        {
            part = Resolve(root, section.Path);
            if (part is null)
            {
                return [];
            }
        }

        switch (section.Text)
        {
            case SectionText.None:
                return section.Path.Count == 0 ? message.Raw : raw[part.BodyStart..part.End].ToArray();
            case SectionText.Mime:
                return raw[part.Start..part.BodyStart].ToArray();
        }

        // HEADER / HEADER.FIELDS / TEXT apply to a message: the root, or an encapsulated message/rfc822 part.
        var target = section.Path.Count == 0 ? root : part.Message ?? part;
        return section.Text switch
        {
            SectionText.Header => HeaderBlock(raw, target),
            SectionText.Text => raw[target.BodyStart..target.End].ToArray(),
            SectionText.HeaderFields => FilterHeaders(raw, target, section.Fields, include: true),
            SectionText.HeaderFieldsNot => FilterHeaders(raw, target, section.Fields, include: false),
            _ => [],
        };
    }

    public static byte[] HeaderBlock(ReadOnlySpan<byte> raw, MimeNode node)
    {
        var block = raw[node.Start..node.BodyStart];
        if (block.EndsWith("\r\n\r\n"u8) || block.Length == 0)
        {
            return block.ToArray();
        }

        // No blank line (header-only message): terminate the header block.
        return block.EndsWith("\r\n"u8) ? [.. block, (byte)'\r', (byte)'\n'] : [.. block, (byte)'\r', (byte)'\n', (byte)'\r', (byte)'\n'];
    }

    public static MimeNode? Resolve(MimeNode root, IReadOnlyList<int> path)
    {
        var node = root;
        for (var i = 0; i < path.Count; i++)
        {
            var n = path[i];
            if (i > 0 && node.IsMessage && node.Message is not null)
            {
                node = node.Message;
            }

            if (node.IsMultipart && node.Children.Count > 0)
            {
                if (n > node.Children.Count)
                {
                    return null;
                }

                node = node.Children[n - 1];
            }
            else if (n != 1)
            {
                return null;
            }
        }

        return node;
    }

    private static byte[] FilterHeaders(ReadOnlySpan<byte> raw, MimeNode node, IReadOnlyList<string> fields, bool include)
    {
        var output = new List<byte>(256);
        foreach (var header in node.Headers)
        {
            var listed = fields.Contains(header.Name, StringComparer.OrdinalIgnoreCase);
            if (listed == include)
            {
                output.AddRange(raw[header.FieldStart..header.FieldEnd]);
            }
        }

        output.Add((byte)'\r');
        output.Add((byte)'\n');
        return [.. output];
    }
}
