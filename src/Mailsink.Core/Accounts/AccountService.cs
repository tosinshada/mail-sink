using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mailsink.Core.Domain;
using Mailsink.Core.Events;
using Mailsink.Core.Options;
using Mailsink.Core.Storage;

namespace Mailsink.Core.Accounts;

public sealed record AuthResult(bool Success, string? Address, string? Error = null)
{
    public static AuthResult Ok(string address) => new(true, address);

    public static AuthResult Fail(string error) => new(false, null, error);
}

public interface IAccountService
{
    AuthMode Mode { get; }

    /// <summary>Normalizes a login name to an address: bare <c>alice</c> becomes <c>alice@{DefaultDomain}</c>.</summary>
    string Normalize(string username);

    ValueTask<AuthResult> AuthenticateAsync(string username, string password, string protocol, CancellationToken ct);

    /// <summary>XOAUTH2 / OAUTHBEARER: any token in accept-any mode, the password in registry mode.</summary>
    ValueTask<AuthResult> AuthenticateTokenAsync(string username, string token, string protocol, CancellationToken ct);

    ValueTask<Account> GetOrCreateAsync(string address, CancellationToken ct);

    Task<Account> CreateUserAsync(string address, string? password, CancellationToken ct);

    Task SetPasswordAsync(string address, string? password, CancellationToken ct);

    Task SeedAsync(CancellationToken ct);
}

public sealed class AccountService(
    IMailStore store,
    IMailEventBus bus,
    IOptionsMonitor<MailsinkOptions> options,
    ILogger<AccountService> logger) : IAccountService
{
    public static readonly IReadOnlyList<(string Name, SpecialUse Use)> DefaultMailboxes =
    [
        ("INBOX", SpecialUse.None),
        ("Sent", SpecialUse.Sent),
        ("Drafts", SpecialUse.Drafts),
        ("Trash", SpecialUse.Trash),
    ];

    private readonly SemaphoreSlim _createGate = new(1, 1);

    public AuthMode Mode => options.CurrentValue.Auth.Mode;

    public string Normalize(string username)
    {
        var trimmed = username.Trim();
        return trimmed.Contains('@') ? trimmed.ToLowerInvariant() : $"{trimmed}@{options.CurrentValue.DefaultDomain}".ToLowerInvariant();
    }

    public async ValueTask<AuthResult> AuthenticateAsync(string username, string password, string protocol, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return await FailAsync("Empty username", ct);
        }

        var address = Normalize(username);
        if (Mode == AuthMode.AcceptAny)
        {
            await GetOrCreateAsync(address, ct);
            await store.RecordLoginAsync(address, protocol, ct);
            return AuthResult.Ok(address);
        }

        var account = await store.FindAccountAsync(address, ct);
        if (account?.PasswordHash is null || !PasswordHasher.Verify(password, account.PasswordHash))
        {
            logger.LogInformation("{Protocol} authentication failed for {Address}", protocol, address);
            return await FailAsync("Invalid credentials", ct);
        }

        await store.RecordLoginAsync(address, protocol, ct);
        return AuthResult.Ok(address);
    }

    public ValueTask<AuthResult> AuthenticateTokenAsync(string username, string token, string protocol, CancellationToken ct) =>
        AuthenticateAsync(username, token, protocol, ct);

    public async ValueTask<Account> GetOrCreateAsync(string address, CancellationToken ct)
    {
        address = Normalize(address);
        var existing = await store.FindAccountAsync(address, ct);
        if (existing is not null)
        {
            return existing;
        }

        return await CreateCoreAsync(address, null, ct);
    }

    public async Task<Account> CreateUserAsync(string address, string? password, CancellationToken ct)
    {
        address = Normalize(address);
        var hash = password is null ? null : PasswordHasher.Hash(password);
        var existing = await store.FindAccountAsync(address, ct);
        if (existing is not null)
        {
            await store.SetPasswordAsync(address, hash, ct);
            return (await store.FindAccountAsync(address, ct))!;
        }

        return await CreateCoreAsync(address, hash, ct);
    }

    public Task SetPasswordAsync(string address, string? password, CancellationToken ct) =>
        store.SetPasswordAsync(Normalize(address), password is null ? null : PasswordHasher.Hash(password), ct);

    public async Task SeedAsync(CancellationToken ct)
    {
        foreach (var user in options.CurrentValue.Auth.Users)
        {
            if (!string.IsNullOrWhiteSpace(user.Address))
            {
                await CreateUserAsync(user.Address, user.Password, ct);
            }
        }
    }

    private async Task<Account> CreateCoreAsync(string address, byte[]? hash, CancellationToken ct)
    {
        await _createGate.WaitAsync(ct);
        try
        {
            var account = await store.FindAccountAsync(address, ct) ?? await store.CreateAccountAsync(address, hash, ct);
            var existing = (await store.ListMailboxesAsync(address, ct)).Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, use) in DefaultMailboxes)
            {
                if (!existing.Contains(name))
                {
                    await store.CreateMailboxAsync(address, name, use, ct);
                }
            }

            if (options.CurrentValue.Imap.OutboxSubmission && !existing.Contains("Outbox"))
            {
                await store.CreateMailboxAsync(address, "Outbox", SpecialUse.Outbox, ct);
            }

            bus.Publish(new AccountsChangedEvent());
            return account;
        }
        finally
        {
            _createGate.Release();
        }
    }

    private async ValueTask<AuthResult> FailAsync(string error, CancellationToken ct)
    {
        var delay = options.CurrentValue.Auth.FailureDelay;
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, ct);
        }

        return AuthResult.Fail(error);
    }
}

/// <summary>PBKDF2-SHA256 password hashes: <c>[version:1][salt:16][hash:32]</c>.</summary>
public static class PasswordHasher
{
    private const byte Version = 1;
    private const int Iterations = 10_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;

    public static byte[] Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return [Version, .. salt, .. hash];
    }

    public static bool Verify(string password, byte[] stored)
    {
        if (stored.Length != 1 + SaltSize + HashSize || stored[0] != Version)
        {
            return false;
        }

        var salt = stored.AsSpan(1, SaltSize);
        var expected = stored.AsSpan(1 + SaltSize);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
