using System.Globalization;
using System.Text;
using MimeKit;
using MimeKit.Utils;

namespace Mailsink.Core.Mime;

/// <summary>IMAP string encoding helpers shared by the renderer and the IMAP server.</summary>
public static class ImapString
{
    /// <summary>Appends <paramref name="value"/> as a quoted string, or as a literal when quoting is not possible.</summary>
    public static StringBuilder AppendString(this StringBuilder sb, string value)
    {
        if (NeedsLiteral(value))
        {
            sb.Append('{').Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)).Append("}\r\n").Append(value);
            return sb;
        }

        sb.Append('"');
        foreach (var c in value)
        {
            if (c is '"' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.Append('"');
    }

    public static StringBuilder AppendNString(this StringBuilder sb, string? value) =>
        value is null ? sb.Append("NIL") : sb.AppendString(value);

    public static bool NeedsLiteral(string value)
    {
        if (value.Length > 4096)
        {
            return true;
        }

        foreach (var c in value)
        {
            if (c is '\r' or '\n' or '\0' || c > 127)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Renders IMAP ENVELOPE and BODYSTRUCTURE for a parsed <see cref="MimeNode"/> tree.</summary>
public static class ImapRenderer
{
    private static readonly FormatOptions EncodeOptions = FormatOptions.Default;

    public static string RenderEnvelope(ReadOnlySpan<byte> raw, MimeNode message)
    {
        var sb = new StringBuilder(256);
        WriteEnvelope(sb, raw, message);
        return sb.ToString();
    }

    public static string RenderBodyStructure(ReadOnlySpan<byte> raw, MimeNode root, bool extended)
    {
        var sb = new StringBuilder(256);
        WriteBody(sb, raw, root, extended);
        return sb.ToString();
    }

    private static void WriteEnvelope(StringBuilder sb, ReadOnlySpan<byte> raw, MimeNode message)
    {
        var from = message.GetHeader(raw, "From");
        var sender = message.GetHeader(raw, "Sender") ?? from;
        var replyTo = message.GetHeader(raw, "Reply-To") ?? from;

        sb.Append('(');
        sb.AppendNString(message.GetHeader(raw, "Date")).Append(' ');
        sb.AppendNString(message.GetHeader(raw, "Subject")).Append(' ');
        WriteAddresses(sb, from).Append(' ');
        WriteAddresses(sb, sender).Append(' ');
        WriteAddresses(sb, replyTo).Append(' ');
        WriteAddresses(sb, message.GetHeader(raw, "To")).Append(' ');
        WriteAddresses(sb, message.GetHeader(raw, "Cc")).Append(' ');
        WriteAddresses(sb, message.GetHeader(raw, "Bcc")).Append(' ');
        sb.AppendNString(message.GetHeader(raw, "In-Reply-To")).Append(' ');
        sb.AppendNString(message.GetHeader(raw, "Message-ID"));
        sb.Append(')');
    }

    private static StringBuilder WriteAddresses(StringBuilder sb, string? header)
    {
        if (string.IsNullOrWhiteSpace(header) || !InternetAddressList.TryParse(header, out var list) || list.Count == 0)
        {
            return sb.Append("NIL");
        }

        sb.Append('(');
        foreach (var address in list)
        {
            WriteAddress(sb, address);
        }

        return sb.Append(')');
    }

    private static void WriteAddress(StringBuilder sb, InternetAddress address)
    {
        switch (address)
        {
            case MailboxAddress mailbox:
                sb.Append('(');
                sb.AppendNString(EncodeName(mailbox.Name)).Append(" NIL ");
                var at = mailbox.Address.LastIndexOf('@');
                if (at > 0)
                {
                    sb.AppendString(mailbox.Address[..at]).Append(' ').AppendString(mailbox.Address[(at + 1)..]);
                }
                else
                {
                    sb.AppendString(mailbox.Address).Append(" NIL");
                }

                sb.Append(')');
                break;
            case GroupAddress group:
                sb.Append("(NIL NIL ").AppendString(group.Name ?? "").Append(" NIL)");
                foreach (var member in group.Members)
                {
                    WriteAddress(sb, member);
                }

                sb.Append("(NIL NIL NIL NIL)");
                break;
        }
    }

    private static string? EncodeName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        foreach (var c in name)
        {
            if (c > 127)
            {
                return Encoding.ASCII.GetString(Rfc2047.EncodePhrase(EncodeOptions, Encoding.UTF8, name));
            }
        }

        return name;
    }

    private static void WriteBody(StringBuilder sb, ReadOnlySpan<byte> raw, MimeNode node, bool extended)
    {
        if (node.IsMultipart && node.Children.Count > 0)
        {
            sb.Append('(');
            foreach (var child in node.Children)
            {
                WriteBody(sb, raw, child, extended);
            }

            sb.Append(' ').AppendString(node.ContentType.MediaSubtype.ToUpperInvariant());
            if (extended)
            {
                sb.Append(' ');
                WriteParams(sb, node.ContentType.Parameters);
                sb.Append(' ');
                WriteDisposition(sb, raw, node);
                sb.Append(' ').AppendNString(node.GetHeader(raw, "Content-Language"));
                sb.Append(' ').AppendNString(node.GetHeader(raw, "Content-Location"));
            }

            sb.Append(')');
            return;
        }

        var type = node.ContentType;
        var mediaType = node.IsMultipart ? "TEXT" : type.MediaType.ToUpperInvariant();
        var subtype = node.IsMultipart ? "PLAIN" : type.MediaSubtype.ToUpperInvariant();

        sb.Append('(');
        sb.AppendString(mediaType).Append(' ').AppendString(subtype).Append(' ');
        WriteParams(sb, type.Parameters);
        sb.Append(' ').AppendNString(node.GetHeader(raw, "Content-ID"));
        sb.Append(' ').AppendNString(node.GetHeader(raw, "Content-Description"));
        sb.Append(' ').AppendString((node.GetHeader(raw, "Content-Transfer-Encoding") ?? "7BIT").ToUpperInvariant());
        sb.Append(' ').Append(node.BodyLength.ToString(CultureInfo.InvariantCulture));

        if (node.IsMessage && node.Message is { } inner)
        {
            sb.Append(' ');
            WriteEnvelope(sb, raw, inner);
            sb.Append(' ');
            WriteBody(sb, raw, inner, extended);
            sb.Append(' ').Append(node.CountBodyLines(raw).ToString(CultureInfo.InvariantCulture));
        }
        else if (node.IsText || node.IsMultipart)
        {
            sb.Append(' ').Append(node.CountBodyLines(raw).ToString(CultureInfo.InvariantCulture));
        }

        if (extended)
        {
            sb.Append(' ').AppendNString(node.GetHeader(raw, "Content-MD5"));
            sb.Append(' ');
            WriteDisposition(sb, raw, node);
            sb.Append(' ').AppendNString(node.GetHeader(raw, "Content-Language"));
            sb.Append(' ').AppendNString(node.GetHeader(raw, "Content-Location"));
        }

        sb.Append(')');
    }

    private static void WriteParams(StringBuilder sb, ParameterList parameters)
    {
        var list = parameters.ToList();
        if (list.Count == 0)
        {
            sb.Append("NIL");
            return;
        }

        sb.Append('(');
        for (var i = 0; i < list.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.AppendString(list[i].Name.ToUpperInvariant()).Append(' ').AppendString(list[i].Value ?? "");
        }

        sb.Append(')');
    }

    private static void WriteDisposition(StringBuilder sb, ReadOnlySpan<byte> raw, MimeNode node)
    {
        var header = node.GetHeader(raw, "Content-Disposition");
        if (header is null || !ContentDisposition.TryParse(header, out var disposition))
        {
            sb.Append("NIL");
            return;
        }

        sb.Append('(').AppendString(disposition.Disposition.ToUpperInvariant()).Append(' ');
        WriteParams(sb, disposition.Parameters);
        sb.Append(')');
    }
}
