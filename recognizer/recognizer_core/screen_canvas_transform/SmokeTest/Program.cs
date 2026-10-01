using System.Drawing;
using System.Text.Json;
using ScreenCanvasTransform.Core;
using ScreenCanvasTransform.Models;
using ScreenCanvasTransform.Services;

if (args.Length == 0)
    throw new ArgumentException("Usage: SmokeTest init3 <workspace-image> <screen-x,y> <navigator-image> <screen-x,y> <ocr-image> <screen-x,y> <canvas-w> <canvas-h> [rapid-model-dir]");

static IntRect Rect(string value)
{
    int[] v = value.Split(',').Select(int.Parse).ToArray();
    if (v.Length != 4) throw new ArgumentException("ROI must be x,y,width,height.");
    return IntRect.FromXYWH(v[0], v[1], v[2], v[3]);
}

static (int X, int Y) Point(string value)
{
    int[] v = value.Split(',').Select(int.Parse).ToArray();
    if (v.Length != 2) throw new ArgumentException("Screen position must be x,y.");
    return (v[0], v[1]);
}

static void Print(ScreenCanvasTransformCoreResult result) => Console.WriteLine(JsonSerializer.Serialize(new
{
    result.Success,
    result.CanvasWindowRoiScreenPx,
    result.NavigatorThumbnailRoiScreenPx,
    result.ScreenCoordinateOriginScreenPx,
    result.CanvasOriginScreenPx,
    result.OcrScalePercent,
    result.OcrRotationDegrees,
    result.FailedStage,
    result.Status,
    result.Message
}, new JsonSerializerOptions { WriteIndented = true }));

if (args[0] == "init3")
{
    if (args.Length < 9)
        throw new ArgumentException("Usage: SmokeTest init3 <workspace-image> <screen-x,y> <navigator-image> <screen-x,y> <ocr-image> <screen-x,y> <canvas-w> <canvas-h> [rapid-model-dir]");
    using var workspace = new Bitmap(args[1]);
    using var navigator = new Bitmap(args[3]);
    using var numbers = new Bitmap(args[5]);
    var wp = Point(args[2]);
    var np = Point(args[4]);
    var op = Point(args[6]);
    using var initCore = new ScreenCanvasTransformCore(new ScreenCanvasTransformCoreConfig
    {
        RapidOcrModelDirectory = args.Length > 9 ? args[9] : null
    });
    Print(await initCore.InitializeAsync(new ScreenCanvasTransformInitializationInput
    {
        Workspace = new ScreenCanvasRegionCapture { Image = workspace, ScreenX = wp.X, ScreenY = wp.Y },
        Navigator = new ScreenCanvasRegionCapture { Image = navigator, ScreenX = np.X, ScreenY = np.Y },
        OcrNumbers = new ScreenCanvasRegionCapture { Image = numbers, ScreenX = op.X, ScreenY = op.Y },
        CanvasPixelWidth = int.Parse(args[7]),
        CanvasPixelHeight = int.Parse(args[8])
    }));
    return;
}

if (args[0] == "recompute3")
{
    if (args.Length < 9)
        throw new ArgumentException("Usage: SmokeTest recompute3 <archive-dir> <archive-id> <workspace-image> <screen-x,y> <navigator-image> <screen-x,y> <ocr-image> <screen-x,y> [rapid-model-dir]");
    var loaded = new SaveArchiveService(args[1]).TryLoad(args[2]);
    if (!loaded.Success || loaded.Archive is null)
        throw new ArgumentException($"Cannot load archive: {loaded.Error}");
    using var workspace = new Bitmap(args[3]);
    using var navigator = new Bitmap(args[5]);
    using var numbers = new Bitmap(args[7]);
    var wp = Point(args[4]);
    var np = Point(args[6]);
    var op = Point(args[8]);
    using var recomputeCore = new ScreenCanvasTransformCore(new ScreenCanvasTransformCoreConfig
    {
        RapidOcrModelDirectory = args.Length > 9 ? args[9] : null
    });
    Print(await recomputeCore.RecomputeFromArchiveAsync(new ScreenCanvasTransformFrameInput
    {
        Workspace = new ScreenCanvasRegionCapture { Image = workspace, ScreenX = wp.X, ScreenY = wp.Y },
        Navigator = new ScreenCanvasRegionCapture { Image = navigator, ScreenX = np.X, ScreenY = np.Y },
        OcrNumbers = new ScreenCanvasRegionCapture { Image = numbers, ScreenX = op.X, ScreenY = op.Y }
    }, loaded.Archive));
    return;
}

if (args.Length < 6)
    throw new ArgumentException("Usage: SmokeTest <image> <workspace x,y,w,h> <navigator x,y,w,h> <ocr-numbers x,y,w,h> <canvas-w> <canvas-h> [rapid-model-dir]");

using var image = new Bitmap(args[0]);
using var core = new ScreenCanvasTransformCore(new ScreenCanvasTransformCoreConfig
{
    RapidOcrModelDirectory = args.Length > 6 ? args[6] : null
});
var result = await core.ProcessAsync(new ScreenCanvasTransformInput
{
    Image = image,
    WorkspaceRoi = Rect(args[1]),
    NavigatorRoi = Rect(args[2]),
    OcrNumbersRoi = Rect(args[3]),
    CanvasPixelWidth = int.Parse(args[4]),
    CanvasPixelHeight = int.Parse(args[5]),
});
Print(result);
