using MimeKit;
using Mailsink.Core.Domain;
using Mailsink.Core.Events;
using Mailsink.Core.Mime;
using Mailsink.Core.Search;
using Mailsink.Core.Storage;

namespace Mailsink.Storage.InMemory;

/// <summary>Fast, test-oriented store. One lock guards all state; events are published after the lock is released.</summary>
public sealed class InMemoryMailStore(IBlobStore blobs, IMailEventBus bus, ParsedMessageCache cache) : IMailStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, BoxState> _boxes = [];
    private readonly Dictionary<MessageId, MessageState> _messages = [];
    private readonly Dictionary<MessageId, int> _refs = [];
    private readonly SemaphoreSlim _blobGate = new(1, 1);
    private long _nextAccountId = 1;
    private long _nextMailboxId = 1;

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

    // ---------------------------------------------------------------- accounts

    public Task<Account?> FindAccountAsync(string address, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_accounts.GetValueOrDefault(address));
        }
    }

    public Task<Account> CreateAccountAsync(string address, byte[]? passwordHash, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_accounts.TryGetValue(address, out var account))
            {
                account = new Account(_nextAccountId++, address.ToLowerInvariant(), passwordHash, DateTimeOffset.UtcNow, null, null);
                _accounts[address] = account;
            }

            return Task.FromResult(account);
        }
    }

    public Task<IReadOnlyList<Account>> ListAccountsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Account>>([.. _accounts.Values.OrderBy(a => a.Address, StringComparer.OrdinalIgnoreCase)]);
        }
    }

    public async Task<bool> DeleteAccountAsync(string address, CancellationToken ct)
    {
        List<string> orphanBlobs;
        lock (_gate)
        {
            if (!_accounts.Remove(address))
            {
                return false;
            }

            orphanBlobs = [];
            foreach (var box in _boxes.Values.Where(b => b.Box.Owner.Equals(address, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                RemoveBox(box, orphanBlobs);
            }
        }

        await DeleteBlobsAsync(orphanBlobs, ct);
        bus.Publish(new MailboxesChangedEvent(address));
        bus.Publish(new AccountsChangedEvent());
        return true;
    }

    public Task SetPasswordAsync(string address, byte[]? passwordHash, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_accounts.TryGetValue(address, out var account))
            {
                _accounts[address] = account with { PasswordHash = passwordHash };
            }
        }

        return Task.CompletedTask;
    }

    public Task RecordLoginAsync(string address, string protocol, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_accounts.TryGetValue(address, out var account))
            {
                _accounts[address] = account with { LastLoginAt = DateTimeOffset.UtcNow, LastLoginProtocol = protocol };
            }
        }

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- capture + delivery

    public async Task<CapturedMessage> CaptureAsync(ReadOnlyMemory<byte> rawMime, Envelope envelope, bool inCaptureView, CancellationToken ct)
    {
        var raw = MimeText.NormalizeLineEndings(rawMime.Span);
        var key = StoreHelpers.ComputeBlobKey(raw);
        var analysis = MessageAnalyzer.Analyze(raw);
        var message = new CapturedMessage(MessageId.New(), key, raw.Length, DateTimeOffset.UtcNow, envelope,
            analysis.Summary, analysis.Imap, [], false, inCaptureView);

        // Blob writes and orphan deletes are serialized so a concurrent sweep never removes a blob being re-used.
        await _blobGate.WaitAsync(ct);
        try
        {
            await blobs.PutAsync(key, raw, ct);
            lock (_gate)
            {
                _messages[message.Id] = new MessageState(message, analysis.BodyText);
            }
        }
        finally
        {
            _blobGate.Release();
        }

        bus.Publish(new MessageCapturedEvent(message));
        return message;
    }

    public Task<MailboxEntry> AppendAsync(MailboxId mailbox, MessageId message, SystemFlags flags, IReadOnlyCollection<string> keywords, DateTimeOffset internalDate, CancellationToken ct)
    {
        MailboxEntry entry;
        string owner;
        lock (_gate)
        {
            var box = GetBox(mailbox);
            if (!_messages.TryGetValue(message, out var state))
            {
                throw new MailStoreException($"Message {message} not found");
            }

            var uid = box.Box.UidNext;
            var modseq = box.Box.HighestModSeq + 1;
            entry = new MailboxEntry(mailbox, uid, message, flags & ~SystemFlags.Recent, [.. keywords.Distinct(StringComparer.OrdinalIgnoreCase)],
                internalDate, modseq, state.Message.Size);
            box.Entries[uid] = entry;
            box.Box = box.Box with { UidNext = uid + 1, HighestModSeq = modseq };
            _refs[message] = _refs.GetValueOrDefault(message) + 1;
            owner = box.Box.Owner;
        }

        bus.Publish(new EntryAddedEvent(owner, entry));
        return Task.FromResult(entry);
    }

    // ---------------------------------------------------------------- mailboxes

    public Task<IReadOnlyList<Mailbox>> ListMailboxesAsync(string owner, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Mailbox>>([.. _boxes.Values
                .Where(b => b.Box.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase))
                .Select(b => b.Box)
                .OrderBy(b => StoreHelpers.IsInbox(b.Name) ? 0 : 1)
                .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)]);
        }
    }

    public Task<Mailbox?> GetMailboxAsync(string owner, string name, CancellationToken ct)
    {
        string normalized;
        try
        {
            normalized = StoreHelpers.NormalizeMailboxName(name);
        }
        catch (MailStoreException)
        {
            return Task.FromResult<Mailbox?>(null);
        }

        lock (_gate)
        {
            return Task.FromResult(FindBox(owner, normalized)?.Box);
        }
    }

    public Task<Mailbox?> GetMailboxAsync(MailboxId id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_boxes.GetValueOrDefault(id.Value)?.Box);
        }
    }

    public Task<Mailbox> CreateMailboxAsync(string owner, string name, SpecialUse specialUse, CancellationToken ct)
    {
        var normalized = StoreHelpers.NormalizeMailboxName(name);
        Mailbox mailbox;
        lock (_gate)
        {
            if (FindBox(owner, normalized) is not null)
            {
                throw new MailStoreException("Mailbox already exists");
            }

            mailbox = new Mailbox(new MailboxId(_nextMailboxId++), owner.ToLowerInvariant(), normalized, StoreHelpers.NewUidValidity(), 1, 1, specialUse, true);
            _boxes[mailbox.Id.Value] = new BoxState(mailbox);
        }

        bus.Publish(new MailboxesChangedEvent(mailbox.Owner));
        return Task.FromResult(mailbox);
    }

    public Task RenameMailboxAsync(MailboxId id, string newName, CancellationToken ct)
    {
        var normalized = StoreHelpers.NormalizeMailboxName(newName);
        string owner;
        var events = new List<MailEvent>();
        lock (_gate)
        {
            var box = GetBox(id);
            owner = box.Box.Owner;
            if (FindBox(owner, normalized) is not null)
            {
                throw new MailStoreException("Target mailbox already exists");
            }

            if (StoreHelpers.IsInbox(box.Box.Name))
            {
                // RFC 3501: renaming INBOX moves its messages to the new mailbox and leaves INBOX empty.
                var target = new BoxState(new Mailbox(new MailboxId(_nextMailboxId++), owner, normalized, StoreHelpers.NewUidValidity(), 1, 1, SpecialUse.None, true));
                _boxes[target.Box.Id.Value] = target;
                foreach (var entry in box.Entries.Values)
                {
                    var uid = target.Box.UidNext;
                    var modseq = target.Box.HighestModSeq + 1;
                    target.Entries[uid] = entry with { Mailbox = target.Box.Id, Uid = uid, ModSeq = modseq };
                    target.Box = target.Box with { UidNext = uid + 1, HighestModSeq = modseq };
                }

                events.Add(new EntriesExpungedEvent(owner, box.Box.Id, [.. box.Entries.Keys]));
                box.Entries.Clear();
            }
            else
            {
                var oldName = box.Box.Name;
                foreach (var child in _boxes.Values.Where(b => b.Box.Owner == owner && StoreHelpers.IsChildOf(b.Box.Name, oldName)).ToList())
                {
                    child.Box = child.Box with { Name = normalized + child.Box.Name[oldName.Length..] };
                }

                box.Box = box.Box with { Name = normalized };
            }
        }

        foreach (var e in events)
        {
            bus.Publish(e);
        }

        bus.Publish(new MailboxesChangedEvent(owner));
        return Task.CompletedTask;
    }

    public async Task DeleteMailboxAsync(MailboxId id, CancellationToken ct)
    {
        var orphanBlobs = new List<string>();
        string owner;
        lock (_gate)
        {
            var box = GetBox(id);
            owner = box.Box.Owner;
            if (StoreHelpers.IsInbox(box.Box.Name))
            {
                throw new MailStoreException("INBOX cannot be deleted");
            }

            if (_boxes.Values.Any(b => b.Box.Owner == owner && StoreHelpers.IsChildOf(b.Box.Name, box.Box.Name)))
            {
                throw new MailStoreException("Mailbox has children");
            }

            RemoveBox(box, orphanBlobs);
        }

        await DeleteBlobsAsync(orphanBlobs, ct);
        bus.Publish(new MailboxesChangedEvent(owner));
    }

    public Task SetSubscribedAsync(MailboxId id, bool subscribed, CancellationToken ct)
    {
        string owner;
        lock (_gate)
        {
            var box = GetBox(id);
            box.Box = box.Box with { Subscribed = subscribed };
            owner = box.Box.Owner;
        }

        bus.Publish(new MailboxesChangedEvent(owner));
        return Task.CompletedTask;
    }

    public Task<MailboxStats> GetMailboxStatsAsync(MailboxId id, CancellationToken ct)
    {
        lock (_gate)
        {
            var entries = GetBox(id).Entries.Values;
            return Task.FromResult(new MailboxStats(entries.Count, entries.Count(e => (e.Flags & SystemFlags.Seen) == 0), entries.Sum(e => e.Size)));
        }
    }

    // ---------------------------------------------------------------- entries

    public Task<IReadOnlyList<MailboxEntry>> GetEntriesAsync(MailboxId mailbox, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<MailboxEntry>>([.. GetBox(mailbox).Entries.Values]);
        }
    }

    public Task<IReadOnlyList<MailboxEntry>> StoreFlagsAsync(MailboxId mailbox, IReadOnlyCollection<uint> uids, FlagOperation op, SystemFlags flags, IReadOnlyCollection<string> keywords, CancellationToken ct)
    {
        var result = new List<MailboxEntry>();
        var changed = new List<MailboxEntry>();
        string owner;
        lock (_gate)
        {
            var box = GetBox(mailbox);
            owner = box.Box.Owner;
            foreach (var uid in uids.Order())
            {
                if (!box.Entries.TryGetValue(uid, out var entry))
                {
                    continue;
                }

                var newFlags = StoreHelpers.ApplyFlags(entry.Flags, op, flags & ~SystemFlags.Recent);
                var newKeywords = StoreHelpers.ApplyKeywords(entry.Keywords, op, keywords);
                if (newFlags != entry.Flags || !newKeywords.SequenceEqual(entry.Keywords, StringComparer.OrdinalIgnoreCase))
                {
                    var modseq = box.Box.HighestModSeq + 1;
                    box.Box = box.Box with { HighestModSeq = modseq };
                    entry = entry with { Flags = newFlags, Keywords = newKeywords, ModSeq = modseq };
                    box.Entries[uid] = entry;
                    changed.Add(entry);
                }

                result.Add(entry);
            }
        }

        foreach (var entry in changed)
        {
            bus.Publish(new EntryFlagsChangedEvent(owner, entry));
        }

        return Task.FromResult<IReadOnlyList<MailboxEntry>>(result);
    }

    public Task<CopyResult> CopyAsync(MailboxId from, IReadOnlyCollection<uint> uids, MailboxId to, bool move, CancellationToken ct)
    {
        var sourceUids = new List<uint>();
        var destUids = new List<uint>();
        var added = new List<MailboxEntry>();
        string sourceOwner, destOwner;
        uint validity;
        lock (_gate)
        {
            var source = GetBox(from);
            var dest = GetBox(to);
            sourceOwner = source.Box.Owner;
            destOwner = dest.Box.Owner;
            validity = dest.Box.UidValidity;
            foreach (var uid in uids.Order())
            {
                if (!source.Entries.TryGetValue(uid, out var entry))
                {
                    continue;
                }

                var newUid = dest.Box.UidNext;
                var modseq = dest.Box.HighestModSeq + 1;
                var copy = entry with { Mailbox = dest.Box.Id, Uid = newUid, ModSeq = modseq, Flags = entry.Flags & ~SystemFlags.Recent };
                dest.Entries[newUid] = copy;
                dest.Box = dest.Box with { UidNext = newUid + 1, HighestModSeq = modseq };
                _refs[entry.Message] = _refs.GetValueOrDefault(entry.Message) + 1;
                sourceUids.Add(uid);
                destUids.Add(newUid);
                added.Add(copy);

                if (move)
                {
                    source.Entries.Remove(uid);
                    _refs[entry.Message]--;
                }
            }

            if (move && sourceUids.Count > 0)
            {
                source.Box = source.Box with { HighestModSeq = source.Box.HighestModSeq + 1 };
            }
        }

        foreach (var entry in added)
        {
            bus.Publish(new EntryAddedEvent(destOwner, entry));
        }

        if (move && sourceUids.Count > 0)
        {
            bus.Publish(new EntriesExpungedEvent(sourceOwner, from, sourceUids));
        }

        return Task.FromResult(new CopyResult(validity, sourceUids, destUids));
    }

    public async Task<IReadOnlyList<uint>> ExpungeAsync(MailboxId mailbox, IReadOnlyCollection<uint>? onlyUids, CancellationToken ct)
    {
        var removed = new List<uint>();
        var orphanBlobs = new List<string>();
        string owner;
        lock (_gate)
        {
            var box = GetBox(mailbox);
            owner = box.Box.Owner;
            var only = onlyUids is null ? null : new HashSet<uint>(onlyUids);
            foreach (var entry in box.Entries.Values.Where(e => (e.Flags & SystemFlags.Deleted) != 0 && (only is null || only.Contains(e.Uid))).ToList())
            {
                box.Entries.Remove(entry.Uid);
                removed.Add(entry.Uid);
                Release(entry.Message, orphanBlobs);
            }

            if (removed.Count > 0)
            {
                box.Box = box.Box with { HighestModSeq = box.Box.HighestModSeq + 1 };
            }
        }

        await DeleteBlobsAsync(orphanBlobs, ct);
        if (removed.Count > 0)
        {
            bus.Publish(new EntriesExpungedEvent(owner, mailbox, removed));
        }

        return removed;
    }

    // ---------------------------------------------------------------- content

    public Task<CapturedMessage?> GetMessageAsync(MessageId id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_messages.GetValueOrDefault(id)?.Message);
        }
    }

    public Task<IReadOnlyDictionary<MessageId, CapturedMessage>> GetMessagesAsync(IReadOnlyCollection<MessageId> ids, CancellationToken ct)
    {
        lock (_gate)
        {
            var result = new Dictionary<MessageId, CapturedMessage>();
            foreach (var id in ids)
            {
                if (_messages.TryGetValue(id, out var state))
                {
                    result[id] = state.Message;
                }
            }

            return Task.FromResult<IReadOnlyDictionary<MessageId, CapturedMessage>>(result);
        }
    }

    public async Task<byte[]> ReadRawAsync(MessageId id, CancellationToken ct)
    {
        string key;
        lock (_gate)
        {
            key = _messages.TryGetValue(id, out var state) ? state.Message.BlobKey : throw new MailStoreException($"Message {id} not found");
        }

        return await blobs.GetAsync(key, ct);
    }

    public Task<MimeMessage> LoadParsedAsync(MessageId id, CancellationToken ct) =>
        cache.GetOrLoadAsync(id, () => ReadRawAsync(id, ct), ct);

    // ---------------------------------------------------------------- capture view

    public Task<PagedResult<CapturedMessage>> QueryCaptureAsync(CaptureQuery query, CancellationToken ct)
    {
        lock (_gate)
        {
            var matches = _messages.Values
                .Where(s => s.Message.InCaptureView && CaptureMatcher.Matches(s.Message, s.BodyText, query))
                .Select(s => s.Message)
                .OrderByDescending(m => m.ReceivedAt)
                .ThenByDescending(m => m.Id.Value)
                .ToList();
            var limit = Math.Clamp(query.Limit, 1, 1000);
            var start = Math.Max(0, query.Start);
            return Task.FromResult(new PagedResult<CapturedMessage>([.. matches.Skip(start).Take(limit)], matches.Count, start, limit));
        }
    }

    public Task<CaptureStats> GetCaptureStatsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            var visible = _messages.Values.Where(s => s.Message.InCaptureView).Select(s => s.Message).ToList();
            return Task.FromResult(new CaptureStats(visible.Count, visible.Count(m => !m.IsRead), _messages.Values.Sum(s => s.Message.Size)));
        }
    }

    public Task SetReadAsync(IReadOnlyCollection<MessageId>? ids, bool read, CancellationToken ct)
    {
        var updated = new List<CapturedMessage>();
        lock (_gate)
        {
            var targets = ids is null ? _messages.Keys.ToList() : ids;
            foreach (var id in targets)
            {
                if (_messages.TryGetValue(id, out var state) && state.Message.IsRead != read)
                {
                    state.Message = state.Message with { IsRead = read };
                    updated.Add(state.Message);
                }
            }
        }

        foreach (var m in updated)
        {
            bus.Publish(new MessageUpdatedEvent(m.Id, m.IsRead, m.Tags));
        }

        return Task.CompletedTask;
    }

    public Task SetTagsAsync(MessageId id, IReadOnlyCollection<string> tags, CancellationToken ct)
    {
        CapturedMessage? updated = null;
        lock (_gate)
        {
            if (_messages.TryGetValue(id, out var state))
            {
                var clean = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
                state.Message = state.Message with { Tags = clean };
                updated = state.Message;
            }
        }

        if (updated is null)
        {
            throw new MailStoreException($"Message {id} not found");
        }

        bus.Publish(new MessageUpdatedEvent(updated.Id, updated.IsRead, updated.Tags));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<string>>([.. _messages.Values
                .Where(s => s.Message.InCaptureView)
                .SelectMany(s => s.Message.Tags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)]);
        }
    }

    public async Task DeleteCapturedAsync(IReadOnlyCollection<MessageId>? ids, CancellationToken ct)
    {
        var deleted = new List<MessageId>();
        var orphanBlobs = new List<string>();
        lock (_gate)
        {
            var targets = ids ?? _messages.Values.Where(s => s.Message.InCaptureView).Select(s => s.Message.Id).ToList();
            foreach (var id in targets)
            {
                if (_messages.TryGetValue(id, out var state) && state.Message.InCaptureView)
                {
                    state.Message = state.Message with { InCaptureView = false };
                    deleted.Add(id);
                    CollectIfOrphan(id, orphanBlobs);
                }
            }
        }

        await DeleteBlobsAsync(orphanBlobs, ct);
        if (deleted.Count > 0)
        {
            bus.Publish(new MessagesDeletedEvent(deleted));
        }
    }

    public async Task<int> ApplyRetentionAsync(RetentionPolicy policy, CancellationToken ct)
    {
        var deleted = new List<MessageId>();
        var orphanBlobs = new List<string>();
        var expunged = new List<EntriesExpungedEvent>();
        lock (_gate)
        {
            var visible = _messages.Values.Where(s => s.Message.InCaptureView).Select(s => s.Message).OrderByDescending(m => m.ReceivedAt).ToList();
            var cutoff = policy.MaxAge is { } age ? DateTimeOffset.UtcNow - age : DateTimeOffset.MinValue;
            long total = 0;
            for (var i = 0; i < visible.Count; i++)
            {
                var m = visible[i];
                total += m.Size;
                var expired = (policy.MaxMessages > 0 && i >= policy.MaxMessages)
                              || m.ReceivedAt < cutoff
                              || (policy.MaxTotalSize is { } max && total > max);
                if (!expired)
                {
                    continue;
                }

                deleted.Add(m.Id);
                _messages[m.Id].Message = m with { InCaptureView = false };
                if (policy.AppliesToMailboxes)
                {
                    foreach (var box in _boxes.Values)
                    {
                        var uids = box.Entries.Values.Where(e => e.Message == m.Id).Select(e => e.Uid).ToList();
                        foreach (var uid in uids)
                        {
                            box.Entries.Remove(uid);
                            _refs[m.Id]--;
                        }

                        if (uids.Count > 0)
                        {
                            expunged.Add(new EntriesExpungedEvent(box.Box.Owner, box.Box.Id, uids));
                        }
                    }
                }

                CollectIfOrphan(m.Id, orphanBlobs);
            }
        }

        await DeleteBlobsAsync(orphanBlobs, ct);
        foreach (var e in expunged)
        {
            bus.Publish(e);
        }

        if (deleted.Count > 0)
        {
            bus.Publish(new MessagesDeletedEvent(deleted));
        }

        return deleted.Count;
    }

    public async Task PurgeAsync(CancellationToken ct)
    {
        List<MessageId> ids;
        List<string> owners;
        lock (_gate)
        {
            ids = [.. _messages.Values.Where(s => s.Message.InCaptureView).Select(s => s.Message.Id)];
            owners = [.. _accounts.Keys];
            _messages.Clear();
            _boxes.Clear();
            _accounts.Clear();
            _refs.Clear();
        }

        await blobs.DeleteAllAsync(ct);
        cache.Clear();
        if (ids.Count > 0)
        {
            bus.Publish(new MessagesDeletedEvent(ids));
        }

        foreach (var owner in owners)
        {
            bus.Publish(new MailboxesChangedEvent(owner));
        }

        bus.Publish(new AccountsChangedEvent());
    }

    // ---------------------------------------------------------------- helpers (call under _gate)

    private BoxState GetBox(MailboxId id) =>
        _boxes.TryGetValue(id.Value, out var box) ? box : throw new MailStoreException("Mailbox does not exist");

    private BoxState? FindBox(string owner, string normalizedName) =>
        _boxes.Values.FirstOrDefault(b => b.Box.Owner.Equals(owner, StringComparison.OrdinalIgnoreCase)
                                          && b.Box.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));

    private void RemoveBox(BoxState box, List<string> orphanBlobs)
    {
        _boxes.Remove(box.Box.Id.Value);
        foreach (var entry in box.Entries.Values)
        {
            Release(entry.Message, orphanBlobs);
        }
    }

    private void Release(MessageId message, List<string> orphanBlobs)
    {
        _refs[message] = _refs.GetValueOrDefault(message) - 1;
        CollectIfOrphan(message, orphanBlobs);
    }

    private void CollectIfOrphan(MessageId id, List<string> orphanBlobs)
    {
        if (_refs.GetValueOrDefault(id) > 0 || !_messages.TryGetValue(id, out var state) || state.Message.InCaptureView)
        {
            return;
        }

        _messages.Remove(id);
        _refs.Remove(id);
        cache.Remove(id);
        var key = state.Message.BlobKey;
        if (!_messages.Values.Any(s => s.Message.BlobKey == key))
        {
            orphanBlobs.Add(key);
        }
    }

    private async Task DeleteBlobsAsync(List<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
        {
            return;
        }

        await _blobGate.WaitAsync(ct);
        try
        {
            foreach (var key in keys)
            {
                bool inUse;
                lock (_gate)
                {
                    inUse = _messages.Values.Any(s => s.Message.BlobKey == key);
                }

                if (!inUse)
                {
                    await blobs.DeleteAsync(key, ct);
                }
            }
        }
        finally
        {
            _blobGate.Release();
        }
    }

    private sealed class BoxState(Mailbox box)
    {
        public Mailbox Box { get; set; } = box;
        public SortedDictionary<uint, MailboxEntry> Entries { get; } = [];
    }

    private sealed class MessageState(CapturedMessage message, string bodyText)
    {
        public CapturedMessage Message { get; set; } = message;
        public string BodyText { get; } = bodyText;
    }
}
