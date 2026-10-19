using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mailsink.Core.Sms;

namespace Mailsink.Api.Endpoints;

internal static class SmsEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var sms = api.MapGroup("/sms").WithTags("SMS");
        sms.MapPost("", Send)
            .WithSummary("Send a text message to one or more phone numbers")
            .WithDescription("Captures one message per recipient; nothing reaches a carrier. `from.name` and `from.number` are optional and default to Mailsink:Sms:DefaultSenderName / DefaultSenderNumber. Numbers are validated loosely: spaces, dashes, dots and parentheses are ignored, one leading + is allowed, and 7–15 digits are required. If any number is invalid the whole request is rejected.");
        sms.MapGet("/stats", (SmsService service) =>
            {
                var s = service.GetStats();
                return Results.Ok(new SmsStatsDto(s.Phones, s.Messages, s.Unread, s.Senders, s.Last24Hours));
            })
            .WithSummary("Totals across every phone: phones, messages, unread, distinct senders, messages in the last 24 hours");
        sms.MapGet("/phones", (SmsService service) => Results.Ok(service.ListPhones().Select(ToDto).ToList()))
            .WithSummary("Phone numbers that have received messages, most recent first");
        sms.MapGet("/phones/{number}", (string number, SmsService service) => Results.Ok(service.GetThread(number).Select(ToDto).ToList()))
            .WithSummary("Messages received by a number, oldest first");
        sms.MapPost("/phones/{number}/read", (string number, SmsService service) =>
        {
            service.MarkRead(number);
            return Results.NoContent();
        }).WithSummary("Mark a number's messages as read");
        sms.MapDelete("/phones/{number}", (string number, SmsService service) =>
                service.DeletePhone(number) ? Results.NoContent() : Results.NotFound(new ErrorResponse("No messages for that number")))
            .WithSummary("Delete a number's messages");
        sms.MapDelete("", (SmsService service) =>
        {
            service.DeleteAll();
            return Results.NoContent();
        }).WithSummary("Delete all text messages");
    }

    private static IResult Send(SmsSendRequest request, SmsService service)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Results.BadRequest(new ErrorResponse("message is required"));
        }

        if (request.Message.Length > SmsService.MaxBodyLength)
        {
            return Results.BadRequest(new ErrorResponse($"message must be at most {SmsService.MaxBodyLength} characters"));
        }

        if (request.To is not { Count: > 0 })
        {
            return Results.BadRequest(new ErrorResponse("At least one phone number in 'to' is required"));
        }

        var recipients = new List<string>();
        var invalid = new List<string>();
        foreach (var number in request.To)
        {
            if (!SmsService.TryNormalize(number, out var normalized))
            {
                invalid.Add(number ?? "");
            }
            else if (!recipients.Contains(normalized))
            {
                recipients.Add(normalized);
            }
        }

        if (invalid.Count > 0)
        {
            return Results.BadRequest(new ErrorResponse("Invalid phone number(s): " + string.Join(", ", invalid.Select(n => $"'{n}'"))));
        }

        string? fromNumber = null;
        if (!string.IsNullOrWhiteSpace(request.From?.Number) && !SmsService.TryNormalize(request.From.Number, out fromNumber))
        {
            return Results.BadRequest(new ErrorResponse($"Invalid sender phone number: '{request.From.Number}'"));
        }

        var sender = service.ResolveSender(request.From?.Name, fromNumber);
        var created = service.Send(sender, request.Message, recipients);
        return Results.Created("/api/v1/sms/phones", new SmsSendResponse([.. created.Select(m => m.Id.ToString())], recipients, new SmsSenderDto(sender.Name, sender.Number)));
    }

    internal static SmsMessageDto ToDto(SmsMessage m) => new(m.Id.ToString(), m.To, new SmsSenderDto(m.FromName, m.FromNumber), m.Body, m.ReceivedAt, m.IsRead);

    private static SmsPhoneDto ToDto(SmsPhone p) =>
        new(p.Number, p.Count, p.Unread, p.Senders, p.LastMessage, p.LastFromName, p.FirstReceivedAt, p.LastReceivedAt);
}
