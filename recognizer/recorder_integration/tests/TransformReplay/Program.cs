using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Core;
using ScreenCanvasTransform.Detection;
using ScreenCanvasTransform.Models;

using var job = JsonDocument.Parse(File.ReadAllText(args[0]));
var crops = job.RootElement.GetProperty("crops");
var frameInfo = crops.GetProperty("__canvas_frame__");
var fr = frameInfo.GetProperty("roi");
using var inputFrame = new Bitmap(frameInfo.GetProperty("path").GetString()!);
var frame = new Bitmap(inputFrame.Width, inputFrame.Height, PixelFormat.Format32bppArgb);
using (var g = Graphics.FromImage(frame)) g.DrawImageUnscaled(inputFrame, 0, 0);
using var session = new CaptureSession("recorder-replay", frame, fr[0].GetInt32(), fr[1].GetInt32(), 144, 144);
IntRect Roi(string key) {
    var a = crops.GetProperty(key).GetProperty("roi");
    return IntRect.FromXYWH(a[0].GetInt32()-session.OriginX,a[1].GetInt32()-session.OriginY,a[2].GetInt32(),a[3].GetInt32());
}
var wr = Roi("画布视口"); var nr = Roi("导航器"); var dr = Roi("导航器数字");
session.TrySetRoi(RoiKind.WorkspaceUser, wr, out _);
session.TrySetRoi(RoiKind.Navigator, nr, out _);
var native = new SctNativeService();
var ws = native.DetectWorkspace(session);
Console.WriteLine($"Workspace: status={ws.Status} roi={ws.RectCapturePx} bg={ws.Background?.ToNative().CenterLabL}");
var nav = native.DetectNavigatorThumbnailCii(session, nr, ws.Background!);
Console.WriteLine($"Thumbnail: status={nav.Status} roi={nav.RectCapturePx}");
if (ws.Background is {} background && nav.Success) {
    foreach (bool withSize in new[]{false,true}) {
        var obs = native.ObserveCanvas(session, nav.RectCapturePx, background, true,
            withSize ? 4961 : 0, withSize ? 7016 : 0);
        Console.WriteLine($"Navigator withSize={withSize}: bounds={obs.BoundsCapture} ambiguous={obs.Ambiguous} reason={obs.AmbiguityReason}");
    }
}
using var core = new ScreenCanvasTransformCore(new() { RapidOcrModelDirectory=Path.GetFullPath("recognizer/Recognizer/publish/win-x64/integration/transform_host/models/v5") });
var result = await core.InitializeFrameAsync(new() { Image=inputFrame,WorkspaceRoi=wr,NavigatorRoi=nr,OcrNumbersRoi=dr,
    OriginX=session.OriginX,OriginY=session.OriginY,DpiX=144,DpiY=144,CanvasPixelWidth=4961,CanvasPixelHeight=7016 });
Console.WriteLine(JsonSerializer.Serialize(new { result.Success,result.Status,result.Message,result.CanvasOriginScreenPx,result.OcrScalePercent,result.OcrRotationDegrees }));
