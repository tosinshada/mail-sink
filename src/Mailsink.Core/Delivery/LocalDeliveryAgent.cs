using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mailsink.Core.Accounts;
using Mailsink.Core.Domain;
using Mailsink.Core.Options;
using Mailsink.Core.Storage;

namespace Mailsink.Core.Delivery;

public sealed record DeliveryResult(IReadOnlyList<string> Delivered, IReadOnlyList<string> Undeliverable);

/// <summary>Splits <c>bob+tag@x</c> into the account address and the tag.</summary>
public static class AddressResolver
{
    public static (string Address, string? Tag) Resolve(string address)
    {
        var at = address.LastIndexOf('@');
        if (at <= 0)
        {
            return (address, null);
        }

        var local = address[..at];
        var plus = local.IndexOf('+');
        if (plus <= 0 || plus == local.Length - 1)
        {
            return (address, null);
        }

        return (local[..plus] + address[at..], local[(plus + 1)..]);
    }
}

/// <summary>Resolves envelope recipients to per-user INBOXes and files the sender's Sent copy.</summary>
public sealed class LocalDeliveryAgent(
    IMailStore store,
    IAccountService accounts,
    IOptionsMonitor<MailsinkOptions> options,
    ILogger<LocalDeliveryAgent> logger)
{
    public async Task<DeliveryResult> DeliverAsync(CapturedMessage message, Envelope envelope, bool saveToSent, CancellationToken ct)
    {
        var smtp = options.CurrentValue.Smtp;
        var delivered = new List<string>();
        var undeliverable = new List<string>();
        var targets = new HashSet<MailboxId>();
        var now = DateTimeOffset.UtcNow;

        foreach (var recipient in envelope.RcptTo.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (address, tag) = AddressResolver.Resolve(accounts.Normalize(recipient));
            var account = await store.FindAccountAsync(address, ct);
            if (account is null && smtp.AutoCreateRecipients)
            {
                account = await accounts.GetOrCreateAsync(address, ct);
            }

            if (account is null)
            {
                undeliverable.Add(recipient);
                continue;
            }

            Mailbox? mailbox = null;
            if (smtp.PlusAddressFolders && tag is not null)
            {
                mailbox = await store.GetMailboxAsync(account.Address, tag, ct)
                          ?? await store.CreateMailboxAsync(account.Address, tag, SpecialUse.None, ct);
            }

            mailbox ??= await store.GetMailboxAsync(account.Address, "INBOX", ct)
                        ?? await store.CreateMailboxAsync(account.Address, "INBOX", SpecialUse.None, ct);

            if (targets.Add(mailbox.Id))
            {
                await store.AppendAsync(mailbox.Id, message.Id, SystemFlags.None, [], now, ct);
            }

            delivered.Add(recipient);
        }

        if (saveToSent && envelope.AuthUser is { Length: > 0 } sender)
        {
            var account = await accounts.GetOrCreateAsync(sender, ct);
            var mailboxes = await store.ListMailboxesAsync(account.Address, ct);
            var sent = mailboxes.FirstOrDefault(m => m.SpecialUse == SpecialUse.Sent)
                       ?? await store.CreateMailboxAsync(account.Address, "Sent", SpecialUse.Sent, ct);
            if (targets.Add(sent.Id))
            {
                await store.AppendAsync(sent.Id, message.Id, SystemFlags.Seen, [], now, ct);
            }
        }

        if (undeliverable.Count > 0)
        {
            logger.LogDebug("Message {Id}: no local mailbox for {Recipients}", message.Id, string.Join(", ", undeliverable));
        }

        return new DeliveryResult(delivered, undeliverable);
    }
}
