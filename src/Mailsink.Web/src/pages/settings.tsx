import { useQuery } from "@tanstack/react-query";
import { Bell, Download, ExternalLink, Monitor, Moon, Sun } from "lucide-react";
import { PageHeader } from "@/components/layout";
import { snippets } from "@/components/snippets";
import { Badge, Button, Card, CodeChip, RelativeTime, Spinner, Switch } from "@/components/ui";
import { api, urls } from "@/lib/api";
import { useApp, type Theme } from "@/lib/app-state";
import { cn, formatSize } from "@/lib/format";

export function SettingsPage() {
  const { theme, setTheme, notifications, setNotifications } = useApp();
  const info = useQuery({ queryKey: ["info"], queryFn: api.info, refetchInterval: 30_000 });
  const i = info.data;

  const themes: { value: Theme; label: string; icon: typeof Sun }[] = [
    { value: "dark", label: "Dark", icon: Moon },
    { value: "light", label: "Light", icon: Sun },
    { value: "system", label: "System", icon: Monitor },
  ];

  return (
    <>
      <PageHeader title="Settings" description="Effective server configuration (read-only; change it through appsettings, environment variables or CLI flags) and browser preferences." snippet={snippets.generic("Server info", "info")} />

      <div className="grid gap-5 lg:grid-cols-2">
        <Card title="Appearance">
          <div className="flex flex-wrap gap-2">
            {themes.map(({ value, label, icon: Icon }) => (
              <button
                key={value}
                onClick={() => setTheme(value)}
                className={cn("flex items-center gap-2 rounded-md border px-3 py-2 text-[14px]", theme === value ? "border-fg bg-surface-active" : "border-border bg-surface text-muted hover:text-fg")}
              >
                <Icon className="size-4" /> {label}
              </button>
            ))}
          </div>
          <div className="mt-5 flex items-start gap-3">
            <Bell className="mt-0.5 size-4 text-muted" />
            <div>
              <Switch checked={notifications} onCheckedChange={setNotifications} label="Desktop notifications for new mail" />
              <p className="mt-1 text-[12.5px] text-muted">Shown only while this tab is in the background. The unread count always appears in the tab title.</p>
            </div>
          </div>
        </Card>

        <Card title="Server" actions={i && <Badge>v{i.version}</Badge>}>
          {!i ? (
            <Spinner />
          ) : (
            <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-[13.5px]">
              <Row label="Hostname">
                <CodeChip value={i.hostname} />
              </Row>
              <Row label="Default domain">
                <CodeChip value={i.defaultDomain} />
              </Row>
              <Row label="Started">
                <RelativeTime iso={i.startedAt} />
              </Row>
              <Row label="Storage">
                {i.storage} · {formatSize(i.storeSize)}
              </Row>
              <Row label="Data dir">
                <span className="break-all font-mono text-[12.5px]">{i.dataDir}</span>
              </Row>
              <Row label="Auth mode">
                <Badge tone={i.authMode === "Registry" ? "warning" : "accent"}>{i.authMode}</Badge>
              </Row>
              <Row label="TLS">
                {i.tlsAvailable ? <Badge tone="accent">available{i.requireTls ? ", required" : ""}</Badge> : <Badge>not configured</Badge>}
              </Row>
              <Row label="UI auth">{i.uiAuth ? <Badge tone="info">basic auth</Badge> : <span className="text-muted">off</span>}</Row>
              <Row label="Max message">{formatSize(i.maxMessageSize)}</Row>
            </dl>
          )}
        </Card>

        <Card title="Listeners">
          {!i ? (
            <Spinner />
          ) : (
            <table className="w-full text-[13.5px]">
              <thead className="text-left text-[12px] font-medium text-muted">
                <tr>
                  <th className="pb-2">Name</th>
                  <th className="pb-2">Protocol</th>
                  <th className="pb-2">Address</th>
                  <th className="pb-2">TLS</th>
                </tr>
              </thead>
              <tbody>
                {i.endpoints.map((e) => (
                  <tr key={e.name} className="border-t border-border">
                    <td className="py-2 font-medium">{e.name}</td>
                    <td className="py-2 text-muted">{e.protocol}</td>
                    <td className="py-2">
                      <CodeChip value={`${e.address}:${e.port}`} />
                    </td>
                    <td className="py-2">{e.implicitTls ? <Badge tone="accent">implicit</Badge> : i.tlsAvailable ? <Badge>STARTTLS</Badge> : <span className="text-muted">—</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {i?.tlsAvailable && (
            <div className="mt-4 flex flex-wrap items-center gap-3">
              <a href={urls.tlsCert()} className="inline-flex h-9 items-center gap-2 rounded-md border border-border bg-surface px-3.5 text-[14px] hover:bg-surface-hover">
                <Download className="size-4" /> Download TLS certificate (PEM)
              </a>
              <span className="text-[12.5px] text-muted">Trust it in your client to test STARTTLS and implicit TLS without warnings.</span>
            </div>
          )}
        </Card>

        <Card title="Retention">
          {!i ? (
            <Spinner />
          ) : (
            <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-[13.5px]">
              <Row label="Max messages">{i.retention.maxMessages.toLocaleString()}</Row>
              <Row label="Max age">{i.retention.maxAgeHours ? `${i.retention.maxAgeHours} h` : <span className="text-muted">unlimited</span>}</Row>
              <Row label="Max total size">{i.retention.maxTotalSize ? formatSize(i.retention.maxTotalSize) : <span className="text-muted">unlimited</span>}</Row>
              <Row label="Applies to mailboxes">{i.retention.appliesToMailboxes ? "yes" : "no (catch-all only)"}</Row>
            </dl>
          )}
        </Card>

        <Card title="API" className="lg:col-span-2">
          <div className="flex flex-wrap items-center gap-3">
            <Button onClick={() => window.open(urls.docs(), "_blank")}>
              <ExternalLink className="size-4" /> API reference (Scalar)
            </Button>
            <CodeChip value={`${window.location.origin}${urls.docs().replace(/docs$/, "openapi/v1.json")}`} />
            <span className="text-[12.5px] text-muted">Every page's &lt;/&gt; button shows the equivalent API call.</span>
          </div>
        </Card>
      </div>
    </>
  );
}

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <>
      <dt className="text-muted">{label}</dt>
      <dd className="min-w-0">{children}</dd>
    </>
  );
}
