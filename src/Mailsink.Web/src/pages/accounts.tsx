import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Folder, KeyRound, Plus, Trash2, UserPlus, Users } from "lucide-react";
import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { DataTable, EmptyState, IconTile, SearchInput, Toolbar } from "@/components/data-table";
import { PageHeader } from "@/components/layout";
import { snippets } from "@/components/snippets";
import { Badge, Button, Dialog, Input, Label, MenuItem, MenuSeparator, RelativeTime } from "@/components/ui";
import { api, type Account } from "@/lib/api";
import { useApp } from "@/lib/app-state";
import { formatSize } from "@/lib/format";

export function AccountsPage() {
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { setAccount, toast } = useApp();
  const accounts = useQuery({ queryKey: ["accounts"], queryFn: api.accounts });
  const info = useQuery({ queryKey: ["info"], queryFn: api.info });
  const [search, setSearch] = useState("");
  const [create, setCreate] = useState(false);
  const [password, setPassword] = useState<Account | null>(null);

  const refresh = () => queryClient.invalidateQueries({ queryKey: ["accounts"] });
  const remove = useMutation({
    mutationFn: (address: string) => api.deleteAccount(address),
    onSuccess: (_, address) => {
      toast({ title: `Deleted ${address}`, tone: "success" });
      void refresh();
    },
    onError: (e) => toast({ title: "Delete failed", description: String(e), tone: "danger" }),
  });

  const rows = (accounts.data ?? []).filter((a) => !search || a.address.toLowerCase().includes(search.toLowerCase()));
  const registry = info.data?.authMode === "Registry";

  return (
    <>
      <PageHeader
        title="Accounts"
        description={
          registry
            ? "Registry mode: only listed accounts can log in, and passwords are required."
            : "Accept-any mode: any address logs in with any password. Accounts are created automatically when mail arrives for a new address."
        }
        actions={
          <Button variant="primary" onClick={() => setCreate(true)}>
            <Plus className="size-4" /> Create account
          </Button>
        }
        snippet={snippets.generic("Create an account", "accounts", "POST", { address: `bob@${info.data?.defaultDomain ?? "test.local"}`, password: "secret" })}
      />

      <Toolbar>
        <SearchInput value={search} onChange={setSearch} placeholder="Filter accounts…" />
        <Badge tone={registry ? "warning" : "accent"}>{registry ? "Registry" : "Accept any"}</Badge>
      </Toolbar>

      <DataTable
        rows={rows}
        rowKey={(a) => a.address}
        loading={accounts.isLoading}
        onOpen={(a) => {
          setAccount(a.address);
          navigate("/mailboxes");
        }}
        empty={
          <EmptyState icon={<Users />} title="No accounts yet" description="Send a message to any address and an account appears here with its INBOX. Or create one explicitly, with a password if you want to test authentication.">
            <div className="text-center">
              <Button variant="primary" onClick={() => setCreate(true)}>
                <UserPlus className="size-4" /> Create account
              </Button>
            </div>
          </EmptyState>
        }
        columns={[
          {
            key: "address",
            header: "Address",
            render: (a) => (
              <span className="flex items-center gap-3">
                <IconTile text={a.address[0].toUpperCase()} seed={a.address} />
                <span className="link-dotted truncate text-[15px] font-medium">{a.address}</span>
              </span>
            ),
          },
          {
            key: "auth",
            header: "Auth",
            hideBelow: "sm",
            render: (a) => (a.hasPassword ? <Badge tone="info">password</Badge> : <Badge>{registry ? "no password" : "any password"}</Badge>),
          },
          { key: "mailboxes", header: "Mailboxes", hideBelow: "md", className: "w-28", render: (a) => <span className="text-muted">{a.mailboxes}</span> },
          { key: "messages", header: "Messages", hideBelow: "md", className: "w-28", render: (a) => <span className="text-muted">{a.messages}</span> },
          {
            key: "login",
            header: "Last login",
            hideBelow: "sm",
            className: "w-40 whitespace-nowrap",
            render: (a) => (
              <span className="flex items-center gap-2">
                <RelativeTime iso={a.lastLoginAt} />
                {a.lastLoginProtocol && <span className="font-mono text-[11px] uppercase text-muted">{a.lastLoginProtocol}</span>}
              </span>
            ),
          },
          { key: "created", header: "Created", hideBelow: "lg", className: "w-28 whitespace-nowrap", render: (a) => <RelativeTime iso={a.createdAt} /> },
        ]}
        rowMenu={(a) => (
          <>
            <MenuItem
              icon={<Folder />}
              onSelect={() => {
                setAccount(a.address);
                navigate("/mailboxes");
              }}
            >
              Open mailboxes
            </MenuItem>
            <MenuItem icon={<KeyRound />} onSelect={() => setPassword(a)}>
              {a.hasPassword ? "Change password…" : "Set password…"}
            </MenuItem>
            <MenuSeparator />
            <MenuItem icon={<Trash2 />} danger onSelect={() => confirm(`Delete ${a.address} and all its folders and messages?`) && remove.mutate(a.address)}>
              Delete account
            </MenuItem>
          </>
        )}
      />
      {info.data && (
        <p className="mt-3 text-[13px] text-muted">
          {info.data.accounts} accounts · store {formatSize(info.data.storeSize)}
        </p>
      )}

      <CreateAccountDialog open={create} onClose={() => setCreate(false)} domain={info.data?.defaultDomain ?? "test.local"} />
      <PasswordDialog account={password} onClose={() => setPassword(null)} />
    </>
  );
}

function CreateAccountDialog({ open, onClose, domain }: { open: boolean; onClose: () => void; domain: string }) {
  const queryClient = useQueryClient();
  const { toast } = useApp();
  const [address, setAddress] = useState("");
  const [pw, setPw] = useState("");
  useEffect(() => {
    if (open) {
      setAddress("");
      setPw("");
    }
  }, [open]);
  const create = useMutation({
    mutationFn: () => api.createAccount(address.trim(), pw || null),
    onSuccess: (a) => {
      toast({ title: `Created ${a.address}`, tone: "success" });
      void queryClient.invalidateQueries({ queryKey: ["accounts"] });
      onClose();
    },
    onError: (e) => toast({ title: "Could not create account", description: String(e), tone: "danger" }),
  });
  const valid = /^[^@\s]+@[^@\s]+$/.test(address.trim());
  return (
    <Dialog
      open={open}
      onOpenChange={(o) => !o && onClose()}
      title="Create account"
      description="Creates the default folders (INBOX, Sent, Drafts, Trash). The password is optional in accept-any mode."
      footer={
        <>
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" disabled={!valid} loading={create.isPending} onClick={() => create.mutate()}>
            Create
          </Button>
        </>
      }
    >
      <div className="space-y-4">
        <div>
          <Label htmlFor="new-address">Address</Label>
          <Input id="new-address" autoFocus value={address} onChange={(e) => setAddress(e.target.value)} placeholder={`bob@${domain}`} onKeyDown={(e) => e.key === "Enter" && valid && create.mutate()} />
        </div>
        <div>
          <Label htmlFor="new-password" hint="optional">
            Password
          </Label>
          <Input id="new-password" type="password" autoComplete="new-password" value={pw} onChange={(e) => setPw(e.target.value)} />
        </div>
      </div>
    </Dialog>
  );
}

function PasswordDialog({ account, onClose }: { account: Account | null; onClose: () => void }) {
  const queryClient = useQueryClient();
  const { toast } = useApp();
  const [pw, setPw] = useState("");
  useEffect(() => setPw(""), [account]);
  const save = useMutation({
    mutationFn: (value: string | null) => api.setPassword(account!.address, value),
    onSuccess: (_, value) => {
      toast({ title: value ? "Password updated" : "Password cleared", tone: "success" });
      void queryClient.invalidateQueries({ queryKey: ["accounts"] });
      onClose();
    },
    onError: (e) => toast({ title: "Could not update password", description: String(e), tone: "danger" }),
  });
  return (
    <Dialog
      open={!!account}
      onOpenChange={(o) => !o && onClose()}
      title={`Password for ${account?.address ?? ""}`}
      description="Stored as a PBKDF2 hash. Used by SMTP AUTH, IMAP LOGIN and POP3."
      footer={
        <>
          {account?.hasPassword && (
            <Button className="mr-auto" onClick={() => save.mutate(null)}>
              Clear password
            </Button>
          )}
          <Button onClick={onClose}>Cancel</Button>
          <Button variant="primary" disabled={!pw} loading={save.isPending} onClick={() => save.mutate(pw)}>
            Save
          </Button>
        </>
      }
    >
      <Label htmlFor="pw">New password</Label>
      <Input id="pw" type="password" autoFocus autoComplete="new-password" value={pw} onChange={(e) => setPw(e.target.value)} onKeyDown={(e) => e.key === "Enter" && pw && save.mutate(pw)} />
    </Dialog>
  );
}
