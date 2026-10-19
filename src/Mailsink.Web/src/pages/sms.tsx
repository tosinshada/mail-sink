import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ExternalLink, MessageSquare, Send, Smartphone, Trash2 } from "lucide-react";
import { useEffect, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { DataTable, EmptyState, IconTile } from "@/components/data-table";
import { distinctSenders, formatPhone, IPhone } from "@/components/iphone";
import { PageHeader } from "@/components/layout";
import { snippets, SnippetTabs, useSnippetContext } from "@/components/snippets";
import { Badge, Button, Dialog, Input, Label, MenuItem, MenuSeparator, RelativeTime, Spinner, Stat, Textarea } from "@/components/ui";
import { api, type SmsPhone } from "@/lib/api";
import { useApp } from "@/lib/app-state";

const exampleSms = {
  message: "Your verification code is 482913",
  to: ["+15551234567", "+44 7700 900123"],
  from: { name: "Acme", number: "+1 555 555 0199" },
};

const phonePath = (number: string) => `/sms/${encodeURIComponent(number)}`;

const shortDateTime = (iso: string) => new Date(iso).toLocaleString([], { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });

export function SmsPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { toast } = useApp();
  const phones = useQuery({ queryKey: ["sms", "phones"], queryFn: api.smsPhones });
  const stats = useQuery({ queryKey: ["sms", "stats"], queryFn: api.smsStats });
  const [composing, setComposing] = useState(false);

  const invalidate = () => void queryClient.invalidateQueries({ queryKey: ["sms"] });
  const clearAll = useMutation({
    mutationFn: api.deleteAllSms,
    onSuccess: () => {
      toast({ title: "All text messages deleted", tone: "success" });
      invalidate();
    },
    onError: (e) => toast({ title: "Delete failed", description: String(e), tone: "danger" }),
  });
  const remove = useMutation({ mutationFn: api.deleteSmsPhone, onSuccess: invalidate });
  const read = useMutation({ mutationFn: api.markSmsRead, onSuccess: invalidate });

  const list = phones.data ?? [];
  const s = stats.data;
  return (
    <>
      <PageHeader
        title="SMS"
        description="Text messages sent to POST /api/v1/sms, one row per recipient number. Nothing reaches a carrier."
        actions={
          <>
            {list.length > 0 && (
              <Button variant="ghost" onClick={() => confirm("Delete all text messages?") && clearAll.mutate()} loading={clearAll.isPending}>
                <Trash2 className="size-4" /> Clear all
              </Button>
            )}
            <Button variant="primary" onClick={() => setComposing(true)}>
              <Send className="size-4" /> Send test SMS
            </Button>
          </>
        }
        snippet={snippets.generic("Send an SMS", "sms", "POST", exampleSms)}
      />

      {list.length > 0 && s && (
        <div className="mb-6 grid grid-cols-2 gap-3 lg:grid-cols-5">
          <Stat label="Phones" value={s.phones.toLocaleString()} />
          <Stat label="Messages" value={s.messages.toLocaleString()} />
          <Stat label="Unread" value={s.unread.toLocaleString()} />
          <Stat label="Senders" value={s.senders.toLocaleString()} />
          <Stat label="Last 24 hours" value={s.last24Hours.toLocaleString()} />
        </div>
      )}

      <DataTable
        rows={list}
        rowKey={(p) => p.number}
        loading={phones.isLoading}
        onOpen={(p) => navigate(phonePath(p.number))}
        rowClassName={(p) => (p.unread > 0 ? "font-medium" : undefined)}
        empty={
          <EmptyState
            icon={<MessageSquare />}
            title="No text messages yet"
            description="POST a message and a list of phone numbers. Numbers are checked loosely: spaces, dashes, dots and parentheses are ignored, a leading + is allowed, and 7–15 digits are required. The sender name and number are optional."
          >
            <EmptySnippet />
          </EmptyState>
        }
        columns={[
          {
            key: "number",
            header: "Phone number",
            render: (p) => (
              <span className="flex items-center gap-3">
                <IconTile icon={<Smartphone className="size-4" />} seed={p.number} unread={p.unread > 0} />
                <span className="min-w-0">
                  <span className="link-dotted block truncate font-mono text-[14px]">{formatPhone(p.number)}</span>
                  <span className="block truncate text-[13px] text-muted">
                    <span className="text-fg/80">{p.lastFromName}:</span> {p.lastMessage}
                  </span>
                </span>
              </span>
            ),
          },
          {
            key: "messages",
            header: "Messages",
            className: "w-24 text-right tabular-nums",
            render: (p) => (
              <span className="inline-flex items-center gap-2">
                {p.unread > 0 && <Badge tone="accent">{p.unread} new</Badge>}
                {p.count}
              </span>
            ),
          },
          { key: "senders", header: "Senders", hideBelow: "md", className: "w-24 text-right tabular-nums", render: (p) => p.senders },
          { key: "first", header: "First received", hideBelow: "lg", className: "w-32 whitespace-nowrap", render: (p) => <RelativeTime iso={p.firstReceivedAt} /> },
          { key: "last", header: "Last received", className: "w-32 whitespace-nowrap", render: (p) => <RelativeTime iso={p.lastReceivedAt} /> },
        ]}
        rowMenu={(p) => (
          <>
            <MenuItem icon={<ExternalLink />} onSelect={() => navigate(phonePath(p.number))}>
              Open phone
            </MenuItem>
            <MenuItem icon={<MessageSquare />} disabled={p.unread === 0} onSelect={() => read.mutate(p.number)}>
              Mark as read
            </MenuItem>
            <MenuSeparator />
            <MenuItem icon={<Trash2 />} danger onSelect={() => confirm(`Delete messages for ${p.number}?`) && remove.mutate(p.number)}>
              Delete
            </MenuItem>
          </>
        )}
      />

      <ComposeSmsDialog open={composing} onOpenChange={setComposing} />
    </>
  );
}

export function SmsPhonePage() {
  const { number = "" } = useParams();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const thread = useQuery({ queryKey: ["sms", "thread", number], queryFn: () => api.smsThread(number) });
  const phones = useQuery({ queryKey: ["sms", "phones"], queryFn: api.smsPhones });
  const phone: SmsPhone | undefined = phones.data?.find((p) => p.number === number);

  const invalidate = () => void queryClient.invalidateQueries({ queryKey: ["sms"] });
  const read = useMutation({ mutationFn: () => api.markSmsRead(number), onSuccess: invalidate });
  const remove = useMutation({
    mutationFn: () => api.deleteSmsPhone(number),
    onSuccess: () => {
      invalidate();
      navigate("/sms");
    },
  });

  // Opening the phone reads its messages, as on a real device. New arrivals while open are read too.
  const unread = phone?.unread ?? 0;
  useEffect(() => {
    if (unread > 0 && !read.isPending) read.mutate();
  }, [unread]);

  const messages = thread.data ?? [];
  const senders = distinctSenders(messages);

  return (
    <>
      <PageHeader
        breadcrumb={
          <>
            <Link to="/sms" className="hover:text-fg">
              SMS
            </Link>{" "}
            / <span className="font-mono">{number}</span>
          </>
        }
        title={<span className="font-mono">{formatPhone(number)}</span>}
        description={`${messages.length} message${messages.length === 1 ? "" : "s"} from ${senders.length} sender${senders.length === 1 ? "" : "s"}`}
        actions={
          <Button variant="ghost" onClick={() => confirm(`Delete messages for ${number}?`) && remove.mutate()} loading={remove.isPending} disabled={messages.length === 0}>
            <Trash2 className="size-4" /> Delete
          </Button>
        }
        snippet={snippets.generic("Read this phone's messages", `sms/phones/${encodeURIComponent(number)}`)}
      />

      {thread.isLoading ? (
        <Spinner />
      ) : messages.length === 0 ? (
        <EmptyState icon={<Smartphone />} title="No messages for this number" description="It may have been deleted or cleared.">
          <div className="text-center">
            <Button onClick={() => navigate("/sms")}>Back to SMS</Button>
          </div>
        </EmptyState>
      ) : (
        <div className="flex flex-col items-start gap-8 lg:flex-row">
          <IPhone number={number} messages={messages} />
          <div className="w-full min-w-0 flex-1 space-y-6">
            <div className="grid grid-cols-2 gap-3">
              <Stat label="Messages" value={messages.length.toLocaleString()} />
              <Stat label="Senders" value={senders.length.toLocaleString()} />
              <Stat label="First received" value={shortDateTime(messages[0].receivedAt)} hint={<RelativeTime iso={messages[0].receivedAt} />} />
              <Stat
                label="Last received"
                value={shortDateTime(messages[messages.length - 1].receivedAt)}
                hint={<RelativeTime iso={messages[messages.length - 1].receivedAt} />}
              />
            </div>
            <section className="rounded-lg border border-border">
              <h3 className="border-b border-border px-4 py-3 text-[14px] font-semibold">Senders</h3>
              <table className="w-full text-[13.5px]">
                <thead className="text-left text-[12px] text-muted">
                  <tr>
                    <th className="px-4 py-2 font-medium">Name</th>
                    <th className="px-4 py-2 font-medium">Number</th>
                    <th className="px-4 py-2 text-right font-medium">Messages</th>
                  </tr>
                </thead>
                <tbody>
                  {senders.map((sender) => (
                    <tr key={`${sender.name}|${sender.number}`} className="border-t border-border">
                      <td className="px-4 py-2">{sender.name}</td>
                      <td className="px-4 py-2 font-mono">{formatPhone(sender.number)}</td>
                      <td className="px-4 py-2 text-right tabular-nums">
                        {messages.filter((m) => m.from.name === sender.name && m.from.number === sender.number).length}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </section>
          </div>
        </div>
      )}
    </>
  );
}

function EmptySnippet() {
  const ctx = useSnippetContext();
  return <SnippetTabs samples={snippets.generic("Send an SMS", "sms", "POST", exampleSms)(ctx).samples} />;
}

function ComposeSmsDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const { toast } = useApp();
  const queryClient = useQueryClient();
  const [to, setTo] = useState("");
  const [fromName, setFromName] = useState("");
  const [fromNumber, setFromNumber] = useState("");
  const [message, setMessage] = useState("");

  const send = useMutation({
    mutationFn: () =>
      api.sendSms({
        message,
        to: to.split(/[,;\n]/).map((n) => n.trim()).filter(Boolean),
        from: { name: fromName.trim() || undefined, number: fromNumber.trim() || undefined },
      }),
    onSuccess: (r) => {
      toast({ title: `Sent to ${r.recipients.length} number${r.recipients.length === 1 ? "" : "s"}`, description: `From ${r.from.name} (${r.from.number})`, tone: "success" });
      void queryClient.invalidateQueries({ queryKey: ["sms"] });
      setMessage("");
      onOpenChange(false);
    },
    onError: (e) => toast({ title: "Send failed", description: e instanceof Error ? e.message : String(e), tone: "danger" }),
  });

  return (
    <Dialog
      open={open}
      onOpenChange={onOpenChange}
      title="Send test SMS"
      description="Goes through the same API your app uses. Leave the sender blank to use the defaults."
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button variant="primary" loading={send.isPending} disabled={!to.trim() || !message.trim()} onClick={() => send.mutate()}>
            <Send className="size-4" /> Send
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <div>
          <Label htmlFor="sms-to" hint="Comma-separated">
            To
          </Label>
          <Input id="sms-to" value={to} onChange={(e) => setTo(e.target.value)} placeholder="+1 555 123 4567, 07700 900123" autoFocus />
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          <div>
            <Label htmlFor="sms-from-name" hint="Optional">
              Sender name
            </Label>
            <Input id="sms-from-name" value={fromName} onChange={(e) => setFromName(e.target.value)} placeholder="Mailsink" />
          </div>
          <div>
            <Label htmlFor="sms-from-number" hint="Optional">
              Sender number
            </Label>
            <Input id="sms-from-number" value={fromNumber} onChange={(e) => setFromNumber(e.target.value)} placeholder="+1 555 555 0100" />
          </div>
        </div>
        <div>
          <Label htmlFor="sms-message">Message</Label>
          <Textarea id="sms-message" rows={4} maxLength={1600} value={message} onChange={(e) => setMessage(e.target.value)} placeholder="Your verification code is 482913" />
        </div>
      </div>
    </Dialog>
  );
}
