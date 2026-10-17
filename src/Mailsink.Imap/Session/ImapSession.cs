using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Mailsink.Core.Accounts;
using Mailsink.Core.Delivery;
using Mailsink.Core.Diagnostics;
using Mailsink.Core.Domain;
using Mailsink.Core.Events;
using Mailsink.Core.Networking;
using Mailsink.Core.Options;
using Mailsink.Core.Storage;
using Mailsink.Imap.Commands;
using Mailsink.Imap.Fetch;
using Mailsink.Imap.Protocol;

namespace Mailsink.Imap.Session;

[Flags]
internal enum ImapState
{
    NotAuthenticated = 1,
    Authenticated = 2,
    Selected = 4,
    Logout = 8,
    Any = NotAuthenticated | Authenticated | Selected,
    AuthenticatedOrSelected = Authenticated | Selected,
}

internal enum ImapStatus { Ok, No, Bad }

internal sealed record ImapResult(ImapStatus Status, string Text, string? Code = null)
{
    public static ImapResult Ok(string text, string? code = null) => new(ImapStatus.Ok, text, code);

    public static ImapResult No(string text, string? code = null) => new(ImapStatus.No, text, code);

    public static ImapResult Bad(string text, string? code = null) => new(ImapStatus.Bad, text, code);
}

/// <summary>One IMAP command handler. Handlers write untagged data and return the tagged result.</summary>
internal interface IImapCommandHandler
{
    string Name { get; }

    ImapState AllowedStates { get; }

    bool SupportsUidPrefix => false;

    ValueTask<ImapResult> ExecuteAsync(ImapSession session, ImapCommand command, CancellationToken ct);
}

/// <summary>Services shared by every IMAP session.</summary>
public sealed class ImapServices(
    IMailStore store,
    IAccountService accounts,
    IMailEventBus bus,
    SubmissionService submission,
    TranscriptStore transcripts,
    MetricsCollector metrics,
    ImapMessageCache messageCache)
{
    internal IMailStore Store => store;
    internal IAccountService Accounts => accounts;
    internal IMailEventBus Bus => bus;
    internal SubmissionService Submission => submission;
    internal TranscriptStore Transcripts => transcripts;
    internal MetricsCollector Metrics => metrics;
    internal ImapMessageCache MessageCache => messageCache;
    internal RecentTracker Recent { get; } = new();
}

/// <summary>
/// One IMAP connection: command loop, state machine, the selected-mailbox snapshot, and change notification at
/// safe points (RFC 3501 §7.4.1 / RFC 9051 §7.5.1).
/// </summary>
internal sealed class ImapSession : IAsyncDisposable
{
    private readonly ProtocolConnection _connection;
    private readonly FrozenDictionary<string, IImapCommandHandler> _handlers;
    private readonly List<EntriesExpungedEvent> _deferredExpunges = [];
    private IMailEventSubscription? _subscription;
    private int _unannouncedExists;

    public ImapSession(ProtocolConnection connection, ImapServices services, MailsinkOptions options,
        FrozenDictionary<string, IImapCommandHandler> handlers, ILogger logger)
    {
        _connection = connection;
        Services = services;
        Options = options;
        _handlers = handlers;
        Logger = logger;
        Transcript = services.Transcripts.Start("imap", connection.ClientIp);
        Transcript.Secure = connection.IsSecure;
    }

    public ImapServices Services { get; }

    public MailsinkOptions Options { get; }

    public ILogger Logger { get; }

    public SessionTranscript Transcript { get; }

    public ProtocolConnection Connection => _connection;

    public ImapState State { get; private set; } = ImapState.NotAuthenticated;

    public string? User { get; private set; }

    public SelectedMailboxView? Selected { get; private set; }

    public string? CurrentTag { get; private set; }

    public HashSet<string> Enabled { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool StartTlsAvailable => !_connection.IsSecure && _connection.CanStartTls;

    public bool LoginDisabled => Options.Auth.RequireTls && !_connection.IsSecure;

    public string Capabilities
    {
        get
        {
            var caps = new List<string> { "IMAP4rev1" };
            if (Options.Imap.EnableRev2)
            {
                caps.Add("IMAP4rev2");
            }

            caps.AddRange(["LITERAL+", "SASL-IR", "ID", "ENABLE", "IDLE"]);
            if (State == ImapState.NotAuthenticated)
            {
                if (StartTlsAvailable)
                {
                    caps.Add("STARTTLS");
                }

                if (LoginDisabled)
                {
                    caps.Add("LOGINDISABLED");
                }
                else
                {
                    caps.AddRange(["AUTH=PLAIN", "AUTH=LOGIN", "AUTH=XOAUTH2"]);
                }
            }
            else
            {
                caps.AddRange(["NAMESPACE", "UIDPLUS", "MOVE", "SPECIAL-USE", "UNSELECT", "CHILDREN", "LIST-EXTENDED", "LIST-STATUS", "ESEARCH"]);
            }

            return string.Join(' ', caps);
        }
    }

    public async Task RunAsync(CancellationToken stopping)
    {
        using var _ = Services.Metrics.TrackSession("imap");
        try
        {
            if (Options.Imap.PreAuth && Options.Auth.Mode == AuthMode.AcceptAny)
            {
                await LoginAsync("preauth", stopping);
                await WriteLineAsync($"* PREAUTH [CAPABILITY {Capabilities}] Mailsink ready, logged in as {User}", stopping);
            }
            else
            {
                await WriteLineAsync($"* OK [CAPABILITY {Capabilities}] Mailsink ready", stopping);
            }

            while (State != ImapState.Logout && !stopping.IsCancellationRequested)
            {
                var buffer = await ReadCommandAsync(stopping);
                if (buffer is null)
                {
                    break;
                }

                await ExecuteAsync(buffer, stopping);
            }

            if (stopping.IsCancellationRequested)
            {
                await TryWriteLineAsync("* BYE Server shutting down");
            }
        }
        catch (TimeoutException)
        {
            await TryWriteLineAsync("* BYE Autologout; idle for too long");
        }
        catch (LineTooLongException)
        {
            await TryWriteLineAsync("* BYE Line too long");
        }
        catch (LiteralTooLargeException)
        {
            await TryWriteLineAsync("* BYE Literal too large");
        }
        finally
        {
            Transcript.End();
        }
    }

    private async Task ExecuteAsync(byte[] buffer, CancellationToken ct)
    {
        ImapCommand command;
        try
        {
            command = ImapParser.Parse(buffer);
        }
        catch (ImapParseException ex)
        {
            LogClient(buffer, null);
            await WriteLineAsync(ex.Tag is null ? $"* BAD {ex.Message}" : $"{ex.Tag} BAD {ex.Message}", ct);
            return;
        }

        LogClient(buffer, command);
        CurrentTag = command.Tag;
        var started = Stopwatch.GetTimestamp();
        ImapResult result;
        if (!_handlers.TryGetValue(command.Name, out var handler))
        {
            result = ImapResult.Bad("Unknown command");
        }
        else if ((handler.AllowedStates & State) == 0)
        {
            result = ImapResult.Bad($"{command.Name} not allowed in this state");
        }
        else if (command.IsUid && !handler.SupportsUidPrefix)
        {
            result = ImapResult.Bad($"UID {command.Name} not supported");
        }
        else
        {
            try
            {
                result = await handler.ExecuteAsync(this, command, ct);
            }
            catch (ImapParseException ex)
            {
                result = ImapResult.Bad(ex.Message);
            }
            catch (MailStoreException ex)
            {
                result = ImapResult.No(ex.Message);
            }
        }

        Services.Metrics.RecordCommand(command.Name, Stopwatch.GetElapsedTime(started));

        // FETCH, STORE and SEARCH (non-UID) must not cause EXPUNGE responses.
        var allowExpunge = command.IsUid || command.Name is not ("FETCH" or "STORE" or "SEARCH");
        if (result.Status != ImapStatus.Bad)
        {
            await FlushUpdatesAsync(allowExpunge, ct);
        }

        if (ReferenceEquals(result, SkipTagged.Instance))
        {
            CurrentTag = null;
            return;
        }

        var code = result.Code is null ? "" : $"[{result.Code}] ";
        var status = result.Status switch { ImapStatus.Ok => "OK", ImapStatus.No => "NO", _ => "BAD" };
        await WriteLineAsync($"{command.Tag} {status} {code}{result.Text}", ct);
        CurrentTag = null;
    }

    // ------------------------------------------------------------------ state transitions

    public async Task LoginAsync(string address, CancellationToken ct)
    {
        User = Services.Accounts.Normalize(address);
        Transcript.User = User;
        await Services.Accounts.GetOrCreateAsync(User, ct);
        State = ImapState.Authenticated;
        _subscription = Services.Bus.Subscribe();
    }

    public async Task SelectAsync(Mailbox mailbox, bool readOnly, CancellationToken ct)
    {
        Unselect();

        // Drain stale events first, then snapshot: later events are reconciled against the snapshot.
        while (_subscription!.Reader.TryRead(out _))
        {
        }

        var entries = await Services.Store.GetEntriesAsync(mailbox.Id, ct);
        var fresh = await Services.Store.GetMailboxAsync(mailbox.Id, ct) ?? mailbox;
        var recentAbove = readOnly ? uint.MaxValue : Services.Recent.ClaimUpTo(mailbox.Id, fresh.UidNext);
        Selected = new SelectedMailboxView(fresh, entries, readOnly, recentAbove);
        State = ImapState.Selected;
    }

    public void Unselect()
    {
        Selected = null;
        _deferredExpunges.Clear();
        _unannouncedExists = 0;
        if (State == ImapState.Selected)
        {
            State = ImapState.Authenticated;
        }
    }

    public void Logout() => State = ImapState.Logout;

    // ------------------------------------------------------------------ change notification

    /// <summary>
    /// Sends EXISTS/RECENT/FETCH FLAGS/EXPUNGE for changes made by other sessions (and by this one through other
    /// paths). Expunges are held back when <paramref name="allowExpunge"/> is false.
    /// </summary>
    public async Task FlushUpdatesAsync(bool allowExpunge, CancellationToken ct)
    {
        var view = Selected;
        if (_subscription is null)
        {
            return;
        }

        if (view is null)
        {
            // Nothing selected: these changes are not reportable, drop them.
            while (_subscription.Reader.TryRead(out _))
            {
            }

            return;
        }

        var w = new ImapWriter();
        if (allowExpunge)
        {
            foreach (var deferred in _deferredExpunges)
            {
                WriteExpunges(w, view, deferred);
            }

            _deferredExpunges.Clear();
        }

        while (_subscription.Reader.TryRead(out var e))
        {
            switch (e)
            {
                case EntryAddedEvent added when added.Entry.Mailbox == view.Id:
                    if (!view.Contains(added.Entry.Uid) && added.Entry.Uid > view.MaxUid)
                    {
                        var recent = !view.ReadOnly && Services.Recent.Claim(view.Id, added.Entry.Uid);
                        view.Add(added.Entry, recent);
                        _unannouncedExists++;
                    }

                    break;
                case EntryFlagsChangedEvent changed when changed.Entry.Mailbox == view.Id:
                    if (view.Update(changed.Entry))
                    {
                        AnnounceExists(w, view);
                        var msn = view.MsnOf(changed.Entry.Uid);
                        w.Untagged().Number(msn).Raw(" FETCH (UID ").Number(changed.Entry.Uid).Raw(" FLAGS ")
                            .Flags(changed.Entry.Flags, changed.Entry.Keywords, view.IsRecent(changed.Entry.Uid)).Raw(")").Line();
                    }

                    break;
                case EntriesExpungedEvent expunged when expunged.Mailbox == view.Id:
                    if (allowExpunge)
                    {
                        AnnounceExists(w, view);
                        WriteExpunges(w, view, expunged);
                    }
                    else
                    {
                        _deferredExpunges.Add(expunged);
                    }

                    break;
            }
        }

        AnnounceExists(w, view);
        if (w.Length > 0)
        {
            await SendAsync(w, ct);
        }
    }

    private void AnnounceExists(ImapWriter w, SelectedMailboxView view)
    {
        if (_unannouncedExists == 0)
        {
            return;
        }

        _unannouncedExists = 0;
        w.Untagged().Number(view.Count).Raw(" EXISTS").Line();
        w.Untagged().Number(view.RecentCount).Raw(" RECENT").Line();
    }

    private static void WriteExpunges(ImapWriter w, SelectedMailboxView view, EntriesExpungedEvent expunged)
    {
        foreach (var uid in expunged.Uids.Where(view.Contains).OrderByDescending(u => u))
        {
            w.Untagged().Number(view.MsnOf(uid)).Raw(" EXPUNGE").Line();
            view.Remove(uid);
        }
    }

    /// <summary>Waits for a bus event (IDLE). Completes false when the subscription ends.</summary>
    public ValueTask<bool> WaitForEventAsync(CancellationToken ct) =>
        _subscription?.Reader.WaitToReadAsync(ct) ?? ValueTask.FromResult(false);

    // ------------------------------------------------------------------ I/O

    public async Task<byte[]?> ReadCommandAsync(CancellationToken stopping)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(Options.Imap.IdleTimeout);
        try
        {
            var buffer = new List<byte>(256);
            while (true)
            {
                var line = await _connection.ReadLineAsync(Options.Imap.MaxLineLength, timeout.Token);
                if (line is null)
                {
                    return null;
                }

                buffer.AddRange(line);
                buffer.AddRange("\r\n"u8);
                if (!ImapParser.TryGetTrailingLiteral(line, out var length, out var synchronizing))
                {
                    return [.. buffer];
                }

                if (length > Options.Smtp.MaxMessageSize)
                {
                    throw new LiteralTooLargeException();
                }

                if (synchronizing)
                {
                    await WriteLineAsync("+ Ready for literal data", timeout.Token);
                }

                var data = await _connection.ReadExactAsync((int)length, timeout.Token);
                if (data is null)
                {
                    return null;
                }

                buffer.AddRange(data);
            }
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            throw new TimeoutException();
        }
    }

    public async Task<string?> ReadContinuationAsync(CancellationToken ct)
    {
        var line = await _connection.ReadLineAsync(Options.Imap.MaxLineLength, ct);
        Transcript.Client(line is null ? "(eof)" : "****");
        return line is null ? null : Encoding.UTF8.GetString(line);
    }

    public ValueTask<byte[]?> ReadLineAsync(CancellationToken ct) => _connection.ReadLineAsync(Options.Imap.MaxLineLength, ct);

    public async Task SendAsync(ImapWriter writer, CancellationToken ct)
    {
        LogServer(writer.Written.Span);
        _connection.Write(writer.Written.Span);
        await _connection.FlushAsync(ct);
        writer.Clear();
    }

    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        Transcript.Server(line);
        await _connection.WriteLineAsync(line, ct);
    }

    private async Task TryWriteLineAsync(string line)
    {
        try
        {
            await WriteLineAsync(line, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Peer went away.
        }
    }

    private void LogClient(byte[] buffer, ImapCommand? command)
    {
        var text = Encoding.UTF8.GetString(buffer, 0, Math.Min(buffer.Length, 400)).TrimEnd('\r', '\n');
        var firstLine = text.Split("\r\n")[0];
        if (command is { Name: "LOGIN" })
        {
            firstLine = $"{command.Tag} LOGIN {(command.ArgAt(0)?.AsString() ?? "")} ****";
        }
        else if (command is { Name: "AUTHENTICATE" } && command.Args.Count > 1)
        {
            firstLine = $"{command.Tag} AUTHENTICATE {command.Args[0]} ****";
        }
        else if (buffer.Length > firstLine.Length + 2)
        {
            firstLine += $" (+{buffer.Length - firstLine.Length - 2} bytes)";
        }

        Transcript.Client(firstLine);
    }

    private void LogServer(ReadOnlySpan<byte> data)
    {
        var text = Encoding.UTF8.GetString(data[..Math.Min(data.Length, 600)]);
        foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Take(8))
        {
            Transcript.Server(line.Length > 300 ? line[..300] + "…" : line);
        }
    }

    public ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class LiteralTooLargeException() : IOException("Literal too large");
