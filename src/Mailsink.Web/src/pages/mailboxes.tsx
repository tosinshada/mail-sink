import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Archive, Eraser, FileEdit, Flag, FlagOff, Folder, FolderPlus, Inbox, Mail, MailOpen, Pencil, Send, Trash, Trash2, Users } from "lucide-react";
import { useEffect, useState, type DragEvent, type ReactNode } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { DataTable, EmptyState, Pagination, Toolbar, useQueryState } from "@/components/data-table";
import { PageHeader } from "@/components/layout";
import { MessageCard, messageColumns } from "@/components/message-columns";
import { snippets } from "@/components/snippets";
import { Badge, Button, Dialog, Input, MenuItem, MenuSeparator, Spinner } from "@/components/ui";
import { api, type Mailbox, type MailboxMessage } from "@/lib/api";
import { useApp } from "@/lib/app-state";
import { cn, formatSize } from "@/lib/format";

const specialIcons: Record<string, ReactNode> = {
  Inbox: <Inbox className="size-4" />,
  Sent: <Send className="size-4" />,
  Drafts: <FileEdit className="size-4" />,
  Trash: <Trash className="size-4" />,
  Junk: <Eraser className="size-4" />,
  Archive: <Archive className="size-4" />,
};

const specialOrder = ["Inbox", "Drafts", "Sent", "Archive", "Junk", "Trash"];

function sortMailboxes(list: Mailbox[]): Mailbox[] {
  const rank = (m: Mailbox) => (m.name.toUpperCase() === "INBOX" ? -1 : m.specialUse ? specialOrder.indexOf(m.specialUse) : 99);
  return [...list].sort((a, b) => {
    const ai = rank(a);
    const bi = rank(b);
    return ai === bi ? a.name.localeCompare(b.name) : ai - bi;
  });
}

export function MailboxesPage() {
  const { account, setAccount, toast } = useApp();
  const navigate = useNavigate();
  const { folder: folderParam } = useParams();
  const queryClient = useQueryClient();
  const folder = folderParam ? decodeURIComponent(folderParam) : "INBOX";
  const [state, setState] = useQueryState({ start: "0", limit: "40" });
  const start = Number(state.start) || 0;
  const limit = Number(state.limit) || 40;
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [newFolder, setNewFolder] = useState(false);
  const [rename, setRename] = useState<Mailbox | null>(null);

  const accounts = useQuery({ queryKey: ["accounts"], queryFn: api.accounts });
  const mailboxes = useQuery({ queryKey: ["mailboxes", account], queryFn: () => api.mailboxes(account!), enabled: !!account });
  const messages = useQuery({
    queryKey: ["mailbox-messages", account, folder, start, limit],
    queryFn: () => api.mailboxMessages(account!, folder, start, limit),
    enabled: !!account,
    placeholderData: (prev) => prev,
  });

  useEffect(() => setSelected(new Set()), [account, folder, start]);

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ["mailboxes", account] });
    void queryClient.invalidateQueries({ queryKey: ["mailbox-messages", account] });
  };
  const fail = (title: string) => (e: unknown) => toast({ title, description: String(e), tone: "danger" });

  const flags = useMutation({
    mutationFn: ({ uids, add, remove }: { uids: number[]; add?: string[]; remove?: string[] }) => api.flags(account!, folder, uids, add ?? null, remove ?? null),
    onSuccess: refresh,
    onError: fail("Could not update flags"),
  });
  const move = useMutation({
    mutationFn: ({ uids, target, copy }: { uids: number[]; target: string; copy?: boolean }) => api.move(account!, folder, uids, target, copy),
    onSuccess: (_, v) => {
      toast({ title: `${v.uids.length === 1 ? "Message" : `${v.uids.length} messages`} ${v.copy ? "copied" : "moved"} to ${v.target}`, tone: "success" });
      setSelected(new Set());
      refresh();
    },
    onError: fail("Move failed"),
  });
  const expunge = useMutation({
    mutationFn: () => api.expunge(account!, folder),
    onSuccess: (uids) => {
      toast({ title: uids.length === 0 ? "Nothing to expunge" : `${uids.length} message${uids.length === 1 ? "" : "s"} expunged`, tone: "success" });
      refresh();
    },
    onError: fail("Expunge failed"),
  });
  const createFolder = useMutation({
    mutationFn: (name: string) => api.createMailbox(account!, name),
    onSuccess: (m) => {
      toast({ title: `Folder ${m.name} created`, tone: "success" });
      setNewFolder(false);
      refresh();
      navigate(`/mailboxes/${encodeURIComponent(m.name)}`);
    },
    onError: fail("Could not create folder"),
  });
  const renameFolder = useMutation({
    mutationFn: ({ from, to }: { from: string; to: string }) => api.renameMailbox(account!, from, to),
    onSuccess: (m) => {
      setRename(null);
      refresh();
      navigate(`/mailboxes/${encodeURIComponent(m.name)}`);
    },
    onError: fail("Could not rename folder"),
  });
  const deleteFolder = useMutation({
    mutationFn: (name: string) => api.deleteMailbox(account!, name),
    onSuccess: () => {
      toast({ title: "Folder deleted", tone: "success" });
      refresh();
      navigate("/mailboxes");
    },
    onError: fail("Could not delete folder"),
  });

  if (!account) {
    return (
      <>
        <PageHeader title="Mailboxes" description="The per-account IMAP view: folders, flags and UIDs exactly as a mail client sees them." />
        <div className="rounded-lg border border-border">
          <EmptyState icon={<Users />} title="Pick an account" description="Mailboxes are per user. Choose one from the switcher at the top of the sidebar, or below.">
            {accounts.isLoading ? (
              <Spinner className="mx-auto" />
            ) : accounts.data && accounts.data.length > 0 ? (
              <ul className="divide-y divide-border overflow-hidden rounded-lg border border-border">
                {accounts.data.map((a) => (
                  <li key={a.address}>
                    <button onClick={() => setAccount(a.address)} className="flex w-full items-center justify-between px-4 py-3 text-left hover:bg-surface-hover">
                      <span className="font-medium">{a.address}</span>
                      <span className="text-[13px] text-muted">
                        {a.mailboxes} folders · {a.messages} messages
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="text-center text-[14px] text-muted">
                No accounts exist yet. Accounts are created automatically when mail arrives for a new address, or on the{" "}
                <button className="link-dotted" onClick={() => navigate("/accounts")}>
                  Accounts
                </button>{" "}
                page.
              </p>
            )}
          </EmptyState>
        </div>
      </>
    );
  }

  const current = mailboxes.data?.find((m) => m.name.toUpperCase() === folder.toUpperCase());
  const sorted = sortMailboxes(mailboxes.data ?? []);
  const rows = messages.data?.messages ?? [];
  const otherFolders = sorted.filter((m) => m.name !== current?.name);
  const trash = sorted.find((m) => m.specialUse === "Trash");

  const onDropToFolder = (target: Mailbox) => (e: DragEvent) => {
    e.preventDefault();
    const payload = e.dataTransfer.getData("text/plain");
    const uids = payload
      .split(",")
      .map(Number)
      .filter((n) => Number.isFinite(n) && n > 0);
    if (uids.length > 0 && target.name !== current?.name) move.mutate({ uids, target: target.name, copy: e.ctrlKey || e.metaKey });
  };

  // The folder column takes width, so the mailbox table drops Tags and Size (both visible on the message itself).
  const columns = messageColumns<MailboxMessage>((r) => r.message, { unread: (r) => !r.flags.includes("\\Seen") }).filter((c) => c.key !== "tags" && c.key !== "size");
  columns.splice(1, 0, {
    key: "flags",
    header: "Flags",
    hideBelow: "lg",
    className: "w-40",
    render: (r) => (
      <span className="flex flex-wrap gap-1">
        {r.flags.includes("\\Flagged") && (
          <Badge tone="warning">
            <Flag className="size-3" /> flagged
          </Badge>
        )}
        {r.flags.includes("\\Deleted") && <Badge tone="danger">deleted</Badge>}
        {r.flags.includes("\\Draft") && <Badge>draft</Badge>}
        {r.flags.includes("\\Answered") && <Badge tone="info">answered</Badge>}
        {r.flags.filter((f) => !f.startsWith("\\")).map((f) => (
          <Badge key={f}>{f}</Badge>
        ))}
      </span>
    ),
  });
  columns.push({ key: "uid", header: "UID", hideBelow: "lg", className: "w-20", render: (r) => <span className="font-mono text-[12.5px] text-muted">{r.uid}</span> });

  return (
    <>
      <PageHeader
        title="Mailboxes"
        description={
          <>
            IMAP view for <span className="font-medium text-fg">{account}</span>
            {current && (
              <>
                {" "}
                · {current.name} · {current.messages} messages, {current.unseen} unseen · UIDVALIDITY <span className="font-mono">{current.uidValidity}</span>
              </>
            )}
          </>
        }
        actions={
          <Button variant="primary" onClick={() => setNewFolder(true)}>
            <FolderPlus className="size-4" /> New folder
          </Button>
        }
        snippet={snippets.mailboxes(account)}
      />

      <div className="flex flex-col gap-5 md:flex-row">
        <aside className="shrink-0 md:w-[220px]">
          <ul className="flex gap-1 overflow-x-auto md:flex-col md:overflow-visible">
            {mailboxes.isLoading && (
              <li className="p-3">
                <Spinner />
              </li>
            )}
            {sorted.map((m) => {
              const active = m.name === current?.name;
              return (
                <li key={m.id}>
                  <button
                    onClick={() => navigate(`/mailboxes/${encodeURIComponent(m.name)}`)}
                    onDragOver={(e) => {
                      if (!active) {
                        e.preventDefault();
                        e.dataTransfer.dropEffect = e.ctrlKey ? "copy" : "move";
                      }
                    }}
                    onDrop={onDropToFolder(m)}
                    className={cn(
                      "flex w-full items-center gap-2.5 whitespace-nowrap rounded-lg px-3 py-2 text-left text-[14.5px] transition-colors",
                      active ? "bg-surface-hover text-fg" : "text-fg/80 hover:bg-surface hover:text-fg",
                    )}
                  >
                    <span className="text-muted">{(m.specialUse && specialIcons[m.specialUse]) ?? (m.name.toUpperCase() === "INBOX" ? specialIcons.Inbox : <Folder className="size-4" />)}</span>
                    <span className="min-w-0 flex-1 truncate">{m.name}</span>
                    {m.unseen > 0 && <span className="rounded-full bg-surface-active px-1.5 py-px font-mono text-[11px] text-muted">{m.unseen}</span>}
                  </button>
                </li>
              );
            })}
          </ul>
          {current && (
            <div className="mt-3 hidden space-y-1 px-3 text-[12.5px] text-muted md:block">
              <p>{formatSize(current.size)}</p>
              <p className="font-mono">UIDNEXT {current.uidNext}</p>
              <p>{current.subscribed ? "Subscribed" : "Not subscribed"}</p>
              <div className="flex gap-2 pt-1">
                {!current.specialUse && (
                  <>
                    <button className="link-dotted hover:text-fg" onClick={() => setRename(current)}>
                      Rename
                    </button>
                    <button className="link-dotted hover:text-danger" onClick={() => confirm(`Delete folder ${current.name} and its messages?`) && deleteFolder.mutate(current.name)}>
                      Delete
                    </button>
                  </>
                )}
                <button className="link-dotted hover:text-fg" onClick={() => expunge.mutate()}>
                  Expunge
                </button>
              </div>
            </div>
          )}
        </aside>

        <div className="min-w-0 flex-1">
          <Toolbar
            selection={{
              count: selected.size,
              clear: () => setSelected(new Set()),
              actions: (
                <>
                  <Button size="sm" onClick={() => flags.mutate({ uids: [...selected], add: ["\\Seen"] })}>
                    <MailOpen className="size-3.5" /> Seen
                  </Button>
                  <Button size="sm" onClick={() => flags.mutate({ uids: [...selected], remove: ["\\Seen"] })}>
                    <Mail className="size-3.5" /> Unseen
                  </Button>
                  <Button size="sm" onClick={() => flags.mutate({ uids: [...selected], add: ["\\Flagged"] })}>
                    <Flag className="size-3.5" /> Flag
                  </Button>
                  <MoveMenu folders={otherFolders} onMove={(target) => move.mutate({ uids: [...selected], target })} />
                  <Button size="sm" variant="danger" className="ml-auto" onClick={() => (trash && current?.specialUse !== "Trash" ? move.mutate({ uids: [...selected], target: trash.name }) : flags.mutate({ uids: [...selected], add: ["\\Deleted"] }))}>
                    <Trash2 className="size-3.5" /> {trash && current?.specialUse !== "Trash" ? "Move to Trash" : "Mark \\Deleted"}
                  </Button>
                </>
              ),
            }}
          >
            <p className="text-[13px] text-muted">Drag a message onto a folder to move it (hold Ctrl to copy). IMAP clients see every change live.</p>
          </Toolbar>

          <DataTable
            rows={rows}
            columns={columns}
            rowKey={(r) => String(r.uid)}
            loading={messages.isLoading}
            onOpen={(r) => navigate(`/emails/${r.message.id}`)}
            selected={new Set([...selected].map(String))}
            onSelectedChange={(s) => setSelected(new Set([...s].map(Number)))}
            onToggleRead={(r) => flags.mutate(r.flags.includes("\\Seen") ? { uids: [r.uid], remove: ["\\Seen"] } : { uids: [r.uid], add: ["\\Seen"] })}
            onDelete={(r) => (trash && current?.specialUse !== "Trash" ? move.mutate({ uids: [r.uid], target: trash.name }) : flags.mutate({ uids: [r.uid], add: ["\\Deleted"] }))}
            dragData={(r) => (selected.has(r.uid) ? [...selected].join(",") : String(r.uid))}
            rowClassName={(r) => (r.flags.includes("\\Deleted") ? "opacity-50" : undefined)}
            card={(r) => <MessageCard m={r.message} />}
            empty={
              <EmptyState icon={<Folder />} title={`${current?.name ?? folder} is empty`} description={current?.specialUse === "Inbox" ? `Mail addressed to ${account} will appear here. Plus-addressing (${account.replace("@", "+tag@")}) also lands in this inbox.` : "Move messages here, or let an IMAP client do it."} />
            }
            rowMenu={(r) => {
              const seen = r.flags.includes("\\Seen");
              const flagged = r.flags.includes("\\Flagged");
              return (
                <>
                  <MenuItem icon={seen ? <Mail /> : <MailOpen />} shortcut="e" onSelect={() => flags.mutate(seen ? { uids: [r.uid], remove: ["\\Seen"] } : { uids: [r.uid], add: ["\\Seen"] })}>
                    Mark {seen ? "unseen" : "seen"}
                  </MenuItem>
                  <MenuItem icon={flagged ? <FlagOff /> : <Flag />} onSelect={() => flags.mutate(flagged ? { uids: [r.uid], remove: ["\\Flagged"] } : { uids: [r.uid], add: ["\\Flagged"] })}>
                    {flagged ? "Remove flag" : "Flag"}
                  </MenuItem>
                  <MenuSeparator />
                  {otherFolders.map((f) => (
                    <MenuItem key={f.id} icon={(f.specialUse && specialIcons[f.specialUse]) ?? <Folder />} onSelect={() => move.mutate({ uids: [r.uid], target: f.name })}>
                      Move to {f.name}
                    </MenuItem>
                  ))}
                  {otherFolders.length > 0 && <MenuSeparator />}
                  <MenuItem icon={<Trash2 />} danger shortcut="#" onSelect={() => flags.mutate({ uids: [r.uid], add: ["\\Deleted"] })}>
                    Mark \Deleted
                  </MenuItem>
                </>
              );
            }}
          />
          {messages.data && messages.data.total > 0 && <Pagination start={start} limit={limit} total={messages.data.total} onChange={(s, l) => setState({ start: String(s), limit: String(l) })} />}
        </div>
      </div>

      <NameDialog open={newFolder} title="New folder" description="Use / to nest folders (Projects/2026). Clients see it on their next LIST." initial="" submit="Create" pending={createFolder.isPending} onClose={() => setNewFolder(false)} onSubmit={(name) => createFolder.mutate(name)} />
      <NameDialog open={!!rename} title={`Rename ${rename?.name ?? ""}`} initial={rename?.name ?? ""} submit="Rename" pending={renameFolder.isPending} onClose={() => setRename(null)} onSubmit={(to) => rename && renameFolder.mutate({ from: rename.name, to })} />
    </>
  );
}

function MoveMenu({ folders, onMove }: { folders: Mailbox[]; onMove: (target: string) => void }) {
  return (
    <select
      aria-label="Move to folder"
      className="h-8 rounded-md border border-border bg-surface px-2 text-[13px]"
      value=""
      onChange={(e) => {
        if (e.target.value) onMove(e.target.value);
        e.target.value = "";
      }}
    >
      <option value="">Move to…</option>
      {folders.map((f) => (
        <option key={f.id} value={f.name}>
          {f.name}
        </option>
      ))}
    </select>
  );
}

function NameDialog({ open, title, description, initial, submit, pending, onClose, onSubmit }: { open: boolean; title: string; description?: string; initial: string; submit: string; pending: boolean; onClose: () => void; onSubmit: (name: string) => void }) {
  const [value, setValue] = useState(initial);
  useEffect(() => setValue(initial), [initial, open]);
  const go = () => value.trim() && onSubmit(value.trim());
  return (
    <Dialog
      open={open}
      onOpenChange={(o) => !o && onClose()}
      title={title}
      description={description}
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" loading={pending} onClick={go}>
            <Pencil className="size-4" /> {submit}
          </Button>
        </>
      }
    >
      <Input autoFocus value={value} onChange={(e) => setValue(e.target.value)} placeholder="Folder name" onKeyDown={(e) => e.key === "Enter" && go()} />
    </Dialog>
  );
}
