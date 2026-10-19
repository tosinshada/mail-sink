using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mailsink.Core.Accounts;
using Mailsink.Core.Events;
using Mailsink.Core.Storage;

namespace Mailsink.Api;

/// <summary>
/// <c>/hubs/mail</c>. Server events: <c>MessageReceived</c>, <c>MessagesDeleted</c>, <c>MessageUpdated</c>, <c>Stats</c>,
/// <c>AccountsChanged</c>, <c>SmsReceived</c>, <c>SmsChanged</c>, and <c>MailboxChanged</c> for accounts a client subscribed to.
/// </summary>
public sealed class MailHub(IAccountService accounts) : Hub
{
    public Task SubscribeAccount(string address) => Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(accounts.Normalize(address)));

    public Task UnsubscribeAccount(string address) => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupFor(accounts.Normalize(address)));

    internal static string GroupFor(string owner) => "acct:" + owner.ToLowerInvariant();
}

/// <summary>Forwards store events to browsers. Stats are sent once per batch so bulk sends do not flood clients.</summary>
internal sealed class HubEventForwarder(IMailEventBus bus, IMailStore store, IHubContext<MailHub> hub, ILogger<HubEventForwarder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = bus.Subscribe();
        var reader = subscription.Reader;
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken))
            {
                var statsChanged = false;
                var mailboxOwners = new HashSet<(string Owner, long? Mailbox)>();
                var received = 0;
                while (reader.TryRead(out var e))
                {
                    switch (e)
                    {
                        case MessageCapturedEvent captured when captured.Message.InCaptureView:
                            if (received++ < 200)
                            {
                                await hub.Clients.All.SendAsync("MessageReceived", Mapping.ToSummary(captured.Message), stoppingToken);
                            }

                            statsChanged = true;
                            break;
                        case MessagesDeletedEvent deleted:
                            await hub.Clients.All.SendAsync("MessagesDeleted", deleted.Ids.Select(i => i.ToString()).ToList(), stoppingToken);
                            statsChanged = true;
                            break;
                        case MessageUpdatedEvent updated:
                            await hub.Clients.All.SendAsync("MessageUpdated", new MessageUpdatedDto(updated.Id.ToString(), updated.IsRead, updated.Tags), stoppingToken);
                            statsChanged = true;
                            break;
                        case EntryAddedEvent added:
                            mailboxOwners.Add((added.Owner, added.Entry.Mailbox.Value));
                            break;
                        case EntryFlagsChangedEvent changed:
                            mailboxOwners.Add((changed.Owner, changed.Entry.Mailbox.Value));
                            break;
                        case EntriesExpungedEvent expunged:
                            mailboxOwners.Add((expunged.Owner, expunged.Mailbox.Value));
                            break;
                        case MailboxesChangedEvent mailboxes:
                            mailboxOwners.Add((mailboxes.Owner, null));
                            break;
                        case AccountsChangedEvent:
                            await hub.Clients.All.SendAsync("AccountsChanged", stoppingToken);
                            break;
                        case SmsReceivedEvent sms:
                            await hub.Clients.All.SendAsync("SmsReceived", sms.Messages.Select(Endpoints.SmsEndpoints.ToDto).ToList(), stoppingToken);
                            break;
                        case SmsUpdatedEvent:
                        case SmsDeletedEvent:
                            await hub.Clients.All.SendAsync("SmsChanged", stoppingToken);
                            break;
                    }
                }

                if (received > 200)
                {
                    // Too many to push individually; clients refetch.
                    await hub.Clients.All.SendAsync("MessagesBulkReceived", received, stoppingToken);
                }

                foreach (var (owner, mailbox) in mailboxOwners)
                {
                    await hub.Clients.Group(MailHub.GroupFor(owner)).SendAsync("MailboxChanged", new MailboxChangedDto(owner, mailbox), stoppingToken);
                }

                if (statsChanged)
                {
                    var stats = await store.GetCaptureStatsAsync(stoppingToken);
                    await hub.Clients.All.SendAsync("Stats", new StatsDto(stats.Total, stats.Unread), stoppingToken);
                }

                await Task.Delay(50, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SignalR event forwarding stopped");
        }
    }
}
