<h1 align="center">Mailsink</h1>

<p align="center">
  <b>A fake mail server for development and testing.</b><br>
  Catch every email your app sends, read it in a clean web UI or any real mail client,<br>
  and assert on it from your tests. Nothing ever reaches a real inbox.
</p>

<p align="center">
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet&logoColor=white">
  <img alt="SMTP · IMAP · POP3" src="https://img.shields.io/badge/SMTP%20%C2%B7%20IMAP%20%C2%B7%20POP3-supported-0EA5C6?style=flat-square">
  <img alt="Docker: amd64 · arm64" src="https://img.shields.io/badge/docker-amd64%20%C2%B7%20arm64-2496ED?style=flat-square&logo=docker&logoColor=white">
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-3a3a3a?style=flat-square"></a>
</p>

<p align="center">
  <a href="#why-mailsink">Why Mailsink</a> ·
  <a href="#features">Features</a> ·
  <a href="#built-for-tests">Built for tests</a> ·
  <a href="#get-started">Get started</a> ·
  <a href="#configuration">Configuration</a> ·
  <a href="#security">Security</a>
</p>

<br>

https://github.com/user-attachments/assets/0e388d9f-aaf3-4d77-ade8-4752b268fb1e

<p align="center">
  <sub>Switch your SMTP and IMAP to Mailsink, capture every outgoing and incoming email, and capture SMS locally.</sub>
</p>

<br>

<table>
  <tr>
    <td width="25%" valign="top">
      <h3>📥 Catch everything</h3>
      Point your app at <code>localhost:1025</code> and every message lands in Mailsink. Any sender, any recipient, no setup.
    </td>
    <td width="25%" valign="top">
      <h3>📬 Real mail clients</h3>
      A full IMAP server means Thunderbird, Outlook and MailKit can browse, read, flag, move and delete captured mail.
    </td>
    <td width="25%" valign="top">
      <h3>🧪 Test-first</h3>
      Run the whole server inside your test process on random ports, then wait for the email and assert on it. No sleeps, no flakes.
    </td>
    <td width="25%" valign="top">
      <h3>🔒 Fully local</h3>
      There is no relay. Mail never leaves Mailsink, so a misconfigured staging box can't email your customers.
    </td>
  </tr>
</table>

<br>

## Why Mailsink

Most fake mail servers give you either a nice inbox to look at or a mail server to test against. Mailsink gives you both: the capture and web UI you'd get from Mailpit, the IMAP server and in-process test library you'd get from GreenMail, in one small .NET binary or Docker image.

Use it on your laptop while you build sign-up flows, in Docker Compose next to your services, or inside your test suite to prove the right email went to the right person with the right link.

## Features

<table>
  <tr>
    <td width="50%" valign="top">
      <h3>An inbox for your app</h3>
      Search across everything that was sent, preview HTML safely in a sandboxed frame, inspect raw source and headers, download attachments and replay the SMTP conversation that delivered each message. Compose mail from the UI, manage accounts and folders, and watch new mail arrive live.
    </td>
    <td width="50%" valign="top">
      <h3>Every protocol your app speaks</h3>
      SMTP capture with no auth, authenticated submission that files a copy in <code>Sent</code>, optional SMTPS, IMAP4rev1 with IDLE, MOVE and STARTTLS, IMAPS, and POP3 when you need it. TLS works out of the box with a generated certificate.
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <h3>Break things on purpose</h3>
      Chaos rules make Mailsink reject recipients, delay delivery or drop the connection at any SMTP stage, for a specific sender or recipient. Find out how your app handles a flaky mail server before production does.
    </td>
    <td width="50%" valign="top">
      <h3>Automate everything</h3>
      A REST API with OpenAPI and interactive Scalar docs at <code>/api/docs</code>, real-time events over SignalR, and one search syntax shared by the UI, the API and the test helpers: <code>from:</code>, <code>to:</code>, <code>subject:</code>, <code>has:attachment</code>, <code>is:unread</code> and more.
    </td>
  </tr>
</table>

<br>

## SMS

Mailsink also catches text messages. Point your app's SMS sender at one endpoint with a message and a list of phone numbers:

```bash
curl -X POST http://localhost:8025/api/v1/sms \
  -H "Content-Type: application/json" \
  -d '{"message": "Your code is 482913", "to": ["+1 (555) 123-4567", "07700 900123"], "from": {"name": "Acme", "number": "+1 555 555 0199"}}'
```

`from` is optional, and so is each of its parts. A missing name or number falls back to `Mailsink:Sms:DefaultSenderName` (`Mailsink`) and `Mailsink:Sms:DefaultSenderNumber` (`+15555550100`).

The **SMS** page lists every number that received a message, with counts, unread, senders and timings, plus totals across all phones. Click a number to see its mock iPhone, with the messages stacked as iMessage bubbles and updated live. Validation is loose on purpose: spaces, dashes, dots and parentheses are ignored, one leading `+` is allowed, and 7–15 digits are required. If any number is invalid, the whole request returns `400` and nothing is stored. `GET /api/v1/sms/phones`, `GET /api/v1/sms/phones/{number}` and `GET /api/v1/sms/stats` return what was captured so tests can assert on it.

## Built for tests

`Mailsink.Testing` starts the full server inside your test process with in-memory storage and OS-assigned ports, so tests run in parallel without port clashes. Send, wait, assert.

```csharp
using Mailsink.Testing;

await using var mail = await MailsinkServer.StartAsync();

await myApp.SendPasswordResetAsync("bob@test.local", smtpHost: mail.Host, smtpPort: mail.SmtpPort);

var message = await mail.WaitForMessageAsync(m => m.To.Contains("bob@test.local"));

message.ShouldHaveSubject("Reset your password")
       .ShouldBeFrom("noreply@myapp.test")
       .ShouldContainText("expires in 24 hours");

var resetLink = message.ExtractLinks().Single(l => l.Contains("/reset?token="));
```

- **Wait, don't sleep:** `WaitForMessageAsync` and `WaitForMessagesAsync` return as soon as mail arrives.
- **Fluent assertions:** `ShouldHaveRecipient`, `ShouldHaveSubject`, `ShouldBeFrom`, `ShouldHaveAttachment`, `ShouldContainText`, `ShouldHaveHeader`, `ShouldContainLink`.
- **Accounts and chaos:** `WithUser(address, password)` for IMAP tests, `AddChaosRule(rule)` for failure tests, `ResetAsync()` between tests.
- **REST API too:** pass `http: true` to `StartAsync` and the API is served at `mail.ApiBaseUri`.

### xUnit

`Mailsink.Testing.Xunit` gives you one server per test class or collection:

```csharp
using Mailsink.Testing.Xunit;

public class SignupEmailTests(MailsinkFixture mail) : IClassFixture<MailsinkFixture>
{
    [Fact]
    public async Task Sends_welcome_email()
    {
        await mail.Server.ResetAsync();
        // ... exercise the app against mail.Server.SmtpPort ...
        var message = await mail.Server.WaitForMessageAsync(m => m.Subject == "Welcome");
        message.ShouldHaveRecipient("new.user@test.local");
    }
}
```

Derive from `MailsinkFixture` and override `Configure(MailsinkOptions)` or `EnableHttp` to change the options.

### Testcontainers

`Testcontainers.Mailsink` runs the Docker image, for services that need a real network endpoint. Ports are mapped to random host ports and the builder waits until Mailsink is healthy.

```csharp
using Testcontainers.Mailsink;

await using var container = new MailsinkBuilder()
    .WithUser("bob@test.local", "secret")
    .WithPop3()
    .Build();
await container.StartAsync();

// ... app sends to container.Hostname:container.GetSmtpPort() ...

var result = await container.WaitForMessagesAsync(query: "to:bob@test.local", count: 1);
```

Use `.WithImage("...")` to pin a version or point at a locally built tag.

## Get started

### Docker

```bash
docker run --rm -p 8025:8025 -p 1025:1025 -p 1587:1587 -p 1143:1143 -p 1993:1993 -v mailsink-data:/data ghcr.io/<owner>/mailsink:latest
```

Open http://localhost:8025, point your app's SMTP settings at `localhost:1025`, and send something. Your mail is there.

Prefer Compose? [`examples/docker-compose/compose.yaml`](examples/docker-compose/compose.yaml) runs Mailsink with a named volume, plus commented examples for seeded users and an app service that waits for Mailsink to be healthy.

To build the image yourself:

```bash
docker build -f docker/Dockerfile -t mailsink .
```

### From source

Requires the .NET 10 SDK and, for the web UI, Node 22.

```bash
cd src/Mailsink.Web && npm ci && npm run build && cd ../..
```

```bash
dotnet run --project src/Mailsink.Server
```

Add `-- --storage InMemory` to keep everything in memory. Outside Docker, Mailsink only listens on `127.0.0.1`; pass `--bind 0.0.0.0` to accept connections from other machines.

### Ports

| Service | Port | Notes |
|---|---|---|
| Web UI and API | 8025 | |
| SMTP capture | 1025 | No auth. Point your app here |
| SMTP submission | 1587 | Auth required, saves a copy to `Sent` |
| SMTPS | 1465 | Off by default |
| IMAP | 1143 | STARTTLS optional |
| IMAPS | 1993 | |
| POP3 | 1110 | Off by default |

TLS endpoints use a self-signed certificate generated on first start. Have your client skip validation, or download the certificate from `GET /api/v1/tls/cert` and trust it. Bring your own with `MAILSINK__Tls__CertificatePath`.

## Configuration

Mailsink works with no configuration. When you need to change something, every setting can come from `appsettings.json`, an environment variable (`MAILSINK__Smtp__Capture__Port=25`) or a command-line flag, in that order of precedence.

| Flag | What it does | Default |
|---|---|---|
| `--storage` | `Sqlite` or `InMemory` | `Sqlite` |
| `--data-dir` | Where the database, attachments and certificate live | `./data` (`/data` in Docker) |
| `--bind` | Address for SMTP, IMAP and POP3 | `127.0.0.1` (`0.0.0.0` in Docker) |
| `--http` | Web UI and API URL | `http://127.0.0.1:8025` |
| `--auth-mode` | `AcceptAny` (anyone can log in) or `Registry` (only known users) | `AcceptAny` |
| `--domain` | Domain added to bare usernames | `test.local` |
| `--max-messages` | Oldest mail is removed past this count (0 disables) | 500 |
| `--ui-auth-file` | htpasswd file protecting the UI and API | none |

Port flags (`--smtp-port`, `--imap-port` and friends) and any other setting (`--Mailsink:Section:Key=value`) work too. The shipped `appsettings.json` lists every key with its default, including plus-address folders, retention by age and size, IMAP options and CORS.

## Security

Mailsink has no outbound path: no relay and no release, so captured mail can never reach a real inbox. Listeners bind to loopback by default, and the UI and API can sit behind basic auth with `--ui-auth-file`. `/api/v1/health` stays open for health checks, and the Docker image runs as a non-root user.

## Contributing

```bash
dotnet build Mailsink.slnx
```

```bash
dotnet test Mailsink.slnx
```

For UI work, `npm run dev` in `src/Mailsink.Web` starts Vite on port 5173 and proxies to a server on 8025. The design lives in [docs/TECHNICAL_DESIGN.md](docs/TECHNICAL_DESIGN.md) and progress against it in [docs/IMPLEMENTATION_STATUS.md](docs/IMPLEMENTATION_STATUS.md).

## License

Mailsink is released under the [MIT License](LICENSE).
