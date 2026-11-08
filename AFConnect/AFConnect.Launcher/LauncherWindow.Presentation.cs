using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AFConnect.EDGE;

namespace AFConnect.Launcher;

public partial class LauncherWindow
{
    private readonly CancellationTokenSource introCancellation = new();
    private readonly DispatcherTimer presentationTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch presentationClock = new();
    private CancellationTokenSource? welcomeCancellation;
    private bool portalVisible;
    private bool presentingWelcome;
    private bool typingHeading;
    private string completeHeading = "需要访问许可";
    private TimeSpan headingStarted;
    private int lastClockSecond = -1;

    private void InitializePresentation()
    {
        this.FindControl<Button>("IntroSkipButton")!.Click += (_, _) => introCancellation.Cancel();
        this.FindControl<Button>("SkipIntroButton")!.Click += (_, _) => welcomeCancellation?.Cancel();
        this.FindControl<Button>("DiagnosticsButton")!.Click += (_, _) =>
        {
            var panel = this.FindControl<Border>("DiagnosticsPanel")!;

            panel.IsVisible = !panel.IsVisible;
        };
        this.FindControl<Button>("CloseDiagnosticsButton")!.Click += (_, _) =>
            this.FindControl<Border>("DiagnosticsPanel")!.IsVisible = false;
        Opened += async (_, _) =>
        {
            FitWindowToWorkingArea();
            LoadEndpoint();
            presentationClock.Start();
            presentationTimer.Start();
            await ShowIntroAsync();
        };
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (this.FindControl<Border>("DiagnosticsPanel")!.IsVisible)
                    this.FindControl<Border>("DiagnosticsPanel")!.IsVisible = false;
                else if (this.FindControl<Border>("IntroOverlay")!.IsVisible)
                    introCancellation.Cancel();
                else if (presentingWelcome)
                    welcomeCancellation?.Cancel();

                e.Handled = true;
            }
            else if (e.Key == Key.Enter && !busy && e.Source is not (Button or ComboBox or TextBox)
                && !this.FindControl<Border>("IntroOverlay")!.IsVisible
                && !this.FindControl<Border>("DiagnosticsPanel")!.IsVisible)
            {
                e.Handled = true;
                await RunPrimaryAsync();
            }
        };
        presentationTimer.Tick += (_, _) =>
        {
            if (WindowState == WindowState.Minimized || !IsVisible) return;

            int second = (int)presentationClock.Elapsed.TotalSeconds;
            var backdrop = this.FindControl<LauncherBackdrop>("Backdrop")!;

            if (second != lastClockSecond)
            {
                lastClockSecond = second;
                Text("ClockText", DateTime.Now.ToString("HH:mm:ss"));
            }

            if (backdrop.IsVisible && (backdrop.IsScanning || backdrop.ShowReticle))
                backdrop.Advance(presentationClock.Elapsed.TotalSeconds);

            if (typingHeading)
            {
                int visible = Math.Clamp((int)((presentationClock.Elapsed - headingStarted).TotalMilliseconds / 46), 0, completeHeading.Length);

                var heading = this.FindControl<TextBlock>("StatusHeading")!;

                heading.Clip = visible == completeHeading.Length ? null : new RectangleGeometry(
                    new Rect(0, 0, heading.Bounds.Width * visible / completeHeading.Length, heading.Bounds.Height));
                typingHeading = visible < completeHeading.Length;
            }
        };
        Closed += (_, _) =>
        {
            presentationTimer.Stop();
            introCancellation.Cancel();
            introCancellation.Dispose();
            welcomeCancellation?.Cancel();
        };
    }

    private void FitWindowToWorkingArea()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;

        if (screen is null) return;

        var available = screen.WorkingArea.Size.ToSize(1 / screen.Scaling);
        double scale = Math.Min(1, Math.Min((available.Width - 48) / 1250, (available.Height - 48) / 750));

        // Fit the actual work area in logical pixels, preserving the wide aspect ratio and edge controls on small or high-DPI screens.
        if (scale <= 0) return;

        MinWidth = Math.Min(1050, 1250 * scale);
        MinHeight = Math.Min(630, 750 * scale);
        Width = 1250 * scale;
        Height = 750 * scale;

        if (scale < 1)
            Position = new PixelPoint(screen.WorkingArea.X + (int)((screen.WorkingArea.Width - Width * screen.Scaling) / 2),
                screen.WorkingArea.Y + (int)((screen.WorkingArea.Height - Height * screen.Scaling) / 2));
    }

    private async Task ShowIntroAsync()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(introCancellation.Token, lifetime.Token);
        var overlay = this.FindControl<Border>("IntroOverlay")!;
        var wash = this.FindControl<Border>("WashOverlay")!;

        try
        {
            await Task.Delay(260, cancellation.Token);
            await FadeAsync(this.FindControl<StackPanel>("IntroBrand")!, 0, 1, 560, cancellation.Token);
            await Task.Delay(950, cancellation.Token);
            await FadeAsync(wash, 0, 1, 220, cancellation.Token);
            overlay.IsVisible = false;
            this.FindControl<Button>("IntroSkipButton")!.IsVisible = false;
            await FadeAsync(wash, 1, 0, 720, cancellation.Token);
            RevealHeading("需要访问许可");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            overlay.IsVisible = false;
            wash.Opacity = 0;
            this.FindControl<Button>("IntroSkipButton")!.IsVisible = false;
        }
    }

    private void ShowConnectionStage(int step, string detail)
    {
        // Progress uses real connection-layer callbacks; animation timers must not fabricate authentication or resource-check results.
        if (!busy || closing || lifetime.IsCancellationRequested || session is null) return;

        var (heading, english) = step switch
        {
            1 => ("校验运行环境", "CLIENT ENVIRONMENT CHECK"),
            2 => ("检测本机端口", "LOCAL ENDPOINT INSPECTION"),
            3 => ("接收访问需求", "ACCESS REQUEST RECEIVED"),
            4 => ("开始读取权限", "PERMISSION READ STARTED"),
            5 => ("登录验证", "LOGIN VERIFICATION"),
            _ => ("读取完毕 · 核验通过", "READ COMPLETE · VERIFIED")
        };
        var backdrop = this.FindControl<LauncherBackdrop>("Backdrop")!;

        RevealHeading(heading);
        Text("StatusEnglish", english);
        Text("StageText", detail + "…");
        Text("SubtitleEnglish", "ESTABLISHING AUTHORIZED SESSION");
        Text("StepLabel", $"[ {step:00} / 06 ] CONNECTING");
        Text("ActionHint", "正在连接，请稍候；可随时取消操作");
        this.FindControl<ProgressBar>("Progress")!.Value = step;
        this.FindControl<LauncherMark>("StageMark")!.IsVisible = step is 3 or 4;
        backdrop.Step = step;
        backdrop.IsScanning = step < 6;
        backdrop.InvalidateVisual();
    }

    private void ShowIdentityStage()
    {
        var backdrop = this.FindControl<LauncherBackdrop>("Backdrop")!;

        RevealHeading("登录验证");
        Text("StatusEnglish", "LOGIN VERIFICATION");
        Text("StepLabel", "[ 06 / 06 ] LOGIN");
        Text("StageText", "登录验证完成，可以登录游戏。");
        Text("SubtitleEnglish", "MATCHING OPERATOR RECORD");
        Text("ActionHint", "登录后进入主页面，选择区服启动游戏");
        this.FindControl<Border>("IdentityCard")!.IsVisible = true;
        this.FindControl<LauncherMark>("StageMark")!.IsVisible = false;
        Canvas.SetTop(this.FindControl<StackPanel>("CenterStage")!, 236);
        Canvas.SetTop(this.FindControl<StackPanel>("ActionPanel")!, 372);
        backdrop.IsScanning = false;
        backdrop.Step = 6;
        backdrop.InvalidateVisual();
    }

    private async Task ShowWelcomeAsync(CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var backdrop = this.FindControl<LauncherBackdrop>("Backdrop")!;
        var center = this.FindControl<StackPanel>("CenterStage")!;
        var welcome = this.FindControl<StackPanel>("WelcomePanel")!;
        ConnectSession? observed = session;

        welcomeCancellation = cancellation;
        presentingWelcome = true;
        this.FindControl<StackPanel>("ActionPanel")!.IsVisible = false;
        this.FindControl<Button>("SkipIntroButton")!.IsVisible = true;
        this.FindControl<Border>("StatusCaret")!.IsVisible = false;
        Canvas.SetTop(center, 275);

        try
        {
            // The handoff animation presents results already obtained; it does not request further authorization or extend background connection work.
            RevealHeading("接收访问需求");
            Text("StatusEnglish", "ACCESS REQUEST RECEIVED");
            Text("StageText", "已建立安全连接，访问需求已接收。");
            Text("SubtitleEnglish", "HANDSHAKE ESTABLISHED");
            Text("StepLabel", "[ 06 / 06 ] REQUEST");
            this.FindControl<LauncherMark>("StageMark")!.IsVisible = true;
            await Task.Delay(750, cancellation.Token);
            RevealHeading("开始读取权限");
            Text("StatusEnglish", "PERMISSION READ STARTED");
            Text("StageText", "公共资源、授权身份与区服目录已核验。");
            Text("SubtitleEnglish", "READING PERMISSION SET");
            Text("StepLabel", "[ 06 / 06 ] READ");
            backdrop.IsScanning = true;
            await Task.Delay(750, cancellation.Token);
            backdrop.IsScanning = false;
            this.FindControl<LauncherMark>("StageMark")!.IsVisible = false;
            backdrop.ShowReticle = true;
            RevealHeading("读取完毕 · 核验通过");
            Text("StatusEnglish", "READ COMPLETE · VERIFIED");
            Text("StageText", "登录验证完成，会话已建立。");
            Text("SubtitleEnglish", "LOGIN VERIFIED · SESSION ESTABLISHED");
            Text("StepLabel", "[ 06 / 06 ] VERIFY");
            await Task.Delay(1300, cancellation.Token);
            await FadeAsync(center, 1, 0, 340, cancellation.Token);
            center.IsVisible = false;
            backdrop.ShowReticle = false;
            backdrop.InvalidateVisual();
            welcome.IsVisible = true;
            Text("StepLabel", "[ 06 / 06 ] WELCOME");
            await FadeAsync(welcome, 0, 1, 420, cancellation.Token);
            await Task.Delay(1300, cancellation.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        finally
        {
            presentingWelcome = false;
            welcomeCancellation = null;
            backdrop.ShowReticle = false;
            backdrop.IsScanning = false;
            this.FindControl<Button>("SkipIntroButton")!.IsVisible = false;
        }

        token.ThrowIfCancellationRequested();

        // Remote failure can dispose the session during an animation wait. An old animation must not restore an invalid online page.
        if (observed is null || !ReferenceEquals(session, observed)) return;

        portalVisible = true;
        this.FindControl<Canvas>("SequencePanel")!.IsVisible = false;
        backdrop.IsVisible = false;
        this.FindControl<Grid>("PortalPanel")!.IsVisible = true;
        this.FindControl<TextBlock>("OnlineLabel")!.IsVisible = true;
        await FadeAsync(this.FindControl<Grid>("PortalPanel")!, 0, 1, 420, token);
    }

    private void ResetPresentation()
    {
        var backdrop = this.FindControl<LauncherBackdrop>("Backdrop")!;
        var center = this.FindControl<StackPanel>("CenterStage")!;
        var action = this.FindControl<StackPanel>("ActionPanel")!;

        portalVisible = false;
        backdrop.IsVisible = true;
        backdrop.ShowReticle = false;
        backdrop.IsScanning = false;
        backdrop.Step = 0;
        backdrop.InvalidateVisual();
        center.IsVisible = true;
        center.Opacity = 1;
        action.IsVisible = true;
        Canvas.SetTop(center, 275);
        Canvas.SetTop(action, 425);
        this.FindControl<Canvas>("SequencePanel")!.IsVisible = true;
        this.FindControl<Grid>("PortalPanel")!.IsVisible = false;
        this.FindControl<Border>("IdentityCard")!.IsVisible = false;
        this.FindControl<StackPanel>("WelcomePanel")!.IsVisible = false;
        this.FindControl<LauncherMark>("StageMark")!.IsVisible = false;
        this.FindControl<Border>("StatusCaret")!.IsVisible = true;
        this.FindControl<TextBlock>("OnlineLabel")!.IsVisible = false;
        RevealHeading("需要访问许可");
        Text("StatusEnglish", "ACCESS PERMISSION REQUIRED");
        Text("StepLabel", "[ 00 / 06 ] AUTHORIZATION");
        Text("StageText", "等待开始登陆…");
        Text("SubtitleEnglish", "AWAITING GAME LOGIN");
        Text("ActionHint", "点击继续，或按 Enter 开始登陆");
    }

    private void RevealHeading(string heading)
    {
        completeHeading = heading;
        headingStarted = presentationClock.Elapsed;
        typingHeading = true;

        var headingBlock = this.FindControl<TextBlock>("StatusHeading")!;

        // Reserve the full text width and advance only its visible portion to prevent a centered title from shifting as characters appear.
        headingBlock.Text = heading;
        headingBlock.Clip = new RectangleGeometry(default(Rect));
    }

    private static async Task FadeAsync(Control control, double from, double to, int milliseconds, CancellationToken token)
    {
        var elapsed = Stopwatch.StartNew();

        control.Opacity = from;

        // Advance on the UI dispatcher using monotonic time. Skipping, closing, and offscreen verification share one cancellation boundary.
        while (elapsed.ElapsedMilliseconds < milliseconds)
        {
            token.ThrowIfCancellationRequested();

            double progress = Math.Clamp(elapsed.Elapsed.TotalMilliseconds / milliseconds, 0, 1);
            double eased = 1 - Math.Pow(1 - progress, 3);

            control.Opacity = from + (to - from) * eased;
            await Task.Delay(16, token);
        }

        token.ThrowIfCancellationRequested();
        control.Opacity = to;
    }
}
