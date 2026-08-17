using System.Windows;
using System.Windows.Threading;
using WorkspaceBorderDetect.Capture;
using WorkspaceBorderDetect.Detection;
using WorkspaceBorderDetect.Ui;

namespace WorkspaceBorderDetect;

public partial class MainWindow : Window
{
    private readonly WorkspaceDetectorService _detector = new();
    private readonly WorkspaceOverlayWindow _overlay = new();
    private CaptureSession? _activeSession;
    private bool _flowRunning;

    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            _overlay.Dispose();
            _activeSession?.Dispose();
        };
    }

    private async void StartButton_OnClick(object sender, RoutedEventArgs e)
        => await RunRecognitionFlowAsync();

    private async void RedetectButton_OnClick(object sender, RoutedEventArgs e)
        => await RunRecognitionFlowAsync();

    private void HideOverlayButton_OnClick(object sender, RoutedEventArgs e)
    {
        _overlay.Hide();
        SetStatus("已隐藏绿色覆盖层。");
    }

    private async Task RunRecognitionFlowAsync()
    {
        if (_flowRunning)
            return;

        _flowRunning = true;
        StartButton.IsEnabled = false;
        RedetectButton.IsEnabled = false;

        try
        {
            // 1) Hide overlay + this window before freeze (avoid capturing ourselves).
            _overlay.Hide();
            SetStatus("正在隐藏窗口并冻结桌面截图…");
            Hide();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            // Give the compositor a brief moment so the window is actually gone from the screen.
            await Task.Delay(120);

            _activeSession?.Dispose();
            _activeSession = null;

            // 2) Freeze full virtual desktop.
            var session = CaptureSession.CreateFromVirtualScreen();
            _activeSession = session;

            // 3) ROI on frozen bitmap (main window still hidden).
            var roiWindow = new RoiSelectWindow(session);
            bool? ok = roiWindow.ShowDialog();

            // Restore control window after ROI UI closes.
            Show();
            Activate();

            if (ok != true || session.UserRoiCapturePx is null)
            {
                SetStatus("已取消框选。");
                return;
            }

            var roi = session.UserRoiCapturePx.Value;
            SetStatus(
                $"已冻结截图（CaptureId={session.CaptureId}）。" +
                $"ROI [{roi.Left},{roi.Top},{roi.Right},{roi.Bottom})，正在调用原生检测…");

            // 4) Native detect on frozen frame.
            var outcome = await Task.Run(() => _detector.Detect(session));

            if (!outcome.Success)
            {
                _overlay.Hide();
                SetStatus($"检测失败：{outcome.StatusName} — {outcome.Message}");
                return;
            }

            // 5) Show overlay only when status OK and CaptureId matches.
            bool shown = _overlay.TryShowIfCaptureMatches(
                outcome.WorkspaceScreenPhysicalPx,
                session.CaptureId,
                outcome.SourceCaptureId);

            if (!shown)
            {
                SetStatus($"结果 CaptureId 不匹配，未显示覆盖层。{outcome.Message}");
                return;
            }

            SetStatus(
                $"检测成功。等级={outcome.EvidenceGrade}，置信度={outcome.Confidence:F2}，" +
                $"屏幕矩形={outcome.WorkspaceScreenPhysicalPx}。已显示绿色边框。");
        }
        catch (Exception ex)
        {
            _overlay.Hide();
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
            RedetectButton.IsEnabled = true;
            _flowRunning = false;
        }
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }
}
