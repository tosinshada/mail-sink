using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using MimeKit;
using Mailsink.Core.Domain;

namespace Mailsink.Core.Storage;

public static class StoreHelpers
{
    private static long s_lastUidValidity;

    public static string ComputeBlobKey(ReadOnlySpan<byte> raw) => Convert.ToHexStringLower(SHA256.HashData(raw));

    /// <summary>A UIDVALIDITY that is unique per mailbox creation within this process and increases over time.</summary>
    public static uint NewUidValidity()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        while (true)
        {
            var last = Interlocked.Read(ref s_lastUidValidity);
            var next = Math.Max(now, last + 1);
            if (Interlocked.CompareExchange(ref s_lastUidValidity, next, last) == last)
            {
                return (uint)next;
            }
        }
    }

    public static bool IsInbox(string name) => name.Equals("INBOX", StringComparison.OrdinalIgnoreCase);

    /// <summary>Validates a mailbox name (UTF-8, <c>/</c>-delimited) and canonicalizes INBOX.</summary>
    public static string NormalizeMailboxName(string name)
    {
        var trimmed = name.Trim().Trim('/');
        if (trimmed.Length == 0 || trimmed.Length > 512)
        {
            throw new MailStoreException("Invalid mailbox name");
        }

        if (trimmed.Contains("//", StringComparison.Ordinal) || trimmed.Any(c => c < 32 || c is '*' or '%'))
        {
            throw new MailStoreException("Invalid mailbox name");
        }

        if (IsInbox(trimmed))
        {
            return "INBOX";
        }

        if (trimmed.StartsWith("INBOX/", StringComparison.OrdinalIgnoreCase))
        {
            return "INBOX" + trimmed[5..];
        }

        return trimmed;
    }

    public static bool IsChildOf(string name, string parent) =>
        name.Length > parent.Length + 1 && name.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);

    public static SystemFlags ApplyFlags(SystemFlags current, FlagOperation op, SystemFlags flags) => op switch
    {
        FlagOperation.Add => current | flags,
        FlagOperation.Remove => current & ~flags,
        _ => (current & SystemFlags.Recent) | flags,
    };

    public static IReadOnlyList<string> ApplyKeywords(IReadOnlyList<string> current, FlagOperation op, IReadOnlyCollection<string> keywords) => op switch
    {
        FlagOperation.Add => [.. current.Union(keywords, StringComparer.OrdinalIgnoreCase)],
        FlagOperation.Remove => [.. current.Where(k => !keywords.Contains(k, StringComparer.OrdinalIgnoreCase))],
        _ => [.. keywords.Distinct(StringComparer.OrdinalIgnoreCase)],
    };
}

/// <summary>Size-bounded cache of parsed <see cref="MimeMessage"/>s, used by FETCH sections and the HTML view.</summary>
public sealed class ParsedMessageCache : IDisposable
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 64L * 1024 * 1024 });

    public async Task<MimeMessage> GetOrLoadAsync(MessageId id, Func<Task<byte[]>> loadRaw, CancellationToken ct)
    {
        if (_cache.TryGetValue(id, out MimeMessage? cached) && cached is not null)
        {
            return cached;
        }

        var raw = await loadRaw();
        using var stream = new MemoryStream(raw, writable: false);
        var message = await MimeMessage.LoadAsync(stream, ct);
        _cache.Set(id, message, new MemoryCacheEntryOptions { Size = Math.Max(1, raw.Length), SlidingExpiration = TimeSpan.FromMinutes(10) });
        return message;
    }

    public void Remove(MessageId id) => _cache.Remove(id);

    public void Clear() => _cache.Compact(1.0);

    public void Dispose() => _cache.Dispose();
}
