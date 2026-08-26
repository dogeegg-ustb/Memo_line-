using System.Text.Json.Serialization;

namespace ScreenCanvasTransform.Models;

public static class SaveArchiveConstants
{
    public const int SchemaVersion = 1;
    public const int CoordinateConventionVersion = 1;
}

public sealed class ScreenPhysicalRectDto
{
    public string Space { get; init; } = "ScreenPhysicalPx";
    public int Left { get; init; }
    public int Top { get; init; }
    public int Right { get; init; }
    public int Bottom { get; init; }

    public IntRect ToIntRect() => new(Left, Top, Right, Bottom);

    public static ScreenPhysicalRectDto FromIntRect(IntRect rect) => new()
    {
        Space = "ScreenPhysicalPx",
        Left = rect.Left,
        Top = rect.Top,
        Right = rect.Right,
        Bottom = rect.Bottom
    };
}

public sealed class OcrLayoutDto
{
    public ScreenPhysicalRectDto ScaleSlotScreen { get; init; } = null!;
    public ScreenPhysicalRectDto RotationSlotScreen { get; init; } = null!;
}

public readonly record struct OcrLayoutScreen(IntRect ScaleSlotScreen, IntRect RotationSlotScreen)
{
    public static OcrLayoutScreen FromDto(OcrLayoutDto dto)
        => new(dto.ScaleSlotScreen.ToIntRect(), dto.RotationSlotScreen.ToIntRect());
}

public sealed class SaveArchiveProvenance
{
    public string InitCaptureId { get; init; } = "";
    public ulong InitGeneration { get; init; }
    public string InitSourceRevision { get; init; } = "";
    public DateTime InitCompletedAtUtc { get; init; }
}

public sealed class SaveArchive
{
    public string ArchiveId { get; init; } = "";
    public int SchemaVersion { get; init; } = SaveArchiveConstants.SchemaVersion;
    public string DisplayName { get; init; } = "";
    public DateTime CreatedAtUtc { get; init; }
    public int CoordinateConventionVersion { get; init; } = SaveArchiveConstants.CoordinateConventionVersion;

    public int CanvasPixelWidth { get; init; }
    public int CanvasPixelHeight { get; init; }

    public ScreenPhysicalRectDto SystemWorkspaceRoiScreen { get; init; } = null!;

    [JsonPropertyName("workspaceBackgroundModel")]
    public WorkspaceBackgroundModel Background { get; init; } = null!;
    public ScreenPhysicalRectDto SystemNavigatorThumbnailRoiScreen { get; init; } = null!;
    public OcrLayoutDto OcrLayout { get; init; } = null!;
    public SaveArchiveProvenance Provenance { get; init; } = null!;

    public DateTime? LastSuccessfulRecomputeAtUtc { get; init; }
    public string? LastSuccessfulCaptureId { get; init; }
    public string? Notes { get; init; }
}

public sealed class SaveArchiveSummary
{
    public string ArchiveId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public int CanvasPixelWidth { get; init; }
    public int CanvasPixelHeight { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public bool IsValid { get; init; }
    public string? ValidationError { get; init; }
}

public sealed class SaveArchiveOperationResult
{
    public bool Success { get; init; }
    public SaveArchive? Archive { get; init; }
    public string Error { get; init; } = "";
}
