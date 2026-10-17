import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Check, Download, ExternalLink, FileDown, Mail, MailOpen, PenSquare, Tag, Trash2 } from "lucide-react";
import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { ConnectionSnippets } from "@/components/connection-snippets";
import { DataTable, EmptyState, NewMessagesPill, Pagination, SearchInput, Toolbar, useQueryState } from "@/components/data-table";
import { PageHeader } from "@/components/layout";
import { MessageCard, messageColumns, sourceLabel } from "@/components/message-columns";
import { snippets } from "@/components/snippets";
import { Button, Dialog, IconButton, Input, Menu, MenuContent, MenuItem, MenuSeparator, MenuTrigger, NativeSelect } from "@/components/ui";
import { api, urls, type MessageSummary } from "@/lib/api";
import { useApp, useHotkeys } from "@/lib/app-state";

type Filter = "all" | "unread" | "attachments" | `tag:${string}` | `source:${string}`;

export function EmailsPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { toast, freshIds, pendingNew, clearPendingNew } = useApp();
  const [state, setState] = useQueryState({ query: "", filter: "all", start: "0", limit: "40" });
  const start = Number(state.start) || 0;
  const limit = Number(state.limit) || 40;
  const filter = state.filter as Filter;
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [tagTarget, setTagTarget] = useState<MessageSummary[] | null>(null);

  const params = {
    query: state.query,
    start,
    limit,
    unread: filter === "unread" ? true : undefined,
    attachments: filter === "attachments" ? true : undefined,
    tag: filter.startsWith("tag:") ? filter.slice(4) : undefined,
    source: filter.startsWith("source:") ? filter.slice(7) : undefined,
  };
  const list = useQuery({ queryKey: ["messages", "list", params], queryFn: () => api.messages(params), placeholderData: (prev) => prev });

  // On an empty, unfiltered first page new mail should just appear instead of sitting behind the pill.
  useEffect(() => {
    if (pendingNew > 0 && start === 0 && (list.data?.messages.length ?? 0) === 0) clearPendingNew();
  }, [pendingNew, start, list.data, clearPendingNew]);

  useEffect(() => setSelected(new Set()), [state.query, state.filter, start]);

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["messages"] });
  const setRead = useMutation({ mutationFn: ({ ids, read }: { ids: string[]; read: boolean }) => api.setRead(ids, read), onSuccess: invalidate });
  const remove = useMutation({
    mutationFn: (ids: string[]) => api.deleteMessages(ids),
    onSuccess: (_, ids) => {
      setSelected(new Set());
      toast({ title: ids.length === 1 ? "Message deleted" : `${ids.length} messages deleted`, tone: "success" });
      void invalidate();
    },
    onError: (e) => toast({ title: "Delete failed", description: String(e), tone: "danger" }),
  });

  const deleteRows = (rows: MessageSummary[]) => {
    if (rows.length > 1 && !confirm(`Delete ${rows.length} messages?`)) return;
    remove.mutate(rows.map((m) => m.id));
  };
  const selectedRows = (list.data?.messages ?? []).filter((m) => selected.has(m.id));

  useHotkeys({ Escape: () => selected.size > 0 && setSelected(new Set()) }, [selected]);

  const columns = messageColumns<MessageSummary>((m) => m);
  const tags = list.data?.tags ?? [];

  return (
    <>
      <PageHeader
        title="Emails"
        description={list.data ? `${list.data.total.toLocaleString()} captured · ${list.data.unread.toLocaleString()} unread` : "Captured mail from every listener"}
        actions={
          <Button variant="primary" onClick={() => navigate("/compose")}>
            <PenSquare className="size-4" /> Compose
          </Button>
        }
        snippet={snippets.emails(state.query)}
      />

      <Toolbar
        selection={{
          count: selected.size,
          clear: () => setSelected(new Set()),
          actions: (
            <>
              <Button size="sm" onClick={() => setRead.mutate({ ids: [...selected], read: true })}>
                <MailOpen className="size-3.5" /> Mark read
              </Button>
              <Button size="sm" onClick={() => setRead.mutate({ ids: [...selected], read: false })}>
                <Mail className="size-3.5" /> Mark unread
              </Button>
              <Button size="sm" onClick={() => setTagTarget(selectedRows)}>
                <Tag className="size-3.5" /> Tag
              </Button>
              <Button size="sm" onClick={() => window.open(urls.export("zip", undefined, [...selected]))}>
                <FileDown className="size-3.5" /> Export .eml
              </Button>
              <Button size="sm" variant="danger" className="ml-auto" onClick={() => deleteRows(selectedRows)}>
                <Trash2 className="size-3.5" /> Delete
              </Button>
            </>
          ),
        }}
      >
        <SearchInput value={state.query} onChange={(query) => setState({ query, start: "0" })} placeholder="Search: from:alice subject:welcome is:unread has:attachment tag:billing …" />
        <NativeSelect className="w-full sm:w-56" value={filter} onChange={(e) => setState({ filter: e.target.value, start: "0" })} aria-label="Filter">
          <option value="all">All messages</option>
          <option value="unread">Unread</option>
          <option value="attachments">With attachments</option>
          {tags.length > 0 && (
            <optgroup label="Tagged">
              {tags.map((t) => (
                <option key={t} value={`tag:${t}`}>
                  tag: {t}
                </option>
              ))}
            </optgroup>
          )}
          <optgroup label="Source">
            {Object.entries(sourceLabel).map(([k, label]) => (
              <option key={k} value={`source:${k.toLowerCase()}`}>
                Source: {label}
              </option>
            ))}
          </optgroup>
        </NativeSelect>
        <Menu>
          <MenuTrigger asChild>
            <IconButton label="Export">
              <Download className="size-4" />
            </IconButton>
          </MenuTrigger>
          <MenuContent>
            <MenuItem icon={<FileDown />} onSelect={() => window.open(urls.export("zip", state.query))}>
              Download .eml files (zip)
            </MenuItem>
            <MenuItem icon={<FileDown />} onSelect={() => window.open(urls.export("json", state.query))}>
              Download JSON
            </MenuItem>
            <MenuSeparator />
            <MenuItem icon={<MailOpen />} onSelect={() => void api.setRead(null, true).then(() => invalidate())}>
              Mark all read
            </MenuItem>
            <MenuItem
              icon={<Trash2 />}
              danger
              onSelect={() => {
                if (confirm("Delete ALL captured messages? This cannot be undone.")) void api.deleteMessages(null).then(() => invalidate());
              }}
            >
              Delete all messages
            </MenuItem>
          </MenuContent>
        </Menu>
      </Toolbar>

      <NewMessagesPill />

      <DataTable
        rows={list.data?.messages ?? []}
        columns={columns}
        rowKey={(m) => m.id}
        loading={list.isLoading}
        onOpen={(m) => navigate(`/emails/${m.id}`)}
        selected={selected}
        onSelectedChange={setSelected}
        rowClassName={(m) => (freshIds.has(m.id) ? "row-new" : undefined)}
        onToggleRead={(m) => setRead.mutate({ ids: [m.id], read: !m.isRead })}
        onDelete={(m) => deleteRows([m])}
        card={(m) => <MessageCard m={m} />}
        empty={
          state.query || filter !== "all" ? (
            <EmptyState title="No matching messages" description="Try a different search or clear the filter.">
              <div className="text-center">
                <Button onClick={() => setState({ query: "", filter: "all", start: "0" })}>Clear search and filter</Button>
              </div>
            </EmptyState>
          ) : (
            <EmptyState icon={<Mail />} title="No emails yet" description="Point your application at the SMTP listener, or send one from here. Every message to any address is captured.">
              <ConnectionSnippets />
            </EmptyState>
          )
        }
        rowMenu={(m) => (
          <>
            <MenuItem icon={<ExternalLink />} onSelect={() => window.open(urls.raw(m.id), "_blank")}>
              Open raw
            </MenuItem>
            <MenuItem icon={<Download />} onSelect={() => window.open(urls.raw(m.id, true))}>
              Download .eml
            </MenuItem>
            <MenuSeparator />
            <MenuItem icon={m.isRead ? <Mail /> : <MailOpen />} shortcut="e" onSelect={() => setRead.mutate({ ids: [m.id], read: !m.isRead })}>
              Mark {m.isRead ? "unread" : "read"}
            </MenuItem>
            <MenuItem icon={<Tag />} onSelect={() => setTagTarget([m])}>
              Tag…
            </MenuItem>
            <MenuSeparator />
            <MenuItem icon={<Trash2 />} danger shortcut="#" onSelect={() => deleteRows([m])}>
              Delete
            </MenuItem>
          </>
        )}
      />
      {list.data && list.data.total > 0 && <Pagination start={start} limit={limit} total={list.data.total} onChange={(s, l) => setState({ start: String(s), limit: String(l) })} />}

      <TagDialog targets={tagTarget} onClose={() => setTagTarget(null)} knownTags={tags} />
    </>
  );
}

export function TagDialog({ targets, onClose, knownTags }: { targets: MessageSummary[] | null; onClose: () => void; knownTags: string[] }) {
  const queryClient = useQueryClient();
  const { toast } = useApp();
  const [value, setValue] = useState("");
  useEffect(() => {
    if (targets) setValue(targets.length === 1 ? targets[0].tags.join(", ") : "");
  }, [targets]);
  const save = useMutation({
    mutationFn: async () => {
      const tags = value
        .split(/[,\n]/)
        .map((t) => t.trim())
        .filter(Boolean);
      await Promise.all((targets ?? []).map((m) => api.setTags(m.id, targets!.length === 1 ? tags : [...new Set([...m.tags, ...tags])])));
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ["messages"] });
      void queryClient.invalidateQueries({ queryKey: ["message"] });
      toast({ title: "Tags updated", tone: "success" });
      onClose();
    },
    onError: (e) => toast({ title: "Could not save tags", description: String(e), tone: "danger" }),
  });
  const many = (targets?.length ?? 0) > 1;
  return (
    <Dialog
      open={!!targets}
      onOpenChange={(o) => !o && onClose()}
      title={many ? `Tag ${targets?.length} messages` : "Edit tags"}
      description={many ? "Tags are added to each selected message." : "Comma-separated. Tags are searchable with tag:name."}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={save.isPending} onClick={() => save.mutate()}>
            <Check className="size-4" /> Save
          </Button>
        </>
      }
    >
      <Input autoFocus value={value} onChange={(e) => setValue(e.target.value)} placeholder="billing, onboarding" onKeyDown={(e) => e.key === "Enter" && save.mutate()} />
      {knownTags.length > 0 && (
        <div className="mt-3 flex flex-wrap gap-1.5">
          {knownTags.map((t) => (
            <button
              key={t}
              type="button"
              onClick={() => setValue((v) => (v.split(",").map((s) => s.trim()).includes(t) ? v : v ? `${v}, ${t}` : t))}
              className="rounded-full border border-border bg-surface px-2 py-0.5 text-[12px] text-muted hover:text-fg"
            >
              {t}
            </button>
          ))}
        </div>
      )}
    </Dialog>
  );
}
