using System.Text.RegularExpressions;
using MimeKit;
using Mailsink.Core.Domain;

namespace Mailsink.Testing;

/// <summary>A captured message with its parsed MIME content, for assertions.</summary>
public sealed partial class ReceivedMessage(CapturedMessage captured, MimeMessage mime)
{
    public CapturedMessage Captured { get; } = captured;

    public MimeMessage Mime { get; } = mime;

    public MessageId Id => Captured.Id;

    public string? Subject => Mime.Subject;

    public string? From => Mime.From.Mailboxes.FirstOrDefault()?.Address;

    public IReadOnlyList<string> To => [.. Mime.To.Mailboxes.Select(m => m.Address)];

    public IReadOnlyList<string> Cc => [.. Mime.Cc.Mailboxes.Select(m => m.Address)];

    /// <summary>Envelope recipients (RCPT TO), including Bcc.</summary>
    public IReadOnlyList<string> Recipients => Captured.Envelope.RcptTo;

    public Envelope Envelope => Captured.Envelope;

    public string? TextBody => Mime.TextBody;

    public string? HtmlBody => Mime.HtmlBody;

    public IReadOnlyList<MimeEntity> Attachments => [.. Mime.Attachments];

    public string? Header(string name) => Mime.Headers[name];

    /// <summary>Links from HTML <c>href</c> attributes and bare URLs in the text body, in order of appearance.</summary>
    public IReadOnlyList<string> ExtractLinks()
    {
        var links = new List<string>();
        if (HtmlBody is { } html)
        {
            links.AddRange(HrefRegex().Matches(html).Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[2].Value)));
        }

        if (TextBody is { } text)
        {
            links.AddRange(UrlRegex().Matches(text).Select(m => m.Value.TrimEnd('.', ',', ')', '>')));
        }

        return [.. links.Distinct()];
    }

    public override string ToString() => $"{Subject} <{From}> → {string.Join(", ", Recipients)}";

    [GeneratedRegex("""href\s*=\s*(["'])(.*?)\1""", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();
}
