import { Paperclip } from "lucide-react";
import type { MessageSummary } from "@/lib/api";
import { cn, displayName, formatAddress, formatSize, initials } from "@/lib/format";
import type { Column } from "./data-table";
import { IconTile } from "./data-table";
import { Badge, RelativeTime, Tooltip } from "./ui";

export const sourceLabel: Record<MessageSummary["source"], string> = {
  Smtp: "SMTP",
  Submission: "Submission",
  ImapAppend: "IMAP",
  Api: "API",
  Ui: "UI",
};

export function SenderSubject({ m, unread }: { m: MessageSummary; unread?: boolean }) {
  const isUnread = unread ?? !m.isRead;
  return (
    <span className="flex min-w-0 items-center gap-3">
      <IconTile text={initials(m.from)} seed={m.from?.email ?? "?"} unread={isUnread} />
      <span className="min-w-0">
        <span className={cn("link-dotted block truncate text-[15px]", isUnread ? "font-semibold text-fg" : "font-medium text-fg/90")}>{m.subject || "(no subject)"}</span>
        <span className="mt-0.5 flex items-center gap-1.5 truncate text-[13px] text-muted">
          {m.hasAttachments && (
            <Tooltip content={`${m.attachmentCount} attachment${m.attachmentCount === 1 ? "" : "s"}`}>
              <Paperclip className="size-3.5 shrink-0" />
            </Tooltip>
          )}
          <span className="truncate">
            {displayName(m.from)}
            {m.from?.name && <span className="text-muted/70"> · {m.from.email}</span>}
          </span>
        </span>
      </span>
    </span>
  );
}

export function Recipients({ m }: { m: MessageSummary }) {
  const all = [...m.to, ...m.cc, ...m.bcc];
  const first = all[0];
  if (!first) return <span className="text-muted">{m.recipients[0] ?? "—"}</span>;
  const rest = all.length - 1;
  return (
    <Tooltip content={<span className="block max-w-xs whitespace-pre-line">{all.map(formatAddress).join("\n")}</span>}>
      <span className="inline-flex max-w-full items-center gap-1.5">
        <span className="truncate">{first.name || first.email}</span>
        {rest > 0 && <span className="shrink-0 rounded-full bg-surface-active px-1.5 py-px font-mono text-[11px] text-muted">+{rest}</span>}
      </span>
    </Tooltip>
  );
}

export function TagList({ tags, max = 3 }: { tags: string[]; max?: number }) {
  if (tags.length === 0) return <span className="text-muted/60">—</span>;
  return (
    <span className="flex flex-wrap gap-1">
      {tags.slice(0, max).map((t) => (
        <Badge key={t}>{t}</Badge>
      ))}
      {tags.length > max && <Badge>+{tags.length - max}</Badge>}
    </span>
  );
}

export function MessageCard({ m }: { m: MessageSummary }) {
  return (
    <span className="flex items-start gap-3">
      <IconTile text={initials(m.from)} seed={m.from?.email ?? "?"} unread={!m.isRead} />
      <span className="min-w-0 flex-1">
        <span className="flex items-baseline justify-between gap-2">
          <span className={cn("truncate text-[15px]", m.isRead ? "font-medium" : "font-semibold")}>{m.subject || "(no subject)"}</span>
          <RelativeTime iso={m.receivedAt} className="text-[12px] text-muted" />
        </span>
        <span className="block truncate text-[13px] text-muted">
          {displayName(m.from)} → {m.recipients[0] ?? "—"}
          {m.recipients.length > 1 ? ` +${m.recipients.length - 1}` : ""}
        </span>
      </span>
    </span>
  );
}

/** Columns shared by the Emails list and the Mailboxes folder view. */
export function messageColumns<T>(get: (row: T) => MessageSummary, options?: { unread?: (row: T) => boolean }): Column<T>[] {
  return [
    { key: "subject", header: "From / Subject", render: (r) => <SenderSubject m={get(r)} unread={options?.unread?.(r)} />, className: "min-w-[220px]" },
    { key: "to", header: "To", render: (r) => <Recipients m={get(r)} />, hideBelow: "md", className: "max-w-[220px]" },
    { key: "tags", header: "Tags", render: (r) => <TagList tags={get(r).tags} />, hideBelow: "lg", className: "max-w-[200px]" },
    { key: "size", header: "Size", render: (r) => <span className="whitespace-nowrap text-muted">{formatSize(get(r).size)}</span>, hideBelow: "lg", className: "w-24" },
    { key: "received", header: "Received", render: (r) => <RelativeTime iso={get(r).receivedAt} />, className: "w-28 whitespace-nowrap" },
  ];
}
