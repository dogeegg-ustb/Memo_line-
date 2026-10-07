using System.Drawing.Imaging;
using System.Text.Json;
using CanvasLayerWatcher;

internal static class BaselineBarrierChecks
{
    internal static async Task RunAsync(Action<bool, string> check, string artifacts)
    {
        async Task Scenario(bool everyAttempt)
        {
            string root = Path.Combine(artifacts, "baseline-barrier-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            var frames = new Queue<JsonElement>();
            ulong appendId = 0; long lastTicks = 0; int barriers = 0, captures = 0, commits = 0;
            void Append(string kind)
            {
                appendId++; lastTicks = checked((long)appendId * 10);
                frames.Enqueue(JsonSerializer.SerializeToElement(new { kind, appendId, ticks = lastTicks }));
            }
            Task<BaselineBarrier> Barrier(string requestId, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); barriers++;
                // The contact existed in the input queue during saving, and only
                // becomes visible in the mechanical stream when the after barrier flushes it.
                if ((barriers & 1) == 0 && (everyAttempt || captures == 1))
                { Append("penBegin"); Append("penEnd"); }
                Append("editBoundaryRequested");
                return Task.FromResult(new BaselineBarrier("baseline", requestId, appendId, lastTicks));
            }
            Task<BaselineCaptureResult> Capture(CancellationToken token)
            {
                captures++;
                string path = Path.Combine(root, captures + ".png");
                using (var image = new Bitmap(8, 8)) image.Save(path, ImageFormat.Png);
                var metadata = JsonSerializer.SerializeToElement(new { width = 8, height = 8, fullCanvas = true, layerId = 1, layerUuid = "baseline-layer" });
                return Task.FromResult(new BaselineCaptureResult([new(new(1, "Ink", "baseline-layer"), path, metadata)],
                    lastTicks + 1, JsonSerializer.SerializeToElement(new { success = true })));
            }
            void Commit(BaselineCaptureResult capture, long ticks)
            {
                commits++;
                check(captures == 2 && frames.Count == 0 && File.Exists(capture.Layers[0].PngPath)
                    && ticks == capture.SaveDispatchedTicks, "A baseline is committed only after both flushed queue barriers are read");
            }
            IReadOnlyList<JsonElement> ReadOne() => frames.TryDequeue(out var frame) ? [frame] : [];
            if (everyAttempt)
            {
                try
                {
                    await RecordingBaselineInitialization.RunAsync("baseline", Barrier, Capture, ReadOne, Commit, _ => { }, CancellationToken.None);
                    throw new Exception("A repeatedly interrupted baseline was accepted");
                }
                catch (InvalidOperationException)
                {
                    check(captures == 3 && barriers == 6 && commits == 0 && Directory.GetFiles(root, "*.png").Length == 0,
                        "Three interrupted baselines stop initialization and clean every uncommitted export");
                }
            }
            else
            {
                await RecordingBaselineInitialization.RunAsync("baseline", Barrier, Capture, ReadOne, Commit, _ => { }, CancellationToken.None);
                check(captures == 2 && barriers == 4 && commits == 1 && Directory.GetFiles(root, "*.png").Length == 0,
                    "Queued penBegin hidden until the after barrier forces a full baseline retry");
            }
        }
        await Scenario(everyAttempt: false);
        await Scenario(everyAttempt: true);
    }
}
