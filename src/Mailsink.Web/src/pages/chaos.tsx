import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Copy, Flame, Pencil, Plus, Trash2 } from "lucide-react";
import { useEffect, useState } from "react";
import { DataTable, EmptyState, IconTile } from "@/components/data-table";
import { PageHeader } from "@/components/layout";
import { snippets } from "@/components/snippets";
import { Badge, Button, Dialog, Input, Label, MenuItem, MenuSeparator, NativeSelect, Switch } from "@/components/ui";
import { api, type ChaosAction, type ChaosRule, type ChaosSettings, type ChaosStage } from "@/lib/api";
import { useApp } from "@/lib/app-state";

const stages: ChaosStage[] = ["Connect", "Helo", "Auth", "MailFrom", "RcptTo", "Data"];
const stageHelp: Record<ChaosStage, string> = {
  Connect: "right after the TCP connection, before the banner",
  Helo: "on EHLO / HELO",
  Auth: "on AUTH (matches user)",
  MailFrom: "on MAIL FROM (matches sender)",
  RcptTo: "on RCPT TO (matches recipient)",
  Data: "after the message body is received",
};

const presets: Omit<ChaosRule, "id">[] = [
  { name: "Greylist everything", enabled: true, stage: "RcptTo", match: null, response: { code: 451, text: "4.7.1 Greylisted, try again later" }, delayMs: 0, action: "Respond", probability: 0.5 },
  { name: "Mailbox full for bob", enabled: true, stage: "RcptTo", match: { recipient: "bob@*" }, response: { code: 552, text: "5.2.2 Mailbox full" }, delayMs: 0, action: "Respond", probability: 1 },
  { name: "Slow DATA", enabled: true, stage: "Data", match: null, response: null, delayMs: 5000, action: "Delay", probability: 1 },
  { name: "Drop 10% of connections", enabled: true, stage: "Connect", match: null, response: null, delayMs: 0, action: "Drop", probability: 0.1 },
];

export function ChaosPage() {
  const queryClient = useQueryClient();
  const { toast } = useApp();
  const chaos = useQuery({ queryKey: ["chaos"], queryFn: api.chaos });
  const [editing, setEditing] = useState<ChaosRule | null>(null);

  const save = useMutation({
    mutationFn: (settings: ChaosSettings) => api.saveChaos(settings),
    onSuccess: (data) => {
      queryClient.setQueryData(["chaos"], data);
      setEditing(null);
    },
    onError: (e) => toast({ title: "Could not save chaos rules", description: String(e), tone: "danger" }),
  });

  const settings = chaos.data ?? { enabled: false, rules: [] };
  const update = (patch: Partial<ChaosSettings>) => save.mutate({ ...settings, ...patch });
  const upsert = (rule: ChaosRule) => update({ rules: settings.rules.some((r) => r.id === rule.id) ? settings.rules.map((r) => (r.id === rule.id ? rule : r)) : [...settings.rules, rule] });
  const newRule = (base?: Omit<ChaosRule, "id">): ChaosRule => ({
    id: crypto.randomUUID(),
    name: null,
    enabled: true,
    stage: "RcptTo",
    match: null,
    response: { code: 451, text: "4.3.0 Temporary failure" },
    delayMs: 0,
    action: "Respond",
    probability: 1,
    ...base,
  });

  return (
    <>
      <PageHeader
        title="Chaos"
        description="Fault injection for the SMTP listeners: reject, delay or drop at any stage, with a probability. Rules are evaluated in order; the first match wins."
        actions={
          <Button variant="primary" onClick={() => setEditing(newRule())}>
            <Plus className="size-4" /> New rule
          </Button>
        }
        snippet={snippets.generic("Set chaos rules", "chaos", "PUT", settings)}
      />

      <div className="mb-4 flex flex-wrap items-center gap-4 rounded-lg border border-border bg-bg-subtle px-4 py-3">
        <Switch checked={settings.enabled} onCheckedChange={(enabled) => update({ enabled })} label={settings.enabled ? "Chaos is ON: rules apply to new SMTP sessions" : "Chaos is off: rules are kept but not applied"} />
        {settings.enabled && settings.rules.some((r) => r.enabled) && (
          <Badge tone="warning">
            <Flame className="size-3" /> {settings.rules.filter((r) => r.enabled).length} active
          </Badge>
        )}
        <div className="ml-auto flex flex-wrap gap-1.5">
          {presets.map((p) => (
            <button key={p.name} onClick={() => setEditing(newRule(p))} className="rounded-full border border-border bg-surface px-2.5 py-0.5 text-[12px] text-muted hover:text-fg">
              + {p.name}
            </button>
          ))}
        </div>
      </div>

      <DataTable
        rows={settings.rules}
        rowKey={(r) => r.id}
        loading={chaos.isLoading}
        onOpen={(r) => setEditing(r)}
        rowClassName={(r) => (r.enabled ? undefined : "opacity-60")}
        empty={<EmptyState icon={<Flame />} title="No chaos rules" description="Add a rule to simulate greylisting, full mailboxes, slow servers or dropped connections, and see how your application retries." />}
        columns={[
          {
            key: "name",
            header: "Rule",
            render: (r) => (
              <span className="flex items-center gap-3">
                <IconTile icon={<Flame className="size-4" />} seed={r.id} />
                <span className="min-w-0">
                  <span className="link-dotted block truncate text-[15px] font-medium">{r.name || describe(r)}</span>
                  <span className="block truncate text-[13px] text-muted">{r.name ? describe(r) : stageHelp[r.stage]}</span>
                </span>
              </span>
            ),
          },
          { key: "stage", header: "Stage", hideBelow: "sm", className: "w-28", render: (r) => <Badge tone="info">{r.stage}</Badge> },
          {
            key: "match",
            header: "Match",
            hideBelow: "md",
            render: (r) => {
              const parts = Object.entries(r.match ?? {}).filter(([, v]) => v);
              return parts.length === 0 ? <span className="text-muted">any</span> : <span className="font-mono text-[12.5px]">{parts.map(([k, v]) => `${k}=${v}`).join(" ")}</span>;
            },
          },
          {
            key: "action",
            header: "Action",
            hideBelow: "sm",
            render: (r) =>
              r.action === "Drop" ? (
                <Badge tone="danger">drop</Badge>
              ) : r.action === "Delay" ? (
                <Badge tone="warning">delay {r.delayMs} ms</Badge>
              ) : (
                <span className="flex items-center gap-2">
                  <Badge tone={r.response && r.response.code >= 500 ? "danger" : "warning"}>{r.response?.code}</Badge>
                  <span className="hidden max-w-[200px] truncate text-[12.5px] text-muted lg:inline">{r.response?.text}</span>
                  {r.delayMs > 0 && <span className="text-[12px] text-muted">+{r.delayMs} ms</span>}
                </span>
              ),
          },
          { key: "probability", header: "Probability", hideBelow: "lg", className: "w-28", render: (r) => <span className="font-mono text-[13px]">{Math.round(r.probability * 100)}%</span> },
          {
            key: "enabled",
            header: "Enabled",
            className: "w-24",
            render: (r) => (
              <span onClick={(e) => e.stopPropagation()}>
                <Switch checked={r.enabled} onCheckedChange={(enabled) => upsert({ ...r, enabled })} />
              </span>
            ),
          },
        ]}
        rowMenu={(r) => (
          <>
            <MenuItem icon={<Pencil />} onSelect={() => setEditing(r)}>
              Edit
            </MenuItem>
            <MenuItem icon={<Copy />} onSelect={() => upsert({ ...r, id: crypto.randomUUID(), name: r.name ? `${r.name} (copy)` : null })}>
              Duplicate
            </MenuItem>
            <MenuSeparator />
            <MenuItem icon={<Trash2 />} danger onSelect={() => update({ rules: settings.rules.filter((x) => x.id !== r.id) })}>
              Delete
            </MenuItem>
          </>
        )}
      />

      <RuleDialog rule={editing} onClose={() => setEditing(null)} onSave={upsert} pending={save.isPending} />
    </>
  );
}

function describe(r: ChaosRule): string {
  const action = r.action === "Drop" ? "drop the connection" : r.action === "Delay" ? `wait ${r.delayMs} ms` : `reply ${r.response?.code ?? "?"} ${r.response?.text ?? ""}`.trim();
  const odds = r.probability < 1 ? ` (${Math.round(r.probability * 100)}% of the time)` : "";
  return `At ${r.stage}, ${action}${odds}`;
}

function RuleDialog({ rule, onClose, onSave, pending }: { rule: ChaosRule | null; onClose: () => void; onSave: (r: ChaosRule) => void; pending: boolean }) {
  const [form, setForm] = useState<ChaosRule | null>(rule);
  useEffect(() => setForm(rule), [rule]);
  if (!form) return null;
  const set = (patch: Partial<ChaosRule>) => setForm((f) => (f ? { ...f, ...patch } : f));
  const setMatch = (key: "sender" | "recipient" | "clientIp" | "user", value: string) => set({ match: { ...(form.match ?? {}), [key]: value || null } });
  const action: ChaosAction = form.action ?? "Respond";
  return (
    <Dialog
      open={!!rule}
      onOpenChange={(o) => !o && onClose()}
      title={rule && rule.name ? `Edit ${rule.name}` : "Chaos rule"}
      description="Match fields accept globs (bob@*, 10.0.*). Empty fields match everything."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={pending} onClick={() => onSave(form)}>
            Save rule
          </Button>
        </>
      }
    >
      <div className="grid gap-4 sm:grid-cols-2">
        <div className="sm:col-span-2">
          <Label htmlFor="r-name" hint="optional">
            Name
          </Label>
          <Input id="r-name" autoFocus value={form.name ?? ""} onChange={(e) => set({ name: e.target.value || null })} placeholder="Greylist first attempt" />
        </div>
        <div>
          <Label htmlFor="r-stage">Stage</Label>
          <NativeSelect id="r-stage" value={form.stage} onChange={(e) => set({ stage: e.target.value as ChaosStage })}>
            {stages.map((s) => (
              <option key={s} value={s}>
                {s}
              </option>
            ))}
          </NativeSelect>
          <p className="mt-1 text-[12px] text-muted">{stageHelp[form.stage]}</p>
        </div>
        <div>
          <Label htmlFor="r-action">Action</Label>
          <NativeSelect id="r-action" value={action} onChange={(e) => set({ action: e.target.value as ChaosAction })}>
            <option value="Respond">Respond with an SMTP code</option>
            <option value="Delay">Delay, then continue</option>
            <option value="Drop">Drop the connection</option>
          </NativeSelect>
        </div>
        {action === "Respond" && (
          <>
            <div>
              <Label htmlFor="r-code">Reply code</Label>
              <Input id="r-code" type="number" min={200} max={599} value={form.response?.code ?? 451} onChange={(e) => set({ response: { code: Number(e.target.value) || 451, text: form.response?.text ?? "" } })} />
            </div>
            <div>
              <Label htmlFor="r-text">Reply text</Label>
              <Input id="r-text" value={form.response?.text ?? ""} onChange={(e) => set({ response: { code: form.response?.code ?? 451, text: e.target.value } })} placeholder="4.7.1 Try again later" />
            </div>
          </>
        )}
        {action !== "Drop" && (
          <div>
            <Label htmlFor="r-delay" hint={action === "Respond" ? "before replying" : undefined}>
              Delay (ms)
            </Label>
            <Input id="r-delay" type="number" min={0} value={form.delayMs} onChange={(e) => set({ delayMs: Math.max(0, Number(e.target.value) || 0) })} />
          </div>
        )}
        <div>
          <Label htmlFor="r-prob">Probability</Label>
          <div className="flex items-center gap-3">
            <input id="r-prob" type="range" min={0} max={100} value={Math.round(form.probability * 100)} onChange={(e) => set({ probability: Number(e.target.value) / 100 })} className="flex-1 accent-[var(--accent)]" />
            <span className="w-12 text-right font-mono text-[13px]">{Math.round(form.probability * 100)}%</span>
          </div>
        </div>
        <div className="sm:col-span-2 grid gap-3 rounded-md border border-border p-3 sm:grid-cols-2">
          <div>
            <Label htmlFor="m-sender">Sender</Label>
            <Input id="m-sender" value={form.match?.sender ?? ""} onChange={(e) => setMatch("sender", e.target.value)} placeholder="*@example.com" />
          </div>
          <div>
            <Label htmlFor="m-recipient">Recipient</Label>
            <Input id="m-recipient" value={form.match?.recipient ?? ""} onChange={(e) => setMatch("recipient", e.target.value)} placeholder="bob@*" />
          </div>
          <div>
            <Label htmlFor="m-ip">Client IP</Label>
            <Input id="m-ip" value={form.match?.clientIp ?? ""} onChange={(e) => setMatch("clientIp", e.target.value)} placeholder="127.0.0.1" />
          </div>
          <div>
            <Label htmlFor="m-user">Auth user</Label>
            <Input id="m-user" value={form.match?.user ?? ""} onChange={(e) => setMatch("user", e.target.value)} placeholder="alice@*" />
          </div>
        </div>
        <div className="sm:col-span-2">
          <Switch checked={form.enabled} onCheckedChange={(enabled) => set({ enabled })} label="Enabled" />
        </div>
      </div>
    </Dialog>
  );
}
