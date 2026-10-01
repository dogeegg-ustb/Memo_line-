using System.Text.Json;
using BehaviorRecognizer.Abstractions.Stroke;

namespace StrokeReplay;

internal static class StrokeJsonReader
{
    public static RecordingSession Read(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var header = root.GetProperty("header");
        if (header.GetProperty("version").GetUInt32() != StrokeFormat.Version)
            throw new InvalidDataException("不支持的笔迹 JSON 版本。");

        var session = new RecordingSession
        {
            FilePath = Path.GetFullPath(path),
            SessionId = header.GetProperty("sessionId").GetString() ?? "",
            Header = new StrokeSessionHeader
            {
                Version = StrokeFormat.Version,
                CreatedAtUnixMs = Time(header.GetProperty("createdAt")),
                PluginVersion = header.GetProperty("pluginVersion").GetString() ?? "",
                Device = new StrokeDeviceInfo
                {
                    Name = header.GetProperty("device").GetProperty("name").GetString() ?? "",
                    Id = header.GetProperty("device").GetProperty("id").GetString() ?? ""
                }
            }
        };

        foreach (var segmentJson in root.GetProperty("segments").EnumerateArray())
        {
            var segment = new StrokeSegment
            {
                SegmentId = segmentJson.GetProperty("segmentId").GetUInt64(),
                StartTimestampMs = Time(segmentJson.GetProperty("startTimestamp")),
                EndTimestampMs = Time(segmentJson.GetProperty("endTimestamp"))
            };
            foreach (var strokeJson in segmentJson.GetProperty("strokes").EnumerateArray())
            {
                var stroke = new Stroke
                {
                    StrokeId = strokeJson.GetProperty("strokeId").GetUInt64(),
                    StartTimestampMs = Time(strokeJson.GetProperty("startTimestamp")),
                    EndTimestampMs = Time(strokeJson.GetProperty("endTimestamp"))
                };
                foreach (var point in strokeJson.GetProperty("points").EnumerateArray())
                {
                    stroke.Points.Add(new SamplePoint
                    {
                        TimestampMs = point.GetProperty("timestamp").GetUInt64(),
                        DeltaTimeMs = point.GetProperty("deltaTime").GetUInt64(),
                        X = point.GetProperty("x").GetDouble(),
                        Y = point.GetProperty("y").GetDouble(),
                        Pressure = point.GetProperty("pressure").GetDouble(),
                        InContact = point.GetProperty("inContact").GetBoolean(),
                        Buttons = point.GetProperty("buttons").GetUInt32(),
                        TiltX = point.GetProperty("tiltX").GetDouble(),
                        TiltY = point.GetProperty("tiltY").GetDouble(),
                        SequenceId = point.GetProperty("sequenceId").GetUInt64()
                    });
                }
                segment.Strokes.Add(stroke);
                segment.PointCount += (ulong)stroke.Points.Count;
            }
            session.Segments.Add(segment);
        }
        return session;
    }

    private static ulong Time(JsonElement iso)
    {
        var value = DateTimeOffset.Parse(iso.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        return checked((ulong)value.ToUnixTimeMilliseconds());
    }
}
