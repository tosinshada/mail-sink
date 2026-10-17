using System.Text;
using Mailsink.Imap.Fetch;
using Mailsink.Imap.Protocol;

namespace Mailsink.Imap.Tests;

public sealed class ParserTests
{
    private static ImapCommand Parse(string text) => ImapParser.Parse(Encoding.UTF8.GetBytes(text + "\r\n"));

    [Fact]
    public void Parses_tag_command_and_astrings()
    {
        var c = Parse("a1 LOGIN \"bob@test.local\" \"p\\\"w\"");
        Assert.Equal("a1", c.Tag);
        Assert.Equal("LOGIN", c.Name);
        Assert.Equal("bob@test.local", c.Arg(0));
        Assert.Equal("p\"w", c.Arg(1));
    }

    [Fact]
    public void Parses_uid_prefix_and_bracketed_fetch_items()
    {
        var c = Parse("a4 UID FETCH 1:* (FLAGS BODY.PEEK[HEADER.FIELDS (SUBJECT FROM)]<0.2048> BODY[1.2.MIME])");
        Assert.True(c.IsUid);
        Assert.Equal("FETCH", c.Name);
        var list = Assert.IsType<ImapList>(c.Args[1]);
        Assert.Equal(["FLAGS", "BODY.PEEK[HEADER.FIELDS (SUBJECT FROM)]<0.2048>", "BODY[1.2.MIME]"], list.Items.Select(i => i.AsString()));
    }

    [Fact]
    public void Parses_literals_inline()
    {
        var buffer = Encoding.ASCII.GetBytes("a5 APPEND Drafts (\\Draft) {11}\r\nhello world\r\n");
        var c = ImapParser.Parse(buffer);
        Assert.Equal("Drafts", c.Arg(0));
        Assert.IsType<ImapList>(c.Args[1]);
        Assert.Equal("hello world", Assert.IsType<ImapLiteral>(c.Args[2]).AsString());
    }

    [Theory]
    [InlineData("a {5}", 5L, true)]
    [InlineData("a {5+}", 5L, false)]
    [InlineData("a ~{12}", 12L, true)]
    public void Detects_trailing_literal(string line, long length, bool sync)
    {
        Assert.True(ImapParser.TryGetTrailingLiteral(Encoding.ASCII.GetBytes(line), out var n, out var s));
        Assert.Equal(length, n);
        Assert.Equal(sync, s);
    }

    [Fact]
    public void Rejects_unterminated_quote_with_tag()
    {
        var ex = Assert.Throws<ImapParseException>(() => Parse("x1 SELECT \"INBOX"));
        Assert.Equal("x1", ex.Tag);
    }

    [Theory]
    [InlineData("Entwürfe", "Entw&APw-rfe")]
    [InlineData("A&B", "A&-B")]
    [InlineData("日本語", "&ZeVnLIqe-")]
    [InlineData("plain", "plain")]
    public void Modified_utf7_round_trips(string utf8, string wire)
    {
        Assert.Equal(wire, ModifiedUtf7.Encode(utf8));
        Assert.Equal(utf8, ModifiedUtf7.Decode(wire));
    }

    [Theory]
    [InlineData("1:3,5", new uint[] { 1, 2, 3, 4, 5, 6 }, new uint[] { 1, 2, 3, 5 })]
    [InlineData("4:*", new uint[] { 1, 2, 5, 9 }, new uint[] { 5, 9 })]
    [InlineData("100:*", new uint[] { 1, 2, 5, 9 }, new uint[] { 9 })]
    [InlineData("*", new uint[] { 3, 7 }, new uint[] { 7 })]
    [InlineData("3:1", new uint[] { 1, 2, 3 }, new uint[] { 1, 2, 3 })]
    public void Sequence_sets_resolve(string set, uint[] available, uint[] expected) =>
        Assert.Equal(expected, SequenceSet.Parse(set).Resolve(available));

    [Fact]
    public void Sequence_set_formats_compactly() =>
        Assert.Equal("1:3,5,7:8", SequenceSet.Format([8, 1, 2, 3, 5, 7]));

    [Fact]
    public void Fetch_items_parse_sections_and_partials()
    {
        var item = FetchItemParser.ParseOne("BODY.PEEK[1.2.HEADER.FIELDS.NOT (Received \"X-Spam\")]<10.20>");
        Assert.True(item.Peek);
        Assert.Equal([1, 2], item.Section!.Path);
        Assert.Equal(SectionText.HeaderFieldsNot, item.Section.Text);
        Assert.Equal(["RECEIVED", "X-SPAM"], item.Section.Fields);
        Assert.Equal(10, item.PartialOffset);
        Assert.Equal(20, item.PartialLength);
        Assert.Equal("1.2.HEADER.FIELDS.NOT (RECEIVED X-SPAM)", item.Section.Format());
    }

    [Fact]
    public void Fetch_macros_expand()
    {
        var items = FetchItemParser.Parse(new ImapAtom("FULL"));
        Assert.Equal([FetchKind.Flags, FetchKind.InternalDate, FetchKind.Rfc822Size, FetchKind.Envelope, FetchKind.Body], items.Select(i => i.Kind));
    }
}
