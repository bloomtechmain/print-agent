# Adding "Receipt printer · Print Agent" to a web app

**Audience:** an AI coding assistant (Claude, Cursor, Copilot…) or a developer adding direct
printing to one of our web products. Follow this file top to bottom; it is self-contained.

**Goal:** the app gets a **Settings → Receipt printer** section that finds *Bloom Print Agent*
running on the same PC, connects to it, lists that PC's printers, lets the user pick one, and from
then on every receipt/ticket/label prints **instantly to that printer — no browser print dialog**.
The browser print dialog remains the fallback whenever the agent isn't set up or fails.

> Prompt you can give an AI tool:
> *"Read PRINT-AGENT-SETTINGS-INTEGRATION.md and add Print Agent support to this app: copy the
> client library, add the Receipt printer section to our settings page using our own UI components,
> and route our receipt printing through it with browser print as fallback. Follow the checklist at
> the end."*

---

## 1. How it works (read once)

```
 Browser (your app, any domain)                       Same PC
 ┌─────────────────────────────────┐   fetch()   ┌───────────────────────────┐
 │ Settings → Receipt printer      │ ──────────► │ Bloom Print Agent (tray)  │ ──► Windows printers
 │ Print button / auto-print       │ 127.0.0.1   │ pair · list · print       │     (USB / LAN / any)
 └─────────────────────────────────┘    :9311    └───────────────────────────┘
```

- The agent is a Windows tray app listening on `http://127.0.0.1:9311` (port changeable).
- The web page calls it **directly from the browser** with `fetch`. Never call it from your
  server — the server is not the PC with the printer.
- **Pairing:** the app calls `POST /v1/pair`; the agent pops up *Allow / Deny* on the PC; Allow
  returns a token. The token only works from the same website origin.
- **Per-device configuration.** Agent URL, token, chosen printer, paper width etc. belong to *this
  computer*, so they are stored in the browser's `localStorage` — **not** in the app's database or
  org-wide settings. Two front-desk PCs = two independent setups. Do not sync them to the server.
- HTTPS pages may call `http://127.0.0.1` (browsers treat loopback as secure). Chrome/Edge show a
  one-time *"Allow this site to access devices on your local network?"* prompt — the user must
  click **Allow**. If they blocked it: site settings (padlock icon) → *Local network access* → Allow.
- Full HTTP API reference: `README.md` next to this file.

## 2. Ground rules

1. **Never block the business flow on printing.** A failed print shows a toast/banner with the
   error and a "Browser print" fallback. The sale/ticket is already saved.
2. **Fallback is always available:** if the agent isn't ready (not running, not paired, no printer
   chosen, or turned off), the Print button keeps doing whatever the app did before (usually
   `window.print()` of an on-screen receipt).
3. **Instant printing** (auto-print) only happens where a document is *created or explicitly
   reprinted* (sale completed, ticket issued, check-out receipt, "Reprint" action) — never when
   someone merely *views* a receipt. Fire it once per document (guard against React StrictMode
   double effects / re-renders).
4. **Send content blocks, not HTML.** Build the receipt as an array of blocks (section 5) from the
   same data the on-screen receipt uses. The agent turns them into ESC/POS (thermal) or driver
   output (any printer).
5. The settings section sits wherever the app's settings live (and inherits their permission
   check), but print buttons work for every user once a PC is configured.

## 3. Step 1 — copy the client library

Copy the file below **unchanged** to the app (e.g. `src/lib/printAgent.ts`; for plain JS strip the
types). It has no dependencies, works in any framework, and owns the localStorage settings.

| Function | Use |
|---|---|
| `getAgentSettings()` / `saveAgentSettings(patch)` / `subscribeAgentSettings(fn)` | Per-device settings; subscribe for reactivity (`useSyncExternalStore` in React, a `ref` + listener in Vue) |
| `isAgentReady(s?)` | `enabled && token && printer` — decides agent vs browser print |
| `agentStatus(url?)` | Is it running? Which version/PC? Are we paired? Throws `PrintAgentError("AGENT_OFFLINE")` |
| `pairAgent(appName)` | Starts pairing; resolves after the user clicks Allow (≤ 60 s), stores token |
| `listPrinters()` | Printers on the PC (`AgentPrinter[]`) |
| `printDocument(blocks, opts?)` | Print content blocks with the saved printer/mode/paper/copies |
| `printRaw(base64, opts?)` / `printTestPage()` | Raw bytes / agent's test page |
| `disconnectAgent()` | Unpair and clear token + printer |

A 401 from the agent (user disconnected the app in the agent window) clears the stored token
automatically, so the settings section falls back to "Not connected".

```ts
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
```

## 4. Step 2 — the settings section

Add one card/section to the app's existing **Settings** page titled **"Receipt printer · Print
Agent"** (or a "Printing" tab if settings are tabbed). Build it with **the app's own UI components**
(cards, selects, toggles, buttons, toasts, badges) so it looks native. Saving is immediate
(`saveAgentSettings`) — it does **not** use the page's "Save settings" button, because these values
are local to the PC. Say so in the section's help text.

### States

On mount call `agentStatus()`; if `paired`, also `listPrinters()`. Show one of:

| State | Detect | Show |
|---|---|---|
| Checking | first load | Badge "Checking…" |
| Agent not found | `agentStatus` throws | Badge "Agent not found" (red). Steps: run BloomPrintAgent.exe on this PC; click **Allow** if the browser asks about local network access; check the address. Optional "Download Print Agent" link if the product hosts the exe. |
| Not connected | status ok, `paired: false` (also clear any stale token) | "Found Bloom Print Agent 1.0.0 on FRONTDESK-PC" + **Connect** button → `pairAgent(appName)`; while waiting show "Click Allow on the Print Agent window on this computer…" |
| Connected | `paired: true` | The configuration form below. Badge "Ready" when a printer is chosen, else "Choose a printer" (amber). |

### Configuration form (connected)

| Control | Bound to | Behaviour |
|---|---|---|
| **Printer** select | `printer` | Options from `listPrinters()`: `"{name} — {Receipt printer / Printer / Virtual}{ · default}{ · offline}"`. On change also set `mode = printer.suggestedMode`, and `paperWidth = 80` for thermal / `0` (full page) otherwise. Hint under it: status badge + driver + port. If the saved printer is no longer in the list, show a red hint "isn't installed on {machine} any more — pick another". |
| **Print as** select | `mode` | `escpos` "Receipt printer (ESC/POS)" — fast, native QR · `graphic` "Any printer (driver)" |
| **Paper width** select | `paperWidth` | 80 mm, 58 mm, and (graphic only) "Full page (A4 / Letter)" = 0 |
| **Copies** number | `copies` | 1–5 |
| Toggle | `enabled` | "Print receipts through Print Agent (otherwise the browser print dialog is used)" |
| Toggle | `autoPrint` | "Print instantly when a receipt or ticket is created" (disabled when `enabled` is off) |

Footer row, always visible: **Agent address** input (default `http://127.0.0.1:9311`; saved on
Enter / Check) · **Refresh printers / Check again** · **Test print** (`printTestPage()`, toast the
result) · **Disconnect** (`disconnectAgent()`).

`appName` passed to `pairAgent` = the product/business name; it's what the PC user sees in the
Allow prompt and in the agent's *Connected apps* list.

### React reference implementation

Production version from the PlayRoom app (`client/react/PrinterAgentSettings.tsx`). Replace the
imports from `./ui` (`Badge, Button, Card, Field, Input, Select, Toggle, toast, Tone`) and the
Tailwind classes with the target app's design system; keep the logic. Note the `probe()` helper:
the mount effect only sets state inside the promise callback (satisfies
`react-hooks/set-state-in-effect`).

```tsx
"use client";

import { useCallback, useEffect, useState, useSyncExternalStore } from "react";
import { Plug, Printer, RefreshCw, Unplug } from "lucide-react";
import {
  AGENT_DEFAULTS,
  agentStatus,
  disconnectAgent,
  getAgentSettings,
  listPrinters,
  pairAgent,
  printTestPage,
  saveAgentSettings,
  subscribeAgentSettings,
  type AgentPrinter,
  type AgentStatus,
} from "@/lib/printAgent";
import { Badge, Button, Card, Field, Input, Select, Toggle, toast, type Tone } from "./ui";

/** This computer's Print Agent connection (stored in localStorage, re-renders on change). */
export function useAgentSettings() {
  return useSyncExternalStore(subscribeAgentSettings, getAgentSettings, () => AGENT_DEFAULTS);
}

type Phase = "checking" | "offline" | "unpaired" | "paired";

const STATUS_TONE: Record<AgentPrinter["status"], Tone> = { ready: "mint", printing: "sky", offline: "rose", error: "rose", unknown: "gray" };
const KIND_LABEL: Record<AgentPrinter["kind"], string> = { thermal: "Receipt printer", standard: "Printer", virtual: "Virtual" };

/** Where this PC stands with the agent: not running, running but not connected, or connected. */
async function probe(): Promise<{ phase: Phase; info: AgentStatus | null; printers: AgentPrinter[] }> {
  try {
    const info = await agentStatus();
    if (!info.paired) {
      if (getAgentSettings().token) saveAgentSettings({ token: null });
      return { phase: "unpaired", info, printers: [] };
    }
    return { phase: "paired", info, printers: await listPrinters() };
  } catch {
    return { phase: "offline", info: null, printers: [] };
  }
}

export function PrinterAgentSettings({ appName }: { appName: string }) {
  const s = useAgentSettings();
  const [phase, setPhase] = useState<Phase>("checking");
  const [info, setInfo] = useState<AgentStatus | null>(null);
  const [printers, setPrinters] = useState<AgentPrinter[]>([]);
  const [url, setUrl] = useState<string | null>(null); // edited address, null = saved one
  const [busy, setBusy] = useState<"" | "pair" | "test" | "refresh">("");

  const check = useCallback(async () => {
    const r = await probe();
    setInfo(r.info);
    setPrinters(r.printers);
    setPhase(r.phase);
  }, []);

  useEffect(() => {
    let alive = true;
    probe().then((r) => {
      if (!alive) return;
      setInfo(r.info);
      setPrinters(r.printers);
      setPhase(r.phase);
    });
    return () => {
      alive = false;
    };
  }, []);

  const applyUrl = async () => {
    if (url !== null) saveAgentSettings({ url: url.trim() || AGENT_DEFAULTS.url });
    setUrl(null);
    setBusy("refresh");
    await check();
    setBusy("");
  };

  const pair = async () => {
    setBusy("pair");
    try {
      await pairAgent(appName);
      toast("Connected to Print Agent");
      await check();
    } catch (e) {
      toast((e as Error).message, "err");
    }
    setBusy("");
  };

  const refresh = async () => {
    setBusy("refresh");
    await check();
    setBusy("");
  };

  const choose = (name: string) => {
    const p = printers.find((x) => x.name === name);
    if (!p) return saveAgentSettings({ printer: null });
    // thermal printers take ESC/POS on a roll; anything else prints through its driver at full page width
    saveAgentSettings({ printer: p.name, mode: p.suggestedMode, paperWidth: p.kind === "thermal" ? s.paperWidth || 80 : 0 });
  };

  const test = async () => {
    setBusy("test");
    try {
      const r = await printTestPage();
      toast(`Test page sent to ${r.printer}`);
    } catch (e) {
      toast((e as Error).message, "err");
    }
    setBusy("");
  };

  const disconnect = async () => {
    await disconnectAgent();
    setPrinters([]);
    setPhase("unpaired");
    toast("Disconnected from Print Agent");
  };

  const selected = printers.find((p) => p.name === s.printer);
  const status: { tone: Tone; label: string } =
    phase === "checking" ? { tone: "gray", label: "Checking…" }
    : phase === "offline" ? { tone: "rose", label: "Agent not found" }
    : phase === "unpaired" ? { tone: "sun", label: "Not connected" }
    : !s.printer ? { tone: "sun", label: "Choose a printer" }
    : { tone: "mint", label: "Ready" };

  return (
    <Card title="Receipt printer · Print Agent" action={<Badge tone={status.tone}>{status.label}</Badge>}>
      <div className="space-y-4 text-sm">
        <p className="text-muted">
          Prints receipts and tickets straight to a printer on this computer — no print dialog. Needs <b className="text-ink">Bloom Print Agent</b> running on this PC.
          These settings are saved on this computer only, so set up each front-desk PC once.
        </p>

        {phase === "offline" && (
          <div className="rounded-xl border border-dashed border-[#e7a3b3] bg-rose-soft p-4 text-[#a63a54]">
            <div className="font-bold">Print Agent isn&apos;t answering at {s.url}</div>
            <ol className="mt-1.5 list-decimal space-y-0.5 pl-5 text-xs">
              <li>Run <b>BloomPrintAgent.exe</b> on this computer (it sits in the system tray).</li>
              <li>If the browser asks to access devices on your local network, choose <b>Allow</b>.</li>
              <li>Changed the agent&apos;s port? Update the address below, then check again.</li>
            </ol>
          </div>
        )}

        {phase === "unpaired" && info && (
          <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-line bg-[#fcfbf8] p-4">
            <div>
              <div className="font-bold">Found {info.agent} {info.version} on {info.machine}</div>
              <div className="text-xs text-muted">
                {busy === "pair" ? "Click Allow on the Print Agent window on this computer…" : "Connect to let this app see and use its printers."}
              </div>
            </div>
            <Button icon={<Plug size={15} />} onClick={pair} disabled={busy === "pair"}>
              {busy === "pair" ? "Waiting for approval…" : "Connect"}
            </Button>
          </div>
        )}

        {phase === "paired" && (
          <>
            <div className="grid gap-4 sm:grid-cols-2">
              <Field
                label="Printer"
                className="sm:col-span-2"
                hint={
                  selected ? (
                    <span className="inline-flex items-center gap-2">
                      <Badge tone={STATUS_TONE[selected.status]}>{selected.status}</Badge>
                      {selected.driver} · {selected.port}
                    </span>
                  ) : s.printer ? (
                    <span className="text-[#c24f69]">“{s.printer}” isn&apos;t installed on {info?.machine} any more — pick another.</span>
                  ) : (
                    `Printers installed on ${info?.machine}.`
                  )
                }
              >
                <Select value={selected ? selected.name : ""} onChange={(e) => choose(e.target.value)}>
                  <option value="">Select a printer…</option>
                  {printers.map((p) => (
                    <option key={p.name} value={p.name}>
                      {p.name} — {KIND_LABEL[p.kind]}{p.isDefault ? " · default" : ""}{p.status === "offline" ? " · offline" : ""}
                    </option>
                  ))}
                </Select>
              </Field>
              <Field label="Print as" hint={s.mode === "escpos" ? "Fast, native QR codes. For thermal receipt printers." : "Through the Windows driver. Works on any printer."}>
                <Select value={s.mode} onChange={(e) => saveAgentSettings({ mode: e.target.value as "escpos" | "graphic" })}>
                  <option value="escpos">Receipt printer (ESC/POS)</option>
                  <option value="graphic">Any printer (driver)</option>
                </Select>
              </Field>
              <Field label="Paper width">
                <Select value={s.paperWidth} onChange={(e) => saveAgentSettings({ paperWidth: Number(e.target.value) })}>
                  <option value={80}>80 mm roll</option>
                  <option value={58}>58 mm roll</option>
                  {s.mode === "graphic" && <option value={0}>Full page (A4 / Letter)</option>}
                </Select>
              </Field>
              <Field label="Copies">
                <Input type="number" min={1} max={5} value={s.copies} onChange={(e) => saveAgentSettings({ copies: Math.max(1, Math.min(5, Number(e.target.value) || 1)) })} />
              </Field>
            </div>
            <div className="space-y-2.5">
              <Toggle checked={s.enabled} onChange={(v) => saveAgentSettings({ enabled: v })} label="Print receipts through Print Agent (otherwise the browser print dialog is used)" />
              <Toggle checked={s.autoPrint} disabled={!s.enabled} onChange={(v) => saveAgentSettings({ autoPrint: v })} label="Print instantly when a receipt or ticket is created" />
            </div>
          </>
        )}

        <div className="flex flex-wrap items-end gap-2 border-t border-line pt-4">
          <Field label="Agent address" className="w-full sm:w-64">
            <Input value={url ?? s.url} onChange={(e) => setUrl(e.target.value)} onKeyDown={(e) => e.key === "Enter" && applyUrl()} />
          </Field>
          <Button variant="secondary" icon={<RefreshCw size={14} className={busy === "refresh" ? "animate-spin" : ""} />} onClick={url !== null ? applyUrl : refresh} disabled={!!busy}>
            {phase === "paired" ? "Refresh printers" : "Check again"}
          </Button>
          {phase === "paired" && (
            <>
              <Button variant="soft" icon={<Printer size={14} />} onClick={test} disabled={!s.printer || !!busy}>Test print</Button>
              <Button variant="ghost" icon={<Unplug size={14} />} onClick={disconnect} className="ml-auto">Disconnect</Button>
            </>
          )}
        </div>
      </div>
    </Card>
  );
}
```

**Other stacks:** Vue — hold `getAgentSettings()` in a `ref`, update it from
`subscribeAgentSettings` in `onMounted` and unsubscribe in `onUnmounted`. Angular — a service
exposing a `BehaviorSubject` fed by `subscribeAgentSettings`. Server-rendered / vanilla — render
the card markup and run the same state machine in a `<script type="module">`.

## 5. Step 3 — print documents (content blocks)

For each printable thing (sale receipt, ticket, invoice summary, kitchen order, label…) add a
**builder function** next to the existing on-screen receipt that returns `PrintBlock[]` from the
same data. Keep the two in step — leave a comment in both places.

| Block | Fields | Notes |
|---|---|---|
| `text` | `text`, `align`, `bold`, `underline`, `size` 1–4 | Wraps automatically. `size: 2` for the shop name / big numbers |
| `row` | `left`, `right`, `bold`, `size` 1–2 | Label + amount on one line; long labels wrap, amount stays right |
| `hr` | `char` | Divider line |
| `feed` | `lines` | Blank lines |
| `qr` | `data`, `size` 1–16 (6–8 suits 80 mm), `ecc` | Native on thermal; printed as plain text in graphic mode |
| `barcode` | `data`, `height`, `width`, `hri` | CODE128 |
| `image` | `data` base64/data URL, `width` (dots: 576 = full 80 mm), `align` | Logos, non-Latin text rendered to a canvas |
| `cut` / `drawer` | – | Cut happens automatically at the end; add `drawer` (or `openDrawer: true`) for cash sales |
| `raw` | `data` base64 | Escape hatch for printer-specific bytes |

Line width: 48 characters on 80 mm, 32 on 58 mm (the agent wraps for you). Text prints in code
page 437: keep important text ASCII; curly quotes/dashes/`…`/`×` are auto-replaced. For Sinhala,
Tamil etc. draw that text on a `<canvas>` and send it as an `image` block.

Example (sale receipt):

```ts
import type { PrintBlock } from "@/lib/printAgent";

const money = (v: number) => v.toLocaleString("en-LK", { minimumFractionDigits: 2 });

export function saleDoc(sale: Sale, shop: Shop): PrintBlock[] {
  return [
    { type: "text", text: shop.name, align: "center", bold: true, size: 2 },
    { type: "text", text: shop.address, align: "center" },
    { type: "text", text: shop.phone, align: "center" },
    { type: "hr" },
    { type: "row", left: "Receipt", right: sale.no },
    { type: "row", left: "Date", right: formatDateTime(sale.at) },
    { type: "hr" },
    ...sale.items.flatMap((i): PrintBlock[] => [
      { type: "text", text: i.name },
      { type: "row", left: `  ${i.qty} x ${money(i.price)}`, right: money(i.qty * i.price) },
    ]),
    { type: "hr" },
    { type: "row", left: "TOTAL", right: money(sale.total), bold: true },
    { type: "row", left: "Cash", right: money(sale.tendered) },
    { type: "row", left: "Change", right: money(sale.tendered - sale.total) },
    { type: "feed", lines: 1 },
    { type: "qr", data: sale.no, size: 6 },
    { type: "text", text: shop.footer, align: "center", bold: true },
  ];
}
```

## 6. Step 4 — route printing through the agent

Find every place that prints a receipt (search for `window.print`, "Print" buttons, receipt
modals). Centralise it in the existing print modal/helper if there is one; otherwise add a small
helper:

```ts
import { isAgentReady, printDocument, type PrintBlock } from "@/lib/printAgent";

/** Prints via Print Agent when this PC is set up, otherwise the old way. */
export async function printReceipt(build: () => PrintBlock[], title: string, fallback: () => void) {
  if (!isAgentReady()) return fallback(); // e.g. window.print()
  try {
    const r = await printDocument(build(), { title });
    toast(`Printed on ${r.printer}`);
  } catch (e) {
    toast(`${(e as Error).message} Use Browser print instead.`, "error");
  }
}
```

React print-modal pattern (what PlayRoom does): the modal takes `doc?: () => PrintBlock[]` and
`autoPrint?: boolean`.

```tsx
const agent = useAgentSettings(); // re-renders when settings change
const direct = !!doc && isAgentReady(agent);
const [sending, setSending] = useState(false);
const autoSent = useRef(false);

const send = useCallback(async () => {
  if (!doc) return;
  setSending(true);
  try {
    const r = await printDocument(doc(), { title });
    toast(`Printed on ${r.printer}`);
  } catch (e) {
    toast(`${(e as Error).message} Use Browser print instead.`, "err");
  }
  setSending(false);
}, [doc, title]);

// instant print, once per opened modal
useEffect(() => {
  if (!open || !direct || !autoPrint || !agent.autoPrint || autoSent.current) return;
  autoSent.current = true;
  queueMicrotask(send);
}, [open, direct, autoPrint, agent.autoPrint, send]);

// footer
{direct && <Button variant="ghost" onClick={() => window.print()}>Browser print</Button>}
<Button onClick={direct ? send : () => window.print()} disabled={sending}>{sending ? "Printing…" : "Print"}</Button>
```

Call sites: `<PrintModal doc={() => saleDoc(sale)} autoPrint …>` where a sale/ticket was just
created or a reprint was requested; `doc` without `autoPrint` where the user is only viewing.

Cash drawer: pass `{ openDrawer: true }` to `printDocument` for cash sales if the product has a
drawer.

## 7. Acceptance checklist

- [ ] `printAgent.ts` copied unchanged; nothing calls the agent from server code.
- [ ] Settings shows the section with all four states; agent stopped → "Agent not found" with steps.
- [ ] Connect → Allow on the PC → printers listed; Deny → error toast, still "Not connected".
- [ ] Choosing a thermal printer sets ESC/POS + 80 mm; a laser/inkjet sets driver + full page.
- [ ] Test print works; settings survive a page reload and are **not** saved to the server.
- [ ] Completing a sale prints instantly, once (no duplicate in dev/StrictMode); viewing an old receipt does not auto-print.
- [ ] Agent stopped mid-shift → Print shows an error toast and "Browser print" still works.
- [ ] Disconnect in the agent's *Connected apps* tab → next refresh shows "Not connected".
- [ ] Typecheck and lint pass.

## 8. Troubleshooting

| Symptom | Fix |
|---|---|
| "Agent not found" but the tray icon is there | Browser blocked local network access: padlock → Site settings → *Local network access* → Allow, reload. Check the port in the agent's Settings tab matches the address. |
| Connect waits, then "declined" | The Allow popup is on the PC's screen (maybe behind other windows) — click Allow within 60 s. |
| Garbage symbols / nothing on a laser printer | Mode is ESC/POS on a non-thermal printer → switch "Print as" to *Any printer (driver)*. |
| Thermal printer prints but no QR | Very old printers lack native QR — send the QR as an `image` block instead. |
| `PRINT_FAILED` on a thermal printer | Its driver doesn't accept RAW data: use *Any printer (driver)*, or install the vendor's ESC/POS driver (or "Generic / Text Only") on the printer's port. |
| Printer shows "offline" | Windows thinks it's offline — check cable/power, or untick *Use printer offline* in Windows. |
