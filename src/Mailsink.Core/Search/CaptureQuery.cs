using System.Globalization;
using System.Text;
using Mailsink.Core.Domain;

namespace Mailsink.Core.Search;

public sealed record CaptureQuery(
    string? Search = null,
    int Start = 0,
    int Limit = 50,
    string? Tag = null,
    bool? Unread = null,
    bool? HasAttachments = null,
    MessageSource? Source = null,
    DateTimeOffset? ReceivedAfter = null)
{
    public IReadOnlyList<SearchTerm> Terms { get; } = SearchTerm.Parse(Search);
}

public enum SearchField
{
    Text,
    From,
    To,
    Subject,
    Tag,
    IsRead,
    IsUnread,
    HasAttachment,
    Before,
    After,
    Source,
    MessageId,
}

/// <summary>
/// One term of the Mailpit-style search syntax: <c>from:</c>, <c>to:</c>, <c>subject:</c>, <c>is:unread</c>,
/// <c>has:attachment</c>, <c>tag:</c>, <c>before:</c>, <c>after:</c>, <c>source:</c>, <c>message-id:</c>, free text.
/// A leading <c>-</c> or <c>!</c> negates the term. Values may be double-quoted.
/// </summary>
public sealed record SearchTerm(SearchField Field, string Value, bool Negated)
{
    public DateTimeOffset? DateValue =>
        DateTimeOffset.TryParse(Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public MessageSource? SourceValue => Value.ToLowerInvariant() switch
    {
        "smtp" => MessageSource.Smtp,
        "submission" => MessageSource.Submission,
        "imap" or "imapappend" => MessageSource.ImapAppend,
        "api" => MessageSource.Api,
        "ui" => MessageSource.Ui,
        _ => null,
    };

    public static IReadOnlyList<SearchTerm> Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var terms = new List<SearchTerm>();
        foreach (var token in Tokenize(query))
        {
            var negated = false;
            var text = token;
            if (text.Length > 1 && (text[0] == '-' || text[0] == '!'))
            {
                negated = true;
                text = text[1..];
            }

            var colon = text.IndexOf(':');
            if (colon > 0 && !text.StartsWith('"'))
            {
                var key = text[..colon].ToLowerInvariant();
                var value = Unquote(text[(colon + 1)..]);
                SearchTerm? term = key switch
                {
                    "from" => new(SearchField.From, value, negated),
                    "to" => new(SearchField.To, value, negated),
                    "subject" => new(SearchField.Subject, value, negated),
                    "tag" => new(SearchField.Tag, value, negated),
                    "is" when value.Equals("unread", StringComparison.OrdinalIgnoreCase) => new(SearchField.IsUnread, value, negated),
                    "is" when value.Equals("read", StringComparison.OrdinalIgnoreCase) => new(SearchField.IsRead, value, negated),
                    "has" when value.StartsWith("attachment", StringComparison.OrdinalIgnoreCase) => new(SearchField.HasAttachment, value, negated),
                    "before" => new(SearchField.Before, value, negated),
                    "after" => new(SearchField.After, value, negated),
                    "source" => new(SearchField.Source, value, negated),
                    "message-id" or "messageid" => new(SearchField.MessageId, value.Trim('<', '>'), negated),
                    _ => null,
                };

                if (term is not null)
                {
                    if (term.Value.Length > 0)
                    {
                        terms.Add(term);
                    }

                    continue;
                }
            }

            var free = Unquote(text);
            if (free.Length > 0)
            {
                terms.Add(new SearchTerm(SearchField.Text, free, negated));
            }
        }

        return terms;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value.Trim('"');

    private static IEnumerable<string> Tokenize(string query)
    {
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in query)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
