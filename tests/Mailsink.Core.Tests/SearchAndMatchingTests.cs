using Mailsink.Core.Chaos;
using Mailsink.Core.Delivery;
using Mailsink.Core.Domain;
using Mailsink.Core.Search;

namespace Mailsink.Core.Tests;

public sealed class SearchAndMatchingTests
{
    [Fact]
    public void Parses_mailpit_style_terms()
    {
        var terms = SearchTerm.Parse("from:alice@x.test -subject:\"weekly report\" is:unread has:attachment tag:urgent hello");

        Assert.Equal(
        [
            new SearchTerm(SearchField.From, "alice@x.test", false),
            new SearchTerm(SearchField.Subject, "weekly report", true),
            new SearchTerm(SearchField.IsUnread, "unread", false),
            new SearchTerm(SearchField.HasAttachment, "attachment", false),
            new SearchTerm(SearchField.Tag, "urgent", false),
            new SearchTerm(SearchField.Text, "hello", false),
        ], terms);
    }

    [Fact]
    public void Unknown_prefix_is_treated_as_free_text()
    {
        var term = Assert.Single(SearchTerm.Parse("https://example.com"));
        Assert.Equal(SearchField.Text, term.Field);
        Assert.Equal("https://example.com", term.Value);
    }

    [Fact]
    public void Matcher_applies_terms_filters_and_negation()
    {
        var message = Message(subject: "Invoice #42", from: "billing@shop.test", tags: ["finance"]);

        Assert.True(CaptureMatcher.Matches(message, "pay now", new CaptureQuery("invoice from:shop.test")));
        Assert.True(CaptureMatcher.Matches(message, "pay now", new CaptureQuery("\"pay now\"")));
        Assert.False(CaptureMatcher.Matches(message, "pay now", new CaptureQuery("-subject:invoice")));
        Assert.True(CaptureMatcher.Matches(message, "", new CaptureQuery(Tag: "FINANCE")));
        Assert.False(CaptureMatcher.Matches(message, "", new CaptureQuery(Unread: false)));
        Assert.True(CaptureMatcher.Matches(message, "", new CaptureQuery("to:bob")));
    }

    [Theory]
    [InlineData("bob+news@x.test", "bob@x.test", "news")]
    [InlineData("bob@x.test", "bob@x.test", null)]
    [InlineData("+tag@x.test", "+tag@x.test", null)]
    [InlineData("bob+@x.test", "bob+@x.test", null)]
    public void Resolves_plus_addressing(string input, string address, string? tag) =>
        Assert.Equal((address, tag), AddressResolver.Resolve(input));

    [Theory]
    [InlineData("*@bounce.test", "a@bounce.test", true)]
    [InlineData("*@bounce.test", "a@other.test", false)]
    [InlineData("qa-?@corp.test", "QA-1@corp.test", true)]
    [InlineData("*", "anything", true)]
    public void Glob_matching(string pattern, string value, bool expected) =>
        Assert.Equal(expected, Glob.IsMatch(pattern, value));

    internal static CapturedMessage Message(string subject = "Hello", string from = "a@test.local", IReadOnlyList<string>? tags = null) => new(
        MessageId.New(),
        "key",
        100,
        DateTimeOffset.UtcNow,
        new Envelope(from, ["bob@test.local"], MessageSource.Smtp),
        new ParsedSummary(null, subject, new EmailAddress(null, from), [new EmailAddress("Bob", "bob@test.local")], [], [], [], null, "", false, 0),
        new ImapRendering("", "", ""),
        tags ?? [],
        false,
        true);
}
