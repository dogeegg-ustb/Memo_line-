using System.Windows;
using System.Windows.Threading;
using ScreenCanvasTransform.Capture;
using ScreenCanvasTransform.Diagnostics;
using ScreenCanvasTransform.Services;
using ScreenCanvasTransform.State;
using ScreenCanvasTransform.Ui;

namespace ScreenCanvasTransform;

public partial class MainWindow : Window
{
    private readonly TransformPipelineService _pipeline = new();
    // Distinct border colors: Workspace=green, Navigator=cyan, Thumbnail=magenta.
    private readonly RoiBorderOverlayWindow _workspaceBorder = RoiBorderOverlayWindow.CreateWorkspace();
    private readonly RoiBorderOverlayWindow _navigatorBorder = RoiBorderOverlayWindow.CreateNavigator();
    private readonly RoiBorderOverlayWindow _thumbnailBorder = RoiBorderOverlayWindow.CreateNavigatorThumbnail();
    private readonly MarkerOverlayWindow _markerOverlay = new();
    private CaptureSession? _activeSession;
    private PipelineResult? _lastResult;
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

    private async void StartButton_OnClick(object sender, RoutedEventArgs e)
        => await RunInitializationFlowAsync();

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

            // Architecture §5.1: hide overlays before capture.
            HideAllOverlays();
            SetStatus("正在隐藏窗口并冻结桌面截图…");
            Hide();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(120);

            _activeSession?.Dispose();
            _activeSession = null;

            var session = CaptureSession.CreateFromVirtualScreen();
            _activeSession = session;
            LiveDebugLog.Write(
                $"[Capture] id={session.CaptureId} frame={session.FrozenCapture.Width}x{session.FrozenCapture.Height} " +
                $"origin=({session.OriginX},{session.OriginY})");

            // 1) Workspace user ROI
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

            // 2) Correct workspace immediately — failure ends init (no navigator stage).
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

            // Green border for corrected WorkspaceRoi
            _workspaceBorder.TryShowIfCaptureMatches(
                workspace.RectScreenPhysicalPx,
                session.CaptureId,
                workspace.SourceCaptureId);

            // 3) Navigator ROI only after workspace success
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

            // Cyan border for user-adopted NavigatorRoi
            var navigatorScreen = session.CaptureToScreen(session.NavigatorRoiCapturePx.Value);
            _navigatorBorder.Show(navigatorScreen, session.CaptureId);

            // 4) C-II thumbnail immediately — magenta border as soon as ROI is ready
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
            SetStatus($"缩略图已标记（品红）。继续观测 / OCR / 求解…");

            var progress = new Progress<TransformStage>(SetStage);
            var result = await Task.Run(
                    async () => await _pipeline.ContinueAfterThumbnailAsync(
                            session, workspace, thumbnail, progress)
                        .ConfigureAwait(false))
                .ConfigureAwait(true);

            // Refresh borders from final snapshot
            _thumbnailBorder.TryShowIfCaptureMatches(
                result.NavigatorThumbnailRoiScreen,
                session.CaptureId,
                result.Snapshot.CaptureId);

            // Keep workspace / navigator borders with final ROIs from snapshot
            _workspaceBorder.TryShowIfCaptureMatches(
                result.WorkspaceRoiScreen,
                session.CaptureId,
                result.Snapshot.CaptureId);
            _navigatorBorder.TryShowIfCaptureMatches(
                result.NavigatorRoiScreen,
                session.CaptureId,
                result.Snapshot.CaptureId);

            bool markerShown = _markerOverlay.TryShowIfGenerationMatches(
                result.Snapshot.Marker,
                result.Snapshot.CaptureId,
                result.Snapshot.Generation,
                session.CaptureId,
                result.Snapshot.Generation);

            string path = result.Snapshot.UsedDirectWorkspacePath ? "直接工作区路径" : "导航器路径";
            string off = result.Snapshot.Marker.Offscreen != 0 ? "（MarkerOffscreen）" : "";
            SetStatus(
                $"已发布 gen={result.Snapshot.Generation}，{path}，" +
                $"scale={result.Snapshot.Numbers.ScalePercent:F1}% rel={result.Snapshot.RelativeScale:F3}，" +
                $"rot={result.Snapshot.RotationDegrees:F1}°，conf={result.Snapshot.Confidence:F2}。" +
                " 边框：绿=工作区 / 青=导航器 / 品红=缩略图。" +
                (markerShown ? $" 橙色 L 已显示{off}。" : " 标记未显示。"));
            SetStage(TransformStage.TrackingStable);
            _lastResult = result;
            RecomputeButton.IsEnabled = true;
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

            var progress = new Progress<TransformStage>(SetStage);
            var result = await Task.Run(
                    async () => await _pipeline.RecomputeAsync(session, _lastResult, progress)
                        .ConfigureAwait(false))
                .ConfigureAwait(true);

            Show();
            Activate();

            _workspaceBorder.TryShowIfCaptureMatches(
                result.WorkspaceRoiScreen, session.CaptureId, result.Snapshot.CaptureId);
            _navigatorBorder.TryShowIfCaptureMatches(
                result.NavigatorRoiScreen, session.CaptureId, result.Snapshot.CaptureId);
            _thumbnailBorder.TryShowIfCaptureMatches(
                result.NavigatorThumbnailRoiScreen, session.CaptureId, result.Snapshot.CaptureId);

            bool markerShown = _markerOverlay.TryShowIfGenerationMatches(
                result.Snapshot.Marker,
                result.Snapshot.CaptureId,
                result.Snapshot.Generation,
                session.CaptureId,
                result.Snapshot.Generation);

            _lastResult = result;
            SetStatus(
                $"重算完成 gen={result.Snapshot.Generation} recompute={result.Snapshot.RecomputeGeneration}，" +
                $"rot_geo={result.Snapshot.RotationDegreesGeometry:F1}°，" +
                $"scale={result.Snapshot.ScalePercentOcrOrInjected:F1}%。" +
                (markerShown ? " 橙色 L 已更新。" : " 标记未显示。"));
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

    private void SetStage(TransformStage stage) => SetStatus($"阶段：{stage}");

    private void SetStatus(string text) => StatusText.Text = text;
}
