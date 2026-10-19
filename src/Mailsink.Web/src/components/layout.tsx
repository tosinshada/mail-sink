import { useQuery } from "@tanstack/react-query";
import {
  Activity,
  BarChart3,
  ChevronDown,
  CircleHelp,
  Code2,
  FileText,
  Flame,
  Folder,
  Keyboard,
  Mail,
  Menu as MenuIcon,
  MessageSquare,
  Monitor,
  Moon,
  PenSquare,
  Settings,
  Sun,
  Users,
  X,
} from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import { NavLink, Outlet, useLocation, useNavigate } from "react-router-dom";
import { api, urls } from "@/lib/api";
import { useApp, useHotkeys } from "@/lib/app-state";
import { cn, hue } from "@/lib/format";
import { CommandPalette } from "./command-palette";
import { ApiSnippetButton, type Snippet } from "./snippets";
import { Kbd, Menu, MenuContent, MenuItem, MenuSeparator, MenuTrigger, Tooltip } from "./ui";

export const navigation = [
  { to: "/emails", label: "Emails", icon: Mail, shortcut: "g e" },
  { to: "/mailboxes", label: "Mailboxes", icon: Folder, shortcut: "g m" },
  { to: "/sms", label: "SMS", icon: MessageSquare, shortcut: "g p" },
  { to: "/compose", label: "Compose", icon: PenSquare, shortcut: "c" },
  { to: "/accounts", label: "Accounts", icon: Users, shortcut: "g a" },
  { to: "/templates", label: "Templates", icon: FileText, shortcut: "g t" },
  { to: "/chaos", label: "Chaos", icon: Flame, shortcut: "g c" },
  { to: "/logs", label: "Logs", icon: Activity, shortcut: "g l" },
  { to: "/metrics", label: "Metrics", icon: BarChart3, shortcut: "g x" },
  { to: "/settings", label: "Settings", icon: Settings, shortcut: "g s" },
] as const;

export function AppShell() {
  const navigate = useNavigate();
  const location = useLocation();
  const { setPaletteOpen, setShortcutsOpen } = useApp();
  const [drawer, setDrawer] = useState(false);

  useEffect(() => setDrawer(false), [location.pathname]);

  useHotkeys({
    "mod+k": () => setPaletteOpen(true),
    "?": () => setShortcutsOpen(true),
    c: () => navigate("/compose"),
    "g e": () => navigate("/emails"),
    "g m": () => navigate("/mailboxes"),
    "g p": () => navigate("/sms"),
    "g a": () => navigate("/accounts"),
    "g t": () => navigate("/templates"),
    "g c": () => navigate("/chaos"),
    "g l": () => navigate("/logs"),
    "g x": () => navigate("/metrics"),
    "g s": () => navigate("/settings"),
  });

  return (
    <div className="flex h-full flex-col">
      <div className="flex min-h-0 flex-1">
        <Sidebar className="hidden md:flex" />
        {drawer && (
          <div className="fixed inset-0 z-40 md:hidden">
            <div className="absolute inset-0 bg-black/60" onClick={() => setDrawer(false)} />
            <Sidebar className="relative z-10 flex h-full w-[280px]" onClose={() => setDrawer(false)} />
          </div>
        )}
        <main className="min-w-0 flex-1 overflow-y-auto">
          <div className="sticky top-0 z-20 flex items-center gap-3 border-b border-border bg-bg/90 px-4 py-2.5 backdrop-blur md:hidden">
            <button aria-label="Open navigation" onClick={() => setDrawer(true)} className="rounded-md p-1.5 text-muted hover:bg-surface-hover">
              <MenuIcon className="size-5" />
            </button>
            <Logo />
          </div>
          <div className="mx-auto w-full max-w-[1380px] px-4 py-6 sm:px-8 lg:px-[6.5%] lg:py-9">
            <Outlet />
          </div>
        </main>
      </div>
      <CommandBar />
      <CommandPalette />
      <ShortcutSheet />
      <Toaster />
    </div>
  );
}

function Logo() {
  return (
    <span className="flex items-center gap-2 font-semibold tracking-tight">
      <span className="flex size-7 items-center justify-center rounded-md border border-accent-border bg-accent-soft text-accent">
        <Mail className="size-4" />
      </span>
      Mailsink
    </span>
  );
}

function Sidebar({ className, onClose }: { className?: string; onClose?: () => void }) {
  const { stats } = useApp();
  return (
    <aside className={cn("w-[260px] shrink-0 flex-col border-r border-border bg-bg-subtle lg:w-[300px]", className)}>
      <div className="flex items-center gap-2 p-4 pb-3">
        <AccountSwitcher />
        {onClose && (
          <button aria-label="Close navigation" onClick={onClose} className="rounded-md p-1.5 text-muted hover:bg-surface-hover">
            <X className="size-5" />
          </button>
        )}
      </div>
      <nav className="flex-1 space-y-0.5 overflow-y-auto px-3 pb-3">
        {navigation.map(({ to, label, icon: Icon }) => (
          <NavLink
            key={to}
            to={to}
            className={({ isActive }) =>
              cn(
                "flex items-center gap-3 rounded-lg px-3 py-2.5 text-[15px] transition-colors",
                isActive ? "bg-surface-hover text-fg" : "text-fg/80 hover:bg-surface hover:text-fg",
              )
            }
          >
            <Icon className="size-[18px] text-fg/85" strokeWidth={1.75} />
            <span className="flex-1">{label}</span>
            {to === "/emails" && stats && stats.unread > 0 && (
              <span className="rounded-full bg-surface-active px-2 py-0.5 font-mono text-[11px] text-muted">{stats.unread}</span>
            )}
          </NavLink>
        ))}
      </nav>
      <SidebarFooter />
    </aside>
  );
}

function AccountSwitcher() {
  const { account, setAccount } = useApp();
  const navigate = useNavigate();
  const accounts = useQuery({ queryKey: ["accounts"], queryFn: api.accounts });
  const label = account ?? "All mail";
  const tileHue = hue(account ?? "all");

  return (
    <Menu>
      <MenuTrigger className="flex min-w-0 flex-1 items-center gap-2.5 rounded-lg px-2 py-1.5 text-left hover:bg-surface-hover">
        <span
          className="flex size-7 shrink-0 items-center justify-center rounded-md text-[13px] font-semibold text-white"
          style={{ background: account ? `hsl(${tileHue} 45% 38%)` : "var(--accent-soft)", color: account ? undefined : "var(--accent)" }}
        >
          {account ? account[0].toUpperCase() : <Mail className="size-4" />}
        </span>
        <span className="min-w-0 flex-1">
          <span className="block truncate text-[15px] font-medium">{label}</span>
          {!account && <span className="block text-[12px] text-muted">catch-all</span>}
        </span>
        <ChevronDown className="size-4 shrink-0 text-muted" />
      </MenuTrigger>
      <MenuContent align="start">
        <MenuItem icon={<Mail />} onSelect={() => setAccount(null)}>
          All mail (catch-all)
        </MenuItem>
        {(accounts.data?.length ?? 0) > 0 && <MenuSeparator />}
        <div className="max-h-72 overflow-y-auto">
          {accounts.data?.map((a) => (
            <MenuItem
              key={a.address}
              icon={<span className="block size-4 rounded" style={{ background: `hsl(${hue(a.address)} 45% 38%)` }} />}
              onSelect={() => {
                setAccount(a.address);
                navigate("/mailboxes");
              }}
            >
              <span className="block max-w-[220px] truncate">{a.address}</span>
            </MenuItem>
          ))}
        </div>
        <MenuSeparator />
        <MenuItem icon={<Users />} onSelect={() => navigate("/accounts")}>
          Manage accounts
        </MenuItem>
      </MenuContent>
    </Menu>
  );
}

function SidebarFooter() {
  const { theme, setTheme, setShortcutsOpen } = useApp();
  const next = theme === "dark" ? "light" : theme === "light" ? "system" : "dark";
  const ThemeIcon = theme === "dark" ? Moon : theme === "light" ? Sun : Monitor;
  return (
    <div className="flex items-center justify-between px-4 py-3">
      <Tooltip content={`Theme: ${theme} (switch to ${next})`}>
        <button aria-label="Toggle theme" onClick={() => setTheme(next)} className="rounded-md p-2 text-muted hover:bg-surface-hover hover:text-fg">
          <ThemeIcon className="size-[18px]" />
        </button>
      </Tooltip>
      <div className="flex gap-1">
        <Tooltip content="Keyboard shortcuts (?)">
          <button aria-label="Keyboard shortcuts" onClick={() => setShortcutsOpen(true)} className="rounded-md p-2 text-muted hover:bg-surface-hover hover:text-fg">
            <Keyboard className="size-[18px]" />
          </button>
        </Tooltip>
        <Tooltip content="API reference">
          <a aria-label="API reference" href={urls.docs()} target="_blank" rel="noreferrer" className="rounded-md p-2 text-muted hover:bg-surface-hover hover:text-fg">
            <CircleHelp className="size-[18px]" />
          </a>
        </Tooltip>
      </div>
    </div>
  );
}

/** Bottom bar: live listener status on the left, command palette entry on the right. */
function CommandBar() {
  const { connected, setPaletteOpen } = useApp();
  const info = useQuery({ queryKey: ["info"], queryFn: api.info, refetchInterval: 30_000 });
  const endpoints = info.data?.endpoints ?? [];
  return (
    <footer className="flex h-10 shrink-0 items-center justify-between gap-4 border-t border-border bg-bg-subtle px-4 text-[13px] text-muted">
      <div className="flex min-w-0 items-center gap-4 overflow-x-auto">
        <Tooltip content={connected ? "Live updates connected" : "Live updates disconnected; reconnecting…"}>
          <span className="flex shrink-0 items-center gap-1.5">
            <span className={cn("size-2 rounded-full", connected ? "bg-accent" : "bg-danger")} />
            {connected ? "live" : "offline"}
          </span>
        </Tooltip>
        {endpoints.map((e) => (
          <span key={e.name} className="flex shrink-0 items-center gap-1.5 font-mono text-[12px]">
            <span className={cn("size-1.5 rounded-full", info.isError ? "bg-danger" : "bg-accent")} />
            {e.name.toUpperCase()} {e.port}
          </span>
        ))}
        <a href={urls.docs()} target="_blank" rel="noreferrer" className="hidden shrink-0 font-mono text-[12px] hover:text-fg sm:inline">
          /api/docs
        </a>
      </div>
      <button onClick={() => setPaletteOpen(true)} className="flex shrink-0 items-center gap-2 rounded-md px-2 py-1 hover:bg-surface-hover hover:text-fg">
        <Kbd>Ctrl K</Kbd>
        <span className="hidden sm:inline">Search or run a command</span>
      </button>
    </footer>
  );
}

export function PageHeader({
  title,
  description,
  actions,
  snippet,
  breadcrumb,
}: {
  title: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  snippet?: Snippet;
  breadcrumb?: ReactNode;
}) {
  return (
    <header className="mb-7">
      {breadcrumb && <div className="mb-3 text-[13px] text-muted">{breadcrumb}</div>}
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="min-w-0">
          <h1 className="break-words text-[28px] font-bold leading-tight tracking-tight sm:text-[32px]">{title}</h1>
          {description && <p className="mt-1.5 text-[14px] text-muted">{description}</p>}
        </div>
        <div className="flex shrink-0 items-center gap-2">
          {actions}
          {snippet && <ApiSnippetButton snippet={snippet} />}
        </div>
      </div>
    </header>
  );
}

function ShortcutSheet() {
  const { shortcutsOpen, setShortcutsOpen } = useApp();
  const rows: [string[][], string][] = [
    [[["Ctrl", "K"]], "Command palette"],
    [[["/"]], "Focus search"],
    [[["j"], ["k"]], "Next / previous row"],
    [[["Enter"]], "Open row"],
    [[["e"]], "Toggle read"],
    [[["#"], ["Del"]], "Delete"],
    [[["x"]], "Select row"],
    [[["c"]], "Compose"],
    [[["g", "e"], ["g", "m"], ["g", "l"]], "Go to Emails / Mailboxes / Logs"],
    [[["g", "a"], ["g", "t"], ["g", "c"]], "Go to Accounts / Templates / Chaos"],
    [[["?"]], "This sheet"],
  ];
  return (
    <DialogShell open={shortcutsOpen} onClose={() => setShortcutsOpen(false)} title="Keyboard shortcuts">
      <dl className="grid grid-cols-[auto_1fr] gap-x-6 gap-y-2.5 text-[14px]">
        {rows.map(([alternatives, label]) => (
          <div key={label} className="contents">
            <dt className="flex flex-wrap items-center gap-1">
              {alternatives.map((keys, i) => (
                <span key={i} className="flex items-center gap-1">
                  {i > 0 && <span className="px-0.5 text-muted">/</span>}
                  {keys.map((k) => (
                    <Kbd key={k}>{k}</Kbd>
                  ))}
                </span>
              ))}
            </dt>
            <dd className="text-muted">{label}</dd>
          </div>
        ))}
      </dl>
    </DialogShell>
  );
}

function DialogShell({ open, onClose, title, children }: { open: boolean; onClose: () => void; title: string; children: ReactNode }) {
  useHotkeys({ Escape: () => open && onClose() }, [open]);
  if (!open) return null;
  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center bg-black/60 p-4 pt-[12vh]" onClick={onClose}>
      <div role="dialog" aria-label={title} className="w-full max-w-md rounded-lg border border-border bg-bg-subtle p-5 shadow-2xl" onClick={(e) => e.stopPropagation()}>
        <div className="mb-4 flex items-center justify-between">
          <h2 className="text-[16px] font-semibold">{title}</h2>
          <button aria-label="Close" onClick={onClose} className="rounded-md p-1 text-muted hover:bg-surface-hover">
            <X className="size-4" />
          </button>
        </div>
        {children}
      </div>
    </div>
  );
}

function Toaster() {
  const { toasts, dismissToast } = useApp();
  return (
    <div className="pointer-events-none fixed bottom-14 right-4 z-50 flex w-[360px] max-w-[calc(100vw-32px)] flex-col gap-2" aria-live="polite">
      {toasts.map((t) => (
        <div
          key={t.id}
          className={cn(
            "pointer-events-auto flex items-start gap-3 rounded-lg border bg-bg-subtle p-3.5 shadow-xl shadow-black/40",
            t.tone === "danger" ? "border-danger/50" : t.tone === "success" ? "border-accent-border" : "border-border",
          )}
        >
          <div className="min-w-0 flex-1">
            <p className="text-[14px] font-medium">{t.title}</p>
            {t.description && <p className="mt-0.5 break-words text-[13px] text-muted">{t.description}</p>}
          </div>
          {t.action && (
            <button
              className="shrink-0 rounded-md border border-border px-2 py-1 text-[13px] hover:bg-surface-hover"
              onClick={() => {
                t.action!.onClick();
                dismissToast(t.id);
              }}
            >
              {t.action.label}
            </button>
          )}
          <button aria-label="Dismiss" className="shrink-0 text-muted hover:text-fg" onClick={() => dismissToast(t.id)}>
            <X className="size-4" />
          </button>
        </div>
      ))}
    </div>
  );
}

export function CodeIcon() {
  return <Code2 className="size-4" />;
}
