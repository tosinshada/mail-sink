import { ArrowDown, ChevronDown, Inbox, MoreHorizontal, Search, X } from "lucide-react";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { useSearchParams } from "react-router-dom";
import { useApp, useHotkeys } from "@/lib/app-state";
import { cn, hue as hueOf } from "@/lib/format";
import { Checkbox, Kbd, Menu, MenuContent, MenuTrigger, Spinner, Tooltip } from "./ui";

export interface Column<T> {
  key: string;
  header: ReactNode;
  render: (row: T) => ReactNode;
  /** Extra classes for both header and cells (widths, alignment). */
  className?: string;
  /** Hide the column below this breakpoint. */
  hideBelow?: "sm" | "md" | "lg";
}

const hidden: Record<NonNullable<Column<unknown>["hideBelow"]>, string> = {
  sm: "hidden sm:table-cell",
  md: "hidden md:table-cell",
  lg: "hidden lg:table-cell",
};

export interface DataTableProps<T> {
  rows: T[];
  columns: Column<T>[];
  rowKey: (row: T) => string;
  loading?: boolean;
  empty?: ReactNode;
  onOpen?: (row: T) => void;
  /** Row ⋯ menu items (MenuItem elements). */
  rowMenu?: (row: T) => ReactNode;
  selected?: Set<string>;
  onSelectedChange?: (selected: Set<string>) => void;
  rowClassName?: (row: T) => string | undefined;
  /** Hotkey handlers applied to the focused row (j/k move the cursor). */
  onToggleRead?: (row: T) => void;
  onDelete?: (row: T) => void;
  /** Two-line card rendering used below the sm breakpoint. */
  card?: (row: T) => ReactNode;
  /** Native drag support: returns the payload to put in dataTransfer, or null to make the row non-draggable. */
  dragData?: (row: T) => string | null;
  dense?: boolean;
}

/** The one table used by every list (design §11.1): hairline rows, hover checkboxes, ⋯ menus, keyboard cursor. */
export function DataTable<T>({
  rows,
  columns,
  rowKey,
  loading,
  empty,
  onOpen,
  rowMenu,
  selected,
  onSelectedChange,
  rowClassName,
  onToggleRead,
  onDelete,
  card,
  dragData,
  dense,
}: DataTableProps<T>) {
  const [cursor, setCursor] = useState<number>(-1);
  const bodyRef = useRef<HTMLTableSectionElement>(null);
  const selectable = !!onSelectedChange;
  const keys = rows.map(rowKey);

  useEffect(() => {
    if (cursor >= rows.length) setCursor(rows.length - 1);
  }, [rows.length, cursor]);

  useEffect(() => {
    if (cursor < 0) return;
    const el = bodyRef.current?.children[cursor] as HTMLElement | undefined;
    el?.scrollIntoView({ block: "nearest" });
  }, [cursor]);

  const current = cursor >= 0 && cursor < rows.length ? rows[cursor] : undefined;

  const toggle = (key: string, on: boolean) => {
    if (!onSelectedChange) return;
    const next = new Set(selected);
    if (on) next.add(key);
    else next.delete(key);
    onSelectedChange(next);
  };

  useHotkeys(
    {
      j: () => setCursor((c) => Math.min(rows.length - 1, c + 1)),
      k: () => setCursor((c) => Math.max(0, c - 1)),
      Enter: () => current && onOpen?.(current),
      x: () => current && selectable && toggle(rowKey(current), !selected?.has(rowKey(current))),
      e: () => current && onToggleRead?.(current),
      "#": () => current && onDelete?.(current),
      Delete: () => current && onDelete?.(current),
    },
    [rows, cursor, selected, onOpen, onToggleRead, onDelete],
  );

  const allSelected = selectable && rows.length > 0 && keys.every((k) => selected?.has(k));
  const someSelected = selectable && keys.some((k) => selected?.has(k));

  if (!loading && rows.length === 0) {
    return <div className="rounded-lg border border-border">{empty ?? <EmptyState title="Nothing here yet" />}</div>;
  }

  return (
    <div className="relative overflow-x-auto rounded-lg border border-border">
      {loading && rows.length === 0 && (
        <div className="flex h-40 items-center justify-center">
          <Spinner />
        </div>
      )}
      {rows.length > 0 && (
        <table className={cn("w-full border-collapse text-[14.5px]", card && "table-fixed sm:table-auto")}>
          <thead className="hidden sm:table-header-group">
            <tr className="bg-surface text-left text-[13px] font-medium text-muted">
              {selectable && (
                <th className="w-10 px-3 py-2.5">
                  <span className="flex items-center">
                    <Checkbox
                      label="Select all"
                      checked={allSelected ? true : someSelected ? "indeterminate" : false}
                      onCheckedChange={(on) => onSelectedChange?.(on ? new Set([...(selected ?? []), ...keys]) : new Set([...(selected ?? [])].filter((k) => !keys.includes(k))))}
                    />
                  </span>
                </th>
              )}
              {columns.map((c) => (
                <th key={c.key} className={cn("px-3 py-2.5 font-medium", c.hideBelow && hidden[c.hideBelow], c.className)}>
                  {c.header}
                </th>
              ))}
              {rowMenu && <th className="w-12 px-2 py-2.5" />}
            </tr>
          </thead>
          <tbody ref={bodyRef}>
            {rows.map((row, i) => {
              const key = rowKey(row);
              const isSelected = selected?.has(key) ?? false;
              const isCursor = i === cursor;
              const drag = dragData?.(row) ?? null;
              return (
                <tr
                  key={key}
                  draggable={drag !== null}
                  onDragStart={(e) => {
                    if (drag === null) return;
                    e.dataTransfer.setData("text/plain", drag);
                    e.dataTransfer.effectAllowed = "move";
                  }}
                  onClick={() => {
                    setCursor(i);
                    onOpen?.(row);
                  }}
                  onMouseEnter={() => setCursor(i)}
                  className={cn(
                    "group border-t border-border transition-colors",
                    onOpen && "cursor-pointer",
                    isSelected ? "bg-surface-active/60" : isCursor ? "bg-surface-hover" : "hover:bg-surface-hover",
                    rowClassName?.(row),
                  )}
                >
                  {selectable && (
                    <td className="hidden w-10 px-3 align-middle sm:table-cell">
                      <span className={cn("flex items-center transition-opacity", isSelected || someSelected ? "opacity-100" : "opacity-0 group-hover:opacity-100 focus-within:opacity-100")}>
                        <Checkbox label={`Select ${key}`} checked={isSelected} onCheckedChange={(on) => toggle(key, on)} />
                      </span>
                    </td>
                  )}
                  {card ? (
                    <>
                      <td className={cn("px-3 sm:hidden", dense ? "py-2" : "py-3")} colSpan={1}>
                        {card(row)}
                      </td>
                      {columns.map((c) => (
                        <td key={c.key} className={cn("px-3 align-middle", dense ? "py-2" : "py-3.5", hidden[c.hideBelow ?? "sm"], c.className)}>
                          {c.render(row)}
                        </td>
                      ))}
                    </>
                  ) : (
                    columns.map((c) => (
                      <td key={c.key} className={cn("px-3 align-middle", dense ? "py-2" : "py-3.5", c.hideBelow && hidden[c.hideBelow], c.className)}>
                        {c.render(row)}
                      </td>
                    ))
                  )}
                  {rowMenu && (
                    <td className="w-12 px-2 text-right align-middle">
                      <Menu>
                        <MenuTrigger asChild>
                          <button
                            aria-label="Row actions"
                            onClick={(e) => e.stopPropagation()}
                            className="rounded-md p-1.5 text-muted opacity-60 hover:bg-surface-active hover:text-fg group-hover:opacity-100 data-[state=open]:opacity-100"
                          >
                            <MoreHorizontal className="size-4" />
                          </button>
                        </MenuTrigger>
                        <MenuContent>{rowMenu(row)}</MenuContent>
                      </Menu>
                    </td>
                  )}
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </div>
  );
}

const pageSizes = [20, 40, 100, 200];

export function Pagination({ start, limit, total, onChange }: { start: number; limit: number; total: number; onChange: (start: number, limit: number) => void }) {
  const page = Math.floor(start / limit) + 1;
  const pages = Math.max(1, Math.ceil(total / limit));
  const first = total === 0 ? 0 : start + 1;
  const last = Math.min(total, start + limit);
  return (
    <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-2 text-[13px] text-muted">
      <span>
        Page {page} – {first}
        {first !== last ? `–${last}` : ""} of {total.toLocaleString()} ·
      </span>
      <Menu>
        <MenuTrigger className="inline-flex items-center gap-1 rounded-md px-1 hover:text-fg">
          {limit} items <ChevronDown className="size-3.5" />
        </MenuTrigger>
        <MenuContent align="start">
          {pageSizes.map((n) => (
            <button key={n} onClick={() => onChange(0, n)} className={cn("block w-full rounded-md px-2.5 py-1.5 text-left text-[13.5px] hover:bg-surface-hover", n === limit && "text-fg", n !== limit && "text-muted")}>
              {n} per page
            </button>
          ))}
        </MenuContent>
      </Menu>
      {pages > 1 && (
        <span className="ml-auto flex items-center gap-1">
          <button disabled={page <= 1} onClick={() => onChange(Math.max(0, start - limit), limit)} className="rounded-md border border-border px-2 py-1 hover:bg-surface-hover disabled:opacity-40">
            Previous
          </button>
          <button disabled={page >= pages} onClick={() => onChange(start + limit, limit)} className="rounded-md border border-border px-2 py-1 hover:bg-surface-hover disabled:opacity-40">
            Next
          </button>
        </span>
      )}
    </div>
  );
}

/** Square rounded avatar tile with initials or an icon (design §11.1 "row identity"). */
export function IconTile({ text, icon, seed, unread, className, size = "md" }: { text?: string; icon?: ReactNode; seed?: string; unread?: boolean; className?: string; size?: "sm" | "md" }) {
  const h = hueOf(seed ?? text ?? "");
  return (
    <span className={cn("relative inline-flex shrink-0 items-center justify-center rounded-md border border-white/10 font-semibold text-white", size === "md" ? "size-10 text-[14px]" : "size-7 text-[11px]", className)} style={{ background: `linear-gradient(135deg, hsl(${h} 40% 30%), hsl(${h} 45% 20%))` }}>
      {icon ?? text}
      {unread && <span className="absolute -right-1 -top-1 size-2.5 rounded-full border-2 border-bg bg-accent" />}
    </span>
  );
}

export function EmptyState({ icon, title, description, children, className }: { icon?: ReactNode; title: ReactNode; description?: ReactNode; children?: ReactNode; className?: string }) {
  return (
    <div className={cn("flex flex-col items-center px-6 py-14 text-center", className)}>
      <span className="mb-4 flex size-12 items-center justify-center rounded-lg border border-border bg-surface text-muted [&>svg]:size-6">{icon ?? <Inbox />}</span>
      <h3 className="text-[16px] font-semibold">{title}</h3>
      {description && <p className="mt-1.5 max-w-md text-[14px] text-muted">{description}</p>}
      {children && <div className="mt-6 w-full max-w-2xl text-left">{children}</div>}
    </div>
  );
}

/** "N new messages, click to show" pill; lists stay still until the user asks for the new rows. */
export function NewMessagesPill() {
  const { pendingNew, clearPendingNew } = useApp();
  if (pendingNew === 0) return null;
  return (
    <div className="pointer-events-none sticky top-2 z-10 flex justify-center">
      <button onClick={clearPendingNew} className="pointer-events-auto inline-flex items-center gap-2 rounded-full border border-accent-border bg-accent-soft px-3.5 py-1.5 text-[13px] font-medium text-accent shadow-lg shadow-black/30 hover:brightness-110">
        <ArrowDown className="size-3.5" />
        {pendingNew} new {pendingNew === 1 ? "message" : "messages"}, click to show
      </button>
    </div>
  );
}

/** Search input with "/" focus and a clear button. Debounced through onChange. */
export function SearchInput({ value, onChange, placeholder, className, autoFocus }: { value: string; onChange: (v: string) => void; placeholder?: string; className?: string; autoFocus?: boolean }) {
  const ref = useRef<HTMLInputElement>(null);
  const [draft, setDraft] = useState(value);
  useEffect(() => setDraft(value), [value]);
  useEffect(() => {
    if (draft === value) return;
    const t = setTimeout(() => onChange(draft), 250);
    return () => clearTimeout(t);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draft]);
  useHotkeys({ "/": () => ref.current?.focus() });
  return (
    <div className={cn("relative flex-1", className)}>
      <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-muted" />
      <input
        ref={ref}
        autoFocus={autoFocus}
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            setDraft("");
            onChange("");
            ref.current?.blur();
          }
          if (e.key === "Enter") onChange(draft);
        }}
        placeholder={placeholder ?? "Search…"}
        className="h-10 w-full rounded-md border border-border bg-surface pl-9 pr-16 text-[14px] text-fg placeholder:text-muted/70 outline-none focus:border-border-strong focus:ring-2 focus:ring-info/30"
      />
      <span className="pointer-events-none absolute right-3 top-1/2 flex -translate-y-1/2 items-center gap-1">
        {draft ? (
          <button aria-label="Clear search" onClick={() => (setDraft(""), onChange(""))} className="pointer-events-auto rounded p-0.5 text-muted hover:text-fg">
            <X className="size-4" />
          </button>
        ) : (
          <Kbd>/</Kbd>
        )}
      </span>
    </div>
  );
}

/** Keeps a small set of string values in the URL query string (design §11.4). */
export function useQueryState<K extends string>(defaults: Record<K, string>): [Record<K, string>, (patch: Partial<Record<K, string>>) => void] {
  const [params, setParams] = useSearchParams();
  const state = {} as Record<K, string>;
  for (const key of Object.keys(defaults) as K[]) state[key] = params.get(key) ?? defaults[key];
  const update = (patch: Partial<Record<K, string>>) => {
    const next = new URLSearchParams(params);
    for (const [key, value] of Object.entries(patch) as [K, string | undefined][]) {
      if (value === undefined || value === "" || value === defaults[key]) next.delete(key);
      else next.set(key, value);
    }
    setParams(next, { replace: true });
  };
  return [state, update];
}

/** Toolbar wrapper: search + filters on one line, bulk-action bar when rows are selected. */
export function Toolbar({ children, selection, className }: { children: ReactNode; selection?: { count: number; clear: () => void; actions: ReactNode }; className?: string }) {
  if (selection && selection.count > 0) {
    return (
      <div className={cn("mb-4 flex min-h-10 flex-wrap items-center gap-2 rounded-md border border-border-strong bg-surface px-3 py-1.5", className)}>
        <span className="text-[13.5px] font-medium">{selection.count} selected</span>
        <Tooltip content="Clear selection (Esc)">
          <button aria-label="Clear selection" onClick={selection.clear} className="rounded p-1 text-muted hover:text-fg">
            <X className="size-4" />
          </button>
        </Tooltip>
        <span className="mx-1 h-5 w-px bg-border" />
        {selection.actions}
      </div>
    );
  }
  return <div className={cn("mb-4 flex flex-wrap items-center gap-2", className)}>{children}</div>;
}

export function ColumnHint({ children }: { children: ReactNode }) {
  return <span className="text-[12px] text-muted">{children}</span>;
}
