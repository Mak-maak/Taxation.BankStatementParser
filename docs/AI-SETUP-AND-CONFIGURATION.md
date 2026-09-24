# AI Models Setup & Tool Configuration Guide

This guide explains, step by step, how to set up the **local, offline AI model** used by the
Bank Statement Parser, and how to enable, disable, and tune every setting of the tool.

> **Privacy first.** All AI processing runs **on your own machine** over the loopback interface
> (`localhost`). No statement data is ever sent to any cloud or remote server. The tool will
> **refuse** to run AI if the configured endpoint is not a loopback address (`localhost`,
> `127.0.0.1`, or `::1`).

---

## Table of Contents

1. [How the tool works (overview)](#1-how-the-tool-works-overview)
2. [Prerequisites](#2-prerequisites)
3. [Step-by-step: install and run the local AI model](#3-step-by-step-install-and-run-the-local-ai-model)
   - [3.1 Install the Ollama runtime](#31-install-the-ollama-runtime)
   - [3.2 Start the Ollama service](#32-start-the-ollama-service)
   - [3.3 Download (pull) the vision model](#33-download-pull-the-vision-model)
   - [3.4 Verify the model is running](#34-verify-the-model-is-running)
4. [Enable / disable AI in the tool](#4-enable--disable-ai-in-the-tool)
5. [All settings explained (`appsettings.json`)](#5-all-settings-explained-appsettingsjson)
6. [Choosing a model & hardware requirements](#6-choosing-a-model--hardware-requirements)
7. [Verifying it works end to end](#7-verifying-it-works-end-to-end)
8. [Troubleshooting](#8-troubleshooting)
9. [Uninstall / roll back](#9-uninstall--roll-back)

---

## 1. How the tool works (overview)

The parser has **two engines**:

1. **Geometric parser (primary, always on).** Reads the PDF text/table layout directly. Fast and
   accurate for normal, text-based PDF statements. Requires no AI.
2. **Local AI extractor (optional fallback).** Uses a local **vision model** to "read" each page
   as an image. This helps with **scanned / photographed statements** or PDFs where the geometric
   parser cannot detect the columns.

By default, **AI is disabled** and the tool behaves exactly as before. When you enable it, AI is
used only as a *fallback* (or *always*, if you choose) and its output is **validated** (balance
reconciliation) before being accepted — so a bad AI read is automatically rejected in favor of the
geometric result.

---

## 2. Prerequisites

| Requirement | Details |
|---|---|
| Operating system | Windows 10/11, macOS, or Linux |
| Disk space | ~5–8 GB free (the vision model files are large) |
| Memory (RAM) | 8 GB minimum; **16 GB recommended** for `llama3.2-vision` |
| GPU (optional) | Not required, but an NVIDIA/Apple-silicon GPU makes extraction much faster |
| Runtime | **Ollama** (the local model runtime — installed in Step 3) |

> **Note:** The parser tool itself already runs on **.NET 10** and needs no extra .NET runtime for AI.
> The only new runtime you install is **Ollama**, which hosts the model locally.

---

## 3. Step-by-step: install and run the local AI model

The tool talks to a local **Ollama** server on `http://localhost:11434`. Ollama is the runtime that
downloads and serves the vision model entirely on your machine.

### 3.1 Install the Ollama runtime

**Where to download:** the official site — **https://ollama.com/download**

Pick the installer for your OS:

- **Windows** — download **`OllamaSetup.exe`** from https://ollama.com/download/windows and run it.
  (Recommended version: **latest stable**, 0.3.x or newer, which is required for vision models.)
- **macOS** — download the **`Ollama.dmg`** from https://ollama.com/download/mac, open it, and drag
  Ollama to Applications.
- **Linux** — run the official install script:
  ```bash
  curl -fsSL https://ollama.com/install.sh | sh
  ```

> **Version requirement:** Vision models such as `llama3.2-vision` require **Ollama 0.3.0 or later**.
> Always prefer the **latest stable** release. Check your version with:
> ```powershell
> ollama --version
> ```

### 3.2 Start the Ollama service

- **Windows / macOS:** Ollama starts automatically after install and runs in the background
  (look for its icon in the system tray / menu bar). It listens on `http://localhost:11434`.
- **Linux / manual start:** run the server in a terminal:
  ```bash
  ollama serve
  ```

To confirm the service is up, open a browser to **http://localhost:11434** — you should see the text
`Ollama is running`.

### 3.3 Download (pull) the vision model

Open a terminal (PowerShell on Windows) and pull the default model used by the tool:

```powershell
ollama pull llama3.2-vision
```

This downloads the model **once** and stores it locally. The download is several GB, so allow time
on the first run. Alternative (smaller / larger) models are listed in
[Section 6](#6-choosing-a-model--hardware-requirements).

### 3.4 Verify the model is running

List the models installed locally:

```powershell
ollama list
```

You should see `llama3.2-vision` in the list. Optionally, do a quick smoke test:

```powershell
ollama run llama3.2-vision "Say hello in one short sentence."
```

If it replies, the runtime and model are ready. You can now enable AI in the tool.

---

## 4. Enable / disable AI in the tool

All AI behavior is controlled from **`appsettings.json`**, located **next to the tool's executable**
(the same folder as `Taxation.StatementParser.Console.exe`). Edit the `Ai` section:

```jsonc
{
  "Ai": {
	"Enabled": "no"   // <-- change to "yes" to turn AI on
  }
}
```

- **To ENABLE AI:** set `"Enabled": "yes"`.
- **To DISABLE AI:** set `"Enabled": "no"` (the default). The tool then uses only the geometric
  parser and behaves exactly as before.

Save the file and re-run the tool — no rebuild needed. `Enabled` accepts friendly values:
`yes/no`, `true/false`, `1/0`, `on/off`, `y`.

> **Tip:** You do **not** need Ollama installed to run the tool with AI disabled. When AI is enabled
> but the local model is unavailable, the tool logs a message and silently keeps using the
> geometric result — it never crashes.

---

## 5. All settings explained (`appsettings.json`)

Below is the full configuration file with every option documented.

```jsonc
{
  // --- Excel output protection -------------------------------------------
  "ExcelProtection": {
	// "yes" password-protects the generated .xlsx; "no" leaves it open.
	"PasswordProtect": "no",
	// Password used to open the workbook when protection is enabled.
	"Password": "confidential_123"
  },

  // --- Local, offline AI extraction --------------------------------------
  "Ai": {
	// Master on/off switch. "no" (default) = geometric parser only.
	"Enabled": "no",

	// Local Ollama address. MUST be loopback (localhost/127.0.0.1/::1),
	// otherwise the tool refuses to run AI (privacy guarantee).
	"Endpoint": "http://localhost:11434",

	// The local vision model to use (must be pulled with `ollama pull`).
	"Model": "llama3.2-vision",

	// "fallback" = AI runs only for scans/images or when the geometric
	//              parser fails / produces an invalid result (recommended).
	// "always"   = AI runs for every statement.
	"Mode": "fallback",

	// Per-request timeout in seconds for the local model. Increase on slow
	// hardware or for large documents.
	"TimeoutSeconds": 180,

	// Sampling temperature. 0 = deterministic, repeatable extraction (best
	// for numeric accuracy). Leave at 0 unless experimenting.
	"Temperature": 0,

	// Safety cap on how many pages are sent to the model per document.
	"MaxPages": 40
  }
}
```

### Setting reference table

| Section | Key | Default | Accepted values | What it does |
|---|---|---|---|---|
| `ExcelProtection` | `PasswordProtect` | `no` | `yes`/`no`/`true`/`false`/`1`/`0`/`on`/`off` | Password-protect the exported Excel file. |
| `ExcelProtection` | `Password` | `confidential_123` | any string | Password used when protection is enabled. |
| `Ai` | `Enabled` | `no` | `yes`/`no`/`true`/`false`/`1`/`0`/`on`/`off` | Master switch for AI extraction. |
| `Ai` | `Endpoint` | `http://localhost:11434` | loopback URL only | Address of the local Ollama service. |
| `Ai` | `Model` | `llama3.2-vision` | any pulled Ollama model | Vision model used for extraction. |
| `Ai` | `Mode` | `fallback` | `fallback` / `always` | When AI runs relative to the geometric parser. |
| `Ai` | `TimeoutSeconds` | `180` | integer (min 5) | Max seconds per model request. |
| `Ai` | `Temperature` | `0` | number `0`–`1` | Model randomness; keep `0` for accuracy. |
| `Ai` | `MaxPages` | `40` | integer | Max pages sent to the model per document. |

---

## 6. Choosing a model & hardware requirements

Any Ollama **vision-capable** model works. Set its name in `Ai.Model` and `ollama pull` it first.

| Model | Pull command | Approx. size | RAM guidance | Notes |
|---|---|---|---|---|
| `llama3.2-vision` (default) | `ollama pull llama3.2-vision` | ~7.9 GB | 16 GB | Good accuracy; recommended default. |
| `llama3.2-vision:11b` | `ollama pull llama3.2-vision:11b` | ~7.9 GB | 16 GB | Explicit 11B tag of the above. |
| `llava` | `ollama pull llava` | ~4.7 GB | 8 GB | Lighter; faster on modest hardware. |
| `minicpm-v` | `ollama pull minicpm-v` | ~5.5 GB | 8–12 GB | Alternative vision model. |

Guidance:
- On machines with **8 GB RAM**, prefer a lighter model such as `llava`.
- A **GPU** dramatically speeds up extraction but is not required (CPU-only works, just slower —
  increase `TimeoutSeconds` if needed).

---

## 7. Verifying it works end to end

1. Ensure Ollama is running (`http://localhost:11434` shows "Ollama is running").
2. Ensure the model is pulled (`ollama list` shows your model).
3. In `appsettings.json`, set `"Ai": { "Enabled": "yes" }`.
4. Run the tool on a **scanned** statement (image or scanned PDF).
5. Watch the tool's log output. On success you should see a line similar to:
   ```
   AI extraction accepted: 3 transaction(s).
   ```
   If AI is unavailable, you'll instead see a message like
   `AI extractor is not available; keeping geometric parse result.` — and the tool still completes
   using the geometric parser.

---

## 8. Troubleshooting

| Symptom (log message) | Cause | Fix |
|---|---|---|
| `could not reach local Ollama at http://localhost:11434` | Ollama not running | Start it (`ollama serve`) or launch the Ollama app. |
| `model '…' is not pulled locally (run: ollama pull …)` | Model not downloaded | Run `ollama pull llama3.2-vision`. |
| `endpoint '…' is not a loopback address` | `Endpoint` points to a remote host | Set it back to `http://localhost:11434`. Remote endpoints are refused by design. |
| AI runs but results are rejected / geometric kept | AI output failed balance validation | This is expected safety behavior. Try a clearer scan, or `Mode: "always"` to force AI attempts. |
| Extraction times out | Slow CPU / large document | Increase `TimeoutSeconds` (e.g., `600`) or use a lighter model. |
| Port `11434` already in use | Another Ollama instance | Reuse the running instance, or stop the conflicting process. |

---

## 9. Uninstall / roll back

- **Turn off AI only:** set `"Enabled": "no"` in `appsettings.json`. The tool reverts to
  geometric-only parsing immediately.
- **Free disk space:** remove a model with `ollama rm llama3.2-vision`.
- **Remove the runtime:** uninstall Ollama via your OS (Windows: *Apps & features*; macOS: drag to
  Trash; Linux: remove the `ollama` package/binary).

Disabling AI or uninstalling Ollama never affects the core parser — it keeps working with the
geometric engine.
