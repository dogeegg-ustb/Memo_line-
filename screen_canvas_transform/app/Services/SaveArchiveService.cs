using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Models;
using ScreenCanvasTransform.Ocr;

namespace ScreenCanvasTransform.Services;

public sealed class InitSuccessBundle
{
    public required PipelineResult Result { get; init; }
    public required string InitCaptureId { get; init; }
    /// <summary>Navigator panel screen rect at init (diagnostics / legacy; OCR layout comes from user box).</summary>
    public required IntRect NavigatorPanelScreenAtInit { get; init; }
}

public sealed class SaveArchiveService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _archivesDirectory;

    public SaveArchiveService(string? archivesDirectory = null)
    {
        _archivesDirectory = archivesDirectory
                             ?? Path.Combine(
                                 Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                 "ScreenCanvasTransform",
                                 "archives");
    }

    public string ArchivesDirectory => _archivesDirectory;

    public IEnumerable<SaveArchiveSummary> ListArchives()
    {
        Directory.CreateDirectory(_archivesDirectory);

        foreach (string path in Directory.EnumerateFiles(_archivesDirectory, "*.json"))
        {
            if (path.EndsWith(".tmp.json", StringComparison.OrdinalIgnoreCase))
                continue;

            SaveArchive? archive = TryReadFile(path, out string? readError);
            if (archive is null)
            {
                yield return new SaveArchiveSummary
                {
                    ArchiveId = Path.GetFileNameWithoutExtension(path),
                    DisplayName = Path.GetFileNameWithoutExtension(path),
                    IsValid = false,
                    ValidationError = readError ?? "无法读取存档"
                };
                continue;
            }

            string? validationError = ValidateArchive(archive);
            yield return new SaveArchiveSummary
            {
                ArchiveId = archive.ArchiveId,
                DisplayName = archive.DisplayName,
                CanvasPixelWidth = archive.CanvasPixelWidth,
                CanvasPixelHeight = archive.CanvasPixelHeight,
                CreatedAtUtc = archive.CreatedAtUtc,
                IsValid = validationError is null,
                ValidationError = validationError
            };
        }
    }

    public SaveArchiveOperationResult TryLoad(string archiveId)
    {
        if (string.IsNullOrWhiteSpace(archiveId))
            return Fail("缺少 ArchiveId");

        string path = GetArchivePath(archiveId);
        SaveArchive? archive = TryReadFile(path, out string? readError);
        if (archive is null)
            return Fail(readError ?? "存档不存在");

        string? validationError = ValidateArchive(archive);
        if (validationError is not null)
            return Fail(validationError);

        return new SaveArchiveOperationResult { Success = true, Archive = archive };
    }

    public SaveArchiveOperationResult TryCreateFromInitSuccess(
        InitSuccessBundle bundle,
        string? displayName = null)
    {
        var result = bundle.Result;
        var snapshot = result.Snapshot;

        if (snapshot.Status != Interop.NativeSct.StatusOk && snapshot.FailureStatus != 117)
            return Fail("初始化未成功，禁止落盘");

        if (result.Stage != State.TransformStage.TrackingStable)
            return Fail("初始化未完成 TrackingStable，禁止落盘");

        if (result.Background is null)
            return Fail("缺少 WorkspaceBackgroundModel");

        if (result.OcrLayoutUsed is not OcrLayoutScreen ocrLayout)
            return Fail("缺少用户框选并验证通过的 OcrLayout，禁止落盘");

        string archiveId = Guid.NewGuid().ToString("N");
        var archive = new SaveArchive
        {
            ArchiveId = archiveId,
            SchemaVersion = SaveArchiveConstants.SchemaVersion,
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? $"存档 {DateTime.Now:yyyy-MM-dd HH:mm}"
                : displayName.Trim(),
            CreatedAtUtc = DateTime.UtcNow,
            CoordinateConventionVersion = snapshot.CoordinateConventionVersion > 0
                ? snapshot.CoordinateConventionVersion
                : SaveArchiveConstants.CoordinateConventionVersion,
            CanvasPixelWidth = snapshot.CanvasPixelWidth,
            CanvasPixelHeight = snapshot.CanvasPixelHeight,
            SystemWorkspaceRoiScreen = ScreenPhysicalRectDto.FromIntRect(result.WorkspaceRoiScreen),
            Background = result.Background,
            SystemNavigatorThumbnailRoiScreen =
                ScreenPhysicalRectDto.FromIntRect(result.NavigatorThumbnailRoiScreen),
            OcrLayout = new OcrLayoutDto
            {
                ScaleSlotScreen = ScreenPhysicalRectDto.FromIntRect(ocrLayout.ScaleSlotScreen),
                RotationSlotScreen = ScreenPhysicalRectDto.FromIntRect(ocrLayout.RotationSlotScreen)
            },
            Provenance = new SaveArchiveProvenance
            {
                InitCaptureId = bundle.InitCaptureId,
                InitGeneration = snapshot.Generation,
                InitSourceRevision = snapshot.SourceRevision,
                InitCompletedAtUtc = DateTime.UtcNow
            }
        };

        string? validationError = ValidateArchive(archive);
        if (validationError is not null)
            return Fail(validationError);

        if (!TryAtomicWrite(archive, out string writeError))
            return Fail(writeError);

        return new SaveArchiveOperationResult { Success = true, Archive = archive };
    }

    public bool TryDelete(string archiveId)
    {
        if (string.IsNullOrWhiteSpace(archiveId))
            return false;

        string path = GetArchivePath(archiveId);
        if (!File.Exists(path))
            return false;

        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public SaveArchiveOperationResult TryUpdateLastSuccessfulRecompute(SaveArchive archive, string captureId)
    {
        var updated = new SaveArchive
        {
            ArchiveId = archive.ArchiveId,
            SchemaVersion = archive.SchemaVersion,
            DisplayName = archive.DisplayName,
            CreatedAtUtc = archive.CreatedAtUtc,
            CoordinateConventionVersion = archive.CoordinateConventionVersion,
            CanvasPixelWidth = archive.CanvasPixelWidth,
            CanvasPixelHeight = archive.CanvasPixelHeight,
            SystemWorkspaceRoiScreen = archive.SystemWorkspaceRoiScreen,
            Background = archive.Background,
            SystemNavigatorThumbnailRoiScreen = archive.SystemNavigatorThumbnailRoiScreen,
            OcrLayout = archive.OcrLayout,
            Provenance = archive.Provenance,
            LastSuccessfulRecomputeAtUtc = DateTime.UtcNow,
            LastSuccessfulCaptureId = captureId,
            Notes = archive.Notes
        };

        return TryAtomicWrite(updated, out string writeError)
            ? new SaveArchiveOperationResult { Success = true, Archive = updated }
            : Fail(writeError);
    }

    internal static string? ValidateArchive(SaveArchive archive)
    {
        if (archive.SchemaVersion != SaveArchiveConstants.SchemaVersion)
            return $"不支持的 SchemaVersion={archive.SchemaVersion}";

        if (archive.CanvasPixelWidth <= 0 || archive.CanvasPixelHeight <= 0)
            return "画布像素尺寸无效";

        if (string.IsNullOrWhiteSpace(archive.ArchiveId))
            return "缺少 ArchiveId";

        string? rectError = ValidateScreenRect(archive.SystemWorkspaceRoiScreen, "SystemWorkspaceRoiScreen");
        if (rectError is not null)
            return rectError;

        rectError = ValidateScreenRect(archive.SystemNavigatorThumbnailRoiScreen, "SystemNavigatorThumbnailRoiScreen");
        if (rectError is not null)
            return rectError;

        rectError = ValidateScreenRect(archive.OcrLayout?.ScaleSlotScreen, "OcrLayout.ScaleSlotScreen", minSizePx: 8);
        if (rectError is not null)
            return rectError;

        rectError = ValidateScreenRect(archive.OcrLayout?.RotationSlotScreen, "OcrLayout.RotationSlotScreen", minSizePx: 8);
        if (rectError is not null)
            return rectError;

        var bg = archive.Background;
        if (bg is null)
            return "缺少 WorkspaceBackgroundModel";

        if (!float.IsFinite(bg.CenterLabL) || !float.IsFinite(bg.CenterLabA) || !float.IsFinite(bg.CenterLabB)
            || !float.IsFinite(bg.StrongDeltaE) || !float.IsFinite(bg.WeakDeltaE) || !float.IsFinite(bg.Confidence))
            return "WorkspaceBackgroundModel 字段无效";

        if (archive.Provenance is null || string.IsNullOrWhiteSpace(archive.Provenance.InitCaptureId))
            return "缺少 Provenance";

        return null;
    }

    private static string? ValidateScreenRect(ScreenPhysicalRectDto? dto, string name, int minSizePx = CaptureSession.MinRoiSizePx)
    {
        if (dto is null)
            return $"缺少 {name}";

        if (!string.Equals(dto.Space, "ScreenPhysicalPx", StringComparison.Ordinal))
            return $"{name} 坐标空间无效";

        var rect = dto.ToIntRect();
        if (rect.IsEmpty)
            return $"{name} 为空";

        if (rect.Width < minSizePx || rect.Height < minSizePx)
            return $"{name} 过小";

        return null;
    }

    private string GetArchivePath(string archiveId)
        => Path.Combine(_archivesDirectory, $"{archiveId}.json");

    private SaveArchive? TryReadFile(string path, out string? error)
    {
        error = null;
        try
        {
            string json = File.ReadAllText(path);
            var archive = JsonSerializer.Deserialize<SaveArchive>(json, JsonOptions);
            if (archive is null)
            {
                error = "反序列化失败";
                return null;
            }

            return archive;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    private bool TryAtomicWrite(SaveArchive archive, out string error)
    {
        error = string.Empty;
        Directory.CreateDirectory(_archivesDirectory);

        string finalPath = GetArchivePath(archive.ArchiveId);
        string tempPath = finalPath + ".tmp";

        try
        {
            string json = JsonSerializer.Serialize(archive, JsonOptions);
            File.WriteAllText(tempPath, json);
            using (var stream = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                stream.Flush(flushToDisk: true);

            SaveArchive? roundTrip = TryReadFile(tempPath, out string? readError);
            if (roundTrip is null)
            {
                error = readError ?? "写入后校验失败";
                TryDeleteTemp(tempPath);
                return false;
            }

            if (ValidateArchive(roundTrip) is not null)
            {
                error = "写入后 schema 校验失败";
                TryDeleteTemp(tempPath);
                return false;
            }

            if (File.Exists(finalPath))
                File.Delete(finalPath);

            File.Move(tempPath, finalPath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            TryDeleteTemp(tempPath);
            return false;
        }
    }

    private static void TryDeleteTemp(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch
        {
            // ignore
        }
    }

    private static SaveArchiveOperationResult Fail(string error)
        => new() { Success = false, Error = error };
}
