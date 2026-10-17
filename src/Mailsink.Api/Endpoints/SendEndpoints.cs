using System.Text;
using System.Text.Json;
using Bogus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using MimeKit;
using Mailsink.Core.Delivery;
using Mailsink.Core.Domain;
using Mailsink.Core.Options;
using Mailsink.Core.Storage;
using Mailsink.Core.Templates;

namespace Mailsink.Api.Endpoints;

internal static class SendEndpoints
{
    private const int MaxRepeat = 10_000;

    public static void Map(RouteGroupBuilder api)
    {
        var send = api.MapGroup("/send").WithTags("Send");
        send.MapPost("", SendAsync)
            .WithSummary("Compose and send a message")
            .WithDescription("Delivers locally like SMTP submission: capture view, each recipient's INBOX, and the sender's Sent folder. `templateData` renders Scriban placeholders such as {{name}} in subject, text and HTML.");
        send.MapPost("/raw", SendRawAsync)
            .WithSummary("Send a raw .eml body (message/rfc822)")
            .Accepts<string>("message/rfc822", "text/plain");
        send.MapPost("/bulk", SendBulkAsync).WithSummary("Generate load-test traffic with optional randomized content");
    }

    private static async Task<IResult> SendAsync(SendRequest request, HttpContext http, SubmissionService submission, TemplateService templates,
        IMailStore store, IOptions<MailsinkOptions> options, CancellationToken ct)
    {
        if (request.From is null || string.IsNullOrWhiteSpace(request.From.Email))
        {
            return Results.BadRequest(new ErrorResponse("from.email is required"));
        }

        if ((request.To?.Count ?? 0) + (request.Cc?.Count ?? 0) + (request.Bcc?.Count ?? 0) == 0)
        {
            return Results.BadRequest(new ErrorResponse("At least one recipient (to, cc or bcc) is required"));
        }

        var subject = request.Subject;
        var text = request.Text;
        var html = request.Html;
        if (!string.IsNullOrEmpty(request.Template))
        {
            var template = Guid.TryParse(request.Template, out var tid) ? templates.Find(tid) : templates.FindByName(request.Template);
            if (template is null)
            {
                return Results.BadRequest(new ErrorResponse($"Template '{request.Template}' not found"));
            }

            subject ??= template.Subject;
            text ??= template.Text;
            html ??= template.Html;
        }

        try
        {
            subject = TemplateService.Render(subject, request.TemplateData);
            text = TemplateService.Render(text, request.TemplateData);
            html = TemplateService.Render(html, request.TemplateData);
        }
        catch (Exception ex) when (ex is FormatException or Scriban.Syntax.ScriptRuntimeException)
        {
            return Results.BadRequest(new ErrorResponse("Template error: " + ex.Message));
        }

        var repeat = Math.Clamp(request.Repeat, 1, MaxRepeat);
        var source = http.Request.Headers["X-Mailsink-Source"] == "ui" ? MessageSource.Ui : MessageSource.Api;
        var ids = new List<string>();
        string? messageIdHeader = null;
        for (var i = 0; i < repeat; i++)
        {
            MimeMessage message;
            try
            {
                message = Build(request, subject, text, html);
            }
            catch (ParseException ex)
            {
                return Results.BadRequest(new ErrorResponse("Invalid address: " + ex.Message));
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }

            var envelope = SubmissionService.EnvelopeFromHeaders(message, source, request.From.Email);
            var result = await submission.SubmitAsync(message, envelope, new SubmissionOptions(source), ct);
            if (request.Tags is { Count: > 0 } tags)
            {
                await store.SetTagsAsync(result.Message.Id, tags, ct);
            }

            ids.Add(result.Message.Id.ToString());
            messageIdHeader ??= result.Message.Summary.MessageIdHeader;
        }

        return Results.Created($"/api/v1/messages/{ids[0]}", new SendResponse(ids, messageIdHeader is null ? null : $"<{messageIdHeader}>"));
    }

    private static async Task<IResult> SendRawAsync(HttpRequest request, SubmissionService submission, string? from, string? to, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length == 0)
        {
            return Results.BadRequest(new ErrorResponse("Request body must be a raw RFC 5322 message"));
        }

        MimeMessage parsed;
        try
        {
            buffer.Position = 0;
            parsed = await MimeMessage.LoadAsync(buffer, ct);
        }
        catch (FormatException ex)
        {
            return Results.BadRequest(new ErrorResponse("Unparseable message: " + ex.Message));
        }

        var envelope = SubmissionService.EnvelopeFromHeaders(parsed, MessageSource.Api, null);
        if (!string.IsNullOrWhiteSpace(from))
        {
            envelope = envelope with { MailFrom = from, AuthUser = from };
        }

        if (!string.IsNullOrWhiteSpace(to))
        {
            envelope = envelope with { RcptTo = to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) };
        }

        if (envelope.RcptTo.Count == 0)
        {
            return Results.BadRequest(new ErrorResponse("No recipients: add To/Cc headers or the ?to= query parameter"));
        }

        var result = await submission.SubmitRawAsync(buffer.ToArray(), envelope, new SubmissionOptions(MessageSource.Api), ct);
        return Results.Created($"/api/v1/messages/{result.Message.Id}",
            new SendResponse([result.Message.Id.ToString()], result.Message.Summary.MessageIdHeader));
    }

    private static async Task<IResult> SendBulkAsync(BulkSendRequest request, SubmissionService submission, IOptions<MailsinkOptions> options, CancellationToken ct)
    {
        var count = Math.Clamp(request.Count, 1, MaxRepeat);
        var faker = new Faker();
        var domain = options.Value.DefaultDomain;
        var ids = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var from = request.From ?? (request.Random
                ? new AddressDto(faker.Name.FullName(), faker.Internet.Email(provider: domain).ToLowerInvariant())
                : new AddressDto("Load Test", $"loadtest@{domain}"));
            var to = request.To is { Count: > 0 } explicitTo
                ? explicitTo.Select(t => new AddressDto(null, t)).ToList()
                : [new AddressDto(faker.Name.FullName(), faker.Internet.Email(provider: domain).ToLowerInvariant())];

            var subject = request.Subject ?? (request.Random ? faker.Company.CatchPhrase() : $"Load test message {i + 1}");
            var text = request.Text ?? (request.Random ? faker.Lorem.Paragraphs(3) : $"Message {i + 1} of {count}");
            var html = request.Html ?? (request.Random
                ? $"<h2>{System.Net.WebUtility.HtmlEncode(subject)}</h2>" + string.Concat(faker.Lorem.Paragraphs(3, "\n").Split('\n').Select(p => $"<p>{System.Net.WebUtility.HtmlEncode(p)}</p>"))
                : null);

            subject = TemplateService.Render(subject, request.TemplateData);
            text = TemplateService.Render(text, request.TemplateData);
            html = TemplateService.Render(html, request.TemplateData);

            var attachments = request.WithAttachments
                ? new List<AttachmentInput> { new("report.txt", "text/plain", Convert.ToBase64String(Encoding.UTF8.GetBytes(faker.Lorem.Paragraphs(2)))) }
                : null;
            var send = new SendRequest(from, to, null, null, null, subject, text, html, new Dictionary<string, string> { ["X-Mailsink-Bulk"] = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) },
                attachments, null, null, null);
            var message = Build(send, subject, text, html);
            var envelope = SubmissionService.EnvelopeFromHeaders(message, MessageSource.Api, from.Email);
            var result = await submission.SubmitAsync(message, envelope, new SubmissionOptions(MessageSource.Api), ct);
            ids.Add(result.Message.Id.ToString());
        }

        return Results.Created("/api/v1/messages", new SendResponse(ids, null));
    }

    internal static MimeMessage Build(SendRequest request, string? subject, string? text, string? html)
    {
        var message = new MimeMessage();
        var from = ToMailbox(request.From!);
        message.From.Add(from);

        // MimeMessage defaults the Message-ID to the machine name; use the sender's domain instead.
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(string.IsNullOrEmpty(from.Domain) ? "mailsink" : from.Domain);
        AddAll(message.To, request.To);
        AddAll(message.Cc, request.Cc);
        AddAll(message.Bcc, request.Bcc);
        AddAll(message.ReplyTo, request.ReplyTo);
        message.Subject = subject ?? "";

        foreach (var (name, value) in request.Headers ?? [])
        {
            if (name.Equals("Message-ID", StringComparison.OrdinalIgnoreCase))
            {
                message.MessageId = value.Trim('<', '>');
            }
            else
            {
                message.Headers.Add(name, value);
            }
        }

        var builder = new BodyBuilder { TextBody = text, HtmlBody = html };
        foreach (var attachment in request.Attachments ?? [])
        {
            var data = Convert.FromBase64String(attachment.ContentBase64);
            if (attachment.ContentType is { Length: > 0 } type)
            {
                builder.Attachments.Add(attachment.Filename, data, ContentType.Parse(type));
            }
            else
            {
                builder.Attachments.Add(attachment.Filename, data);
            }
        }

        foreach (var inline in request.Inline ?? [])
        {
            var data = Convert.FromBase64String(inline.ContentBase64);
            var resource = inline.ContentType is { Length: > 0 } type
                ? builder.LinkedResources.Add(inline.Filename ?? inline.ContentId, data, ContentType.Parse(type))
                : builder.LinkedResources.Add(inline.Filename ?? inline.ContentId, data);
            resource.ContentId = inline.ContentId;
        }

        message.Body = builder.ToMessageBody();
        return message;
    }

    private static void AddAll(InternetAddressList list, IReadOnlyList<AddressDto>? addresses)
    {
        foreach (var address in addresses ?? [])
        {
            if (!string.IsNullOrWhiteSpace(address.Email))
            {
                list.Add(ToMailbox(address));
            }
        }
    }

    private static MailboxAddress ToMailbox(AddressDto address) => new(address.Name ?? "", address.Email.Trim());
}
