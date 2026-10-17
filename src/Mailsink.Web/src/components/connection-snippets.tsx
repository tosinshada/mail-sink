import { useSnippetContext, SnippetTabs } from "./snippets";
import { CodeChip } from "./ui";

/** Copyable "send your first mail" instructions used by empty states (design §11.3). */
export function ConnectionSnippets() {
  const ctx = useSnippetContext();
  const domain = ctx.info?.defaultDomain ?? "test.local";
  const smtp = ctx.port("smtp", 1025);
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-center gap-2 text-[13px] text-muted">
        SMTP host <CodeChip value={ctx.host} /> port <CodeChip value={String(smtp)} /> · IMAP port <CodeChip value={String(ctx.port("imap", 1143))} /> · any sender, any recipient
      </div>
      <SnippetTabs
        samples={[
          {
            label: "swaks",
            code: `swaks --server ${ctx.host}:${smtp} --from app@example.com --to bob@${domain} \\
  --header "Subject: Hello from swaks" --body "It works."`,
          },
          {
            label: "curl (API)",
            code: `curl -X POST ${ctx.base}/api/v1/send -H "Content-Type: application/json" -d '{
  "from": { "email": "app@example.com" },
  "to": [{ "email": "bob@${domain}" }],
  "subject": "Hello from the API",
  "text": "It works."
}'`,
          },
          {
            label: ".NET",
            code: `using var client = new System.Net.Mail.SmtpClient("${ctx.host}", ${smtp});
client.Send("app@example.com", "bob@${domain}", "Hello from .NET", "It works.");`,
          },
          {
            label: "Python",
            code: `import smtplib
from email.message import EmailMessage

msg = EmailMessage()
msg["From"], msg["To"], msg["Subject"] = "app@example.com", "bob@${domain}", "Hello from Python"
msg.set_content("It works.")
with smtplib.SMTP("${ctx.host}", ${smtp}) as s:
    s.send_message(msg)`,
          },
          {
            label: "Node",
            code: `import nodemailer from "nodemailer";
const transport = nodemailer.createTransport({ host: "${ctx.host}", port: ${smtp}, secure: false });
await transport.sendMail({ from: "app@example.com", to: "bob@${domain}", subject: "Hello from Node", text: "It works." });`,
          },
        ]}
      />
    </div>
  );
}
