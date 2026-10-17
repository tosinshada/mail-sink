using Microsoft.Data.Sqlite;
using Mailsink.Core.Domain;
using Mailsink.Core.Storage;

namespace Mailsink.Storage.Sqlite;

/// <summary><c>{dataDir}/blobs/ab/cd/abcdef….eml</c>, content-addressed, written via temp file and atomic rename.</summary>
public sealed class FileBlobStore(string root) : IBlobStore
{
    public async Task PutAsync(string key, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var path = PathFor(key);
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temp, data.ToArray(), ct);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(temp);
        }
    }

    public async Task<byte[]> GetAsync(string key, CancellationToken ct)
    {
        var path = PathFor(key);
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : throw new MailStoreException($"Blob {key} not found");
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAllAsync(CancellationToken ct)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private string PathFor(string key)
    {
        if (key.Length < 8 || key.Any(c => !char.IsAsciiHexDigit(c)))
        {
            throw new MailStoreException("Invalid blob key");
        }

        return Path.Combine(root, key[..2], key[2..4], key + ".eml");
    }
}

/// <summary>Single-file mode: blobs live in a table of the SQLite database.</summary>
public sealed class SqliteBlobStore(SqliteDatabase database) : IBlobStore
{
    public async Task PutAsync(string key, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO blobs(key, data) VALUES ($key, $data)";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.Add("$data", SqliteType.Blob).Value = data.ToArray();
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<byte[]> GetAsync(string key, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM blobs WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(ct) as byte[] ?? throw new MailStoreException($"Blob {key} not found");
    }

    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM blobs WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAllAsync(CancellationToken ct)
    {
        await using var connection = await database.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM blobs";
        await command.ExecuteNonQueryAsync(ct);
    }
}
