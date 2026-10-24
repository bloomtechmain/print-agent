/**
 * Client for Bloom Print Agent — the small Windows tray app that lets a web app list the PC's
 * printers and print to them directly (no print dialog). Framework-agnostic; the per-device
 * connection (token, chosen printer) lives in localStorage because the agent is per computer.
 * Canonical copy: bloom-print-agent/client/printAgent.ts — copy this file unchanged into any web app.
 */

export const AGENT_DEFAULT_URL = "http://127.0.0.1:9311";
const STORAGE_KEY = "bloom-print-agent";

export type Align = "left" | "center" | "right";
export type PrintMode = "escpos" | "graphic";

/** Content blocks the agent understands. escpos renders them natively on thermal printers;
 *  graphic draws them through the Windows driver (any printer; QR/barcodes print as text there). */
export type PrintBlock =
  | { type: "text"; text: string; align?: Align; bold?: boolean; underline?: boolean; size?: 1 | 2 | 3 | 4 }
  | { type: "row"; left: string; right: string; bold?: boolean; size?: 1 | 2 }
  | { type: "hr"; char?: string }
  | { type: "feed"; lines?: number }
  | { type: "qr"; data: string; size?: number; ecc?: "L" | "M" | "Q" | "H"; align?: Align }
  | { type: "barcode"; data: string; height?: number; width?: number; hri?: boolean; align?: Align }
  | { type: "image"; data: string; width?: number; align?: Align }
  | { type: "cut" }
  | { type: "drawer" }
  | { type: "raw"; data: string };

export interface AgentPrinter {
  name: string;
  isDefault: boolean;
  status: "ready" | "printing" | "offline" | "error" | "unknown";
  driver: string;
  port: string;
  isNetwork: boolean;
  isShared: boolean;
  kind: "thermal" | "standard" | "virtual";
  suggestedMode: PrintMode;
}

export interface AgentStatus {
  agent: string;
  version: string;
  api: number;
  machine: string;
  paired: boolean;
  requireApproval: boolean;
}

export interface AgentSettings {
  url: string;
  token: string | null;
  machine: string | null;
  printer: string | null;
  mode: PrintMode;
  /** mm; 0 = full page width (graphic mode on A4/Letter printers) */
  paperWidth: number;
  copies: number;
  /** route receipt printing through the agent */
  enabled: boolean;
  /** print as soon as a receipt is created, without pressing Print */
  autoPrint: boolean;
}

export interface PrintOptions {
  title?: string;
  printer?: string;
  mode?: PrintMode;
  paperWidth?: number;
  copies?: number;
  cut?: boolean;
  openDrawer?: boolean;
}

export const AGENT_DEFAULTS: AgentSettings = {
  url: AGENT_DEFAULT_URL,
  token: null,
  machine: null,
  printer: null,
  mode: "escpos",
  paperWidth: 80,
  copies: 1,
  enabled: true,
  autoPrint: true,
};

export class PrintAgentError extends Error {
  constructor(public code: string, message: string) {
    super(message);
    this.name = "PrintAgentError";
  }
}

// ---- per-device settings (subscribe-able, e.g. for React's useSyncExternalStore) ----

let cache: AgentSettings | null = null;
const listeners = new Set<() => void>();

export function getAgentSettings(): AgentSettings {
  if (typeof window === "undefined") return AGENT_DEFAULTS;
  if (!cache) {
    try {
      cache = { ...AGENT_DEFAULTS, ...JSON.parse(localStorage.getItem(STORAGE_KEY) ?? "{}") };
    } catch {
      cache = { ...AGENT_DEFAULTS };
    }
  }
  return cache!;
}

export function saveAgentSettings(patch: Partial<AgentSettings>): AgentSettings {
  cache = { ...getAgentSettings(), ...patch };
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(cache));
  } catch {
    // private mode / storage blocked: keep the in-memory copy for this tab
  }
  listeners.forEach((l) => l());
  return cache;
}

export function subscribeAgentSettings(fn: () => void) {
  listeners.add(fn);
  return () => {
    listeners.delete(fn);
  };
}

/** Connected, a printer chosen and agent printing switched on. */
export const isAgentReady = (s: AgentSettings = getAgentSettings()) => s.enabled && !!s.token && !!s.printer;

// ---- HTTP ----

async function call<T>(path: string, opts: { method?: "GET" | "POST"; body?: unknown; timeout?: number; s?: AgentSettings } = {}): Promise<T> {
  const s = opts.s ?? getAgentSettings();
  let res: Response;
  try {
    res = await fetch(s.url.replace(/\/+$/, "") + path, {
      method: opts.method ?? "GET",
      headers: {
        ...(opts.body !== undefined ? { "Content-Type": "application/json" } : {}),
        ...(s.token ? { Authorization: `Bearer ${s.token}` } : {}),
      },
      body: opts.body !== undefined ? JSON.stringify(opts.body) : undefined,
      signal: AbortSignal.timeout(opts.timeout ?? 8000),
    });
  } catch {
    throw new PrintAgentError(
      "AGENT_OFFLINE",
      "Can't reach Print Agent on this computer. Make sure it is running and that the browser is allowed to access local devices.",
    );
  }
  const data = await res.json().catch(() => ({}));
  if (!res.ok || data.ok === false) {
    // the agent forgot us (disconnected in the agent window): drop the stale token
    if (res.status === 401 && s.token) saveAgentSettings({ token: null });
    throw new PrintAgentError(data.code ?? `HTTP_${res.status}`, data.error ?? `Print Agent error (${res.status})`);
  }
  return data as T;
}

export const agentStatus = (url?: string) =>
  call<AgentStatus>("/v1/status", { timeout: 3000, s: url ? { ...getAgentSettings(), url } : undefined });

/** Asks the agent for access. The person at the PC has to click Allow on the agent's popup (60 s). */
export async function pairAgent(appName: string, url?: string) {
  if (url) saveAgentSettings({ url });
  const r = await call<{ token: string; machine: string }>("/v1/pair", { method: "POST", body: { app: appName }, timeout: 75000, s: { ...getAgentSettings(), token: null } });
  saveAgentSettings({ token: r.token, machine: r.machine });
  return r;
}

export async function listPrinters() {
  return (await call<{ printers: AgentPrinter[] }>("/v1/printers")).printers;
}

function jobBase(o: PrintOptions) {
  const s = getAgentSettings();
  return {
    printer: o.printer ?? s.printer ?? undefined,
    mode: o.mode ?? s.mode,
    paperWidth: o.paperWidth ?? s.paperWidth,
    copies: o.copies ?? s.copies,
    title: o.title ?? "Receipt",
    cut: o.cut ?? true,
    openDrawer: o.openDrawer ?? false,
  };
}

/** Prints content blocks on the chosen printer. */
export const printDocument = (content: PrintBlock[], o: PrintOptions = {}) =>
  call<{ printer: string; mode: string; jobId: number }>("/v1/print", { method: "POST", body: { ...jobBase(o), content }, timeout: 30000 });

/** Sends bytes as-is (e.g. ESC/POS or ZPL you built yourself). */
export const printRaw = (base64: string, o: PrintOptions = {}) =>
  call<{ printer: string; jobId: number }>("/v1/print", { method: "POST", body: { ...jobBase(o), raw: base64 }, timeout: 30000 });

export const printTestPage = (o: PrintOptions = {}) =>
  call<{ printer: string }>("/v1/print/test", { method: "POST", body: jobBase(o), timeout: 30000 });

export async function disconnectAgent() {
  try {
    await call("/v1/unpair", { method: "POST", body: {}, timeout: 3000 });
  } catch {
    // agent offline or already forgot us — clearing locally is what matters
  }
  saveAgentSettings({ token: null, machine: null, printer: null });
}
