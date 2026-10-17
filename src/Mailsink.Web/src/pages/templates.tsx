import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { FileText, Pencil, Plus, Send, Trash2 } from "lucide-react";
import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { DataTable, EmptyState, IconTile } from "@/components/data-table";
import { PageHeader } from "@/components/layout";
import { snippets } from "@/components/snippets";
import { Button, Dialog, Input, Label, MenuItem, MenuSeparator, RelativeTime, Textarea } from "@/components/ui";
import { api, type Template } from "@/lib/api";
import { useApp } from "@/lib/app-state";
import { cn } from "@/lib/format";

const exampleTemplate = {
  name: "Welcome",
  subject: "Welcome, {{ name }}!",
  html: '<h1>Hi {{ name }}</h1>\n<p>Thanks for joining {{ product }}. <a href="{{ confirm_url }}">Confirm your address</a>.</p>',
  text: "Hi {{ name }},\n\nThanks for joining {{ product }}. Confirm your address: {{ confirm_url }}",
  sampleData: '{\n  "name": "Bob",\n  "product": "Mailsink",\n  "confirm_url": "https://app.test/confirm?token=abc123"\n}',
};

export function TemplatesPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { toast } = useApp();
  const templates = useQuery({ queryKey: ["templates"], queryFn: api.templates });
  const [editing, setEditing] = useState<Template | "new" | null>(null);

  const remove = useMutation({
    mutationFn: (id: string) => api.deleteTemplate(id),
    onSuccess: () => {
      toast({ title: "Template deleted", tone: "success" });
      void queryClient.invalidateQueries({ queryKey: ["templates"] });
    },
    onError: (e) => toast({ title: "Delete failed", description: String(e), tone: "danger" }),
  });

  return (
    <>
      <PageHeader
        title="Templates"
        description="Scriban templates for the send API. Reference one by id or name in POST /api/v1/send with templateData."
        actions={
          <Button variant="primary" onClick={() => setEditing("new")}>
            <Plus className="size-4" /> New template
          </Button>
        }
        snippet={snippets.generic("Send with a template", "send", "POST", { from: { email: "app@example.com" }, to: [{ email: "bob@test.local" }], template: "Welcome", templateData: { name: "Bob" } })}
      />

      <DataTable
        rows={templates.data ?? []}
        rowKey={(t) => t.id}
        loading={templates.isLoading}
        onOpen={(t) => setEditing(t)}
        empty={
          <EmptyState icon={<FileText />} title="No templates yet" description="Templates let tests send realistic mail with one API call. Variables use Scriban syntax: {{ name }}, {{ for item in items }} … {{ end }}.">
            <div className="text-center">
              <Button variant="primary" onClick={() => setEditing("new")}>
                <Plus className="size-4" /> Create the example template
              </Button>
            </div>
          </EmptyState>
        }
        columns={[
          {
            key: "name",
            header: "Name",
            render: (t) => (
              <span className="flex items-center gap-3">
                <IconTile icon={<FileText className="size-4" />} seed={t.name} />
                <span className="min-w-0">
                  <span className="link-dotted block truncate text-[15px] font-medium">{t.name}</span>
                  <span className="block truncate text-[13px] text-muted">{t.subject || "(no subject)"}</span>
                </span>
              </span>
            ),
          },
          {
            key: "parts",
            header: "Parts",
            hideBelow: "sm",
            render: (t) => (
              <span className="text-muted">
                {[t.html && "HTML", t.text && "text", t.sampleData && "sample data"].filter(Boolean).join(" · ") || "—"}
              </span>
            ),
          },
          { key: "id", header: "Id", hideBelow: "lg", render: (t) => <span className="font-mono text-[12px] text-muted">{t.id}</span> },
          { key: "updated", header: "Updated", className: "w-28 whitespace-nowrap", render: (t) => <RelativeTime iso={t.updatedAt} /> },
        ]}
        rowMenu={(t) => (
          <>
            <MenuItem icon={<Send />} onSelect={() => navigate(`/compose?template=${t.id}`)}>
              Use in Compose
            </MenuItem>
            <MenuItem icon={<Pencil />} onSelect={() => setEditing(t)}>
              Edit
            </MenuItem>
            <MenuSeparator />
            <MenuItem icon={<Trash2 />} danger onSelect={() => confirm(`Delete template ${t.name}?`) && remove.mutate(t.id)}>
              Delete
            </MenuItem>
          </>
        )}
      />

      <TemplateDialog template={editing} onClose={() => setEditing(null)} />
    </>
  );
}

function TemplateDialog({ template, onClose }: { template: Template | "new" | null; onClose: () => void }) {
  const queryClient = useQueryClient();
  const { toast } = useApp();
  const [form, setForm] = useState({ name: "", subject: "", text: "", html: "", sampleData: "" });
  useEffect(() => {
    if (template === "new") setForm({ ...exampleTemplate });
    else if (template) setForm({ name: template.name, subject: template.subject ?? "", text: template.text ?? "", html: template.html ?? "", sampleData: template.sampleData ?? "" });
  }, [template]);

  const save = useMutation({
    mutationFn: () =>
      api.saveTemplate(template && template !== "new" ? template.id : null, {
        name: form.name.trim(),
        subject: form.subject || null,
        text: form.text || null,
        html: form.html || null,
        sampleData: form.sampleData || null,
      }),
    onSuccess: (t) => {
      toast({ title: `Saved ${t.name}`, tone: "success" });
      void queryClient.invalidateQueries({ queryKey: ["templates"] });
      onClose();
    },
    onError: (e) => toast({ title: "Could not save template", description: String(e), tone: "danger" }),
  });

  const sampleInvalid = !!form.sampleData.trim() && !isJson(form.sampleData);
  const set = (key: keyof typeof form) => (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) => setForm((f) => ({ ...f, [key]: e.target.value }));

  return (
    <Dialog
      open={!!template}
      onOpenChange={(o) => !o && onClose()}
      title={template === "new" ? "New template" : `Edit ${form.name}`}
      description="Subject, HTML and text are Scriban templates. Sample data is used by Compose and the API docs."
      wide
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" disabled={!form.name.trim() || sampleInvalid || (!form.html && !form.text)} loading={save.isPending} onClick={() => save.mutate()}>
            Save
          </Button>
        </>
      }
    >
      <div className="grid gap-4 sm:grid-cols-2">
        <div>
          <Label htmlFor="t-name">Name</Label>
          <Input id="t-name" autoFocus value={form.name} onChange={set("name")} />
        </div>
        <div>
          <Label htmlFor="t-subject">Subject</Label>
          <Input id="t-subject" value={form.subject} onChange={set("subject")} placeholder="Welcome, {{ name }}!" />
        </div>
        <div className="sm:col-span-2">
          <Label htmlFor="t-html">HTML</Label>
          <Textarea id="t-html" rows={8} spellCheck={false} className="font-mono text-[13px]" value={form.html} onChange={set("html")} />
        </div>
        <div>
          <Label htmlFor="t-text" hint="optional">
            Text
          </Label>
          <Textarea id="t-text" rows={6} className="font-mono text-[13px]" value={form.text} onChange={set("text")} />
        </div>
        <div>
          <Label htmlFor="t-data" hint={sampleInvalid ? "invalid JSON" : "JSON, optional"}>
            Sample data
          </Label>
          <Textarea id="t-data" rows={6} spellCheck={false} className={cn("font-mono text-[13px]", sampleInvalid && "border-danger")} value={form.sampleData} onChange={set("sampleData")} />
        </div>
      </div>
    </Dialog>
  );
}

function isJson(s: string) {
  try {
    JSON.parse(s);
    return true;
  } catch {
    return false;
  }
}
