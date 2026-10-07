using System.Text.Json;

namespace CanvasLayerWatcher;

internal sealed record SavedLayerDocument(string SnapshotPath, long SaveDispatchedTicks, JsonElement ControlResponse);
internal sealed record BoundaryLayerCapture(CaptureRequest Request, CaptureResult Result);
internal interface ILayerCaptureBackend
{
    bool InputIdle { get; }
    Task<SavedLayerDocument> SaveSnapshotAsync(long triggerTicks, CancellationToken token);
    Task<IReadOnlyList<LayerReference>> InspectAsync(SavedLayerDocument document, CancellationToken token);
    Task<CaptureResult> ExportAsync(SavedLayerDocument document, LayerReference layer, Size size, CancellationToken token);
    void Cleanup(SavedLayerDocument document);
}

internal sealed class ClipLayerCaptureBackend(string clip, string bridge, RecorderControlClient control) : ILayerCaptureBackend
{
    public bool InputIdle => Native.InputIdle();
    public async Task<SavedLayerDocument> SaveSnapshotAsync(long triggerTicks, CancellationToken token)
    {
        var before = SaveCapture.Stat(clip);
        var response = await control.SaveClipAsync(clip, token, triggerTicks);
        string directory = Path.Combine(AppContext.BaseDirectory, "snapshots"); Directory.CreateDirectory(directory);
        string snapshot = Path.Combine(directory, "edit-boundary-" + Guid.NewGuid().ToString("N") + ".clip");
        try
        {
            await SaveCapture.StableSnapshotAsync(clip, snapshot, before, token, allowUnchanged: true);
            return new(snapshot, J.Tick(response, "saveInputDispatchedTicks", -1), response);
        }
        catch { SaveCapture.TryDelete(snapshot); throw; }
    }
    public async Task<IReadOnlyList<LayerReference>> InspectAsync(SavedLayerDocument document, CancellationToken token)
    {
        using var inspection = JsonDocument.Parse(await Bridge.RunAsync(bridge, ["inspect", document.SnapshotPath], token));
        return LayerMapping.ReadLayers(inspection.RootElement);
    }
    public async Task<CaptureResult> ExportAsync(SavedLayerDocument document, LayerReference layer, Size size, CancellationToken token)
    {
        string png = Path.ChangeExtension(document.SnapshotPath, null) + "-" + layer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png";
        try
        {
            string metadata = await Bridge.RunAsync(bridge, ["export-id", document.SnapshotPath,
                layer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), png,
                size.Width.ToString(System.Globalization.CultureInfo.InvariantCulture), size.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)], token);
            return new(png, metadata, layer.Name, document.SaveDispatchedTicks, document.ControlResponse);
        }
        catch { SaveCapture.TryDelete(png); throw; }
    }
    public void Cleanup(SavedLayerDocument document) => SaveCapture.TryDelete(document.SnapshotPath);
}
