using BehaviorRecognizer.Abstractions.Input;
using BehaviorRecognizer.Abstractions.Stroke;
using BehaviorRecognizer.Recording;
using BehaviorRecognizer.Storage;
using BehaviorRecognizer.Storage.Strokebin;
using Xunit;

namespace BehaviorRecognizer.Tests;

/// <summary>STRO / .strokebin ??????????</summary>
public class StrokeStorageTests
{
    [Fact]
    public async Task Write_Strokebin_RoundTrip_And_Export()
    {
        // ???????
        var dir = Path.Combine(Path.GetTempPath(), "br-stroke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var recorder = new PenInputRecorder(dir);
            recorder.StartSession("test-device", "dev-1");

            var sessionId = recorder.CurrentSessionId;
            var now = DateTimeOffset.UtcNow;

            // ?? + ?????
            await recorder.OnEventAsync(new InputEvent
            {
                Type = InputEventType.PenDown,
                Timestamp = now,
                SessionId = sessionId,
                DeviceId = "dev-1",
                Sequence = 1,
                Position = new System.Numerics.Vector2(10.5f, 20.25f),
                Pressure = 0.4f,
                ContactState = ContactState.Contact,
                PenButtons = [true, false],
                Tilt = new System.Numerics.Vector2(1.5f, -2.5f),
            });

            // ??????
            await recorder.OnEventAsync(new InputEvent
            {
                Type = InputEventType.PenMove,
                Timestamp = now.AddMilliseconds(8),
                SessionId = sessionId,
                DeviceId = "dev-1",
                Sequence = 2,
                Position = new System.Numerics.Vector2(11f, 21f),
                Pressure = 0.5f,
                ContactState = ContactState.Contact,
            });

            // ????????
            await recorder.OnEventAsync(new InputEvent
            {
                Type = InputEventType.PenHover,
                Timestamp = now.AddMilliseconds(16),
                SessionId = sessionId,
                DeviceId = "dev-1",
                Sequence = 3,
                Position = new System.Numerics.Vector2(99f, 99f),
                Pressure = 0f,
                ContactState = ContactState.Hover,
            });

            // ??
            await recorder.OnEventAsync(new InputEvent
            {
                Type = InputEventType.PenUp,
                Timestamp = now.AddMilliseconds(20),
                SessionId = sessionId,
                DeviceId = "dev-1",
                Sequence = 4,
                Pressure = 0f,
                ContactState = ContactState.Hover,
            });

            // ?????????500ms?
            await Task.Delay(700);
            recorder.StopSession();

            var filePath = recorder.CurrentFilePath;
            Assert.True(File.Exists(filePath));
            Assert.False(File.Exists(filePath + ".part"));
            Assert.True(StrokeBinaryReader.HasCompletedSessionEnd(filePath));

            var session = StrokeBinaryReader.Read(filePath);
            Assert.Equal(StrokeFormat.Version, session.Header.Version);
            Assert.Equal(0, session.Header.Encoding);
            Assert.StartsWith("session-", session.SessionId);
            Assert.NotEmpty(session.Segments);
            Assert.Equal(FlushReason.PenUpTimeout, session.Segments[0].Reason);
            Assert.Single(session.Segments[0].Strokes);
            Assert.Equal(2, session.Segments[0].Strokes[0].Points.Count);
            Assert.Equal(10.5, session.Segments[0].Strokes[0].Points[0].X, 3);
            Assert.Equal(1u, session.Segments[0].Strokes[0].Points[0].Buttons);
            Assert.Equal(0uL, session.Segments[0].Strokes[0].Points[0].DeltaTimeMs);
            Assert.Equal(8uL, session.Segments[0].Strokes[0].Points[1].DeltaTimeMs);

            var jsonPath = Path.Combine(dir, "out.json");
            await new JsonEventExporter().ExportJsonAsync(filePath, jsonPath);
            var text = await File.ReadAllTextAsync(jsonPath);
            Assert.Contains("\"version\": 1", text);
            Assert.Contains("PenUpTimeout", text);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task DeltaTime_Resets_Per_Stroke_And_Deduplicates_Identical_Payload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "br-stroke-dedup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var recorder = new PenInputRecorder(dir);
            recorder.StartSession("test-device", "dev-1");
            var sessionId = recorder.CurrentSessionId;
            var now = DateTimeOffset.UtcNow;

            // stroke1: down + move + duplicate move (should drop) + up
            await recorder.OnEventAsync(new InputEvent
            {
                Type = InputEventType.PenDown,
                Timestamp = now,
                SessionId = sessionId,
                DeviceId = "dev-1",
                Sequence = 1,
                Position = new System.Numerics.Vector2(1f, 2f),
                Pressure = 0.3f,
                ContactState = ContactState.Contact,
            });
            await recorder.OnEventAsync(new InputEvent
            {
                Type = InputEventType.PenMove,
                Timestamp = now.AddMilliseconds(5),
                SessionId = sessionId,
                DeviceId = "dev-1",
                Sequence = 2,
                Position = new System.Numerics.Vector2(3f, 4f),
                Pressure = 0.4f,
                ContactState = ContactState.Contact,
            });
            // identical payload, different timestamp/sequence — must be ignored
            await recorder.OnEventAsync(new InputEvent
            {
                Type = InputEventType.PenMove,
                Timestamp = now.AddMilliseconds(6),
                SessionId = sessionId,
                DeviceId = "dev-1",
                Sequence = 3,
                Position = new System.Numerics.Vector2(3f, 4f),
                Pressure = 0.4f,
                ContactState = ContactState.Contact,
            });
            await recorder.OnEventAsync(new InputEvent
            {
                Type = InputEventType.PenUp,
                Timestamp = now.AddMilliseconds(10),
                SessionId = sessionId,
                DeviceId = "dev-1",
                Sequence = 4,
                Pressure = 0f,
                ContactState = ContactState.Hover,
            });

            // stroke2 after air gap: first delta must still be 0
            await recorder.OnEventAsync(new InputEvent
            {
                Type = InputEventType.PenDown,
                Timestamp = now.AddMilliseconds(300),
                SessionId = sessionId,
                DeviceId = "dev-1",
                Sequence = 5,
                Position = new System.Numerics.Vector2(10f, 20f),
                Pressure = 0.5f,
                ContactState = ContactState.Contact,
            });
            await recorder.OnEventAsync(new InputEvent
            {
                Type = InputEventType.PenMove,
                Timestamp = now.AddMilliseconds(312),
                SessionId = sessionId,
                DeviceId = "dev-1",
                Sequence = 6,
                Position = new System.Numerics.Vector2(11f, 21f),
                Pressure = 0.6f,
                ContactState = ContactState.Contact,
            });
            await recorder.OnEventAsync(new InputEvent
            {
                Type = InputEventType.PenUp,
                Timestamp = now.AddMilliseconds(320),
                SessionId = sessionId,
                DeviceId = "dev-1",
                Sequence = 7,
                Pressure = 0f,
                ContactState = ContactState.Hover,
            });

            await Task.Delay(700);
            recorder.StopSession();

            var session = StrokeBinaryReader.Read(recorder.CurrentFilePath);
            Assert.Equal(2, session.Segments[0].Strokes.Count);

            var stroke1 = session.Segments[0].Strokes[0];
            Assert.Equal(2, stroke1.Points.Count);
            Assert.Equal(0uL, stroke1.Points[0].DeltaTimeMs);
            Assert.Equal(5uL, stroke1.Points[1].DeltaTimeMs);

            var stroke2 = session.Segments[0].Strokes[1];
            Assert.Equal(2, stroke2.Points.Count);
            Assert.Equal(0uL, stroke2.Points[0].DeltaTimeMs);
            Assert.Equal(12uL, stroke2.Points[1].DeltaTimeMs);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Part_Files_Are_Not_Renamed_By_Recovery()
    {
        var dir = Path.Combine(Path.GetTempPath(), "br-part-" + Guid.NewGuid().ToString("N"));
        var strokeDir = Path.Combine(dir, "stroke");
        Directory.CreateDirectory(strokeDir);
        var part = Path.Combine(strokeDir, "20260101_000000.strokebin.part");
        File.WriteAllBytes(part, "STRO"u8.ToArray());

        try
        {
            var count = await new RecoveryReader().RecoverPartFilesAsync(strokeDir);
            Assert.Equal(1, count);
            Assert.True(File.Exists(part));
            Assert.False(File.Exists(Path.Combine(strokeDir, "20260101_000000.strokebin")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
