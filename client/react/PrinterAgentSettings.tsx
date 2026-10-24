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
