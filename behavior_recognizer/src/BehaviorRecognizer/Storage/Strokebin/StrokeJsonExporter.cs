using System.Text;
using System.Text.Json;
using BehaviorRecognizer.Abstractions.Stroke;

namespace BehaviorRecognizer.Storage.Strokebin;

/// <summary>�?RecordingSession 导出�?JSON（与 OtdStrokePlugin 导出结构对齐）�?/summary>
public static class StrokeJsonExporter
{
    /// <summary>导出会话�?JSON 文本�?/summary>
    public static string ExportSession(RecordingSession session)
    {
        var model = new // 匿名导出模型
        {
            header = new
            {
                version = session.Header.Version,
                createdAt = StrokePathUtil.ToIso8601(session.Header.CreatedAtUnixMs),
                pluginVersion = session.Header.PluginVersion,
                sessionId = session.SessionId,
                filePath = session.FilePath,
                device = new
                {
                    name = session.Header.Device.Name,
                    id = session.Header.Device.Id,
                },
            },
            segments = session.Segments.Select(seg => new
            {
                segmentId = seg.SegmentId,
                reason = StrokeEnumNames.ToName(seg.Reason),
                startTimestamp = StrokePathUtil.ToIso8601(seg.StartTimestampMs),
                endTimestamp = StrokePathUtil.ToIso8601(seg.EndTimestampMs),
                pointCount = seg.PointCount,
                writeStatus = StrokeEnumNames.ToName(seg.WriteStatus),
                strokes = seg.Strokes.Select(st => new
                {
                    strokeId = st.StrokeId,
                    startTimestamp = StrokePathUtil.ToIso8601(st.StartTimestampMs),
                    endTimestamp = StrokePathUtil.ToIso8601(st.EndTimestampMs),
                    points = st.Points.Select(pt => new
                    {
                        timestamp = pt.TimestampMs,
                        timestampIso = StrokePathUtil.ToIso8601(pt.TimestampMs),
                        deltaTime = pt.DeltaTimeMs,
                        x = pt.X,
                        y = pt.Y,
                        pressure = pt.Pressure,
                        inContact = pt.InContact,
                        buttons = pt.Buttons,
                        tiltX = pt.TiltX,
                        tiltY = pt.TiltY,
                        sequenceId = pt.SequenceId,
                    }).ToArray(),
                }).ToArray(),
            }).ToArray(),
        };

        return JsonSerializer.Serialize(model, new JsonSerializerOptions
        {
            WriteIndented = true, // 可读缩进
        });
    }

    /// <summary>�?.strokebin 读入并写到输出路径�?/summary>
    public static async Task ExportFileAsync(string strokeBinPath, string outputPath, CancellationToken cancellationToken = default)
    {
        var session = StrokeBinaryReader.Read(strokeBinPath); // 读取二进�?
        var json = ExportSession(session); // �?JSON
        await File.WriteAllTextAsync(outputPath, json, new UTF8Encoding(false), cancellationToken); // 写出
    }
}
