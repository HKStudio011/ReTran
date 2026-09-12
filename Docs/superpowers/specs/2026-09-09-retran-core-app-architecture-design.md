# ReTran — Core/App Architecture Design

**Status:** Draft for review (M0 backbone agreed; follow-on milestones scoped)
**Date:** 2026-09-09
**Scope:** How the App (controller surface) and Core (headless engine) are split, how they talk, and where data lives. This spec is the contract that the M0 implementation plan argues from.

---

## 1. The two roles

| | **ReTran App** | **ReTran Core** |
|---|---|---|
| Analogy | Hermes Desktop (the surface) | Hermes Agent (the engine) |
| Has UI? | Yes — MAUI Blazor Hybrid WebView | No — headless process(es) |
| Owns persistence? | **Yes** — SQLite + EF Core + `settings.json` | No DB. Small own state in JSON/YAML only |
| Does real work? | No — orchestrates, displays, persists | Yes — capture, OCR, translation, OBS export |
| Languages | C# (shell) + TS/HTML (WebView) | C# (main) + Python (OCR sidecar) + Rust (hook DLL) |

The App is a **supervisor + client**: it launches the Core main process, drives it over IPC, renders results, and owns all durable data. The Core never writes to the App's database directly — it asks.

## 2. Process topology (one peer)

```
ReTran App (MAUI, C#)
   │  spawns + talks JSON-RPC over stdio (1 peer)
   ▼
ReTran.Core.exe --serve          ← MAIN CORE PROCESS (C#, headless console)
   ├─ Win32/DXGI capture          (screen / window)
   ├─ loads hook.dll (Rust cdylib)→ injected into the GAME process for exclusive fullscreen
   │      (game back buffer → shared memory; Core reads it)
   ├─ spawns Python OCR sidecar   (retran-core, long-lived, keeps model in VRAM)
   │        └─ JSON-RPC over stdio (same framing as App↔Core)
   └─ obs-websocket client        (export annotated frames)
```

Rules:
- **Exactly one IPC peer** between App and Core. The App never talks to the Python sidecar or the Rust hook directly — the C# main process mediates everything. This keeps lifecycle, error handling, and shutdown in one place.
- The **Python sidecar is long-lived**, spawned by Core at start (or on first OCR use) and kept warm so the model stays resident. It is NOT re-spawned per frame.
- The **Rust hook DLL is not a peer** — it lives inside the target game process and hands frames to Core via shared memory.

## 3. IPC contract: JSON-RPC 2.0 over stdio, newline-delimited

- One JSON-RPC 2.0 message per line on stdout (responses/notifications) / stdin (requests).
- **Transport is swappable, the message contract is not.** M0 uses stdio (no ports, no firewall, works from a terminal = also the CLI). If we later need multiple clients or out-of-band control, we swap the transport to a named pipe; method names and payloads stay identical.
- `Id` values are **strings** (we generate them) for simplicity across C#/Python.
- Standard JSON-RPC error codes: `-32700` parse, `-32600` invalid request, `-32601` method not found, `-32602` invalid params, `-32603` internal.

### Method namespaces (prefix = owner)
- `core.*` — handled by the C# main process (`core.ping`, `core.version`, `core.shutdown`, later `capture.*`, `translate.request_cache`, …)
- `sidecar.*` — handled by the Python OCR sidecar (`sidecar.ping`, later `ocr.spot`)
- Notifications (no `id`) are used for streaming results back to the App: e.g. `frame.ocr_result`, `frame.translated`.

### The diff/cache split (agreed)
Continuity lives in Core; persistence lives in App.
1. Core runs OCR each frame and diffs against its own last-seen text set → produces **new lines**.
2. For each new line Core sends the App a cache lookup: `core.cache_lookup {text, src, dst}` (or batched).
3. App answers with the cached translation, or `{cached:false}`; on miss Core calls the translator and the result is written to the DB **by the App** (App owns EF), then fed back for display.

This keeps "what's new" (pipeline state) in Core and "what we've seen before" (durable cache) in the App's DB, with no shared file and no second writer.

## 4. CLI one-shot mode (shares handlers with runtime)

The same C# main process is both the long-running peer **and** a CLI:

- `ReTran.Core.exe --serve` → run the JSON-RPC loop on stdio (the App uses this).
- `ReTran.Core.exe <subcommand> [args]` → one-shot, prints result to stdout and exits. Subcommands (`ocr`, `translate`, `capture`) **reuse the exact same handler functions** as the runtime — only the transport differs. This gives us scriptable/testable one-shots for free and a dev loop that needs no App.

M0 implements the dispatcher + `version`; the real subcommand handlers land with their own milestones (they depend on capture/OCR/translate existing).

## 5. Data & persistence

| Data | Owner | Format | Location |
|---|---|---|---|
| Translation cache (keyed by content context + text + src/dst) | **App** | SQLite / EF Core | `%LOCALAPPDATA%\ReTran\retran.db` |
| Process allowlist (injection consent) | **App** | SQLite / EF Core | same DB |
| App settings (provider keys, capture defaults, OBS config, `core.python` path) | **App** | JSON | `%LOCALAPPDATA%\ReTran\settings.json` |
| Core runtime state (last-seen text set, session scratch) | **Core** | JSON/YAML | `%LOCALAPPDATA%\ReTran\core\` |

EF migrations from day one (no raw schema scripts). Core never opens the DB file.

## 6. Folder layout (respecting what's already set up)

Existing: `ReTran App/` (MAUI C# + `vite-project/`) and `ReTran Core/` (Python project at its root: `pyproject.toml`, `.pyproj`). We keep Python where it is and add the two missing engines as siblings under Core.

```
ReTran Project/
├── ReTran App/                    # MAUI Blazor Hybrid — controller surface + DB + settings
│   └── (CoreProcessClient, EF DbContext, MainPage UI)
├── ReTran Core/                   # polyglot engine root
│   ├── pyproject.toml             # (existing) Python OCR sidecar — stays at root
│   ├── .venv/                     # uv-managed CPython 3.13 (gitignored)
│   ├── src/retran_core/           # python package: __main__ (JSON-RPC server), ocr/ …
│   ├── tests/                     # pytest for the sidecar
│   ├── core-cs/                   # NEW: ReTran.Core.csproj — headless C# main process (the peer)
│   │   └── src/…                  #    JsonRpc/, Capture/, OcrClient/, Translate/, Obs/, Cli/
│   └── hook/                      # NEW: Rust cdylib `retran-hook` (D3D11 Present hook)
│       └── Cargo.toml
└── Docs/superpowers/{specs,plans}/
```

## 7. Performance budget (hard requirement — carried into every milestone)

| Component | Budget |
|---|---|
| Capture (screen/window) | < 2% CPU |
| OCR (GPU, PP-OCR mobile default) | ~3 fps throttle, VRAM < 1 GB, drop frames if busy |
| Translation | diff-based, cached, fully async — never blocks pipeline |
| UI/overlay updates | 10–15 fps |
| OBS export | ~10 fps image-source updates |
| Total extra footprint | < 8% CPU, < 500 MB RAM while gaming + streaming |

Every milestone measures against this with a real game running and records numbers in `Docs/performance.md`. Nothing ships if it breaks budget without an approved tradeoff.

## 8. Assumptions to confirm (correct any before M0 starts)

- **A1 — Folder layout:** Core is polyglot; Python sidecar stays at `ReTran Core/` root, C# main process in `ReTran Core/core-cs/`, Rust hook in `ReTran Core/hook/`.
- **A2 — Main peer is a plain headless console exe** (`net10.0-windows`, not MAUI) — it's Windows-only by nature (DXGI/PrintWindow/injection).
- **A3 — Paths:** DB + settings under `%LOCALAPPDATA%\ReTran\`; Core scratch under `%LOCALAPPDATA%\ReTran\core\`.
- **A4 — Python version (resolved):** pyproject says `requires-python>=3.13`; system Python is 3.11.15 and `python` on PATH points at a non-project venv without pytest. Use `uv sync` in `ReTran Core/` — uv provisions CPython 3.13 into the repo-local `.venv`, system Python untouched (per AGENTS.md venv rule). M0 sidecar code stays stdlib-only regardless.
- **A5 — Solution entries (updated):** the old dangling `ReTran Core.csproj` entry was already replaced by the user with a link to `ReTran Core/ReTran Core.pyproj` (Python, Build=false). M0 adds a second entry for `ReTran Core/core-cs/ReTran.Core.csproj`; both stay.

## 9. Milestone decomposition (one plan each)

- **M0 — Core/App IPC backbone** ← *this first plan.* C# core process + JSON-RPC-over-stdio, Python sidecar spawn+ping, App `CoreProcessClient`, CLI dispatcher stub, config load, `.slnx` fix. End-to-end: App launches Core, Core spawns sidecar, all pingable.
- **M1 — Capture:** screen (DXGI) + window (PrintWindow) behind an `ICaptureSource`; frames into the pipeline; first budget numbers in `Docs/performance.md`.
- **M2 — OCR:** Python PP-OCR engine behind `OcrEngine` in the sidecar; `ocr.spot` method; Core diff logic.
- **M3 — Translation:** `ITranslator` providers (Google/DeepL/local/LLM) + the cache_lookup round-trip with the App's DB.
- **M4 — OBS export:** obs-websocket image-source updates, ~10 fps.
- **M5 — Rust hook DLL:** D3D11 Present hook, shared memory, injection consent flow (allowlist in App).
