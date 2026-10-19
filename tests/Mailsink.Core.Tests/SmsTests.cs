using Mailsink.Core.Events;
using Mailsink.Core.Options;
using Mailsink.Core.Sms;

namespace Mailsink.Core.Tests;

public sealed class SmsTests
{
    [Theory]
    [InlineData("+1 (555) 123-4567", "+15551234567")]
    [InlineData("+15551234567", "+15551234567")]
    [InlineData("07700 900123", "07700900123")]
    [InlineData("555.123.4567", "5551234567")]
    [InlineData("  1234567  ", "1234567")]
    public void Normalizes_loosely_formatted_numbers(string input, string expected)
    {
        Assert.True(SmsService.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("123")]
    [InlineData("++15551234567")]
    [InlineData("555-123-4567 ext 2")]
    [InlineData("1234567890123456")]
    [InlineData("+1 555 123 4567+")]
    public void Rejects_invalid_numbers(string? input) => Assert.False(SmsService.TryNormalize(input, out _));

    [Fact]
    public void Missing_sender_parts_use_configured_defaults()
    {
        var options = new MailsinkOptions { Storage = StorageKind.InMemory, Sms = { DefaultSenderName = "Bank", DefaultSenderNumber = "+44 7700 900000" } };
        var service = new SmsService(Microsoft.Extensions.Options.Options.Create(options), new MailEventBus(), TimeProvider.System);

        Assert.Equal(new SmsSender("Bank", "+447700900000"), service.ResolveSender(" ", null));
        Assert.Equal(new SmsSender("Acme", "+447700900000"), service.ResolveSender(" Acme ", null));
        Assert.Equal(new SmsSender("Bank", "+15555550199"), service.ResolveSender(null, "+15555550199"));
    }

    [Fact]
    public void Groups_messages_by_number_and_tracks_unread()
    {
        var bus = new MailEventBus();
        using var subscription = bus.Subscribe();
        var service = new SmsService(Microsoft.Extensions.Options.Options.Create(new MailsinkOptions { Storage = StorageKind.InMemory }), bus, TimeProvider.System);

        service.Send(service.ResolveSender("Acme", "+15555550199"), "first", ["+15551234567", "07700900123"]);
        service.Send(service.ResolveSender(null, null), "second", ["+15551234567"]);

        var phones = service.ListPhones();
        Assert.Equal(2, phones.Count);
        var us = Assert.Single(phones, p => p.Number == "+15551234567");
        Assert.Equal((2, 2, 2, "second", "Mailsink"), (us.Count, us.Unread, us.Senders, us.LastMessage, us.LastFromName));
        Assert.Equal(new SmsStats(2, 3, 3, 2, 3), service.GetStats());

        Assert.Equal(["first", "second"], service.GetThread("+1 (555) 123-4567").Select(m => m.Body));
        Assert.Equal(2, service.MarkRead("+15551234567"));
        Assert.Equal(0, service.ListPhones().Single(p => p.Number == "+15551234567").Unread);

        Assert.True(service.DeletePhone("07700 900123"));
        Assert.Single(service.ListPhones());

        Assert.True(subscription.Reader.TryRead(out var e));
        Assert.Equal(2, Assert.IsType<SmsReceivedEvent>(e).Messages.Count);
    }
}
