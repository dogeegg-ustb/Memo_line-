using System.Text;
using System.Text.Json;
using BehaviorRecognizer.Storage.Memoline;

Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 1 && args[0] is "--help" or "-h")
{
    Usage();
    return 0;
}
if (args.Length is < 1 or > 2)
{
    Usage();
    return 2;
}

string input = Path.GetFullPath(args[0]);
string defaultInput = input.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ? input[..^5] : input;
string output = Path.GetFullPath(args.Length == 2 ? args[1] : Path.ChangeExtension(defaultInput, ".json"));
if (input.Equals(output, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("输入和输出路径不能相同。");
    return 2;
}
if (!File.Exists(input))
{
    Console.Error.WriteLine($"找不到文件: {input}");
    return 1;
}

string? outputDirectory = Path.GetDirectoryName(output);
if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);
string temporary = output + "." + Guid.NewGuid().ToString("N") + ".part";
try
{
    ulong appendId = 0, eventId = 0;
    long lastAppendTicks = -1, lastHardwareTicks = -1;
    int records = 0;
    JsonElement? footer = null;
    bool headerSeen = false;
    var timeline = new MemolineTimeline();
    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
    {
        json.WriteStartObject();
        foreach (var frame in MemolineReader.Read(input))
        {
            string kind = frame.GetProperty("kind").GetString() ?? throw new InvalidDataException("帧缺少 kind。");
            if (!headerSeen)
            {
                if (kind != "header") throw new InvalidDataException("首帧不是 header。");
                headerSeen = true;
                json.WritePropertyName("header");
                frame.WriteTo(json);
                json.WritePropertyName("events");
                json.WriteStartArray();
                continue;
            }
            if (kind == "footer")
            {
                footer = frame;
                continue;
            }
            if (footer is not null) throw new InvalidDataException("footer 之后出现事件帧。");
            ulong nextAppend = frame.GetProperty("appendId").GetUInt64();
            if (nextAppend != appendId + 1) throw new InvalidDataException("appendId 不连续。");
            appendId = nextAppend;
            long appendedTicks = frame.GetProperty("appendedTicks").GetInt64();
            if (appendedTicks < lastAppendTicks) throw new InvalidDataException("追加时间倒退。");
            lastAppendTicks = appendedTicks;
            if (frame.GetProperty("path").GetString() == "hardware")
            {
                ulong nextEvent = frame.GetProperty("eventId").GetUInt64();
                if (nextEvent != eventId + 1) throw new InvalidDataException("硬件 eventId 不连续。");
                eventId = nextEvent;
                long ticks = frame.GetProperty("ticks").GetInt64();
                if (ticks < lastHardwareTicks) throw new InvalidDataException("硬件事件时间倒退。");
                lastHardwareTicks = ticks;
            }
            frame.WriteTo(json);
            timeline.Add(frame);
            records++;
        }
        if (!headerSeen) throw new InvalidDataException("文件中没有完整 header 帧。");
        if (footer is not null &&
            (footer.Value.GetProperty("hardwareEvents").GetUInt64() != eventId ||
             footer.Value.GetProperty("appendedRecords").GetUInt64() != appendId))
            throw new InvalidDataException("footer 事件计数与内容不符。");
        json.WriteEndArray();
        timeline.Write(json);
        json.WriteBoolean("complete", footer is not null);
        json.WritePropertyName("footer");
        if (footer is null) json.WriteNullValue(); else footer.Value.WriteTo(json);
        json.WriteEndObject();
        json.Flush();
        stream.Flush(true);
    }
    File.Move(temporary, output, overwrite: true);
    Console.WriteLine($"已解析 {records} 条记录（硬件 {eventId} 条）：{output}");
    return 0;
}
catch (Exception ex)
{
    if (File.Exists(temporary)) File.Delete(temporary);
    Console.Error.WriteLine($"解析失败: {ex.Message}");
    return 1;
}

static void Usage()
{
    Console.WriteLine("Memoline 事件文件 JSON 解析器");
    Console.WriteLine("用法: MemolineToJson <input.memoline|input.memoline.part> [output.json]");
    Console.WriteLine("省略输出路径时，生成同名 .json；.part 可解析到最后一帧完整数据。");
}
