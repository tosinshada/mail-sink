import { Code2 } from "lucide-react";
import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { api, type Info } from "@/lib/api";
import { copyText } from "@/lib/format";
import { Button, Dialog, IconButton, Tabs, TabsContent, TabsList, TabsTrigger } from "./ui";

/** Builds code snippets that reproduce the current view; receives live server info (ports, base URL). */
export type Snippet = (ctx: SnippetContext) => { title: string; description?: string; samples: { label: string; code: string }[] };

export interface SnippetContext {
  base: string;
  host: string;
  info: Info | undefined;
  port: (name: string, fallback: number) => number;
}

export function useSnippetContext(): SnippetContext {
  const info = useQuery({ queryKey: ["info"], queryFn: api.info });
  const base = window.location.origin + new URL(".", document.baseURI).pathname.replace(/\/$/, "");
  return {
    base,
    host: window.location.hostname || "localhost",
    info: info.data,
    port: (name, fallback) => info.data?.endpoints.find((e) => e.name === name)?.port ?? fallback,
  };
}

export function ApiSnippetButton({ snippet }: { snippet: Snippet }) {
  const [open, setOpen] = useState(false);
  const ctx = useSnippetContext();
  const content = snippet(ctx);
  return (
    <>
      <IconButton label="API snippet" onClick={() => setOpen(true)}>
        <Code2 className="size-4" />
      </IconButton>
      <Dialog open={open} onOpenChange={setOpen} title={content.title} description={content.description} wide>
        <SnippetTabs samples={content.samples} />
      </Dialog>
    </>
  );
}

export function SnippetTabs({ samples }: { samples: { label: string; code: string }[] }) {
  const [copied, setCopied] = useState<string | null>(null);
  return (
    <Tabs defaultValue={samples[0]?.label}>
      <TabsList>
        {samples.map((s) => (
          <TabsTrigger key={s.label} value={s.label}>
            {s.label}
          </TabsTrigger>
        ))}
      </TabsList>
      {samples.map((s) => (
        <TabsContent key={s.label} value={s.label} className="relative mt-4">
          <pre className="max-h-[50vh] overflow-auto rounded-md border border-border bg-bg p-4 font-mono text-[12.5px] leading-relaxed text-fg/90">{s.code}</pre>
          <Button
            size="sm"
            className="absolute right-2 top-2"
            onClick={() => {
              void copyText(s.code);
              setCopied(s.label);
              setTimeout(() => setCopied(null), 1200);
            }}
          >
            {copied === s.label ? "Copied" : "Copy"}
          </Button>
        </TabsContent>
      ))}
    </Tabs>
  );
}

export const snippets = {
  emails: (query: string): Snippet => (ctx) => ({
    title: "Query captured mail",
    description: "The same list, from a script or a test.",
    samples: [
      {
        label: "curl",
        code: `curl "${ctx.base}/api/v1/messages?limit=50${query ? `&query=${encodeURIComponent(query)}` : ""}"

# Block until a matching message arrives (no sleeps in tests)
curl "${ctx.base}/api/v1/wait?query=${encodeURIComponent(query || "subject:welcome")}&timeout=30s"`,
      },
      {
        label: "C# (HttpClient)",
        code: `using var http = new HttpClient { BaseAddress = new Uri("${ctx.base}/") };
var list = await http.GetFromJsonAsync<JsonElement>("api/v1/messages?query=${encodeURIComponent(query)}");
foreach (var m in list.GetProperty("messages").EnumerateArray())
    Console.WriteLine(m.GetProperty("subject").GetString());`,
      },
      {
        label: "Mailsink.Testing",
        code: `await using var mail = await MailsinkServer.StartAsync();
// point your app's SMTP client at 127.0.0.1:mail.SmtpPort
var message = await mail.WaitForMessageAsync(m => m.Subject!.Contains("Welcome"));
message.ShouldHaveRecipient("bob@test.local");
var link = message.HtmlBody.ShouldContainLink("https://app.test/confirm");`,
      },
      {
        label: "SMTP",
        code: `swaks --server ${ctx.host}:${ctx.port("smtp", 1025)} --from app@example.com --to bob@${ctx.info?.defaultDomain ?? "test.local"} --header "Subject: Hello"

# .NET
using var client = new System.Net.Mail.SmtpClient("${ctx.host}", ${ctx.port("smtp", 1025)});
client.Send("app@example.com", "bob@${ctx.info?.defaultDomain ?? "test.local"}", "Hello", "Body");`,
      },
    ],
  }),
  message: (id: string): Snippet => (ctx) => ({
    title: "This message via the API",
    samples: [
      {
        label: "curl",
        code: `curl ${ctx.base}/api/v1/messages/${id}
curl ${ctx.base}/api/v1/messages/${id}/raw -o message.eml
curl ${ctx.base}/api/v1/messages/${id}/headers
curl -X DELETE ${ctx.base}/api/v1/messages/${id}`,
      },
      {
        label: "C# (MailKit IMAP)",
        code: `using var imap = new MailKit.Net.Imap.ImapClient();
await imap.ConnectAsync("${ctx.host}", ${ctx.port("imap", 1143)}, SecureSocketOptions.StartTlsWhenAvailable);
await imap.AuthenticateAsync("bob@${ctx.info?.defaultDomain ?? "test.local"}", "any");
await imap.Inbox.OpenAsync(FolderAccess.ReadOnly);
var latest = await imap.Inbox.GetMessageAsync(imap.Inbox.Count - 1);`,
      },
    ],
  }),
  compose: (body: unknown): Snippet => (ctx) => ({
    title: "Send with the API",
    samples: [
      {
        label: "curl",
        code: `curl -X POST ${ctx.base}/api/v1/send \\
  -H "Content-Type: application/json" \\
  -d '${JSON.stringify(body, null, 2).replace(/'/g, "'\\''")}'`,
      },
      {
        label: "SMTP submission",
        code: `swaks --server ${ctx.host}:${ctx.port("submission", 1587)} --auth LOGIN --auth-user alice@${ctx.info?.defaultDomain ?? "test.local"} --auth-password any \\
  --from alice@${ctx.info?.defaultDomain ?? "test.local"} --to bob@${ctx.info?.defaultDomain ?? "test.local"}`,
      },
    ],
  }),
  mailboxes: (account: string | null): Snippet => (ctx) => ({
    title: "Read these folders over IMAP",
    samples: [
      {
        label: "C# (MailKit)",
        code: `using var imap = new MailKit.Net.Imap.ImapClient();
await imap.ConnectAsync("${ctx.host}", ${ctx.port("imap", 1143)}, SecureSocketOptions.StartTlsWhenAvailable);
await imap.AuthenticateAsync("${account ?? `bob@${ctx.info?.defaultDomain ?? "test.local"}`}", "any");
foreach (var folder in await imap.GetFoldersAsync(imap.PersonalNamespaces[0]))
    Console.WriteLine($"{folder.FullName} {folder.Attributes}");`,
      },
      {
        label: "curl",
        code: `curl ${ctx.base}/api/v1/accounts/${encodeURIComponent(account ?? "bob@test.local")}/mailboxes
curl ${ctx.base}/api/v1/accounts/${encodeURIComponent(account ?? "bob@test.local")}/mailboxes/INBOX/messages`,
      },
      {
        label: "Thunderbird / Outlook",
        code: `Incoming (IMAP): ${ctx.host}  port ${ctx.port("imap", 1143)}  STARTTLS optional  (IMAPS ${ctx.port("imaps", 1993)})
Outgoing (SMTP): ${ctx.host}  port ${ctx.port("submission", 1587)}  normal password
Username: ${account ?? "your-address@" + (ctx.info?.defaultDomain ?? "test.local")}
Password: ${ctx.info?.authMode === "Registry" ? "(the account password)" : "anything (accept-any mode)"}`,
      },
    ],
  }),
  generic: (title: string, path: string, method = "GET", body?: unknown): Snippet => (ctx) => ({
    title,
    samples: [
      {
        label: "curl",
        code: `curl${method !== "GET" ? ` -X ${method}` : ""} ${ctx.base}/api/v1/${path}${body ? ` \\\n  -H "Content-Type: application/json" \\\n  -d '${JSON.stringify(body, null, 2)}'` : ""}`,
      },
    ],
  }),
};
