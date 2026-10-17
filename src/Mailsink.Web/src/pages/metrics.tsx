import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { PageHeader } from "@/components/layout";
import { snippets } from "@/components/snippets";
import { Badge, RelativeTime, Spinner } from "@/components/ui";
import { api } from "@/lib/api";
import { cn, formatSize } from "@/lib/format";

export function MetricsPage() {
  const metrics = useQuery({ queryKey: ["metrics"], queryFn: api.metrics, refetchInterval: 5_000 });
  const info = useQuery({ queryKey: ["info"], queryFn: api.info, refetchInterval: 30_000 });
  const m = metrics.data;

  return (
    <>
      <PageHeader title="Metrics" description="Live counters for the last hour. The same numbers are published on the Mailsink meter for OpenTelemetry." snippet={snippets.generic("Metrics", "metrics")} />

      {!m ? (
        <Spinner />
      ) : (
        <div className="space-y-6">
          <div className="grid grid-cols-2 gap-3 lg:grid-cols-5">
            <Stat label="Messages stored" value={m.messages.toLocaleString()} />
            <Stat label="Unread" value={m.unread.toLocaleString()} />
            <Stat label="Received since start" value={m.metrics.messagesReceived.toLocaleString()} hint={<RelativeTime iso={m.metrics.startedAt} />} />
            <Stat label="Store size" value={formatSize(m.storeSize)} />
            <Stat label="Accounts" value={m.accounts.toLocaleString()} />
          </div>

          <MessagesPerMinute points={m.metrics.messagesPerMinute} />

          <section className="grid gap-4 md:grid-cols-2">
            <div className="rounded-lg border border-border p-4">
              <h3 className="mb-3 text-[14px] font-semibold">Sessions</h3>
              <table className="w-full text-[13.5px]">
                <thead className="text-left text-[12px] font-medium text-muted">
                  <tr>
                    <th className="pb-2">Protocol</th>
                    <th className="pb-2 text-right">Active</th>
                    <th className="pb-2 text-right">Total</th>
                  </tr>
                </thead>
                <tbody>
                  {Object.keys({ ...m.metrics.totalSessions, ...m.metrics.activeSessions })
                    .sort()
                    .map((p) => (
                      <tr key={p} className="border-t border-border">
                        <td className="py-2">
                          <Badge tone="info">{p.toUpperCase()}</Badge>
                        </td>
                        <td className="py-2 text-right font-mono">{m.metrics.activeSessions[p] ?? 0}</td>
                        <td className="py-2 text-right font-mono text-muted">{m.metrics.totalSessions[p] ?? 0}</td>
                      </tr>
                    ))}
                  {Object.keys(m.metrics.totalSessions).length === 0 && (
                    <tr className="border-t border-border">
                      <td className="py-3 text-muted" colSpan={3}>
                        No sessions yet.
                      </td>
                    </tr>
                  )}
                </tbody>
              </table>
            </div>
            <div className="rounded-lg border border-border p-4">
              <h3 className="mb-3 text-[14px] font-semibold">Listeners</h3>
              <ul className="space-y-1.5 text-[13.5px]">
                {info.data?.endpoints.map((e) => (
                  <li key={e.name} className="flex items-center justify-between">
                    <span className="flex items-center gap-2">
                      <span className="size-2 rounded-full bg-accent" />
                      {e.name.toUpperCase()}
                      {e.implicitTls && <Badge tone="accent">TLS</Badge>}
                    </span>
                    <span className="font-mono text-[12.5px] text-muted">
                      {e.address}:{e.port}
                    </span>
                  </li>
                ))}
              </ul>
              {info.data && (
                <p className="mt-3 text-[12.5px] text-muted">
                  Up <RelativeTime iso={info.data.startedAt} /> · retention {info.data.retention.maxMessages.toLocaleString()} messages
                  {info.data.retention.maxAgeHours ? ` / ${info.data.retention.maxAgeHours} h` : ""}
                </p>
              )}
            </div>
          </section>
        </div>
      )}
    </>
  );
}

function Stat({ label, value, hint }: { label: string; value: string; hint?: React.ReactNode }) {
  return (
    <div className="rounded-lg border border-border bg-bg-subtle p-4">
      <div className="text-[12px] font-medium uppercase tracking-wide text-muted">{label}</div>
      <div className="mt-1 text-[26px] font-semibold leading-tight tabular-nums">{value}</div>
      {hint && <div className="mt-0.5 text-[12px] text-muted">{hint}</div>}
    </div>
  );
}

/** Single-series bar chart of messages per minute over the last hour. One hue, hover tooltip, table view. */
function MessagesPerMinute({ points }: { points: { minute: string; messages: number }[] }) {
  const [hover, setHover] = useState<number | null>(null);
  const [table, setTable] = useState(false);

  // Fill the last 60 minutes so quiet minutes show as gaps, not missing bars.
  const byMinute = new Map(points.map((p) => [new Date(p.minute).setSeconds(0, 0), p.messages]));
  const now = new Date().setSeconds(0, 0);
  const series = Array.from({ length: 60 }, (_, i) => {
    const t = now - (59 - i) * 60_000;
    return { t, value: byMinute.get(t) ?? 0 };
  });
  const max = Math.max(1, ...series.map((s) => s.value));
  const total = series.reduce((a, s) => a + s.value, 0);
  const width = 720;
  const height = 180;
  const pad = { top: 8, right: 8, bottom: 22, left: 34 };
  const plotW = width - pad.left - pad.right;
  const plotH = height - pad.top - pad.bottom;
  const slot = plotW / 60;
  const barW = Math.max(2, slot - 2);
  const ticks = niceTicks(max);
  const y = (v: number) => pad.top + plotH - (v / ticks[ticks.length - 1]) * plotH;
  const fmt = (t: number) => new Date(t).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });

  return (
    <section className="rounded-lg border border-border p-4">
      <div className="mb-2 flex items-center justify-between">
        <h3 className="text-[14px] font-semibold">
          Messages per minute <span className="ml-2 font-normal text-muted">{total.toLocaleString()} in the last hour</span>
        </h3>
        <button className="link-dotted text-[12.5px] text-muted hover:text-fg" onClick={() => setTable((v) => !v)}>
          {table ? "Show chart" : "Show table"}
        </button>
      </div>
      {table ? (
        <div className="max-h-72 overflow-auto rounded-md border border-border">
          <table className="w-full text-[13px]">
            <thead className="sticky top-0 bg-surface text-left text-[12px] text-muted">
              <tr>
                <th className="px-3 py-1.5">Minute</th>
                <th className="px-3 py-1.5 text-right">Messages</th>
              </tr>
            </thead>
            <tbody>
              {series
                .filter((s) => s.value > 0)
                .reverse()
                .map((s) => (
                  <tr key={s.t} className="border-t border-border">
                    <td className="px-3 py-1 font-mono">{fmt(s.t)}</td>
                    <td className="px-3 py-1 text-right font-mono">{s.value}</td>
                  </tr>
                ))}
              {total === 0 && (
                <tr>
                  <td className="px-3 py-3 text-muted" colSpan={2}>
                    No messages in the last hour.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="relative">
          <svg viewBox={`0 0 ${width} ${height}`} className="h-auto w-full" role="img" aria-label={`Messages per minute, ${total} in the last hour`} onMouseLeave={() => setHover(null)}>
            {ticks.map((tick) => (
              <g key={tick}>
                <line x1={pad.left} x2={width - pad.right} y1={y(tick)} y2={y(tick)} stroke="var(--border)" strokeWidth={1} />
                <text x={pad.left - 6} y={y(tick) + 3.5} textAnchor="end" fontSize={10} fill="var(--text-muted)" fontFamily="var(--font-mono)">
                  {tick}
                </text>
              </g>
            ))}
            <line x1={pad.left} x2={width - pad.right} y1={pad.top + plotH} y2={pad.top + plotH} stroke="var(--border-strong)" strokeWidth={1} />
            {series.map((s, i) => {
              const x = pad.left + i * slot + (slot - barW) / 2;
              const top = y(s.value);
              const h = Math.max(0, pad.top + plotH - top);
              return (
                <g key={s.t} onMouseEnter={() => setHover(i)}>
                  <rect x={pad.left + i * slot} y={pad.top} width={slot} height={plotH} fill="transparent" />
                  {s.value > 0 && <rect x={x} y={top} width={barW} height={h} rx={Math.min(3, barW / 2)} fill="var(--accent)" opacity={hover === null || hover === i ? 1 : 0.55} />}
                </g>
              );
            })}
            {[0, 15, 30, 45, 59].map((i) => (
              <text key={i} x={pad.left + i * slot + slot / 2} y={height - 6} textAnchor="middle" fontSize={10} fill="var(--text-muted)" fontFamily="var(--font-mono)">
                {fmt(series[i].t)}
              </text>
            ))}
          </svg>
          {hover !== null && (
            <div
              className={cn("pointer-events-none absolute top-2 rounded-md border border-border bg-bg-subtle px-2.5 py-1.5 text-[12.5px] shadow-lg", hover > 40 ? "right-2" : "left-10")}
            >
              <span className="font-mono text-muted">{fmt(series[hover].t)}</span> · <span className="font-semibold tabular-nums">{series[hover].value}</span> {series[hover].value === 1 ? "message" : "messages"}
            </div>
          )}
        </div>
      )}
    </section>
  );
}

function niceTicks(max: number): number[] {
  const raw = max / 3;
  const pow = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 5, 10].map((m) => m * pow).find((s) => s >= raw) ?? pow * 10;
  const top = Math.ceil(max / step) * step;
  const ticks: number[] = [];
  for (let v = 0; v <= top; v += step) ticks.push(v);
  return ticks.length > 1 ? ticks : [0, 1];
}
