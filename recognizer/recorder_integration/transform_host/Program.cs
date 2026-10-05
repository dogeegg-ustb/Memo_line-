using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenCanvasTransform.Core;
using ScreenCanvasTransform.Models;

// One persistent core, one serial caller. stdout is exclusively JSONL IPC.
Console.InputEncoding = System.Text.Encoding.UTF8;
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
using var core = new ScreenCanvasTransformCore(new() {
    RapidOcrModelDirectory = Environment.GetEnvironmentVariable("MEMOLINE_OCR_MODELS")
        ?? Path.Combine(AppContext.BaseDirectory,"models","v5") });
while (Console.ReadLine() is { } line)
{
    try
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var crops = root.GetProperty("crops");
        var frameEvidence = root.GetProperty("frame");
        using var frame = new Bitmap(frameEvidence.GetProperty("path").GetString()!);
        var frameRoi = frameEvidence.GetProperty("roi");
        if (frameRoi.GetArrayLength()!=4 || frame.Width!=frameRoi[2].GetInt32()
            || frame.Height!=frameRoi[3].GetInt32())
            throw new ArgumentException("frame: image dimensions must exactly match its physical screen rectangle.");
        int originX=frameRoi[0].GetInt32(), originY=frameRoi[1].GetInt32();
        IntRect LocalRoi(string name)
        {
            var roi = crops.GetProperty(name).GetProperty("roi");
            if (roi.GetArrayLength()!=4)
                throw new ArgumentException($"{name}: expected a physical screen ROI.");
            var local=IntRect.FromXYWH(roi[0].GetInt32()-originX,roi[1].GetInt32()-originY,
                roi[2].GetInt32(),roi[3].GetInt32());
            if (local.IsEmpty || local.Left<0 || local.Top<0 || local.Right>frame.Width || local.Bottom>frame.Height)
                throw new ArgumentException($"{name}: ROI must stay inside the original frame.");
            return local;
        }
        var workspaceRoi=LocalRoi("workspace");
        var navigatorRoi=LocalRoi("navigator");
        var numbersRoi=LocalRoi("numbers");
        float dpi = root.TryGetProperty("dpi",out var dpiValue) ? dpiValue.GetSingle() : 96;
        // One real, untouched frame with local ROIs, as in the original application.
        // Native red-line completion can inspect factual pixels beyond the thumbnail.
        // Recompute keeps the core's initial workspace/thumbnail/OCR anchors.
        ScreenCanvasTransformCoreResult result = root.GetProperty("initialize").GetBoolean()
            ? await core.InitializeFrameAsync(new ScreenCanvasTransformInput {
                Image=frame, WorkspaceRoi=workspaceRoi, NavigatorRoi=navigatorRoi,
                OcrNumbersRoi=numbersRoi, OriginX=originX,OriginY=originY,DpiX=dpi,DpiY=dpi,
                CanvasPixelWidth = root.GetProperty("width").GetInt32(),
                CanvasPixelHeight = root.GetProperty("height").GetInt32() })
            : await core.RecomputeAsync(frame,originX,originY,dpi,dpi);
        ScreenPoint[]? completedViewport = null;
        if (result.Success && result.Snapshot is { UsedDirectWorkspacePath: false } snapshot
            && result.ScreenCoordinateOriginScreenPx is { } captureOrigin)
        {
            var viewport = snapshot.Raw.Viewport;
            if (viewport.Status == 0 && viewport.Width > 0 && viewport.Height > 0)
                completedViewport = new[] { viewport.Corner0, viewport.Corner1, viewport.Corner2, viewport.Corner3 }
                    .Select(p => new ScreenPoint(p.X+captureOrigin.X, p.Y+captureOrigin.Y)).ToArray();
        }
        // Expose calibrated digit rectangles in physical screen pixels without serializing native pipeline details.
        Console.WriteLine(JsonSerializer.Serialize(new { result.Success, result.CanvasWindowRoiScreenPx,
            result.NavigatorThumbnailRoiScreenPx, result.ScreenCoordinateOriginScreenPx,
            result.CanvasOriginScreenPx, result.OcrScalePercent, result.OcrRotationDegrees,
            completedViewportScreenPx = completedViewport,
            ocrLayout = result.PipelineState?.OcrLayoutUsed,
            result.Snapshot, result.FailedStage, result.Status, result.Message }, options));
    }
    catch (Exception ex) { Console.WriteLine(JsonSerializer.Serialize(new { success = false, message = ex.Message }, options)); }
}
