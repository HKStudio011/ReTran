# M0 — Core/App IPC Backbone Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A working end-to-end pipe — the App launches a headless C# Core process, Core spawns a Python OCR sidecar, and all three speak JSON-RPC 2.0 over stdio (pingable), plus a CLI dispatcher stub and config loading.

**Architecture:** One IPC peer between App and Core (C# `ReTran.Core.exe --serve`). Core mediates to the Python sidecar (same framing). The C# process is also a CLI (`<subcommand>` one-shot) sharing handlers with the runtime. Transport is abstracted so stdio can later become a named pipe without changing the message contract.

**Tech Stack:** .NET 10 (C#, `net10.0-windows`, console), Python 3 (stdlib-only sidecar for M0, pytest), MAUI Blazor Hybrid App (`net10.0-windows10.0.19041.0`), System.Text.Json.

**Spec:** `Docs/superpowers/specs/2026-09-09-retran-core-app-architecture-design.md`

## Global Constraints
- C# TFM for the core process: `net10.0-windows` (headless console, NOT MAUI). App build target stays `net10.0-windows10.0.19041.0`.
- IPC = JSON-RPC 2.0, **one message per line** on stdin/stdout. `Id` values are **strings**. Standard error codes: `-32700`, `-32600`, `-32601`, `-32602`, `-32603`.
- Method namespaces: `core.*` (C# main), `sidecar.*` (Python). Streaming results use notifications (no `id`).
- Core process is Windows-only. Python sidecar code in M0 is **stdlib-only**.
- Python env: `uv sync` inside `ReTran Core/` creates a repo-local `.venv`; uv provisions CPython 3.13 per `requires-python>=3.13` (system Python 3.11 untouched, per AGENTS.md venv rule). ALL python test/smoke commands run via `uv run` from `ReTran Core/`.
- C# public API gets bilingual doc comments (English first sentence, then Vietnamese) per AGENTS.md. Python uses PEP 257 Google-style docstrings. TS uses JSDoc.
- No layer depends on a concrete engine; keep capture/ocr/translate behind interfaces (M0 only needs the transport + dispatch seams).
- Log via DI `Microsoft.Extensions.Logging` in C#, never `Console.WriteLine` for app logging (CLI one-shot stdout output is the exception — that's data, not logs).
- Build ONLY the Windows TFM locally. Verify with the exact commands in each task.

## File Structure (locked)

```
ReTran Core/core-cs/
├── ReTran.Core.csproj              # net10.0-windows console exe
└── src/
    ├── Program.cs                  # entry: --serve vs <subcommand> dispatch
    ├── JsonRpc/
    │   ├── JsonRpcMessages.cs      # envelope records + error codes (typed)
    │   ├── IJsonRpcTransport.cs    # transport abstraction (stdio now, pipe later)
    │   ├── LineJsonRpcTransport.cs # stdio newline-delimited implementation
    │   └── JsonRpcServer.cs        # read→parse→dispatch→write loop + handler registry
    ├── Cli/
    │   └── CommandDispatcher.cs    # maps subcommand → handler (runtime & CLI share)
    └── Config/
        └── CoreConfig.cs           # reads %LOCALAPPDATA%\ReTran\core\config.yaml (or .json)
ReTran Core/
├── src/retran_core/
│   ├── __init__.py
│   ├── __main__.py                 # python entry: serve JSON-RPC over stdio
│   └── jsonrpc.py                  # framing + dispatch (stdlib only)
├── tests/
│   └── test_jsonrpc.py
ReTran App/
├── Core/
│   ├── CoreProcessClient.cs        # launches ReTran.Core.exe --serve, JSON-RPC client
│   └── JsonRpcClient.cs            # reusable JSON-RPC-over-stream client
└── (MainPage.xaml / MainPage.xaml.cs)  # Start / Ping / Stop buttons
```

---

### Task 1: C# project scaffold + JSON-RPC message types + framing

**Files:**
- Create: `ReTran Core/core-cs/ReTran.Core.csproj`
- Create: `ReTran Core/core-cs/src/JsonRpc/JsonRpcMessages.cs`
- Test: `ReTran Core/core-cs/tests/ReTran.Core.Tests.csproj` + `tests/JsonRpcFramingTests.cs`

**Interfaces:**
- Produces: `JsonRpcRequest(string Id, string Method, JsonNode? Params)`, `JsonRpcResponse(string Id, JsonNode? Result, JsonRpcError? Error)`, `JsonRpcNotification(string Method, JsonNode? Params)`, static class `JsonRpc` with `string Serialize(JsonNode)` (one line, no trailing newline) and `JsonNode? ParseLine(string)` (returns null on empty/whitespace line).

- [ ] **Step 1: Write the failing test**

Create `ReTran Core/core-cs/tests/JsonRpcFramingTests.cs`:
```csharp
using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;
using Xunit;

namespace RetranCore.Tests;

public class JsonRpcFramingTests
{
    [Fact]
    public void Serialize_ProducesSingleLine_WithoutTrailingNewline()
    {
        var req = new JsonRpcRequest("1", "core.ping", null);
        string line = JsonRpc.SerializeRequest(req);
        Assert.DoesNotContain('\n', line);
        Assert.StartsWith("{", line);
    }

    [Fact]
    public void ParseLine_RoundTrips_ARequest()
    {
        var req = new JsonRpcRequest("1", "core.ping", null);
        JsonNode? back = JsonRpc.ParseLine(JsonRpc.SerializeRequest(req));
        Assert.NotNull(back);
        Assert.Equal("2.0", back!["jsonrpc"]!.GetValue<string>());
        Assert.Equal("core.ping", back["method"]!.GetValue<string>());
    }

    [Fact]
    public void ParseLine_ReturnsNull_ForBlankLine()
    {
        Assert.Null(JsonRpc.ParseLine(""));
        Assert.Null(JsonRpc.ParseLine("   "));
    }
}
```
Note: test namespaces are `RetranCore.Tests` (NOT `ReTran.Core.Tests`) — inside any `ReTran.Core.*` namespace the bare name `JsonRpc` binds to the *namespace* `ReTran.Core.JsonRpc`, not the class, and breaks compilation.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test "ReTran Core/core-cs/tests/ReTran.Core.Tests.csproj" -f net10.0-windows`
Expected: FAIL — `JsonRpc`, `JsonRpcRequest` do not exist (compile error).

- [ ] **Step 3: Write the project files + minimal implementation**

Create `ReTran Core/core-cs/ReTran.Core.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RootNamespace>ReTran.Core</RootNamespace>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

Create `ReTran Core/core-cs/tests/ReTran.Core.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\ReTran.Core.csproj" />
  </ItemGroup>
</Project>
```

Create `ReTran Core/core-cs/src/JsonRpc/JsonRpcMessages.cs`:
```csharp
using System.Text.Json.Nodes;

namespace ReTran.Core.JsonRpc;

/// <summary>
/// A JSON-RPC 2.0 request: a method call with an id and optional params.
/// Yêu cầu JSON-RPC 2.0: lời gọi phương thức kèm id và tham số tùy chọn.
/// </summary>
public sealed record JsonRpcRequest(string Id, string Method, JsonNode? Params);

/// <summary>
/// A JSON-RPC 2.0 error object (code + message + optional data).
/// Đối tượng lỗi JSON-RPC 2.0 (mã + thông điệp + dữ liệu tùy chọn).
/// </summary>
public sealed record JsonRpcError(int Code, string Message, JsonNode? Data = null);

/// <summary>
/// A JSON-RPC 2.0 response carrying either a result or an error.
/// Phản hồi JSON-RPC 2.0 mang kết quả hoặc lỗi.
/// </summary>
public sealed record JsonRpcResponse(string Id, JsonNode? Result, JsonRpcError? Error);

/// <summary>
/// A JSON-RPC 2.0 notification (no id) used for streaming results.
/// Thông báo JSON-RPC 2.0 (không id) dùng cho kết quả dạng luồng.
/// </summary>
public sealed record JsonRpcNotification(string Method, JsonNode? Params);

/// <summary>
/// Standard JSON-RPC error codes.
/// Mã lỗi chuẩn JSON-RPC.
/// </summary>
public static class RpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

/// <summary>
/// Converts records to/from JSON-RPC 2.0 nodes and frames single-line messages.
/// Chuyển đổi record sang/ra node JSON-RPC 2.0 và đóng gói tin một dòng.
/// </summary>
public static class JsonRpc
{
    /// <summary>Serializes a request into one line of JSON (no trailing newline).</summary>
    public static string SerializeRequest(JsonRpcRequest r) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = r.Id,
        ["method"] = r.Method,
        ["params"] = r.Params ?? new JsonObject(),
    }.ToJsonString();

    /// <summary>Serializes a response into one line of JSON.</summary>
    public static string SerializeResponse(JsonRpcResponse resp)
    {
        var o = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = resp.Id };
        if (resp.Error is not null)
            o["error"] = new JsonObject { ["code"] = resp.Error.Code, ["message"] = resp.Error.Message, ["data"] = resp.Error.Data ?? new JsonObject() };
        else
            o["result"] = resp.Result ?? JsonValue.Create<object?>(null);
        return o.ToJsonString();
    }

    /// <summary>Serializes a notification into one line of JSON (no id).</summary>
    public static string SerializeNotification(JsonRpcNotification n) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["method"] = n.Method,
        ["params"] = n.Params ?? new JsonObject(),
    }.ToJsonString();

    /// <summary>Parses one line into a JsonNode; returns null for blank lines.</summary>
    public static JsonNode? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try { return JsonNode.Parse(line); }
        catch { return null; }
    }

    /// <summary>Wraps a request as its wire JsonNode (used by tests and the CLI).</summary>
    public static JsonNode RequestToNode(JsonRpcRequest r) => JsonNode.Parse(SerializeRequest(r))!;
}
```

- [ ] **Step 4: Run tests to verify they pass**
Run: `dotnet test "ReTran Core/core-cs/tests/ReTran.Core.Tests.csproj" -f net10.0-windows`
Expected: PASS (3 framing tests).

- [ ] **Step 5: Commit**
```bash
git add "ReTran Core/core-cs"
git commit -m "feat(core): scaffold ReTran.Core + JSON-RPC message types and framing"
```

---

### Task 2: JSON-RPC transport abstraction + stdio implementation + server loop

**Files:**
- Create: `ReTran Core/core-cs/src/JsonRpc/IJsonRpcTransport.cs`
- Create: `ReTran Core/core-cs/src/JsonRpc/LineJsonRpcTransport.cs`
- Create: `ReTran Core/core-cs/src/JsonRpc/JsonRpcServer.cs`
- Test: `ReTran Core/core-cs/tests/JsonRpcServerTests.cs`

**Interfaces:**
- Consumes: `JsonRpc.ParseLine`, `JsonRpc.SerializeResponse`, `RpcErrorCodes` (Task 1).
- Produces: `interface IJsonRpcTransport { Task<string?> ReadLineAsync(CancellationToken ct); Task WriteLineAsync(string line, CancellationToken ct); }`; `class JsonRpcServer` with `void Register(string method, Func<JsonNode?, Task<JsonNode?>> handler)` and `Task RunAsync(IJsonRpcTransport transport, CancellationToken ct)`.

- [ ] **Step 1: Write the failing test** (uses an in-memory fake transport so no real stdio is needed)

Create `ReTran Core/core-cs/tests/JsonRpcServerTests.cs`:
```csharp
using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;
using Xunit;

namespace RetranCore.Tests;

public class JsonRpcServerTests
{
    private sealed class FakeTransport : IJsonRpcTransport
    {
        private readonly Queue<string> _in;
        public Queue<string> Out { get; } = new();
        public FakeTransport(IEnumerable<string> lines) { _in = new(lines); }
        public Task<string?> ReadLineAsync(CancellationToken ct) =>
            Task.FromResult(_in.Count > 0 ? _in.Dequeue() : null);
        public Task WriteLineAsync(string line, CancellationToken ct) { Out.Enqueue(line); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Ping_ReturnsPong()
    {
        var server = new JsonRpcServer();
        server.Register("core.ping", _ => Task.FromResult<JsonNode?>(JsonValue.Create("pong")));
        var t = new FakeTransport(new[] { "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"core.ping\",\"params\":{}}" });
        await server.RunAsync(t, CancellationToken.None);
        var resp = JsonNode.Parse(t.Out.Dequeue())!;
        Assert.Equal("pong", resp["result"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownMethod_ReturnsMethodNotFound()
    {
        var server = new JsonRpcServer();
        var t = new FakeTransport(new[] { "{\"jsonrpc\":\"2.0\",\"id\":\"2\",\"method\":\"core.nope\",\"params\":{}}" });
        await server.RunAsync(t, CancellationToken.None);
        var resp = JsonNode.Parse(t.Out.Dequeue())!;
        Assert.Equal(RpcErrorCodes.MethodNotFound, resp["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task MalformedLine_ReturnsParseError()
    {
        var server = new JsonRpcServer();
        var t = new FakeTransport(new[] { "this is not json" });
        await server.RunAsync(t, CancellationToken.None);
        var resp = JsonNode.Parse(t.Out.Dequeue())!;
        Assert.Equal(RpcErrorCodes.ParseError, resp["error"]!["code"]!.GetValue<int>());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test "ReTran Core/core-cs/tests/ReTran.Core.Tests.csproj" -f net10.0-windows --filter JsonRpcServerTests`
Expected: FAIL — `IJsonRpcTransport`, `JsonRpcServer` do not exist.

- [ ] **Step 3: Implement transport + server**

Create `IJsonRpcTransport.cs`:
```csharp
namespace ReTran.Core.JsonRpc;

/// <summary>
/// Abstraction over the byte/line channel carrying JSON-RPC messages.
/// Trừu tượng cho kênh dòng mang tin nhắn JSON-RPC.
/// </summary>
public interface IJsonRpcTransport
{
    /// <summary>Returns the next line, or null when the stream is closed.</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);
    /// <summary>Writes one framed line to the peer.</summary>
    Task WriteLineAsync(string line, CancellationToken ct);
}
```

Create `LineJsonRpcTransport.cs`:
```csharp
using System.Text;

namespace ReTran.Core.JsonRpc;

/// <summary>
/// Newline-delimited JSON-RPC transport over a stdin/stdout pair.
/// Transport JSON-RPC phân tách bằng xuống dòng trên cặp stdin/stdout.
/// </summary>
public sealed class LineJsonRpcTransport : IJsonRpcTransport
{
    private readonly TextReader _in;
    private readonly TextWriter _out;

    public LineJsonRpcTransport(TextReader input, TextWriter output)
    { _in = input; _out = output; }

    /// <summary>Reads the next line; null on EOF.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct) => await _in.ReadLineAsync(ct);

    /// <summary>Writes a line followed by a newline and flushes.</summary>
    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        await _out.WriteAsync((line + "\n").AsMemory(), ct);
        await _out.FlushAsync(ct);
    }
}
```

Create `JsonRpcServer.cs`:
```csharp
using System.Text.Json.Nodes;

namespace ReTran.Core.JsonRpc;

/// <summary>
/// Minimal JSON-RPC 2.0 server: reads lines, dispatches to registered handlers, writes responses.
/// Máy chủ JSON-RPC 2.0 tối giản: đọc dòng, điều phối tới handler đã đăng ký, ghi phản hồi.
/// </summary>
public sealed class JsonRpcServer
{
    private readonly Dictionary<string, Func<JsonNode?, Task<JsonNode?>>> _handlers = new();

    /// <summary>Registers a handler for a method name (e.g. "core.ping").</summary>
    public void Register(string method, Func<JsonNode?, Task<JsonNode?>> handler) => _handlers[method] = handler;

    /// <summary>Runs the read/dispatch/write loop until the transport is closed.</summary>
    public async Task RunAsync(IJsonRpcTransport transport, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await transport.ReadLineAsync(ct);
            if (line is null) break; // EOF

            JsonNode? node = JsonRpc.ParseLine(line);
            if (node is null || node["jsonrpc"]?.GetValue<string>() != "2.0" || node["method"] is null)
            {
                await transport.WriteLineAsync(JsonRpc.SerializeResponse(
                    new JsonRpcResponse("null", null, new JsonRpcError(RpcErrorCodes.ParseError, "Parse error"))), ct);
                continue;
            }

            string id = node["id"]?.GetValue<string>() ?? "null";
            string method = node["method"]!.GetValue<string>()!;
            JsonNode? @params = node["params"];

            if (!_handlers.TryGetValue(method, out var handler))
            {
                await transport.WriteLineAsync(JsonRpc.SerializeResponse(
                    new JsonRpcResponse(id, null, new JsonRpcError(RpcErrorCodes.MethodNotFound, $"Method not found: {method}"))), ct);
                continue;
            }

            try
            {
                JsonNode? result = await handler(@params);
                await transport.WriteLineAsync(JsonRpc.SerializeResponse(new JsonRpcResponse(id, result, null)), ct);
            }
            catch (Exception ex)
            {
                await transport.WriteLineAsync(JsonRpc.SerializeResponse(
                    new JsonRpcResponse(id, null, new JsonRpcError(RpcErrorCodes.InternalError, ex.Message))), ct);
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**
Run: `dotnet test "ReTran Core/core-cs/tests/ReTran.Core.Tests.csproj" -f net10.0-windows`
Expected: PASS (framing + 3 server tests).

- [ ] **Step 5: Commit**
```bash
git add "ReTran Core/core-cs/src/JsonRpc" "ReTran Core/core-cs/tests/JsonRpcServerTests.cs"
git commit -m "feat(core): JSON-RPC transport abstraction + stdio impl + server loop"
```

---

### Task 3: CLI dispatcher + `--serve` entry point (with `core.version`)

**Files:**
- Create: `ReTran Core/core-cs/src/Cli/CommandDispatcher.cs`
- Create: `ReTran Core/core-cs/src/Program.cs`
- Test: `ReTran Core/core-cs/tests/CommandDispatcherTests.cs`

**Interfaces:**
- Consumes: `JsonRpcServer`, `LineJsonRpcTransport` (Task 2).
- Produces: `class CommandDispatcher { void Register(string name, Func<string[], Task<int>> handler); Task<int> DispatchAsync(string[] args); }`; `Program.Main(string[] args)` → `0` on success.

- [ ] **Step 1: Write the failing test**
Create `ReTran Core/core-cs/tests/CommandDispatcherTests.cs`:
```csharp
using ReTran.Core.Cli;
using Xunit;

namespace RetranCore.Tests;

public class CommandDispatcherTests
{
    [Fact]
    public async Task KnownSubcommand_InvokesHandler()
    {
        var d = new CommandDispatcher();
        string? seen = null;
        d.Register("version", async args => { seen = string.Join(' ', args); return 0; });
        int code = await d.DispatchAsync(new[] { "version" });
        Assert.Equal(0, code);
        Assert.Equal("", seen);
    }

    [Fact]
    public async Task UnknownSubcommand_ReturnsNonZero()
    {
        var d = new CommandDispatcher();
        int code = await d.DispatchAsync(new[] { "frobnicate" });
        Assert.NotEqual(0, code);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test "ReTran Core/core-cs/tests/ReTran.Core.Tests.csproj" -f net10.0-windows --filter CommandDispatcherTests`
Expected: FAIL — `CommandDispatcher` does not exist.

- [ ] **Step 3: Implement dispatcher + Program**

Create `Cli/CommandDispatcher.cs`:
```csharp
namespace ReTran.Core.Cli;

/// <summary>
/// Maps a CLI subcommand name to a handler. Shared by one-shot CLI and (later) runtime methods.
/// Ánh xạ tên lệnh CLI tới handler. Dùng chung cho CLI một lần và (sau này) phương thức runtime.
/// </summary>
public sealed class CommandDispatcher
{
    private readonly Dictionary<string, Func<string[], Task<int>>> _handlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers a subcommand handler.</summary>
    public void Register(string name, Func<string[], Task<int>> handler) => _handlers[name] = handler;

    /// <summary>Dispatches args[0] as the subcommand; remaining args are passed through. Returns exit code.</summary>
    public async Task<int> DispatchAsync(string[] args)
    {
        if (args.Length == 0 || !_handlers.TryGetValue(args[0], out var handler))
        {
            Console.Error.WriteLine("usage: ReTran.Core <serve|version|ocr|translate|capture> [args]");
            return 2;
        }
        return await handler(args.Skip(1).ToArray());
    }
}
```

Create `Program.cs`:
```csharp
using System.Text;
using ReTran.Core.Cli;
using ReTran.Core.JsonRpc;

namespace ReTran.Core;

public static class Program
{
    /// <summary>App version reported by core.version and the CLI.</summary>
    public const string Version = "0.1.0";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] == "--serve" || args[0] == "serve")
            return await RunServeAsync();

        var dispatcher = new CommandDispatcher();
        dispatcher.Register("version", _ => Task.FromResult(WriteStdout(Version)));
        // ocr / translate / capture handlers land in M1–M3.
        return await dispatcher.DispatchAsync(args);
    }

    private static int WriteStdout(string s) { Console.Out.Write(s + "\n"); return 0; }

    /// <summary>Runs the long-running JSON-RPC peer on stdio.</summary>
    private static async Task<int> RunServeAsync()
    {
        var server = new JsonRpcServer();
        server.Register("core.ping", _ => Task.FromResult<JsonNode?>(System.Text.Json.Nodes.JsonValue.Create("pong")));
        server.Register("core.version", _ => Task.FromResult<System.Text.Json.Nodes.JsonNode?>(System.Text.Json.Nodes.JsonValue.Create(Version)));
        var transport = new LineJsonRpcTransport(new StreamReader(Console.OpenStandardInput()), new StreamWriter(Console.OpenStandardOutput()) { NewLine = "\n" });
        await server.RunAsync(transport, CancellationToken.None);
        return 0;
    }
}
```

- [ ] **Step 4: Run tests + a real CLI smoke test**
Run: `dotnet test "ReTran Core/core-cs/tests/ReTran.Core.Tests.csproj" -f net10.0-windows`
Then: `dotnet run --project "ReTran Core/core-cs" -f net10.0-windows -- version`
Expected: tests PASS; CLI prints `0.1.0`.

- [ ] **Step 5: Commit**
```bash
git add "ReTran Core/core-cs/src/Cli" "ReTran Core/core-cs/src/Program.cs" "ReTran Core/core-cs/tests/CommandDispatcherTests.cs"
git commit -m "feat(core): CLI dispatcher + --serve entry with core.version/ping"
```

---

### Task 4: Python sidecar — stdlib JSON-RPC-over-stdio server (`sidecar.ping`/`sidecar.version`)

**Files:**
- Create: `ReTran Core/src/retran_core/__init__.py`
- Create: `ReTran Core/src/retran_core/jsonrpc.py`
- Create: `ReTran Core/src/retran_core/__main__.py`
- Test: `ReTran Core/tests/test_jsonrpc.py`

**Interfaces:**
- Produces (Python): module `retran_core.jsonrpc` with `def handle_line(line: str, handlers: dict) -> str | None` (returns one response line, or None for blank lines) and a `serve(handlers)` loop over stdin. Entry: `python -m retran_core serve`.

- [ ] **Step 1: Write the failing test**
Create `ReTran Core/tests/test_jsonrpc.py`:
```python
import json
from retran_core.jsonrpc import handle_line

def _handlers():
    return {
        "sidecar.ping": lambda p: "pong",
        "sidecar.version": lambda p: "0.1.0",
    }

def test_ping_returns_pong():
    req = json.dumps({"jsonrpc": "2.0", "id": "1", "method": "sidecar.ping", "params": {}})
    resp = json.loads(handle_line(req, _handlers()))
    assert resp["result"] == "pong"
    assert resp["id"] == "1"

def test_unknown_method_returns_not_found():
    req = json.dumps({"jsonrpc": "2.0", "id": "2", "method": "sidecar.nope", "params": {}})
    resp = json.loads(handle_line(req, _handlers()))
    assert resp["error"]["code"] == -32601

def test_blank_line_returns_none():
    assert handle_line("   ", _handlers()) is None
```

- [ ] **Step 2: Create the venv, then run test to verify it fails**
Run: `cd "ReTran Core" && uv sync`  (creates `.venv`, installs dev group incl. pytest; uv auto-provisions CPython 3.13)
Run: `cd "ReTran Core" && uv run pytest tests/test_jsonrpc.py -v`  (`pythonpath=["src"]` is set in pyproject)
Expected: FAIL — `retran_core.jsonrpc` not found.

- [ ] **Step 3: Implement the sidecar**

Add a build backend to `ReTran Core/pyproject.toml` (after `[project]`, before `[dependency-groups]`) so `uv sync` installs the package editable into `.venv`:
```toml
[build-system]
requires = ["hatchling"]
build-backend = "hatchling.build"

[tool.hatch.build.targets.wheel]
packages = ["src/retran_core"]
```
Then re-run `uv sync` from `ReTran Core/`.

Create `src/retran_core/__init__.py`:
```python
"""ReTran Core OCR sidecar (Python).

Long-lived process spawned by the C# main core; speaks JSON-RPC 2.0 over stdio.
"""
__version__ = "0.1.0"
```

Create `src/retran_core/jsonrpc.py`:
```python
"""Newline-delimited JSON-RPC 2.0 server helpers (stdlib only)."""
from __future__ import annotations
import json
from typing import Any, Callable, Dict, Optional

Handler = Callable[[Optional[dict]], Any]

def _resp(id_, result: Any) -> str:
    return json.dumps({"jsonrpc": "2.0", "id": id_, "result": result}, ensure_ascii=False)

def _err(id_, code: int, message: str) -> str:
    return json.dumps({"jsonrpc": "2.0", "id": id_, "error": {"code": code, "message": message}}, ensure_ascii=False)

def handle_line(line: str, handlers: Dict[str, Handler]) -> Optional[str]:
    """Process one request line; return one response line, or None for blank lines."""
    if not line.strip():
        return None
    try:
        req = json.loads(line)
    except json.JSONDecodeError:
        return _err(None, -32700, "Parse error")
    if not isinstance(req, dict) or req.get("jsonrpc") != "2.0" or "method" not in req:
        return _err(req.get("id") if isinstance(req, dict) else None, -32600, "Invalid Request")
    id_ = req.get("id")
    method = req["method"]
    params = req.get("params")
    handler = handlers.get(method)
    if handler is None:
        return _err(id_, -32601, f"Method not found: {method}")
    try:
        return _resp(id_, handler(params))
    except Exception as exc:  # noqa: BLE001 - report any handler failure as internal error
        return _err(id_, -32603, str(exc))

def serve(handlers: Dict[str, Handler]) -> None:
    """Read request lines from stdin and write response lines to stdout until EOF."""
    import sys
    for line in sys.stdin:
        out = handle_line(line, handlers)
        if out is not None:
            print(out, flush=True)
```

Create `src/retran_core/__main__.py`:
```python
"""Entry point: `python -m retran_core serve` runs the JSON-RPC server on stdio."""
from __future__ import annotations
import sys
from . import __version__
from .jsonrpc import serve

def _handlers():
    return {
        "sidecar.ping": lambda p: "pong",
        "sidecar.version": lambda p: __version__,
    }

def main(argv=None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    if not argv or argv[0] == "serve":
        serve(_handlers())
        return 0
    print(f"usage: python -m retran_core serve", file=sys.stderr)
    return 2

if __name__ == "__main__":
    raise SystemExit(main())
```

- [ ] **Step 4: Run tests + a real sidecar smoke test**
Run: `cd "ReTran Core" && uv run pytest tests/test_jsonrpc.py -v`
Then: `cd "ReTran Core" && echo '{"jsonrpc":"2.0","id":"1","method":"sidecar.ping","params":{}}' | uv run python -m retran_core serve`
Expected: tests PASS; smoke prints a line containing `"result":"pong"`.

- [ ] **Step 5: Commit**
```bash
git add "ReTran Core/src/retran_core" "ReTran Core/tests/test_jsonrpc.py"
git commit -m "feat(core): Python sidecar stdlib JSON-RPC server (ping/version)"
```

---

### Task 5: C# spawns the Python sidecar and pings it over stdio

**Files:**
- Create: `ReTran Core/core-cs/src/OcrClient/SidecarProcess.cs`
- Test: `ReTran Core/core-cs/tests/SidecarProcessTests.cs`

**Interfaces:**
- Consumes: `JsonRpc.ParseLine`, `JsonRpc.SerializeRequest`, `JsonRpcRequest` (Task 1).
- Produces: `sealed class SidecarProcess : IAsyncDisposable { static Task<SidecarProcess> StartAsync(string pythonExe, string workingDir, CancellationToken ct); Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct); }`. `pythonExe` is the venv interpreter (`ReTran Core/.venv/Scripts/python.exe` on Windows).

- [ ] **Step 1: Write the failing test** (integration; skips cleanly if no Python)
Create `ReTran Core/core-cs/tests/SidecarProcessTests.cs`:
```csharp
using System.Text.Json.Nodes;
using ReTran.Core.OcrClient;
using Xunit;

namespace RetranCore.Tests;

public class SidecarProcessTests
{
    [Fact]
    public async Task Ping_RoundTrips_ThroughRealPythonProcess()
    {
        if (!OperatingSystem.IsWindows()) return; // sidecar spawn is Windows-focused for M0
        if (FindRepoRoot() is not string repoRoot) return; // no repo layout -> skip
        string pythonExe = Path.Combine(repoRoot, "ReTran Core", ".venv", "Scripts", "python.exe");
        if (!File.Exists(pythonExe)) return; // venv not created yet (Task 4: uv sync) -> skip
        await using var sp = await SidecarProcess.StartAsync(pythonExe, repoRoot, CancellationToken.None);
        JsonNode? pong = await sp.CallAsync("sidecar.ping", null, CancellationToken.None);
        Assert.Equal("pong", pong!.GetValue<string>());
    }

    /// <summary>Walks up from the test bin dir to the repo root (dir containing "ReTran Core/pyproject.toml").</summary>
    private static string? FindRepoRoot()
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "ReTran Core", "pyproject.toml")))
            d = d.Parent;
        return d?.FullName;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test "ReTran Core/core-cs/tests/ReTran.Core.Tests.csproj" -f net10.0-windows --filter SidecarProcessTests`
Expected: FAIL — `SidecarProcess` does not exist.

- [ ] **Step 3: Implement SidecarProcess**
Create `OcrClient/SidecarProcess.cs`:
```csharp
using System.Diagnostics;
using System.Text.Json.Nodes;
using ReTran.Core.JsonRpc;
// The static framing class shares its name with the namespace it lives in; from any
// sibling namespace the bare name binds to the namespace, so alias it explicitly.
using Rpc = ReTran.Core.JsonRpc.JsonRpc;

namespace ReTran.Core.OcrClient;

/// <summary>
/// Owns the long-lived Python OCR sidecar process and talks JSON-RPC over its stdio.
/// Sở hữu tiến trình sidecar Python dài hạn và nói chuyện JSON-RPC qua stdio của nó.
/// </summary>
public sealed class SidecarProcess : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private int _nextId = 0;
    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();

    private SidecarProcess(Process proc, StreamWriter stdin) { _proc = proc; _stdin = stdin; }

    /// <summary>Spawns `python -m retran_core serve` in workingDir and starts the response read loop.</summary>
    public static Task<SidecarProcess> StartAsync(string pythonExe, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = pythonExe,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.Combine(workingDir, "ReTran Core"),
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("retran_core");
        psi.ArgumentList.Add("serve");
        var proc = new Process { StartInfo = psi };
        proc.Start();
        // .NET 10: StandardInput is a StreamWriter, StandardOutput a StreamReader — use them directly.
        proc.StandardInput.NewLine = "\n";
        var sp = new SidecarProcess(proc, proc.StandardInput);
        sp.StartReadLoop();
        return Task.FromResult(sp);
    }

    /// <summary>Background loop: reads response lines and routes each to its waiter by id.</summary>
    private void StartReadLoop()
    {
        var reader = _proc.StandardOutput;
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    if (Rpc.ParseLine(line) is { } n
                        && n["id"]?.GetValue<string>() is { } id
                        && n["result"] is { } r)
                    {
                        Deliver(id, r);
                    }
                }
            }
            catch { /* sidecar exited; pending waiters are cancelled by DisposeAsync */ }
        });
    }

    /// <summary>Sends a request and awaits its response (matched by id).</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        string id = (++_nextId).ToString();
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await _stdin.WriteAsync((Rpc.SerializeRequest(new JsonRpcRequest(id, method, @params)) + "\n").AsMemory(), ct);
        await _stdin.FlushAsync(ct);
        return await tcs.Task.WaitAsync(ct);
    }

    /// <summary>Called by the read loop to route a response to its waiter.</summary>
    private void Deliver(string id, JsonNode? result)
    {
        if (_pending.TryGetValue(id, out var tcs)) { _pending.Remove(id); tcs.TrySetResult(result); }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
        await _proc.WaitForExitAsync().ConfigureAwait(false);
        _proc.Dispose();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**
Run: `dotnet test "ReTran Core/core-cs/tests/ReTran.Core.Tests.csproj" -f net10.0-windows --filter SidecarProcessTests`
Expected: PASS (real Python round-trip via `.venv/Scripts/python.exe`). The test skips (returns early) when the venv is missing or the OS isn't Windows — never fails for environment reasons.

- [ ] **Step 5: Commit**
```bash
git add "ReTran Core/core-cs/src/OcrClient" "ReTran Core/core-cs/tests/SidecarProcessTests.cs"
git commit -m "feat(core): spawn + ping Python sidecar over stdio from C#"
```

---

### Task 6: App `CoreProcessClient` (launch core, handshake, ping) + minimal UI

**Files:**
- Create: `ReTran App/Core/JsonRpcClient.cs`
- Create: `ReTran App/Core/CoreProcessClient.cs`
- Modify: `ReTran App/ReTran App.csproj` (exclude the test folder from the MAUI app's compile glob)
- Modify: `ReTran App/MainPage.xaml` + `ReTran App/MainPage.xaml.cs`
- Test: `ReTran App/Core.Tests/ReTran.App.Core.Tests.csproj` + `JsonRpcClientTests.cs`

**Interfaces:**
- Consumes: JSON-RPC framing (same wire format as Tasks 1–3).
- Produces: `sealed class JsonRpcClient : IAsyncDisposable { Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct); }`; `sealed class CoreProcessClient : IAsyncDisposable { Task StartAsync(string coreExePath, CancellationToken ct); Task<JsonNode?> PingAsync(CancellationToken ct); }`.

- [ ] **Step 1: Write the failing test** (client framing against an in-memory fake, no real process)
Create the test project `ReTran App/Core.Tests/ReTran.App.Core.Tests.csproj` (TFM must match the MAUI app's Windows TFM to reference it):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\\ReTran App.csproj" />
  </ItemGroup>
</Project>
```
Also edit `ReTran App/ReTran App.csproj`: the MAUI SDK globs every `.cs` under its folder into the app, so add to the existing `<ItemGroup>`:
```xml
    <!-- Core.Tests is a separate test assembly; keep it out of the app's compile glob. -->
    <Compile Remove="Core.Tests\**" />
    <None Include="Core.Tests\**" CopyToOutputDirectory="Never" />
```
Test that `JsonRpcClient.CallAsync` writes a correctly framed request line and resolves when a matching response line is fed back. The client takes injectable `TextReader`/`TextWriter`; the test wires an anonymous pipe pair so both directions are real async streams (MemoryStream would EOF instantly):
```csharp
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using ReTran_App.Core;
using Xunit;

public class JsonRpcClientTests
{
    [Fact]
    public async Task CallAsync_WritesRequest_AndResolvesOnMatchingResponse()
    {
        // inFromServer: server -> client ; outToServer: client -> server
        var (inToClient, inFromServer) = AnonymousPipeStreamStream.CreateStreams();
        var (outToServer, outFromClient) = AnonymousPipeStreamStream.CreateStreams();

        using var client = new JsonRpcClient(new StreamReader(inToClient), new StreamWriter(outFromClient) { NewLine = "\n" });

        // Read what the client sends to us.
        string? sentLine = await inFromServer.ReadLineAsync();

        // Feed the matching response back.
        await outToServer.WriteAsync(("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":\"pong\"}\n").AsMemory());
        await outToServer.FlushAsync();

        JsonNode? result = await client.CallAsync("core.ping", null, CancellationToken.None);
        Assert.Equal("pong", result!.GetValue<string>());

        var req = JsonNode.Parse(sentLine!)!;
        Assert.Equal("core.ping", req["method"]!.GetValue<string>());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test "ReTran App/Core.Tests" -f net10.0-windows`
Expected: FAIL — `JsonRpcClient` does not exist.

- [ ] **Step 3: Implement client + process wrapper**

Create `ReTran App/Core/JsonRpcClient.cs` (takes TextReader/TextWriter so it can bind directly to .NET 10's `Process.StandardInput`/`StandardOutput`, which are already writer/reader):
```csharp
using System.Text;
using System.Text.Json.Nodes;

namespace ReTran_App.Core;

/// <summary>
/// Newline-delimited JSON-RPC 2.0 client over an injectable reader/writer pair.
/// Client JSON-RPC 2.0 phân tách bằng xuống dòng trên cặp reader/writer có thể tiêm.
/// </summary>
public sealed class JsonRpcClient : IAsyncDisposable
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private int _nextId = 0;

    public JsonRpcClient(TextReader input, TextWriter output)
    {
        _input = input;
        _output = output;
        _ = ReadLoopAsync();
    }

    /// <summary>Sends a request and awaits its response (matched by id).</summary>
    public async Task<JsonNode?> CallAsync(string method, JsonNode? @params, CancellationToken ct)
    {
        string id = (++_nextId).ToString();
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        string line = $"{{\"jsonrpc\":\"2.0\",\"id\":\"{id}\",\"method\":\"{method}\",\"params\":{(@params is null ? "{}" : @params.ToJsonString())}}}";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        await _output.WriteAsync((line + "\n").AsMemory(), linked.Token);
        await _output.FlushAsync(linked.Token);
        return await tcs.Task.WaitAsync(ct);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _input.ReadLineAsync(_cts.Token)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (JsonNode.Parse(line) is { } n && n["id"]?.GetValue<string>() is { } id
                    && _pending.TryGetValue(id, out var tcs))
                {
                    _pending.Remove(id);
                    tcs.TrySetResult(n["result"]);
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _output.FlushAsync(); } catch { /* best effort */ }
    }
}
```

Create `ReTran App/Core/CoreProcessClient.cs`:
```csharp
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ReTran_App.Core;

/// <summary>
/// Launches the headless ReTran.Core.exe as a child process and talks JSON-RPC over its stdio.
/// Khởi chạy ReTran.Core.exe không-UI làm tiến trình con và nói chuyện JSON-RPC qua stdio của nó.
/// </summary>
public sealed class CoreProcessClient : IAsyncDisposable
{
    private Process? _proc;
    private JsonRpcClient? _client;

    /// <summary>Launches the core process with --serve and wires its stdio into a JSON-RPC client.</summary>
    public Task StartAsync(string coreExePath, CancellationToken ct)
    {
        if (!File.Exists(coreExePath)) throw new FileNotFoundException("Core executable not found.", coreExePath);
        var psi = new ProcessStartInfo
        {
            FileName = coreExePath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--serve");
        _proc = new Process { StartInfo = psi };
        _proc.Start();
        // .NET 10: StandardInput is a StreamWriter, StandardOutput a StreamReader — bind directly.
        _proc.StandardInput.NewLine = "\n";
        _client = new JsonRpcClient(_proc.StandardOutput, _proc.StandardInput);
        return Task.CompletedTask;
    }

    /// <summary>Pings the core; returns the result node (expected "pong").</summary>
    public async Task<JsonNode?> PingAsync(CancellationToken ct)
    {
        if (_client is null) throw new InvalidOperationException("Core not started.");
        return await _client.CallAsync("core.ping", null, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_proc is not null)
        {
            try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            await _proc.WaitForExitAsync().ConfigureAwait(false);
            _proc.Dispose();
        }
    }
}
```

- [ ] **Step 4: Wire minimal UI** — in `MainPage.xaml` add three buttons + a status label (`Start Core`, `Ping`, `Stop`); in `MainPage.xaml.cs` instantiate a `CoreProcessClient`, wire Start→`StartAsync`, Ping→`PingAsync` (show result in the label), Stop→`DisposeAsync`. Guard: if the core exe isn't built yet, show "core not found" instead of crashing.

- [ ] **Step 5: Build App (Windows TFM only) + run client test**
Run: `dotnet build "ReTran App/ReTran App.csproj" -f net10.0-windows10.0.19041.0`
Run: `dotnet test "ReTran App/Core.Tests/ReTran.App.Core.Tests.csproj" -f net10.0-windows`
Expected: build succeeds; client test PASS.

- [ ] **Step 6: Commit**
```bash
git add "ReTran App/Core" "ReTran App/Core.Tests" "ReTran App/MainPage.xaml" "ReTran App/MainPage.xaml.cs"
git commit -m "feat(app): CoreProcessClient + JSON-RPC client + Start/Ping/Stop UI"
```

---

### Task 7: Config loading (App settings.json + Core config.json) + add core-cs to `.slnx`

**Files:**
- Create: `ReTran App/Core/AppSettings.cs`
- Create: `ReTran Core/core-cs/src/Config/CoreConfig.cs`
- Modify: `ReTran Project.slnx`
- Test: `ReTran Core/core-cs/tests/CoreConfigTests.cs`

**Interfaces:**
- Produces: `record AppSettings(string? CoreExePath, string? PythonExe, ...)` with `static Task<AppSettings> LoadAsync()` from `%LOCALAPPDATA%\ReTran\settings.json` (creates defaults if missing); `record CoreConfig(string? PythonExe, int OcrFps = 3, ...)` with `static CoreConfig LoadFrom(string path)` from `%LOCALAPPDATA%\ReTran\core\config.json` (JSON for M0 — loader sits behind an interface so YAML can be swapped in later without touching callers).

- [ ] **Step 1: Write the failing test**
Create `CoreConfigTests.cs`: assert `CoreConfig.Load()` returns defaults when the file is absent (OcrFps == 3), and parses a written YAML/JSON file correctly. (Use JSON for M0 to avoid adding a YAML dependency; name the file `config.json` but keep the loader behind an interface so YAML can be swapped in later — record this decision.)
```csharp
using ReTran.Core.Config;
using Xunit;

public class CoreConfigTests
{
    [Fact]
    public void Load_ReturnsDefaults_WhenFileMissing()
    {
        var cfg = CoreConfig.LoadFrom(Path.Combine(Path.GetTempPath(), "retran-test-missing.json"));
        Assert.Equal(3, cfg.OcrFps);
    }

    [Fact]
    public void Load_Parses_OcrFps()
    {
        string p = Path.Combine(Path.GetTempPath(), "retran-test-cfg.json");
        File.WriteAllText(p, "{\"ocrFps\": 5}");
        var cfg = CoreConfig.LoadFrom(p);
        Assert.Equal(5, cfg.OcrFps);
        File.Delete(p);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**
Run: `dotnet test "ReTran Core/core-cs/tests/ReTran.Core.Tests.csproj" -f net10.0-windows --filter CoreConfigTests`
Expected: FAIL — `CoreConfig` does not exist.

- [ ] **Step 3: Implement config + App settings**
Create `Config/CoreConfig.cs` (JSON for M0, loader behind an interface for later YAML). Create `ReTran App/Core/AppSettings.cs` reading/writing `%LOCALAPPDATA%\ReTran\settings.json` with defaults (`CoreExePath`, `PythonExe="python"`).

- [ ] **Step 4: Add the C# core project to `.slnx`**
The solution already links `ReTran Core/ReTran Core.pyproj` (Python, `<Build Project="false" />`) — keep that entry. Insert a new line after it in `ReTran Project.slnx`:
```xml
  <Project Path="ReTran Core/core-cs/ReTran.Core.csproj" />
```

- [ ] **Step 5: Verify solution builds + all core tests pass**
Run: `dotnet build "ReTran Project.slnx"` (or build the two csproj paths individually if slnx multi-TFM fails on this box)
Run: `dotnet test "ReTran Core/core-cs/tests/ReTran.Core.Tests.csproj" -f net10.0-windows`
Expected: config tests PASS; solution builds with both core entries resolving (pyproj + core-cs).

- [ ] **Step 6: Commit**
```bash
git add "ReTran App/Core/AppSettings.cs" "ReTran Core/core-cs/src/Config" "ReTran Core/core-cs/tests/CoreConfigTests.cs" "ReTran Project.slnx"
git commit -m "feat: config loading (app settings.json + core config) and fix slnx core ref"
```

---

## M0 Acceptance (end-to-end)
1. `dotnet run --project "ReTran Core/core-cs" -f net10.0-windows -- version` prints `0.1.0`.
2. Piping a `core.ping` line into `ReTran.Core.exe --serve` returns `"result":"pong"`.
3. From the App, **Start Core** launches the process; **Ping** shows `pong`; **Stop** terminates it cleanly.
4. The C# core can spawn the Python sidecar and get `sidecar.ping → pong` (Task 5 test green).
5. All C# + Python tests pass; solution builds with no dangling project reference.

## Self-Review notes
- Spec coverage: IPC contract (§3) → Tasks 1–3, 6; one peer + sidecar mediation (§2) → Tasks 4–5; CLI one-shot (§4) → Task 3; data/paths (§5) → Task 7; `.slnx` fix (A5) → Task 7. Capture/OCR/translate/OBS/hook are intentionally out of M0 (M1–M5).
- Open items carried from spec: A1–A4 need user confirmation before Task 5's real sidecar path and the OCR milestone; A4 (Python 3.13 vs 3.11) does not block M0 because the sidecar is stdlib-only.
- Type consistency: `JsonRpcRequest/Response/Notification`, `IJsonRpcTransport.ReadLineAsync/WriteLineAsync`, `JsonRpcServer.Register/RunAsync`, `CommandDispatcher.DispatchAsync`, `SidecarProcess.CallAsync`, `JsonRpcClient.CallAsync` are used identically across tasks.
