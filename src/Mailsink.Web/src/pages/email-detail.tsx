import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ArrowLeft, Download, ExternalLink, FileText, Image, Mail, MailOpen, Monitor, Moon, Paperclip, Reply, Smartphone, Sun, Tablet, Tag, Trash2 } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { IconTile } from "@/components/data-table";
import { PageHeader } from "@/components/layout";
import { TagList, sourceLabel } from "@/components/message-columns";
import { snippets } from "@/components/snippets";
import { Badge, Button, CodeChip, IconButton, Input, Menu, MenuContent, MenuItem, MenuTrigger, RelativeTime, Spinner, Switch, Tabs, TabsContent, TabsList, TabsTrigger, Tooltip } from "@/components/ui";
import { api, urls, type Address, type MessageDetail, type Transcript } from "@/lib/api";
import { useApp, useHotkeys } from "@/lib/app-state";
import { absoluteTime, cn, formatAddress, formatSize, initials } from "@/lib/format";
import { TagDialog } from "./emails";

export function EmailDetailPage() {
  const { id = "" } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { toast } = useApp();
  const detail = useQuery({ queryKey: ["message", id], queryFn: () => api.message(id), enabled: !!id });
  const [tagOpen, setTagOpen] = useState(false);

  const invalidate = () => {
    void queryClient.invalidateQueries({ queryKey: ["message", id] });
    void queryClient.invalidateQueries({ queryKey: ["messages"] });
  };
  const setRead = useMutation({ mutationFn: (read: boolean) => api.setRead([id], read), onSuccess: invalidate });
  const remove = useMutation({
    mutationFn: () => api.deleteMessages([id]),
    onSuccess: () => {
      toast({ title: "Message deleted", tone: "success" });
      void queryClient.invalidateQueries({ queryKey: ["messages"] });
      navigate("/emails");
    },
    onError: (e) => toast({ title: "Delete failed", description: String(e), tone: "danger" }),
  });

  // Opening a message marks it read, as a mail client would (the API does not do this implicitly).
  useEffect(() => {
    if (detail.data && !detail.data.summary.isRead) setRead.mutate(true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [detail.data?.summary.id]);

  useHotkeys(
    {
      Backspace: () => navigate(-1),
      "#": () => confirm("Delete this message?") && remove.mutate(),
      e: () => detail.data && setRead.mutate(!detail.data.summary.isRead),
      r: () => detail.data && reply(detail.data),
    },
    [detail.data],
  );

  const reply = (d: MessageDetail) => {
    const s = d.summary;
    const to: Address[] = d.replyTo.length > 0 ? d.replyTo : s.from ? [s.from] : [];
    const quote = (d.text ?? "")
      .split("\n")
      .map((l) => `> ${l}`)
      .join("\n");
    navigate("/compose", {
      state: {
        reply: {
          to,
          subject: s.subject?.startsWith("Re:") ? s.subject : `Re: ${s.subject ?? ""}`,
          text: `\n\nOn ${s.date ? new Date(s.date).toLocaleString() : "an unknown date"}, ${s.from ? formatAddress(s.from) : "someone"} wrote:\n${quote}`,
          headers: s.messageId ? { "In-Reply-To": s.messageId, References: s.messageId } : undefined,
        },
      },
    });
  };

  if (detail.isLoading) {
    return (
      <div className="flex h-60 items-center justify-center">
        <Spinner />
      </div>
    );
  }

  if (detail.isError || !detail.data) {
    return (
      <div className="py-20 text-center">
        <p className="text-[16px] font-semibold">Message not found</p>
        <p className="mt-1 text-muted">It may have been deleted or expired under the retention policy.</p>
        <Button className="mt-5" onClick={() => navigate("/emails")}>
          <ArrowLeft className="size-4" /> Back to Emails
        </Button>
      </div>
    );
  }

  const d = detail.data;
  const s = d.summary;

  return (
    <>
      <PageHeader
        breadcrumb={
          <span className="flex items-center gap-1.5">
            <Link to="/emails" className="hover:text-fg">
              Emails
            </Link>
            <span>/</span>
            <span className="truncate text-fg/80">{s.subject || "(no subject)"}</span>
          </span>
        }
        title={s.subject || "(no subject)"}
        actions={
          <>
            <Button onClick={() => reply(d)}>
              <Reply className="size-4" /> Reply
            </Button>
            <Menu>
              <MenuTrigger asChild>
                <IconButton label="Download">
                  <Download className="size-4" />
                </IconButton>
              </MenuTrigger>
              <MenuContent>
                <MenuItem icon={<Download />} onSelect={() => window.open(urls.raw(s.id, true))}>
                  Download .eml
                </MenuItem>
                <MenuItem icon={<ExternalLink />} onSelect={() => window.open(urls.raw(s.id), "_blank")}>
                  Open raw in new tab
                </MenuItem>
              </MenuContent>
            </Menu>
            <Menu>
              <MenuTrigger asChild>
                <IconButton label="More">
                  <span className="text-[16px] leading-none">⋯</span>
                </IconButton>
              </MenuTrigger>
              <MenuContent>
                <MenuItem icon={s.isRead ? <Mail /> : <MailOpen />} shortcut="e" onSelect={() => setRead.mutate(!s.isRead)}>
                  Mark {s.isRead ? "unread" : "read"}
                </MenuItem>
                <MenuItem icon={<Tag />} onSelect={() => setTagOpen(true)}>
                  Edit tags…
                </MenuItem>
                <MenuItem icon={<Trash2 />} danger shortcut="#" onSelect={() => confirm("Delete this message?") && remove.mutate()}>
                  Delete
                </MenuItem>
              </MenuContent>
            </Menu>
          </>
        }
        snippet={snippets.message(s.id)}
      />

      <section className="mb-5 flex flex-col gap-4 rounded-lg border border-border bg-bg-subtle p-4 md:flex-row md:items-start">
        <IconTile text={initials(s.from)} seed={s.from?.email ?? "?"} />
        <div className="min-w-0 flex-1 space-y-1 text-[14px]">
          <AddressLine label="From" addresses={s.from ? [s.from] : []} />
          <AddressLine label="To" addresses={s.to} />
          {s.cc.length > 0 && <AddressLine label="Cc" addresses={s.cc} />}
          {s.bcc.length > 0 && <AddressLine label="Bcc" addresses={s.bcc} />}
          {d.replyTo.length > 0 && <AddressLine label="Reply-To" addresses={d.replyTo} />}
          <div className="flex flex-wrap items-center gap-x-4 gap-y-1 pt-1 text-[13px] text-muted">
            <span>
              Received <RelativeTime iso={s.receivedAt} />
            </span>
            {s.date && <span>Sent {absoluteTime(s.date).split(" · ")[0]}</span>}
            {s.messageId && <CodeChip value={s.messageId} display={s.messageId.length > 40 ? `${s.messageId.slice(0, 38)}…` : s.messageId} />}
            <TagList tags={s.tags} max={6} />
          </div>
        </div>
      </section>

      <section className="mb-6 grid grid-cols-2 gap-x-6 gap-y-3 rounded-lg border border-border p-4 text-[13.5px] sm:grid-cols-3 lg:grid-cols-4">
        <Meta label="MAIL FROM">{d.envelope.mailFrom ? <CodeChip value={d.envelope.mailFrom} /> : <span className="text-muted">(empty)</span>}</Meta>
        <Meta label="RCPT TO">
          <span className="flex flex-wrap gap-1">
            {d.envelope.rcptTo.map((r) => (
              <CodeChip key={r} value={r} />
            ))}
          </span>
        </Meta>
        <Meta label="Client">
          {d.envelope.clientIp ? <CodeChip value={d.envelope.clientIp} /> : <span className="text-muted">—</span>}
          {d.envelope.helo && <span className="ml-2 text-muted">HELO {d.envelope.helo}</span>}
        </Meta>
        <Meta label="TLS">{d.envelope.secure ? <Badge tone="accent">encrypted</Badge> : <Badge>plaintext</Badge>}</Meta>
        <Meta label="Auth user">{d.envelope.authUser ?? <span className="text-muted">none</span>}</Meta>
        <Meta label="Size">{formatSize(s.size)}</Meta>
        <Meta label="Source">
          <Badge tone="info">{sourceLabel[s.source] ?? s.source}</Badge>
        </Meta>
        <Meta label="Session">
          {d.envelope.sessionId ? (
            <Link to={`/logs/${d.envelope.sessionId}`} className="link-dotted font-mono text-[12.5px]">
              {d.envelope.sessionId}
            </Link>
          ) : (
            <span className="text-muted">—</span>
          )}
        </Meta>
      </section>

      <BodyTabs detail={d} />

      <TagDialog targets={tagOpen ? [s] : null} onClose={() => setTagOpen(false)} knownTags={[]} />
    </>
  );
}

function AddressLine({ label, addresses }: { label: string; addresses: Address[] }) {
  return (
    <div className="flex gap-2">
      <span className="w-16 shrink-0 text-muted">{label}</span>
      <span className="min-w-0 break-words">
        {addresses.length === 0 && <span className="text-muted">—</span>}
        {addresses.map((a, i) => (
          <span key={a.email + i}>
            {i > 0 && ", "}
            {a.name ? (
              <>
                {a.name} <span className="text-muted">&lt;{a.email}&gt;</span>
              </>
            ) : (
              a.email
            )}
          </span>
        ))}
      </span>
    </div>
  );
}

function Meta({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="min-w-0">
      <div className="mb-1 text-[12px] font-medium uppercase tracking-wide text-muted">{label}</div>
      <div className="min-w-0 break-words">{children}</div>
    </div>
  );
}

const devices = [
  { key: "desktop", label: "Desktop", width: "100%", icon: Monitor },
  { key: "tablet", label: "Tablet (768px)", width: "768px", icon: Tablet },
  { key: "mobile", label: "Mobile (375px)", width: "375px", icon: Smartphone },
] as const;

function BodyTabs({ detail: d }: { detail: MessageDetail }) {
  const s = d.summary;
  const [tab, setTab] = useState(d.html ? "preview" : d.text ? "text" : "headers");
  const [device, setDevice] = useState<(typeof devices)[number]["key"]>("desktop");
  const [lightBg, setLightBg] = useState(true);
  const [remote, setRemote] = useState(false);
  const [headerFilter, setHeaderFilter] = useState("");

  const headers = useQuery({ queryKey: ["message", s.id, "headers"], queryFn: () => api.headers(s.id), enabled: tab === "headers" });
  const raw = useQuery({ queryKey: ["message", s.id, "raw"], queryFn: () => api.raw(s.id), enabled: tab === "raw" });
  const transcript = useQuery({ queryKey: ["message", s.id, "transcript"], queryFn: () => api.transcriptFor(s.id), enabled: tab === "transcript", retry: false });
  const filteredHeaders = useMemo(() => {
    const q = headerFilter.toLowerCase();
    return (headers.data ?? []).filter((h) => !q || h.name.toLowerCase().includes(q) || h.value.toLowerCase().includes(q));
  }, [headers.data, headerFilter]);
  const width = devices.find((x) => x.key === device)!.width;

  return (
    <Tabs value={tab} onValueChange={setTab}>
      <TabsList>
        {d.html && <TabsTrigger value="preview">Preview</TabsTrigger>}
        {d.html && <TabsTrigger value="html">HTML source</TabsTrigger>}
        <TabsTrigger value="text">Text</TabsTrigger>
        <TabsTrigger value="headers">Headers</TabsTrigger>
        <TabsTrigger value="raw">Raw</TabsTrigger>
        <TabsTrigger value="attachments">Attachments ({d.attachments.length})</TabsTrigger>
        <TabsTrigger value="transcript">SMTP transcript</TabsTrigger>
      </TabsList>

      <TabsContent value="preview" className="mt-4">
        <div className="mb-3 flex flex-wrap items-center gap-2">
          <div className="flex rounded-md border border-border bg-surface p-0.5">
            {devices.map(({ key, label, icon: Icon }) => (
              <Tooltip key={key} content={label}>
                <button aria-label={label} onClick={() => setDevice(key)} className={cn("rounded px-2.5 py-1.5 text-muted transition-colors hover:text-fg", device === key && "bg-surface-active text-fg")}>
                  <Icon className="size-4" />
                </button>
              </Tooltip>
            ))}
          </div>
          <Tooltip content={lightBg ? "Email background: light" : "Email background: dark"}>
            <button aria-label="Toggle email background" onClick={() => setLightBg((v) => !v)} className="rounded-md border border-border bg-surface p-2 text-muted hover:text-fg">
              {lightBg ? <Sun className="size-4" /> : <Moon className="size-4" />}
            </button>
          </Tooltip>
          <Switch checked={remote} onCheckedChange={setRemote} label="Load remote images" />
          <span className="ml-auto text-[12.5px] text-muted">Scripts are removed and the frame is sandboxed.</span>
        </div>
        <div className="overflow-auto rounded-lg border border-border bg-bg-subtle p-3">
          <iframe
            key={`${remote}`}
            title="Email preview"
            // allow-same-origin is needed for the server's frame-ancestors 'self' CSP to match; scripts stay blocked.
            sandbox="allow-same-origin"
            src={urls.html(s.id, remote)}
            className="mx-auto block h-[70vh] min-h-[480px] rounded-md border border-border transition-[width]"
            style={{ width, maxWidth: "100%", background: lightBg ? "#ffffff" : "#111111", colorScheme: lightBg ? "light" : "dark" }}
          />
        </div>
      </TabsContent>

      <TabsContent value="html" className="mt-4">
        <Source text={d.html ?? ""} />
      </TabsContent>

      <TabsContent value="text" className="mt-4">
        {d.text ? <pre className="whitespace-pre-wrap rounded-lg border border-border bg-bg-subtle p-4 font-sans text-[14px] leading-relaxed">{d.text}</pre> : <Empty>This message has no text part.</Empty>}
      </TabsContent>

      <TabsContent value="headers" className="mt-4">
        <Input value={headerFilter} onChange={(e) => setHeaderFilter(e.target.value)} placeholder="Filter headers…" className="mb-3 max-w-sm" />
        {headers.isLoading ? (
          <Spinner />
        ) : (
          <div className="overflow-hidden rounded-lg border border-border">
            <table className="w-full text-[13.5px]">
              <tbody>
                {filteredHeaders.map((h, i) => (
                  <tr key={i} className="border-t border-border first:border-t-0">
                    <td className="w-48 px-3 py-2 align-top font-medium text-muted">{h.name}</td>
                    <td className="break-all px-3 py-2 font-mono text-[12.5px]">{h.value}</td>
                  </tr>
                ))}
                {filteredHeaders.length === 0 && (
                  <tr>
                    <td className="px-3 py-6 text-center text-muted" colSpan={2}>
                      No headers match.
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </TabsContent>

      <TabsContent value="raw" className="mt-4">
        {raw.isLoading ? <Spinner /> : <Source text={raw.data ?? ""} wrap />}
      </TabsContent>

      <TabsContent value="attachments" className="mt-4">
        {d.attachments.length === 0 ? (
          <Empty>No attachments.</Empty>
        ) : (
          <ul className="divide-y divide-border overflow-hidden rounded-lg border border-border">
            {d.attachments.map((a) => (
              <li key={a.partId} className="flex items-center gap-3 px-4 py-3">
                <span className="flex size-9 items-center justify-center rounded-md border border-border bg-surface text-muted">
                  {a.contentType.startsWith("image/") ? <Image className="size-4" /> : a.contentType.includes("text") ? <FileText className="size-4" /> : <Paperclip className="size-4" />}
                </span>
                <span className="min-w-0 flex-1">
                  <span className="block truncate font-medium">{a.fileName ?? `(unnamed part ${a.partId})`}</span>
                  <span className="block text-[12.5px] text-muted">
                    {a.contentType} · {formatSize(a.size)}
                    {a.inline && " · inline"}
                    {a.contentId && ` · cid:${a.contentId}`}
                  </span>
                </span>
                <a href={urls.part(s.id, a.partId)} className="rounded-md border border-border px-2.5 py-1.5 text-[13px] hover:bg-surface-hover">
                  Download
                </a>
              </li>
            ))}
          </ul>
        )}
      </TabsContent>

      <TabsContent value="transcript" className="mt-4">
        {transcript.isLoading ? <Spinner /> : transcript.data ? <TranscriptView transcript={transcript.data} /> : <Empty>No transcript is linked to this message (it was created through the API, IMAP APPEND, or the transcript has rotated out).</Empty>}
      </TabsContent>

    </Tabs>
  );
}

function Source({ text, wrap }: { text: string; wrap?: boolean }) {
  return <pre className={cn("max-h-[75vh] overflow-auto rounded-lg border border-border bg-bg-subtle p-4 font-mono text-[12.5px] leading-relaxed", wrap && "whitespace-pre-wrap break-all")}>{text}</pre>;
}

function Empty({ children }: { children: React.ReactNode }) {
  return <p className="rounded-lg border border-dashed border-border px-4 py-8 text-center text-[14px] text-muted">{children}</p>;
}

export function TranscriptView({ transcript }: { transcript: Transcript }) {
  const t = transcript.session;
  return (
    <div>
      <div className="mb-3 flex flex-wrap items-center gap-x-4 gap-y-1 text-[13px] text-muted">
        <Badge tone="info">{t.protocol.toUpperCase()}</Badge>
        {t.clientIp && <CodeChip value={t.clientIp} />}
        {t.user && <span>user {t.user}</span>}
        {t.secure && <Badge tone="accent">TLS</Badge>}
        <span>{t.commands} commands</span>
        <span>
          started <RelativeTime iso={t.startedAt} />
        </span>
      </div>
      <pre className="max-h-[70vh] overflow-auto rounded-lg border border-border bg-bg-subtle p-4 font-mono text-[12.5px] leading-relaxed">
        {transcript.lines.map((l, i) => (
          <div key={i} className={cn(l.direction === "Client" ? "text-info" : l.direction === "Server" ? "text-accent" : "text-muted italic")}>
            <span className="select-none text-muted/60">{l.direction === "Client" ? "C: " : l.direction === "Server" ? "S: " : "-- "}</span>
            {l.text}
          </div>
        ))}
      </pre>
    </div>
  );
}
