using System.Text.Json;
using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Capture;
using BehaviorRecognizer.Config;
using BehaviorRecognizer.Storage.Memoline;
using Xunit;

namespace BehaviorRecognizer.Tests;

public class PenDownLocationTests
{
    private sealed class Probe : IInputTargetProbe
    {
        public CspWindowProbe.ScreenPoint Point = new() { X=200,Y=100 };
        public bool TryGetCspCursor(out CspWindowProbe.ScreenPoint point) { point=Point; return true; }
        public bool IsCspPoint(int x,int y) => x>=100 && x<600 && y>=0 && y<500;
        public bool IsCspForeground() => true;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PenRegionIsFrozenAtBeginForOtdAndWindowsPen(bool passive)
    {
        string directory=Path.Combine(Path.GetTempPath(),"pen-region-"+Guid.NewGuid().ToString("N"));
        try
        {
            string path;
            var map=PanelRegionMap.FromRegions(JsonSerializer.SerializeToElement(new Dictionary<string,int[]>
            { ["笔刷属性"]=[100,0,150,500],["画布视口"]=[250,0,350,500] }));
            await using (var writer=new MemolineWriter(directory,new {}))
            {
                path=writer.FilePath;
                var probe=new Probe();
                await using var capture=new UnifiedInputCapture(writer,targetProbe:probe,locatePenDown:(x,y)=>map.Classify(x,y));
                if (passive)
                {
                    capture.PostMouse(0x0201,200,100,0,passivePen:true);
                    capture.PostMouse(0x0200,300,100,0,passivePen:true);
                    capture.PostMouse(0x0202,300,100,0,passivePen:true);
                    capture.PostMouse(0x0201,300,100,0,passivePen:true);
                    capture.PostMouse(0x0202,300,100,0,passivePen:true);
                }
                else
                {
                    var normalizer=new InputEventNormalizer(PenProfileProvider.CreateHardcodedDefault());
                    ulong sequence=0;
                    void Report(float pressure)
                    {
                        foreach (var e in normalizer.Normalize(new RawInputReport
                        { DeviceId="test",Timestamp=DateTimeOffset.UtcNow,X=10000,Y=5000,Pressure=pressure,MaxPressure=1000 },"test",++sequence))
                            capture.OnPen(e);
                    }
                    Report(100);
                    probe.Point=new() { X=300,Y=100 };
                    Report(200); Report(0); Report(100); Report(0);
                }
            }
            var frames=MemolineReader.Read(path).Where(f=>f.GetProperty("kind").GetString() is "penBegin" or "penSample" or "penEnd").ToArray();
            Assert.Equal(new[] { "penBegin","penSample","penEnd","penBegin","penEnd" },frames.Select(f=>f.GetProperty("kind").GetString()));
            var locations=frames.Select(f=>PanelRegionMap.ReadLocation(f.GetProperty("data"))).ToArray();
            Assert.All(locations.Take(3),l=>Assert.Equal("brushProperties",l!.Region));
            Assert.All(locations.Take(3),l=>Assert.Equal(200,l!.X));
            Assert.All(locations.Skip(3),l=>Assert.Equal("canvasViewport",l!.Region));
            Assert.All(locations,l=>Assert.Equal("windowsCursor",l!.CoordinateSource));
            Assert.NotEqual(frames[0].GetProperty("operationId").GetUInt64(),frames[3].GetProperty("operationId").GetUInt64());
        }
        finally
        {
            string resolved=Path.GetFullPath(directory),temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if (resolved.StartsWith(temp,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("pen-region-",StringComparison.Ordinal) && Directory.Exists(resolved))
                Directory.Delete(resolved,recursive:true);
        }
    }
}
