# ReTran — Agent Instructions

## Project Overview

**ReTran** is a Windows desktop application that captures the screen, individual app windows, or game frames, runs OCR to locate text on the frame, translates it, and shows the result to the user. Annotated frames can be exported to OBS (e.g. as an image source updated over obs-websocket).

## Ngôn ngữ làm việc (Working Language)

- **Tiếng Việt là ngôn ngữ chính** cho mọi giao tiếp với user: báo cáo kết quả, giải thích, hỏi đáp trong session.
- Kế hoạch (plans trong `Docs/superpowers/plans/`) và tài liệu thiết kế/quyết định (`Docs/`) được viết bằng tiếng Việt.
- Code, tên file, API, log và doc comments giữ nguyên quy ước hiện có (tiếng Anh / song ngữ theo mục Doc Comments) — không dịch code sang tiếng Việt.

## Working Rules (mandatory)

- **NEVER edit code without explicit user permission.** Default mode is: analyze, explain, propose — the user decides and executes (or approves execution).
- Report findings concisely and directly. No over-engineered analysis.
- When asked "does X work / load?", verify empirically (run a small real experiment) and report measured numbers — not code-reading answers.
- When asked about a tool/dependency in this project, check project code only; do not install packages or probe system-wide unless asked.

## Repository Layout

```
ReTran Project/
├── ReTran Project.slnx        # solution: ReTran App + ReTran Core pyproj (no build) + core-cs (from M0)
├── ReTran App/                # .NET 10 MAUI Blazor Hybrid — controller surface / UI shell
│   ├── Components/            # Razor components (pages, layout)
│   ├── vite-project/          # Vite + TypeScript + Tailwind v4 frontend → builds to ../wwwroot/build
│   └── wwwroot/               # static assets + vite build output
├── ReTran Core/               # headless engine (NO UI), polyglot:
│   ├── pyproject.toml         # Python OCR sidecar project `retran-core` — uv-managed .venv (CPython 3.13)
│   ├── core-cs/               # C# main core process (ReTran.Core.csproj, net10.0-windows console) — created in M0
│   └── hook/                  # Rust cdylib for exclusive-fullscreen game capture — planned
└── Docs/                      # design notes, decisions; superpowers/specs + superpowers/plans
```

Current state: ReTran App is the MAUI Blazor Hybrid shell (template scaffold). ReTran Core is a Python project skeleton (`pyproject.toml` + `.pyproj`); the C# core process and Rust hook do not exist yet — the M0 plan (`Docs/superpowers/plans/2026-09-09-m0-core-ipc-backbone.md`) builds the JSON-RPC backbone.

## Tech Stack & Role of Each Language

| Language | Role | Status |
|---|---|---|
| **C#** (.NET 10) | App shell (MAUI Blazor Hybrid controller surface) + headless core process (`ReTran Core/core-cs`, net10.0-windows console): Windows capture interop, JSON-RPC peer, OCR sidecar client, translation orchestration, OBS WebSocket client; EF Core + SQLite persistence in the App | scaffolded (App) / M0 (core-cs) |
| **TypeScript / JavaScript** | UI logic in the WebView (overlay drawing, result display); built by Vite from `ReTran App/vite-project` | scaffolded |
| **HTML / CSS** | WebView markup and styling (Tailwind v4) | scaffolded |
| **Python** | OCR sidecar (`retran-core`): long-lived PaddleOCR process spawned by the C# core, JSON-RPC over stdio; local translation models later | scaffolded (pyproject + uv .venv) |
| **Rust** | Native hook DLL for exclusive fullscreen game capture (OBS-style D3D11 Present hook, injected into the game process) | planned — `cargo` not installed on dev machine yet |

## Product Pipeline

```
capture (screen / window / game) → OCR text spotting (boxes + text)
  → translation → display in app UI (annotated frame + translated text)
  → optional: export annotated frame to OBS
```

### Capture (3 modes, all required)

1. **Full screen** — DXGI Desktop Duplication.
2. **Window** — `PrintWindow`/BitBlt per HWND (covers borderless/windowed games too).
3. **Exclusive fullscreen game** — OBS-style GPU hook: a small native DLL injected into the game process hooks D3D11 `Present`, copies the back buffer to shared memory; the app reads it. The DLL is planned as a Rust cdylib (`retran-hook`); injection via `CreateRemoteThread` + `LoadLibrary`. The user must explicitly approve injection per target process (UI consent + allowlist in settings).

### OCR

Initial approach: **PaddleOCR** (https://github.com/PaddlePaddle/PaddleOCR) — specifically the **PP-OCR scene text spotting** pipeline (detection + recognition), e.g. PP-OCRv6 models. Notes:

- PaddleOCR-VL / PP-StructureV3 are for *document parsing* (PDFs, pages) — not the right tool for on-screen game/UI text spotting.
- Integration options (pick one when implementing, keep it swappable):
  1. Python sidecar process (long-lived, spawned by the C# core, JSON-RPC over stdio) — chosen; keeps the model resident in VRAM.
  2. ONNX Runtime inference in-process (C# or Rust) using exported PP-OCR models — lower latency for live frames.
- OCR engine must be behind an interface so the implementation can change later ("sau này có thể điều chỉnh").

### Translation

`ITranslator` interface with multiple swappable providers (user selects in UI; can mix per use):

- Google Translate official API (key)
- DeepL API (key)
- Local model (e.g. NLLB/m2m100 via ONNX Runtime — runs inside the Python sidecar alongside OCR)
- LLM via any OpenAI-compatible endpoint (Ollama, llama.cpp server, cloud)

Diff-based: only newly appearing text lines are sent to the translator. Results persist in SQLite (EF Core) **owned by the App** (Core performs cache lookups over IPC), keyed per content context — the game/app process name for window/hook capture, `screen:<monitor>` for full-screen capture — plus (text, src, dst). Translation must never block the capture/OCR pipeline.

### OBS Export

Export annotated frames to OBS via **obs-websocket** (update an image source, or feed a browser source). Keep frame rate modest; full 60fps streaming is not a first milestone.

## Data & Persistence

- **SQLite + EF Core** (`ReTranDbContext` in `ReTran App/`) is the application database, **owned by the App**: translation cache (per content context), process allowlist, anything needing queries or history. DB file: `%LOCALAPPDATA%\ReTran\retran.db`. Use EF migrations from day one — no raw schema scripts. Core never opens this file; it asks the App over IPC for cache lookups/writes.
- **JSON** for app settings (`settings.json` beside the DB): provider keys/endpoint, capture defaults, OBS config, core/python executable paths — small startup-read data stays out of the DB.
- **Core's own state** (last-seen text set, session scratch) is JSON under `%LOCALAPPDATA%\ReTran\core\` — no database in Core.

## Performance Budget (hard requirement)

The app runs **while the user is gaming and streaming** — every component must stay within budget:

| Component | Budget |
|---|---|
| Capture (screen/window) | < 2% CPU |
| OCR (GPU, PP-OCR mobile default) | throttled to ~3 fps, VRAM < 1 GB, drop frames if busy |
| Translation | diff-based (only new text), cached, fully async — never blocks pipeline |
| UI/overlay updates | throttled to 10–15 fps |
| OBS export | ~10 fps image source updates |
| Total extra footprint | < 8% CPU, < 500 MB RAM while gaming + streaming |

Every milestone must measure against this budget with a real game running and record numbers in `Docs/performance.md`. No feature ships if it breaks the budget without an approved tradeoff.

## Build & Run Commands

Verified on this machine (Windows, .NET SDK 10.0.400, Node v24, uv 0.11.x; system Python is 3.11 but the sidecar runs on uv-provisioned CPython 3.13):

```bash
# C# — build the whole solution (verified clean 2026-09-09)
dotnet build "ReTran Project.slnx"

# Frontend — from ReTran App/vite-project (output → ../wwwroot/build)
npm run dev      # watch mode
npm run build    # tsc && vite build

# Python sidecar — from ReTran Core/ (uv creates .venv with CPython 3.13; never use system/hermes Python)
uv sync
uv run pytest tests -v
```

- `ReTran Core/core-cs` is added to the `.slnx` by M0; until then build it directly: `dotnet build "ReTran Core/core-cs/ReTran.Core.csproj"`.
- Python work runs through `uv run` from `ReTran Core/` — do not pollute the system Python or use other venvs.

## Code Conventions

- C# projects: `Nullable` enabled, implicit usings enabled. Follow standard .NET naming.
- TypeScript: strict project via `tsconfig.json`; Tailwind v4 (CSS-first config, no tailwind.config.js needed for most cases).
- Keep the OCR/translation/capture layers behind small interfaces; no layer may depend on a concrete engine.
- Log through the DI-provided logger in C# (`Microsoft.Extensions.Logging`), not `Console.WriteLine`.

## Doc Comments

These instructions apply when creating, reviewing, or modifying source code in this repository.

**Project-specific convention (applies on top of the per-language rules below):** C# public API documentation is bilingual — first sentence in English (IDE tooltip), immediately followed by the Vietnamese equivalent:

```csharp
/// <summary>
/// Captures the next frame from the active source.
/// Chụp khung hình tiếp theo từ nguồn đang hoạt động.
/// </summary>
```

### General Rules

- Document all new or materially modified public APIs.
- Document protected APIs intended for inheritance or extension.
- Document private APIs only when their behavior, constraints, side effects, or design decisions are not obvious.
- Describe purpose, parameters, return values, errors, side effects, constraints, and usage when relevant.
- Keep the first sentence concise and suitable for IDE tooltips.
- Use complete sentences.
- Do not merely translate the implementation into prose.
- Do not repeat information already clear from the declaration or type signature.
- Never invent behavior that cannot be verified from code, tests, or requirements.
- Preserve the repository's existing documentation style when it is consistent.
- Update documentation whenever behavior, parameters, return values, errors, side effects, or constraints change.
- Remove or correct outdated documentation.
- Do not add documentation to unrelated code.

### C#

Use XML documentation comments beginning with `///`.

Document public and protected:

- Classes, records, structs, interfaces, enums, and delegates
- Constructors
- Methods and extension methods
- Properties and indexers
- Events
- Constants and public fields

Use these XML tags when applicable:

- `<summary>`
- `<param>`
- `<typeparam>`
- `<returns>`
- `<value>`
- `<exception>`
- `<remarks>`
- `<example>`
- `<inheritdoc/>`
- `<see cref="..."/>`

Example:

```csharp
/// <summary>
/// Retrieves a user by identifier.
/// </summary>
/// <param name="userId">The unique identifier of the user.</param>
/// <param name="cancellationToken">
/// A token that can be used to cancel the operation.
/// </param>
/// <returns>
/// The matching user, or <see langword="null"/> when no user exists.
/// </returns>
/// <exception cref="ArgumentException">
/// Thrown when <paramref name="userId"/> is empty.
/// </exception>
public Task<User?> GetUserAsync(
    Guid userId,
    CancellationToken cancellationToken = default);
```

Additional rules:

- Use `<inheritdoc/>` when inherited behavior is unchanged.
- Document exceptions callers are expected to handle.
- Do not use ordinary `//` comments as public API documentation.

### Python

Use PEP 257-compatible docstrings with triple double quotes.

Use Google-style docstrings unless the repository already consistently uses NumPy or Sphinx style.

Document:

- Public modules and packages
- Public classes
- Public functions and methods
- Public properties
- Complex internal functions whose behavior is not obvious

Use these sections when applicable:

- `Args:`
- `Returns:`
- `Yields:`
- `Raises:`
- `Examples:`
- `Note:`
- `Warning:`

Example:

```python
def load_user(user_id: str, *, use_cache: bool = True) -> User | None:
    """Load a user by identifier.

    Args:
        user_id: Unique identifier of the user. Must not be empty.
        use_cache: Whether cached data may be returned.

    Returns:
        The matching user, or `None` when no user exists.

    Raises:
        ValueError: If `user_id` is empty.
        RepositoryError: If the repository cannot be accessed.
    """
```

For a simple function, use a one-line docstring:

```python
def is_active(self) -> bool:
    """Return whether the account is active."""
```

Additional rules:

- Do not repeat types already expressed clearly by type annotations.
- Explain value ranges, formats, units, and constraints when relevant.
- Do not use `#` comments as replacements for public API docstrings.

### TypeScript

Use JSDoc comments beginning with `/**`.

Document:

- Exported functions
- Exported classes and public members
- Interfaces, types, enums, and constants
- Callbacks and event payloads
- Non-obvious internal APIs

Use these tags when applicable:

- `@param`
- `@returns`
- `@throws`
- `@example`
- `@deprecated`
- `@see`
- `@remarks`
- `@typeParam`

Example:

```ts
/**
 * Loads a user by identifier.
 *
 * @param userId - Unique identifier of the user.
 * @param useCache - Whether cached data may be returned.
 * @returns The matching user, or `undefined` when no user exists.
 * @throws If `userId` is empty.
 */
export async function loadUser(
  userId: string,
  useCache = true,
): Promise<User | undefined> {
  // ...
}
```

Additional rules:

- Do not duplicate TypeScript types inside JSDoc.
- Keep types in the TypeScript declaration.
- Use JSDoc to explain meaning, constraints, behavior, errors, and usage.

Avoid redundant documentation such as:

```ts
/**
 * @param {string} userId
 * @returns {Promise<User>}
 */
```

### JavaScript

Use JSDoc comments beginning with `/**`.

Because JavaScript declarations do not contain static types, include JSDoc type annotations when they improve type checking or IDE support.

Example:

```js
/**
 * Loads a user by identifier.
 *
 * @param {string} userId - Unique identifier of the user.
 * @param {boolean} [useCache=true] - Whether cached data may be returned.
 * @returns {Promise<User | undefined>} The matching user, if one exists.
 * @throws {TypeError} If `userId` is not a string.
 * @throws {Error} If `userId` is empty.
 */
export async function loadUser(userId, useCache = true) {
  // ...
}
```

Additional rules:

- Use `@typedef`, `@callback`, and `@template` when useful.
- Document optional parameters and default values.
- Keep declared JSDoc types synchronized with runtime behavior.

### C++

Use Doxygen-compatible documentation comments.

Prefer:

- `///` for short documentation
- `/** ... */` for longer documentation blocks

Document public:

- Classes and structs
- Constructors and destructors
- Functions and methods
- Templates
- Enums and enumerators
- Type aliases
- Public fields and constants
- Namespaces when their purpose is not obvious

Use these commands when applicable:

- `@brief`
- `@param`
- `@tparam`
- `@return`
- `@throws`
- `@note`
- `@warning`
- `@pre`
- `@post`
- `@see`

Example:

```cpp
/**
 * @brief Retrieves a user by identifier.
 *
 * @param user_id Unique identifier of the user. Must not be empty.
 * @param use_cache Whether cached data may be returned.
 * @return The matching user, or std::nullopt when no user exists.
 *
 * @throws std::invalid_argument If user_id is empty.
 * @throws RepositoryError If the repository cannot be accessed.
 */
[[nodiscard]]
std::optional<User> load_user(
    std::string_view user_id,
    bool use_cache = true);
```

Template example:

```cpp
/**
 * @brief Finds an element matching the supplied predicate.
 *
 * @tparam Range Input range type.
 * @tparam Predicate Callable predicate type.
 * @param range Range to search.
 * @param predicate Predicate used to match an element.
 * @return An iterator to the matching element, or the end iterator.
 */
template <typename Range, typename Predicate>
auto find_matching(Range&& range, Predicate&& predicate);
```

Additional rules:

- Document ownership and lifetime requirements when relevant.
- Document thread-safety guarantees and invalidation behavior when relevant.
- Do not explain basic control flow line by line.

### Rust

Use rustdoc comments with Markdown formatting.

- Use `///` for the item following the comment.
- Use `//!` for crate-level or module-level documentation.
- Document all public items.
- Add runnable examples for important public APIs.
- Explicitly document errors, panics, and unsafe requirements.

Use these sections when applicable:

- `# Examples`
- `# Errors`
- `# Panics`
- `# Safety`
- `# Returns`
- `# Notes`

Example:

```rust
/// Loads a user by identifier.
///
/// Returns `Ok(None)` when no matching user exists.
///
/// # Errors
///
/// Returns [`RepositoryError`] when the repository cannot be accessed.
///
/// # Examples
///
/// ```
/// # async fn example(
/// #     repository: &UserRepository,
/// # ) -> Result<(), RepositoryError> {
/// let user = repository.load_user("user-123").await?;
/// assert!(user.is_some());
/// # Ok(())
/// # }
/// ```
pub async fn load_user(
    &self,
    user_id: &str,
) -> Result<Option<User>, RepositoryError> {
    // ...
}
```

Use `//!` for modules or crates:

```rust
//! User repository abstractions.
//!
//! This module contains types used to store and retrieve users.
```

Unsafe public APIs must include a `# Safety` section:

```rust
/// Creates a user reference from a raw pointer.
///
/// # Safety
///
/// `ptr` must be non-null, properly aligned, and point to a valid `User`
/// that remains alive for the returned lifetime.
pub unsafe fn user_from_ptr<'a>(ptr: *const User) -> &'a User {
    // ...
}
```

### Existing Code

When modifying an existing API:

1. Preserve correct existing documentation.
2. Update documentation when behavior changes.
3. Remove inaccurate or obsolete descriptions.
4. Follow the documentation style used by surrounding code.
5. Do not rewrite documentation only for cosmetic wording changes.
6. Do not add documentation to unrelated code.

### Quality Checks

Before completing a task, verify that:

- Every documented parameter exists.
- Generic parameters are documented when necessary.
- Return-value documentation matches the implementation.
- Documented exceptions and errors are actually possible.
- Examples are syntactically valid.
- Symbol references resolve where tooling supports validation.
- Documentation does not expose secrets or sensitive implementation details.
- Documentation describes current behavior, not planned future behavior.
- Comments provide value beyond what is immediately obvious from the code.

## Agent Infrastructure

- **CodeGraph** (`codegraph_explore`): structural queries — call what, define where, blast radius
- **GitNexus** (`.claude/skills/gitnexus/`): impact analysis, refactoring, execution flows
