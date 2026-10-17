# Mailsink — Technical Implementation Document

> A .NET email testing server in the spirit of **Mailpit** (capture + web UI + API) and **GreenMail** (real multi-protocol mail server for tests: SMTP, IMAP, POP3), with first-class support for **sending** test mail, not only capturing it.

| | |
|---|---|
| Status | Draft v1 |
| Target runtime | .NET 10 (LTS) |
| Date | 2026-10-17 |

---

## 1. Goals and scope

### 1.1 What we are building

One self-contained server process (also usable as an in-process library in test suites) that:

1. **Captures** all mail sent to it over SMTP. Nothing is delivered to the real internet unless explicitly configured.
2. **Exposes captured mail** through:
   - a web UI (Mailpit-style inbox, HTML/text/raw/headers views, attachments, search),
   - a REST API and real-time push (WebSocket/SignalR),
   - **IMAP4rev1/IMAP4rev2** so real mail clients (Outlook, Thunderbird, Apple Mail, MailKit, JavaMail, Python `imaplib`) can log in and read, flag, move and delete mail,
   - POP3 (optional, low cost once the store exists).
3. **Sends test mail** in several ways:
   - SMTP submission (port 587 style, with AUTH): an authenticated client sends, the message lands in the sender's `Sent` folder **and** in each recipient's `INBOX` on the server.
   - IMAP `APPEND`: clients that save sent/draft copies over IMAP work properly.
   - Compose/send from the web UI and the REST API (`POST /api/v1/send`), including templated and bulk "load" sends.
4. Ships as a **NuGet test library** (`Mailsink.Testing`) with xUnit/NUnit/MSTest fixtures, like GreenMail's JUnit rule, plus a Docker image and a Testcontainers module.

> **A note on "IMAP for sending":** IMAP doesn't send mail. It reads and manages mailboxes, and `APPEND` stores a message in a folder. Sending always goes over SMTP. "Full send and receive" in this design means: authenticated SMTP submission, local delivery between mailboxes, and IMAP access to those mailboxes (including the `Sent` folder), all inside Mailsink. Together these give clients the full round trip.

### 1.2 Non-goals (v1)

- Any outbound delivery: Mailsink is fully local and never relays or "releases" mail to a real SMTP server.
- Production MTA features: MX lookup, queue retry and backoff, DKIM signing for real delivery, spam filtering, DMARC enforcement.
- Durability guarantees beyond "SQLite on disk".
- Clustering and multi-node.
- Full IMAP extension coverage (see §6.2 for the supported list).

### 1.3 Feature comparison

| Feature | Mailpit | GreenMail | **Mailsink** |
|---|---|---|---|
| SMTP capture | ✅ | ✅ | ✅ |
| SMTP AUTH / submission | accept-any | ✅ users | ✅ both modes |
| IMAP server | ❌ | ✅ | ✅ (rev1 + rev2 subset, IDLE, UIDPLUS, MOVE) |
| POP3 | ✅ | ✅ | ✅ (phase 3) |
| Web UI | ✅ | ❌ | ✅ |
| REST API | ✅ | partial | ✅ |
| Real-time push | ✅ WebSocket | ❌ | ✅ SignalR |
| Send from UI/API | partial (send API) | ❌ | ✅ |
| Relay/release upstream | ✅ | ❌ | ❌ (fully local by design) |
| In-process test library | ❌ | ✅ JUnit | ✅ xUnit/NUnit/MSTest |
| Chaos / fault injection | ✅ | ❌ | ✅ |
| HTML compatibility / link check | ✅ | ❌ | phase 4 |

---

## 2. High-level architecture

```
                         ┌───────────────────────────────────────────────────────────┐
                         │                  Mailsink.Server (Generic Host)            │
                         │                                                           │
 SMTP clients ──25/1025──►  SmtpListener (IHostedService)  ─┐                        │
 Submission ───587/1587──►  (SmtpServer lib + MimeKit)       │                        │
                         │                                   ▼                        │
 IMAP clients ─143/1143──►  ImapListener ──► ImapSession ─► IMailStore ◄── Retention  │
 IMAPS ────────993/1993──►  (Pipelines, custom parser)       ▲   │        (BG service)│
                         │                                   │   │                    │
 POP3 ─────────110/1110──►  Pop3Listener                     │   ▼                    │
                         │                              IMailEventBus (Channels)      │
 Browser / CI ───8025────►  ASP.NET Core: REST API ──────────┤   │                    │
                         │               SignalR hub ◄───────────┘                    │
                         │               Static SPA (web UI)                          │
                         │                                                           │
                         │  ChaosEngine (fault injection)                             │
                         └───────────────────────────────────────────────────────────┘
                                         │
                                         ▼
                              SQLite (metadata, FTS5)  +  blob store (.eml files)
```

### 2.1 Key design decisions

| Decision | Choice | Rationale |
|---|---|---|
| Host model | One `WebApplication` with protocol listeners as `IHostedService`s | One process, one DI container, shared store and event bus. Matches Mailpit's single binary. |
| SMTP server | [`SmtpServer`](https://github.com/cosullivan/SmtpServer) NuGet (MIT) | Mature, async, pluggable `IMessageStore`, `IMailboxFilter` and `IUserAuthenticator`. No reason to rewrite SMTP. |
| MIME parsing/building | **MimeKit** | The standard on .NET. Gives BODYSTRUCTURE-ready parsing, stream offsets for `BODY[section]`, and encoders. |
| SMTP/IMAP client | **MailKit** | Test client in the integration tests. Mailsink itself never connects to another mail server. |
| IMAP server | **Custom**, built on `System.IO.Pipelines` | There is no maintained, permissively licensed .NET IMAP server library. IMAP is the core of this project and needs full control. |
| Persistence | SQLite via `Microsoft.Data.Sqlite` + Dapper (or hand-written ADO.NET); raw `.eml` on disk or as a blob | Zero-config, file-portable, FTS5 for search, fast enough for test workloads. An in-memory provider handles unit tests and the testing library. |
| Real-time | SignalR | Built into ASP.NET Core, browser client available. |
| Web UI | Vite + React + TypeScript + Tailwind CSS + shadcn/ui (Radix) SPA served from `wwwroot` | Matches the minimal, dark-first dashboard style in `docs/sample-design.png` (see §11). A static SPA keeps the server AOT-friendly. |
| Native AOT | Target AOT-compatible code paths (no EF Core reflection, source-generated JSON) | Small Docker image, fast startup in CI. Keep it as a goal, not a hard v1 requirement. |

---

## 3. Solution layout

```
mailsink/
├─ Mailsink.sln
├─ src/
│  ├─ Mailsink.Core/              # Domain model, IMailStore, events, options. No I/O protocol code.
│  │  ├─ Domain/                  # Mailbox, Message, Flags, User, Uid types
│  │  ├─ Storage/                 # IMailStore, IBlobStore, query model
│  │  ├─ Delivery/                # LocalDeliveryAgent, address resolution, catch-all rules
│  │  └─ Events/                  # IMailEventBus, MessageAdded/FlagsChanged/Expunged
│  ├─ Mailsink.Storage.Sqlite/    # SQLite + FTS5 + file blob store implementation
│  ├─ Mailsink.Storage.InMemory/  # Fast, test-oriented implementation
│  ├─ Mailsink.Smtp/              # SmtpServer integration: store, filters, authenticator, chaos hooks
│  ├─ Mailsink.Imap/              # IMAP server: parser, commands, session, FETCH/SEARCH engines
│  ├─ Mailsink.Pop3/              # POP3 server
│  ├─ Mailsink.Api/               # Minimal API endpoints, SignalR hub, DTOs (source-gen JSON)
│  ├─ Mailsink.Server/            # Composition root: Program.cs, config, TLS, hosting
│  ├─ Mailsink.Web/               # SPA source (Vite). Build output copied to Server/wwwroot.
│  └─ Mailsink.Testing/           # NuGet: in-process server, fixtures, assertion helpers
├─ tests/
│  ├─ Mailsink.Core.Tests/
│  ├─ Mailsink.Imap.Tests/        # Parser and command unit tests, golden transcripts
│  ├─ Mailsink.Integration.Tests/ # MailKit-driven end-to-end SMTP/IMAP/POP3/API
│  └─ Mailsink.Conformance/       # Scripts to run Dovecot imaptest against the server
├─ docker/
│  └─ Dockerfile
└─ docs/
   └─ TECHNICAL_DESIGN.md
```

Dependency rule: `Core` depends on nothing. Protocol projects depend on `Core`. `Server` composes everything. `Testing` references `Server`'s composition helpers but not ASP.NET hosting when run "headless".

---

## 4. Domain model

### 4.1 Concepts

- **User / account**: a login identity (`alice@test.local`). Two modes (configurable):
  - `AuthMode.AcceptAny` (Mailpit-like): any username/password is accepted and the account is created on first use.
  - `AuthMode.Registry` (GreenMail-like): users are predefined in config or the API with passwords, and auth is checked.
- **Mailbox (folder)**: belongs to a user. Hierarchy delimiter `/`. Special-use attributes (`\Sent`, `\Drafts`, `\Trash`, `\Junk`, `\Archive`). Every user is auto-provisioned with `INBOX`, `Sent`, `Drafts`, `Trash`.
- **Message (content)**: immutable raw MIME bytes, stored once and content-addressed by SHA-256.
- **Mailbox entry**: a message *instance* in a mailbox, with its own UID, flags, internal date and modseq. One message content can appear in many mailboxes (fan-out to N recipients plus the sender's Sent folder) without being copied.
- **Catch-all view**: Mailpit-style "all captured mail" is a virtual view over every SMTP-received message, independent of per-user mailboxes. The web UI defaults to it.

### 4.2 Address-to-mailbox resolution (local delivery)

When a message arrives (SMTP or API send), `LocalDeliveryAgent` resolves each envelope `RCPT TO`:

1. Exact user match → that user's `INBOX`.
2. No match and `AutoCreateRecipients = true` (default) → create the user, then deliver to `INBOX`.
3. Plus-addressing (`bob+tag@x`) → user `bob@x`. Optionally maps to folder `tag` when `PlusAddressFolders = true`.
4. If the sender is authenticated (submission) and `SaveToSent = true` → also add an entry to the sender's `Sent` with `\Seen`.

Whatever happens, the message is always recorded in the **capture log** (the catch-all view) with its envelope (`MAIL FROM`, `RCPT TO` list, client IP, HELO, TLS state, auth identity). This is what tests assert against.

### 4.3 Core types (sketch)

```csharp
public readonly record struct MessageId(Guid Value);          // global capture id (API/UI)
public readonly record struct MailboxId(long Value);
public readonly record struct Uid(uint Value);                // IMAP UID, per mailbox

[Flags]
public enum SystemFlags : byte { None = 0, Seen = 1, Answered = 2, Flagged = 4, Deleted = 8, Draft = 16, Recent = 32 }

public sealed record Mailbox(
    MailboxId Id, string Owner, string Name, uint UidValidity,
    uint UidNext, ulong HighestModSeq, SpecialUse SpecialUse, bool Subscribed);

public sealed record MailboxEntry(
    MailboxId Mailbox, Uid Uid, MessageId Message, SystemFlags Flags,
    IReadOnlyList<string> Keywords, DateTimeOffset InternalDate, ulong ModSeq, long Size);

public sealed record CapturedMessage(
    MessageId Id, string BlobKey, long Size, DateTimeOffset ReceivedAt,
    Envelope SmtpEnvelope, ParsedSummary Summary /* subject, from, to, cc, snippet, hasAttachments, ... */,
    IReadOnlyList<string> Tags);
```

### 4.4 Store interface

```csharp
public interface IMailStore
{
    // Capture + delivery
    Task<CapturedMessage> CaptureAsync(Stream rawMime, Envelope envelope, CancellationToken ct);
    Task<MailboxEntry> AppendAsync(MailboxId box, MessageId msg, SystemFlags flags,
                                   IEnumerable<string> keywords, DateTimeOffset internalDate, CancellationToken ct);

    // Mailboxes
    Task<IReadOnlyList<Mailbox>> ListMailboxesAsync(string owner, CancellationToken ct);
    Task<Mailbox> CreateMailboxAsync(string owner, string name, CancellationToken ct);
    Task RenameMailboxAsync(MailboxId id, string newName, CancellationToken ct);
    Task DeleteMailboxAsync(MailboxId id, CancellationToken ct);

    // IMAP-facing entry operations (all atomic, all bump modseq + emit events)
    Task<IReadOnlyList<MailboxEntry>> GetEntriesAsync(MailboxId box, CancellationToken ct);
    Task<IReadOnlyList<MailboxEntry>> StoreFlagsAsync(MailboxId box, IReadOnlyCollection<Uid> uids,
                                   FlagOperation op, SystemFlags flags, IEnumerable<string> keywords,
                                   ulong? unchangedSince, CancellationToken ct);
    Task<CopyResult> CopyAsync(MailboxId from, IReadOnlyCollection<Uid> uids, MailboxId to, bool move, CancellationToken ct);
    Task<IReadOnlyList<Uid>> ExpungeAsync(MailboxId box, IReadOnlyCollection<Uid>? onlyUids, CancellationToken ct);

    // Content
    Task<Stream> OpenRawAsync(MessageId id, CancellationToken ct);
    Task<MimeMessage> LoadParsedAsync(MessageId id, CancellationToken ct);  // cached (see §8.3)

    // Search / API
    Task<PagedResult<CapturedMessage>> QueryCaptureAsync(CaptureQuery q, CancellationToken ct);
    Task DeleteCapturedAsync(IEnumerable<MessageId> ids, CancellationToken ct);
}
```

---

## 5. SMTP subsystem

### 5.1 Listeners

| Endpoint | Default port | Purpose | Auth | TLS |
|---|---|---|---|---|
| Capture | 1025 (25 in Docker) | Apps under test point here | optional (accept-any) | STARTTLS optional |
| Submission | 1587 (587) | Authenticated sending, Sent-folder copy | required | STARTTLS required (configurable) |
| SMTPS | 1465 (465) | Implicit TLS submission | required | implicit |

All three are `SmtpServer` endpoints in one `SmtpServer` instance, configured with `EndpointDefinitionBuilder`.

### 5.2 Integration points

```csharp
public sealed class MailsinkMessageStore(IMailStore store, LocalDeliveryAgent delivery,
                                        IChaosEngine chaos, IMailEventBus bus) : MessageStore
{
    public override async Task<SmtpResponse> SaveAsync(ISessionContext ctx, IMessageTransaction tx,
        ReadOnlySequence<byte> buffer, CancellationToken ct)
    {
        if (chaos.Evaluate(ChaosStage.Data, ctx, tx) is { } fault) return fault.ToSmtpResponse();

        var envelope = Envelope.From(ctx, tx);                        // MAIL FROM, RCPT TO, HELO, IP, auth, TLS
        await using var stream = new ReadOnlySequenceStream(buffer);  // no extra copy
        var captured = await store.CaptureAsync(stream, envelope, ct);
        await delivery.DeliverAsync(captured, envelope, ct);          // per-user INBOX + Sent
        return SmtpResponse.Ok;   // "250 2.0.0 Ok: queued as <id>"
    }
}
```

- `IMailboxFilter` handles accept/reject rules per sender and recipient (allow-list domains, simulate `550 mailbox unavailable`).
- `IUserAuthenticator` delegates to `IAccountService` (accept-any or registry).
- Hook SMTP session events (`SessionCreated`, `CommandExecuting`, `SessionCompleted`) to write the **protocol transcript** to a ring buffer, viewable in the UI for debugging client issues.
- Limits: `MaxMessageSize` (default 50 MB, advertised via `SIZE`), max recipients, and command timeouts.
- Advertised extensions: `8BITMIME`, `SMTPUTF8`, `PIPELINING`, `SIZE`, `STARTTLS`, `AUTH PLAIN LOGIN` (plus `CRAM-MD5` in registry mode).

### 5.3 Chaos / fault injection (Mailpit "chaos" parity)

Configurable rules, at runtime through the API, per stage (`Connect`, `Helo`, `MailFrom`, `RcptTo`, `Data`, `Auth`):

```json
{ "stage": "RcptTo", "match": { "recipient": "*@bounce.test" }, "response": { "code": 550, "text": "5.1.1 User unknown" }, "probability": 1.0 }
{ "stage": "Data", "delayMs": 5000 }
{ "stage": "Connect", "action": "drop" }
```

These let you test application retry and bounce handling.

---

## 6. IMAP subsystem (core of the project)

### 6.1 Standards baseline

- **RFC 3501** IMAP4rev1 as the main compatibility target, since nearly all clients use it.
- **RFC 9051** IMAP4rev2, advertised as `IMAP4rev2` alongside `IMAP4rev1`, behind a feature flag until conformance tests pass.
- Base syntax: RFC 5234 ABNF and RFC 4466 (collected extensions).

### 6.2 Capabilities

| Phase | Capability | RFC | Notes |
|---|---|---|---|
| 1 | `IMAP4rev1`, `AUTH=PLAIN`, `AUTH=LOGIN`, `LOGINDISABLED` (pre-TLS when required), `STARTTLS` | 3501, 4616 | |
| 1 | `LITERAL+` / `LITERAL-` | 7888 | Non-synchronizing literals. Simplifies clients. |
| 1 | `IDLE` | 2177 | Essential for "real-time" in clients and tests. |
| 1 | `UIDPLUS` | 4315 | `APPENDUID` / `COPYUID` response codes, `UID EXPUNGE`. Tests depend on these. |
| 1 | `NAMESPACE` | 2342 | |
| 1 | `ID` | 2971 | Logs the client name. |
| 1 | `ENABLE` | 5161 | |
| 1 | `SPECIAL-USE` | 6154 | `\Sent`, `\Drafts`, `\Trash`, … |
| 1 | `MOVE` | 6851 | |
| 2 | `CHILDREN`, `LIST-EXTENDED`, `LIST-STATUS` | 3348, 5258, 5819 | |
| 2 | `UNSELECT` | 3691 | |
| 2 | `ESEARCH`, `SEARCHRES` | 4731, 5182 | Required by rev2. |
| 2 | `CONDSTORE`, `QRESYNC` | 7162 | modseq is already in the schema from day one. |
| 2 | `SASL-IR`, `AUTH=XOAUTH2` (accept-any token) | 4959, Google | Lets apps using OAuth flows be tested. |
| 2 | `IMAP4rev2` | 9051 | |
| 3 | `COMPRESS=DEFLATE`, `SORT`, `THREAD=REFERENCES`, `QUOTA` (fake) | 4978, 5256, 9208 | Nice to have. |

### 6.3 Connection pipeline

```
Socket ─► (SslStream on 993 / after STARTTLS) ─► PipeReader ─► ImapCommandParser ─► CommandDispatcher
                                                                                      │
                                    PipeWriter ◄── ImapResponseWriter ◄── CommandHandler(s)
                                                       ▲
                                    Untagged notifier ─┘  (EXISTS / EXPUNGE / FETCH FLAGS from IMailEventBus)
```

- `ImapListener : BackgroundService` accepts with `Socket.AcceptAsync`. Each connection gets an `ImapSession` running on its own task, with `MaxConnections` and per-IP limits.
- Use `System.IO.Pipelines` (`PipeReader.Create(stream)`) for zero-copy line framing and **literal** handling, since IMAP mixes line-based input with `{n}` byte literals.
- **STARTTLS**: after the `OK Begin TLS` response, flush, complete the reader and writer *without* closing the stream, wrap the `NetworkStream` in `SslStream`, authenticate as server, and recreate the pipes. Throw away any bytes buffered before TLS (protection against STARTTLS command injection).

### 6.4 Session state machine

```
           ┌──────────────┐  LOGIN / AUTHENTICATE  ┌───────────────┐  SELECT / EXAMINE  ┌──────────┐
 connect ─►│NotAuthenticated├──────────────────────►│ Authenticated ├───────────────────►│ Selected │
           └──────┬───────┘                        └──────┬────────┘◄───── CLOSE ───────┴────┬─────┘
                  │ LOGOUT                                 │ LOGOUT       UNSELECT              │ LOGOUT
                  ▼                                        ▼                                    ▼
                                             ┌────────┐
                                             │ Logout │
                                             └────────┘
 (PREAUTH greeting is possible in AcceptAny + "preauth" mode for very lazy test setups)
```

Each command handler declares its allowed states. The dispatcher rejects anything else with `BAD`.

```csharp
public interface IImapCommandHandler
{
    string Name { get; }                       // "FETCH"
    SessionState AllowedStates { get; }        // flags
    bool SupportsUidPrefix { get; }
    ValueTask ExecuteAsync(ImapCommandContext ctx, ImapCommand command, CancellationToken ct);
}
```

Handlers are registered in DI and looked up from a `FrozenDictionary<string, IImapCommandHandler>` (case-insensitive).

### 6.5 Parser

IMAP grammar is irregular, so the parser is hand-written and incremental.

- **Tokens**: `atom`, `quoted` (escapes `\"` `\\`), `literal` `{n}` / `{n+}` / `~{n}` (binary), `NIL`, `(`, `)`, `[`, `]`, `sequence-set` (`1:*,5,7:9`, `$` for SEARCHRES), `date` (`17-Oct-2026`), and `section-spec` inside `BODY[...]`.
- **Synchronizing literals**: when the parser hits `{n}\r\n` it returns `NeedLiteral(n)`, the session writes `+ Ready for literal data\r\n`, and parsing resumes once `n` bytes are buffered. Non-synchronizing `{n+}` skips the continuation. Large `APPEND` literals over a threshold (default 1 MB) stream straight to a temp file instead of being buffered.
- Output is an AST: `ImapCommand(Tag, Name, IsUid, IReadOnlyList<ImapArg> Args)`. Specialized sub-parsers handle the FETCH attribute list, SEARCH keys and STORE operations.
- Limit total line length (e.g. 64 KB, excluding literals) and literal size (`MaxMessageSize`) to prevent memory exhaustion.
- **Thorough unit tests** cover golden inputs from RFC examples and corpora captured from real clients (Thunderbird, Outlook, iOS Mail, MailKit).

### 6.6 Mailbox snapshot and sequence numbers

IMAP sequence numbers are per session and must stay stable until the server tells the client about changes. Each `Selected` session keeps a **`SelectedMailboxView`**:

```csharp
sealed class SelectedMailboxView
{
    List<Uid> _uids;                    // index + 1 == message sequence number (MSN)
    Dictionary<Uid, MailboxEntrySnapshot> _known;
    ConcurrentQueue<MailboxChange> _pending;   // fed by event bus while session is busy
    bool ReadOnly;                      // EXAMINE
    // Translate seq-set <-> uid-set, apply pending changes at "safe points"
}
```

Rules from RFC 3501 §7.4.1 / RFC 9051 §7.5.1:

- `EXPUNGE` responses must not be sent during `FETCH`, `STORE` or `SEARCH` (non-UID). Pending changes are flushed only after `NOOP`, `CHECK`, `IDLE`, a UID command, or the end of any command where that is allowed.
- New messages produce `* n EXISTS` (and `* n RECENT` for rev1).
- Flag changes made by other sessions produce `* n FETCH (FLAGS (...) [MODSEQ (x)])`.
- Expunges are emitted in descending MSN order (or as `VANISHED` under QRESYNC), and the view is updated to match.

### 6.7 Concurrency and change notification

- `IMailEventBus` is backed by `System.Threading.Channels`. Each session subscribes to the mailboxes it has selected or is IDLE-ing on (`Subscribe(MailboxId) → ChannelReader<MailboxChange>`).
- Store mutations go through a **per-mailbox `SemaphoreSlim`** (or SQLite write transactions). UID assignment (`UidNext++`) and modseq bumps are atomic with the insert.
- **IDLE**: the session sends `+ idling`, then races `ReadLineAsync("DONE")` against the change channel. Changes are written immediately as untagged responses. An idle timeout of 29 minutes (RFC recommendation) closes or refreshes the session.
- SignalR and the IMAP sessions consume the same bus, so a message sent via SMTP shows up in the browser and in an IDLE-ing Thunderbird within milliseconds.

### 6.8 Command coverage (phase 1)

| Group | Commands |
|---|---|
| Any state | `CAPABILITY`, `NOOP`, `LOGOUT`, `ID`, `ENABLE` |
| Not authenticated | `STARTTLS`, `LOGIN`, `AUTHENTICATE` (PLAIN, LOGIN; XOAUTH2 in phase 2) |
| Authenticated | `SELECT`, `EXAMINE`, `CREATE`, `DELETE`, `RENAME`, `SUBSCRIBE`, `UNSUBSCRIBE`, `LIST`, `LSUB`, `NAMESPACE`, `STATUS`, `APPEND`, `IDLE` |
| Selected | `CHECK`, `CLOSE`, `UNSELECT`, `EXPUNGE`, `UID EXPUNGE`, `SEARCH`, `FETCH`, `STORE`, `COPY`, `MOVE`, and all `UID` variants |

### 6.9 FETCH engine (the hard part)

FETCH attributes to support: `FLAGS`, `INTERNALDATE`, `RFC822.SIZE`, `UID`, `ENVELOPE`, `BODYSTRUCTURE`, `BODY`, `BODY[<section>]<<partial>>`, `BODY.PEEK[...]`, `RFC822`, `RFC822.HEADER`, `RFC822.TEXT`, `MODSEQ`, macros `ALL`/`FAST`/`FULL`, and later `BINARY[...]` / `BINARY.SIZE` (rev2).

Implementation:

1. **Plan** the request. Work out whether the parsed message is needed (ENVELOPE, BODYSTRUCTURE, section parts) or whether metadata alone is enough (FLAGS, UID, SIZE, INTERNALDATE). Cheap requests like `FETCH 1:* (FLAGS UID)`, which clients send constantly, never touch the blob store.
2. **ENVELOPE / BODYSTRUCTURE** are computed once at capture time and **cached as serialized IMAP strings** in the DB (`imap_envelope`, `imap_bodystructure` columns). This turns the most expensive per-message work into a string read.
3. **Section addressing** (`BODY[1.2.MIME]`, `BODY[2.HEADER.FIELDS (SUBJECT FROM)]`, `BODY[TEXT]`, `BODY[]<0.2048>`):
   - Parse with MimeKit's `MimeParser` with `persistent: true` over a seekable `FileStream`. MimeKit records `HeadersOffset`, `ContentOffset` and `EndOffset` for each entity.
   - Resolve the part path (IMAP part numbering: multipart children are 1-based, and a `message/rfc822` nested part exposes its own numbering).
   - Serve **raw bytes** from those offsets, not re-serialized content. Clients compare sizes and hashes, so byte-exactness matters.
   - `HEADER.FIELDS (...)` / `.NOT`: filter the raw header block line by line, keeping folding and the trailing CRLF.
   - Partial `<origin.count>`: slice, and respond `BODY[...]<origin>`.
4. **Implicit `\Seen`**: a non-PEEK `BODY[...]` on a read-write mailbox sets `\Seen` and adds `FLAGS` to the response.
5. **Streaming output**: large literals are written as `{size}\r\n` followed by a stream copy into the `PipeWriter` (`CopyToAsync` with backpressure), never buffered whole.

```csharp
// BODYSTRUCTURE generation sketch (computed once at capture time)
static void WriteBodyStructure(ImapWriter w, MimeEntity e, bool extended)
{
    if (e is Multipart mp)
    {
        w.Open();
        foreach (var child in mp) WriteBodyStructure(w, child, extended);
        w.Space().QuotedOrNil(mp.ContentType.MediaSubtype.ToUpperInvariant());
        if (extended) w.Space().ParamList(mp.ContentType.Parameters).Space().Disposition(mp).Space().Language(mp).Space().Location(mp);
        w.Close();
        return;
    }
    var part = (MimePart)e; // also handle MessagePart (message/rfc822): envelope + nested structure + lines
    w.Open()
     .Quoted(part.ContentType.MediaType.ToUpperInvariant()).Space()
     .Quoted(part.ContentType.MediaSubtype.ToUpperInvariant()).Space()
     .ParamList(part.ContentType.Parameters).Space()
     .NString(part.ContentId).Space().NString(part.ContentDescription).Space()
     .Quoted(TransferEncodingName(part)).Space()
     .Number(RawContentLength(part));          // octets of encoded body, from offsets
    if (part.ContentType.IsMimeType("text", "*")) w.Space().Number(RawLineCount(part));
    if (extended) w.Space().NString(Md5OrNil(part)).Space().Disposition(part).Space().Language(part).Space().Location(part);
    w.Close();
}
```

### 6.10 SEARCH engine

1. Parse the SEARCH key tree (`AND` by juxtaposition, `OR a b`, `NOT a`, parentheses, `UID set`, seq set, `CHARSET`).
2. **Push down** to SQL what can be indexed: flags/keywords, `SINCE`/`BEFORE`/`ON` (internal date), `SENTSINCE`/… (Date header column), `LARGER`/`SMALLER`, `FROM`/`TO`/`CC`/`BCC`/`SUBJECT` (columns plus LIKE), `BODY`/`TEXT` (FTS5), `MODSEQ`.
3. Evaluate anything left (e.g. `HEADER X-Custom value`) in memory against the parsed message, only for the candidate set.
4. Return MSNs for `SEARCH` and UIDs for `UID SEARCH`. `ESEARCH` formats `(MIN MAX COUNT ALL)`.
5. Matching is case-insensitive substring after Unicode normalization (RFC 9051 requires at least ASCII case-insensitivity). `CHARSET` other than US-ASCII/UTF-8 gets `NO [BADCHARSET]`.

### 6.11 APPEND

- `APPEND <mailbox> [(flags)] [date-time] <literal>`. Stream the literal to the blob store, parse the summary, create the `CapturedMessage` (source = `ImapAppend`, *not* shown in the SMTP capture view by default) and the `MailboxEntry`.
- Respond `OK [APPENDUID <uidvalidity> <uid>] APPEND completed`.
- `NO [TRYCREATE]` if the mailbox doesn't exist.
- Optional "**send on append to Outbox**": if a user appends to a special `Outbox` folder, the server submits it as if sent via SMTP (envelope from headers). This gives IMAP-only clients a way to "send". It's off by default and clearly documented as non-standard.

### 6.12 Mailbox naming

- Hierarchy delimiter `/`, `INBOX` case-insensitive.
- Wire encoding: modified UTF-7 (RFC 3501 §5.1.3) for rev1 clients, raw UTF-8 when `UTF8=ACCEPT` is enabled or in rev2. Store names as UTF-8 and convert at the edge.
- `RENAME INBOX` follows the special RFC semantics: move all messages and leave an empty INBOX.

### 6.13 Authentication (IMAP and SMTP shared)

```csharp
public interface IAccountService
{
    ValueTask<AuthResult> AuthenticateAsync(string mechanism, string username, ReadOnlyMemory<byte> credential, CancellationToken ct);
    ValueTask<Account> GetOrCreateAsync(string address, CancellationToken ct);
}
```

- `AcceptAny`: any credentials succeed, and the username is normalized to an address (bare `alice` becomes `alice@{DefaultDomain}`).
- `Registry`: PBKDF2-hashed passwords (`Rfc2898DeriveBytes.Pbkdf2`), with users from config, API or test fixtures.
- `RequireTlsForAuth` (default false for local dev convenience): if true, advertise `LOGINDISABLED` before STARTTLS.
- Failed auth adds a small delay (e.g. 500 ms) to mimic real servers, and can be configured to zero for tests.

---

## 7. POP3 subsystem (phase 3)

A small RFC 1939 implementation over the same store: `USER/PASS`, `APOP` (optional), `STAT`, `LIST`, `UIDL` (use the message `Guid`), `RETR`, `TOP`, `DELE`, `RSET`, `NOOP`, `QUIT`, `CAPA`, `STLS`. It reads the user's `INBOX` only. `DELE` + `QUIT` translate to `\Deleted` + expunge. It reuses the line-reader and TLS-upgrade code from IMAP (put it in `Mailsink.Core.Networking`).

---

## 8. Storage

### 8.1 SQLite schema (v1)

```sql
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;

CREATE TABLE accounts (
  id            INTEGER PRIMARY KEY,
  address       TEXT NOT NULL UNIQUE COLLATE NOCASE,
  password_hash BLOB NULL,               -- NULL in AcceptAny mode
  created_at    INTEGER NOT NULL
);

CREATE TABLE messages (                  -- immutable content + capture metadata
  id               TEXT PRIMARY KEY,     -- GUID
  blob_key         TEXT NOT NULL,        -- sha256 hex
  size             INTEGER NOT NULL,
  received_at      INTEGER NOT NULL,     -- unix ms
  source           INTEGER NOT NULL,     -- 0=SMTP,1=Submission,2=ImapAppend,3=Api,4=Ui
  smtp_mail_from   TEXT NULL,
  smtp_rcpt_to     TEXT NULL,            -- JSON array
  smtp_meta        TEXT NULL,            -- JSON: helo, client ip, tls, auth user
  message_id_hdr   TEXT NULL,
  subject          TEXT NULL,
  from_addr        TEXT NULL,
  to_addrs         TEXT NULL,
  cc_addrs         TEXT NULL,
  bcc_addrs        TEXT NULL,
  date_hdr         INTEGER NULL,
  snippet          TEXT NULL,
  has_attachments  INTEGER NOT NULL DEFAULT 0,
  imap_envelope    TEXT NOT NULL,        -- pre-rendered IMAP ENVELOPE
  imap_bodystructure TEXT NOT NULL,      -- pre-rendered extended BODYSTRUCTURE
  imap_body        TEXT NOT NULL,        -- pre-rendered non-extended BODY
  is_read_in_ui    INTEGER NOT NULL DEFAULT 0,
  in_capture_view  INTEGER NOT NULL DEFAULT 1
);
CREATE INDEX ix_messages_received ON messages(received_at DESC);

CREATE TABLE message_tags (message_id TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
                           tag TEXT NOT NULL COLLATE NOCASE, PRIMARY KEY (message_id, tag));

CREATE TABLE mailboxes (
  id              INTEGER PRIMARY KEY,
  account_id      INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  name            TEXT NOT NULL,         -- UTF-8, '/'-delimited
  uid_validity    INTEGER NOT NULL,
  uid_next        INTEGER NOT NULL DEFAULT 1,
  highest_modseq  INTEGER NOT NULL DEFAULT 1,
  special_use     INTEGER NOT NULL DEFAULT 0,
  subscribed      INTEGER NOT NULL DEFAULT 1,
  UNIQUE(account_id, name COLLATE NOCASE)
);

CREATE TABLE mailbox_entries (
  mailbox_id    INTEGER NOT NULL REFERENCES mailboxes(id) ON DELETE CASCADE,
  uid           INTEGER NOT NULL,
  message_id    TEXT NOT NULL REFERENCES messages(id),
  flags         INTEGER NOT NULL DEFAULT 0,
  keywords      TEXT NULL,             -- space-separated
  internal_date INTEGER NOT NULL,
  modseq        INTEGER NOT NULL,
  PRIMARY KEY (mailbox_id, uid)
) WITHOUT ROWID;
CREATE INDEX ix_entries_modseq ON mailbox_entries(mailbox_id, modseq);
CREATE INDEX ix_entries_message ON mailbox_entries(message_id);

CREATE VIRTUAL TABLE messages_fts USING fts5(
  subject, from_addr, to_addrs, body_text, content='', tokenize='unicode61 remove_diacritics 2'
);
```

### 8.2 Blob store

- `FileBlobStore`: `{dataDir}/blobs/ab/cd/abcdef….eml`, content-addressed (SHA-256) and written via temp file + atomic rename.
- `SqliteBlobStore` (single-file mode): blobs in a table, handy for `--db-file` portability.
- `InMemoryBlobStore`: for the testing library.
- Reference counting: when the last `messages` row for a blob is deleted, a background sweeper deletes the blob.

### 8.3 Caching

- `MemoryCache` of parsed `MimeMessage` per `MessageId` (size-bounded, e.g. 64 MB), used by FETCH sections and the HTML view.
- The per-mailbox entry list for currently selected mailboxes stays in memory and is invalidated by the event bus.

### 8.4 Retention

`RetentionService : BackgroundService` runs every minute and enforces `MaxMessages` (default 500, as Mailpit does), `MaxAge`, and optionally `MaxTotalSize`. It deletes the oldest capture-view messages first. Messages referenced by mailbox entries are kept unless `RetentionAppliesToMailboxes = true`. Deletions raise `Expunged` events so IMAP sessions stay consistent.

---

## 9. Sending

### 9.1 Send paths

| Path | Entry point | Result |
|---|---|---|
| SMTP capture (port 1025) | App under test | Capture view + recipients' INBOX (if auto-create) |
| SMTP submission (1587, AUTH) | Mail client / app | Capture view + recipients' INBOX + sender's `Sent` |
| IMAP APPEND | Mail client | Target folder only (e.g. `Sent`, `Drafts`) |
| IMAP APPEND to `Outbox` (opt-in) | IMAP-only client | Treated as submission |
| REST `POST /api/v1/send` | CI scripts, UI | Same as submission (as `from` account) |
| Web UI compose | Human tester | Calls the same send API |

All local sends go through **one `SubmissionService`**:

```csharp
public sealed class SubmissionService(IMailStore store, LocalDeliveryAgent local)
{
    public async Task<SubmissionResult> SubmitAsync(MimeMessage message, Envelope envelope,
                                                    SubmissionOptions opts, CancellationToken ct)
    {
        EnsureHeaders(message);                    // Date, Message-ID, MIME-Version if missing
        using var raw = Serialize(message);
        var captured = await store.CaptureAsync(raw, envelope, ct);
        await local.DeliverAsync(captured, envelope, ct);              // INBOXes + Sent

        return new(captured.Id);
    }
}
```

### 9.2 Send API contract

```http
POST /api/v1/send
Content-Type: application/json

{
  "from": { "name": "Alice", "email": "alice@test.local" },
  "to":   [{ "email": "bob@test.local" }],
  "cc":   [], "bcc": [],
  "subject": "Welcome {{name}}",
  "text": "Hi {{name}}",
  "html": "<p>Hi <b>{{name}}</b></p>",
  "headers": { "X-Test-Run": "42" },
  "attachments": [{ "filename": "a.pdf", "contentType": "application/pdf", "contentBase64": "..." }],
  "inline":      [{ "contentId": "logo", "contentType": "image/png", "contentBase64": "..." }],
  "templateData": { "name": "Bob" },
  "repeat": 1
}
```

```http
201 Created
{ "ids": ["5d1f..."], "messageId": "<...@mailsink>" }
```

`POST /api/v1/send/raw` takes an `.eml` body (`message/rfc822`) plus envelope query parameters.

`repeat` (capped, e.g. 10,000) and the `/api/v1/send/bulk` endpoint generate load-test traffic, with optional randomized Faker-based content.

---

## 10. HTTP API and real-time

### 10.1 REST endpoints (Minimal APIs, `/api/v1`)

| Method | Path | Description |
|---|---|---|
| GET | `/messages?query=&limit=&start=&tag=&mailbox=` | List/search the capture view (Mailpit-style search syntax: `from:`, `to:`, `subject:`, `is:unread`, `has:attachment`, `tag:`, `before:`, `after:`, free text). |
| GET | `/messages/{id}` | Parsed summary: headers, addresses, text, html, attachments list |
| GET | `/messages/{id}/raw` | `message/rfc822` |
| GET | `/messages/{id}/headers` | All headers (ordered, duplicates kept) |
| GET | `/messages/{id}/html` | Sanitized HTML with `cid:` rewritten to part URLs |
| GET | `/messages/{id}/parts/{partId}` | Attachment/inline download |
| GET | `/messages/{id}/source-transcript` | SMTP session transcript |
| PUT | `/messages/read` / `/messages/unread` | Bulk read state |
| PUT | `/messages/{id}/tags` | Set tags |
| DELETE | `/messages` (body `ids`, or all) | Delete |
| POST | `/send`, `/send/raw`, `/send/bulk` | See §9.2 |
| GET/POST/DELETE | `/accounts` | Manage users (registry mode) |
| GET | `/accounts/{address}/mailboxes` | Per-user folders (IMAP view) |
| GET | `/accounts/{address}/mailboxes/{name}/messages` | Messages in a folder |
| GET/PUT | `/chaos` | Fault injection rules |
| GET | `/info` | Version, ports, counts, uptime |
| GET | `/health` | Liveness/readiness for Docker/Testcontainers |
| GET | `/wait?query=...&count=1&timeout=30s` | **Long-poll until a matching message arrives.** Removes `Thread.Sleep` from tests. |

An OpenAPI document is generated with `Microsoft.AspNetCore.OpenApi`, with a Scalar UI at `/api/docs`.

### 10.2 SignalR hub `/hubs/mail`

Server-to-client events: `MessageReceived(summary)`, `MessagesDeleted(ids)`, `MessageUpdated(id, readState, tags)`, `Stats(total, unread)`. Clients may `SubscribeAccount(address)` for per-user folder views.

### 10.3 Security of the HTTP surface

- Bind to `127.0.0.1` by default outside Docker. In Docker, bind to `0.0.0.0` and print a warning.
- Optional basic auth for the UI and API (`--ui-auth-file`, htpasswd-style).
- HTML preview is rendered in a **sandboxed iframe** (`sandbox="allow-same-origin"` without scripts, plus CSP `default-src 'none'; img-src data: cid: http: https:; style-src 'unsafe-inline'`). The HTML is sanitized with `HtmlSanitizer` (Ganss.Xss). Remote images are blocked by default with a "load remote content" toggle.
- CORS is off by default and configurable.

---

## 11. Web UI

The visual guide is [`docs/sample-design.png`](sample-design.png): a minimal, dark-first developer dashboard with a fixed left sidebar, one content column per page, and data presented in clean tables. We take its **layout and visual language** only. Mailsink uses its own name, logo, icons and copy, and copies no third-party brand assets.

### 11.1 Design language (taken from the reference)

| Element | Reference pattern | Mailsink usage |
|---|---|---|
| Theme | Near-black background, white primary text, muted grey secondary text, very thin borders. No heavy shadows. | Dark theme by default, plus a light theme with the same tokens. |
| App shell | Fixed ~300px left sidebar with a vertical border, and a content area with generous left padding and a max width of ~1380px. | Same. The sidebar collapses to an icon rail below 1024px and to a drawer below 768px. |
| Workspace switcher | Avatar + name + chevron at the top of the sidebar. | **Account switcher**: "All mail (catch-all)" or a specific account (`bob@test.local`). It sets the context for the Mailboxes and Compose pages. |
| Navigation | Icon + label rows. The active item has a subtle filled, rounded background. | See §11.2. |
| Page header | Large bold title on the left. On the right, one **white primary button** ("+ Create …") and a square icon button (`</>`). | Title + primary action (e.g. "+ Compose"). The `</>` button opens an **"API snippet" panel** showing the equivalent `curl` / C# / MailKit code for the current view. |
| Toolbar | A full-width search input with an icon, a filter dropdown ("All permissions"), and a square download/export icon button. | Search (Mailpit query syntax, §10.1) + a context filter dropdown + export (`.eml` zip / JSON). |
| Table | Rounded, slightly lighter header row with small grey column labels. Rows ~70px tall, separated by hairline dividers, and a `⋯` row-action menu at the end. | Every list (messages, accounts, logs, chaos rules) uses this one `DataTable` component. |
| Row identity | A square rounded icon tile before the primary name. The name has a dotted underline (it's a link). | Avatar tile (sender initials, or the folder or account icon) + subject/name as a link. |
| Code-ish values | Monospace text in a small rounded dark "chip" (`re_D4JC8Vi5...`). | Message-IDs, UIDs, SMTP codes, IP addresses, SHA keys. Click to copy. |
| Relative times | "2mo ago", "8 days ago", with a dotted underline. Hovering shows the exact timestamp. An info icon explains empty states ("No activity ⓘ"). | All dates. The tooltip shows the absolute local time + UTC. |
| Pagination | A muted text footer under the table ("Page 1 – 1 of 2 · 40 items ▾") with a page-size dropdown. | Same. Lists that update live show a "N new messages, click to show" pill instead of shifting rows around. |
| Sidebar footer | Small icon buttons (device/theme, tips, help). | Theme toggle, keyboard-shortcut sheet, link to docs/API reference. |
| Bottom bar | A full-width thin bar: "/docs" on the left, "+ Ask anything" on the right. | **Command bar**: on the left, server status (ports SMTP 1025 · IMAP 1143 · … with green/red dots). On the right, "⌘K Search or run a command" opens the command palette. |

### 11.2 Information architecture (sidebar)

| Nav item | Page | Contents |
|---|---|---|
| **Emails** | Captured mail (catch-all view) | Message table, the default landing page |
| **Mailboxes** | Per-account IMAP view | Folder list (INBOX, Sent, Drafts, Trash, custom) with unread counts, and a message table per folder |
| **Compose** | Send test email | Form, templates and bulk send (§9) |
| **Accounts** | Users (registry mode) | Table: address, auth mode, mailbox count, last login (IMAP/SMTP), created |
| **Templates** | Saved send templates | Scriban templates with sample `templateData` |
| **Chaos** | Fault injection rules | Table of rules (stage, match, response, probability) + a toggle to enable or disable each |
| **Logs** | Protocol transcripts | SMTP/IMAP/POP3 sessions: client IP, protocol chip, user, commands, duration. Clicking a row opens the C:/S: transcript. |
| **Metrics** | Stats | Messages/min, active IMAP sessions, store size (simple charts) |
| **Settings** | Server info and config | Ports, TLS cert download, retention, UI auth, theme |

### 11.3 Key screens

**Emails (list).** Header: "Emails" with the primary button **"+ Compose"** and `</>`. Toolbar: search, a filter dropdown (`All` · `Unread` · `With attachments` · `Tagged…` · `Source: SMTP / Submission / API / IMAP`), and an export button. Table columns:

| Column | Content |
|---|---|
| (tile) | Sender initials avatar. A small dot shows unread. |
| From / Subject | Subject (bold if unread, dotted-underline link) above the sender name and address in grey, with an attachment 📎 icon |
| To | First recipient + "+2" chip. Hovering shows the full list. |
| Tags | Small rounded chips |
| Size | `14.2 KB` |
| Received | Relative time with an absolute-time tooltip |
| ⋯ | Open raw · Download `.eml` · Mark read/unread · Tag · Delete |

Selecting several rows (checkboxes appear on hover) replaces the toolbar with a bulk-action bar (Mark read · Tag · Delete). New messages pushed by SignalR slide in at the top with a brief highlight.

**Email detail** (route `/emails/{id}`, full page, which suits the single-column reference layout better than a third pane):
- Breadcrumb `Emails / <subject>`. A header row holds the subject, the from/to/cc block, the received time, and a chip with the Message-ID. Action buttons on the right: **Reply** (opens Compose pre-filled), **Download**, **Delete**, `</>`.
- A **metadata card** grid: SMTP envelope (MAIL FROM, RCPT TO), client IP chip, TLS yes/no, auth user, size, source.
- **Tabs** (underlined text tabs): `Preview` · `HTML source` · `Text` · `Headers` · `Raw` · `Attachments (n)` · `SMTP transcript`.
- Preview: a sandboxed iframe in a bordered, rounded frame with a **device toggle** (Desktop / Tablet / Mobile widths), a light/dark email background toggle, and a "Load remote images" switch.
- Headers: a two-column key/value table, monospace values, filterable.

**Mailboxes.** The account switcher at the top of the sidebar picks the account. The page shows a secondary folder column (about 220px, inside the content area) and the same message table. Folder rows show special-use icons and unread counts. "+ New folder" is the primary action. Messages can be dragged onto a folder to MOVE them, which is the same IMAP operation, so IMAP clients see the change live.

**Compose** (page, also openable as a large modal from anywhere via `C`):
- From: a combobox of known accounts, or free text.
- To, Cc, Bcc: token inputs.
- Subject.
- Body editor: tabs for `Rich text` · `HTML` · `Text`, with a live preview on the right on wide screens.
- Attachments: a dropzone.
- Advanced (collapsible): custom headers, template + JSON data, repeat count, tags.
- Primary button **"Send"**. A secondary button says **"Copy as curl"**.
- After sending, a toast with a "View email" link appears.

**Empty states.** Centred, muted, and instructive. For example, an empty Emails page shows "No emails yet" with copyable connection snippets: an SMTP host/port chip, a `swaks` command and a .NET `SmtpClient` snippet, so new users can send their first mail straight away.

### 11.4 Interaction details

- Keyboard: `⌘K`/`Ctrl+K` command palette, `/` search, `j`/`k` move between rows, `Enter` open, `e` mark read, `#`/`Del` delete, `c` compose, `g e` / `g m` / `g l` go to Emails / Mailboxes / Logs, `?` shortcut sheet.
- URL state: search query, filter, page and page size live in the query string, so views can be shared and bookmarked.
- Live updates: the unread count shows in the browser tab title (`(3) Mailsink`). Optional desktop notifications.
- Accessibility: WCAG AA contrast in both themes (the reference's grey-on-black secondary text must meet ≥ 4.5:1), focus rings visible on dark backgrounds, and full keyboard reach for the table row `⋯` menus (Radix handles this).
- Responsive: below 768px, the table collapses to two-line list cards (subject/sender + time), and the detail page tabs become a horizontal scroller.

### 11.5 Design tokens

```css
:root[data-theme="dark"] {
  --bg:            #000000;   /* page */
  --bg-subtle:     #0a0a0a;   /* sidebar, bottom bar */
  --surface:       #111111;   /* table header, inputs, chips */
  --surface-hover: #1a1a1a;   /* row hover, active nav */
  --border:        #222222;   /* hairlines */
  --text:          #ededed;
  --text-muted:    #a1a1a1;
  --primary-bg:    #ffffff;   /* primary button */
  --primary-fg:    #000000;
  --accent:        #3ecf8e;   /* icon tiles, success dots */
  --danger:        #ff5c5c;
  --radius-sm: 6px; --radius-md: 10px; --radius-lg: 12px;
  --font-sans: "Inter", system-ui, sans-serif;
  --font-mono: "JetBrains Mono", ui-monospace, monospace;
}
:root[data-theme="light"] { --bg:#ffffff; --bg-subtle:#fafafa; --surface:#f4f4f5; --surface-hover:#ececee;
  --border:#e4e4e7; --text:#09090b; --text-muted:#52525b; --primary-bg:#09090b; --primary-fg:#ffffff; }
```

Type scale: page title 28–32px bold, table body 15–16px, column labels and footer 13–14px medium in `--text-muted`, chips 13px mono.

### 11.6 Front-end tech and structure

- **Stack**: Vite + React 18 + TypeScript, Tailwind CSS (tokens above as CSS variables), shadcn/ui on Radix primitives (DropdownMenu, Dialog, Tooltip, Tabs, Command), TanStack Table + TanStack Virtual for the message table, TanStack Query for API caching, `@microsoft/signalr` for live updates, lucide-react icons, and CodeMirror for the HTML source/raw views and the compose HTML editor.
- **API client**: generated from the OpenAPI document (`openapi-typescript`), so UI and API stay in sync.
- **Structure**: `src/app/` (routes: `emails`, `emails/$id`, `mailboxes`, `compose`, `accounts`, `chaos`, `logs`, `metrics`, `settings`), `src/components/` (`AppShell`, `Sidebar`, `PageHeader`, `Toolbar`, `DataTable`, `CodeChip`, `RelativeTime`, `IconTile`, `CommandBar`, `EmailPreviewFrame`), `src/lib/` (api, signalr, shortcuts).
- **Build**: built in CI and embedded into `wwwroot` (optionally as embedded resources for a true single-file binary). In development, the Vite dev server proxies `/api` and `/hubs` to the .NET host.

---

## 12. Configuration

Settings merge in this order: `appsettings.json` < environment variables (`MAILSINK__SMTP__PORT`) < CLI flags (`System.CommandLine`).

```json
{
  "Mailsink": {
    "DataDir": "./data",
    "Storage": "Sqlite",                // Sqlite | InMemory
    "DefaultDomain": "test.local",
    "Auth": { "Mode": "AcceptAny", "RequireTls": false, "Users": [ { "Address": "alice@test.local", "Password": "secret" } ] },
    "Smtp": {
      "Capture":    { "Port": 1025, "StartTls": "Optional" },
      "Submission": { "Port": 1587, "StartTls": "Optional", "SaveToSent": true },
      "Smtps":      { "Port": 1465, "Enabled": false },
      "MaxMessageSize": 52428800,
      "AutoCreateRecipients": true
    },
    "Imap": { "Port": 1143, "ImapsPort": 1993, "ImapsEnabled": true, "EnableRev2": false, "IdleTimeout": "00:29:00", "OutboxSubmission": false },
    "Pop3": { "Enabled": false, "Port": 1110 },
    "Http": { "Urls": "http://127.0.0.1:8025", "BasePath": "/", "UiAuthFile": null },
    "Tls":  { "CertificatePath": null, "KeyPath": null, "GenerateSelfSigned": true },
    "Retention": { "MaxMessages": 500, "MaxAge": "7.00:00:00" }
  }
}
```

**TLS**: if no certificate is supplied and `GenerateSelfSigned` is set, generate one at startup with `CertificateRequest` (RSA 2048 or ECDSA P-256, SAN = `localhost`, `127.0.0.1`, `mailsink`, and the configured hostnames), persist it in `DataDir`, and expose its public cert at `GET /api/v1/tls/cert` so tests can trust it.

---

## 13. Testing library (`Mailsink.Testing`)

GreenMail's biggest value is "start a real mail server inside my test". We provide the same for .NET.

```csharp
public sealed class MailsinkServer : IAsyncDisposable
{
    public static Task<MailsinkServer> StartAsync(Action<MailsinkOptions>? configure = null);
    // Ports default to 0 (OS-assigned) to allow parallel test runs
    public int SmtpPort { get; }  public int SubmissionPort { get; }
    public int ImapPort { get; }  public int Pop3Port { get; }  public Uri? ApiBaseUri { get; }

    public MailsinkServer WithUser(string address, string password);
    public IReadOnlyList<CapturedMessage> Messages { get; }
    public Task<CapturedMessage> WaitForMessageAsync(Func<CapturedMessage, bool> predicate, TimeSpan? timeout = null);
    public Task<IReadOnlyList<CapturedMessage>> WaitForMessagesAsync(int count, TimeSpan? timeout = null);
    public Task SendAsync(MimeMessage message);    // via in-process SubmissionService
    public Task ResetAsync();                      // purge store between tests
}
```

- **Headless mode**: no ASP.NET host, no UI, in-memory store, ports bound to `0`. Starts in about 50 ms.
- xUnit `IAsyncLifetime` fixture `MailsinkFixture` and `[Collection]` sharing. NUnit `[OneTimeSetUp]` helper.
- Assertion helpers (FluentAssertions-free, plain extension methods): `message.ShouldHaveRecipient(...)`, `.ShouldHaveAttachment("invoice.pdf")`, `.HtmlBody.ShouldContainLink(...)`, `ExtractLinks()` for following password-reset links in tests.
- **Testcontainers module** `Testcontainers.Mailsink` (separate package) wraps the Docker image with `WithUser`, `GetSmtpPort()` and similar, and waits on `/health`.

Example:

```csharp
await using var mail = await MailsinkServer.StartAsync(o => o.Auth.Mode = AuthMode.Registry);
mail.WithUser("bob@test.local", "pw");

await app.SendPasswordResetAsync("bob@test.local", smtpPort: mail.SmtpPort);

// Assert through IMAP exactly as a real client would
using var imap = new MailKit.Net.Imap.ImapClient();
await imap.ConnectAsync("127.0.0.1", mail.ImapPort, SecureSocketOptions.None);
await imap.AuthenticateAsync("bob@test.local", "pw");
await imap.Inbox.OpenAsync(FolderAccess.ReadOnly);
Assert.Equal(1, imap.Inbox.Count);
```

---

## 14. Cross-cutting concerns

### 14.1 Networking common layer (`Mailsink.Core.Networking`)

- `TcpListenerService` base class: bind, accept loop, connection limits, graceful shutdown (stop accepting, send `* BYE Server shutting down` / `421`, then wait up to N seconds).
- `ConnectionContext`: `Stream`, `PipeReader`/`PipeWriter`, `RemoteEndPoint`, `IsSecure`, `UpgradeToTlsAsync()`.
- `ProtocolLogger`: optional per-session wire log (credentials redacted) into a bounded ring buffer and `ILogger` at `Trace`.

### 14.2 Observability

- `ILogger` with structured logs (Serilog console sink optional).
- **OpenTelemetry** metrics: `mailsink.smtp.messages_received`, `mailsink.imap.sessions_active`, `mailsink.imap.command_duration` (histogram, tag = command), `mailsink.store.size_bytes`. Traces span SMTP receive → store → delivery → notification.
- `/metrics` endpoint (Prometheus exporter), opt-in.

### 14.3 Performance targets (single dev laptop)

| Scenario | Target |
|---|---|
| SMTP ingest, 10 KB messages, 20 parallel clients | ≥ 1,500 msg/s (SQLite WAL) |
| Startup (headless, in-memory) | < 100 ms |
| Startup (full, Docker, AOT) | < 300 ms |
| `FETCH 1:10000 (UID FLAGS)` | < 150 ms |
| IDLE notification latency after SMTP DATA end | < 50 ms p99 |
| Memory idle | < 60 MB |

Techniques: Pipelines and `ArrayPool`, pre-rendered ENVELOPE/BODYSTRUCTURE, batched SQLite writes (one transaction per message, covering capture + all deliveries), and `FrozenDictionary` command lookup.

### 14.4 Security hardening (it's a test tool, but it often runs on shared CI hosts)

- Strict parser limits: line length, literal size, nesting depth for parenthesized lists and MIME (MimeKit `ParserOptions.MaxMimeDepth`), and the number of FETCH items.
- Per-IP connection caps and command rate limits (`System.Threading.RateLimiting`).
- No outbound mail traffic at all: there is no relay, so nothing is ever delivered beyond Mailsink.
- Sanitized HTML, sandboxed preview, no script execution.
- Container runs as non-root (chiseled image), with a read-only root FS except `/data`.

---

## 15. Testing strategy

| Layer | Approach |
|---|---|
| IMAP parser | Table-driven unit tests: RFC examples, fuzzing with SharpFuzz/libFuzzer on the tokenizer, and a corpus of real client traffic. |
| IMAP commands | Golden transcript tests: `C:`/`S:` scripts replayed against an in-memory server, with exact response matching (regex for UIDVALIDITY, dates). |
| FETCH/BODYSTRUCTURE | Corpus of tricky `.eml` files (nested `message/rfc822`, `multipart/alternative` inside `mixed`, missing boundaries, 8-bit headers, `encoded-word`s, UTF-8 subjects, TNEF, S/MIME). Compare output against Dovecot's for the same files: run Dovecot in a container in CI and diff. |
| Concurrency | Two MailKit clients on the same mailbox: one STOREs/EXPUNGEs while the other IDLEs, then assert untagged responses and sequence-number consistency. |
| Conformance | **Dovecot `imaptest`** (stress + scripted tests) run in CI against the Docker image. Track a pass-rate badge. |
| SMTP | MailKit `SmtpClient` E2E: auth modes, STARTTLS, SIZE limit, chaos rules, 8BITMIME/SMTPUTF8. |
| API | `WebApplicationFactory` integration tests. |
| Client compatibility (manual, per release) | Thunderbird, Outlook (new + classic), Apple Mail, iOS Mail, Gmail app (IMAP), mutt, Python `imaplib`, JavaMail, Nodemailer/imapflow. |
| UI | Playwright smoke tests: receive → appears live → open HTML → delete. Visual regression snapshots (dark + light, desktop + mobile widths) for the shell, Emails table and detail page, plus axe-core accessibility checks. |

---

## 16. Deployment

### 16.1 Docker

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Mailsink.Server -c Release -o /out -p:PublishAot=true

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
COPY --from=build /out /app
VOLUME /data
ENV MAILSINK__DataDir=/data MAILSINK__Http__Urls=http://0.0.0.0:8025
EXPOSE 1025 1587 1143 1993 1110 8025
USER app
ENTRYPOINT ["/app/mailsink"]
HEALTHCHECK CMD ["/app/mailsink", "health"]
```

### 16.2 Distribution

- Docker image (`ghcr.io/<owner>/mailsink`), multi-arch amd64/arm64.
- `dotnet tool install -g mailsink`.
- Self-contained single-file binaries for win-x64, linux-x64, linux-arm64, osx-arm64 (GitHub Releases).
- NuGet: `Mailsink.Testing`, `Testcontainers.Mailsink`.
- Optional Windows Service / systemd unit samples.

### 16.3 CI (GitHub Actions)

`build` → `unit tests` → `integration tests` → `imaptest conformance (Docker)` → `Playwright UI` → `publish (tags only)`.

---

## 17. Implementation roadmap

| Phase | Scope | Exit criteria |
|---|---|---|
| **0. Skeleton** (week 1) | Solution layout, Generic Host, config, logging, networking base, TLS cert generation, in-memory store | `dotnet run` starts, ports bind, `/health` OK |
| **1. Capture MVP** (weeks 2–3) | SMTP capture via SmtpServer, SQLite store and blob store, REST list/get/raw/delete, SignalR, basic SPA (list + HTML/Text/Raw/Headers), retention | Mailpit-equivalent basic flow works end to end |
| **2. IMAP core** (weeks 4–7) | Parser, state machine, LOGIN/AUTHENTICATE, LIST/SELECT/EXAMINE/STATUS, FETCH (all attributes), STORE, SEARCH (SQL pushdown), COPY/MOVE, EXPUNGE, APPEND + UIDPLUS, IDLE, STARTTLS/IMAPS, special-use folders, local delivery | Thunderbird + MailKit + Outlook can browse, read, flag, move and delete. imaptest basic scripts pass. |
| **3. Sending** (weeks 8–9) | Submission endpoint + Sent copy, `SubmissionService`, `/send` API (+raw, bulk, templates), UI compose, Outbox-append (opt-in), POP3 | A client can send A→B and both see it in Sent/INBOX via IMAP. |
| **4. Test tooling** (week 10) | `Mailsink.Testing` (headless, port 0, waits, assertions), `/wait` long-poll, Testcontainers module, chaos engine + UI | Sample repo with xUnit tests using all three styles |
| **5. Hardening and rev2** (weeks 11–13) | CONDSTORE/QRESYNC, ESEARCH, LIST-EXTENDED, IMAP4rev2 flag on, fuzzing, perf tuning, AOT publish, OpenTelemetry | Perf targets met. imaptest pass rate ≥ 95%. No fuzz crashes in 24 h. |
| **6. Extras** | HTML compatibility report (caniemail data), link checker, spam-score (SpamAssassin sidecar), SORT/THREAD, webhooks on receive, multi-tenant "projects" | Backlog |

---

## 18. Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| IMAP FETCH/BODYSTRUCTURE edge cases break specific clients | High | Byte-exact offsets from MimeKit, a Dovecot diff-testing harness, and a real-client corpus. Pre-render at capture time so bugs are deterministic. |
| Sequence-number/expunge races between concurrent sessions | High | A single per-session `SelectedMailboxView` with strict "safe point" flushing. Concurrency integration tests. |
| SQLite write contention under burst load | Medium | WAL, a single writer queue (`Channel<WriteOp>` + batching), and the in-memory store for heavy test runs. |
| `SmtpServer` library maintenance stalls | Medium | Thin adapter layer (`Mailsink.Smtp`). The SMTP protocol is small enough to replace with a custom Pipelines implementation that reuses the IMAP networking base. |
| Accidental real-world email delivery | High (reputational) | No relay or release exists. Mailsink has no outbound SMTP path, so mail cannot leave the process. |
| AOT incompatibilities (SignalR, Dapper reflection) | Low | AOT is a phase 5 goal. Use Dapper.AOT or hand-written ADO.NET and source-generated JSON from the start. |
| Scope creep toward a real MTA | Medium | Keep the non-goals list (§1.2) explicit. No MX delivery and no queue retries. |

---

## 19. Key NuGet dependencies

| Package | Use |
|---|---|
| `MimeKit`, `MailKit` | MIME parsing/building, test clients |
| `SmtpServer` | Inbound SMTP protocol |
| `Microsoft.Data.Sqlite`, `Dapper` / `Dapper.AOT` | Persistence |
| `Microsoft.AspNetCore.SignalR` (in-box) | Real-time UI |
| `Microsoft.AspNetCore.OpenApi`, `Scalar.AspNetCore` | API docs |
| `HtmlSanitizer` | Safe HTML preview |
| `System.CommandLine` | CLI flags |
| `OpenTelemetry.Extensions.Hosting` (+ Prometheus exporter) | Observability |
| `Scriban` (or `Fluid`) | Template rendering in the send API |
| `Bogus` | Random content for bulk/load sends |
| Tests: `xunit.v3`, `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers`, `Microsoft.Playwright`, `SharpFuzz` | |

---

## 20. Appendix: example IMAP session (target behavior)

```
S: * OK [CAPABILITY IMAP4rev1 LITERAL+ SASL-IR ID ENABLE IDLE STARTTLS AUTH=PLAIN AUTH=LOGIN] Mailsink ready
C: a1 LOGIN bob@test.local pw
S: a1 OK [CAPABILITY IMAP4rev1 LITERAL+ ID ENABLE IDLE NAMESPACE UIDPLUS MOVE SPECIAL-USE UNSELECT CHILDREN] Logged in
C: a2 LIST "" "*"
S: * LIST (\HasNoChildren) "/" INBOX
S: * LIST (\HasNoChildren \Sent) "/" Sent
S: * LIST (\HasNoChildren \Drafts) "/" Drafts
S: * LIST (\HasNoChildren \Trash) "/" Trash
S: a2 OK LIST completed
C: a3 SELECT INBOX
S: * FLAGS (\Answered \Flagged \Deleted \Seen \Draft)
S: * OK [PERMANENTFLAGS (\Answered \Flagged \Deleted \Seen \Draft \*)] Flags permitted
S: * 1 EXISTS
S: * 1 RECENT
S: * OK [UIDVALIDITY 1760659200] UIDs valid
S: * OK [UIDNEXT 2] Predicted next UID
S: a3 OK [READ-WRITE] SELECT completed
C: a4 UID FETCH 1:* (FLAGS ENVELOPE BODY.PEEK[HEADER.FIELDS (SUBJECT)])
S: * 1 FETCH (UID 1 FLAGS (\Recent) ENVELOPE ("Fri, 17 Oct 2026 10:00:00 +0000" "Reset your password" ...) BODY[HEADER.FIELDS (SUBJECT)] {30}
S: Subject: Reset your password
S:
S: )
S: a4 OK FETCH completed
C: a5 IDLE
S: + idling
   ... (app sends another mail via SMTP) ...
S: * 2 EXISTS
S: * 2 RECENT
C: DONE
S: a5 OK IDLE terminated
C: a6 UID MOVE 1 Trash
S: * OK [COPYUID 1760659203 1 1] Moved
S: * 1 EXPUNGE
S: a6 OK MOVE completed
```
