import { useQuery } from "@tanstack/react-query";
import { Command } from "cmdk";
import { Mail, Moon, Search, Trash2, Users } from "lucide-react";
import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { api } from "@/lib/api";
import { useApp } from "@/lib/app-state";
import { navigation } from "./layout";

export function CommandPalette() {
  const { paletteOpen, setPaletteOpen, setTheme, theme, setAccount, toast } = useApp();
  const navigate = useNavigate();
  const [search, setSearch] = useState("");
  const accounts = useQuery({ queryKey: ["accounts"], queryFn: api.accounts, enabled: paletteOpen });
  const messages = useQuery({
    queryKey: ["messages", "palette", search],
    queryFn: () => api.messages({ query: search, limit: 6 }),
    enabled: paletteOpen && search.length > 1,
  });

  useEffect(() => {
    if (!paletteOpen) setSearch("");
  }, [paletteOpen]);

  if (!paletteOpen) return null;

  const run = (action: () => void) => {
    setPaletteOpen(false);
    action();
  };

  return (
    <div className="fixed inset-0 z-50 flex items-start justify-center bg-black/60 p-4 pt-[12vh]" onClick={() => setPaletteOpen(false)}>
      <Command
        label="Command palette"
        className="w-full max-w-xl overflow-hidden rounded-xl border border-border bg-bg-subtle shadow-2xl shadow-black/60"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => e.key === "Escape" && setPaletteOpen(false)}
        shouldFilter={true}
      >
        <div className="flex items-center gap-2 border-b border-border px-4">
          <Search className="size-4 text-muted" />
          <Command.Input autoFocus value={search} onValueChange={setSearch} placeholder="Search mail, jump to a page, run a command…" className="h-12 flex-1 bg-transparent text-[15px] outline-none placeholder:text-muted" />
        </div>
        <Command.List className="max-h-[50vh] overflow-y-auto p-2 [&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:py-1.5 [&_[cmdk-group-heading]]:text-[12px] [&_[cmdk-group-heading]]:text-muted">
          <Command.Empty className="px-3 py-6 text-center text-[14px] text-muted">No results.</Command.Empty>
          {search.length > 1 && (
            <Command.Group heading="Search">
              <Item onSelect={() => run(() => navigate(`/emails?query=${encodeURIComponent(search)}`))} value={`search-all ${search}`}>
                <Search className="size-4" /> Search emails for “{search}”
              </Item>
              {messages.data?.messages.map((m) => (
                <Item key={m.id} value={`msg ${m.id} ${m.subject} ${m.from?.email}`} onSelect={() => run(() => navigate(`/emails/${m.id}`))}>
                  <Mail className="size-4" />
                  <span className="truncate">{m.subject || "(no subject)"}</span>
                  <span className="ml-auto truncate text-[12px] text-muted">{m.from?.email}</span>
                </Item>
              ))}
            </Command.Group>
          )}
          <Command.Group heading="Go to">
            {navigation.map(({ to, label, icon: Icon, shortcut }) => (
              <Item key={to} value={`go ${label}`} onSelect={() => run(() => navigate(to))}>
                <Icon className="size-4" /> {label}
                <span className="ml-auto font-mono text-[11px] text-muted">{shortcut}</span>
              </Item>
            ))}
          </Command.Group>
          <Command.Group heading="Commands">
            <Item value="theme toggle" onSelect={() => run(() => setTheme(theme === "dark" ? "light" : "dark"))}>
              <Moon className="size-4" /> Toggle theme
            </Item>
            <Item value="mark all read" onSelect={() => run(() => void api.setRead(null, true).then(() => toast({ title: "All messages marked read", tone: "success" })))}>
              <Mail className="size-4" /> Mark all emails read
            </Item>
            <Item
              value="delete all messages"
              onSelect={() =>
                run(() => {
                  if (confirm("Delete all captured messages?")) void api.deleteMessages(null).then(() => toast({ title: "All messages deleted", tone: "success" }));
                })
              }
            >
              <Trash2 className="size-4" /> Delete all emails
            </Item>
          </Command.Group>
          {(accounts.data?.length ?? 0) > 0 && (
            <Command.Group heading="Switch account">
              <Item value="account all catch-all" onSelect={() => run(() => setAccount(null))}>
                <Mail className="size-4" /> All mail (catch-all)
              </Item>
              {accounts.data?.slice(0, 20).map((a) => (
                <Item key={a.address} value={`account ${a.address}`} onSelect={() => run(() => (setAccount(a.address), navigate("/mailboxes")))}>
                  <Users className="size-4" /> {a.address}
                </Item>
              ))}
            </Command.Group>
          )}
        </Command.List>
      </Command>
    </div>
  );
}

function Item({ children, onSelect, value }: { children: React.ReactNode; onSelect: () => void; value: string }) {
  return (
    <Command.Item
      value={value}
      onSelect={onSelect}
      className="flex cursor-pointer items-center gap-3 rounded-md px-3 py-2 text-[14px] text-fg/90 data-[selected=true]:bg-surface-hover data-[selected=true]:text-fg [&>svg]:text-muted"
    >
      {children}
    </Command.Item>
  );
}
