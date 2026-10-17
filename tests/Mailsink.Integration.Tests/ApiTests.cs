using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.SignalR.Client;
using MimeKit;
using Mailsink.Testing;
using Mailsink.Testing.Xunit;

namespace Mailsink.Integration.Tests;

public sealed class HttpMailsinkFixture : MailsinkFixture
{
    protected override bool EnableHttp => true;
}

public sealed class ApiTests(HttpMailsinkFixture fixture) : IClassFixture<HttpMailsinkFixture>, IAsyncLifetime
{
    private readonly MailsinkServer _mail = fixture.Server;
    private readonly HttpClient _http = new() { BaseAddress = fixture.Server.ApiBaseUri };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _mail.ResetAsync(Ct);

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<JsonElement> GetJsonAsync(string path)
    {
        using var response = await _http.GetAsync(path, Ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    [Fact]
    public async Task Send_api_renders_templates_and_delivers_locally()
    {
        using var response = await _http.PostAsJsonAsync("api/v1/send", new
        {
            from = new { name = "Shop", email = "shop@test.local" },
            to = new[] { new { email = "dave@test.local" } },
            bcc = new[] { new { email = "audit@test.local" } },
            subject = "Welcome {{name}}",
            html = "<p>Hi <b>{{name}}</b> <img src=\"cid:logo\"></p>",
            text = "Hi {{name}}",
            headers = new Dictionary<string, string> { ["X-Test-Run"] = "42" },
            attachments = new[] { new { filename = "a.txt", contentType = "text/plain", contentBase64 = Convert.ToBase64String("hello"u8.ToArray()) } },
            inline = new[] { new { contentId = "logo", contentType = "image/png", contentBase64 = Convert.ToBase64String([137, 80, 78, 71]) } },
            templateData = new { name = "Dave" },
            tags = new[] { "signup" },
        }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = created.GetProperty("ids")[0].GetString();

        var detail = await GetJsonAsync($"api/v1/messages/{id}");
        Assert.Equal("Welcome Dave", detail.GetProperty("summary").GetProperty("subject").GetString());
        Assert.Equal(["dave@test.local", "audit@test.local"], detail.GetProperty("envelope").GetProperty("rcptTo").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("signup", detail.GetProperty("summary").GetProperty("tags")[0].GetString());
        Assert.Contains(detail.GetProperty("attachments").EnumerateArray(), a => a.GetProperty("fileName").GetString() == "a.txt");

        var headers = await GetJsonAsync($"api/v1/messages/{id}/headers");
        Assert.Contains(headers.EnumerateArray(), h => h.GetProperty("name").GetString() == "X-Test-Run" && h.GetProperty("value").GetString() == "42");

        var mailboxes = await GetJsonAsync("api/v1/accounts/dave%40test.local/mailboxes");
        Assert.Equal(1, mailboxes.EnumerateArray().First(m => m.GetProperty("name").GetString() == "INBOX").GetProperty("messages").GetInt32());
        var sent = await GetJsonAsync("api/v1/accounts/shop%40test.local/mailboxes/Sent/messages");
        Assert.Equal(1, sent.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Html_preview_is_sanitized_and_rewrites_cid_and_blocks_remote_images()
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse("x@test.local"));
        message.To.Add(MailboxAddress.Parse("y@test.local"));
        message.Subject = "html";
        var builder = new BodyBuilder
        {
            HtmlBody = "<html><head><style>p{color:red}</style></head><body><script>alert(1)</script><p onclick=\"x()\">Hi</p>" +
                       "<img src=\"cid:pic\"><img src=\"https://tracker.test/p.gif\"></body></html>",
        };
        builder.LinkedResources.Add("pic.png", new byte[] { 1, 2, 3 }).ContentId = "pic";
        message.Body = builder.ToMessageBody();
        var sent = await _mail.SendAsync(message, Ct);

        using var response = await _http.GetAsync($"api/v1/messages/{sent.Id}/html", Ct);
        var html = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("onclick", html);
        Assert.Contains("<style>", html);
        Assert.Contains("src=\"parts/", html);
        Assert.Contains("data-blocked-src=\"https://tracker.test/p.gif\"", html);
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());

        var remote = await _http.GetStringAsync($"api/v1/messages/{sent.Id}/html?remote=true", Ct);
        Assert.Contains("src=\"https://tracker.test/p.gif\"", remote);
    }

    [Fact]
    public async Task List_search_read_tags_and_delete()
    {
        await SmtpSendAsync("Order shipped", "orders@shop.test");
        await SmtpSendAsync("Weekly digest", "news@site.test");

        var all = await GetJsonAsync("api/v1/messages");
        Assert.Equal(2, all.GetProperty("total").GetInt32());
        Assert.Equal(2, all.GetProperty("unread").GetInt32());

        var found = await GetJsonAsync("api/v1/messages?query=" + Uri.EscapeDataString("from:shop.test is:unread"));
        var id = Assert.Single(found.GetProperty("messages").EnumerateArray()).GetProperty("id").GetString();

        (await _http.PutAsJsonAsync("api/v1/messages/read", new { ids = new[] { id } }, Ct)).EnsureSuccessStatusCode();
        (await _http.PutAsJsonAsync($"api/v1/messages/{id}/tags", new { tags = new[] { "shipping" } }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(1, (await GetJsonAsync("api/v1/messages?query=tag:shipping")).GetProperty("total").GetInt32());
        Assert.Equal(1, (await GetJsonAsync("api/v1/messages")).GetProperty("unread").GetInt32());

        var raw = await _http.GetStringAsync($"api/v1/messages/{id}/raw", Ct);
        Assert.Contains("Subject: Order shipped", raw);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "api/v1/messages") { Content = JsonContent.Create(new { ids = new[] { id } }) };
        (await _http.SendAsync(delete, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(1, (await GetJsonAsync("api/v1/messages")).GetProperty("total").GetInt32());

        (await _http.DeleteAsync("api/v1/messages", Ct)).EnsureSuccessStatusCode();
        Assert.Equal(0, (await GetJsonAsync("api/v1/messages")).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Wait_endpoint_returns_when_mail_arrives_and_408_on_timeout()
    {
        var wait = _http.GetAsync("api/v1/wait?query=subject:token&timeout=10s", Ct);
        await Task.Delay(200, Ct);
        await SmtpSendAsync("Your token", "auth@test.local");

        using var response = await wait;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("Your token", body.GetProperty("messages")[0].GetProperty("subject").GetString());

        using var timeout = await _http.GetAsync("api/v1/wait?query=subject:never&timeout=300ms", Ct);
        Assert.Equal(HttpStatusCode.RequestTimeout, timeout.StatusCode);
    }

    [Fact]
    public async Task SignalR_pushes_new_messages()
    {
        await using var hub = new HubConnectionBuilder().WithUrl(new Uri(_mail.ApiBaseUri!, "hubs/mail")).Build();
        var received = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JsonElement>("MessageReceived", m => received.TrySetResult(m));
        await hub.StartAsync(Ct);

        await SmtpSendAsync("Live!", "live@test.local");

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("Live!", message.GetProperty("subject").GetString());
    }

    [Fact]
    public async Task Info_chaos_accounts_and_openapi()
    {
        var info = await GetJsonAsync("api/v1/info");
        Assert.Contains(info.GetProperty("endpoints").EnumerateArray(), e => e.GetProperty("name").GetString() == "imap" && e.GetProperty("port").GetInt32() == _mail.ImapPort);

        var rules = new { enabled = true, rules = new[] { new { stage = "Connect", action = "Drop" } } };
        using var put = await _http.PutAsJsonAsync("api/v1/chaos", rules, Ct);
        put.EnsureSuccessStatusCode();
        using (var smtp = new SmtpClient())
        {
            await Assert.ThrowsAnyAsync<Exception>(() => smtp.ConnectAsync(_mail.Host, _mail.SmtpPort, SecureSocketOptions.None, Ct));
        }

        (await _http.PutAsJsonAsync("api/v1/chaos", new { enabled = true, rules = Array.Empty<object>() }, Ct)).EnsureSuccessStatusCode();

        (await _http.PostAsJsonAsync("api/v1/accounts", new { address = "erin@test.local", password = "pw" }, Ct)).EnsureSuccessStatusCode();
        var accounts = await GetJsonAsync("api/v1/accounts");
        Assert.Contains(accounts.EnumerateArray(), a => a.GetProperty("address").GetString() == "erin@test.local" && a.GetProperty("hasPassword").GetBoolean());

        var openApi = await GetJsonAsync("api/openapi/v1.json");
        Assert.True(openApi.GetProperty("paths").TryGetProperty("/api/v1/messages", out _));
    }

    [Fact]
    public async Task Raw_send_is_captured_and_delivered_locally()
    {
        var eml = "From: a@test.local\r\nTo: b@test.local\r\nSubject: raw one\r\n\r\nbody\r\n";
        using var response = await _http.PostAsync("api/v1/send/raw", new StringContent(eml, Encoding.UTF8, "message/rfc822"), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("ids")[0].GetString();

        var detail = await GetJsonAsync($"api/v1/messages/{id}");
        Assert.Equal("raw one", detail.GetProperty("summary").GetProperty("subject").GetString());
        Assert.Equal(["b@test.local"], detail.GetProperty("envelope").GetProperty("rcptTo").EnumerateArray().Select(e => e.GetString()));
    }

    private async Task SmtpSendAsync(string subject, string from)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse("bob@test.local"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = "body of " + subject };
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(_mail.Host, _mail.SmtpPort, SecureSocketOptions.None, Ct);
        await smtp.SendAsync(message, Ct);
        await smtp.DisconnectAsync(true, Ct);
    }
}
