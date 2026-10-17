import { useMutation, useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronRight, Copy, Paperclip, Send, Shuffle, X } from "lucide-react";
import { useEffect, useMemo, useRef, useState, type DragEvent, type KeyboardEvent } from "react";
import { useLocation, useNavigate, useSearchParams } from "react-router-dom";
import { PageHeader } from "@/components/layout";
import { snippets, useSnippetContext } from "@/components/snippets";
import { Button, Input, Label, NativeSelect, Switch, Tabs, TabsContent, TabsList, TabsTrigger, Textarea } from "@/components/ui";
import { api, type Address, type SendRequest } from "@/lib/api";
import { useApp } from "@/lib/app-state";
import { cn, copyText, fileToBase64, formatAddress, formatSize, parseAddressList } from "@/lib/format";

interface ReplyState {
  reply?: { to: Address[]; subject: string; text: string; headers?: Record<string, string> };
}

interface Attachment {
  file: File;
  base64: string;
}

export function ComposePage() {
  const navigate = useNavigate();
  const location = useLocation();
  const [params] = useSearchParams();
  const { account, toast } = useApp();
  const ctx = useSnippetContext();
  const reply = (location.state as ReplyState | null)?.reply;
  const domain = ctx.info?.defaultDomain ?? "test.local";

  const accounts = useQuery({ queryKey: ["accounts"], queryFn: api.accounts });
  const templates = useQuery({ queryKey: ["templates"], queryFn: api.templates });

  const [from, setFrom] = useState(account ?? `alice@${domain}`);
  const [to, setTo] = useState<Address[]>(reply?.to ?? []);
  const [cc, setCc] = useState<Address[]>([]);
  const [bcc, setBcc] = useState<Address[]>([]);
  const [showCc, setShowCc] = useState(false);
  const [subject, setSubject] = useState(reply?.subject ?? "");
  const [mode, setMode] = useState<"rich" | "html" | "text">(reply ? "text" : "rich");
  const [html, setHtml] = useState("");
  const [text, setText] = useState(reply?.text ?? "");
  const [attachments, setAttachments] = useState<Attachment[]>([]);
  const [advanced, setAdvanced] = useState(!!reply?.headers);
  const [headers, setHeaders] = useState(reply?.headers ? Object.entries(reply.headers).map(([k, v]) => `${k}: ${v}`).join("\n") : "");
  const [template, setTemplate] = useState(params.get("template") ?? "");
  const [templateData, setTemplateData] = useState("");
  const [repeat, setRepeat] = useState(1);
  const [tags, setTags] = useState("");
  const [bulkCount, setBulkCount] = useState(25);
  const [bulkRandom, setBulkRandom] = useState(true);
  const [bulkAttachments, setBulkAttachments] = useState(false);

  useEffect(() => {
    if (!ctx.info || from.includes("@")) return;
    setFrom(`alice@${ctx.info.defaultDomain}`);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ctx.info?.defaultDomain]);

  useEffect(() => {
    const t = templates.data?.find((x) => x.id === template);
    if (t?.sampleData && !templateData) setTemplateData(t.sampleData);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [template, templates.data]);

  const selectedTemplate = templates.data?.find((x) => x.id === template);

  const body = useMemo<SendRequest>(() => {
    const [fromAddress] = parseAddressList(from);
    const headerMap: Record<string, string> = {};
    headers
      .split("\n")
      .map((l) => l.trim())
      .filter((l) => l.includes(":"))
      .forEach((l) => {
        const i = l.indexOf(":");
        headerMap[l.slice(0, i).trim()] = l.slice(i + 1).trim();
      });
    let data: unknown;
    if (template && templateData.trim()) {
      try {
        data = JSON.parse(templateData);
      } catch {
        data = undefined;
      }
    }
    const tagList = tags
      .split(",")
      .map((t) => t.trim())
      .filter(Boolean);
    return {
      from: fromAddress ?? { name: null, email: from },
      to,
      ...(cc.length ? { cc } : {}),
      ...(bcc.length ? { bcc } : {}),
      ...(subject && !template ? { subject } : {}),
      ...(template ? { template, ...(data !== undefined ? { templateData: data } : {}) } : mode === "text" ? { text } : { html, ...(text ? { text } : {}) }),
      ...(Object.keys(headerMap).length ? { headers: headerMap } : {}),
      ...(attachments.length ? { attachments: attachments.map((a) => ({ filename: a.file.name, contentType: a.file.type || undefined, contentBase64: a.base64 })) } : {}),
      ...(repeat > 1 ? { repeat } : {}),
      ...(tagList.length ? { tags: tagList } : {}),
    };
  }, [from, to, cc, bcc, subject, mode, html, text, headers, template, templateData, attachments, repeat, tags]);

  const templateDataInvalid = template && templateData.trim() && !isJson(templateData);

  const send = useMutation({
    mutationFn: () => api.send(body),
    onSuccess: (result) => {
      const first = result.ids[0];
      toast({
        title: result.ids.length === 1 ? "Sent" : `${result.ids.length} messages sent`,
        description: subject || selectedTemplate?.name,
        tone: "success",
        action: first ? { label: "View email", onClick: () => navigate(`/emails/${first}`) } : undefined,
      });
    },
    onError: (e) => toast({ title: "Send failed", description: String(e), tone: "danger" }),
  });

  const bulk = useMutation({
    mutationFn: () => api.sendBulk({ count: bulkCount, random: bulkRandom, withAttachments: bulkAttachments, ...(to.length ? { to: to.map((a) => a.email) } : {}) }),
    onSuccess: (r) => toast({ title: `${r.ids.length} messages generated`, tone: "success", action: { label: "View emails", onClick: () => navigate("/emails") } }),
    onError: (e) => toast({ title: "Bulk send failed", description: String(e), tone: "danger" }),
  });

  const curl = () => {
    const sample = snippets.compose(body)(ctx).samples[0].code;
    void copyText(sample).then(() => toast({ title: "Copied as curl", tone: "success" }));
  };

  const addFiles = async (files: FileList | File[]) => {
    const list = await Promise.all([...files].map(async (file) => ({ file, base64: await fileToBase64(file) })));
    setAttachments((a) => [...a, ...list]);
  };

  const valid = to.length > 0 && from.trim().length > 0 && !templateDataInvalid;
  const previewHtml = mode === "text" ? `<pre style="font-family:system-ui;white-space:pre-wrap;padding:16px">${escapeHtml(text)}</pre>` : html;

  return (
    <>
      <PageHeader
        title="Compose"
        description="Send a test message through the same path as the API. It is captured like any other mail."
        actions={
          <Button onClick={curl}>
            <Copy className="size-4" /> Copy as curl
          </Button>
        }
        snippet={snippets.compose(body)}
      />

      <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
        <form
          className="space-y-4"
          onSubmit={(e) => {
            e.preventDefault();
            if (valid) send.mutate();
          }}
        >
          <div>
            <Label htmlFor="from">From</Label>
            <Input id="from" list="known-accounts" value={from} onChange={(e) => setFrom(e.target.value)} placeholder={`Alice <alice@${domain}>`} />
            <datalist id="known-accounts">
              {accounts.data?.map((a) => (
                <option key={a.address} value={a.address} />
              ))}
            </datalist>
          </div>
          <div>
            <Label htmlFor="to" hint={showCc ? undefined : "add Cc / Bcc"}>
              <span className="flex items-center gap-3">
                To
                {!showCc && (
                  <button type="button" className="link-dotted text-[12px] font-normal" onClick={() => setShowCc(true)}>
                    Cc / Bcc
                  </button>
                )}
              </span>
            </Label>
            <TokenInput id="to" value={to} onChange={setTo} placeholder={`bob@${domain}, Carol <carol@${domain}>`} suggestions={accounts.data?.map((a) => a.address) ?? []} />
          </div>
          {showCc && (
            <div className="grid gap-4 sm:grid-cols-2">
              <div>
                <Label htmlFor="cc">Cc</Label>
                <TokenInput id="cc" value={cc} onChange={setCc} />
              </div>
              <div>
                <Label htmlFor="bcc">Bcc</Label>
                <TokenInput id="bcc" value={bcc} onChange={setBcc} />
              </div>
            </div>
          )}
          <div>
            <Label htmlFor="subject" hint={template ? "from the template" : undefined}>
              Subject
            </Label>
            <Input id="subject" value={template ? (selectedTemplate?.subject ?? "") : subject} disabled={!!template} onChange={(e) => setSubject(e.target.value)} placeholder="Welcome to Mailsink" />
          </div>

          {template ? (
            <div className="rounded-md border border-dashed border-border px-4 py-3 text-[13.5px] text-muted">
              Body comes from template <span className="font-medium text-fg">{selectedTemplate?.name}</span>. Clear the template under Advanced to write your own.
            </div>
          ) : (
            <Tabs value={mode} onValueChange={(v) => setMode(v as typeof mode)}>
              <TabsList>
                <TabsTrigger value="rich">Rich text</TabsTrigger>
                <TabsTrigger value="html">HTML</TabsTrigger>
                <TabsTrigger value="text">Text</TabsTrigger>
              </TabsList>
              <TabsContent value="rich" className="mt-3">
                <RichEditor value={html} onChange={setHtml} />
              </TabsContent>
              <TabsContent value="html" className="mt-3">
                <Textarea value={html} onChange={(e) => setHtml(e.target.value)} rows={12} spellCheck={false} className="font-mono text-[13px]" placeholder={"<h1>Hello</h1>\n<p>Click <a href=\"https://app.test/confirm?token=abc\">here</a> to confirm.</p>"} />
                <Textarea value={text} onChange={(e) => setText(e.target.value)} rows={3} className="mt-2" placeholder="Optional plain-text alternative" />
              </TabsContent>
              <TabsContent value="text" className="mt-3">
                <Textarea value={text} onChange={(e) => setText(e.target.value)} rows={12} placeholder="Plain text body" />
              </TabsContent>
            </Tabs>
          )}

          <Dropzone onFiles={addFiles}>
            {attachments.length > 0 && (
              <ul className="mb-2 flex flex-wrap gap-2">
                {attachments.map((a, i) => (
                  <li key={i} className="flex items-center gap-2 rounded-md border border-border bg-surface px-2 py-1 text-[13px]">
                    <Paperclip className="size-3.5 text-muted" />
                    <span className="max-w-[200px] truncate">{a.file.name}</span>
                    <span className="text-muted">{formatSize(a.file.size)}</span>
                    <button type="button" aria-label={`Remove ${a.file.name}`} onClick={() => setAttachments((all) => all.filter((_, j) => j !== i))} className="text-muted hover:text-fg">
                      <X className="size-3.5" />
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </Dropzone>

          <div className="rounded-lg border border-border">
            <button type="button" onClick={() => setAdvanced((v) => !v)} className="flex w-full items-center gap-2 px-4 py-3 text-[14px] font-medium">
              {advanced ? <ChevronDown className="size-4 text-muted" /> : <ChevronRight className="size-4 text-muted" />}
              Advanced
              <span className="ml-auto text-[12.5px] font-normal text-muted">headers · template · repeat · tags</span>
            </button>
            {advanced && (
              <div className="grid gap-4 border-t border-border p-4 sm:grid-cols-2">
                <div className="sm:col-span-2">
                  <Label htmlFor="headers" hint="one per line, Name: value">
                    Custom headers
                  </Label>
                  <Textarea id="headers" value={headers} onChange={(e) => setHeaders(e.target.value)} rows={3} spellCheck={false} className="font-mono text-[13px]" placeholder={"X-Campaign: spring\nList-Unsubscribe: <mailto:unsub@example.com>"} />
                </div>
                <div>
                  <Label htmlFor="template">Template</Label>
                  <NativeSelect id="template" value={template} onChange={(e) => setTemplate(e.target.value)}>
                    <option value="">None (use the body above)</option>
                    {templates.data?.map((t) => (
                      <option key={t.id} value={t.id}>
                        {t.name}
                      </option>
                    ))}
                  </NativeSelect>
                </div>
                <div>
                  <Label htmlFor="data" hint={templateDataInvalid ? "invalid JSON" : "JSON"}>
                    Template data
                  </Label>
                  <Textarea id="data" value={templateData} onChange={(e) => setTemplateData(e.target.value)} rows={3} disabled={!template} spellCheck={false} className={cn("font-mono text-[13px]", templateDataInvalid && "border-danger")} placeholder='{ "name": "Bob" }' />
                </div>
                <div>
                  <Label htmlFor="repeat" hint="sends N copies">
                    Repeat
                  </Label>
                  <Input id="repeat" type="number" min={1} max={1000} value={repeat} onChange={(e) => setRepeat(Math.max(1, Number(e.target.value) || 1))} />
                </div>
                <div>
                  <Label htmlFor="tags" hint="comma-separated">
                    Tags
                  </Label>
                  <Input id="tags" value={tags} onChange={(e) => setTags(e.target.value)} placeholder="onboarding, test" />
                </div>
              </div>
            )}
          </div>

          <div className="flex flex-wrap items-center gap-2">
            <Button type="submit" variant="primary" disabled={!valid} loading={send.isPending}>
              <Send className="size-4" /> Send{repeat > 1 ? ` ×${repeat}` : ""}
            </Button>
            <Button type="button" onClick={curl}>
              <Copy className="size-4" /> Copy as curl
            </Button>
            {to.length === 0 && <span className="text-[13px] text-muted">Add at least one recipient.</span>}
          </div>

          <section className="mt-8 rounded-lg border border-border p-4">
            <h3 className="flex items-center gap-2 text-[14px] font-semibold">
              <Shuffle className="size-4 text-muted" /> Bulk send
            </h3>
            <p className="mt-1 text-[13px] text-muted">Generate load-test traffic with randomized senders, subjects and bodies. Recipients above are used when set.</p>
            <div className="mt-3 flex flex-wrap items-end gap-3">
              <div className="w-28">
                <Label htmlFor="bulk-count">Count</Label>
                <Input id="bulk-count" type="number" min={1} max={10000} value={bulkCount} onChange={(e) => setBulkCount(Math.max(1, Number(e.target.value) || 1))} />
              </div>
              <Switch checked={bulkRandom} onCheckedChange={setBulkRandom} label="Random content" />
              <Switch checked={bulkAttachments} onCheckedChange={setBulkAttachments} label="With attachments" />
              <Button type="button" loading={bulk.isPending} onClick={() => bulk.mutate()}>
                Generate
              </Button>
            </div>
          </section>
        </form>

        <aside className="hidden xl:block">
          <div className="sticky top-6">
            <div className="mb-2 flex items-center justify-between text-[13px] text-muted">
              <span>Live preview</span>
              <span>{template ? "rendered on send" : mode === "text" ? "text/plain" : "text/html"}</span>
            </div>
            <div className="overflow-hidden rounded-lg border border-border bg-white">
              <iframe title="Preview" sandbox="" srcDoc={template ? `<p style="font-family:system-ui;color:#666;padding:16px">${escapeHtml(selectedTemplate?.html ?? selectedTemplate?.text ?? "")}</p>` : previewHtml} className="h-[70vh] w-full" />
            </div>
          </div>
        </aside>
      </div>
    </>
  );
}

function isJson(s: string) {
  try {
    JSON.parse(s);
    return true;
  } catch {
    return false;
  }
}

function escapeHtml(s: string) {
  return s.replace(/[&<>]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" })[c]!);
}

/** Address chips with a free-text input. Enter, comma, Tab and paste add tokens. */
export function TokenInput({ id, value, onChange, placeholder, suggestions = [] }: { id?: string; value: Address[]; onChange: (v: Address[]) => void; placeholder?: string; suggestions?: string[] }) {
  const [draft, setDraft] = useState("");
  const inputRef = useRef<HTMLInputElement>(null);
  const commit = (raw: string) => {
    const parsed = parseAddressList(raw);
    if (parsed.length === 0) return;
    onChange([...value, ...parsed.filter((p) => !value.some((v) => v.email.toLowerCase() === p.email.toLowerCase()))]);
    setDraft("");
  };
  const onKey = (e: KeyboardEvent<HTMLInputElement>) => {
    if ((e.key === "Enter" || e.key === "," || e.key === "Tab") && draft.trim()) {
      e.preventDefault();
      commit(draft);
    } else if (e.key === "Backspace" && !draft && value.length > 0) {
      onChange(value.slice(0, -1));
    }
  };
  const listId = id ? `${id}-suggestions` : undefined;
  return (
    <div className="flex min-h-9 w-full flex-wrap items-center gap-1.5 rounded-md border border-border bg-surface px-2 py-1 focus-within:border-border-strong focus-within:ring-2 focus-within:ring-info/30" onClick={() => inputRef.current?.focus()}>
      {value.map((a) => (
        <span key={a.email} className="flex items-center gap-1 rounded-md bg-surface-active px-2 py-0.5 text-[13px]">
          <span title={formatAddress(a)}>{a.name || a.email}</span>
          <button type="button" aria-label={`Remove ${a.email}`} className="text-muted hover:text-fg" onClick={() => onChange(value.filter((v) => v !== a))}>
            <X className="size-3" />
          </button>
        </span>
      ))}
      <input
        ref={inputRef}
        id={id}
        list={listId}
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={onKey}
        onBlur={() => draft.trim() && commit(draft)}
        onPaste={(e) => {
          const pasted = e.clipboardData.getData("text");
          if (/[,;\n]/.test(pasted)) {
            e.preventDefault();
            commit(pasted);
          }
        }}
        placeholder={value.length === 0 ? placeholder : undefined}
        className="h-7 min-w-[160px] flex-1 bg-transparent text-[14px] outline-none placeholder:text-muted/70"
      />
      {listId && (
        <datalist id={listId}>
          {suggestions.map((s) => (
            <option key={s} value={s} />
          ))}
        </datalist>
      )}
    </div>
  );
}

function Dropzone({ onFiles, children }: { onFiles: (files: FileList | File[]) => void; children?: React.ReactNode }) {
  const [over, setOver] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    setOver(false);
    if (e.dataTransfer.files.length) onFiles(e.dataTransfer.files);
  };
  return (
    <div
      onDragOver={(e) => {
        e.preventDefault();
        setOver(true);
      }}
      onDragLeave={() => setOver(false)}
      onDrop={onDrop}
      className={cn("rounded-lg border border-dashed p-3 transition-colors", over ? "border-accent bg-accent-soft/40" : "border-border")}
    >
      {children}
      <div className="flex items-center gap-2 text-[13px] text-muted">
        <Paperclip className="size-4" />
        Drop files here or
        <button type="button" className="link-dotted text-fg" onClick={() => input.current?.click()}>
          choose attachments
        </button>
        <input ref={input} type="file" multiple className="hidden" onChange={(e) => e.target.files && onFiles(e.target.files)} />
      </div>
    </div>
  );
}

/** Minimal contentEditable editor; its innerHTML is the HTML body. */
function RichEditor({ value, onChange }: { value: string; onChange: (html: string) => void }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (ref.current && ref.current.innerHTML !== value && document.activeElement !== ref.current) ref.current.innerHTML = value;
  }, [value]);
  const exec = (command: string, arg?: string) => {
    ref.current?.focus();
    document.execCommand(command, false, arg);
    onChange(ref.current?.innerHTML ?? "");
  };
  const tools: [string, string, string?][] = [
    ["B", "bold"],
    ["I", "italic"],
    ["U", "underline"],
    ["H1", "formatBlock", "h1"],
    ["H2", "formatBlock", "h2"],
    ["¶", "formatBlock", "p"],
    ["• list", "insertUnorderedList"],
    ["1. list", "insertOrderedList"],
  ];
  return (
    <div className="overflow-hidden rounded-md border border-border bg-surface focus-within:border-border-strong">
      <div className="flex flex-wrap gap-1 border-b border-border px-2 py-1.5">
        {tools.map(([label, cmd, arg]) => (
          <button key={label} type="button" onMouseDown={(e) => e.preventDefault()} onClick={() => exec(cmd, arg)} className="rounded px-2 py-0.5 text-[12.5px] text-muted hover:bg-surface-hover hover:text-fg">
            {label}
          </button>
        ))}
        <button
          type="button"
          onMouseDown={(e) => e.preventDefault()}
          onClick={() => {
            const url = prompt("Link URL", "https://");
            if (url) exec("createLink", url);
          }}
          className="rounded px-2 py-0.5 text-[12.5px] text-muted hover:bg-surface-hover hover:text-fg"
        >
          Link
        </button>
      </div>
      <div
        ref={ref}
        contentEditable
        suppressContentEditableWarning
        onInput={() => onChange(ref.current?.innerHTML ?? "")}
        data-placeholder="Write your message…"
        className="min-h-[260px] px-3 py-2 text-[14.5px] leading-relaxed outline-none [&_a]:text-info [&_a]:underline [&_h1]:text-[22px] [&_h1]:font-bold [&_h2]:text-[18px] [&_h2]:font-semibold [&_ol]:list-decimal [&_ol]:pl-6 [&_ul]:list-disc [&_ul]:pl-6 empty:before:text-muted/60 empty:before:content-[attr(data-placeholder)]"
      />
    </div>
  );
}
