using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Diagnostics;
using ScreenCanvasTransform.Models;
using ScreenCanvasTransform.Services;
using ScreenCanvasTransform.State;
using ScreenCanvasTransform.Ui;

namespace ScreenCanvasTransform;

public partial class MainWindow : Window
{
    private readonly TransformPipelineService _pipeline = new();
    private readonly SaveArchiveService _archiveService = new();
    // Distinct border colors: Workspace=green, Navigator=cyan, Thumbnail=magenta.
    private readonly RoiBorderOverlayWindow _workspaceBorder = RoiBorderOverlayWindow.CreateWorkspace();
    private readonly RoiBorderOverlayWindow _navigatorBorder = RoiBorderOverlayWindow.CreateNavigator();
    private readonly RoiBorderOverlayWindow _thumbnailBorder = RoiBorderOverlayWindow.CreateNavigatorThumbnail();
    private readonly MarkerOverlayWindow _markerOverlay = new();
    private CaptureSession? _activeSession;
    private PipelineResult? _lastResult;
    private SaveArchive? _activeArchive;
    private bool _flowRunning;
    private bool _recomputePending;

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            _markerOverlay.Dispose();
            _workspaceBorder.Dispose();
            _navigatorBorder.Dispose();
            _thumbnailBorder.Dispose();
            _activeSession?.Dispose();
        };
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        SetStage(TransformStage.SelectingSaveArchive);
        RefreshArchiveList();
    }

    private void RefreshArchiveList()
    {
        var items = _archiveService.ListArchives()
            .OrderByDescending(a => a.CreatedAtUtc)
            .Select(ArchiveListItem.FromSummary)
            .ToList();

        ArchiveListView.ItemsSource = items;
        UpdateArchiveButtons();
    }

    private void ArchiveListView_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateArchiveButtons();

    private void UpdateArchiveButtons()
    {
        if (ArchiveListView.SelectedItem is ArchiveListItem item && item.IsValid)
        {
            LoadArchiveButton.IsEnabled = !_flowRunning;
            DeleteArchiveButton.IsEnabled = !_flowRunning;
        }
        else
        {
            LoadArchiveButton.IsEnabled = false;
            DeleteArchiveButton.IsEnabled = ArchiveListView.SelectedItem is ArchiveListItem;
        }
    }

    private async void LoadArchiveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ArchiveListView.SelectedItem is not ArchiveListItem item || !item.IsValid)
            return;

        SetStage(TransformStage.LoadingSaveArchive);
        var load = _archiveService.TryLoad(item.ArchiveId);
        if (!load.Success || load.Archive is null)
        {
            SetStatus($"无法加载存档：{load.Error}");
            RefreshArchiveList();
            return;
        }

        await RunArchiveRecomputeAsync(load.Archive);
    }

    private async void StartButton_OnClick(object sender, RoutedEventArgs e)
        => await RunInitializationFlowAsync();

    private void DeleteArchiveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (ArchiveListView.SelectedItem is not ArchiveListItem item)
            return;

        if (_archiveService.TryDelete(item.ArchiveId))
        {
            if (string.Equals(_activeArchive?.ArchiveId, item.ArchiveId, StringComparison.Ordinal))
            {
                _activeArchive = null;
                _lastResult = null;
                RecomputeButton.IsEnabled = false;
            }

            SetStatus($"已删除存档：{item.DisplayName}");
            RefreshArchiveList();
        }
        else
        {
            SetStatus("删除存档失败。");
        }
    }

    private void HideOverlayButton_OnClick(object sender, RoutedEventArgs e)
    {
        HideAllOverlays();
        SetStatus("已隐藏覆盖层。");
    }

    private void HideAllOverlays()
    {
        _markerOverlay.Hide();
        _workspaceBorder.Hide();
        _navigatorBorder.Hide();
        _thumbnailBorder.Hide();
    }

    private async Task RunInitializationFlowAsync()
    {
        if (_flowRunning)
            return;

        _flowRunning = true;
        StartButton.IsEnabled = false;
        LoadArchiveButton.IsEnabled = false;
        DeleteArchiveButton.IsEnabled = false;

        try
        {
            var sizeDialog = new CanvasSizeInputDialog();
            bool? sizeOk = sizeDialog.ShowDialog();
            if (sizeOk != true)
            {
                SetStatus("已取消：未输入有效画布像素尺寸，不会截图或进入 ROI。");
                return;
            }

            _pipeline.BeginNewInitializationGeneration(
                sizeDialog.CanvasPixelWidth,
                sizeDialog.CanvasPixelHeight);

            HideAllOverlays();
            SetStatus("正在隐藏窗口并冻结桌面截图…");
            Hide();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(120);

            _activeSession?.Dispose();
            _activeSession = null;

            var session = CaptureSession.CreateFromVirtualScreen();
            _activeSession = session;
            SetStage(TransformStage.CaptureFrozen);
            LiveDebugLog.Write(
                $"[Capture] id={session.CaptureId} frame={session.FrozenCapture.Width}x{session.FrozenCapture.Height} " +
                $"origin=({session.OriginX},{session.OriginY})");

            SetStage(TransformStage.SelectingWorkspaceRoi);
            var wsRoi = new RoiSelectWindow(
                session,
                RoiKind.WorkspaceUser,
                "拖拽框选工作区粗略范围 · Enter 确认 · Esc 取消");
            bool? wsOk = wsRoi.ShowDialog();
            if (wsOk != true || session.WorkspaceUserRoiCapturePx is null)
            {
                Show();
                Activate();
                SetStatus("已取消工作区框选。");
                return;
            }

            SetStage(TransformStage.DetectingWorkspace);
            SetStatus($"CaptureId={session.CaptureId}。正在纠正工作区…");
            var workspace = await Task.Run(() => _pipeline.DetectWorkspace(session)).ConfigureAwait(true);

            Show();
            Activate();

            if (!workspace.Success || workspace.Background is null)
            {
                HideAllOverlays();
                SetStage(TransformStage.DetectingWorkspace);
                SetStatus(
                    $"工作区标记失败，初始化结束。{workspace.StatusName} — {workspace.Message} " +
                    $"(CaptureId={session.CaptureId})");
                return;
            }

            _workspaceBorder.TryShowIfCaptureMatches(
                workspace.RectScreenPhysicalPx,
                session.CaptureId,
                workspace.SourceCaptureId);

            Hide();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(60);

            SetStage(TransformStage.SelectingNavigatorRoi);
            var navRoi = new RoiSelectWindow(
                session,
                RoiKind.Navigator,
                "拖拽框选完整导航器面板（直接采用，不做外边界纠正）· Enter 确认 · Esc 取消");
            bool? navOk = navRoi.ShowDialog();

            Show();
            Activate();

            if (navOk != true || session.NavigatorRoiCapturePx is null)
            {
                SetStatus("已取消导航器框选。工作区绿框仍保留。");
                return;
            }

            var navigatorScreen = session.CaptureToScreen(session.NavigatorRoiCapturePx.Value);
            _navigatorBorder.Show(navigatorScreen, session.CaptureId);

            SetStage(TransformStage.DetectingNavigatorThumbnailCII);
            SetStatus($"CaptureId={session.CaptureId}。正在 C-II 生成导航器缩略图…");
            var thumbnail = await Task.Run(() => _pipeline.DetectNavigatorThumbnail(session, workspace))
                .ConfigureAwait(true);
            if (!thumbnail.Success)
            {
                _thumbnailBorder.Hide();
                SetStatus(
                    $"缩略图标记失败。{thumbnail.StatusName} — {thumbnail.Message} " +
                    $"(CaptureId={session.CaptureId})");
                return;
            }

            _thumbnailBorder.TryShowIfCaptureMatches(
                thumbnail.RectScreenPhysicalPx,
                session.CaptureId,
                thumbnail.SourceCaptureId);
            SetStatus("缩略图已标记（品红）。继续观测 / OCR / 求解…");

            var progress = new Progress<TransformStage>(SetStage);
            var result = await Task.Run(
                    async () => await _pipeline.ContinueAfterThumbnailAsync(
                            session, workspace, thumbnail, progress)
                        .ConfigureAwait(false))
                .ConfigureAwait(true);

            ApplyResultOverlays(session, result);

            SetStage(TransformStage.TrackingStable);

            SetStage(TransformStage.PersistingSaveArchive);
            var persist = _archiveService.TryCreateFromInitSuccess(new InitSuccessBundle
            {
                Result = result,
                InitCaptureId = session.CaptureId,
                NavigatorPanelScreenAtInit = result.NavigatorRoiScreen
            });

            _lastResult = result;
            _activeArchive = persist.Archive;
            RecomputeButton.IsEnabled = true;
            RefreshArchiveList();

            string path = result.Snapshot.UsedDirectWorkspacePath ? "直接工作区路径" : "导航器路径";
            string off = result.Snapshot.Marker.Offscreen != 0 ? "（MarkerOffscreen）" : "";
            string persistNote = persist.Success
                ? $" 已写入存档「{persist.Archive!.DisplayName}」。"
                : $" 初始化几何成功但存档写入失败：{persist.Error}";

            SetStatus(
                $"已发布 gen={result.Snapshot.Generation}，{path}，" +
                $"scale={result.Snapshot.Numbers.ScalePercent:F1}% rel={result.Snapshot.RelativeScale:F3}，" +
                $"rot={result.Snapshot.RotationDegrees:F1}°，conf={result.Snapshot.Confidence:F2}。" +
                persistNote +
                (result.Snapshot.Marker.Offscreen != 0 ? $" 橙色 L 已显示{off}。" : " 标记已显示。"));
            SetStage(TransformStage.TrackingStable);
        }
        catch (PipelineFailureException ex)
        {
            _markerOverlay.Hide();
            _thumbnailBorder.Hide();
            if (!IsVisible)
            {
                Show();
                Activate();
            }
            SetStage(ex.Stage);
            SetStatus(
                $"失败 stage={ex.Stage} status={ex.Status}：{ex.Message} " +
                $"(CaptureId={ex.CaptureId}, gen={ex.Generation})" +
                (ex.Stage == TransformStage.ReadingNavigatorNumbers
                    ? $"  OCR调试: %TEMP%\\sct_ocr_debug\\{ex.CaptureId}"
                    : ""));
        }
        catch (Exception ex)
        {
            HideAllOverlays();
            if (!IsVisible)
            {
                Show();
                Activate();
            }
            SetStatus($"发生错误：{ex.Message}");
        }
        finally
        {
            if (!IsVisible)
            {
                Show();
                Activate();
            }
            StartButton.IsEnabled = true;
            _flowRunning = false;
            UpdateArchiveButtons();
        }
    }

    private async Task RunArchiveRecomputeAsync(SaveArchive archive)
    {
        if (_flowRunning)
            return;

        _flowRunning = true;
        _recomputePending = true;
        LoadArchiveButton.IsEnabled = false;
        StartButton.IsEnabled = false;
        DeleteArchiveButton.IsEnabled = false;
        RecomputeButton.IsEnabled = false;

        try
        {
            HideAllOverlays();
            SetStage(TransformStage.ArchiveRecomputeRequested);
            Hide();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(120);

            _activeSession?.Dispose();
            var session = CaptureSession.CreateFromVirtualScreen();
            _activeSession = session;
            SetStage(TransformStage.CaptureFrozen);

            var progress = new Progress<TransformStage>(SetStage);
            var result = await Task.Run(
                    async () => await _pipeline.RecomputeFromArchiveAsync(session, archive, progress)
                        .ConfigureAwait(false))
                .ConfigureAwait(true);

            Show();
            Activate();

            ApplyResultOverlays(session, result);

            _lastResult = result;
            _activeArchive = archive;
            _archiveService.TryUpdateLastSuccessfulRecompute(archive, session.CaptureId);

            SetStatus(
                $"从存档「{archive.DisplayName}」重算完成 gen={result.Snapshot.Generation} " +
                $"recompute={result.Snapshot.RecomputeGeneration}，" +
                $"rot_geo={result.Snapshot.RotationDegreesGeometry:F1}°，" +
                $"scale={result.Snapshot.ScalePercentOcrOrInjected:F1}%。");
            SetStage(TransformStage.TrackingStable);
        }
        catch (PipelineFailureException ex)
        {
            _markerOverlay.Hide();
            if (!IsVisible)
            {
                Show();
                Activate();
            }
            SetStage(ex.Stage);
            SetStatus(
                $"从存档重算失败 stage={ex.Stage} status={ex.Status}：{ex.Message}。" +
                " 请检查 CSP 窗口位置、缩放或导航器布局是否相对存档发生变化。");
        }
        finally
        {
            _recomputePending = false;
            _flowRunning = false;
            RecomputeButton.IsEnabled = _lastResult is not null;
            StartButton.IsEnabled = true;
            UpdateArchiveButtons();
            if (!IsVisible)
            {
                Show();
                Activate();
            }
        }
    }

    private async void RecomputeButton_OnClick(object sender, RoutedEventArgs e)
        => await RunRecomputeAsync();

    private async Task RunRecomputeAsync()
    {
        if (_lastResult is null || _flowRunning)
            return;

        if (_recomputePending)
            return;

        _recomputePending = true;
        _flowRunning = true;
        RecomputeButton.IsEnabled = false;

        try
        {
            HideAllOverlays();
            SetStage(TransformStage.RecomputeRequested);
            Hide();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(120);

            _activeSession?.Dispose();
            var session = CaptureSession.CreateFromVirtualScreen();
            _activeSession = session;
            SetStage(TransformStage.CaptureFrozen);

            var progress = new Progress<TransformStage>(SetStage);
            var result = await Task.Run(
                    async () => await _pipeline.RecomputeAsync(session, _lastResult, progress)
                        .ConfigureAwait(false))
                .ConfigureAwait(true);

            Show();
            Activate();

            ApplyResultOverlays(session, result);

            _lastResult = result;
            if (_activeArchive is not null)
                _archiveService.TryUpdateLastSuccessfulRecompute(_activeArchive, session.CaptureId);

            SetStatus(
                $"重算完成 gen={result.Snapshot.Generation} recompute={result.Snapshot.RecomputeGeneration}，" +
                $"rot_geo={result.Snapshot.RotationDegreesGeometry:F1}°，" +
                $"scale={result.Snapshot.ScalePercentOcrOrInjected:F1}%。");
            SetStage(TransformStage.TrackingStable);
        }
        catch (PipelineFailureException ex)
        {
            _markerOverlay.Hide();
            if (!IsVisible)
            {
                Show();
                Activate();
            }
            SetStage(ex.Stage);
            SetStatus($"重算失败 stage={ex.Stage} status={ex.Status}：{ex.Message}");
        }
        finally
        {
            _recomputePending = false;
            _flowRunning = false;
            RecomputeButton.IsEnabled = _lastResult is not null;
            if (!IsVisible)
            {
                Show();
                Activate();
            }
        }
    }

    private void ApplyResultOverlays(CaptureSession session, PipelineResult result)
    {
        _thumbnailBorder.TryShowIfCaptureMatches(
            result.NavigatorThumbnailRoiScreen,
            session.CaptureId,
            result.Snapshot.CaptureId);

        _workspaceBorder.TryShowIfCaptureMatches(
            result.WorkspaceRoiScreen,
            session.CaptureId,
            result.Snapshot.CaptureId);

        _navigatorBorder.TryShowIfCaptureMatches(
            result.NavigatorRoiScreen,
            session.CaptureId,
            result.Snapshot.CaptureId);

        _markerOverlay.TryShowIfGenerationMatches(
            result.Snapshot.Marker,
            result.Snapshot.CaptureId,
            result.Snapshot.Generation,
            session.CaptureId,
            result.Snapshot.Generation);
    }

    private void SetStage(TransformStage stage) => SetStatus($"阶段：{stage}");

    private void SetStatus(string text) => StatusText.Text = text;

    private sealed class ArchiveListItem
    {
        public string ArchiveId { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string CanvasSizeText { get; init; } = "";
        public string CreatedAtText { get; init; } = "";
        public string StatusText { get; init; } = "";
        public bool IsValid { get; init; }

        public static ArchiveListItem FromSummary(SaveArchiveSummary summary) => new()
        {
            ArchiveId = summary.ArchiveId,
            DisplayName = summary.DisplayName,
            CanvasSizeText = $"{summary.CanvasPixelWidth} × {summary.CanvasPixelHeight}",
            CreatedAtText = summary.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture),
            StatusText = summary.IsValid ? "可用" : summary.ValidationError ?? "不可用",
            IsValid = summary.IsValid
        };
    }
}
