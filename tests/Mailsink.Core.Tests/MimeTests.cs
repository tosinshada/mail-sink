using System.Text;
using Mailsink.Core.Accounts;
using Mailsink.Core.Mime;

namespace Mailsink.Core.Tests;

public sealed class MimeTests
{
    internal const string Multipart = "From: \"Alice Ä\" <alice@test.local>\r\n" +
                                      "To: bob@test.local, \"Carol\" <carol@test.local>\r\n" +
                                      "Subject: =?utf-8?q?Caf=C3=A9?=\r\n" +
                                      "Date: Fri, 17 Oct 2026 10:00:00 +0000\r\n" +
                                      "Message-ID: <m1@test.local>\r\n" +
                                      "MIME-Version: 1.0\r\n" +
                                      "Content-Type: multipart/mixed; boundary=\"outer\"\r\n" +
                                      "\r\n" +
                                      "preamble\r\n" +
                                      "--outer\r\n" +
                                      "Content-Type: multipart/alternative; boundary=\"inner\"\r\n" +
                                      "\r\n" +
                                      "--inner\r\n" +
                                      "Content-Type: text/plain; charset=utf-8\r\n" +
                                      "\r\n" +
                                      "Hello\r\n" +
                                      "World\r\n" +
                                      "--inner\r\n" +
                                      "Content-Type: text/html; charset=utf-8\r\n" +
                                      "\r\n" +
                                      "<p>Hello</p>\r\n" +
                                      "--inner--\r\n" +
                                      "--outer\r\n" +
                                      "Content-Type: message/rfc822\r\n" +
                                      "\r\n" +
                                      "Subject: inner\r\n" +
                                      "From: x@test.local\r\n" +
                                      "\r\n" +
                                      "nested body\r\n" +
                                      "--outer\r\n" +
                                      "Content-Type: application/pdf; name=\"a.pdf\"\r\n" +
                                      "Content-Disposition: attachment; filename=\"a.pdf\"\r\n" +
                                      "Content-Transfer-Encoding: base64\r\n" +
                                      "\r\n" +
                                      "UERG\r\n" +
                                      "--outer--\r\n" +
                                      "epilogue\r\n";

    [Fact]
    public void Parses_nested_structure_with_exact_offsets()
    {
        var raw = Encoding.UTF8.GetBytes(Multipart);
        var root = MimeStructureParser.Parse(raw);

        Assert.True(root.IsMultipart);
        Assert.Equal(3, root.Children.Count);
        var alternative = root.Children[0];
        Assert.Equal(2, alternative.Children.Count);
        Assert.Equal("Hello\r\nWorld", Encoding.UTF8.GetString(raw[alternative.Children[0].BodyStart..alternative.Children[0].End]));
        Assert.Equal("<p>Hello</p>", Encoding.UTF8.GetString(raw[alternative.Children[1].BodyStart..alternative.Children[1].End]));

        var rfc822 = root.Children[1];
        Assert.True(rfc822.IsMessage);
        Assert.NotNull(rfc822.Message);
        Assert.Equal("nested body", Encoding.UTF8.GetString(raw[rfc822.Message!.BodyStart..rfc822.Message.End]));
        Assert.Equal("UERG", Encoding.UTF8.GetString(raw[root.Children[2].BodyStart..root.Children[2].End]));
    }

    [Fact]
    public void Renders_imap_envelope_and_bodystructure()
    {
        var raw = Encoding.UTF8.GetBytes(Multipart);
        var root = MimeStructureParser.Parse(raw);

        var envelope = ImapRenderer.RenderEnvelope(raw, root);
        Assert.StartsWith("(\"Fri, 17 Oct 2026 10:00:00 +0000\" \"=?utf-8?q?Caf=C3=A9?=\" ((\"Alice =?utf-8?b?w4Q=?=\" NIL \"alice\" \"test.local\"))", envelope);
        Assert.Contains("((NIL NIL \"bob\" \"test.local\")(\"Carol\" NIL \"carol\" \"test.local\"))", envelope);
        Assert.EndsWith("NIL \"<m1@test.local>\")", envelope);

        var body = ImapRenderer.RenderBodyStructure(raw, root, extended: false);
        Assert.Equal(
            "(((\"TEXT\" \"PLAIN\" (\"CHARSET\" \"utf-8\") NIL NIL \"7BIT\" 12 2)(\"TEXT\" \"HTML\" (\"CHARSET\" \"utf-8\") NIL NIL \"7BIT\" 12 1) \"ALTERNATIVE\")" +
            "(\"MESSAGE\" \"RFC822\" NIL NIL NIL \"7BIT\" 49 (NIL \"inner\" ((NIL NIL \"x\" \"test.local\")) ((NIL NIL \"x\" \"test.local\")) ((NIL NIL \"x\" \"test.local\")) NIL NIL NIL NIL NIL) (\"TEXT\" \"PLAIN\" NIL NIL NIL \"7BIT\" 11 1) 4)" +
            "(\"APPLICATION\" \"PDF\" (\"NAME\" \"a.pdf\") NIL NIL \"BASE64\" 4) \"MIXED\")",
            body);

        var extended = ImapRenderer.RenderBodyStructure(raw, root, extended: true);
        Assert.Contains("(\"ATTACHMENT\" (\"FILENAME\" \"a.pdf\"))", extended);
        Assert.EndsWith("\"MIXED\" (\"BOUNDARY\" \"outer\") NIL NIL NIL)", extended);
    }

    [Fact]
    public void Analyzer_produces_summary_and_search_text()
    {
        var analysis = MessageAnalyzer.Analyze(Encoding.UTF8.GetBytes(Multipart));

        Assert.Equal("Café", analysis.Summary.Subject);
        Assert.Equal("alice@test.local", analysis.Summary.From!.Address);
        Assert.Equal(2, analysis.Summary.To.Count);
        Assert.True(analysis.Summary.HasAttachments);
        Assert.Contains("Hello", analysis.BodyText);
        Assert.Equal("m1@test.local", analysis.Summary.MessageIdHeader);
    }

    [Fact]
    public void Single_part_message_without_blank_line_is_all_header()
    {
        var raw = "Subject: only headers\r\n"u8.ToArray();
        var root = MimeStructureParser.Parse(raw);
        Assert.Equal(raw.Length, root.BodyStart);
        Assert.Equal("only headers", root.GetHeader(raw, "subject"));
    }

    [Theory]
    [InlineData("a\nb\n", "a\r\nb\r\n")]
    [InlineData("a\r\nb", "a\r\nb")]
    [InlineData("a\rb", "a\r\nb")]
    public void Normalizes_line_endings(string input, string expected) =>
        Assert.Equal(expected, Encoding.ASCII.GetString(MimeText.NormalizeLineEndings(Encoding.ASCII.GetBytes(input))));

    [Fact]
    public void Password_hashes_verify()
    {
        var hash = PasswordHasher.Hash("s3cret");
        Assert.True(PasswordHasher.Verify("s3cret", hash));
        Assert.False(PasswordHasher.Verify("wrong", hash));
        Assert.NotEqual(hash, PasswordHasher.Hash("s3cret"));
    }
}
