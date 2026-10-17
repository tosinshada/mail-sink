using System.Threading.Channels;
using Mailsink.Core.Domain;

namespace Mailsink.Core.Events;

public abstract record MailEvent;

/// <summary>A message was added to the capture log.</summary>
public sealed record MessageCapturedEvent(CapturedMessage Message) : MailEvent;

public sealed record MessagesDeletedEvent(IReadOnlyList<MessageId> Ids) : MailEvent;

public sealed record MessageUpdatedEvent(MessageId Id, bool IsRead, IReadOnlyList<string> Tags) : MailEvent;

public sealed record EntryAddedEvent(string Owner, MailboxEntry Entry) : MailEvent;

public sealed record EntryFlagsChangedEvent(string Owner, MailboxEntry Entry) : MailEvent;

public sealed record EntriesExpungedEvent(string Owner, MailboxId Mailbox, IReadOnlyList<uint> Uids) : MailEvent;

/// <summary>A mailbox was created, renamed, deleted or its subscription changed.</summary>
public sealed record MailboxesChangedEvent(string Owner) : MailEvent;

public sealed record AccountsChangedEvent : MailEvent;

/// <summary>In-process pub/sub for store changes. Every subscriber sees every event.</summary>
public interface IMailEventBus
{
    void Publish(MailEvent mailEvent);

    IMailEventSubscription Subscribe();
}

public interface IMailEventSubscription : IDisposable
{
    ChannelReader<MailEvent> Reader { get; }
}

public sealed class MailEventBus : IMailEventBus
{
    private readonly Lock _gate = new();
    private Subscription[] _subscribers = [];

    public void Publish(MailEvent mailEvent)
    {
        foreach (var subscriber in Volatile.Read(ref _subscribers))
        {
            subscriber.Channel.Writer.TryWrite(mailEvent);
        }
    }

    public IMailEventSubscription Subscribe()
    {
        var subscription = new Subscription(this);
        lock (_gate)
        {
            _subscribers = [.. _subscribers, subscription];
        }

        return subscription;
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            _subscribers = [.. _subscribers.Where(s => s != subscription)];
        }

        subscription.Channel.Writer.TryComplete();
    }

    private sealed class Subscription(MailEventBus bus) : IMailEventSubscription
    {
        private int _disposed;

        public Channel<MailEvent> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<MailEvent>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

        public ChannelReader<MailEvent> Reader => Channel.Reader;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                bus.Remove(this);
            }
        }
    }
}
