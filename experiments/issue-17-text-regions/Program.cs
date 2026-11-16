using ZwcadBatchPlot;

// Issue #17: «Поля текста» configured on a landscape A3 sheet, then a portrait A4 sheet is scanned.
// Rectangle frames always report landscape paper (ActualWidth = long side), so A4 portrait gets 297×210 paper.
var title = LocalRectangle.FromPoints(-120, 15, -10, 30); // mm from the right-bottom corner

static LocalRectangle OldToWorld(LocalRectangle mm, LocalRectangle frame, double paperW, double paperH)
{
    var sx = (frame.MaxX - frame.MinX) / paperW;
    var sy = (frame.MaxY - frame.MinY) / paperH;
    return LocalRectangle.FromPoints(frame.MaxX + mm.MinX * sx, frame.MinY + mm.MinY * sy, frame.MaxX + mm.MaxX * sx, frame.MinY + mm.MaxY * sy);
}

static string D(LocalRectangle r) => $"[{r.MinX:0.#}; {r.MinY:0.#}] - [{r.MaxX:0.#}; {r.MaxY:0.#}]";

var cases = new (string Name, LocalRectangle Frame, double PaperW, double PaperH, double Scale)[]
{
    ("A3 landscape 1:100", LocalRectangle.FromPoints(0, 0, 42000, 29700), 420, 297, 100),
    ("A4 portrait 1:100 (rect paper 297x210)", LocalRectangle.FromPoints(50000, 0, 71000, 29700), 297, 210, 100),
    ("A3 portrait 1:100 (rect paper 420x297)", LocalRectangle.FromPoints(80000, 0, 109700, 42000), 420, 297, 100),
};

var failures = 0;
foreach (var c in cases)
{
    var expected = LocalRectangle.FromPoints(c.Frame.MaxX + title.MinX * c.Scale, c.Frame.MinY + title.MinY * c.Scale,
        c.Frame.MaxX + title.MaxX * c.Scale, c.Frame.MinY + title.MaxY * c.Scale);
    var old = OldToWorld(title, c.Frame, c.PaperW, c.PaperH);
    FrameTextRegionGeometry.TryToWorld(title, c.Frame, c.PaperW, c.PaperH, out var fixedRegion);
    bool Same(LocalRectangle a) => Math.Abs(a.MinX - expected.MinX) < 1e-6 && Math.Abs(a.MinY - expected.MinY) < 1e-6
        && Math.Abs(a.MaxX - expected.MaxX) < 1e-6 && Math.Abs(a.MaxY - expected.MaxY) < 1e-6;
    Console.WriteLine($"{c.Name}\n  expected {D(expected)}\n  old      {D(old)} {(Same(old) ? "OK" : "WRONG")}\n  new      {D(fixedRegion)} {(Same(fixedRegion) ? "OK" : "WRONG")}");
    if (!Same(fixedRegion)) failures++;
}
return failures;
