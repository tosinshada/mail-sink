using Mailsink.Core.Domain;

namespace Mailsink.Core.Search;

/// <summary>In-memory evaluation of a <see cref="CaptureQuery"/>, used by the in-memory store and the wait API.</summary>
public static class CaptureMatcher
{
    public static bool Matches(CapturedMessage message, string bodyText, CaptureQuery query)
    {
        if (query.Tag is { Length: > 0 } tag && !message.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (query.Unread is { } unread && message.IsRead == unread)
        {
            return false;
        }

        if (query.HasAttachments is { } has && message.Summary.HasAttachments != has)
        {
            return false;
        }

        if (query.Source is { } source && message.Source != source)
        {
            return false;
        }

        if (query.ReceivedAfter is { } after && message.ReceivedAt <= after)
        {
            return false;
        }

        foreach (var term in query.Terms)
        {
            if (MatchesTerm(message, bodyText, term) == term.Negated)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesTerm(CapturedMessage m, string bodyText, SearchTerm term)
    {
        var v = term.Value;
        return term.Field switch
        {
            SearchField.From => Contains(m.Summary.From?.ToString(), v) || Contains(m.Envelope.MailFrom, v),
            SearchField.To => m.Summary.To.Concat(m.Summary.Cc).Concat(m.Summary.Bcc).Any(a => Contains(a.ToString(), v))
                              || m.Envelope.RcptTo.Any(r => Contains(r, v)),
            SearchField.Subject => Contains(m.Summary.Subject, v),
            SearchField.Tag => m.Tags.Any(t => t.Equals(v, StringComparison.OrdinalIgnoreCase)),
            SearchField.IsRead => m.IsRead,
            SearchField.IsUnread => !m.IsRead,
            SearchField.HasAttachment => m.Summary.HasAttachments,
            SearchField.Before => term.DateValue is { } d && m.ReceivedAt < d,
            SearchField.After => term.DateValue is { } d && m.ReceivedAt > d,
            SearchField.Source => term.SourceValue is { } s && m.Source == s,
            SearchField.MessageId => Contains(m.Summary.MessageIdHeader, v),
            _ => Contains(m.Summary.Subject, v)
                 || Contains(m.Summary.From?.ToString(), v)
                 || m.Summary.To.Any(a => Contains(a.ToString(), v))
                 || Contains(bodyText, v),
        };
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
