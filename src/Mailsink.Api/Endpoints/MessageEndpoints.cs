using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MimeKit;
using Mailsink.Core.Diagnostics;
using Mailsink.Core.Domain;
using Mailsink.Core.Events;
using Mailsink.Core.Search;
using Mailsink.Core.Storage;

namespace Mailsink.Api.Endpoints;

internal static class MessageEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var messages = api.MapGroup("/messages").WithTags("Messages");

        messages.MapGet("", ListAsync)
            .WithSummary("List or search captured messages")
            .WithDescription("Search syntax: from: to: subject: is:unread is:read has:attachment tag: before: after: source: message-id: and free text. Prefix a term with - to negate it.");
        messages.MapGet("/export", ExportAsync).WithSummary("Export matching messages as a .zip of .eml files or as JSON");
        messages.MapGet("/{id}", GetAsync).WithSummary("Parsed message: addresses, text, HTML, attachments, envelope");
        messages.MapGet("/{id}/raw", RawAsync).WithSummary("Original message source (message/rfc822)");
        messages.MapGet("/{id}/headers", HeadersAsync).WithSummary("All headers in order, duplicates kept");
        messages.MapGet("/{id}/html", HtmlAsync).WithSummary("Sanitized HTML for the preview frame (?remote=true loads remote images)");
        messages.MapGet("/{id}/parts/{partId}", PartAsync).WithSummary("Download an attachment or inline part");
        messages.MapGet("/{id}/source-transcript", TranscriptAsync).WithSummary("Protocol transcript of the session that delivered the message");
        messages.MapPut("/read", (IdsRequest? request, IMailStore store, CancellationToken ct) => SetReadAsync(request, true, store, ct)).WithSummary("Mark messages read (all when no ids)");
        messages.MapPut("/unread", (IdsRequest? request, IMailStore store, CancellationToken ct) => SetReadAsync(request, false, store, ct)).WithSummary("Mark messages unread (all when no ids)");
        messages.MapPut("/{id}/tags", SetTagsAsync).WithSummary("Replace a message's tags");
        messages.MapDelete("", DeleteAsync).WithSummary("Delete messages by id (body { ids }), or all when no ids are given");
        messages.MapDelete("/{id}", DeleteOneAsync).WithSummary("Delete one message");

        api.MapGet("/tags", async (IMailStore store, CancellationToken ct) => Results.Ok(await store.ListTagsAsync(ct))).WithTags("Messages").WithSummary("All tags in use");
        api.MapGet("/wait", WaitAsync).WithTags("Messages")
            .WithSummary("Long-poll until messages matching a query arrive")
            .WithDescription("Returns as soon as at least `count` messages match `query` (optionally only those received after `since`). Responds 408 on timeout. Timeout accepts 30s, 1500ms, 2m or plain seconds; max 5 minutes.");
    }

    private static async Task<IResult> ListAsync(IMailStore store, string? query, int? start, int? limit, string? tag, bool? unread,
        bool? attachments, string? source, CancellationToken ct)
    {
        var q = new CaptureQuery(query, start ?? 0, limit ?? 50, tag, unread, attachments, ParseSource(source));
        var page = await store.QueryCaptureAsync(q, ct);
        var stats = await store.GetCaptureStatsAsync(ct);
        var tags = await store.ListTagsAsync(ct);
        return Results.Ok(new MessageListResponse(page.Total, stats.Unread, page.Start, page.Limit, [.. page.Items.Select(Mapping.ToSummary)], tags));
    }

    private static async Task<IResult> GetAsync(string id, IMailStore store, CancellationToken ct)
    {
        if (!MessageId.TryParse(id, out var messageId) || await store.GetMessageAsync(messageId, ct) is not { } message)
        {
            return NotFound();
        }

        var parsed = await store.LoadParsedAsync(messageId, ct);
        return Results.Ok(new MessageDetailDto(
            Mapping.ToSummary(message),
            [.. message.Summary.ReplyTo.Select(AddressDto.From)],
            Mapping.ToEnvelope(message.Envelope),
            parsed.TextBody,
            parsed.HtmlBody,
            Mapping.Attachments(parsed)));
    }

    private static async Task<IResult> RawAsync(string id, bool? download, IMailStore store, CancellationToken ct)
    {
        if (!MessageId.TryParse(id, out var messageId) || await store.GetMessageAsync(messageId, ct) is null)
        {
            return NotFound();
        }

        var raw = await store.ReadRawAsync(messageId, ct);
        return download == true
            ? Results.File(raw, "message/rfc822", $"{messageId}.eml")
            : Results.Bytes(raw, "text/plain; charset=utf-8");
    }

    private static async Task<IResult> HeadersAsync(string id, IMailStore store, CancellationToken ct)
    {
        if (!MessageId.TryParse(id, out var messageId) || await store.GetMessageAsync(messageId, ct) is null)
        {
            return NotFound();
        }

        var parsed = await store.LoadParsedAsync(messageId, ct);
        return Results.Ok(parsed.Headers.Select(h => new HeaderDto(h.Field, h.Value)).ToList());
    }

    private static async Task<IResult> HtmlAsync(string id, bool? remote, HttpContext http, IMailStore store, CancellationToken ct)
    {
        if (!MessageId.TryParse(id, out var messageId) || await store.GetMessageAsync(messageId, ct) is null)
        {
            return NotFound();
        }

        var parsed = await store.LoadParsedAsync(messageId, ct);
        var allowRemote = remote == true;
        http.Response.Headers.ContentSecurityPolicy = HtmlPreview.ContentSecurityPolicy(allowRemote);
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Results.Content(HtmlPreview.Render(parsed, "parts/", allowRemote), "text/html; charset=utf-8");
    }

    private static async Task<IResult> PartAsync(string id, int partId, bool? download, IMailStore store, CancellationToken ct)
    {
        if (!MessageId.TryParse(id, out var messageId) || await store.GetMessageAsync(messageId, ct) is null)
        {
            return NotFound();
        }

        var parsed = await store.LoadParsedAsync(messageId, ct);
        var entity = parsed.BodyParts.ElementAtOrDefault(partId);
        switch (entity)
        {
            case MimePart { Content: not null } part:
            {
                var buffer = new MemoryStream();
                await part.Content.DecodeToAsync(buffer, ct);
                var name = part.FileName ?? $"part-{partId}";
                return download == true || part.IsAttachment
                    ? Results.File(buffer.ToArray(), part.ContentType.MimeType, name)
                    : Results.Bytes(buffer.ToArray(), part.ContentType.MimeType);
            }

            case MessagePart { Message: not null } attached:
            {
                var buffer = new MemoryStream();
                await attached.Message.WriteToAsync(buffer, ct);
                return Results.File(buffer.ToArray(), "message/rfc822", "attached.eml");
            }

            default:
                return NotFound();
        }
    }

    private static async Task<IResult> TranscriptAsync(string id, IMailStore store, TranscriptStore transcripts, CancellationToken ct)
    {
        if (!MessageId.TryParse(id, out var messageId) || await store.GetMessageAsync(messageId, ct) is not { } message)
        {
            return NotFound();
        }

        if (message.Envelope.SessionId is not { } sessionId || transcripts.Find(sessionId) is not { } transcript)
        {
            return Results.NotFound(new ErrorResponse("No transcript is available for this message (API sends have none; old sessions are evicted)."));
        }

        return Results.Ok(AdminEndpoints.ToDto(transcript));
    }

    private static async Task<IResult> SetReadAsync(IdsRequest? request, bool read, IMailStore store, CancellationToken ct)
    {
        await store.SetReadAsync(ParseIds(request?.Ids), read, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SetTagsAsync(string id, TagsRequest request, IMailStore store, CancellationToken ct)
    {
        if (!MessageId.TryParse(id, out var messageId) || await store.GetMessageAsync(messageId, ct) is null)
        {
            return NotFound();
        }

        await store.SetTagsAsync(messageId, request.Tags, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(HttpRequest request, IMailStore store, CancellationToken ct)
    {
        IdsRequest? body = null;
        if (request.ContentLength is > 0 || request.Headers.TransferEncoding.Count > 0)
        {
            body = await JsonSerializer.DeserializeAsync(request.Body, ApiJsonContext.Default.IdsRequest, ct);
        }

        await store.DeleteCapturedAsync(ParseIds(body?.Ids), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteOneAsync(string id, IMailStore store, CancellationToken ct)
    {
        if (!MessageId.TryParse(id, out var messageId))
        {
            return NotFound();
        }

        await store.DeleteCapturedAsync([messageId], ct);
        return Results.NoContent();
    }

    private static async Task ExportAsync(HttpContext http, IMailStore store, string? query, string? format, string? ids, CancellationToken ct)
    {
        var selected = ParseIds(ids?.Split(',', StringSplitOptions.RemoveEmptyEntries));
        List<CapturedMessage> items;
        if (selected is not null)
        {
            items = [.. (await store.GetMessagesAsync(selected, ct)).Values.OrderByDescending(m => m.ReceivedAt)];
        }
        else
        {
            items = [.. (await store.QueryCaptureAsync(new CaptureQuery(query, 0, 1000), ct)).Items];
        }

        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            http.Response.ContentType = "application/json";
            http.Response.Headers.ContentDisposition = "attachment; filename=\"mailsink-export.json\"";
            await JsonSerializer.SerializeAsync(http.Response.Body, items.Select(Mapping.ToSummary).ToList(),
                ApiJsonContext.Default.Options, ct);
            return;
        }

        http.Response.ContentType = "application/zip";
        http.Response.Headers.ContentDisposition = "attachment; filename=\"mailsink-export.zip\"";
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var message in items)
            {
                var name = $"{message.ReceivedAt:yyyyMMdd-HHmmss}-{Slug(message.Summary.Subject)}-{message.Id.Value.ToString("N")[..8]}.eml";
                var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                await using var stream = await entry.OpenAsync(ct);
                await stream.WriteAsync(await store.ReadRawAsync(message.Id, ct), ct);
            }
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(http.Response.Body, ct);
    }

    private static async Task<IResult> WaitAsync(IMailStore store, IMailEventBus bus, string? query, int? count, string? timeout,
        DateTimeOffset? since, CancellationToken ct)
    {
        var wanted = Math.Clamp(count ?? 1, 1, 1000);
        var limit = ParseTimeout(timeout);
        var q = new CaptureQuery(query, 0, wanted, ReceivedAfter: since);
        using var subscription = bus.Subscribe();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(limit);
        try
        {
            while (true)
            {
                var page = await store.QueryCaptureAsync(q, cts.Token);
                if (page.Total >= wanted)
                {
                    return Results.Ok(new WaitResponse(page.Total, [.. page.Items.Select(Mapping.ToSummary)]));
                }

                while (true)
                {
                    await subscription.Reader.WaitToReadAsync(cts.Token);
                    var captured = false;
                    while (subscription.Reader.TryRead(out var e))
                    {
                        captured |= e is MessageCapturedEvent or MessageUpdatedEvent;
                    }

                    if (captured)
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var page = await store.QueryCaptureAsync(q, ct);
            return Results.Json(new WaitResponse(page.Total, [.. page.Items.Select(Mapping.ToSummary)]), ApiJsonContext.Default.WaitResponse,
                statusCode: StatusCodes.Status408RequestTimeout);
        }
    }

    internal static TimeSpan ParseTimeout(string? text)
    {
        var max = TimeSpan.FromMinutes(5);
        if (string.IsNullOrWhiteSpace(text))
        {
            return TimeSpan.FromSeconds(30);
        }

        text = text.Trim().ToLowerInvariant();
        TimeSpan value;
        if (text.EndsWith("ms", StringComparison.Ordinal) && double.TryParse(text[..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
        {
            value = TimeSpan.FromMilliseconds(ms);
        }
        else if (text.EndsWith('s') && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s))
        {
            value = TimeSpan.FromSeconds(s);
        }
        else if (text.EndsWith('m') && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var m))
        {
            value = TimeSpan.FromMinutes(m);
        }
        else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
        {
            value = TimeSpan.FromSeconds(plain);
        }
        else
        {
            value = TimeSpan.FromSeconds(30);
        }

        return value <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : value > max ? max : value;
    }

    internal static MessageSource? ParseSource(string? source) => source?.ToLowerInvariant() switch
    {
        "smtp" => MessageSource.Smtp,
        "submission" => MessageSource.Submission,
        "imap" or "imapappend" => MessageSource.ImapAppend,
        "api" => MessageSource.Api,
        "ui" => MessageSource.Ui,
        _ => null,
    };

    internal static IReadOnlyCollection<MessageId>? ParseIds(IEnumerable<string>? ids)
    {
        // No ids (null or empty) means "all"; ids that do not parse are ignored, so a bad list never widens to "all".
        var raw = ids?.ToList();
        if (raw is null || raw.Count == 0)
        {
            return null;
        }

        return [.. raw.Select(i => MessageId.TryParse(i, out var id) ? id : (MessageId?)null).Where(i => i is not null).Select(i => i!.Value)];
    }

    internal static IResult NotFound() => Results.NotFound(new ErrorResponse("Message not found"));

    private static string Slug(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return "no-subject";
        }

        var sb = new StringBuilder();
        foreach (var c in subject)
        {
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
            if (sb.Length >= 40)
            {
                break;
            }
        }

        return sb.ToString().Trim('-');
    }
}
