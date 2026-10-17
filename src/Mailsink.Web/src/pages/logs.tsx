import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Activity, Lock, Mail, Trash2 } from "lucide-react";
import { useNavigate, useParams } from "react-router-dom";
import { DataTable, EmptyState, IconTile, Toolbar, useQueryState } from "@/components/data-table";
import { PageHeader } from "@/components/layout";
import { snippets } from "@/components/snippets";
import { Badge, Button, CodeChip, Dialog, MenuItem, NativeSelect, RelativeTime, Spinner } from "@/components/ui";
import { api } from "@/lib/api";
import { useApp } from "@/lib/app-state";
import { TranscriptView } from "./email-detail";

const protocols = ["smtp", "imap", "pop3"];

export function LogsPage() {
  const navigate = useNavigate();
  const { id } = useParams();
  const queryClient = useQueryClient();
  const { toast } = useApp();
  const [state, setState] = useQueryState({ protocol: "" });
  const logs = useQuery({ queryKey: ["logs", state.protocol], queryFn: () => api.logs(state.protocol || undefined), refetchInterval: 5_000 });
  const transcript = useQuery({ queryKey: ["log", id], queryFn: () => api.log(id!), enabled: !!id, retry: false });
  const clear = useMutation({
    mutationFn: api.clearLogs,
    onSuccess: () => {
      toast({ title: "Transcripts cleared", tone: "success" });
      void queryClient.invalidateQueries({ queryKey: ["logs"] });
    },
  });

  return (
    <>
      <PageHeader
        title="Logs"
        description="Protocol transcripts of every SMTP, IMAP and POP3 session, with credentials redacted. Kept in a ring buffer in memory."
        actions={
          <Button onClick={() => confirm("Clear all transcripts?") && clear.mutate()}>
            <Trash2 className="size-4" /> Clear
          </Button>
        }
        snippet={snippets.generic("List sessions", "logs?protocol=smtp")}
      />

      <Toolbar>
        <NativeSelect className="w-48" value={state.protocol} onChange={(e) => setState({ protocol: e.target.value })} aria-label="Protocol">
          <option value="">All protocols</option>
          {protocols.map((p) => (
            <option key={p} value={p}>
              {p.toUpperCase()}
            </option>
          ))}
        </NativeSelect>
        <span className="text-[13px] text-muted">{logs.data ? `${logs.data.length} sessions` : ""}</span>
      </Toolbar>

      <DataTable
        rows={logs.data ?? []}
        rowKey={(s) => s.id}
        loading={logs.isLoading}
        onOpen={(s) => navigate(`/logs/${s.id}`)}
        dense
        empty={<EmptyState icon={<Activity />} title="No sessions yet" description="Connect an SMTP, IMAP or POP3 client and its wire conversation shows up here." />}
        columns={[
          {
            key: "session",
            header: "Session",
            render: (s) => (
              <span className="flex items-center gap-3">
                <IconTile size="sm" text={s.protocol.slice(0, 1).toUpperCase()} seed={s.protocol} />
                <span className="min-w-0">
                  <span className="flex items-center gap-2">
                    <Badge tone="info">{s.protocol.toUpperCase()}</Badge>
                    {s.secure && (
                      <Badge tone="accent">
                        <Lock className="size-3" /> TLS
                      </Badge>
                    )}
                    {s.endedAt === null && <Badge tone="warning">open</Badge>}
                  </span>
                  <span className="mt-0.5 block font-mono text-[12px] text-muted">{s.id}</span>
                </span>
              </span>
            ),
          },
          { key: "ip", header: "Client", hideBelow: "sm", render: (s) => (s.clientIp ? <CodeChip value={s.clientIp} /> : <span className="text-muted">—</span>) },
          { key: "user", header: "User", hideBelow: "md", render: (s) => s.user ?? <span className="text-muted">—</span> },
          { key: "commands", header: "Commands", hideBelow: "md", className: "w-24", render: (s) => <span className="text-muted">{s.commands}</span> },
          {
            key: "messages",
            header: "Messages",
            hideBelow: "lg",
            className: "w-24",
            render: (s) =>
              s.messageIds.length > 0 ? (
                <span className="inline-flex items-center gap-1 text-muted">
                  <Mail className="size-3.5" /> {s.messageIds.length}
                </span>
              ) : (
                <span className="text-muted">—</span>
              ),
          },
          {
            key: "duration",
            header: "Duration",
            hideBelow: "sm",
            className: "w-24",
            render: (s) => <span className="font-mono text-[12.5px] text-muted">{s.endedAt ? duration(s.startedAt, s.endedAt) : "…"}</span>,
          },
          { key: "started", header: "Started", className: "w-28 whitespace-nowrap", render: (s) => <RelativeTime iso={s.startedAt} /> },
        ]}
        rowMenu={(s) => (
          <>
            <MenuItem icon={<Activity />} onSelect={() => navigate(`/logs/${s.id}`)}>
              View transcript
            </MenuItem>
            {s.messageIds.slice(0, 5).map((m) => (
              <MenuItem key={m} icon={<Mail />} onSelect={() => navigate(`/emails/${m}`)}>
                Open message {m.slice(0, 8)}…
              </MenuItem>
            ))}
          </>
        )}
      />

      <Dialog open={!!id} onOpenChange={(o) => !o && navigate("/logs")} title={`Transcript ${id ?? ""}`} wide>
        {transcript.isLoading ? <Spinner /> : transcript.data ? <TranscriptView transcript={transcript.data} /> : <p className="text-muted">This transcript is no longer available.</p>}
      </Dialog>
    </>
  );
}

function duration(start: string, end: string): string {
  const ms = new Date(end).getTime() - new Date(start).getTime();
  if (ms < 1000) return `${ms} ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} s`;
  return `${Math.floor(ms / 60_000)}m ${Math.round((ms % 60_000) / 1000)}s`;
}
