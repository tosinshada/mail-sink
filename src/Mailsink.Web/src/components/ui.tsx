import * as DialogPrimitive from "@radix-ui/react-dialog";
import * as DropdownPrimitive from "@radix-ui/react-dropdown-menu";
import * as SwitchPrimitive from "@radix-ui/react-switch";
import * as TabsPrimitive from "@radix-ui/react-tabs";
import * as TooltipPrimitive from "@radix-ui/react-tooltip";
import * as CheckboxPrimitive from "@radix-ui/react-checkbox";
import { Check, Copy, Info, Loader2, X } from "lucide-react";
import { forwardRef, useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes, type TextareaHTMLAttributes } from "react";
import { absoluteTime, cn, copyText, relativeTime } from "@/lib/format";

type ButtonVariant = "primary" | "secondary" | "ghost" | "danger";

export const Button = forwardRef<HTMLButtonElement, ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant; size?: "sm" | "md"; loading?: boolean }>(
  ({ className, variant = "secondary", size = "md", loading, children, disabled, ...props }, ref) => (
    <button
      ref={ref}
      disabled={disabled || loading}
      className={cn(
        "inline-flex items-center justify-center gap-2 rounded-md font-medium whitespace-nowrap transition-colors disabled:opacity-50 disabled:pointer-events-none select-none",
        size === "md" ? "h-9 px-3.5 text-[14px]" : "h-8 px-2.5 text-[13px]",
        variant === "primary" && "bg-primary text-primary-fg hover:opacity-90",
        variant === "secondary" && "border border-border bg-surface text-fg hover:bg-surface-hover",
        variant === "ghost" && "text-muted hover:text-fg hover:bg-surface-hover",
        variant === "danger" && "bg-danger text-white hover:opacity-90",
        className,
      )}
      {...props}
    >
      {loading && <Loader2 className="size-4 animate-spin" />}
      {children}
    </button>
  ),
);
Button.displayName = "Button";

export const IconButton = forwardRef<HTMLButtonElement, ButtonHTMLAttributes<HTMLButtonElement> & { label: string; bordered?: boolean }>(
  ({ className, label, bordered = true, children, ...props }, ref) => (
    <Tooltip content={label}>
      <button
        ref={ref}
        aria-label={label}
        className={cn(
          "inline-flex size-9 shrink-0 items-center justify-center rounded-md text-muted transition-colors hover:text-fg hover:bg-surface-hover disabled:opacity-40",
          bordered && "border border-border bg-surface",
          className,
        )}
        {...props}
      >
        {children}
      </button>
    </Tooltip>
  ),
);
IconButton.displayName = "IconButton";

export const Input = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement>>(({ className, ...props }, ref) => (
  <input
    ref={ref}
    className={cn(
      "h-9 w-full rounded-md border border-border bg-surface px-3 text-[14px] text-fg placeholder:text-muted/70 outline-none focus:border-border-strong focus:ring-2 focus:ring-info/30",
      className,
    )}
    {...props}
  />
));
Input.displayName = "Input";

export const Textarea = forwardRef<HTMLTextAreaElement, TextareaHTMLAttributes<HTMLTextAreaElement>>(({ className, ...props }, ref) => (
  <textarea
    ref={ref}
    className={cn(
      "w-full rounded-md border border-border bg-surface px-3 py-2 text-[14px] text-fg placeholder:text-muted/70 outline-none focus:border-border-strong focus:ring-2 focus:ring-info/30",
      className,
    )}
    {...props}
  />
));
Textarea.displayName = "Textarea";

export function NativeSelect({ className, children, ...props }: SelectHTMLAttributes<HTMLSelectElement>) {
  return (
    <div className={cn("relative", className)}>
      <select
        className="h-9 w-full appearance-none rounded-md border border-border bg-surface pl-3 pr-9 text-[14px] text-fg outline-none focus:border-border-strong focus:ring-2 focus:ring-info/30"
        {...props}
      >
        {children}
      </select>
      <svg className="pointer-events-none absolute right-3 top-1/2 size-4 -translate-y-1/2 text-muted" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
        <path d="m6 9 6 6 6-6" />
      </svg>
    </div>
  );
}

export function Label({ children, hint, htmlFor }: { children: ReactNode; hint?: string; htmlFor?: string }) {
  return (
    <label htmlFor={htmlFor} className="mb-1.5 flex items-baseline justify-between text-[13px] font-medium text-muted">
      <span>{children}</span>
      {hint && <span className="font-normal text-muted/80">{hint}</span>}
    </label>
  );
}

export function Tooltip({ content, children, side = "top" }: { content: ReactNode; children: ReactNode; side?: "top" | "bottom" | "left" | "right" }) {
  return (
    <TooltipPrimitive.Root delayDuration={300}>
      <TooltipPrimitive.Trigger asChild>{children}</TooltipPrimitive.Trigger>
      <TooltipPrimitive.Portal>
        <TooltipPrimitive.Content
          side={side}
          sideOffset={6}
          className="z-50 max-w-sm rounded-md border border-border bg-surface px-2.5 py-1.5 text-[12px] text-fg shadow-lg shadow-black/30"
        >
          {content}
        </TooltipPrimitive.Content>
      </TooltipPrimitive.Portal>
    </TooltipPrimitive.Root>
  );
}

/** Monospace value in a small rounded chip. Click copies. */
export function CodeChip({ value, display, className }: { value: string; display?: string; className?: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <Tooltip content={copied ? "Copied" : `Copy ${value}`}>
      <button
        type="button"
        onClick={(e) => {
          e.stopPropagation();
          void copyText(value).then(() => {
            setCopied(true);
            setTimeout(() => setCopied(false), 1200);
          });
        }}
        className={cn(
          "group inline-flex max-w-full items-center gap-1.5 rounded-md border border-border bg-surface px-2 py-0.5 font-mono text-[12.5px] text-fg/90 hover:border-border-strong",
          className,
        )}
      >
        <span className="truncate">{display ?? value}</span>
        {copied ? <Check className="size-3 text-accent" /> : <Copy className="size-3 opacity-0 transition-opacity group-hover:opacity-60" />}
      </button>
    </Tooltip>
  );
}

export function RelativeTime({ iso, className }: { iso: string | null | undefined; className?: string }) {
  if (!iso) {
    return (
      <span className={cn("inline-flex items-center gap-1 text-muted", className)}>
        Never
        <Tooltip content="No activity recorded yet">
          <Info className="size-3.5" />
        </Tooltip>
      </span>
    );
  }

  return (
    <Tooltip content={absoluteTime(iso)}>
      <time dateTime={iso} className={cn("link-dotted cursor-default whitespace-nowrap", className)}>
        {relativeTime(iso)}
      </time>
    </Tooltip>
  );
}

export function Badge({ children, tone = "neutral", className }: { children: ReactNode; tone?: "neutral" | "accent" | "danger" | "warning" | "info"; className?: string }) {
  return (
    <span
      className={cn(
        "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[12px] font-medium whitespace-nowrap",
        tone === "neutral" && "border-border bg-surface text-muted",
        tone === "accent" && "border-accent-border bg-accent-soft text-accent",
        tone === "danger" && "border-danger/40 bg-danger-soft text-danger",
        tone === "warning" && "border-warning/40 bg-warning/10 text-warning",
        tone === "info" && "border-info/40 bg-info/10 text-info",
        className,
      )}
    >
      {children}
    </span>
  );
}

export function Switch({ checked, onCheckedChange, label, disabled }: { checked: boolean; onCheckedChange: (v: boolean) => void; label?: string; disabled?: boolean }) {
  return (
    <label className={cn("inline-flex cursor-pointer items-center gap-2 text-[13px] text-muted", disabled && "cursor-not-allowed opacity-50")}>
      <SwitchPrimitive.Root
        checked={checked}
        disabled={disabled}
        onCheckedChange={onCheckedChange}
        className="relative h-5 w-9 rounded-full border border-border bg-surface-active transition-colors data-[state=checked]:border-accent-border data-[state=checked]:bg-accent"
      >
        <SwitchPrimitive.Thumb className="block size-4 translate-x-0.5 rounded-full bg-white shadow transition-transform data-[state=checked]:translate-x-[17px]" />
      </SwitchPrimitive.Root>
      {label}
    </label>
  );
}

export function Checkbox({ checked, onCheckedChange, label }: { checked: boolean | "indeterminate"; onCheckedChange: (v: boolean) => void; label: string }) {
  return (
    <CheckboxPrimitive.Root
      aria-label={label}
      checked={checked}
      onClick={(e) => e.stopPropagation()}
      onCheckedChange={(v) => onCheckedChange(v === true)}
      className="flex size-4 items-center justify-center rounded border border-border-strong bg-surface data-[state=checked]:border-fg data-[state=checked]:bg-fg data-[state=indeterminate]:bg-fg"
    >
      <CheckboxPrimitive.Indicator className="text-bg">
        {checked === "indeterminate" ? <span className="block h-0.5 w-2 bg-bg" /> : <Check className="size-3" strokeWidth={3} />}
      </CheckboxPrimitive.Indicator>
    </CheckboxPrimitive.Root>
  );
}

export const Tabs = TabsPrimitive.Root;

export function TabsList({ children, className }: { children: ReactNode; className?: string }) {
  return <TabsPrimitive.List className={cn("flex gap-5 overflow-x-auto border-b border-border", className)}>{children}</TabsPrimitive.List>;
}

export function TabsTrigger({ value, children }: { value: string; children: ReactNode }) {
  return (
    <TabsPrimitive.Trigger
      value={value}
      className="-mb-px shrink-0 border-b-2 border-transparent pb-2.5 text-[14px] text-muted transition-colors hover:text-fg data-[state=active]:border-fg data-[state=active]:text-fg"
    >
      {children}
    </TabsPrimitive.Trigger>
  );
}

export const TabsContent = TabsPrimitive.Content;

export function Dialog({
  open,
  onOpenChange,
  title,
  description,
  children,
  footer,
  wide,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: ReactNode;
  description?: ReactNode;
  children?: ReactNode;
  footer?: ReactNode;
  wide?: boolean;
}) {
  return (
    <DialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
      <DialogPrimitive.Portal>
        <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-black/60 backdrop-blur-[2px]" />
        <DialogPrimitive.Content
          className={cn(
            "fixed left-1/2 top-[8vh] z-50 flex max-h-[84vh] w-[calc(100vw-32px)] -translate-x-1/2 flex-col rounded-lg border border-border bg-bg-subtle shadow-2xl shadow-black/50 outline-none",
            wide ? "max-w-4xl" : "max-w-lg",
          )}
        >
          <div className="flex items-start justify-between gap-4 border-b border-border px-5 py-4">
            <div>
              <DialogPrimitive.Title className="text-[16px] font-semibold">{title}</DialogPrimitive.Title>
              {description && <DialogPrimitive.Description className="mt-1 text-[13px] text-muted">{description}</DialogPrimitive.Description>}
            </div>
            <DialogPrimitive.Close className="rounded-md p-1 text-muted hover:bg-surface-hover hover:text-fg" aria-label="Close">
              <X className="size-4" />
            </DialogPrimitive.Close>
          </div>
          <div className="min-h-0 flex-1 overflow-auto px-5 py-4">{children}</div>
          {footer && <div className="flex justify-end gap-2 border-t border-border px-5 py-3">{footer}</div>}
        </DialogPrimitive.Content>
      </DialogPrimitive.Portal>
    </DialogPrimitive.Root>
  );
}

export const Menu = DropdownPrimitive.Root;
export const MenuTrigger = DropdownPrimitive.Trigger;

export function MenuContent({ children, align = "end" }: { children: ReactNode; align?: "start" | "end" | "center" }) {
  return (
    <DropdownPrimitive.Portal>
      <DropdownPrimitive.Content
        align={align}
        sideOffset={6}
        className="z-50 min-w-[200px] rounded-lg border border-border bg-bg-subtle p-1 shadow-xl shadow-black/40"
        onClick={(e) => e.stopPropagation()}
      >
        {children}
      </DropdownPrimitive.Content>
    </DropdownPrimitive.Portal>
  );
}

export function MenuItem({ children, onSelect, danger, icon, disabled }: { children: ReactNode; onSelect?: () => void; danger?: boolean; icon?: ReactNode; disabled?: boolean }) {
  return (
    <DropdownPrimitive.Item
      disabled={disabled}
      onSelect={onSelect}
      className={cn(
        "flex cursor-pointer items-center gap-2.5 rounded-md px-2.5 py-1.5 text-[13.5px] outline-none data-[highlighted]:bg-surface-hover data-[disabled]:opacity-40",
        danger ? "text-danger" : "text-fg",
      )}
    >
      <span className="text-muted [&>svg]:size-4">{icon}</span>
      <span className="flex-1">{children}</span>
    </DropdownPrimitive.Item>
  );
}

export const MenuSeparator = () => <DropdownPrimitive.Separator className="my-1 h-px bg-border" />;

export function Spinner({ className }: { className?: string }) {
  return <Loader2 className={cn("size-5 animate-spin text-muted", className)} />;
}

export function Card({ children, className, title, actions }: { children: ReactNode; className?: string; title?: ReactNode; actions?: ReactNode }) {
  return (
    <section className={cn("rounded-lg border border-border bg-bg-subtle", className)}>
      {(title || actions) && (
        <header className="flex items-center justify-between gap-3 border-b border-border px-4 py-3">
          <h3 className="text-[14px] font-semibold">{title}</h3>
          {actions}
        </header>
      )}
      <div className="p-4">{children}</div>
    </section>
  );
}

export function Kbd({ children }: { children: ReactNode }) {
  return <kbd className="inline-flex h-5 min-w-5 items-center justify-center rounded border border-border bg-surface px-1 font-mono text-[11px] text-muted">{children}</kbd>;
}

/** Metric tile: uppercase label, large tabular value, optional hint. */
export function Stat({ label, value, hint }: { label: string; value: string; hint?: ReactNode }) {
  return (
    <div className="rounded-lg border border-border bg-bg-subtle p-4">
      <div className="text-[12px] font-medium uppercase tracking-wide text-muted">{label}</div>
      <div className="mt-1 text-[26px] font-semibold leading-tight tabular-nums">{value}</div>
      {hint && <div className="mt-0.5 text-[12px] text-muted">{hint}</div>}
    </div>
  );
}
