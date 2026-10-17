using MimeKit;
using MimeKit.Utils;
using Mailsink.Core.Domain;
using Mailsink.Core.Storage;

namespace Mailsink.Core.Delivery;

public sealed record SubmissionOptions(MessageSource Source, bool SaveToSent = true, bool InCaptureView = true);

public sealed record SubmissionResult(CapturedMessage Message, DeliveryResult Delivery);

/// <summary>The one path every local send goes through: capture, then local delivery. Nothing leaves the process.</summary>
public sealed class SubmissionService(IMailStore store, LocalDeliveryAgent delivery)
{
    public async Task<SubmissionResult> SubmitAsync(MimeMessage message, Envelope? envelope, SubmissionOptions options, CancellationToken ct)
    {
        EnsureHeaders(message);
        envelope ??= EnvelopeFromHeaders(message, options.Source, null);

        // Bcc travels in the envelope only, as a real MTA would deliver it.
        message.Bcc.Clear();
        using var buffer = new MemoryStream();
        await message.WriteToAsync(FormatOptions.Default, buffer, ct);
        return await SubmitRawAsync(buffer.ToArray(), envelope, options, ct);
    }

    public async Task<SubmissionResult> SubmitRawAsync(byte[] raw, Envelope envelope, SubmissionOptions options, CancellationToken ct)
    {
        var captured = await store.CaptureAsync(raw, envelope, options.InCaptureView, ct);
        var result = await delivery.DeliverAsync(captured, envelope, options.SaveToSent, ct);
        return new SubmissionResult(captured, result);
    }

    public static Envelope EnvelopeFromHeaders(MimeMessage message, MessageSource source, string? authUser)
    {
        var from = message.Sender?.Address ?? message.From.Mailboxes.FirstOrDefault()?.Address;
        var rcpt = message.To.Mailboxes.Concat(message.Cc.Mailboxes).Concat(message.Bcc.Mailboxes)
            .Select(m => m.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new Envelope(from, rcpt, source, AuthUser: authUser ?? from);
    }

    public static void EnsureHeaders(MimeMessage message)
    {
        if (!message.Headers.Contains(HeaderId.Date))
        {
            message.Date = DateTimeOffset.Now;
        }

        if (string.IsNullOrEmpty(message.MessageId))
        {
            var domain = message.From.Mailboxes.FirstOrDefault()?.Domain;
            message.MessageId = MimeUtils.GenerateMessageId(string.IsNullOrEmpty(domain) ? "mailsink" : domain);
        }

        if (!message.Headers.Contains(HeaderId.MimeVersion))
        {
            message.MimeVersion = new Version(1, 0);
        }
    }
}
