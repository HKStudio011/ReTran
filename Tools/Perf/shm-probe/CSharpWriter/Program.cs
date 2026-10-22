// CSharpWriter — spike probe (Task 1, Docs/superpowers/plans/2026-10-22-ocr-models-shm.md).
// Creates the named shared-memory section `Local\ReTran.Ocr.V1`, writes a magic + seq
// pair, then holds the mapping alive while the Python reader (../read_probe.py) reads
// it back. Proves the C# `MemoryMappedFile` ↔ Python `mmap` name namespace works.
using System.IO.MemoryMappedFiles;
using System.Text;

// Layout mirrored byte-for-byte by ../read_probe.py (little-endian):
//   off 0:  magic [0x4F, 0x43, 0x52, 0x31] ("OCR1")
//   off 8:  int64 seq = 7
const string DefaultMapName = @"Local\ReTran.Ocr.V1";
const long MapSize = 64;
const int OffsetMagic = 0;
const int OffsetSeq = 8;
const long SeqValue = 7;

byte[] magic = [0x4F, 0x43, 0x52, 0x31];
string mapName = args.Length > 0 ? args[0] : DefaultMapName;

using MemoryMappedFile section = MemoryMappedFile.CreateOrOpen(mapName, MapSize);
using MemoryMappedViewAccessor view = section.CreateViewAccessor();
view.WriteArray(OffsetMagic, magic, 0, magic.Length);
view.Write(OffsetSeq, SeqValue);

Console.WriteLine($"map:   {mapName}");
Console.WriteLine($"size:  {MapSize}");
Console.WriteLine($"magic: {Convert.ToHexString(magic)} ('{Encoding.ASCII.GetString(magic)}') @ {OffsetMagic}");
Console.WriteLine($"seq:   {SeqValue} (int64 LE) @ {OffsetSeq}");
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
