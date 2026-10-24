# Bloom Print Agent

A small Windows tray app (80 KB, no installer, no runtime to install) that lets **any web app**
print straight to the printers installed on the PC — no browser print dialog, instant receipts.

Browsers can't reach system printers. The agent runs on each PC and exposes a local API on
`http://127.0.0.1:9311`. A web app connects once (the person at the PC clicks **Allow**), lists
the PC's printers, the user picks one in the app's settings, and from then on every receipt goes
straight to that printer.

```
 Web app (any domain, in the browser)            This PC
 ┌─────────────────────────────┐   fetch   ┌──────────────────────────┐   spooler  ┌──────────┐
 │ Settings → Receipt printer  │ ────────► │ Bloom Print Agent (tray) │ ─────────► │ Printers │
 │ Print button / auto-print   │ 127.0.0.1 │ pairing · ESC/POS · GDI  │  RAW / GDI │ USB, LAN │
 └─────────────────────────────┘   :9311   └──────────────────────────┘            └──────────┘
```

- **Adding it to a web app?** Read [PRINT-AGENT-SETTINGS-INTEGRATION.md](./PRINT-AGENT-SETTINGS-INTEGRATION.md)
  — written so an AI coding tool can add the settings section and direct printing to any product.
- Client library to copy into web apps: [`client/printAgent.ts`](./client/printAgent.ts)
- React reference settings section: [`client/react/PrinterAgentSettings.tsx`](./client/react/PrinterAgentSettings.tsx)

---

## Install on a PC

1. Run **`dist\BloomPrintAgent-Setup-<version>.exe`** → **Install**. No administrator rights needed
   (installs for the current Windows user into `%LOCALAPPDATA%\Programs\Bloom Print Agent`).
   It adds Start menu (and optional desktop) shortcuts and an entry in *Settings → Apps → Installed
   apps* for uninstalling. Running a newer setup updates in place and keeps settings and connected apps.
   - Unattended: `BloomPrintAgent-Setup-<version>.exe /silent` · uninstall: `Uninstall.exe /uninstall /silent` (add `/purge` to also delete settings).
   - `dist\BloomPrintAgent.exe` also runs on its own as a portable copy, if you'd rather not install.
2. The window opens and an icon appears in the system tray. Closing the window only hides it.
3. **Printers** tab → select the receipt printer → **Test print** to check it works.
4. In the web app: Settings → Receipt printer → **Connect** → click **Allow** on the agent popup →
   choose the printer.

### Starts automatically, stays running

With *Start automatically* on (default), two independent mechanisms keep it up — neither needs admin:

| Mechanism | Covers |
|---|---|
| `HKCU\…\Run` entry | Starts at sign-in after every restart |
| Scheduled task **BloomPrintAgent Keep Running** — at sign-in + every 2 min, runs `--keepalive` | Crashes or accidental kills (back within 2 min); sign-in start even if the Run entry is disabled in Task Manager's Startup tab |

`--keepalive` exits immediately when the agent is already running, so the task is invisible.
Tray menu → **Exit** (asks to confirm) is respected: the task won't bring it back until it's started
from the Start menu or the computer restarts. On every start the agent repairs a missing Run entry
or task. Turning *Start automatically* off in Settings removes both.

**Unattended POS PCs:** the agent runs in the signed-in user's session (it needs that user's
printers), so after a restart someone has to sign in. For terminals that must recover alone after a
power cut, turn on Windows automatic sign-in for the POS account (Sysinternals *Autologon*, or
`netplwiz` → untick "Users must enter a user name and password") and enable *Restore on AC power
loss* in the BIOS.

Requirements: Windows 10 or 11 (uses .NET Framework 4.8, which is built in). The printer must be
installed in Windows (its driver installed, visible in *Settings → Printers & scanners*). Network
printers work once added to Windows.

SmartScreen may warn about an unsigned app on first run — **More info → Run anyway**. Sign the exe
with a code-signing certificate to remove that.

## The window

| Tab | What it's for |
|---|---|
| Printers | Every printer Windows knows, with type (thermal / standard / virtual), status, driver and port. Test-print as ESC/POS or through the driver. |
| Connected apps | Websites allowed to print here, when they connected and last printed. **Disconnect** revokes one. |
| Settings | Port (default 9311), start with Windows, ask before new websites connect. |
| Log | Every print, pairing and error. Also in `%APPDATA%\BloomPrintAgent\agent.log`. |

Config lives in `%APPDATA%\BloomPrintAgent\config.json`.

## Security model

- Listens on **loopback only** (127.0.0.1 / ::1) — other machines on the network can't reach it.
- A website must **pair** first: `POST /v1/pair` pops up *"<App> (https://site) wants to print —
  Allow / Deny"* on the PC (auto-denies after 60 s). Approval issues a random token.
- Tokens are **bound to the website's origin**: a token copied to another site is rejected.
- Requests whose `Host` isn't 127.0.0.1 / localhost / [::1] are rejected (blocks DNS-rebinding).
- *Ask before new websites connect* can be turned off in Settings for kiosk setups — then any
  website could pair silently, so leave it on unless the PC only ever opens your app.

## Building

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

Uses the C# compiler that ships with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`),
so there is nothing to install. Output: `dist\BloomPrintAgent.exe`. The source is C# 5 (that
compiler's language level — no `$"..."` strings, `?.`, or `=>` members). The icon is drawn in code
(`src/Icons.cs`) and exported to `assets\app.ico` on the first build; delete it to regenerate.

| File | Purpose |
|---|---|
| `src/Program.cs` | Entry point, single instance, tray icon, auto-start, log |
| `src/HttpServer.cs` | Minimal HTTP/1.1 server on loopback (CORS + Private Network Access headers) |
| `src/Api.cs` | Routes, pairing, auth |
| `src/Config.cs` | Settings + pairings in `%APPDATA%` |
| `src/Printers.cs` | Printer discovery (WMI), job parsing, RAW spooler output |
| `src/EscPos.cs` | Content blocks → ESC/POS bytes (text, rows, QR, CODE128, raster images, cut, drawer) |
| `src/GraphicPrinter.cs` | Content blocks → GDI drawing for any printer, with pagination |
| `src/Ui.cs` | Main window and the pairing prompt |

Releasing a new version: bump `Version` in `shared/AppInfo.cs`, run `build.ps1`, and ship
`dist\BloomPrintAgent-Setup-<version>.exe` — running it on a PC stops the old agent, updates it and
starts the new one. `installer/Setup.cs` is the installer; it embeds the agent exe at build time.

---

## API reference (v1)

Base URL `http://127.0.0.1:9311`. JSON in and out. Every response has `"ok": true|false`; errors
also carry `code` and a human-readable `error`. Authenticated endpoints need
`Authorization: Bearer <token>` from a page on the same origin that paired.

| Method & path | Auth | Purpose |
|---|---|---|
| `GET /v1/status` | – | Is the agent running? `{agent, version, api, machine, paired, requireApproval}` — `paired` is true when the sent token is valid for this origin |
| `POST /v1/pair` | – | Body `{app: "Display name"}`. Blocks until the person allows/denies (≤ 60 s). → `{token, machine}` |
| `GET /v1/printers` | ✓ | → `{printers: [{name, isDefault, status, driver, port, isNetwork, isShared, kind, suggestedMode}]}` |
| `POST /v1/print` | ✓ | Print a job (below). → `{printer, mode, jobId, bytes}` |
| `POST /v1/print/test` | ✓ | Built-in test page. Body: `{printer?, mode?, paperWidth?}` |
| `POST /v1/unpair` | ✓ | Forget this website's token |

`status`: `ready | printing | offline | error | unknown` (as reported by Windows — a USB printer that's
switched off often still shows `ready`). `kind`: `thermal | standard | virtual` (PDF/XPS/OneNote/Fax),
guessed from the name and driver. `suggestedMode`: `escpos` for thermal, otherwise `graphic`.

### Print job

```jsonc
{
  "printer": "POS-80",        // Windows printer name; omit for the Windows default printer
  "title": "Receipt R-1001",  // shown in the Windows print queue
  "mode": "escpos",           // escpos (thermal, native QR) | graphic (any printer, via driver)
  "paperWidth": 80,           // mm: 80 → 48 chars/576 dots, 58 → 32 chars/384 dots; 0 = full page (graphic)
  "columns": 48,              // optional override of characters per line (escpos)
  "copies": 1,                // 1–10
  "cut": true,                // cut paper at the end (escpos)
  "openDrawer": false,        // kick the cash drawer first (escpos)
  "fontSize": 9,              // base font size in pt (graphic)
  "content": [ /* blocks */ ] // or "text": "plain text" or "raw": "<base64 bytes>"
}
```

### Content blocks

| Block | Fields | escpos | graphic |
|---|---|---|---|
| `text` | `text`, `align` (left/center/right), `bold`, `underline`, `size` 1–4 | ✓ wraps | ✓ wraps |
| `row` | `left`, `right`, `bold`, `size` 1–2 — label left, amount right | ✓ | ✓ |
| `hr` | `char` (default `-`) | ✓ | dashed line |
| `feed` | `lines` | ✓ | ✓ |
| `qr` | `data`, `size` 1–16 (module size, default 6), `ecc` L/M/Q/H, `align` | ✓ native | prints the value as text |
| `barcode` | `data` (CODE128), `height`, `width`, `hri`, `align` | ✓ native | prints the value as text |
| `image` | `data` (base64 PNG/JPEG or data URL), `width` in dots, `align` | ✓ raster | ✓ |
| `cut` | – | ✓ | ignored |
| `drawer` | – | ✓ | ignored |
| `raw` | `data` base64 — bytes inserted as-is | ✓ | ignored |

Text is converted to code page 437; curly quotes, dashes, `…`, `×` etc. are replaced with ASCII so
printers never show garbage. For other scripts (Sinhala, Tamil…) render that part as an `image`.

### Error codes

| HTTP | `code` | Meaning |
|---|---|---|
| 400 | `BAD_REQUEST` | Body isn't JSON / no content / unknown block type |
| 401 | `UNAUTHORIZED` | No/invalid token, or token belongs to another website → pair again |
| 403 | `PAIRING_DENIED` | Person clicked Deny or didn't answer in 60 s |
| 404 | `PRINTER_NOT_FOUND` | Printer isn't installed on this PC |
| 421 | `BAD_HOST` | Request not addressed to 127.0.0.1/localhost |
| 502 | `PRINT_FAILED` | Windows refused the job (message says why) |

### Quick test from PowerShell

```powershell
curl.exe http://127.0.0.1:9311/v1/status
```
