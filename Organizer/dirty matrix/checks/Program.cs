using BehaviorRecognizer.Abstractions.Stroke;
using BehaviorRecognizer.Storage.Strokebin;
using DirtyMatrix.Core;
using DirtyMatrix.IO;
using System.Text.Json;
using StrokeSegment = DirtyMatrix.Core.StrokeSegment;

var tests = new List<(string Name, Action Run)>();
void Test(string name, Action run) => tests.Add((name, run));
void Equal<T>(T expected, T actual) where T : notnull
{ if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}"); }
void Close(double expected, double actual)
{ if (Math.Abs(expected - actual) > 1e-8) throw new Exception($"expected {expected}, got {actual}"); }
void Reject(Action run)
{ try { run(); } catch (ArgumentException) { return; } throw new Exception("invalid parameters accepted"); }
StrokeExtent Extent(params PointD[] points) { var e = new StrokeExtent(); foreach (var p in points) e.Add(p); return e; }
var settings = new CoverageSettings { CanvasWidth = 1000, CanvasHeight = 800, BrushSize = 20, ZoomPercent = 50,
    OriginX = -1000, OriginY = 100, Viewport = new(-1920, 0, 0, 1080),
    CoveragePaddingScreenPixels = 0, ClipToCanvasBounds = true };

Test("px brush ignores canvas pixel dimensions and DPI", () =>
{ Close(10, settings.BrushRadius); Close(10, (settings with { Dpi = 72, CanvasWidth = 5000 }).BrushRadius); });
Test("mm brush depends on canvas DPI", () =>
{ Close(300, (settings with { BrushUnit = BrushUnit.Mm, BrushSize = 25.4 }).NominalBrushPixels); Close(72, (settings with { BrushUnit = BrushUnit.Mm, BrushSize = 25.4, Dpi = 72 }).NominalBrushPixels); });
Test("screen-size mode and zoom conversion", () =>
{ Close(20, (settings with { SpecifyBySizeOnScreen = true }).BrushRadius); Close(8, settings.Radius * settings.Zoom); Close(13, (settings with { SpecifyBySizeOnScreen = true }).Radius * settings.Zoom); });
Test("R is added on all four sides with outward integer rounding", () =>
{ Equal(new PixelBox(84, 84, 217, 217), Extent(new(100,100), new(200,200)).Expand(settings)!.Value); });
Test("single-point stroke retains a brush footprint", () =>
{ Equal(new PixelBox(34,34,67,67), Extent(new PointD(50,50)).Expand(settings)!.Value); });
Test("hover ignored and empty strokes omitted", () =>
{ var e = Extent(new PointD(50,50)); e.Add(new(999,799), false); Equal(new PixelBox(34,34,67,67), e.Expand(settings)!.Value); Equal(true, new StrokeExtent().Expand(settings) is null); });
Test("bounds clip to canvas, including strokes centered just outside", () =>
{ Equal(new PixelBox(0,0,7,7), Extent(new PointD(-10,-10)).Expand(settings)!.Value); Equal(true, Extent(new PointD(-100,-100)).Expand(settings) is null); });
Test("screen and raw tablet mapping preserve absolute placement on a negative monitor", () =>
{
    Equal(new PointD(100,100), settings.InputToCanvas(new(-950,150), InputSpace.ScreenPixels));
    var s = settings with { RawArea = new(100,200,1100,1200), MappedScreen = new(-1000,100,-500,500) };
    Equal(new PointD(500,400), s.InputToCanvas(new(600,700), InputSpace.TabletRaw));
    Equal(new PointD(7,9), s.InputToCanvas(new(7,9), InputSpace.CanvasPixels));
});
Test("small-zoom projection and viewport clipping", () =>
{ var s = settings with { ZoomPercent = 12.5 }; Equal(new RectD(-1000,100,-987.5,112.5), s.Project(new(0,0,100,100))); Equal(new RectD(-990,100,-987.5,112.5), s.Project(new(0,0,100,100)).Intersect(new(-990,0,0,1080))); });
Test("invalid scales, DPI, nonfinite values and inverted mapping rejected", () =>
{ Reject(() => (settings with { ZoomPercent = 0 }).Validate()); Reject(() => (settings with { Dpi = 0 }).Validate()); Reject(() => (settings with { SafetyRadius = -1 }).Validate()); Reject(() => (settings with { BrushSize = double.NaN }).Validate()); Reject(() => (settings with { RawArea = new(10,0,5,9) }).Validate()); });
Test("sparse matrix matches independent per-cell union for random overlapping rectangles", () =>
{
    var matrix = new DirtyTileMatrix(500, 400, 32); var expected = new HashSet<(int Row, int Column)>(); var random = new Random(518);
    for (int i = 0; i < 500; i++)
    {
        int x = random.Next(-80, 550), y = random.Next(-80, 450);
        var box = new PixelBox(x,y,x + random.Next(1,190),y + random.Next(1,190)); matrix.Add(box);
        for (int row=0; row<matrix.Rows; row++) for (int col=0; col<matrix.Columns; col++)
            if (box.Left < Math.Min(500,(col+1)*32) && box.Right > col*32 && box.Top < Math.Min(400,(row+1)*32) && box.Bottom > row*32)
                expected.Add((row,col));
    }
    var actual = matrix.Runs.SelectMany(r => Enumerable.Range(r.Start,r.End-r.Start).Select(c => (r.Row,c))).ToHashSet();
    Equal(true, expected.SetEquals(actual)); Equal((long)expected.Count,matrix.DirtyCellCount);
});
Test("row interval union never propagates unrelated columns into later rows", () =>
{ var m = new DirtyTileMatrix(640,640,64); m.Add(new(0,0,320,64)); m.Add(new(256,0,320,128)); Equal(6L,m.DirtyCellCount); Equal(4,m.Runs.Last().Start); });
Test("100000px canvas uses row runs rather than allocating all tiles", () =>
{ var m=new DirtyTileMatrix(100000,100000,64); m.Add(new(0,0,100000,100000)); Equal(1563,m.Runs.Count()); Equal(2442969L,m.DirtyCellCount); });
bool Dirty(DirtyTileMatrix matrix, int x, int y) => matrix.Runs.Any(r =>
    r.Row == (int)Math.Floor((double)(y - matrix.Bounds.Top) / matrix.TileSize) &&
    r.Start <= (int)Math.Floor((double)(x - matrix.Bounds.Left) / matrix.TileSize) &&
    r.End > (int)Math.Floor((double)(x - matrix.Bounds.Left) / matrix.TileSize));
Test("default coverage adds a visible margin and does not infer a clipping boundary", () =>
{
    var s = new CoverageSettings(); Equal(false,s.ClipToCanvasBounds); Close(12,s.CoveragePaddingScreenPixels);
    Close(12,s.CoveragePaddingRadius*s.Zoom); Equal(s.Viewport,s.VisibleCanvas);
    var small=s with { ZoomPercent=18.3 }; Close(12,small.CoveragePaddingRadius*small.Zoom);
    Reject(() => (s with { CoveragePaddingScreenPixels=-1 }).Validate());
});
Test("saved settings from previous versions inherit expanded coverage defaults", () =>
{
    var s=JsonSerializer.Deserialize<CoverageSettings>("""{"CanvasWidth":4961,"ZoomPercent":18.3,"OriginX":780,"SafetyRadius":4}""")!;
    Equal(4961,s.CanvasWidth); Close(18.3,s.ZoomPercent); Close(780,s.OriginX);
    Close(12,s.CoveragePaddingScreenPixels); Equal(false,s.ClipToCanvasBounds);
});
Test("coverage and matrix continue past the former fixed right boundary", () =>
{
    var s = settings with { CanvasWidth=4961,CanvasHeight=7016,ZoomPercent=18.3,OriginX=780,OriginY=425,
        Viewport=new(0,0,2560,1600),ClipToCanvasBounds=false,CoveragePaddingScreenPixels=12 };
    var end=s.ScreenToCanvas(new(1900,600));
    var e=Extent(s.ScreenToCanvas(new(1600,600)),end); var box=e.Expand(s)!.Value;
    Equal(true,box.Right>s.CanvasWidth); Equal(true,s.VisibleCanvas.Contains(new(1900,600)));
    var m=DirtyTileMatrix.ForCoverage(s,[box]); m.AddCoverage(new(e.Segments,s.Radius));
    Equal(true,Dirty(m,(int)end.X,(int)end.Y)); Equal(true,m.Bounds.Right>s.CanvasWidth);
    var clipped=DirtyTileMatrix.ForCoverage(s with { ClipToCanvasBounds=true },[box]);
    clipped.AddCoverage(new(e.Segments,s.Radius)); Equal(s.CanvasWidth,clipped.Bounds.Right);
    Equal(false,Dirty(clipped,(int)end.X,(int)end.Y));
});
Test("expanded matrix includes negative coordinates with a stable canvas-aligned grid", () =>
{
    var s=settings with { ClipToCanvasBounds=false,TileSize=64 };
    var e=Extent(new(-100,-50),new(1200,900)); var box=e.Expand(s)!.Value;
    var m=DirtyTileMatrix.ForCoverage(s,[box]); m.AddCoverage(new(e.Segments,s.Radius));
    Equal(-128,m.Bounds.Left); Equal(-128,m.Bounds.Top);
    Equal(true,Dirty(m,-100,-50)); Equal(true,Dirty(m,1200,900));
    Equal(true,m.Runs.All(r => r.Row>=0 && r.Row<m.Rows && r.Start>=0 && r.End<=m.Columns));
    Equal(true,m.Boxes.All(b => b.Left>=m.Bounds.Left && b.Top>=m.Bounds.Top && b.Right<=m.Bounds.Right && b.Bottom<=m.Bounds.Bottom));
});
Test("diagonal coverage follows the line instead of its bounding rectangle", () =>
{
    var m = new DirtyTileMatrix(1000,800,16); m.Add(new StrokeSegment(new(100,100),new(900,700)),16);
    Equal(true, Dirty(m,100,100)); Equal(true, Dirty(m,500,400)); Equal(true, Dirty(m,900,700));
    Equal(false, Dirty(m,100,700)); Equal(false, Dirty(m,850,150));
});
Test("polyline turns stay connected and do not fill their interior", () =>
{
    var e = Extent(new(50,50),new(350,50),new(350,350)); var m = new DirtyTileMatrix(400,400,8);
    m.AddCoverage(new StrokeCoverage(e.Segments,4));
    Equal(true,Dirty(m,350,50)); Equal(true,Dirty(m,350,350)); Equal(false,Dirty(m,150,200));
});
Test("hover separates contact paths rather than joining separate marks", () =>
{
    var e = Extent(new PointD(20,20)); e.Add(new(100,100),false); e.Add(new(180,180));
    var m = new DirtyTileMatrix(200,200,4); m.AddCoverage(new StrokeCoverage(e.Segments,2));
    Equal(true,Dirty(m,20,20)); Equal(true,Dirty(m,180,180)); Equal(false,Dirty(m,100,100));
});
Test("single-point coverage is round and repeated points retain their footprint", () =>
{
    var e = Extent(new(100,100),new(100,100)); Equal(2,e.PointCount); Equal(1,e.Segments.Count);
    var m = new DirtyTileMatrix(200,200,4); m.AddCoverage(new StrokeCoverage(e.Segments,20));
    Equal(true,Dirty(m,100,100)); Equal(true,Dirty(m,120,100)); Equal(false,Dirty(m,116,116));
});
Test("long segments crossing canvas boundaries cover the complete visible portion", () =>
{
    var m = new DirtyTileMatrix(1000,800,8); m.Add(new StrokeSegment(new(-100,400),new(1200,400)),5);
    Equal(true,Dirty(m,0,400)); Equal(true,Dirty(m,999,400)); Equal(false,Dirty(m,500,500));
    Equal(true,m.Runs.All(r => r.Row >= 0 && r.Row < m.Rows && r.Start >= 0 && r.End <= m.Columns));
});
Test("screen raster retains subpixel coverage at small zoom", () =>
{
    var m = new DirtyTileMatrix(200,200,1); m.Add(new StrokeSegment(new(20.25,60.75),new(170.25,60.75)),.01);
    Equal(true,Dirty(m,20,60)); Equal(true,Dirty(m,100,60)); Equal(true,Dirty(m,170,60));
});
Test("100000px diagonal stays sparse and reaches the last partial tile", () =>
{
    var m = new DirtyTileMatrix(100000,99999,64); m.Add(new StrokeSegment(new(0,0),new(99999,99998)),2);
    Equal(true,Dirty(m,99999,99998)); Equal(true,m.DirtyCellCount < 7000); Equal(1563,m.Runs.Count());
});
// Independent distance-to-rectangle oracle: four edges plus endpoint distances.
double PointSegmentDistance(PointD p, PointD a, PointD b)
{
    double dx=b.X-a.X, dy=b.Y-a.Y, lengthSquared=dx*dx+dy*dy;
    double t=lengthSquared == 0 ? 0 : Math.Clamp(((p.X-a.X)*dx+(p.Y-a.Y)*dy)/lengthSquared,0,1);
    return Math.Sqrt(Math.Pow(p.X-a.X-t*dx,2)+Math.Pow(p.Y-a.Y-t*dy,2));
}
double SegmentRectDistance(StrokeSegment s, RectD r)
{
    PointD[] corners=[new(r.Left,r.Top),new(r.Right,r.Top),new(r.Right,r.Bottom),new(r.Left,r.Bottom)];
    double PointRectDistance(PointD p) => Math.Sqrt(Math.Pow(Math.Max(0,Math.Max(r.Left-p.X,p.X-r.Right)),2)+
        Math.Pow(Math.Max(0,Math.Max(r.Top-p.Y,p.Y-r.Bottom)),2));
    double distance=Math.Min(PointRectDistance(s.Start),PointRectDistance(s.End));
    for(int i=0;i<4;i++)
    {
        var a=corners[i]; var b=corners[(i+1)%4];
        double Cross(PointD p,PointD q,PointD v) => (q.X-p.X)*(v.Y-p.Y)-(q.Y-p.Y)*(v.X-p.X);
        if(Cross(s.Start,s.End,a)*Cross(s.Start,s.End,b)<0 && Cross(a,b,s.Start)*Cross(a,b,s.End)<0) return 0;
        distance=Math.Min(distance,PointSegmentDistance(a,s.Start,s.End));
        distance=Math.Min(distance,PointSegmentDistance(s.Start,a,b));
        distance=Math.Min(distance,PointSegmentDistance(s.End,a,b));
    }
    return distance;
}
Test("swept lines agree with independent per-cell geometry for random directions and radii", () =>
{
    var random = new Random(7016);
    for(int i=0;i<1000;i++)
    {
        PointD Point() => new(random.NextDouble()*650-100,random.NextDouble()*550-100);
        var a=Point(); var segment=new StrokeSegment(a,i%10==0 ? a : Point()); double radius=random.NextDouble()*100+.01;
        var m=new DirtyTileMatrix(451,353,32); m.Add(segment,radius);
        for(int row=0;row<m.Rows;row++) for(int col=0;col<m.Columns;col++)
        {
            var rect=new RectD(col*32,row*32,Math.Min(451,(col+1)*32),Math.Min(353,(row+1)*32));
            bool expected=SegmentRectDistance(segment,rect)<=radius;
            bool actual=Dirty(m,col*32,row*32);
            if(expected!=actual) throw new Exception($"sample {i}, tile ({col},{row}), radius {radius}, segment {segment}");
        }
    }
});

string tempDir = Path.Combine(Path.GetTempPath(), "dirty-matrix-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
byte[] BinaryFixture(ulong timestamp)
{
    var stroke = new Stroke { StrokeId=1, StartTimestampMs=timestamp, EndTimestampMs=timestamp+1 };
    stroke.Points.Add(new() { X=10,Y=20,Pressure=500,InContact=true,TimestampMs=timestamp });
    stroke.Points.Add(new() { X=30,Y=40,Pressure=0,InContact=false,TimestampMs=timestamp+1 });
    using var ms=new MemoryStream(); ms.Write(StrokeBinaryEncoder.EncodeHeader(new() { CreatedAtUnixMs=timestamp }));
    ms.Write(StrokeBinaryEncoder.EncodeEvent(StrokeEventType.StrokeStart,StrokeBinaryEncoder.EncodeStrokeStart(stroke)));
    foreach(var p in stroke.Points) ms.Write(StrokeBinaryEncoder.EncodeEvent(StrokeEventType.StrokePoint,StrokeBinaryEncoder.EncodeStrokePoint(1,p)));
    ms.Write(StrokeBinaryEncoder.EncodeEvent(StrokeEventType.StrokeEnd,StrokeBinaryEncoder.EncodeStrokeEnd(stroke)));
    return ms.ToArray();
}
Test("Recognizer encoder and linked reader agree; snapshots do not duplicate points", () =>
{ var path=Path.Combine(tempDir,"full.strokebin"); File.WriteAllBytes(path,BinaryFixture(1000)); var file=StrokeFiles.Read(path); Equal(1,file.Strokes.Count); Equal(2,file.Strokes[0].Points.Count); Equal(false,file.Strokes[0].Points[1].InContact); });
Test("open .part writer remains usable and incomplete tail is recovered", () =>
{
    var path=Path.Combine(tempDir,"live.strokebin.part"); using var writer=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.Read);
    writer.Write(BinaryFixture(1000)); writer.Write(new byte[]{3,255,0,0,0,1}); writer.Flush(true);
    var file=StrokeFiles.Read(path); Equal(2,file.Strokes[0].Points.Count);
    writer.WriteByte(0); writer.Flush(true); // Snapshot reader has released all source handles.
});
Test("simple canvas JSON and exported Recognizer JSON accepted", () =>
{
    var simple=Path.Combine(tempDir,"simple.json"); File.WriteAllText(simple,"""{"coordinateSpace":"canvas","strokes":[{"points":[{"x":2,"y":3}]}]}""");
    var a=StrokeFiles.Read(simple); Equal(InputSpace.CanvasPixels,a.DeclaredSpace!.Value); Equal(true,a.Strokes[0].Points[0].InContact);
    var exported=Path.Combine(tempDir,"export.json"); File.WriteAllText(exported,"""{"header":{"version":1},"segments":[{"strokes":[{"strokeId":4,"startTimestamp":"2026-10-02T00:00:00Z","points":[{"x":7,"y":9,"inContact":true}]}]}]}""");
    var b=StrokeFiles.Read(exported); Equal(true,b.DeclaredSpace is null); Equal("4",b.Strokes[0].Id); Equal(1790899200000UL,b.Strokes[0].StartTimestampMs);
});
Test("directory feed follows new strokes from an open Recognizer .part", () =>
{
    string dir=Path.Combine(tempDir,"feed"); Directory.CreateDirectory(dir); using var seen=new ManualResetEventSlim();
    using var feed=new StrokeFolderFeed(dir,(_,file) => { if (file.Strokes.Count>0) seen.Set(); }, _=>{});
    var path=Path.Combine(dir,"capture.strokebin.part"); using var writer=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.Read);
    writer.Write(BinaryFixture((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+5)); writer.Flush(true);
    Equal(true,seen.Wait(4000));
    feed.Dispose(); Equal(true,feed.Completion.Wait(2000));
});

int failed=0;
try
{
    foreach(var test in tests)
    {
        try { test.Run(); Console.WriteLine("PASS " + test.Name); }
        catch(Exception ex) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + ex.Message); }
    }
}
finally
{
    var resolved = Path.GetFullPath(tempDir);
    var tempRoot = Path.GetFullPath(Path.GetTempPath());
    if (!resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(resolved).StartsWith("dirty-matrix-check-", StringComparison.Ordinal))
        throw new InvalidOperationException("refusing to clean an unexpected check directory");
    Directory.Delete(resolved,true);
}
Console.WriteLine($"{tests.Count-failed}/{tests.Count} passed");
return failed == 0 ? 0 : 1;
