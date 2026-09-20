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
