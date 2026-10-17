using System.Text;

namespace Mailsink.Imap.Protocol;

/// <summary>RFC 3501 §5.1.3 modified UTF-7 for mailbox names on the wire. Names are stored as UTF-8.</summary>
internal static class ModifiedUtf7
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+,";

    public static string Encode(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pending = new List<char>();

        void Flush()
        {
            if (pending.Count == 0)
            {
                return;
            }

            var bytes = Encoding.BigEndianUnicode.GetBytes(pending.ToArray());
            sb.Append('&');
            var bits = 0;
            var buffer = 0;
            foreach (var b in bytes)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 6)
                {
                    bits -= 6;
                    sb.Append(Alphabet[(buffer >> bits) & 0x3F]);
                }
            }

            if (bits > 0)
            {
                sb.Append(Alphabet[(buffer << (6 - bits)) & 0x3F]);
            }

            sb.Append('-');
            pending.Clear();
        }

        foreach (var c in value)
        {
            if (c is >= (char)0x20 and <= (char)0x7E)
            {
                Flush();
                sb.Append(c == '&' ? "&-" : c.ToString());
            }
            else
            {
                pending.Add(c);
            }
        }

        Flush();
        return sb.ToString();
    }

    public static string Decode(string value)
    {
        if (!value.Contains('&'))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '&')
            {
                sb.Append(value[i]);
                continue;
            }

            var end = value.IndexOf('-', i + 1);
            if (end < 0)
            {
                sb.Append(value[i..]);
                break;
            }

            if (end == i + 1)
            {
                sb.Append('&');
                i = end;
                continue;
            }

            var bytes = new List<byte>();
            var bits = 0;
            var buffer = 0;
            for (var j = i + 1; j < end; j++)
            {
                var index = Alphabet.IndexOf(value[j]);
                if (index < 0)
                {
                    return value; // not valid modified UTF-7; treat as raw
                }

                buffer = (buffer << 6) | index;
                bits += 6;
                if (bits >= 8)
                {
                    bits -= 8;
                    bytes.Add((byte)((buffer >> bits) & 0xFF));
                }
            }

            sb.Append(Encoding.BigEndianUnicode.GetString([.. bytes]));
            i = end;
        }

        return sb.ToString();
    }
}
