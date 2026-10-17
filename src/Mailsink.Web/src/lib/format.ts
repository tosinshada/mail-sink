import { clsx, type ClassValue } from "clsx";
import { twMerge } from "tailwind-merge";
import type { Address } from "./api";

export const cn = (...inputs: ClassValue[]) => twMerge(clsx(inputs));

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

const units: [Intl.RelativeTimeFormatUnit, number][] = [
  ["year", 365 * 24 * 3600],
  ["month", 30 * 24 * 3600],
  ["day", 24 * 3600],
  ["hour", 3600],
  ["minute", 60],
];

export function relativeTime(iso: string, now = Date.now()): string {
  const seconds = Math.round((new Date(iso).getTime() - now) / 1000);
  const abs = Math.abs(seconds);
  if (abs < 45) return "just now";
  for (const [unit, size] of units) {
    if (abs >= size) {
      const value = Math.round(seconds / size);
      const short: Record<string, string> = { year: "y", month: "mo", day: "d", hour: "h", minute: "m" };
      return value < 0 ? `${-value}${short[unit]} ago` : `in ${value}${short[unit]}`;
    }
  }
  return "just now";
}

export function absoluteTime(iso: string): string {
  const d = new Date(iso);
  return `${d.toLocaleString()} · ${d.toISOString().replace("T", " ").replace(/\.\d+Z$/, " UTC")}`;
}

export const displayName = (a: Address | null | undefined) => (a ? a.name || a.email : "(unknown sender)");

export const formatAddress = (a: Address) => (a.name ? `${a.name} <${a.email}>` : a.email);

export function initials(a: Address | null | undefined): string {
  const source = a?.name || a?.email || "?";
  const parts = source.replace(/@.*/, "").split(/[\s._-]+/).filter(Boolean);
  return ((parts[0]?.[0] ?? "?") + (parts[1]?.[0] ?? "")).toUpperCase();
}

/** A stable hue per string, for avatar tiles. */
export function hue(text: string): number {
  let h = 0;
  for (let i = 0; i < text.length; i++) h = (h * 31 + text.charCodeAt(i)) % 360;
  return h;
}

export function parseAddressList(value: string): Address[] {
  return value
    .split(/[,;\n]/)
    .map((s) => s.trim())
    .filter(Boolean)
    .map((s) => {
      const match = s.match(/^(.*)<([^>]+)>$/);
      return match ? { name: match[1].trim().replace(/^"|"$/g, "") || null, email: match[2].trim() } : { name: null, email: s };
    });
}

export function fileToBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).split(",")[1] ?? "");
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

export async function copyText(text: string) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    const area = document.createElement("textarea");
    area.value = text;
    document.body.appendChild(area);
    area.select();
    document.execCommand("copy");
    area.remove();
  }
}
