namespace Mailsink.Testing;

public sealed class MailsinkAssertionException(string message) : Exception(message);

/// <summary>Plain assertion helpers (no assertion library needed). Each throws <see cref="MailsinkAssertionException"/> on failure.</summary>
public static class MailsinkAssertions
{
    public static ReceivedMessage ShouldHaveRecipient(this ReceivedMessage message, string address)
    {
        if (!message.Recipients.Concat(message.To).Concat(message.Cc).Contains(address, StringComparer.OrdinalIgnoreCase))
        {
            throw new MailsinkAssertionException($"Expected recipient {address}, but the message went to {string.Join(", ", message.Recipients)}.");
        }

        return message;
    }

    public static ReceivedMessage ShouldHaveSubject(this ReceivedMessage message, string subject)
    {
        if (!string.Equals(message.Subject, subject, StringComparison.Ordinal))
        {
            throw new MailsinkAssertionException($"Expected subject \"{subject}\", but was \"{message.Subject}\".");
        }

        return message;
    }

    public static ReceivedMessage ShouldBeFrom(this ReceivedMessage message, string address)
    {
        if (!string.Equals(message.From, address, StringComparison.OrdinalIgnoreCase))
        {
            throw new MailsinkAssertionException($"Expected sender {address}, but was {message.From}.");
        }

        return message;
    }

    public static ReceivedMessage ShouldHaveAttachment(this ReceivedMessage message, string fileName)
    {
        var names = message.Attachments.Select(a => (a as MimeKit.MimePart)?.FileName ?? a.ContentDisposition?.FileName).ToList();
        if (!names.Contains(fileName, StringComparer.OrdinalIgnoreCase))
        {
            throw new MailsinkAssertionException($"Expected attachment {fileName}; attachments: {(names.Count == 0 ? "(none)" : string.Join(", ", names))}.");
        }

        return message;
    }

    public static ReceivedMessage ShouldContainText(this ReceivedMessage message, string text)
    {
        var bodies = (message.TextBody ?? "") + "\n" + (message.HtmlBody ?? "");
        if (!bodies.Contains(text, StringComparison.Ordinal))
        {
            throw new MailsinkAssertionException($"Expected the body to contain \"{text}\".");
        }

        return message;
    }

    public static ReceivedMessage ShouldHaveHeader(this ReceivedMessage message, string name, string? value = null)
    {
        var actual = message.Header(name);
        if (actual is null || (value is not null && !string.Equals(actual, value, StringComparison.Ordinal)))
        {
            throw new MailsinkAssertionException(value is null
                ? $"Expected header {name}."
                : $"Expected header {name}: {value}, but was {actual ?? "(missing)"}.");
        }

        return message;
    }

    /// <summary>Asserts that the HTML contains a link equal to, or starting with, <paramref name="urlOrPrefix"/>. Returns the first match.</summary>
    public static string ShouldContainLink(this string? html, string urlOrPrefix)
    {
        if (html is null)
        {
            throw new MailsinkAssertionException("Expected an HTML body, but there is none.");
        }

        var links = System.Text.RegularExpressions.Regex.Matches(html, """href\s*=\s*(["'])(.*?)\1""", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[2].Value))
            .ToList();
        return links.FirstOrDefault(l => l.StartsWith(urlOrPrefix, StringComparison.OrdinalIgnoreCase))
               ?? throw new MailsinkAssertionException($"Expected a link starting with {urlOrPrefix}; links: {(links.Count == 0 ? "(none)" : string.Join(", ", links))}.");
    }
}
