using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Detection;
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
        // 只隐藏 ROI 框；橙色 L 标记始终保留，便于对照画布角。
        HideRoiBorders();
        SetStatus("已隐藏区域标记（橙色 L 仍保留）。");
    }

    private void HideRoiBorders()
    {
        _workspaceBorder.Hide();
        _navigatorBorder.Hide();
        _thumbnailBorder.Hide();
    }

    private void HideAllOverlays()
    {
        _markerOverlay.Hide();
        HideRoiBorders();
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
            // —— 阶段 1：输入画布像素（取消 = 整段退出）——
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

            // —— 冻结截图：失败则重载截图（保留已确认尺寸）——
            CaptureSession session = await CaptureFrozenSessionWithReloadAsync().ConfigureAwait(true);

            _activeSession = session;
            SetStage(TransformStage.CaptureFrozen);
            LiveDebugLog.Write(
                $"[Capture] id={session.CaptureId} frame={session.FrozenCapture.Width}x{session.FrozenCapture.Height} " +
                $"origin=({session.OriginX},{session.OriginY}) source=ClipStudioThreadWindows");

            // —— 阶段 2：粗选工作区 + 纠正；检测失败则同帧重选；Esc = 整段退出 ——
            DetectOutcome workspace = await SelectAndDetectWorkspaceUntilSuccessAsync(session)
                .ConfigureAwait(true);
            if (workspace.Background is null)
                return; // Esc 退出（状态文案已写）

            _workspaceBorder.TryShowIfCaptureMatches(
                workspace.RectScreenPhysicalPx,
                session.CaptureId,
                workspace.SourceCaptureId);

            // —— 阶段 3：粗选导航器 + C-II/观测/OCR/求解；失败均回到粗选导航器；Esc = 整段退出 ——
            PipelineResult? result = await SelectNavigatorAndSolveUntilSuccessAsync(session, workspace)
                .ConfigureAwait(true);
            if (result is null)
                return; // Esc 退出

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

    /// <summary>Hide main window and freeze CSP; on failure reload capture until success.</summary>
    private async Task<CaptureSession> CaptureFrozenSessionWithReloadAsync()
    {
        while (true)
        {
            SetStatus("正在隐藏窗口并冻结 CLIP STUDIO PAINT 窗口截图…");
            Hide();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(120);

            _activeSession?.Dispose();
            _activeSession = null;

            try
            {
                return CaptureSession.CreateFromClipStudioWindows();
            }
            catch (InvalidOperationException ex)
            {
                Show();
                Activate();
                SetStatus($"截图失败，将重新截图：{ex.Message}");
                MessageBox.Show(
                    this,
                    $"{ex.Message}\n\n将重新尝试截图。",
                    "截图失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    /// <summary>
    /// Stage 2 loop: select workspace ROI → detect. Detect failure retries selection on same frame.
    /// Esc cancels the entire initialization (returns a failed outcome with null Background).
    /// </summary>
    private async Task<DetectOutcome> SelectAndDetectWorkspaceUntilSuccessAsync(CaptureSession session)
    {
        while (true)
        {
            session.ClearRoi(RoiKind.WorkspaceUser);
            _workspaceBorder.Hide();

            Hide();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(60);

            SetStage(TransformStage.SelectingWorkspaceRoi);
            var wsRoi = new RoiSelectWindow(
                session,
                RoiKind.WorkspaceUser,
                "拖拽框选工作区粗略范围（仅显示 CSP 窗口）· Enter 确认 · Esc 退出初始化");
            bool? wsOk = wsRoi.ShowDialog();
            if (wsOk != true || session.WorkspaceUserRoiCapturePx is null)
            {
                Show();
                Activate();
                SetStatus("已退出初始化（Esc / 取消工作区框选）。");
                return new DetectOutcome
                {
                    Success = false,
                    StatusName = "Cancelled",
                    Message = "用户取消工作区框选",
                    SourceCaptureId = session.CaptureId
                };
            }

            SetStage(TransformStage.DetectingWorkspace);
            SetStatus($"CaptureId={session.CaptureId}。正在纠正工作区…");
            var workspace = await Task.Run(() => _pipeline.DetectWorkspace(session)).ConfigureAwait(true);

            if (workspace.Success && workspace.Background is not null)
                return workspace;

            Show();
            Activate();
            SetStage(TransformStage.DetectingWorkspace);
            SetStatus(
                $"工作区标记失败，将重新框选工作区。{workspace.StatusName} — {workspace.Message} " +
                $"(CaptureId={session.CaptureId})");
            MessageBox.Show(
                this,
                $"工作区纠正失败：{workspace.StatusName} — {workspace.Message}\n\n将重新框选工作区。",
                "工作区失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Stage 3 loop: select navigator → C-II → observe/OCR/solve.
    /// Any failure returns to navigator selection; workspace green border is kept.
    /// Esc cancels the entire initialization (returns null).
    /// </summary>
    private async Task<PipelineResult?> SelectNavigatorAndSolveUntilSuccessAsync(
        CaptureSession session,
        DetectOutcome workspace)
    {
        while (true)
        {
            session.ClearRoi(RoiKind.Navigator);
            _navigatorBorder.Hide();
            _thumbnailBorder.Hide();
            _markerOverlay.Hide();

            Hide();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(60);

            SetStage(TransformStage.SelectingNavigatorRoi);
            var navRoi = new RoiSelectWindow(
                session,
                RoiKind.Navigator,
                "拖拽框选完整导航器面板（仅显示 CSP 窗口，直接采用）· Enter 确认 · Esc 退出初始化");
            bool? navOk = navRoi.ShowDialog();

            if (navOk != true || session.NavigatorRoiCapturePx is null)
            {
                Show();
                Activate();
                SetStatus("已退出初始化（Esc / 取消导航器框选）。工作区绿框仍保留。");
                return null;
            }

            var navigatorScreen = session.CaptureToScreen(session.NavigatorRoiCapturePx.Value);
            _navigatorBorder.Show(navigatorScreen, session.CaptureId);

            Show();
            Activate();

            try
            {
                SetStage(TransformStage.DetectingNavigatorThumbnailCII);
                SetStatus($"CaptureId={session.CaptureId}。正在 C-II 生成导航器缩略图…");
                var thumbnail = await Task.Run(() => _pipeline.DetectNavigatorThumbnail(session, workspace))
                    .ConfigureAwait(true);
                if (!thumbnail.Success)
                {
                    _thumbnailBorder.Hide();
                    SetStatus(
                        $"缩略图标记失败，将重新框选导航器。{thumbnail.StatusName} — {thumbnail.Message} " +
                        $"(CaptureId={session.CaptureId})");
                    MessageBox.Show(
                        this,
                        $"缩略图失败：{thumbnail.StatusName} — {thumbnail.Message}\n\n将重新框选导航器。",
                        "导航器阶段失败",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    continue;
                }

                _thumbnailBorder.TryShowIfCaptureMatches(
                    thumbnail.RectScreenPhysicalPx,
                    session.CaptureId,
                    thumbnail.SourceCaptureId);
                SetStatus("缩略图已标记（品红）。继续观测 / OCR / 求解…");

                var progress = new Progress<TransformStage>(SetStage);
                return await Task.Run(
                        async () => await _pipeline.ContinueAfterThumbnailAsync(
                                session, workspace, thumbnail, progress)
                            .ConfigureAwait(false))
                    .ConfigureAwait(true);
            }
            catch (PipelineFailureException ex)
            {
                _markerOverlay.Hide();
                _thumbnailBorder.Hide();
                _navigatorBorder.Hide();
                if (!IsVisible)
                {
                    Show();
                    Activate();
                }

                SetStage(ex.Stage);
                string ocrHint = ex.Stage == TransformStage.ReadingNavigatorNumbers
                    ? $"  OCR调试: %TEMP%\\sct_ocr_debug\\{ex.CaptureId}"
                    : "";
                SetStatus(
                    $"失败 stage={ex.Stage} status={ex.Status}：{ex.Message} " +
                    $"(CaptureId={ex.CaptureId}, gen={ex.Generation})。将重新框选导航器。{ocrHint}");
                MessageBox.Show(
                    this,
                    $"stage={ex.Stage}\n{ex.Message}\n\n将重新框选导航器。",
                    "导航器阶段失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
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
            CaptureSession session;
            try
            {
                session = CaptureSession.CreateFromClipStudioWindows();
            }
            catch (InvalidOperationException ex)
            {
                Show();
                Activate();
                SetStatus(ex.Message);
                return;
            }

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
            CaptureSession session;
            try
            {
                session = CaptureSession.CreateFromClipStudioWindows();
            }
            catch (InvalidOperationException ex)
            {
                Show();
                Activate();
                SetStatus(ex.Message);
                return;
            }

            _activeSession = session;
            SetStage(TransformStage.CaptureFrozen);

            var progress = new Progress<TransformStage>(SetStage);
            // Prefer bound archive anchors when present so session recompute cannot drift from archive.
            var result = await Task.Run(
                    async () =>
                    {
                        if (_activeArchive is not null)
                        {
                            return await _pipeline.RecomputeFromArchiveAsync(
                                    session, _activeArchive, progress)
                                .ConfigureAwait(false);
                        }

                        return await _pipeline.RecomputeAsync(session, _lastResult, progress)
                            .ConfigureAwait(false);
                    })
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
            SetStatus(
                $"重算失败 stage={ex.Stage} status={ex.Status}：{ex.Message}" +
                (ex.EvidenceSummary is "MissingOcrLayoutOrAnchors"
                    ? "。请从存档开始或重新初始化。"
                    : ""));
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
