import * as signalR from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { urls, type MessageSummary } from "./api";

export type Theme = "dark" | "light" | "system";

export interface Toast {
  id: number;
  title: string;
  description?: string;
  tone?: "neutral" | "success" | "danger";
  action?: { label: string; onClick: () => void };
}

interface AppState {
  theme: Theme;
  setTheme: (t: Theme) => void;
  account: string | null;
  setAccount: (a: string | null) => void;
  toasts: Toast[];
  toast: (t: Omit<Toast, "id">) => void;
  dismissToast: (id: number) => void;
  connected: boolean;
  stats: { total: number; unread: number } | null;
  pendingNew: number;
  clearPendingNew: () => void;
  freshIds: Set<string>;
  paletteOpen: boolean;
  setPaletteOpen: (open: boolean) => void;
  shortcutsOpen: boolean;
  setShortcutsOpen: (open: boolean) => void;
  notifications: boolean;
  setNotifications: (on: boolean) => void;
}

const AppContext = createContext<AppState | null>(null);

function read(key: string, fallback: string): string {
  try {
    return localStorage.getItem(key) ?? fallback;
  } catch {
    return fallback;
  }
}

function write(key: string, value: string | null) {
  try {
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, value);
  } catch {
    /* storage unavailable */
  }
}

function applyTheme(theme: Theme) {
  const light = theme === "light" || (theme === "system" && window.matchMedia("(prefers-color-scheme: light)").matches);
  document.documentElement.dataset.theme = light ? "light" : "dark";
}

export function AppStateProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [theme, setThemeState] = useState<Theme>(() => read("mailsink.theme", "dark") as Theme);
  const [account, setAccountState] = useState<string | null>(() => read("mailsink.account", "") || null);
  const [toasts, setToasts] = useState<Toast[]>([]);
  const [connected, setConnected] = useState(false);
  const [stats, setStats] = useState<{ total: number; unread: number } | null>(null);
  const [pendingNew, setPendingNew] = useState(0);
  const [freshIds, setFreshIds] = useState<Set<string>>(new Set());
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  const [notifications, setNotificationsState] = useState(() => read("mailsink.notifications", "0") === "1");
  const nextToast = useRef(1);
  const accountRef = useRef(account);
  const notificationsRef = useRef(notifications);
  notificationsRef.current = notifications;

  const setTheme = useCallback((t: Theme) => {
    setThemeState(t);
    write("mailsink.theme", t);
    applyTheme(t);
  }, []);

  useEffect(() => {
    applyTheme(theme);
    if (theme !== "system") return;
    const media = window.matchMedia("(prefers-color-scheme: light)");
    const listener = () => applyTheme("system");
    media.addEventListener("change", listener);
    return () => media.removeEventListener("change", listener);
  }, [theme]);

  const dismissToast = useCallback((id: number) => setToasts((all) => all.filter((t) => t.id !== id)), []);
  const toast = useCallback(
    (t: Omit<Toast, "id">) => {
      const id = nextToast.current++;
      setToasts((all) => [...all.slice(-3), { ...t, id }]);
      setTimeout(() => dismissToast(id), t.action ? 8000 : 4500);
    },
    [dismissToast],
  );

  const setNotifications = useCallback((on: boolean) => {
    if (on && "Notification" in window && Notification.permission !== "granted") {
      void Notification.requestPermission().then((p) => {
        const granted = p === "granted";
        setNotificationsState(granted);
        write("mailsink.notifications", granted ? "1" : "0");
      });
      return;
    }

    setNotificationsState(on);
    write("mailsink.notifications", on ? "1" : "0");
  }, []);

  const connectionRef = useRef<signalR.HubConnection | null>(null);

  const setAccount = useCallback((a: string | null) => {
    const previous = accountRef.current;
    accountRef.current = a;
    setAccountState(a);
    write("mailsink.account", a);
    const connection = connectionRef.current;
    if (connection?.state === signalR.HubConnectionState.Connected) {
      if (previous) void connection.invoke("UnsubscribeAccount", previous).catch(() => {});
      if (a) void connection.invoke("SubscribeAccount", a).catch(() => {});
    }
  }, []);

  // One SignalR connection for the whole app; events invalidate cached queries.
  useEffect(() => {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(urls.hub())
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => Math.min(10_000, 500 * 2 ** ctx.previousRetryCount) })
      .configureLogging(signalR.LogLevel.Warning)
      .build();
    connectionRef.current = connection;

    connection.on("MessageReceived", (message: MessageSummary) => {
      setPendingNew((n) => n + 1);
      setFreshIds((s) => new Set(s).add(message.id));
      void queryClient.invalidateQueries({ queryKey: ["tags"] });
      if (notificationsRef.current && "Notification" in window && Notification.permission === "granted" && document.hidden) {
        new Notification(message.subject || "(no subject)", { body: `From ${message.from?.email ?? "unknown"}`, tag: message.id });
      }
    });
    connection.on("MessagesBulkReceived", (count: number) => setPendingNew((n) => n + count));
    connection.on("MessagesDeleted", () => void queryClient.invalidateQueries({ queryKey: ["messages"] }));
    connection.on("MessageUpdated", () => {
      void queryClient.invalidateQueries({ queryKey: ["messages"] });
      void queryClient.invalidateQueries({ queryKey: ["message"] });
    });
    connection.on("Stats", (s: { total: number; unread: number }) => setStats(s));
    connection.on("SmsReceived", () => void queryClient.invalidateQueries({ queryKey: ["sms"] }));
    connection.on("SmsChanged", () => void queryClient.invalidateQueries({ queryKey: ["sms"] }));
    connection.on("AccountsChanged", () => void queryClient.invalidateQueries({ queryKey: ["accounts"] }));
    connection.on("MailboxChanged", (e: { owner: string }) => {
      void queryClient.invalidateQueries({ queryKey: ["mailboxes", e.owner] });
      void queryClient.invalidateQueries({ queryKey: ["mailbox-messages", e.owner] });
    });

    const onConnected = () => {
      setConnected(true);
      if (accountRef.current) void connection.invoke("SubscribeAccount", accountRef.current).catch(() => {});
      void queryClient.invalidateQueries();
    };
    connection.onreconnecting(() => setConnected(false));
    connection.onreconnected(onConnected);
    connection.onclose(() => setConnected(false));

    let stopped = false;
    const start = async () => {
      while (!stopped) {
        try {
          await connection.start();
          onConnected();
          return;
        } catch {
          await new Promise((r) => setTimeout(r, 3000));
        }
      }
    };
    void start();
    return () => {
      stopped = true;
      void connection.stop();
    };
  }, [queryClient]);

  useEffect(() => {
    document.title = stats && stats.unread > 0 ? `(${stats.unread}) Mailsink` : "Mailsink";
  }, [stats]);

  const clearPendingNew = useCallback(() => {
    setPendingNew(0);
    void queryClient.invalidateQueries({ queryKey: ["messages"] });
    setTimeout(() => setFreshIds(new Set()), 2000);
  }, [queryClient]);

  const value = useMemo<AppState>(
    () => ({
      theme,
      setTheme,
      account,
      setAccount,
      toasts,
      toast,
      dismissToast,
      connected,
      stats,
      pendingNew,
      clearPendingNew,
      freshIds,
      paletteOpen,
      setPaletteOpen,
      shortcutsOpen,
      setShortcutsOpen,
      notifications,
      setNotifications,
    }),
    [theme, setTheme, account, setAccount, toasts, toast, dismissToast, connected, stats, pendingNew, clearPendingNew, freshIds, paletteOpen, shortcutsOpen, notifications, setNotifications],
  );

  return <AppContext.Provider value={value}>{children}</AppContext.Provider>;
}

export function useApp(): AppState {
  const context = useContext(AppContext);
  if (!context) throw new Error("useApp outside AppStateProvider");
  return context;
}

/** Global keyboard shortcuts. Ignored while typing in inputs. Supports two-key "g e" chords. */
export function useHotkeys(bindings: Record<string, (e: KeyboardEvent) => void>, deps: unknown[] = []) {
  const pending = useRef<{ key: string; at: number } | null>(null);
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      const target = e.target as HTMLElement | null;
      const typing = target && (target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName));
      const combo = `${e.ctrlKey || e.metaKey ? "mod+" : ""}${e.key.toLowerCase()}`;
      if (typing && combo !== "mod+k" && e.key !== "Escape") return;
      if (e.altKey) return;

      if (pending.current && Date.now() - pending.current.at < 1200) {
        const chord = `${pending.current.key} ${e.key.toLowerCase()}`;
        pending.current = null;
        if (bindings[chord]) {
          e.preventDefault();
          bindings[chord](e);
          return;
        }
      }

      if (Object.keys(bindings).some((k) => k.startsWith(`${e.key.toLowerCase()} `)) && !e.ctrlKey && !e.metaKey) {
        pending.current = { key: e.key.toLowerCase(), at: Date.now() };
        return;
      }

      const binding = bindings[combo] ?? bindings[e.key];
      if (binding) {
        e.preventDefault();
        binding(e);
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
}
