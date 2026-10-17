using MimeKit;
using Mailsink.Core.Domain;

namespace Mailsink.Api;

internal static class Mapping
{
    public static MessageSummaryDto ToSummary(CapturedMessage m) => new(
        m.Id.ToString(),
        m.Summary.MessageIdHeader,
        m.Summary.Subject,
        m.Summary.From is { } from ? AddressDto.From(from) : null,
        [.. m.Summary.To.Select(AddressDto.From)],
        [.. m.Summary.Cc.Select(AddressDto.From)],
        [.. m.Summary.Bcc.Select(AddressDto.From)],
        m.Summary.Date,
        m.ReceivedAt,
        m.Size,
        m.Summary.Snippet,
        m.IsRead,
        m.Tags,
        m.Summary.HasAttachments,
        m.Summary.AttachmentCount,
        m.Source.ToString(),
        m.Envelope.RcptTo);

    public static EnvelopeDto ToEnvelope(Envelope e) =>
        new(e.MailFrom, e.RcptTo, e.Helo, e.ClientIp, e.Secure, e.AuthUser, e.SessionId, e.Source.ToString());

    public static IReadOnlyList<string> FlagNamesOf(MailboxEntry entry) =>
        [.. FlagNames.ToNames(entry.Flags), .. entry.Keywords];

    public static MailboxDto ToMailbox(Mailbox m, MailboxStats stats) =>
        new(m.Id.Value, m.Name, m.SpecialUse == SpecialUse.None ? null : m.SpecialUse.ToString(), stats.Messages, stats.Unseen, stats.Size,
            m.UidValidity, m.UidNext, m.Subscribed);

    /// <summary>Attachments and inline parts, numbered by their position in <see cref="MimeMessage.BodyParts"/>.</summary>
    public static IReadOnlyList<AttachmentDto> Attachments(MimeMessage message)
    {
        var bodies = new HashSet<MimeEntity>();
        if (message.HtmlBody is not null)
        {
            bodies.UnionWith(message.BodyParts.OfType<TextPart>().Where(p => p.IsHtml && !p.IsAttachment).Take(1));
        }

        if (message.TextBody is not null)
        {
            bodies.UnionWith(message.BodyParts.OfType<TextPart>().Where(p => p.IsPlain && !p.IsAttachment).Take(1));
        }

        var result = new List<AttachmentDto>();
        var index = 0;
        foreach (var entity in message.BodyParts)
        {
            var partId = index++.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (bodies.Contains(entity) || entity is not MimePart part)
            {
                if (entity is MessagePart attachedMessage)
                {
                    result.Add(new AttachmentDto(partId, attachedMessage.ContentDisposition?.FileName ?? "message.eml", "message/rfc822", 0, null, false));
                }

                continue;
            }

            result.Add(new AttachmentDto(
                partId,
                part.FileName,
                part.ContentType.MimeType,
                DecodedLength(part),
                part.ContentId,
                !part.IsAttachment && part.ContentId is not null));
        }

        return result;
    }

    private static long DecodedLength(MimePart part)
    {
        if (part.Content is null)
        {
            return 0;
        }

        using var counter = new MimeKit.IO.MeasuringStream();
        part.Content.DecodeTo(counter);
        return counter.Length;
    }
}
