# Plan: Profiles — quản lý cache dịch theo cửa sổ

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sheet "Profiles" trong app: xem cache dịch theo context (cửa sổ/màn hình), sửa/xoá entry, xoá cả context, export/import JSON.

**Architecture:** Service `ProfileStore` (EF, cùng `RetranDbContext` với `TranslationCache`, KHÔNG đụng đường nóng) + sheet Blazor `ProfilesPanel` mở từ Home + i18n vi/en. Export ghi `%LOCALAPPDATA%\ReTran\exports\`; import qua MAUI FilePicker; định dạng JSON versioned.

**Tech Stack:** .NET 10, EF Core SQLite (sẵn có), MAUI Blazor Hybrid, xUnit v3 — không dependency mới.

**Spec:** `Docs/superpowers/specs/2026-11-14-cache-profiles-design.md` (user yêu cầu "làm luôn, review một lần" 2026-11-14).

## Global Constraints

- **Không `git add -A`**; mỗi commit đúng file của task; không push; branch `master`.
- Build chuẩn: `dotnet build "ReTran Project.slnx"` → 0 error (warning `xUnit1051` có sẵn).
- Test: `dotnet test --project "ReTran.Tests/ReTran.Tests.csproj" -- --filter-class "<FQN>"`; **baseline trước plan này: 778 passed / 0 failed**.
- `TranslationCache` và các handler wire giữ nguyên; không đổi `core.start`/RPC.
- i18n: mọi key mới vào **cả** `Vi` và `En` (`I18nServiceTests` giữ chân).
- Doc comment C# song ngữ; UI theo style component sẵn có (`rt-*` classes).

## Review Focus

1. **Sửa cache khi pipeline đang chạy** không được làm hỏng chu kỳ dịch (không chung transaction với `translate.put`) — hint UI nói rõ session RAM không đổi tới khi restart. → Task 1 test `Update_WhileWriterActive_NoThrow` (mô phỏng bằng 2 save xen kẽ).
2. **Import file hỏng** không được nửa vời im lặng: JSON hỏng → `FormatException` (UI đỏ); row thiếu field → skip + đếm. → Task 1 `Import_MalformedJson_Throws`, `Import_SkipsInvalidRows_CountsSkipped`.
3. **Import upsert không tạo trùng key** (unique `(Context,Text,Src,Dst)`). → Task 1 `Import_UpsertsExisting_NoDuplicateKey`.
4. **Export → Import round-trip** giữ nguyên `context/src/dst/text/translated` (kể cả ký tự Unicode). → Task 1 `ExportImport_RoundTrip_PreservesAll`.
5. **Xoá context** không đụng context khác. → Task 1 `DeleteContext_LeavesOtherContexts`.

---

### Task 1: `ProfileStore` + tests

**Files:**
- Create: `ReTran.App/Services/ProfileStore.cs`
- Create: `ReTran.Tests/ProfileStoreTests.cs` (temp SQLite như `TranslationCacheTests`, namespace `ReTran.Tests`)

**Interfaces:**
- Consumes: `RetranDbContext` (`ReTran_App.Data`), `TranslationEntry`.
- Produces (Task 2 gọi):
  ```csharp
  public sealed record ProfileContextInfo(string Context, int Count);
  public sealed record ProfileEntryInfo(string Context, string Text, string Src, string Dst, string Translated, DateTime CreatedAtUtc);
  public sealed record ProfileImportResult(int Added, int Updated, int Skipped);

  public sealed class ProfileStore(RetranDbContext db)
  {
      public Task<IReadOnlyList<ProfileContextInfo>> GetContextsAsync(CancellationToken ct);
      public Task<IReadOnlyList<ProfileEntryInfo>> GetEntriesAsync(string context, string? search, int limit, CancellationToken ct);
      public Task<bool> UpdateEntryAsync(string context, string text, string src, string dst, string translated, CancellationToken ct);
      public Task<bool> DeleteEntryAsync(string context, string text, string src, string dst, CancellationToken ct);
      public Task<int> DeleteContextAsync(string context, CancellationToken ct);
      public Task ExportAsync(Stream output, string? context, CancellationToken ct);   // Utf8JsonWriter, version 1
      public Task<ProfileImportResult> ImportAsync(Stream input, CancellationToken ct); // upsert; hỏng JSON -> FormatException
  }
  ```
  Ghi chú implementation: `GetContextsAsync` group theo `Context` (chỉ `Context` + `Count` — spec D1 liệt thêm Srcs/Dsts nhưng UI không dùng, ledger ruling); `GetEntriesAsync` filter LIKE `%search%` trên `Text` hoặc `Translated`, `OrderByDescending(CreatedAtUtc)`, `Take(limit)`; Import: một transaction, per-entry lookup (SQLite local, import là thao tác thủ công) — row có `context/src/dst/text` không phải string không rỗng → skip.

- [ ] **Step 1: Viết test failing** — `ProfileStoreTests` (helper `NewStore(out db)` tạo file tạm + `db.Database.Migrate()` như `TranslationCacheTests`):
  `GetContexts_GroupsAndCounts`; `GetEntries_SearchAndLimit`; `UpdateEntry_ChangesTranslated`; `Update_UnknownKey_ReturnsFalse`; `DeleteEntry_Removes`; `DeleteContext_LeavesOtherContexts`; `ExportImport_RoundTrip_PreservesAll` (text có tiếng Việt + emoji); `Import_UpsertsExisting_NoDuplicateKey` (Added=0/Updated=1 khi key tồn tại); `Import_SkipsInvalidRows_CountsSkipped` (thiếu `translated`, `context` không phải string); `Import_MalformedJson_Throws`; `Update_WhileWriterActive_NoThrow` (update + chèn row khác xen kẽ trong một transaction ngắn).
- [ ] **Step 2: Chạy — xác nhận fail** (compile error: ProfileStore chưa tồn tại).
- [ ] **Step 3: Implement** `ProfileStore` + DTO theo Interfaces (doc comment song ngữ; Export dùng `Utf8JsonWriter` stream từng entry; Import bọc `JsonException` → `FormatException`).
- [ ] **Step 4: Chạy — pass** + full suite ≥ 778 + 11 test mới.
- [ ] **Step 5: Commit** `git add "ReTran.App/Services/ProfileStore.cs" "ReTran.Tests/ProfileStoreTests.cs"` → `feat(app): profile store for browsing and editing the translation cache`

---

### Task 2: Sheet `ProfilesPanel` + entry point + i18n

**Files:**
- Create: `ReTran.App/Components/Shared/ProfilesPanel.razor`
- Modify: `ReTran.App/Components/Pages/Home.razor` (nút mở sheet cạnh nút Settings hiện có)
- Modify: `ReTran.App/MauiProgram.cs` (DI `ProfileStore`)
- Modify: `ReTran.App/Services/I18nStrings.cs` (vi + en)

**Interfaces:**
- Consumes: `ProfileStore` (Task 1), `L10n`/`I18nService`, style classes `rt-*`, `ToastService`, JS bridge nếu cần "mở thư mục".
- Produces: UI sheet — không có API máy khác dùng.

- [ ] **Step 1: i18n** — keys: `profiles.title`, `profiles.open`, `profiles.loading`, `profiles.empty`, `profiles.col.text`, `profiles.col.translated`, `profiles.col.count`, `profiles.save`, `profiles.saved`, `profiles.delete`, `profiles.confirmDelete`, `profiles.deleteContext`, `profiles.confirmDeleteContext`, `profiles.export`, `profiles.exportAll`, `profiles.exportedTo`, `profiles.openFolder`, `profiles.import`, `profiles.importDone`, `profiles.importFailed`, `profiles.search`, `profiles.hintSession`, `profiles.actionFailed` (đủ cho mọi chuỗi trong sheet).
- [ ] **Step 2: `ProfilesPanel.razor`** — logic: load contexts on open; chọn context → load entries (limit 200, search box debounce 300 ms); mỗi dòng: text (read-only, mono), input bound `translated` + nút Lưu (gọi `UpdateEntryAsync`, toast khi lưu); nút Xoá từng dòng (2 bước: bấm → "Chắc chắn?" 3 s); footer: Xoá context (2 bước), Export (context đang chọn), Export all, Import (FilePicker `*.json` → `ImportAsync` → toast `importDone` với số liệu; `FormatException` → toast đỏ `importFailed`); sau export hiện đường dẫn + nút "Mở thư mục" (`Process.Start("explorer.exe", $"/select,\"{path}\"")` trong try/catch). Hint `profiles.hintSession` hiển thị cố định. Không auto-refresh khi pipeline chạy (có nút "Tải lại" = mở lại context).
- [ ] **Step 3: Home entry + DI** — thêm nút mở sheet trong `Home.razor` (cùng pattern nút Settings hiện có); `MauiProgram` đăng ký `ProfileStore` (scoped).
- [ ] **Step 4: Build + full suite** — `dotnet build "ReTran Project.slnx"` 0 error; suite ≥ 789; `npm run build` trong `vite-project` **không cần** (không đụng CSS build).
- [ ] **Step 5: Commit** `git add` 4 file → `feat(app): profiles sheet to browse, edit and move translation-cache profiles`

---

### Task 3: Verification cuối + smoke note

- [ ] **Step 1:** build + full suite đầy đủ (0 failed, ≥ 789).
- [ ] **Step 2: Smoke tương tác — DEFERRED cho user**: mở app → Start pipeline cửa sổ có text → mở Profiles: thấy context `*.exe` + số entry; sửa 1 dòng → restart pipeline → bản dịch mới hiển thị; export → mở thư mục thấy JSON; xoá context → biến mất; import lại file → entry quay lại (Added/Updated đúng).
- [ ] **Step 3: Commit** (nếu có doc chạm) — không code.

---

## Self-review (đã chạy theo writing-plans)

1. **Spec coverage:** D1 → Task 1; D2 → Task 1 (export/import); D3 → Task 2 (đường dẫn + FilePicker + mở thư mục); D4 → Task 2; D5 → Task 1 (`Update_WhileWriterActive`); D6 → Task 2 Step 1. ✔
2. **Step scan:** mỗi step một hành động; code chỉ ở chỗ không suy ra được (DTO + chữ ký ở Interfaces; hành vi UI ở Step 2).
3. **Type consistency:** DTO/`ProfileStore` dùng đúng tên ở Task 1 ↔ Task 2; key i18n liệt kê một lần.
4. **Review Focus:** 5 dòng ghim vào Task 1 (tests tên ở Step 1) + Task 2 (hintSession).
5. **Proportion:** plan ngắn — spec chứa quyết định, plan chứa test/commit.
