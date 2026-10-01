extern alias original;
extern alias extracted;

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Core = extracted::ScreenCanvasTransform.Core.ScreenCanvasTransformCore;
using CoreConfig = extracted::ScreenCanvasTransform.Core.ScreenCanvasTransformCoreConfig;
using CoreInput = extracted::ScreenCanvasTransform.Core.ScreenCanvasTransformInput;
using CoreInit3 = extracted::ScreenCanvasTransform.Core.ScreenCanvasTransformInitializationInput;
using CoreFrame3 = extracted::ScreenCanvasTransform.Core.ScreenCanvasTransformFrameInput;
using CoreCapture = extracted::ScreenCanvasTransform.Core.ScreenCanvasRegionCapture;
using CoreRect = extracted::ScreenCanvasTransform.Models.IntRect;
using CoreResult = extracted::ScreenCanvasTransform.Core.ScreenCanvasTransformCoreResult;
using OldSession = original::ScreenCanvasTransform.Capture.CaptureSession;
using OldRoiKind = original::ScreenCanvasTransform.Capture.RoiKind;
using OldRect = original::ScreenCanvasTransform.Models.IntRect;
using OldPipeline = original::ScreenCanvasTransform.Services.TransformPipelineService;
using OldOcr = original::ScreenCanvasTransform.Ocr.NavigatorOcrService;
using OldNative = original::ScreenCanvasTransform.Detection.SctNativeService;
using OldFailure = original::ScreenCanvasTransform.Services.PipelineFailureException;
using OldPipelineResult = original::ScreenCanvasTransform.Services.PipelineResult;
using OldDetect = original::ScreenCanvasTransform.Detection.DetectOutcome;
using CoreSession = extracted::ScreenCanvasTransform.Capture.CaptureSession;
using CoreNative = extracted::ScreenCanvasTransform.Detection.SctNativeService;
using CoreBackground = extracted::ScreenCanvasTransform.Models.WorkspaceBackgroundModel;
using CoreRoiKind = extracted::ScreenCanvasTransform.Capture.RoiKind;

string inputPath = args.ElementAtOrDefault(0) ?? throw new ArgumentException("image path required");
string outputPath = args.ElementAtOrDefault(1) ?? "probe.json";
string label = args.ElementAtOrDefault(2) ?? "unspecified";
string modelDirectory = Path.Combine(AppContext.BaseDirectory, "models", "v5");
using var image = new Bitmap(inputPath);
int[] workspace = [0, 29, 1790, 1301];
int[] navigator = [1810, 0, 342, 438];
int[] numbers = [1810, 365, 133, 70];
if (args.Length>3) workspace = args[3].Split(',').Select(int.Parse).ToArray();
if (args.Length>4) navigator = args[4].Split(',').Select(int.Parse).ToArray();
if (args.Length>5) numbers = args[5].Split(',').Select(int.Parse).ToArray();
int[] diagnosticFrozenThumbnail = [1810, 30, 342, 332];
float dpi=args.Length>6 ? float.Parse(args[6],System.Globalization.CultureInfo.InvariantCulture) : 96;
if(args.Length>7) diagnosticFrozenThumbnail=args[7].Split(',').Select(int.Parse).ToArray();
var options = new JsonSerializerOptions { WriteIndented = true, IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
var results = new List<object>();
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
CoreRect CR(int[] r) => CoreRect.FromXYWH(r[0], r[1], r[2], r[3]);
OldRect OR(int[] r) => OldRect.FromXYWH(r[0], r[1], r[2], r[3]);
Bitmap Crop(int[] r) => image.Clone(new Rectangle(r[0], r[1], r[2], r[3]), PixelFormat.Format32bppArgb);
int[] ArrayRect(CoreRect r) => [r.Left, r.Top, r.Width, r.Height];

async Task Run(string name, Func<Task<object>> action)
{
    var timer = Stopwatch.StartNew();
    try { var result = await action(); results.Add(new { name, elapsedMs = timer.Elapsed.TotalMilliseconds, result }); }
    catch (Exception ex) { results.Add(new { name, elapsedMs = timer.Elapsed.TotalMilliseconds, error = new { type = ex.GetType().FullName, ex.Message, stack = ex.StackTrace } }); }
    File.WriteAllText(outputPath, JsonSerializer.Serialize(new {
        label, inputPath, inputSha256 = Hash(inputPath), imageWidth = image.Width, imageHeight = image.Height,
        imagePixelFormat = image.PixelFormat.ToString(), imageDpiX = image.HorizontalResolution, imageDpiY = image.VerticalResolution,
        coordinateSystem = "Attachment pixels; origin=(0,0), no resampling. The attachment is a cropped screen, not a whole desktop.",
        dpi, canvasWidth = 4961, canvasHeight = 7016, initialWorkspaceRoi = workspace, initialNavigatorRoi = navigator, numbersRoi = numbers,
        coreAssembly = typeof(Core).Assembly.Location, coreSha256 = Hash(typeof(Core).Assembly.Location),
        nativeSha256 = Hash(Path.Combine(AppContext.BaseDirectory, "ScreenCanvasNative.dll")),
        loadedNativeModules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m=>m.ModuleName.Contains("ScreenCanvasNative",StringComparison.OrdinalIgnoreCase)).Select(m=>new { m.FileName, sha256 = Hash(m.FileName) }).ToArray(), results
    }, options));
    Console.WriteLine($"{name} {timer.Elapsed.TotalMilliseconds:F1}ms");
}

Core MakeCore() => new(new CoreConfig { RapidOcrModelDirectory = modelDirectory });
CoreInput FullInput() => new() {
    Image = image, WorkspaceRoi = CR(workspace), NavigatorRoi = CR(navigator), OcrNumbersRoi = CR(numbers),
    CanvasPixelWidth = 4961, CanvasPixelHeight = 7016, OriginX = 0, OriginY = 0, DpiX = dpi, DpiY = dpi
};
byte[] Pixels(Bitmap value) {
    var data=value.LockBits(new Rectangle(0,0,value.Width,value.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
    try {
        byte[] bytes=new byte[value.Width*value.Height*4];
        for(int row=0;row<value.Height;row++) Marshal.Copy(data.Scan0+row*data.Stride,bytes,row*value.Width*4,value.Width*4);
        return bytes;
    } finally { value.UnlockBits(data); }
}

await Run("bitmap_conversion_pixel_audit", () => {
    using var converted = (Bitmap)typeof(Core).GetMethod("ToArgb32",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,[image])!;
    using var exact = image.Clone(new Rectangle(0,0,image.Width,image.Height),PixelFormat.Format32bppArgb);
    var before=Pixels(image); var after=Pixels(converted); var clone=Pixels(exact);
    int difference=0,left=image.Width,top=image.Height,right=-1,bottom=-1;
    for(int p=0;p<before.Length;p+=4) if(!before.AsSpan(p,4).SequenceEqual(after.AsSpan(p,4))) {
        difference++;int x=(p/4)%image.Width,y=(p/4)/image.Width;
        left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);
    }
    using var originalPixelsSession=new CoreSession("probe-pixels-original",(Bitmap)exact.Clone(),0,0,dpi,dpi);
    using var convertedSession=new CoreSession("probe-pixels-converted",(Bitmap)converted.Clone(),0,0,dpi,dpi);
    originalPixelsSession.TrySetRoi(CoreRoiKind.WorkspaceUser,CR(workspace),out _);
    convertedSession.TrySetRoi(CoreRoiKind.WorkspaceUser,CR(workspace),out _);
    var native=new CoreNative();
    converted.Save(Path.ChangeExtension(outputPath,"converted.png"),ImageFormat.Png);
    return Task.FromResult<object>(new {
        originalDpi=image.HorizontalResolution,convertedDpi=converted.HorizontalResolution,originalHash=Convert.ToHexString(SHA256.HashData(before)),convertedHash=Convert.ToHexString(SHA256.HashData(after)),exactCloneHash=Convert.ToHexString(SHA256.HashData(clone)),
        differentPixels=difference,differenceBounds=new[]{left,top,right,bottom},
        originalPixelDetect=native.DetectWorkspace(originalPixelsSession),convertedPixelDetect=native.DetectWorkspace(convertedSession)
    });
});

await Run("three_crop_composition_pixel_audit", () => {
    using var wi=Crop(workspace);using var ni=Crop(navigator);using var oi=Crop(numbers);
    var input=new CoreInit3 {
        Workspace=new CoreCapture { Image=wi,ScreenX=workspace[0],ScreenY=workspace[1] },
        Navigator=new CoreCapture { Image=ni,ScreenX=navigator[0],ScreenY=navigator[1] },
        OcrNumbers=new CoreCapture { Image=oi,ScreenX=numbers[0],ScreenY=numbers[1] },
        CanvasPixelWidth=4961,CanvasPixelHeight=7016,DpiX=dpi,DpiY=dpi
    };
    object?[] callArgs=[input,null];
    using var composed=(Bitmap)typeof(Core).GetMethod("ComposeInitializationFrame",BindingFlags.NonPublic|BindingFlags.Static)!.Invoke(null,callArgs)!;
    var descriptor=(CoreInput)callArgs[1]!;
    object Audit(Bitmap source,CoreRect roi) {
        using var region=composed.Clone(new Rectangle(roi.Left,roi.Top,roi.Width,roi.Height),PixelFormat.Format32bppArgb);
        var src=Pixels(source);var dst=Pixels(region);
        int different=0;
        for(int p=0;p<src.Length;p+=4) if(!src.AsSpan(p,4).SequenceEqual(dst.AsSpan(p,4))) different++;
        return new {roi,sourceDpi=source.HorizontalResolution,sourceSha256=Convert.ToHexString(SHA256.HashData(src)),composedRegionSha256=Convert.ToHexString(SHA256.HashData(dst)),differentPixels=different};
    }
    return Task.FromResult<object>(new { composed.Width,composed.Height,originX=descriptor.OriginX,originY=descriptor.OriginY,workspace=Audit(wi,descriptor.WorkspaceRoi),navigator=Audit(ni,descriptor.NavigatorRoi),numbers=Audit(oi,descriptor.OcrNumbersRoi),
        note="Coarse navigation and numeric ROIs overlap, so workspace is the clean invariant; overlap can intentionally overwrite crop pixels." });
});

// Call the original app's pipeline on the complete attachment, with the same ROIs.
OldPipelineResult? originalResult = null;
OldDetect? originalWorkspace = null;
OldDetect? originalThumbnail = null;
await Run("original_complete_frame", async () => {
    using var session = new OldSession("probe-original", (Bitmap)image.Clone(), OldRect.FromXYWH(0, 0, image.Width, image.Height), dpi, dpi);
    if (!session.TrySetRoi(OldRoiKind.WorkspaceUser, OR(workspace), out var error)
        || !session.TrySetRoi(OldRoiKind.Navigator, OR(navigator), out error)
        || !session.TrySetRoi(OldRoiKind.OcrNumbers, OR(numbers), out error)) throw new ArgumentException(error);
    var pipeline = new OldPipeline();
    pipeline.BeginNewInitializationGeneration(4961, 7016);
    originalWorkspace = pipeline.DetectWorkspace(session);
    if (!originalWorkspace.Success) return new { success = false, stage = "workspace", workspace = originalWorkspace };
    originalThumbnail = pipeline.DetectNavigatorThumbnail(session, originalWorkspace);
    if (!originalThumbnail.Success) return new { success = false, stage = "thumbnail", workspace = originalWorkspace, thumbnail = originalThumbnail };
    var ocr = new OldOcr();
    var probe = await ocr.TryReadUserRegionAsync(session, OR(numbers));
    if (!probe.Ok) return new { success = false, stage = "ocr", workspace = originalWorkspace, thumbnail = originalThumbnail, numbers = probe.Numbers };
    try {
        originalResult = await pipeline.ContinueAfterThumbnailAsync(session, originalWorkspace, originalThumbnail, fixedOcrLayout: probe.Layout);
        var again = await pipeline.RecomputeAsync(session, originalResult);
        return new { success = true, workspace = originalWorkspace, thumbnail = originalThumbnail, initial = originalResult, recompute = again };
    }
    catch (OldFailure ex) {
        return new { success = false, stage = ex.Stage, status = ex.Status, message = ex.Message, evidence = ex.EvidenceSummary, workspace = originalWorkspace, thumbnail = originalThumbnail, numbers = probe.Numbers };
    }
});

// Direct completion retains diagnostics even when the orchestration rejects its result.
if (originalWorkspace?.Success == true)
{
    await Run("diagnostic_native_same_frozen_roi_image_coverage", () => {
        var native = new OldNative();
        using var session = new OldSession("probe-direct-full", (Bitmap)image.Clone(), OldRect.FromXYWH(0, 0, image.Width, image.Height), dpi, dpi);
        var bg = originalWorkspace.Background!;
        var ws = native.ObserveCanvas(session, originalWorkspace.RectCapturePx, bg);
        var thumbnail = OR(diagnosticFrozenThumbnail);
        var nav = native.ObserveCanvas(session, thumbnail, bg, navigator: true, canvasPixelWidth:4961,canvasPixelHeight:7016);
        var relation = native.BuildWorkspaceCanvasRelation(session, originalWorkspace.RectScreenPhysicalPx, ws, 4961, 7016);
        var full = native.CompleteViewportFrame(session, thumbnail, nav.BoundsCapture, relation, 0, 1);
        var r = thumbnail;
        using var limited = new OldSession("probe-direct-crop", image.Clone(new Rectangle(r.Left, r.Top, r.Width, r.Height), PixelFormat.Format32bppArgb),
            OldRect.FromXYWH(r.Left, r.Top, r.Width, r.Height), dpi, dpi);
        var navLocal = new OldRect(nav.BoundsCapture.Left-r.Left, nav.BoundsCapture.Top-r.Top, nav.BoundsCapture.Right-r.Left, nav.BoundsCapture.Bottom-r.Top);
        var thumbnailOnly = native.CompleteViewportFrame(limited, OldRect.FromXYWH(0, 0, r.Width, r.Height), navLocal, relation, 0, 1);
        using var cs = new CoreSession("probe-core-native", (Bitmap)image.Clone(), 0, 0, dpi, dpi);
        var cn = new CoreNative();
        var cb = new CoreBackground { CenterLabL=bg.CenterLabL,CenterLabA=bg.CenterLabA,CenterLabB=bg.CenterLabB,StrongDeltaE=bg.StrongDeltaE,WeakDeltaE=bg.WeakDeltaE,Confidence=bg.Confidence };
        var cw = new CoreRect(originalWorkspace.RectCapturePx.Left,originalWorkspace.RectCapturePx.Top,originalWorkspace.RectCapturePx.Right,originalWorkspace.RectCapturePx.Bottom);
        var cws = cn.ObserveCanvas(cs, cw, cb);
        var cnav = cn.ObserveCanvas(cs,CR(diagnosticFrozenThumbnail),cb,navigator:true,canvasPixelWidth:4961,canvasPixelHeight:7016);
        var cr = cn.BuildWorkspaceCanvasRelation(cs,cw,cws,4961,7016);
        var extractedImplicit = cn.CompleteViewportFrame(cs,CR(diagnosticFrozenThumbnail),cnav.BoundsCapture,cr,0,1);
        typeof(CoreSession).GetProperty("NavigatorEvidenceRoiCapturePx")?.SetValue(cs,CoreRect.FromXYWH(0,0,image.Width,image.Height));
        var extractedFullEvidence = cn.CompleteViewportFrame(cs,CR(diagnosticFrozenThumbnail),cnav.BoundsCapture,cr,0,1);
        return Task.FromResult<object>(new { manualFrozenThumbnail = diagnosticFrozenThumbnail, workspace = ws, navigator = nav, relation, full, thumbnailOnly, extractedImplicit, extractedFullEvidence,
            note = "Full endpoints are attachment-local; thumbnailOnly endpoints are thumbnail-local. Same native DLL, same frozen ROIs and relation, only image coverage differs." });
    });
}

using var fullCore = MakeCore();
CoreResult? fullResult = null;
await Run("extracted_initialize_complete_frame", async () => fullResult = await fullCore.InitializeFrameAsync(FullInput()));
if (fullResult?.Success == true)
{
    await Run("extracted_recompute_complete_frame", async () => await fullCore.RecomputeAsync(image, 0, 0, dpi, dpi));
    await Run("extracted_recompute_exact_frozen_three_crops", async () => {
        var w = ArrayRect(fullResult.CanvasWindowRoiScreenPx!.Value);
        var n = ArrayRect(fullResult.NavigatorThumbnailRoiScreenPx!.Value);
        using var wi = Crop(w); using var ni = Crop(n); using var oi = Crop(numbers);
        return new { workspaceCrop = w, navigatorCrop = n, numbersCrop = numbers,
            result = await fullCore.RecomputeAsync(new CoreFrame3 {
                Workspace = new CoreCapture { Image = wi, ScreenX = w[0], ScreenY = w[1] },
                Navigator = new CoreCapture { Image = ni, ScreenX = n[0], ScreenY = n[1] },
                OcrNumbers = new CoreCapture { Image = oi, ScreenX = numbers[0], ScreenY = numbers[1] }, DpiX = dpi, DpiY = dpi
            }) };
    });
}

await Run("extracted_initialize_three_coarse_crops", async () => {
    using var core = MakeCore();
    using var wi = Crop(workspace); using var ni = Crop(navigator); using var oi = Crop(numbers);
    return await core.InitializeAsync(new CoreInit3 {
        Workspace = new CoreCapture { Image = wi, ScreenX = workspace[0], ScreenY = workspace[1] },
        Navigator = new CoreCapture { Image = ni, ScreenX = navigator[0], ScreenY = navigator[1] },
        OcrNumbers = new CoreCapture { Image = oi, ScreenX = numbers[0], ScreenY = numbers[1] },
        CanvasPixelWidth = 4961, CanvasPixelHeight = 7016, DpiX = dpi, DpiY = dpi
    });
});
Console.WriteLine(Path.GetFullPath(outputPath));
