// CSharpWriter — cross-language OCR shm contract probe (Docs/superpowers/plans/2026-10-22-ocr-models-shm.md).
// Publishes one known BGRA frame through the PRODUCTION `ReTran.Core` OcrShmWriter into the
// probe-only section `Local\ReTran.Ocr.Probe` (never the production `Local\ReTran.Ocr.V1`),
// then holds the mapping while the Python reader (../read_probe.py) reads it back through the
// production `retran_core.ocr.shm.ShmReader`. This proves the C# writer and the Python reader
// agree on the real header layout (magic RTOC, version, seqlock seq, width, height, stride,
// format) and on the payload bytes — no hand-mirrored spike layout involved.
//
// Run (repo root):  dotnet run --project Tools/Perf/shm-probe/CSharpWriter
// Then in a second terminal (note: --project, not --directory):
//   uv run --project "D:\Code\ReTran Project\ReTran.OCR" python Tools/Perf/shm-probe/read_probe.py
using ReTran.Core.Capture;
using ReTran.Core.Ocr;

// Must match ../read_probe.py — both sides derive the same payload bytes from these.
const string DefaultMapName = @"Local\ReTran.Ocr.Probe";
const int Width = 64;
const int Height = 32;

string mapName = args.Length > 0 ? args[0] : DefaultMapName;

// Deterministic payload both sides compute: byte i = (i * 13 + 5) % 251.
byte[] pixels = new byte[Width * Height * 4];
for (int i = 0; i < pixels.Length; i++)
{
    pixels[i] = (byte)((i * 13 + 5) % 251);
}

using Frame frame = Frame.FromBgra(Width, Height, pixels);
using var writer = new OcrShmWriter(mapName);
long seq = writer.Publish(frame);

Console.WriteLine($"map:    {mapName}");
Console.WriteLine($"frame:  {Width}x{Height} BGRA (stride {Width * 4}, {pixels.Length} bytes)");
Console.WriteLine($"seq:    {seq} (int64 LE, even = stable)");
Console.WriteLine("READY: mapping held — press Enter to release");
Console.Out.Flush();

// Keep the section alive while the Python reader runs. When stdin is already closed
// (background launch), ReadLine returns null — fall back to an infinite sleep so the
// mapping still survives until the process is killed.
if (Console.ReadLine() is null)
{
    Console.WriteLine("stdin EOF — holding mapping until killed");
    Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
}
