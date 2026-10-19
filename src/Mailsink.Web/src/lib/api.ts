// Typed client for the Mailsink REST API (/api/v1). Shapes mirror Mailsink.Api/Dtos.cs.

export interface Address {
  name: string | null;
  email: string;
}

export interface MessageSummary {
  id: string;
  messageId: string | null;
  subject: string | null;
  from: Address | null;
  to: Address[];
  cc: Address[];
  bcc: Address[];
  date: string | null;
  receivedAt: string;
  size: number;
  snippet: string;
  isRead: boolean;
  tags: string[];
  hasAttachments: boolean;
  attachmentCount: number;
  source: "Smtp" | "Submission" | "ImapAppend" | "Api" | "Ui";
  recipients: string[];
}

export interface MessageList {
  total: number;
  unread: number;
  start: number;
  limit: number;
  messages: MessageSummary[];
  tags: string[];
}

export interface Envelope {
  mailFrom: string | null;
  rcptTo: string[];
  helo: string | null;
  clientIp: string | null;
  secure: boolean;
  authUser: string | null;
  sessionId: string | null;
  source: string;
}

export interface Attachment {
  partId: string;
  fileName: string | null;
  contentType: string;
  size: number;
  contentId: string | null;
  inline: boolean;
}

export interface MessageDetail {
  summary: MessageSummary;
  replyTo: Address[];
  envelope: Envelope;
  text: string | null;
  html: string | null;
  attachments: Attachment[];
}

export interface Header {
  name: string;
  value: string;
}

export interface Account {
  address: string;
  hasPassword: boolean;
  mailboxes: number;
  messages: number;
  createdAt: string;
  lastLoginAt: string | null;
  lastLoginProtocol: string | null;
}

export interface Mailbox {
  id: number;
  name: string;
  specialUse: string | null;
  messages: number;
  unseen: number;
  size: number;
  uidValidity: number;
  uidNext: number;
  subscribed: boolean;
}

export interface MailboxMessage {
  uid: number;
  flags: string[];
  internalDate: string;
  size: number;
  message: MessageSummary;
}

export interface MailboxMessages {
  mailbox: Mailbox;
  total: number;
  start: number;
  limit: number;
  messages: MailboxMessage[];
}

export interface EndpointInfo {
  name: string;
  protocol: string;
  port: number;
  implicitTls: boolean;
  address: string;
}

export interface Info {
  version: string;
  hostname: string;
  defaultDomain: string;
  startedAt: string;
  uptimeSeconds: number;
  endpoints: EndpointInfo[];
  messages: number;
  unread: number;
  storeSize: number;
  accounts: number;
  storage: string;
  dataDir: string;
  authMode: "AcceptAny" | "Registry";
  requireTls: boolean;
  tlsAvailable: boolean;
  retention: { maxMessages: number; maxAgeHours: number | null; maxTotalSize: number | null; appliesToMailboxes: boolean };
  maxMessageSize: number;
  uiAuth: boolean;
}

export type ChaosStage = "Connect" | "Helo" | "Auth" | "MailFrom" | "RcptTo" | "Data";
export type ChaosAction = "Respond" | "Delay" | "Drop";

export interface ChaosRule {
  id: string;
  name: string | null;
  enabled: boolean;
  stage: ChaosStage;
  match: { sender?: string | null; recipient?: string | null; clientIp?: string | null; user?: string | null } | null;
  response: { code: number; text: string } | null;
  delayMs: number;
  action: ChaosAction | null;
  probability: number;
}

export interface ChaosSettings {
  enabled: boolean;
  rules: ChaosRule[];
}

export interface TranscriptSummary {
  id: string;
  protocol: string;
  clientIp: string | null;
  user: string | null;
  secure: boolean;
  startedAt: string;
  endedAt: string | null;
  commands: number;
  messageIds: string[];
}

export interface Transcript {
  session: TranscriptSummary;
  lines: { at: string; direction: "Client" | "Server" | "Info"; text: string }[];
}

export interface Template {
  id: string;
  name: string;
  subject: string | null;
  text: string | null;
  html: string | null;
  sampleData: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface Metrics {
  metrics: {
    startedAt: string;
    messagesReceived: number;
    activeSessions: Record<string, number>;
    totalSessions: Record<string, number>;
    messagesPerMinute: { minute: string; messages: number }[];
  };
  messages: number;
  unread: number;
  storeSize: number;
  accounts: number;
}

export interface SendRequest {
  from: Address;
  to: Address[];
  cc?: Address[];
  bcc?: Address[];
  replyTo?: Address[];
  subject?: string;
  text?: string;
  html?: string;
  headers?: Record<string, string>;
  attachments?: { filename: string; contentType?: string; contentBase64: string }[];
  inline?: { contentId: string; contentType?: string; contentBase64: string; filename?: string }[];
  templateData?: unknown;
  template?: string;
  repeat?: number;
  tags?: string[];
}

export interface BulkSendRequest {
  count: number;
  from?: Address;
  to?: string[];
  random?: boolean;
  subject?: string;
  text?: string;
  html?: string;
  template?: string;
  templateData?: unknown;
  withAttachments?: boolean;
}

export interface SmsSender {
  name: string;
  number: string;
}

export interface SmsMessage {
  id: string;
  to: string;
  from: SmsSender;
  body: string;
  receivedAt: string;
  isRead: boolean;
}

export interface SmsPhone {
  number: string;
  count: number;
  unread: number;
  senders: number;
  lastMessage: string;
  lastFromName: string;
  firstReceivedAt: string;
  lastReceivedAt: string;
}

export interface SmsStats {
  phones: number;
  messages: number;
  unread: number;
  senders: number;
  last24Hours: number;
}

export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
  }
}

export const apiBase = new URL("api/v1/", new URL(".", document.baseURI)).pathname;

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(apiBase + path, {
    ...init,
    headers: { "X-Mailsink-Source": "ui", ...(init?.body && typeof init.body === "string" ? { "Content-Type": "application/json" } : {}), ...init?.headers },
  });
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const body = await response.json();
      if (body?.error) message = body.error;
      else if (body?.response) message = body.response;
    } catch {
      /* not JSON */
    }
    throw new ApiError(response.status, message);
  }
  if (response.status === 204) return undefined as T;
  const type = response.headers.get("content-type") ?? "";
  return (type.includes("json") ? response.json() : response.text()) as Promise<T>;
}

const json = (body: unknown) => JSON.stringify(body);
const enc = encodeURIComponent;

export const api = {
  messages: (params: { query?: string; start?: number; limit?: number; tag?: string; unread?: boolean; attachments?: boolean; source?: string }) => {
    const qs = new URLSearchParams();
    Object.entries(params).forEach(([k, v]) => v !== undefined && v !== "" && qs.set(k, String(v)));
    return request<MessageList>(`messages?${qs}`);
  },
  message: (id: string) => request<MessageDetail>(`messages/${id}`),
  headers: (id: string) => request<Header[]>(`messages/${id}/headers`),
  raw: (id: string) => request<string>(`messages/${id}/raw`),
  transcriptFor: (id: string) => request<Transcript>(`messages/${id}/source-transcript`),
  setRead: (ids: string[] | null, read: boolean) => request<void>(`messages/${read ? "read" : "unread"}`, { method: "PUT", body: json({ ids }) }),
  setTags: (id: string, tags: string[]) => request<void>(`messages/${id}/tags`, { method: "PUT", body: json({ tags }) }),
  deleteMessages: (ids: string[] | null) => request<void>("messages", { method: "DELETE", body: json({ ids }) }),
  send: (body: SendRequest) => request<{ ids: string[]; messageId: string | null }>("send", { method: "POST", body: json(body) }),
  sendBulk: (body: BulkSendRequest) => request<{ ids: string[] }>("send/bulk", { method: "POST", body: json(body) }),
  sendRaw: (raw: string, from?: string, to?: string[]) => {
    const qs = new URLSearchParams();
    if (from) qs.set("from", from);
    if (to?.length) qs.set("to", to.join(","));
    return request<{ ids: string[]; messageId: string | null }>(`send/raw?${qs}`, {
      method: "POST",
      body: raw,
      headers: { "Content-Type": "message/rfc822" },
    });
  },
  tags: () => request<string[]>("tags"),
  info: () => request<Info>("info"),
  metrics: () => request<Metrics>("metrics"),
  accounts: () => request<Account[]>("accounts"),
  createAccount: (address: string, password: string | null) => request<Account>("accounts", { method: "POST", body: json({ address, password }) }),
  deleteAccount: (address: string) => request<void>(`accounts/${enc(address)}`, { method: "DELETE" }),
  setPassword: (address: string, password: string | null) => request<void>(`accounts/${enc(address)}/password`, { method: "PUT", body: json({ password }) }),
  mailboxes: (address: string) => request<Mailbox[]>(`accounts/${enc(address)}/mailboxes`),
  createMailbox: (address: string, name: string) => request<Mailbox>(`accounts/${enc(address)}/mailboxes`, { method: "POST", body: json({ name }) }),
  deleteMailbox: (address: string, name: string) => request<void>(`accounts/${enc(address)}/mailboxes/${enc(name)}`, { method: "DELETE" }),
  renameMailbox: (address: string, name: string, newName: string) =>
    request<Mailbox>(`accounts/${enc(address)}/mailboxes/${enc(name)}`, { method: "PUT", body: json({ newName }) }),
  mailboxMessages: (address: string, name: string, start = 0, limit = 50) =>
    request<MailboxMessages>(`accounts/${enc(address)}/mailboxes/${enc(name)}/messages?start=${start}&limit=${limit}`),
  move: (address: string, name: string, uids: number[], target: string, copy = false) =>
    request<number[]>(`accounts/${enc(address)}/mailboxes/${enc(name)}/move`, { method: "POST", body: json({ uids, target, copy }) }),
  flags: (address: string, name: string, uids: number[], add: string[] | null, remove: string[] | null) =>
    request<void>(`accounts/${enc(address)}/mailboxes/${enc(name)}/flags`, { method: "PUT", body: json({ uids, add, remove }) }),
  expunge: (address: string, name: string) => request<number[]>(`accounts/${enc(address)}/mailboxes/${enc(name)}/expunge`, { method: "POST" }),
  chaos: () => request<ChaosSettings>("chaos"),
  saveChaos: (settings: ChaosSettings) => request<ChaosSettings>("chaos", { method: "PUT", body: json(settings) }),
  logs: (protocol?: string) => request<TranscriptSummary[]>(`logs${protocol ? `?protocol=${protocol}` : ""}`),
  log: (id: string) => request<Transcript>(`logs/${id}`),
  clearLogs: () => request<void>("logs", { method: "DELETE" }),
  templates: () => request<Template[]>("templates"),
  saveTemplate: (id: string | null, body: { name: string; subject: string | null; text: string | null; html: string | null; sampleData: string | null }) =>
    id ? request<Template>(`templates/${id}`, { method: "PUT", body: json(body) }) : request<Template>("templates", { method: "POST", body: json(body) }),
  deleteTemplate: (id: string) => request<void>(`templates/${id}`, { method: "DELETE" }),
  smsStats: () => request<SmsStats>("sms/stats"),
  smsPhones: () => request<SmsPhone[]>("sms/phones"),
  smsThread: (number: string) => request<SmsMessage[]>(`sms/phones/${enc(number)}`),
  sendSms: (body: { message: string; to: string[]; from?: { name?: string; number?: string } }) =>
    request<{ ids: string[]; recipients: string[]; from: SmsSender }>("sms", { method: "POST", body: json(body) }),
  markSmsRead: (number: string) => request<void>(`sms/phones/${enc(number)}/read`, { method: "POST" }),
  deleteSmsPhone: (number: string) => request<void>(`sms/phones/${enc(number)}`, { method: "DELETE" }),
  deleteAllSms: () => request<void>("sms", { method: "DELETE" }),
};

export const urls = {
  raw: (id: string, download = false) => `${apiBase}messages/${id}/raw${download ? "?download=true" : ""}`,
  html: (id: string, remote: boolean) => `${apiBase}messages/${id}/html${remote ? "?remote=true" : ""}`,
  part: (id: string, partId: string) => `${apiBase}messages/${id}/parts/${partId}?download=true`,
  export: (format: "zip" | "json", query?: string, ids?: string[]) =>
    `${apiBase}messages/export?format=${format}${query ? `&query=${enc(query)}` : ""}${ids?.length ? `&ids=${ids.join(",")}` : ""}`,
  tlsCert: () => `${apiBase}tls/cert`,
  docs: () => new URL("api/docs", new URL(".", document.baseURI)).pathname,
  hub: () => new URL("hubs/mail", new URL(".", document.baseURI)).pathname,
};
