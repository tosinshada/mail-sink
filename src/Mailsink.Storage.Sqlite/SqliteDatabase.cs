using Microsoft.Data.Sqlite;

namespace Mailsink.Storage.Sqlite;

/// <summary>Connection factory and schema migrations for <c>{DataDir}/mailsink.db</c>.</summary>
public sealed class SqliteDatabase(string path)
{
    private const int SchemaVersion = 1;

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
    }.ToString();

    public string Path { get; } = path;

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000; PRAGMA synchronous = NORMAL;";
        await pragma.ExecuteNonQueryAsync(ct);
        return connection;
    }

    public async Task MigrateAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        await using var connection = await OpenAsync(ct);
        await using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            await wal.ExecuteNonQueryAsync(ct);
        }

        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
        if (version >= SchemaVersion)
        {
            return;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Schema + $"\nPRAGMA user_version = {SchemaVersion};";
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS accounts (
          id                  INTEGER PRIMARY KEY,
          address             TEXT NOT NULL UNIQUE COLLATE NOCASE,
          password_hash       BLOB NULL,
          created_at          INTEGER NOT NULL,
          last_login_at       INTEGER NULL,
          last_login_protocol TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS messages (
          id                 TEXT PRIMARY KEY,
          blob_key           TEXT NOT NULL,
          size               INTEGER NOT NULL,
          received_at        INTEGER NOT NULL,
          source             INTEGER NOT NULL,
          smtp_mail_from     TEXT NULL,
          smtp_rcpt_to       TEXT NULL,
          smtp_meta          TEXT NULL,
          message_id_hdr     TEXT NULL,
          subject            TEXT NULL,
          from_addr          TEXT NULL,
          to_addrs           TEXT NULL,
          cc_addrs           TEXT NULL,
          bcc_addrs          TEXT NULL,
          date_hdr           INTEGER NULL,
          snippet            TEXT NULL,
          has_attachments    INTEGER NOT NULL DEFAULT 0,
          attachment_count   INTEGER NOT NULL DEFAULT 0,
          addresses_json     TEXT NOT NULL,
          imap_envelope      TEXT NOT NULL,
          imap_bodystructure TEXT NOT NULL,
          imap_body          TEXT NOT NULL,
          is_read_in_ui      INTEGER NOT NULL DEFAULT 0,
          in_capture_view    INTEGER NOT NULL DEFAULT 1
        );
        CREATE INDEX IF NOT EXISTS ix_messages_received ON messages(received_at DESC);
        CREATE INDEX IF NOT EXISTS ix_messages_blob ON messages(blob_key);

        CREATE TABLE IF NOT EXISTS message_tags (
          message_id TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
          tag        TEXT NOT NULL COLLATE NOCASE,
          PRIMARY KEY (message_id, tag)
        );

        CREATE TABLE IF NOT EXISTS mailboxes (
          id              INTEGER PRIMARY KEY,
          account_id      INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
          owner           TEXT NOT NULL COLLATE NOCASE,
          name            TEXT NOT NULL COLLATE NOCASE,
          uid_validity    INTEGER NOT NULL,
          uid_next        INTEGER NOT NULL DEFAULT 1,
          highest_modseq  INTEGER NOT NULL DEFAULT 1,
          special_use     INTEGER NOT NULL DEFAULT 0,
          subscribed      INTEGER NOT NULL DEFAULT 1,
          UNIQUE(account_id, name)
        );

        CREATE TABLE IF NOT EXISTS mailbox_entries (
          mailbox_id    INTEGER NOT NULL REFERENCES mailboxes(id) ON DELETE CASCADE,
          uid           INTEGER NOT NULL,
          message_id    TEXT NOT NULL REFERENCES messages(id),
          flags         INTEGER NOT NULL DEFAULT 0,
          keywords      TEXT NULL,
          internal_date INTEGER NOT NULL,
          modseq        INTEGER NOT NULL,
          size          INTEGER NOT NULL,
          PRIMARY KEY (mailbox_id, uid)
        ) WITHOUT ROWID;
        CREATE INDEX IF NOT EXISTS ix_entries_modseq ON mailbox_entries(mailbox_id, modseq);
        CREATE INDEX IF NOT EXISTS ix_entries_message ON mailbox_entries(message_id);

        CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
          subject, from_addr, to_addrs, body_text, tokenize = 'unicode61 remove_diacritics 2'
        );

        CREATE TABLE IF NOT EXISTS blobs (
          key  TEXT PRIMARY KEY,
          data BLOB NOT NULL
        );
        """;
}
