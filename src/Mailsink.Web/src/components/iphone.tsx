import { BatteryFull, ChevronLeft, Mic, Plus, Signal, Video, Wifi } from "lucide-react";
import { Fragment, useEffect, useRef, useState, type ReactNode } from "react";
import type { SmsMessage, SmsSender } from "@/lib/api";
import { cn, hue } from "@/lib/format";

/** Formats North American numbers for display ("+15551234567" → "+1 (555) 123-4567"); others are shown as stored. */
export function formatPhone(number: string): string {
  const plus = number.startsWith("+");
  const digits = plus ? number.slice(1) : number;
  if (plus && digits.startsWith("1") && digits.length === 11) return `+1 (${digits.slice(1, 4)}) ${digits.slice(4, 7)}-${digits.slice(7)}`;
  if (!plus && digits.length === 10) return `(${digits.slice(0, 3)}) ${digits.slice(3, 6)}-${digits.slice(6)}`;
  return number;
}

function useClock() {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), 30_000);
    return () => clearInterval(timer);
  }, []);
  return now.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" }).replace(/\s?[AP]M$/i, "");
}

/** iMessage-style separator label: "Today 9:41 AM", "Yesterday 9:41 AM", "Mon 9 Oct 9:41 AM". */
function separatorLabel(iso: string): string {
  const date = new Date(iso);
  const time = date.toLocaleTimeString([], { hour: "numeric", minute: "2-digit" });
  const startOfToday = new Date();
  startOfToday.setHours(0, 0, 0, 0);
  const diffDays = Math.floor((startOfToday.getTime() - new Date(date).setHours(0, 0, 0, 0)) / 86_400_000);
  if (diffDays <= 0) return `Today ${time}`;
  if (diffDays === 1) return `Yesterday ${time}`;
  return `${date.toLocaleDateString([], { weekday: "short", day: "numeric", month: "short" })} ${time}`;
}

const urlPattern = /(https?:\/\/[^\s]+)/g;

function Linkified({ text }: { text: string }) {
  return (
    <>
      {text.split(urlPattern).map((part, i) =>
        i % 2 === 1 ? (
          <a key={i} href={part} target="_blank" rel="noreferrer noopener" className="underline underline-offset-2">
            {part}
          </a>
        ) : (
          <Fragment key={i}>{part}</Fragment>
        ),
      )}
    </>
  );
}

const SEPARATOR_GAP_MS = 60 * 60 * 1000;
const GROUP_GAP_MS = 60 * 1000;

/** A received iMessage-style bubble. The tail sits on the last bubble of a run. */
function Bubble({ message, tail }: { message: SmsMessage; tail: boolean }) {
  return (
    <div className={cn("relative max-w-[78%] self-start", tail ? "mb-2" : "mb-0.5")}>
      <div
        title={new Date(message.receivedAt).toLocaleString()}
        className="whitespace-pre-wrap break-words rounded-[18px] bg-[#e9e9eb] px-3 py-[7px] text-[14px] leading-[1.3] text-black dark:bg-[#262628] dark:text-white"
      >
        <Linkified text={message.body} />
      </div>
      {tail && (
        <svg aria-hidden viewBox="0 0 12 17" className="absolute -left-[5px] bottom-0 h-[17px] w-[12px] text-[#e9e9eb] dark:text-[#262628]">
          <path fill="currentColor" d="M12 0v11c0 3-1 6 0 6-4 0-8-1-11-4 3 0 5-2 5-5V0z" />
        </svg>
      )}
    </div>
  );
}

const senderKey = (s: SmsSender) => `${s.name}|${s.number}`;

/** Distinct senders in first-seen order. */
export function distinctSenders(messages: SmsMessage[]): SmsSender[] {
  const seen = new Map<string, SmsSender>();
  messages.forEach((m) => seen.has(senderKey(m.from)) || seen.set(senderKey(m.from), m.from));
  return [...seen.values()];
}

function initialsOf(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean);
  return ((words[0]?.[0] ?? "") + (words.length > 1 ? words[words.length - 1][0] : (words[0]?.[1] ?? ""))).toUpperCase() || "#";
}

/** A mock iPhone showing every message received by <paramref name="number"/> as an iMessage thread. */
export function IPhone({ number, messages, loading }: { number: string; messages: SmsMessage[]; loading?: boolean }) {
  const clock = useClock();
  const scrollRef = useRef<HTMLDivElement>(null);
  const senders = distinctSenders(messages);
  const single = senders.length === 1 ? senders[0] : null;
  const title = single ? single.name : senders.length > 1 ? `${senders.length} senders` : formatPhone(number);
  const avatarHue = hue(title);

  // Keep the newest message in view whenever one arrives.
  useEffect(() => {
    const el = scrollRef.current;
    if (el) el.scrollTo({ top: el.scrollHeight, behavior: "smooth" });
  }, [messages.length]);

  const rows: ReactNode[] = [];
  messages.forEach((m, i) => {
    const prev = messages[i - 1];
    const next = messages[i + 1];
    const at = new Date(m.receivedAt).getTime();
    if (!prev || at - new Date(prev.receivedAt).getTime() > SEPARATOR_GAP_MS) {
      rows.push(
        <div key={`sep-${m.id}`} className="my-2 text-center text-[11px] text-[#8e8e93]">
          {i === 0 && <div className="mb-0.5 font-medium">Text Message · SMS</div>}
          {separatorLabel(m.receivedAt)}
        </div>,
      );
    }
    if (!single && (!prev || senderKey(prev.from) !== senderKey(m.from))) {
      rows.push(
        <div key={`from-${m.id}`} className="ml-3 mt-1 truncate text-[11px] text-[#8e8e93]">
          {m.from.name} · {formatPhone(m.from.number)}
        </div>,
      );
    }
    const endsRun = !next || senderKey(next.from) !== senderKey(m.from) || new Date(next.receivedAt).getTime() - at > GROUP_GAP_MS;
    rows.push(<Bubble key={m.id} message={m} tail={endsRun} />);
  });

  return (
    <div className="relative mx-auto w-[300px] shrink-0">

      {/* Bezel */}
      <div className="rounded-[52px] bg-[#1c1c1e] p-[11px] shadow-xl shadow-black/30 ring-1 ring-[#3a3a3c]">
        {/* Screen */}
        <div className="relative flex h-[600px] flex-col overflow-hidden rounded-[42px] bg-white text-black dark:bg-black dark:text-white">
          {/* Status bar + Dynamic Island */}
          <div className="relative flex h-[46px] shrink-0 items-center justify-between px-7 pt-1 text-[14px] font-semibold">
            <span className="w-14">{clock}</span>
            <span className="absolute left-1/2 top-[10px] h-[26px] w-[88px] -translate-x-1/2 rounded-full bg-black" />
            <span className="flex w-14 items-center justify-end gap-1">
              <Signal className="size-3.5" strokeWidth={2.5} />
              <Wifi className="size-3.5" strokeWidth={2.5} />
              <BatteryFull className="size-[18px]" strokeWidth={2} />
            </span>
          </div>

          {/* Conversation header */}
          <div className="relative shrink-0 border-b border-black/10 bg-[#f6f6f6]/90 px-3 pb-2 pt-1 dark:border-white/10 dark:bg-[#1c1c1e]/90">
            <ChevronLeft className="absolute left-2 top-4 size-6 text-[#007aff]" />
            <Video className="absolute right-3 top-4 size-5 text-[#007aff]" />
            <div className="flex flex-col items-center">
              <span
                className="flex size-10 items-center justify-center rounded-full text-[15px] font-semibold text-white"
                style={{ background: `linear-gradient(180deg, hsl(${avatarHue} 30% 66%), hsl(${avatarHue} 25% 50%))` }}
              >
                {single ? initialsOf(single.name) : "#"}
              </span>
              <span className="mt-1 max-w-[200px] truncate text-[11px] font-medium">{title}</span>
              {single && <span className="text-[10px] text-[#8e8e93]">{formatPhone(single.number)}</span>}
            </div>
          </div>

          {/* Messages */}
          <div ref={scrollRef} className="flex min-h-0 flex-1 flex-col overflow-y-auto px-3 pb-2 pl-4 [scrollbar-width:none]">
            {loading && messages.length === 0 ? <div className="m-auto text-[12px] text-[#8e8e93]">Loading…</div> : rows}
          </div>

          {/* Input bar (decorative) */}
          <div className="flex shrink-0 items-center gap-2 px-3 pb-7 pt-1.5">
            <span className="flex size-8 items-center justify-center rounded-full bg-[#e9e9eb] text-[#8e8e93] dark:bg-[#262628]">
              <Plus className="size-4" />
            </span>
            <span className="flex h-8 flex-1 items-center justify-between rounded-full border border-black/15 px-3 text-[13px] text-[#8e8e93] dark:border-white/20">
              Text Message
              <Mic className="size-4" />
            </span>
          </div>

          {/* Home indicator */}
          <span className="absolute bottom-2 left-1/2 h-[5px] w-[120px] -translate-x-1/2 rounded-full bg-black dark:bg-white" />
        </div>
      </div>
    </div>
  );
}
