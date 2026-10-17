# Mailsink: implementation status

Snapshot taken 2026-10-17, mid-implementation. Work stopped while the web UI was being written. Nothing is committed yet: everything below is uncommitted in the working tree.

## Summary

| Area | State |
|---|---|
| Backend (.NET 10): Core, storage, SMTP, IMAP, POP3, hosting, API, server | **Done**, builds and is tested. Fully local: relay/release was removed on 2026-10-17 |
| Testing library, xUnit fixture, Testcontainers module | **Done**, builds. Testcontainers is untested because no Docker image exists yet |
| Automated tests | **82 passing**: 46 core/store, 21 IMAP, 15 end-to-end with MailKit |
| Web UI (Vite/React/Tailwind) | **Done for this pass**: every page in §11 is implemented, builds, and was checked in the browser at desktop and mobile widths in both themes |
| Solution file, Docker image, CI workflow, README | **Done** (2026-10-17): `Mailsink.slnx`, `docker/Dockerfile`, `.github/workflows/ci.yml`, `README.md` |
| Conformance tests, Testcontainers module test | **Not started** |

## Implemented

### Solution scaffolding
- `global.json` (SDK 10), `Directory.Build.props`, `Directory.Packages.props` (central versions, transitive pinning; `System.Security.Cryptography.Pkcs` pinned to 10.0.13 to clear a vulnerability warning), `.gitignore`, `.editorconfig`.
- Projects under `src/`: Core, Storage.InMemory, Storage.Sqlite, Smtp, Imap, Pop3, **Hosting** (an extra project: the shared composition root used by Server and Testing), Api, Server, Testing, Testing.Xunit, Testcontainers.Mailsink.
- Projects under `tests/`: Core.Tests, Imap.Tests, Integration.Tests.

### Mailsink.Core
- Options tree (`MailsinkOptions`) matching design §12, plus `BindAddress`, `BlobStorage`, `Diagnostics`, `Imap.PreAuth`.
- Domain: `MessageId`, `MailboxId`, `SystemFlags`, `SpecialUse`, `Mailbox`, `MailboxEntry`, `CapturedMessage`, `Envelope`, `ParsedSummary`, `ImapRendering`, `Account`.
- Event bus on `System.Threading.Channels`: captured, deleted, updated, entry added, flags changed, expunged, mailboxes changed, accounts changed.
- `IMailStore` / `IBlobStore` contracts. Shared helpers: UIDVALIDITY generation, mailbox-name normalization, flag math, and a size-bounded parsed-message cache.
- MIME: a byte-exact offset parser (`MimeStructureParser`, which handles nested multipart and `message/rfc822`); an IMAP ENVELOPE / BODYSTRUCTURE / BODY renderer that runs once at capture time; `MessageAnalyzer` for the summary, snippet and search text; CRLF normalization.
- Mailpit-style search syntax (`SearchTerm`, `CaptureQuery`) and an in-memory matcher.
- Accounts: AcceptAny and Registry modes, PBKDF2 hashes, address normalization, a configurable auth-failure delay, user seeding, and default folders (INBOX, Sent, Drafts, Trash, plus Outbox when enabled).
- `LocalDeliveryAgent` (plus-addressing, optional plus-folders, auto-create recipients, Sent copy). `SubmissionService` (one send path, header fix-up, envelope from headers). Every send is delivered locally; nothing is forwarded to another SMTP server.
- Chaos engine: stages Connect, Helo, Auth, MailFrom, RcptTo and Data; actions respond, delay and drop; probability; glob matching.
- Diagnostics: a protocol transcript ring buffer and `MetricsCollector` (a `System.Diagnostics.Metrics` meter plus a 60-minute window).
- Networking: `TcpServer` (bind on start, port 0 supported, connection cap, graceful stop), `ProtocolConnection` (PipeReader line and literal framing, in-place STARTTLS upgrade that discards pipelined plaintext), `CertificateProvider` (PFX or PEM, or a generated self-signed ECDSA certificate persisted in DataDir), and `EndpointRegistry`.
- Send templates: Scriban rendering, persisted to `templates.json`.

### Storage
- **InMemory store**: a single lock, events published after it is released, reference-counted GC of messages and blobs.
- **SQLite store**: WAL mode, schema from §8.1 (plus `owner`, `addresses_json`, `attachment_count` and a `blobs` table), one writer gate with a transaction per operation, FTS5 free-text search, search terms translated to SQL, retention, purge.
- Blob stores: file-based (sharded and content-addressed, written atomically) and SQLite single-file mode.
- One contract test suite runs against InMemory, SQLite with file blobs, and SQLite with database blobs.

### SMTP (Mailsink.Smtp)
- Capture, submission and SMTPS endpoints.
- ESMTP extensions: PIPELINING, SIZE, 8BITMIME, SMTPUTF8, ENHANCEDSTATUSCODES, STARTTLS, and AUTH PLAIN, LOGIN and XOAUTH2.
- Enforces RequireAuth and RequireTls, the size limit, the recipient limit, command timeouts and an error cap.
- Chaos hooks at every stage, transcripts with credentials redacted, and messages linked to their session.

### IMAP (Mailsink.Imap)
- Hand-written parser that handles sync and non-sync (`LITERAL+`) literals and bracketed FETCH atoms. Also modified UTF-7 and sequence sets.
- Session state machine, a `FrozenDictionary` of command handlers, and a per-session `SelectedMailboxView` that sends updates only at safe points: EXISTS, RECENT, FETCH FLAGS, and EXPUNGE (held back during non-UID FETCH, STORE and SEARCH). Cross-session `\Recent` tracking.
- Commands: CAPABILITY, NOOP, CHECK, LOGOUT, ID, ENABLE, STARTTLS, LOGIN, AUTHENTICATE (PLAIN, LOGIN, XOAUTH2, SASL-IR), SELECT, EXAMINE, CREATE (with `USE` special-use), DELETE, RENAME (including the special INBOX rule), SUBSCRIBE, UNSUBSCRIBE, LIST (LIST-EXTENDED, LIST-STATUS, SPECIAL-USE, CHILDREN, implied `\Noselect` parents), LSUB, NAMESPACE, STATUS, APPEND (APPENDUID, TRYCREATE, opt-in Outbox submission), IDLE, CLOSE, UNSELECT, FETCH (every attribute in §6.9 except BINARY, plus exact sections, partials and implicit `\Seen`), STORE, SEARCH (full key set, ESEARCH RETURN, BADCHARSET), COPY and MOVE (COPYUID), EXPUNGE, UID EXPUNGE.
- IMAP and IMAPS listeners.

### POP3 (Mailsink.Pop3)
- RFC 1939 over INBOX: CAPA, STLS, USER/PASS, AUTH PLAIN/LOGIN, STAT, LIST, UIDL, RETR, TOP, DELE, RSET, NOOP, QUIT. Deletes are applied on QUIT as `\Deleted` plus expunge.

### Hosting
- `AddMailsink()` wires everything together.
- `StoreInitializer` migrates the database, seeds users and logs a warning when listeners bind to all interfaces.
- `RetentionService` and `MetricsEventListener`.

### HTTP API (Mailsink.Api) and server
- Every endpoint in §10.1:
  - Messages: list/search, export (zip or JSON), detail, raw, headers, sanitized HTML, parts, source transcript, read/unread, tags, delete.
  - Sending: `/send` (templates, attachments, inline parts, repeat, tags), `/send/raw`, `/send/bulk` (Bogus).
  - Accounts and mailboxes: CRUD, plus move, flags and expunge.
  - Server: chaos, logs, templates, info, metrics, health, TLS certificate, the `/wait` long-poll, and tags.
- Source-generated JSON with reflection fallback, OpenAPI at `/api/openapi/v1.json`, and Scalar at `/api/docs`.
- SignalR hub at `/hubs/mail` with batched event forwarding and per-account groups.
- HtmlSanitizer preview: `cid:` references rewritten, remote images blocked unless `?remote=true`, CSP header.
- Optional basic auth (htpasswd plain, `{SHA}` and `{SHA256}`), CORS, and the SPA fallback (or a "UI not built" page).
- Server `Program.cs`: short CLI switches, `mailsink health` for the Docker HEALTHCHECK, `PathBase`, `appsettings.json`, and an optional `-p:BuildWeb=true` target that builds the SPA.

### Testing library
- `MailsinkServer`: headless or with HTTP, ports 0, in-memory storage. Provides `WithUser`, `Messages`, `WaitForMessage(s)Async`, `SendAsync`, `ResetAsync`, `AddChaosRule` and `CertificatePem`.
- `ReceivedMessage` with `ExtractLinks()`, and the assertion helpers (`ShouldHaveRecipient`, `ShouldHaveAttachment`, `ShouldContainLink`, and others).
- `MailsinkFixture` (xUnit v3 `IAsyncLifetime`).
- `Testcontainers.Mailsink` builder and container (`WithUser`, port getters, `WaitForMessagesAsync`).

### Bugs found and fixed during testing
- Source-generated JSON ignored property initializers on `init` properties, so chaos rules arrived disabled with probability 0. `ChaosRule` now uses `set`.
- API sends put the machine hostname in the Message-ID. It now uses the sender's domain.
- A delete request whose ids were all invalid could widen to "delete all". Invalid ids are now ignored.
- The SQLite database blob store could deadlock when blob writes ran inside a transaction. They now run under the writer gate but outside the transaction.

### Web UI (partial: `src/Mailsink.Web`)
Written but **not yet compiled or run**:
- `package.json` with dependencies installed, `vite.config.ts` (build output to `Server/wwwroot`, dev proxy), `tsconfig.json`, `index.html`, favicon.
- `src/index.css`: dark and light tokens from §11.5 and the Tailwind v4 theme.
- `src/lib/api.ts` (typed client for every endpoint), `src/lib/format.ts`, `src/lib/app-state.tsx` (theme, account switcher, toasts, SignalR connection with query invalidation, new-message counter, unread count in the tab title, desktop notifications, the `useHotkeys` hook with `g x` chords).
- `src/components/ui.tsx` (Button, IconButton, Input, Select, Tooltip, CodeChip, RelativeTime, Badge, Switch, Checkbox, Tabs, Dialog, Menu, Card, Kbd).
- `src/components/layout.tsx` (AppShell, sidebar with account switcher, mobile drawer, page header, command bar showing port status, shortcut sheet, toaster).
- `src/components/snippets.tsx` (the `</>` API-snippet dialog and snippet builders).
- `src/components/command-palette.tsx` (cmdk).

## Outstanding

### Web UI
Done on 2026-10-17 (second pass). All pages from §11.2 exist under `src/Mailsink.Web/src/pages/`: Emails (search, filter, export, URL state, bulk bar, tag dialog), Email detail (metadata card; Preview with device widths, light/dark frame and remote-images switch; HTML source, Text, Headers, Raw, Attachments, SMTP transcript; Reply/Download/Delete), Mailboxes (folder column, drag-to-move with Ctrl to copy, new/rename/delete folder, flags, expunge), Compose (token inputs, Rich/HTML/Text tabs with live preview, attachments dropzone, advanced headers/template/repeat/tags, "Copy as curl", bulk send, Reply pre-fill), Accounts, Templates, Chaos (rule editor, per-rule toggles, presets), Logs (transcript dialog), Metrics (stat tiles plus a messages-per-minute bar chart with table view), Settings. Shared pieces live in `components/data-table.tsx` (`DataTable`, `Pagination`, `IconTile`, `EmptyState`, `NewMessagesPill`, `SearchInput`, `Toolbar`, `useQueryState`), `components/message-columns.tsx` and `components/connection-snippets.tsx`.

`npm install`, `tsc -b` and `npm run build` all pass; the build lands in `src/Mailsink.Server/wwwroot` (gitignored). `.claude/launch.json` starts the server on alternate ports (HTTP 8026, SMTP 2025, submission 2587, IMAP 2143, IMAPS 2993) because a Mailpit container on this machine holds 8025 and 1025.

Checked in the browser: Emails, detail (including the sanitized HTML preview), Mailboxes, Compose (a message was sent from the form), Accounts, Templates, Chaos, Logs, Metrics and Settings at 1280px; Emails at 375px; light and dark themes. No console errors.

Server fixes made while testing:
- Deep links such as `/emails/{id}` were blank: the SPA uses relative asset URLs (`base: "./"`), so the fallback now serves `index.html` with an injected `<base href="{PathBase}/">` instead of `MapFallbackToFile`. This also makes hosting under a PathBase work.
- The preview iframe must use `sandbox="allow-same-origin"`: with a fully sandboxed (opaque-origin) frame the API's `frame-ancestors 'self'` CSP blocks rendering. Scripts remain blocked.

### Packaging, docs and CI
Done on 2026-10-17:
- [x] `Mailsink.slnx` (XML solution format) at the repo root with all 12 `src/` and 3 `tests/` projects. `dotnet build Mailsink.slnx` and `dotnet test Mailsink.slnx` pass (82 tests).
- [x] `docker/Dockerfile` and `.dockerignore`. Three stages: `node:22-alpine` builds the SPA into `wwwroot`; `sdk:10.0` publishes `Mailsink.Server` framework-dependent for `$TARGETARCH` (both run on `$BUILDPLATFORM`, so multi-arch builds do not emulate); `aspnet:10.0-noble-chiseled` runs as `$APP_UID` with an app-owned `/data` volume, `MAILSINK__DataDir=/data`, `MAILSINK__BindAddress=0.0.0.0`, `MAILSINK__Http__Urls=http://0.0.0.0:8025`, `EXPOSE 8025 1025 1587 1143 1993 1110` and `HEALTHCHECK CMD ["/app/mailsink", "health"]`. Verified locally: the image is 208 MB, `docker inspect` reports `healthy`, the process runs as uid 1654, the UI is served at `/`, SQLite initialises in `/data`, a message sent to the SMTP port is captured and visible over IMAP.
- [x] `.github/workflows/ci.yml`: `dotnet` job (restore, build, the three test projects with TRX upload), `web` job (`npm ci`, `npm run build`), `docker` job (buildx; single-arch build on every push and PR, `linux/amd64,linux/arm64` build and push to `ghcr.io/<owner>/mailsink` with semver and `latest` tags on `v*` tags).
- [x] `README.md`: overview, Docker and `dotnet run` quick start, ports table, configuration (appsettings, `MAILSINK__` env vars, short CLI flags), search syntax, send API, `Mailsink.Testing`, the xUnit fixture, `Testcontainers.Mailsink`, security (fully local, no outbound mail), development.

Still open:
- [ ] `tests/Mailsink.Conformance`: Dovecot `imaptest` scripts against the Docker image.
- [ ] Testcontainers module test (needs the published Docker image).

### Features not implemented (later phases or deferred)
- IMAP: CONDSTORE/QRESYNC (MODSEQ is stored and fetchable but not advertised), IMAP4rev2 behaviour beyond the capability flag, SEARCHRES (`$`), BINARY / BINARY.SIZE, UTF8=ACCEPT, COMPRESS=DEFLATE, SORT, THREAD, QUOTA, MULTIAPPEND, and `[CLOSED]`.
- IMAP SEARCH is evaluated in memory, not pushed down to SQL as §6.10 describes.
- APPEND literals larger than 1 MB are buffered in memory, not streamed to a temp file.
- SMTP: CRAM-MD5 (passwords are stored hashed), BDAT/CHUNKING, per-IP connection caps and rate limits (§14.4).
- OpenTelemetry exporter and Prometheus `/metrics` endpoint (the meter exists; nothing exports it yet).
- Native AOT publish (source-generated JSON is in place; SignalR and Scalar still use reflection).
- `System.CommandLine` (CLI flags currently use `AddCommandLine` switch mappings).
- Phase 6 extras: HTML compatibility report, link checker, spam score, webhooks.

## Deviations from the design doc
1. **SMTP is a custom implementation on the shared Pipelines networking layer, not the `SmtpServer` NuGet package.** The doc lists this as the mitigation for that dependency. It gives full control over chaos at the Connect and Helo stages and over transcripts.
2. Hand-written ADO.NET instead of Dapper. This is AOT-friendly, which §18 allows.
3. The FTS5 table stores its content instead of using `content=''`, so deletes are simple.
4. Protocol transcripts are an in-memory ring buffer and are lost on restart.
5. Orphaned blobs are deleted inline when the last reference goes, not by a background sweeper.
6. A new `Mailsink.Hosting` project holds the composition root shared by Server and Testing.
7. The testing library returns a `ReceivedMessage` wrapper (captured metadata plus a parsed `MimeMessage`) instead of a bare `CapturedMessage`.
8. Listeners bind to `127.0.0.1` by default (`BindAddress`). Docker should set `MAILSINK__BindAddress=0.0.0.0`.

## How to verify the current state
```bash
dotnet build Mailsink.slnx
dotnet test Mailsink.slnx
docker build -f docker/Dockerfile -t mailsink .
dotnet test tests/Mailsink.Core.Tests
dotnet test tests/Mailsink.Imap.Tests
dotnet test tests/Mailsink.Integration.Tests
dotnet run --project src/Mailsink.Server -- --storage InMemory
```
