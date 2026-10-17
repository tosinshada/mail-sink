using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Mailsink.Core.Accounts;
using Mailsink.Core.Domain;
using Mailsink.Core.Storage;

namespace Mailsink.Api.Endpoints;

/// <summary>Accounts and their IMAP mailboxes. Mailbox names in the path are URL-encoded (<c>Archive%2F2026</c>).</summary>
internal static class AccountEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var accounts = api.MapGroup("/accounts").WithTags("Accounts");
        accounts.MapGet("", ListAsync).WithSummary("List accounts");
        accounts.MapPost("", CreateAsync).WithSummary("Create an account (registry mode) or set its password");
        accounts.MapDelete("/{address}", DeleteAsync).WithSummary("Delete an account and its mailboxes");
        accounts.MapPut("/{address}/password", SetPasswordAsync).WithSummary("Set or clear an account password");

        var mailboxes = accounts.MapGroup("/{address}/mailboxes").WithTags("Mailboxes");
        mailboxes.MapGet("", ListMailboxesAsync).WithSummary("Per-user folders (the IMAP view)");
        mailboxes.MapPost("", CreateMailboxAsync).WithSummary("Create a folder");
        mailboxes.MapDelete("/{name}", DeleteMailboxAsync).WithSummary("Delete a folder");
        mailboxes.MapPut("/{name}", RenameMailboxAsync).WithSummary("Rename a folder");
        mailboxes.MapGet("/{name}/messages", MessagesAsync).WithSummary("Messages in a folder, newest first");
        mailboxes.MapPost("/{name}/move", MoveAsync).WithSummary("Move (or copy) messages to another folder; IMAP clients see it live");
        mailboxes.MapPut("/{name}/flags", FlagsAsync).WithSummary("Add or remove IMAP flags (\\Seen, \\Flagged, keywords…)");
        mailboxes.MapPost("/{name}/expunge", ExpungeAsync).WithSummary("Permanently remove messages flagged \\Deleted");
    }

    private static async Task<IResult> ListAsync(IMailStore store, CancellationToken ct)
    {
        var result = new List<AccountDto>();
        foreach (var account in await store.ListAccountsAsync(ct))
        {
            var boxes = await store.ListMailboxesAsync(account.Address, ct);
            var messages = 0;
            foreach (var box in boxes)
            {
                messages += (await store.GetMailboxStatsAsync(box.Id, ct)).Messages;
            }

            result.Add(new AccountDto(account.Address, account.PasswordHash is not null, boxes.Count, messages, account.CreatedAt, account.LastLoginAt, account.LastLoginProtocol));
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> CreateAsync(CreateAccountRequest request, IAccountService accounts, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Address))
        {
            return Results.BadRequest(new ErrorResponse("address is required"));
        }

        var account = await accounts.CreateUserAsync(request.Address, request.Password, ct);
        return Results.Created($"/api/v1/accounts/{Uri.EscapeDataString(account.Address)}",
            new AccountDto(account.Address, account.PasswordHash is not null, 0, 0, account.CreatedAt, account.LastLoginAt, account.LastLoginProtocol));
    }

    private static async Task<IResult> DeleteAsync(string address, IMailStore store, IAccountService accounts, CancellationToken ct) =>
        await store.DeleteAccountAsync(accounts.Normalize(Decode(address)), ct) ? Results.NoContent() : Results.NotFound(new ErrorResponse("Account not found"));

    private static async Task<IResult> SetPasswordAsync(string address, PasswordRequest request, IMailStore store, IAccountService accounts, CancellationToken ct)
    {
        var normalized = accounts.Normalize(Decode(address));
        if (await store.FindAccountAsync(normalized, ct) is null)
        {
            return Results.NotFound(new ErrorResponse("Account not found"));
        }

        await accounts.SetPasswordAsync(normalized, request.Password, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ListMailboxesAsync(string address, IMailStore store, IAccountService accounts, CancellationToken ct)
    {
        var owner = accounts.Normalize(Decode(address));
        var result = new List<MailboxDto>();
        foreach (var box in await store.ListMailboxesAsync(owner, ct))
        {
            result.Add(Mapping.ToMailbox(box, await store.GetMailboxStatsAsync(box.Id, ct)));
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> CreateMailboxAsync(string address, CreateMailboxRequest request, IMailStore store, IAccountService accounts, CancellationToken ct)
    {
        var owner = accounts.Normalize(Decode(address));
        await accounts.GetOrCreateAsync(owner, ct);
        try
        {
            var box = await store.CreateMailboxAsync(owner, request.Name, SpecialUse.None, ct);
            return Results.Created($"/api/v1/accounts/{Uri.EscapeDataString(owner)}/mailboxes/{Uri.EscapeDataString(box.Name)}",
                Mapping.ToMailbox(box, new MailboxStats(0, 0, 0)));
        }
        catch (MailStoreException ex)
        {
            return Results.Conflict(new ErrorResponse(ex.Message));
        }
    }

    private static async Task<IResult> DeleteMailboxAsync(string address, string name, IMailStore store, IAccountService accounts, CancellationToken ct)
    {
        var box = await FindAsync(store, accounts, address, name, ct);
        if (box is null)
        {
            return Results.NotFound(new ErrorResponse("Mailbox not found"));
        }

        try
        {
            await store.DeleteMailboxAsync(box.Id, ct);
            return Results.NoContent();
        }
        catch (MailStoreException ex)
        {
            return Results.Conflict(new ErrorResponse(ex.Message));
        }
    }

    private static async Task<IResult> RenameMailboxAsync(string address, string name, RenameMailboxRequest request, IMailStore store, IAccountService accounts, CancellationToken ct)
    {
        var box = await FindAsync(store, accounts, address, name, ct);
        if (box is null)
        {
            return Results.NotFound(new ErrorResponse("Mailbox not found"));
        }

        try
        {
            await store.RenameMailboxAsync(box.Id, request.NewName, ct);
            return Results.NoContent();
        }
        catch (MailStoreException ex)
        {
            return Results.Conflict(new ErrorResponse(ex.Message));
        }
    }

    private static async Task<IResult> MessagesAsync(string address, string name, int? start, int? limit, IMailStore store, IAccountService accounts, CancellationToken ct)
    {
        var box = await FindAsync(store, accounts, address, name, ct);
        if (box is null)
        {
            return Results.NotFound(new ErrorResponse("Mailbox not found"));
        }

        var entries = (await store.GetEntriesAsync(box.Id, ct)).OrderByDescending(e => e.Uid).ToList();
        var skip = Math.Max(0, start ?? 0);
        var take = Math.Clamp(limit ?? 50, 1, 1000);
        var page = entries.Skip(skip).Take(take).ToList();
        var messages = await store.GetMessagesAsync([.. page.Select(e => e.Message).Distinct()], ct);
        var items = page.Where(e => messages.ContainsKey(e.Message))
            .Select(e => new MailboxMessageDto(e.Uid, Mapping.FlagNamesOf(e), e.InternalDate, e.Size, Mapping.ToSummary(messages[e.Message])))
            .ToList();
        var stats = await store.GetMailboxStatsAsync(box.Id, ct);
        return Results.Ok(new MailboxMessagesResponse(Mapping.ToMailbox(box, stats), entries.Count, skip, take, items));
    }

    private static async Task<IResult> MoveAsync(string address, string name, MoveRequest request, IMailStore store, IAccountService accounts, CancellationToken ct)
    {
        var box = await FindAsync(store, accounts, address, name, ct);
        var target = box is null ? null : await store.GetMailboxAsync(box.Owner, request.Target, ct);
        if (box is null || target is null)
        {
            return Results.NotFound(new ErrorResponse("Mailbox not found"));
        }

        var result = await store.CopyAsync(box.Id, request.Uids, target.Id, move: !request.Copy, ct);
        return Results.Ok(result.DestinationUids);
    }

    private static async Task<IResult> FlagsAsync(string address, string name, FlagsRequest request, IMailStore store, IAccountService accounts, CancellationToken ct)
    {
        var box = await FindAsync(store, accounts, address, name, ct);
        if (box is null)
        {
            return Results.NotFound(new ErrorResponse("Mailbox not found"));
        }

        if (request.Add is { Count: > 0 } add)
        {
            var (flags, keywords) = SplitFlags(add);
            await store.StoreFlagsAsync(box.Id, request.Uids, FlagOperation.Add, flags, keywords, ct);
        }

        if (request.Remove is { Count: > 0 } remove)
        {
            var (flags, keywords) = SplitFlags(remove);
            await store.StoreFlagsAsync(box.Id, request.Uids, FlagOperation.Remove, flags, keywords, ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ExpungeAsync(string address, string name, IMailStore store, IAccountService accounts, CancellationToken ct)
    {
        var box = await FindAsync(store, accounts, address, name, ct);
        if (box is null)
        {
            return Results.NotFound(new ErrorResponse("Mailbox not found"));
        }

        return Results.Ok(await store.ExpungeAsync(box.Id, null, ct));
    }

    private static (SystemFlags, List<string>) SplitFlags(IEnumerable<string> names)
    {
        var flags = SystemFlags.None;
        var keywords = new List<string>();
        foreach (var name in names)
        {
            if (FlagNames.TryParseSystem(name, out var flag))
            {
                flags |= flag & ~SystemFlags.Recent;
            }
            else if (!name.StartsWith('\\'))
            {
                keywords.Add(name);
            }
        }

        return (flags, keywords);
    }

    private static Task<Mailbox?> FindAsync(IMailStore store, IAccountService accounts, string address, string name, CancellationToken ct) =>
        store.GetMailboxAsync(accounts.Normalize(Decode(address)), Decode(name), ct);

    // Route values keep %2F encoded; mailbox names use '/' as the hierarchy delimiter.
    private static string Decode(string value) => Uri.UnescapeDataString(value);
}
