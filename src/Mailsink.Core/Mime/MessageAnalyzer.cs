using System.Net;
using System.Text.RegularExpressions;
using MimeKit;
using Mailsink.Core.Domain;

namespace Mailsink.Core.Mime;

public sealed record MessageAnalysis(ParsedSummary Summary, ImapRendering Imap, string BodyText);

/// <summary>Parses raw MIME once at capture time: summary for the API, pre-rendered IMAP structures, and body text for search.</summary>
public static partial class MessageAnalyzer
{
    private const int SnippetLength = 200;

    public static MessageAnalysis Analyze(ReadOnlyMemory<byte> raw)
    {
        var span = raw.Span;
        var root = MimeStructureParser.Parse(span);
        var imap = new ImapRendering(
            ImapRenderer.RenderEnvelope(span, root),
            ImapRenderer.RenderBodyStructure(span, root, extended: true),
            ImapRenderer.RenderBodyStructure(span, root, extended: false));

        MimeMessage message;
        try
        {
            using var stream = new MemoryStream(raw.ToArray(), writable: false);
            message = MimeMessage.Load(stream);
        }
        catch (FormatException)
        {
            message = new MimeMessage();
        }

        var text = message.TextBody;
        if (string.IsNullOrWhiteSpace(text) && message.HtmlBody is { } html)
        {
            text = HtmlToText(html);
        }

        text ??= "";
        var attachments = message.Attachments.Count();
        var summary = new ParsedSummary(
            message.MessageId,
            message.Subject,
            message.From.Mailboxes.Select(ToAddress).FirstOrDefault(),
            [.. message.To.Mailboxes.Select(ToAddress)],
            [.. message.Cc.Mailboxes.Select(ToAddress)],
            [.. message.Bcc.Mailboxes.Select(ToAddress)],
            [.. message.ReplyTo.Mailboxes.Select(ToAddress)],
            message.Headers.Contains(HeaderId.Date) ? message.Date : null,
            MakeSnippet(text),
            attachments > 0,
            attachments);

        return new MessageAnalysis(summary, imap, text);
    }

    public static EmailAddress ToAddress(MailboxAddress mailbox) =>
        new(string.IsNullOrWhiteSpace(mailbox.Name) ? null : mailbox.Name, mailbox.Address);

    public static string HtmlToText(string html)
    {
        var withoutBlocks = BlockRegex().Replace(html, " ");
        var withBreaks = BreakRegex().Replace(withoutBlocks, "\n");
        var stripped = TagRegex().Replace(withBreaks, " ");
        return WebUtility.HtmlDecode(stripped);
    }

    private static string MakeSnippet(string text)
    {
        var collapsed = WhitespaceRegex().Replace(text, " ").Trim();
        return collapsed.Length <= SnippetLength ? collapsed : collapsed[..SnippetLength].TrimEnd() + "…";
    }

    [GeneratedRegex(@"<(script|style|head)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockRegex();

    [GeneratedRegex(@"<(br|/p|/div|/tr|/h[1-6]|/li)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
