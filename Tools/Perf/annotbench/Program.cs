// annotbench — times the REAL FrameAnnotator (Debug build, matching the probe binaries) on a
// synthetic 1920x1080 BGRA frame, for 0/63/150/300 boxes. Prints avg + min ms/frame and JPEG KB.
using System.Diagnostics;
using ReTran.Core.Capture;
using ReTran.Core.Serve;

const int W = 1920, H = 1080;
byte[] pixels = new byte[W * H * 4];
var rnd = new Random(42);
for (int y = 0; y < H; y++)
{
    for (int x = 0; x < W; x++)
    {
        int i = (y * W + x) * 4;
        byte v = (byte)((x / 7 + y / 5) % 2 == 0 ? 60 : 200);
        pixels[i] = (byte)(v + rnd.Next(0, 24));
        pixels[i + 1] = (byte)(v / 2 + rnd.Next(0, 24));
        pixels[i + 2] = (byte)(v / 3 + rnd.Next(0, 24));
        pixels[i + 3] = 255;
    }
}

string[] labels =
{
    "Xin chào thế giới", "Bản dịch tiếng Việt", "Hello world text",
    "Congratulations on the win", "Press space to continue", "Điểm kinh nghiệm +250",
};

AnnotSnapshot Snapshot(int n)
{
    var boxes = new List<AnnotBox>(n);
    var r = new Random(7 + n);
    for (int k = 0; k < n; k++)
        boxes.Add(new AnnotBox(
            r.NextDouble() * 0.8, r.NextDouble() * 0.85,
            0.02 + r.NextDouble() * 0.12, 0.012 + r.NextDouble() * 0.02,
            labels[k % labels.Length], labels[k % labels.Length]));
    return new AnnotSnapshot(W, H, boxes);
}

var annotator = new FrameAnnotator();
Console.WriteLine("annotbench: real FrameAnnotator, 1920x1080 synthetic frame, Debug build");

foreach (int n in new[] { -1, 0, 63, 150, 300 })
{
    AnnotSnapshot? snap = n < 0 ? null : Snapshot(n);
    for (int i = 0; i < 3; i++)
    {
        using var f = Frame.FromBgra(W, H, pixels);
        annotator.Annotate(f, snap);
    }

    const int reps = 20;
    var times = new double[reps];
    long jpegBytes = 0;
    for (int i = 0; i < reps; i++)
    {
        using var f = Frame.FromBgra(W, H, pixels);
        var sw = Stopwatch.StartNew();
        AnnotatedFrame a = annotator.Annotate(f, snap);
        sw.Stop();
        times[i] = sw.Elapsed.TotalMilliseconds;
        jpegBytes = a.Jpeg.Length;
    }
    Array.Sort(times);
    double avg = times.Average(), min = times[0], p50 = times[reps / 2];
    Console.WriteLine($"boxes={(n < 0 ? "null" : n.ToString()),4}  avg={avg,7:F2} ms  p50={p50,7:F2}  min={min,7:F2}  jpeg={jpegBytes / 1024.0,6:F1} KB");
}
