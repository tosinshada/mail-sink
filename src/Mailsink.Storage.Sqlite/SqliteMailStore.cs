using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using MimeKit;
using Mailsink.Core.Domain;
using Mailsink.Core.Events;
using Mailsink.Core.Mime;
using Mailsink.Core.Search;
using Mailsink.Core.Storage;

namespace Mailsink.Storage.Sqlite;

internal sealed record StoredMeta(string? Helo, string? ClientIp, bool Secure, string? AuthUser, string? SessionId);

internal sealed record StoredAddresses(
    EmailAddress? From,
    IReadOnlyList<EmailAddress> To,
    IReadOnlyList<EmailAddress> Cc,
    IReadOnlyList<EmailAddress> Bcc,
    IReadOnlyList<EmailAddress> ReplyTo);

[JsonSerializable(typeof(StoredMeta))]
[JsonSerializable(typeof(StoredAddresses))]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class SqliteJsonContext : JsonSerializerContext;

/// <summary>
/// SQLite (WAL) store. Writes are serialized through one gate and run in a transaction each; reads use pooled
/// connections concurrently. Free-text search uses FTS5.
/// </summary>
public sealed class SqliteMailStore(SqliteDatabase db, IBlobStore blobs, IMailEventBus bus, ParsedMessageCache cache) : IMailStore
{
    private const string MessageColumns = """
        m.id, m.blob_key, m.size, m.received_at, m.source, m.smtp_mail_from, m.smtp_rcpt_to, m.smtp_meta,
        m.message_id_hdr, m.subject, m.date_hdr, m.snippet, m.has_attachments, m.attachment_count, m.addresses_json,
        m.imap_envelope, m.imap_bodystructure, m.imap_body, m.is_read_in_ui, m.in_capture_view,
        (SELECT group_concat(tag, char(31)) FROM message_tags t WHERE t.message_id = m.id) AS tags
        """;

    private const string MailboxColumns = "id, owner, name, uid_validity, uid_next, highest_modseq, special_use, subscribed";
    private const string EntryColumns = "mailbox_id, uid, message_id, flags, keywords, internal_date, modseq, size";

    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public Task InitializeAsync(CancellationToken ct) => db.MigrateAsync(ct);

    // ---------------------------------------------------------------- accounts

    public async Task<Account?> FindAccountAsync(string address, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var list = await QueryAsync(c, null, "SELECT id, address, password_hash, created_at, last_login_at, last_login_protocol FROM accounts WHERE address = $a",
            p => p.AddWithValue("$a", address), ReadAccount, ct);
        return list.FirstOrDefault();
    }

    public async Task<Account> CreateAccountAsync(string address, byte[]? passwordHash, CancellationToken ct)
    {
        await WriteAsync(async (c, tx) =>
        {
            await ExecAsync(c, tx, "INSERT OR IGNORE INTO accounts(address, password_hash, created_at) VALUES ($a, $h, $t)", p =>
            {
                p.AddWithValue("$a", address.ToLowerInvariant());
                p.Add("$h", SqliteType.Blob).Value = (object?)passwordHash ?? DBNull.Value;
                p.AddWithValue("$t", Now());
            }, ct);
        }, ct);
        return (await FindAccountAsync(address, ct))!;
    }

    public async Task<IReadOnlyList<Account>> ListAccountsAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return await QueryAsync(c, null, "SELECT id, address, password_hash, created_at, last_login_at, last_login_protocol FROM accounts ORDER BY address", null, ReadAccount, ct);
    }

    public async Task<bool> DeleteAccountAsync(string address, CancellationToken ct)
    {
        var orphans = new List<string>();
        var deleted = await WriteAsync(async (c, tx) =>
        {
            var messageIds = await QueryAsync(c, tx,
                "SELECT DISTINCT e.message_id FROM mailbox_entries e JOIN mailboxes b ON b.id = e.mailbox_id WHERE b.owner = $a",
                p => p.AddWithValue("$a", address), r => r.GetString(0), ct);
            var n = await ExecAsync(c, tx, "DELETE FROM accounts WHERE address = $a", p => p.AddWithValue("$a", address), ct);
            foreach (var id in messageIds)
            {
                await CollectIfOrphanAsync(c, tx, id, orphans, ct);
            }

            return n > 0;
        }, ct);

        await DeleteBlobsAsync(orphans, ct);
        if (deleted)
        {
            bus.Publish(new MailboxesChangedEvent(address));
            bus.Publish(new AccountsChangedEvent());
        }

        return deleted;
    }

    public Task SetPasswordAsync(string address, byte[]? passwordHash, CancellationToken ct) =>
        WriteAsync((c, tx) => ExecAsync(c, tx, "UPDATE accounts SET password_hash = $h WHERE address = $a", p =>
        {
            p.AddWithValue("$a", address);
            p.Add("$h", SqliteType.Blob).Value = (object?)passwordHash ?? DBNull.Value;
        }, ct), ct);

    public Task RecordLoginAsync(string address, string protocol, CancellationToken ct) =>
        WriteAsync((c, tx) => ExecAsync(c, tx, "UPDATE accounts SET last_login_at = $t, last_login_protocol = $p WHERE address = $a", p =>
        {
            p.AddWithValue("$a", address);
            p.AddWithValue("$t", Now());
            p.AddWithValue("$p", protocol);
        }, ct), ct);

    // ---------------------------------------------------------------- capture + delivery

    public async Task<CapturedMessage> CaptureAsync(ReadOnlyMemory<byte> rawMime, Envelope envelope, bool inCaptureView, CancellationToken ct)
    {
        var raw = MimeText.NormalizeLineEndings(rawMime.Span);
        var key = StoreHelpers.ComputeBlobKey(raw);
        var analysis = MessageAnalyzer.Analyze(raw);
        var message = new CapturedMessage(MessageId.New(), key, raw.Length, DateTimeOffset.UtcNow, envelope,
            analysis.Summary, analysis.Imap, [], false, inCaptureView);
        var s = analysis.Summary;

        // Blob put happens under the writer gate but outside the transaction (the database blob store uses its own connection).
        await GatedAsync(async () =>
        {
            await blobs.PutAsync(key, raw, ct);
            return await TransactAsync(async (c, tx) =>
        {
            await ExecAsync(c, tx, """
                INSERT INTO messages(id, blob_key, size, received_at, source, smtp_mail_from, smtp_rcpt_to, smtp_meta,
                  message_id_hdr, subject, from_addr, to_addrs, cc_addrs, bcc_addrs, date_hdr, snippet, has_attachments,
                  attachment_count, addresses_json, imap_envelope, imap_bodystructure, imap_body, in_capture_view)
                VALUES ($id, $key, $size, $received, $source, $from, $rcpt, $meta, $mid, $subject, $fromAddr, $to, $cc, $bcc,
                  $date, $snippet, $hasAtt, $attCount, $addr, $env, $bs, $body, $view)
                """, p =>
            {
                p.AddWithValue("$id", message.Id.ToString());
                p.AddWithValue("$key", key);
                p.AddWithValue("$size", message.Size);
                p.AddWithValue("$received", message.ReceivedAt.ToUnixTimeMilliseconds());
                p.AddWithValue("$source", (int)envelope.Source);
                p.AddWithValue("$from", (object?)envelope.MailFrom ?? DBNull.Value);
                p.AddWithValue("$rcpt", JsonSerializer.Serialize(envelope.RcptTo.ToList(), SqliteJsonContext.Default.ListString));
                p.AddWithValue("$meta", JsonSerializer.Serialize(new StoredMeta(envelope.Helo, envelope.ClientIp, envelope.Secure, envelope.AuthUser, envelope.SessionId), SqliteJsonContext.Default.StoredMeta));
                p.AddWithValue("$mid", (object?)s.MessageIdHeader ?? DBNull.Value);
                p.AddWithValue("$subject", (object?)s.Subject ?? DBNull.Value);
                p.AddWithValue("$fromAddr", (object?)s.From?.ToString() ?? DBNull.Value);
                p.AddWithValue("$to", string.Join(", ", s.To));
                p.AddWithValue("$cc", string.Join(", ", s.Cc));
                p.AddWithValue("$bcc", string.Join(", ", s.Bcc));
                p.AddWithValue("$date", (object?)s.Date?.ToUnixTimeMilliseconds() ?? DBNull.Value);
                p.AddWithValue("$snippet", s.Snippet);
                p.AddWithValue("$hasAtt", s.HasAttachments ? 1 : 0);
                p.AddWithValue("$attCount", s.AttachmentCount);
                p.AddWithValue("$addr", JsonSerializer.Serialize(new StoredAddresses(s.From, s.To, s.Cc, s.Bcc, s.ReplyTo), SqliteJsonContext.Default.StoredAddresses));
                p.AddWithValue("$env", analysis.Imap.Envelope);
                p.AddWithValue("$bs", analysis.Imap.BodyStructure);
                p.AddWithValue("$body", analysis.Imap.Body);
                p.AddWithValue("$view", inCaptureView ? 1 : 0);
            }, ct);

            await ExecAsync(c, tx, """
                INSERT INTO messages_fts(rowid, subject, from_addr, to_addrs, body_text)
                VALUES ((SELECT rowid FROM messages WHERE id = $id), $subject, $from, $to, $body)
                """, p =>
            {
                p.AddWithValue("$id", message.Id.ToString());
                p.AddWithValue("$subject", s.Subject ?? "");
                p.AddWithValue("$from", s.From?.ToString() ?? "");
                p.AddWithValue("$to", string.Join(", ", s.To.Concat(s.Cc)));
                p.AddWithValue("$body", analysis.BodyText);
            }, ct);
            return true;
        }, ct);
        }, ct);

        bus.Publish(new MessageCapturedEvent(message));
        return message;
    }

    public async Task<MailboxEntry> AppendAsync(MailboxId mailbox, MessageId message, SystemFlags flags, IReadOnlyCollection<string> keywords, DateTimeOffset internalDate, CancellationToken ct)
    {
        var (owner, entry) = await WriteAsync(async (c, tx) =>
        {
            var box = await GetBoxAsync(c, tx, mailbox, ct);
            var size = await ScalarAsync<long?>(c, tx, "SELECT size FROM messages WHERE id = $id", p => p.AddWithValue("$id", message.ToString()), ct)
                       ?? throw new MailStoreException($"Message {message} not found");
            var entry = new MailboxEntry(mailbox, box.UidNext, message, flags & ~SystemFlags.Recent,
                [.. keywords.Distinct(StringComparer.OrdinalIgnoreCase)], internalDate, box.HighestModSeq + 1, size);
            await InsertEntryAsync(c, tx, entry, ct);
            await ExecAsync(c, tx, "UPDATE mailboxes SET uid_next = $next, highest_modseq = $mod WHERE id = $id", p =>
            {
                p.AddWithValue("$next", entry.Uid + 1);
                p.AddWithValue("$mod", (long)entry.ModSeq);
                p.AddWithValue("$id", mailbox.Value);
            }, ct);
            return (box.Owner, entry);
        }, ct);

        bus.Publish(new EntryAddedEvent(owner, entry));
        return entry;
    }

    // ---------------------------------------------------------------- mailboxes

    public async Task<IReadOnlyList<Mailbox>> ListMailboxesAsync(string owner, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var list = await QueryAsync(c, null, $"SELECT {MailboxColumns} FROM mailboxes WHERE owner = $o", p => p.AddWithValue("$o", owner), ReadMailbox, ct);
        return [.. list.OrderBy(b => StoreHelpers.IsInbox(b.Name) ? 0 : 1).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<Mailbox?> GetMailboxAsync(string owner, string name, CancellationToken ct)
    {
        string normalized;
        try
        {
            normalized = StoreHelpers.NormalizeMailboxName(name);
        }
        catch (MailStoreException)
        {
            return null;
        }

        await using var c = await db.OpenAsync(ct);
        var list = await QueryAsync(c, null, $"SELECT {MailboxColumns} FROM mailboxes WHERE owner = $o AND name = $n", p =>
        {
            p.AddWithValue("$o", owner);
            p.AddWithValue("$n", normalized);
        }, ReadMailbox, ct);
        return list.FirstOrDefault();
    }

    public async Task<Mailbox?> GetMailboxAsync(MailboxId id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var list = await QueryAsync(c, null, $"SELECT {MailboxColumns} FROM mailboxes WHERE id = $id", p => p.AddWithValue("$id", id.Value), ReadMailbox, ct);
        return list.FirstOrDefault();
    }

    public async Task<Mailbox> CreateMailboxAsync(string owner, string name, SpecialUse specialUse, CancellationToken ct)
    {
        var normalized = StoreHelpers.NormalizeMailboxName(name);
        var mailbox = await WriteAsync((c, tx) => InsertMailboxAsync(c, tx, owner, normalized, specialUse, ct), ct);
        bus.Publish(new MailboxesChangedEvent(mailbox.Owner));
        return mailbox;
    }

    public async Task RenameMailboxAsync(MailboxId id, string newName, CancellationToken ct)
    {
        var normalized = StoreHelpers.NormalizeMailboxName(newName);
        var (owner, expunged) = await WriteAsync(async (c, tx) =>
        {
            var box = await GetBoxAsync(c, tx, id, ct);
            var exists = await ScalarAsync<long?>(c, tx, "SELECT id FROM mailboxes WHERE owner = $o AND name = $n", p =>
            {
                p.AddWithValue("$o", box.Owner);
                p.AddWithValue("$n", normalized);
            }, ct);
            if (exists is not null)
            {
                throw new MailStoreException("Target mailbox already exists");
            }

            if (StoreHelpers.IsInbox(box.Name))
            {
                // RFC 3501: renaming INBOX moves its messages to the new mailbox and leaves INBOX empty.
                var target = await InsertMailboxAsync(c, tx, box.Owner, normalized, SpecialUse.None, ct);
                var entries = await QueryAsync(c, tx, $"SELECT {EntryColumns} FROM mailbox_entries WHERE mailbox_id = $id ORDER BY uid",
                    p => p.AddWithValue("$id", id.Value), ReadEntry, ct);
                var next = target.UidNext;
                foreach (var e in entries)
                {
                    await InsertEntryAsync(c, tx, e with { Mailbox = target.Id, Uid = next, ModSeq = target.HighestModSeq + next }, ct);
                    next++;
                }

                await ExecAsync(c, tx, "UPDATE mailboxes SET uid_next = $n, highest_modseq = $m WHERE id = $id", p =>
                {
                    p.AddWithValue("$n", next);
                    p.AddWithValue("$m", (long)(target.HighestModSeq + next));
                    p.AddWithValue("$id", target.Id.Value);
                }, ct);
                await ExecAsync(c, tx, "DELETE FROM mailbox_entries WHERE mailbox_id = $id", p => p.AddWithValue("$id", id.Value), ct);
                return (box.Owner, (EntriesExpungedEvent?)new EntriesExpungedEvent(box.Owner, id, [.. entries.Select(e => e.Uid)]));
            }

            await ExecAsync(c, tx, """
                UPDATE mailboxes SET name = $new || substr(name, length($old) + 1)
                WHERE owner = $o AND (name = $old OR name LIKE $prefix ESCAPE '\')
                """, p =>
            {
                p.AddWithValue("$new", normalized);
                p.AddWithValue("$old", box.Name);
                p.AddWithValue("$o", box.Owner);
                p.AddWithValue("$prefix", EscapeLike(box.Name) + "/%");
            }, ct);
            return (box.Owner, (EntriesExpungedEvent?)null);
        }, ct);

        if (expunged is not null)
        {
            bus.Publish(expunged);
        }

        bus.Publish(new MailboxesChangedEvent(owner));
    }

    public async Task DeleteMailboxAsync(MailboxId id, CancellationToken ct)
    {
        var orphans = new List<string>();
        var owner = await WriteAsync(async (c, tx) =>
        {
            var box = await GetBoxAsync(c, tx, id, ct);
            if (StoreHelpers.IsInbox(box.Name))
            {
                throw new MailStoreException("INBOX cannot be deleted");
            }

            var children = await ScalarAsync<long?>(c, tx, "SELECT COUNT(*) FROM mailboxes WHERE owner = $o AND name LIKE $prefix ESCAPE '\\'", p =>
            {
                p.AddWithValue("$o", box.Owner);
                p.AddWithValue("$prefix", EscapeLike(box.Name) + "/%");
            }, ct);
            if (children > 0)
            {
                throw new MailStoreException("Mailbox has children");
            }

            var messageIds = await QueryAsync(c, tx, "SELECT DISTINCT message_id FROM mailbox_entries WHERE mailbox_id = $id",
                p => p.AddWithValue("$id", id.Value), r => r.GetString(0), ct);
            await ExecAsync(c, tx, "DELETE FROM mailboxes WHERE id = $id", p => p.AddWithValue("$id", id.Value), ct);
            foreach (var m in messageIds)
            {
                await CollectIfOrphanAsync(c, tx, m, orphans, ct);
            }

            return box.Owner;
        }, ct);

        await DeleteBlobsAsync(orphans, ct);
        bus.Publish(new MailboxesChangedEvent(owner));
    }

    public async Task SetSubscribedAsync(MailboxId id, bool subscribed, CancellationToken ct)
    {
        var owner = await WriteAsync(async (c, tx) =>
        {
            var box = await GetBoxAsync(c, tx, id, ct);
            await ExecAsync(c, tx, "UPDATE mailboxes SET subscribed = $s WHERE id = $id", p =>
            {
                p.AddWithValue("$s", subscribed ? 1 : 0);
                p.AddWithValue("$id", id.Value);
            }, ct);
            return box.Owner;
        }, ct);
        bus.Publish(new MailboxesChangedEvent(owner));
    }

    public async Task<MailboxStats> GetMailboxStatsAsync(MailboxId id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var list = await QueryAsync(c, null,
            "SELECT COUNT(*), COALESCE(SUM(CASE WHEN flags & 1 = 0 THEN 1 ELSE 0 END), 0), COALESCE(SUM(size), 0) FROM mailbox_entries WHERE mailbox_id = $id",
            p => p.AddWithValue("$id", id.Value), r => new MailboxStats(r.GetInt32(0), r.GetInt32(1), r.GetInt64(2)), ct);
        return list[0];
    }

    // ---------------------------------------------------------------- entries

    public async Task<IReadOnlyList<MailboxEntry>> GetEntriesAsync(MailboxId mailbox, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return await QueryAsync(c, null, $"SELECT {EntryColumns} FROM mailbox_entries WHERE mailbox_id = $id ORDER BY uid",
            p => p.AddWithValue("$id", mailbox.Value), ReadEntry, ct);
    }

    public async Task<IReadOnlyList<MailboxEntry>> StoreFlagsAsync(MailboxId mailbox, IReadOnlyCollection<uint> uids, FlagOperation op, SystemFlags flags, IReadOnlyCollection<string> keywords, CancellationToken ct)
    {
        var (owner, result, changed) = await WriteAsync(async (c, tx) =>
        {
            var box = await GetBoxAsync(c, tx, mailbox, ct);
            var wanted = uids.ToHashSet();
            var entries = (await QueryAsync(c, tx, $"SELECT {EntryColumns} FROM mailbox_entries WHERE mailbox_id = $id ORDER BY uid",
                p => p.AddWithValue("$id", mailbox.Value), ReadEntry, ct)).Where(e => wanted.Contains(e.Uid)).ToList();
            var modseq = box.HighestModSeq;
            var result = new List<MailboxEntry>();
            var changed = new List<MailboxEntry>();
            foreach (var entry in entries)
            {
                var newFlags = StoreHelpers.ApplyFlags(entry.Flags, op, flags & ~SystemFlags.Recent);
                var newKeywords = StoreHelpers.ApplyKeywords(entry.Keywords, op, keywords);
                var current = entry;
                if (newFlags != entry.Flags || !newKeywords.SequenceEqual(entry.Keywords, StringComparer.OrdinalIgnoreCase))
                {
                    modseq++;
                    current = entry with { Flags = newFlags, Keywords = newKeywords, ModSeq = modseq };
                    await ExecAsync(c, tx, "UPDATE mailbox_entries SET flags = $f, keywords = $k, modseq = $m WHERE mailbox_id = $b AND uid = $u", p =>
                    {
                        p.AddWithValue("$f", (int)current.Flags);
                        p.AddWithValue("$k", current.Keywords.Count == 0 ? DBNull.Value : string.Join(' ', current.Keywords));
                        p.AddWithValue("$m", (long)modseq);
                        p.AddWithValue("$b", mailbox.Value);
                        p.AddWithValue("$u", current.Uid);
                    }, ct);
                    changed.Add(current);
                }

                result.Add(current);
            }

            if (changed.Count > 0)
            {
                await SetModSeqAsync(c, tx, mailbox, modseq, ct);
            }

            return (box.Owner, result, changed);
        }, ct);

        foreach (var entry in changed)
        {
            bus.Publish(new EntryFlagsChangedEvent(owner, entry));
        }

        return result;
    }

    public async Task<CopyResult> CopyAsync(MailboxId from, IReadOnlyCollection<uint> uids, MailboxId to, bool move, CancellationToken ct)
    {
        var (sourceOwner, destOwner, result, added) = await WriteAsync(async (c, tx) =>
        {
            var source = await GetBoxAsync(c, tx, from, ct);
            var dest = await GetBoxAsync(c, tx, to, ct);
            var wanted = uids.ToHashSet();
            var entries = (await QueryAsync(c, tx, $"SELECT {EntryColumns} FROM mailbox_entries WHERE mailbox_id = $id ORDER BY uid",
                p => p.AddWithValue("$id", from.Value), ReadEntry, ct)).Where(e => wanted.Contains(e.Uid)).ToList();

            var next = dest.UidNext;
            var modseq = dest.HighestModSeq;
            var added = new List<MailboxEntry>();
            foreach (var entry in entries)
            {
                modseq++;
                var copy = entry with { Mailbox = to, Uid = next++, ModSeq = modseq, Flags = entry.Flags & ~SystemFlags.Recent };
                await InsertEntryAsync(c, tx, copy, ct);
                added.Add(copy);
            }

            await ExecAsync(c, tx, "UPDATE mailboxes SET uid_next = $n, highest_modseq = $m WHERE id = $id", p =>
            {
                p.AddWithValue("$n", next);
                p.AddWithValue("$m", (long)modseq);
                p.AddWithValue("$id", to.Value);
            }, ct);

            if (move && entries.Count > 0)
            {
                foreach (var entry in entries)
                {
                    await ExecAsync(c, tx, "DELETE FROM mailbox_entries WHERE mailbox_id = $b AND uid = $u", p =>
                    {
                        p.AddWithValue("$b", from.Value);
                        p.AddWithValue("$u", entry.Uid);
                    }, ct);
                }

                await SetModSeqAsync(c, tx, from, source.HighestModSeq + 1, ct);
            }

            var result = new CopyResult(dest.UidValidity, [.. entries.Select(e => e.Uid)], [.. added.Select(e => e.Uid)]);
            return (source.Owner, dest.Owner, result, added);
        }, ct);

        foreach (var entry in added)
        {
            bus.Publish(new EntryAddedEvent(destOwner, entry));
        }

        if (move && result.SourceUids.Count > 0)
        {
            bus.Publish(new EntriesExpungedEvent(sourceOwner, from, result.SourceUids));
        }

        return result;
    }

    public async Task<IReadOnlyList<uint>> ExpungeAsync(MailboxId mailbox, IReadOnlyCollection<uint>? onlyUids, CancellationToken ct)
    {
        var orphans = new List<string>();
        var (owner, removed) = await WriteAsync(async (c, tx) =>
        {
            var box = await GetBoxAsync(c, tx, mailbox, ct);
            var only = onlyUids?.ToHashSet();
            var entries = (await QueryAsync(c, tx, $"SELECT {EntryColumns} FROM mailbox_entries WHERE mailbox_id = $id AND (flags & 8) != 0 ORDER BY uid",
                p => p.AddWithValue("$id", mailbox.Value), ReadEntry, ct)).Where(e => only is null || only.Contains(e.Uid)).ToList();
            foreach (var entry in entries)
            {
                await ExecAsync(c, tx, "DELETE FROM mailbox_entries WHERE mailbox_id = $b AND uid = $u", p =>
                {
                    p.AddWithValue("$b", mailbox.Value);
                    p.AddWithValue("$u", entry.Uid);
                }, ct);
            }

            foreach (var id in entries.Select(e => e.Message.ToString()).Distinct())
            {
                await CollectIfOrphanAsync(c, tx, id, orphans, ct);
            }

            if (entries.Count > 0)
            {
                await SetModSeqAsync(c, tx, mailbox, box.HighestModSeq + 1, ct);
            }

            return (box.Owner, (IReadOnlyList<uint>)[.. entries.Select(e => e.Uid)]);
        }, ct);

        await DeleteBlobsAsync(orphans, ct);
        if (removed.Count > 0)
        {
            bus.Publish(new EntriesExpungedEvent(owner, mailbox, removed));
        }

        return removed;
    }

    // ---------------------------------------------------------------- content

    public async Task<CapturedMessage?> GetMessageAsync(MessageId id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var list = await QueryAsync(c, null, $"SELECT {MessageColumns} FROM messages m WHERE m.id = $id", p => p.AddWithValue("$id", id.ToString()), ReadMessage, ct);
        return list.FirstOrDefault();
    }

    public async Task<IReadOnlyDictionary<MessageId, CapturedMessage>> GetMessagesAsync(IReadOnlyCollection<MessageId> ids, CancellationToken ct)
    {
        var result = new Dictionary<MessageId, CapturedMessage>();
        if (ids.Count == 0)
        {
            return result;
        }

        await using var c = await db.OpenAsync(ct);
        foreach (var chunk in ids.Distinct().Chunk(500))
        {
            var names = chunk.Select((_, i) => "$p" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
            var list = await QueryAsync(c, null, $"SELECT {MessageColumns} FROM messages m WHERE m.id IN ({string.Join(',', names)})", p =>
            {
                for (var i = 0; i < chunk.Length; i++)
                {
                    p.AddWithValue(names[i], chunk[i].ToString());
                }
            }, ReadMessage, ct);
            foreach (var m in list)
            {
                result[m.Id] = m;
            }
        }

        return result;
    }

    public async Task<byte[]> ReadRawAsync(MessageId id, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var key = await ScalarAsync<string?>(c, null, "SELECT blob_key FROM messages WHERE id = $id", p => p.AddWithValue("$id", id.ToString()), ct)
                  ?? throw new MailStoreException($"Message {id} not found");
        return await blobs.GetAsync(key, ct);
    }

    public Task<MimeMessage> LoadParsedAsync(MessageId id, CancellationToken ct) =>
        cache.GetOrLoadAsync(id, () => ReadRawAsync(id, ct), ct);

    // ---------------------------------------------------------------- capture view

    public async Task<PagedResult<CapturedMessage>> QueryCaptureAsync(CaptureQuery query, CancellationToken ct)
    {
        var (where, bind) = BuildWhere(query);
        var limit = Math.Clamp(query.Limit, 1, 1000);
        var start = Math.Max(0, query.Start);
        await using var c = await db.OpenAsync(ct);
        var total = await ScalarAsync<long?>(c, null, $"SELECT COUNT(*) FROM messages m WHERE {where}", bind, ct) ?? 0;
        var items = await QueryAsync(c, null,
            $"SELECT {MessageColumns} FROM messages m WHERE {where} ORDER BY m.received_at DESC, m.id DESC LIMIT $limit OFFSET $start", p =>
            {
                bind(p);
                p.AddWithValue("$limit", limit);
                p.AddWithValue("$start", start);
            }, ReadMessage, ct);
        return new PagedResult<CapturedMessage>(items, (int)total, start, limit);
    }

    public async Task<CaptureStats> GetCaptureStatsAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        var list = await QueryAsync(c, null, """
            SELECT COALESCE(SUM(in_capture_view), 0),
                   COALESCE(SUM(CASE WHEN in_capture_view = 1 AND is_read_in_ui = 0 THEN 1 ELSE 0 END), 0),
                   COALESCE(SUM(size), 0)
            FROM messages
            """, null, r => new CaptureStats(r.GetInt32(0), r.GetInt32(1), r.GetInt64(2)), ct);
        return list[0];
    }

    public async Task SetReadAsync(IReadOnlyCollection<MessageId>? ids, bool read, CancellationToken ct)
    {
        var updated = await WriteAsync(async (c, tx) =>
        {
            List<string> targets;
            if (ids is null)
            {
                targets = await QueryAsync(c, tx, "SELECT id FROM messages WHERE is_read_in_ui != $r", p => p.AddWithValue("$r", read ? 1 : 0), r => r.GetString(0), ct);
            }
            else
            {
                targets = [.. ids.Select(i => i.ToString())];
            }

            var changed = new List<string>();
            foreach (var id in targets)
            {
                var n = await ExecAsync(c, tx, "UPDATE messages SET is_read_in_ui = $r WHERE id = $id AND is_read_in_ui != $r", p =>
                {
                    p.AddWithValue("$r", read ? 1 : 0);
                    p.AddWithValue("$id", id);
                }, ct);
                if (n > 0)
                {
                    changed.Add(id);
                }
            }

            return changed;
        }, ct);

        if (updated.Count == 0)
        {
            return;
        }

        var messages = await GetMessagesAsync([.. updated.Select(i => new MessageId(Guid.Parse(i)))], ct);
        foreach (var m in messages.Values)
        {
            bus.Publish(new MessageUpdatedEvent(m.Id, m.IsRead, m.Tags));
        }
    }

    public async Task SetTagsAsync(MessageId id, IReadOnlyCollection<string> tags, CancellationToken ct)
    {
        var clean = tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await WriteAsync(async (c, tx) =>
        {
            var exists = await ScalarAsync<long?>(c, tx, "SELECT 1 FROM messages WHERE id = $id", p => p.AddWithValue("$id", id.ToString()), ct);
            if (exists is null)
            {
                throw new MailStoreException($"Message {id} not found");
            }

            await ExecAsync(c, tx, "DELETE FROM message_tags WHERE message_id = $id", p => p.AddWithValue("$id", id.ToString()), ct);
            foreach (var tag in clean)
            {
                await ExecAsync(c, tx, "INSERT INTO message_tags(message_id, tag) VALUES ($id, $t)", p =>
                {
                    p.AddWithValue("$id", id.ToString());
                    p.AddWithValue("$t", tag);
                }, ct);
            }
        }, ct);

        var message = await GetMessageAsync(id, ct);
        if (message is not null)
        {
            bus.Publish(new MessageUpdatedEvent(message.Id, message.IsRead, message.Tags));
        }
    }

    public async Task<IReadOnlyList<string>> ListTagsAsync(CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        return await QueryAsync(c, null,
            "SELECT DISTINCT t.tag FROM message_tags t JOIN messages m ON m.id = t.message_id WHERE m.in_capture_view = 1 ORDER BY t.tag COLLATE NOCASE",
            null, r => r.GetString(0), ct);
    }

    public async Task DeleteCapturedAsync(IReadOnlyCollection<MessageId>? ids, CancellationToken ct)
    {
        var orphans = new List<string>();
        var deleted = await WriteAsync(async (c, tx) =>
        {
            var targets = ids is null
                ? await QueryAsync(c, tx, "SELECT id FROM messages WHERE in_capture_view = 1", null, r => r.GetString(0), ct)
                : [.. ids.Select(i => i.ToString())];
            return await HideAsync(c, tx, targets, orphans, ct);
        }, ct);

        await DeleteBlobsAsync(orphans, ct);
        if (deleted.Count > 0)
        {
            bus.Publish(new MessagesDeletedEvent([.. deleted.Select(i => new MessageId(Guid.Parse(i)))]));
        }
    }

    public async Task<int> ApplyRetentionAsync(RetentionPolicy policy, CancellationToken ct)
    {
        var orphans = new List<string>();
        var expunged = new List<EntriesExpungedEvent>();
        var deleted = await WriteAsync(async (c, tx) =>
        {
            var visible = await QueryAsync(c, tx, "SELECT id, size, received_at FROM messages WHERE in_capture_view = 1 ORDER BY received_at DESC", null,
                r => (Id: r.GetString(0), Size: r.GetInt64(1), At: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(2))), ct);
            var cutoff = policy.MaxAge is { } age ? DateTimeOffset.UtcNow - age : DateTimeOffset.MinValue;
            long total = 0;
            var expired = new List<string>();
            for (var i = 0; i < visible.Count; i++)
            {
                total += visible[i].Size;
                if ((policy.MaxMessages > 0 && i >= policy.MaxMessages) || visible[i].At < cutoff || (policy.MaxTotalSize is { } max && total > max))
                {
                    expired.Add(visible[i].Id);
                }
            }

            if (policy.AppliesToMailboxes)
            {
                foreach (var id in expired)
                {
                    var entries = await QueryAsync(c, tx,
                        "SELECT e.mailbox_id, e.uid, b.owner FROM mailbox_entries e JOIN mailboxes b ON b.id = e.mailbox_id WHERE e.message_id = $id",
                        p => p.AddWithValue("$id", id), r => (Box: r.GetInt64(0), Uid: (uint)r.GetInt64(1), Owner: r.GetString(2)), ct);
                    await ExecAsync(c, tx, "DELETE FROM mailbox_entries WHERE message_id = $id", p => p.AddWithValue("$id", id), ct);
                    expunged.AddRange(entries.GroupBy(e => (e.Box, e.Owner))
                        .Select(g => new EntriesExpungedEvent(g.Key.Owner, new MailboxId(g.Key.Box), [.. g.Select(e => e.Uid)])));
                }
            }

            return await HideAsync(c, tx, expired, orphans, ct);
        }, ct);

        await DeleteBlobsAsync(orphans, ct);
        foreach (var e in expunged)
        {
            bus.Publish(e);
        }

        if (deleted.Count > 0)
        {
            bus.Publish(new MessagesDeletedEvent([.. deleted.Select(i => new MessageId(Guid.Parse(i)))]));
        }

        return deleted.Count;
    }

    public async Task PurgeAsync(CancellationToken ct)
    {
        var (ids, owners) = await GatedAsync(async () =>
        {
            var result = await TransactAsync(async (c, tx) =>
            {
                var ids = await QueryAsync(c, tx, "SELECT id FROM messages WHERE in_capture_view = 1", null, r => r.GetString(0), ct);
                var owners = await QueryAsync(c, tx, "SELECT address FROM accounts", null, r => r.GetString(0), ct);
                await ExecAsync(c, tx, "DELETE FROM mailbox_entries; DELETE FROM mailboxes; DELETE FROM accounts; DELETE FROM message_tags; DELETE FROM messages; DELETE FROM messages_fts;", null, ct);
                return (ids, owners);
            }, ct);
            await blobs.DeleteAllAsync(ct);
            return result;
        }, ct);

        cache.Clear();
        if (ids.Count > 0)
        {
            bus.Publish(new MessagesDeletedEvent([.. ids.Select(i => new MessageId(Guid.Parse(i)))]));
        }

        foreach (var owner in owners)
        {
            bus.Publish(new MailboxesChangedEvent(owner));
        }

        bus.Publish(new AccountsChangedEvent());
    }

    // ---------------------------------------------------------------- search translation

    private static (string Where, Action<SqliteParameterCollection> Bind) BuildWhere(CaptureQuery query)
    {
        var clauses = new List<string> { "m.in_capture_view = 1" };
        var parameters = new List<(string, object)>();
        var n = 0;
        string Param(object value)
        {
            var name = "$q" + (n++).ToString(CultureInfo.InvariantCulture);
            parameters.Add((name, value));
            return name;
        }

        if (query.Tag is { Length: > 0 } tag)
        {
            clauses.Add($"EXISTS (SELECT 1 FROM message_tags t WHERE t.message_id = m.id AND t.tag = {Param(tag)})");
        }

        if (query.Unread is { } unread)
        {
            clauses.Add(unread ? "m.is_read_in_ui = 0" : "m.is_read_in_ui = 1");
        }

        if (query.HasAttachments is { } has)
        {
            clauses.Add(has ? "m.has_attachments = 1" : "m.has_attachments = 0");
        }

        if (query.Source is { } source)
        {
            clauses.Add($"m.source = {Param((int)source)}");
        }

        if (query.ReceivedAfter is { } after)
        {
            clauses.Add($"m.received_at > {Param(after.ToUnixTimeMilliseconds())}");
        }

        foreach (var term in query.Terms)
        {
            var like = "%" + EscapeLike(term.Value) + "%";
            var expression = term.Field switch
            {
                SearchField.From => $"(m.from_addr LIKE {Param(like)} ESCAPE '\\' OR m.smtp_mail_from LIKE {Param(like)} ESCAPE '\\')",
                SearchField.To => $"(m.to_addrs LIKE {Param(like)} ESCAPE '\\' OR m.cc_addrs LIKE {Param(like)} ESCAPE '\\' OR m.bcc_addrs LIKE {Param(like)} ESCAPE '\\' OR m.smtp_rcpt_to LIKE {Param(like)} ESCAPE '\\')",
                SearchField.Subject => $"m.subject LIKE {Param(like)} ESCAPE '\\'",
                SearchField.Tag => $"EXISTS (SELECT 1 FROM message_tags t WHERE t.message_id = m.id AND t.tag = {Param(term.Value)})",
                SearchField.IsRead => "m.is_read_in_ui = 1",
                SearchField.IsUnread => "m.is_read_in_ui = 0",
                SearchField.HasAttachment => "m.has_attachments = 1",
                SearchField.Before => term.DateValue is { } d ? $"m.received_at < {Param(d.ToUnixTimeMilliseconds())}" : "0",
                SearchField.After => term.DateValue is { } d ? $"m.received_at > {Param(d.ToUnixTimeMilliseconds())}" : "0",
                SearchField.Source => term.SourceValue is { } s ? $"m.source = {Param((int)s)}" : "0",
                SearchField.MessageId => $"m.message_id_hdr LIKE {Param(like)} ESCAPE '\\'",
                _ => $"(m.subject LIKE {Param(like)} ESCAPE '\\' OR m.from_addr LIKE {Param(like)} ESCAPE '\\' OR m.to_addrs LIKE {Param(like)} ESCAPE '\\' " +
                     $"OR m.rowid IN (SELECT rowid FROM messages_fts WHERE messages_fts MATCH {Param(FtsPhrase(term.Value))}))",
            };
            clauses.Add(term.Negated ? $"NOT {expression}" : expression);
        }

        return (string.Join(" AND ", clauses), p =>
        {
            foreach (var (name, value) in parameters)
            {
                p.AddWithValue(name, value);
            }
        });
    }

    private static string FtsPhrase(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"*";

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    // ---------------------------------------------------------------- write helpers

    private Task<T> WriteAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> work, CancellationToken ct) =>
        GatedAsync(() => TransactAsync(work, ct), ct);

    /// <summary>Runs <paramref name="work"/> while holding the single-writer gate.</summary>
    private async Task<T> GatedAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            return await work();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new MailStoreException("Constraint violation: " + ex.Message);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<T> TransactAsync<T>(Func<SqliteConnection, SqliteTransaction, Task<T>> work, CancellationToken ct)
    {
        await using var c = await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        var result = await work(c, tx);
        await tx.CommitAsync(ct);
        return result;
    }

    private Task WriteAsync(Func<SqliteConnection, SqliteTransaction, Task> work, CancellationToken ct) =>
        WriteAsync<bool>(async (c, tx) =>
        {
            await work(c, tx);
            return true;
        }, ct);

    private async Task<Mailbox> InsertMailboxAsync(SqliteConnection c, SqliteTransaction tx, string owner, string name, SpecialUse use, CancellationToken ct)
    {
        var accountId = await ScalarAsync<long?>(c, tx, "SELECT id FROM accounts WHERE address = $a", p => p.AddWithValue("$a", owner), ct);
        if (accountId is null)
        {
            await ExecAsync(c, tx, "INSERT INTO accounts(address, created_at) VALUES ($a, $t)", p =>
            {
                p.AddWithValue("$a", owner.ToLowerInvariant());
                p.AddWithValue("$t", Now());
            }, ct);
            accountId = await ScalarAsync<long?>(c, tx, "SELECT last_insert_rowid()", null, ct);
        }

        var exists = await ScalarAsync<long?>(c, tx, "SELECT id FROM mailboxes WHERE owner = $o AND name = $n", p =>
        {
            p.AddWithValue("$o", owner);
            p.AddWithValue("$n", name);
        }, ct);
        if (exists is not null)
        {
            throw new MailStoreException("Mailbox already exists");
        }

        var validity = StoreHelpers.NewUidValidity();
        await ExecAsync(c, tx, "INSERT INTO mailboxes(account_id, owner, name, uid_validity, special_use) VALUES ($acc, $o, $n, $v, $s)", p =>
        {
            p.AddWithValue("$acc", accountId!.Value);
            p.AddWithValue("$o", owner.ToLowerInvariant());
            p.AddWithValue("$n", name);
            p.AddWithValue("$v", validity);
            p.AddWithValue("$s", (int)use);
        }, ct);
        var id = await ScalarAsync<long?>(c, tx, "SELECT last_insert_rowid()", null, ct);
        return new Mailbox(new MailboxId(id!.Value), owner.ToLowerInvariant(), name, validity, 1, 1, use, true);
    }

    private static Task InsertEntryAsync(SqliteConnection c, SqliteTransaction tx, MailboxEntry e, CancellationToken ct) =>
        ExecAsync(c, tx, $"INSERT INTO mailbox_entries({EntryColumns}) VALUES ($b, $u, $m, $f, $k, $d, $mod, $s)", p =>
        {
            p.AddWithValue("$b", e.Mailbox.Value);
            p.AddWithValue("$u", e.Uid);
            p.AddWithValue("$m", e.Message.ToString());
            p.AddWithValue("$f", (int)e.Flags);
            p.AddWithValue("$k", e.Keywords.Count == 0 ? DBNull.Value : string.Join(' ', e.Keywords));
            p.AddWithValue("$d", e.InternalDate.ToUnixTimeMilliseconds());
            p.AddWithValue("$mod", (long)e.ModSeq);
            p.AddWithValue("$s", e.Size);
        }, ct);

    private static Task SetModSeqAsync(SqliteConnection c, SqliteTransaction tx, MailboxId id, ulong modseq, CancellationToken ct) =>
        ExecAsync(c, tx, "UPDATE mailboxes SET highest_modseq = MAX(highest_modseq, $m) WHERE id = $id", p =>
        {
            p.AddWithValue("$m", (long)modseq);
            p.AddWithValue("$id", id.Value);
        }, ct);

    private static async Task<Mailbox> GetBoxAsync(SqliteConnection c, SqliteTransaction tx, MailboxId id, CancellationToken ct)
    {
        var list = await QueryAsync(c, tx, $"SELECT {MailboxColumns} FROM mailboxes WHERE id = $id", p => p.AddWithValue("$id", id.Value), ReadMailbox, ct);
        return list.FirstOrDefault() ?? throw new MailStoreException("Mailbox does not exist");
    }

    /// <summary>Removes messages from the capture view, deleting those no mailbox references. Returns the hidden ids.</summary>
    private static async Task<List<string>> HideAsync(SqliteConnection c, SqliteTransaction tx, IEnumerable<string> ids, List<string> orphans, CancellationToken ct)
    {
        var hidden = new List<string>();
        foreach (var id in ids)
        {
            var n = await ExecAsync(c, tx, "UPDATE messages SET in_capture_view = 0 WHERE id = $id AND in_capture_view = 1", p => p.AddWithValue("$id", id), ct);
            if (n > 0)
            {
                hidden.Add(id);
                await CollectIfOrphanAsync(c, tx, id, orphans, ct);
            }
        }

        return hidden;
    }

    private static async Task CollectIfOrphanAsync(SqliteConnection c, SqliteTransaction tx, string id, List<string> orphans, CancellationToken ct)
    {
        var rows = await QueryAsync(c, tx, """
            SELECT rowid, blob_key FROM messages
            WHERE id = $id AND in_capture_view = 0 AND NOT EXISTS (SELECT 1 FROM mailbox_entries WHERE message_id = $id)
            """, p => p.AddWithValue("$id", id), r => (RowId: r.GetInt64(0), Key: r.GetString(1)), ct);
        if (rows.Count == 0)
        {
            return;
        }

        var (rowId, key) = rows[0];
        await ExecAsync(c, tx, "DELETE FROM messages_fts WHERE rowid = $r", p => p.AddWithValue("$r", rowId), ct);
        await ExecAsync(c, tx, "DELETE FROM messages WHERE id = $id", p => p.AddWithValue("$id", id), ct);
        var stillUsed = await ScalarAsync<long?>(c, tx, "SELECT 1 FROM messages WHERE blob_key = $k LIMIT 1", p => p.AddWithValue("$k", key), ct);
        if (stillUsed is null)
        {
            orphans.Add(key);
        }
    }

    private async Task DeleteBlobsAsync(List<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
        {
            return;
        }

        // Under the write gate, so a concurrent capture of identical content cannot lose its blob.
        await GatedAsync(async () =>
        {
            foreach (var key in keys)
            {
                await using var c = await db.OpenAsync(ct);
                var used = await ScalarAsync<long?>(c, null, "SELECT 1 FROM messages WHERE blob_key = $k LIMIT 1", p => p.AddWithValue("$k", key), ct);
                if (used is null)
                {
                    await blobs.DeleteAsync(key, ct);
                }
            }

            return true;
        }, ct);
    }

    // ---------------------------------------------------------------- ADO.NET helpers

    private static async Task<int> ExecAsync(SqliteConnection c, SqliteTransaction? tx, string sql, Action<SqliteParameterCollection>? bind, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        bind?.Invoke(command.Parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T?> ScalarAsync<T>(SqliteConnection c, SqliteTransaction? tx, string sql, Action<SqliteParameterCollection>? bind, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        bind?.Invoke(command.Parameters);
        var value = await command.ExecuteScalarAsync(ct);
        if (value is null or DBNull)
        {
            return default;
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    private static async Task<List<T>> QueryAsync<T>(SqliteConnection c, SqliteTransaction? tx, string sql, Action<SqliteParameterCollection>? bind, Func<DbDataReader, T> read, CancellationToken ct)
    {
        await using var command = c.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        bind?.Invoke(command.Parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await reader.ReadAsync(ct))
        {
            list.Add(read(reader));
        }

        return list;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static string? GetStringOrNull(DbDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static Account ReadAccount(DbDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.IsDBNull(2) ? null : (byte[])r.GetValue(2),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
        r.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)),
        GetStringOrNull(r, 5));

    private static Mailbox ReadMailbox(DbDataReader r) => new(
        new MailboxId(r.GetInt64(0)),
        r.GetString(1),
        r.GetString(2),
        (uint)r.GetInt64(3),
        (uint)r.GetInt64(4),
        (ulong)r.GetInt64(5),
        (SpecialUse)r.GetInt32(6),
        r.GetInt32(7) != 0);

    private static MailboxEntry ReadEntry(DbDataReader r) => new(
        new MailboxId(r.GetInt64(0)),
        (uint)r.GetInt64(1),
        new MessageId(Guid.Parse(r.GetString(2))),
        (SystemFlags)r.GetInt32(3),
        r.IsDBNull(4) ? [] : r.GetString(4).Split(' ', StringSplitOptions.RemoveEmptyEntries),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)),
        (ulong)r.GetInt64(6),
        r.GetInt64(7));

    private static CapturedMessage ReadMessage(DbDataReader r)
    {
        var meta = r.IsDBNull(7) ? null : JsonSerializer.Deserialize(r.GetString(7), SqliteJsonContext.Default.StoredMeta);
        var rcpt = r.IsDBNull(6) ? [] : JsonSerializer.Deserialize(r.GetString(6), SqliteJsonContext.Default.ListString) ?? [];
        var addresses = JsonSerializer.Deserialize(r.GetString(14), SqliteJsonContext.Default.StoredAddresses)
                        ?? new StoredAddresses(null, [], [], [], []);
        var envelope = new Envelope(GetStringOrNull(r, 5), rcpt, (MessageSource)r.GetInt32(4), meta?.Helo, meta?.ClientIp,
            meta?.Secure ?? false, meta?.AuthUser, meta?.SessionId);
        var summary = new ParsedSummary(
            GetStringOrNull(r, 8),
            GetStringOrNull(r, 9),
            addresses.From,
            addresses.To,
            addresses.Cc,
            addresses.Bcc,
            addresses.ReplyTo,
            r.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(10)),
            GetStringOrNull(r, 11) ?? "",
            r.GetInt32(12) != 0,
            r.GetInt32(13));
        var tags = r.IsDBNull(20) ? [] : r.GetString(20).Split('\u001f').Order(StringComparer.OrdinalIgnoreCase).ToArray();
        return new CapturedMessage(
            new MessageId(Guid.Parse(r.GetString(0))),
            r.GetString(1),
            r.GetInt64(2),
            DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)),
            envelope,
            summary,
            new ImapRendering(r.GetString(15), r.GetString(16), r.GetString(17)),
            tags,
            r.GetInt32(18) != 0,
            r.GetInt32(19) != 0);
    }
}
