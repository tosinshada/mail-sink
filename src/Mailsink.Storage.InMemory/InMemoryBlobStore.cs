using System.Collections.Concurrent;
using Mailsink.Core.Domain;
using Mailsink.Core.Storage;

namespace Mailsink.Storage.InMemory;

public sealed class InMemoryBlobStore : IBlobStore
{
    private readonly ConcurrentDictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    public Task PutAsync(string key, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        _blobs.TryAdd(key, data.ToArray());
        return Task.CompletedTask;
    }

    public Task<byte[]> GetAsync(string key, CancellationToken ct) =>
        _blobs.TryGetValue(key, out var data)
            ? Task.FromResult(data)
            : Task.FromException<byte[]>(new MailStoreException($"Blob {key} not found"));

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        _blobs.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task DeleteAllAsync(CancellationToken ct)
    {
        _blobs.Clear();
        return Task.CompletedTask;
    }
}
