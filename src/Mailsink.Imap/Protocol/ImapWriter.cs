using System.Buffers;
using System.Globalization;
using System.Text;
using Mailsink.Core.Domain;
using Mailsink.Core.Mime;

namespace Mailsink.Imap.Protocol;

/// <summary>Builds IMAP response bytes: atoms, quoted strings, literals, lists and mailbox names.</summary>
internal sealed class ImapWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new(1024);

    public ReadOnlyMemory<byte> Written => _buffer.WrittenMemory;

    public int Length => _buffer.WrittenCount;

    public void Clear() => _buffer.ResetWrittenCount();

    public ImapWriter Raw(string text)
    {
        var span = _buffer.GetSpan(Encoding.UTF8.GetMaxByteCount(text.Length));
        _buffer.Advance(Encoding.UTF8.GetBytes(text, span));
        return this;
    }

    public ImapWriter Raw(ReadOnlySpan<byte> bytes)
    {
        _buffer.Write(bytes);
        return this;
    }

    public ImapWriter Untagged() => Raw("* "u8);

    public ImapWriter Space() => Raw(" "u8);

    public ImapWriter Line() => Raw("\r\n"u8);

    public ImapWriter Number(long value) => Raw(value.ToString(CultureInfo.InvariantCulture));

    public ImapWriter Number(ulong value) => Raw(value.ToString(CultureInfo.InvariantCulture));

    public ImapWriter String(string value) => Raw(new StringBuilder().AppendString(value).ToString());

    public ImapWriter NString(string? value) => value is null ? Raw("NIL"u8) : String(value);

    public ImapWriter Literal(ReadOnlySpan<byte> data)
    {
        Raw("{").Number(data.Length).Raw("}\r\n");
        return Raw(data);
    }

    /// <summary>An astring: bare atom when safe, otherwise quoted or literal.</summary>
    public ImapWriter AString(string value) => IsAtom(value) ? Raw(value) : String(value);

    /// <summary>A mailbox name in modified UTF-7.</summary>
    public ImapWriter Mailbox(string name) => AString(ModifiedUtf7.Encode(name));

    public ImapWriter Flags(SystemFlags flags, IEnumerable<string> keywords, bool recent)
    {
        Raw("(");
        var first = true;
        foreach (var name in FlagNames.ToNames(flags))
        {
            if (!first)
            {
                Space();
            }

            Raw(name);
            first = false;
        }

        if (recent)
        {
            if (!first)
            {
                Space();
            }

            Raw("\\Recent");
            first = false;
        }

        foreach (var keyword in keywords)
        {
            if (!first)
            {
                Space();
            }

            Raw(keyword);
            first = false;
        }

        return Raw(")");
    }

    public static string FormatInternalDate(DateTimeOffset date) =>
        date.ToString("dd-MMM-yyyy HH:mm:ss ", CultureInfo.InvariantCulture) +
        (date.Offset < TimeSpan.Zero ? "-" : "+") + date.Offset.Duration().ToString("hhmm", CultureInfo.InvariantCulture);

    public static bool IsAtom(string value)
    {
        if (value.Length == 0 || value.Equals("NIL", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c <= ' ' || c >= 127 || c is '(' or ')' or '{' or '"' or '\\' or '%' or '*' or ']')
            {
                return false;
            }
        }

        return true;
    }
}
