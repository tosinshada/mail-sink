using System.Collections.Concurrent;
using Mailsink.Core.Domain;

namespace Mailsink.Imap.Session;

/// <summary>
/// A session's snapshot of its selected mailbox. Message sequence numbers (MSNs) are positions in
/// <see cref="Uids"/> (+1) and only change at safe points, when the session tells the client.
/// </summary>
internal sealed class SelectedMailboxView
{
    private readonly List<uint> _uids;
    private readonly Dictionary<uint, MailboxEntry> _entries;
    private readonly HashSet<uint> _recent;

    public SelectedMailboxView(Mailbox mailbox, IReadOnlyList<MailboxEntry> entries, bool readOnly, uint recentAbove)
    {
        Mailbox = mailbox;
        ReadOnly = readOnly;
        _uids = [.. entries.Select(e => e.Uid).Order()];
        _entries = entries.ToDictionary(e => e.Uid);
        _recent = [.. _uids.Where(u => u > recentAbove)];
    }

    public Mailbox Mailbox { get; private set; }

    public MailboxId Id => Mailbox.Id;

    public bool ReadOnly { get; }

    public int Count => _uids.Count;

    public int RecentCount => _recent.Count;

    public IReadOnlyList<uint> Uids => _uids;

    public uint MaxUid => _uids.Count == 0 ? 0 : _uids[^1];

    public bool Contains(uint uid) => _entries.ContainsKey(uid);

    public MailboxEntry Entry(uint uid) => _entries[uid];

    public bool TryGetEntry(uint uid, out MailboxEntry entry) => _entries.TryGetValue(uid, out entry!);

    public bool IsRecent(uint uid) => _recent.Contains(uid);

    public uint UidAt(int msn) => _uids[msn - 1];

    public int MsnOf(uint uid)
    {
        var index = _uids.BinarySearch(uid);
        return index < 0 ? 0 : index + 1;
    }

    public IReadOnlyList<int> AllMsns => [.. Enumerable.Range(1, _uids.Count)];

    public void Add(MailboxEntry entry, bool recent)
    {
        if (_entries.ContainsKey(entry.Uid))
        {
            return;
        }

        _entries[entry.Uid] = entry;
        var index = _uids.BinarySearch(entry.Uid);
        _uids.Insert(~index, entry.Uid);
        if (recent)
        {
            _recent.Add(entry.Uid);
        }
    }

    /// <summary>Records the latest known state. Returns true when flags or keywords differ from what the client knows.</summary>
    public bool Update(MailboxEntry entry)
    {
        if (!_entries.TryGetValue(entry.Uid, out var known))
        {
            return false;
        }

        _entries[entry.Uid] = entry;
        return known.Flags != entry.Flags || !known.Keywords.SequenceEqual(entry.Keywords, StringComparer.OrdinalIgnoreCase);
    }

    public void Remove(uint uid)
    {
        if (_entries.Remove(uid))
        {
            _uids.Remove(uid);
            _recent.Remove(uid);
        }
    }

    public void SetMailbox(Mailbox mailbox) => Mailbox = mailbox;

    public IEnumerable<string> Keywords => _entries.Values.SelectMany(e => e.Keywords).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Tracks the highest UID any session has seen per mailbox, so <c>\Recent</c> is reported to only one session
/// (the first to select the mailbox read-write after the message arrived).
/// </summary>
internal sealed class RecentTracker
{
    private readonly ConcurrentDictionary<long, uint> _highest = new();

    /// <summary>Claims everything below <paramref name="uidNext"/>; returns the previous high-water mark.</summary>
    public uint ClaimUpTo(MailboxId mailbox, uint uidNext)
    {
        var claimed = uidNext == 0 ? 0 : uidNext - 1;
        uint previous = 0;
        _highest.AddOrUpdate(mailbox.Value, _ => claimed, (_, old) =>
        {
            previous = old;
            return Math.Max(old, claimed);
        });
        return previous;
    }

    /// <summary>Returns true if this caller is the first to claim <paramref name="uid"/>.</summary>
    public bool Claim(MailboxId mailbox, uint uid)
    {
        while (true)
        {
            var current = _highest.GetOrAdd(mailbox.Value, 0);
            if (uid <= current)
            {
                return false;
            }

            if (_highest.TryUpdate(mailbox.Value, uid, current))
            {
                return true;
            }
        }
    }
}
