using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PromptGenerator.Models;
using PromptGenerator.Services;

namespace PromptGenerator;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\PromptGenerator.SingleInstance";
    private const string ShowWindowEventName = @"Local\PromptGenerator.ShowWindow";

    /// <summary>单实例互斥体：同一会话内只允许一个实例（Local 前缀，避免跨会话/权限问题）。</summary>
    private static Mutex? _singleInstanceMutex;

    /// <summary>第二实例唤起信号：收到后把窗口恢复到桌面。</summary>
    private EventWaitHandle? _showSignal;

    public App()
    {
        InitializeComponent();

        // 崩溃处理三层（封装前补齐）：
        //   Dispatcher        —— UI 线程异常（可标记已处理、弹窗提示后继续）
        //   AppDomain         —— 进程级未处理异常（记录后由 CLR 终止进程，不阻塞退出）
        //   UnobservedTask    —— 未观察的 Task 异常（如后台读取管道超时被杀产生的未观察任务，记录并 SetObserved 防崩溃）
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    /// <summary>ToolTip 统一摆放回调：按钮下方、水平居中、距按钮外边 2px。</summary>
    public static readonly CustomPopupPlacementCallback ToolTipPlacementCallback = ToolTipPlacement;

    /// <summary>确认弹层（popconfirm）放置回调：首选按钮下方左对齐，其次上方左对齐、
    /// 下方/上方右对齐——WPF 自动选择第一个能完整落入屏幕的位置（窗口贴屏幕边缘时完整显示）。</summary>
    public static readonly CustomPopupPlacementCallback ConfirmPopupPlacement = ConfirmPlacement;

    private static CustomPopupPlacement[] ConfirmPlacement(Size popupSize, Size targetSize, Point offset)
        => new[]
        {
            new CustomPopupPlacement(new Point(0, targetSize.Height + 4), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(0, -popupSize.Height - 4), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height + 4), PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, -popupSize.Height - 4), PopupPrimaryAxis.Horizontal),
        };

    private static CustomPopupPlacement[] ToolTipPlacement(Size popupSize, Size targetSize, Point offset)
        => new[]
        {
            // 首选：按钮下方、水平居中、外边距 2px
            new CustomPopupPlacement(
                new Point((targetSize.Width - popupSize.Width) / 2, targetSize.Height + 2),
                PopupPrimaryAxis.Horizontal),
            // 按钮贴屏幕底边/窗口底部时翻转到按钮上方，仍水平居中、外边距 2px
            new CustomPopupPlacement(
                new Point((targetSize.Width - popupSize.Width) / 2, -popupSize.Height - 2),
                PopupPrimaryAxis.Horizontal)
        };

    protected override void OnStartup(StartupEventArgs e)
    {
        // 单实例：同会话内只允许一个实例；第二实例通知已运行实例显示窗口后退出（无提示框打扰）
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowWindowEventName);
                signal.Set();
            }
            catch { /* 已运行实例尚未就绪时静默退出 */ }
            _singleInstanceMutex.Dispose();
            Shutdown();
            return;
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        StartShowSignalListener();

        // 初始化统一日志：从设置读取 LogLevel，命令行 --debug 可临时提升到 Debug
        int settingsLevel = 2; // Info
        try
        {
            var settings = new SettingsStore(AppPaths.DataDir).Load();
            settingsLevel = settings.LogLevel;
        }
        catch (Exception ex) { Logging.Warn($"设置读取失败，使用默认 Info：{ex.Message}"); }
        Logging.Init(e.Args, (LogLevel)Math.Clamp(settingsLevel, 0, 3));
        Logging.Info($"进程启动：版本={Environment.Version} 参数={string.Join(" ", e.Args ?? Array.Empty<string>())}");

        // 显式声明 PerMonitorV2 DPI 感知（必须在任何窗口创建前）
        SetProcessDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        base.OnStartup(e);
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
        Logging.Info("主窗口已显示");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logging.Info($"进程退出，退出码={e.ApplicationExitCode}");
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { /* 所有权已由进程退出回收 */ }
        _singleInstanceMutex?.Dispose();
        _showSignal?.Dispose();
        base.OnExit(e);
    }

    /// <summary>后台线程监听"第二实例唤起"信号，收到后经 Dispatcher 把窗口恢复到桌面。</summary>
    private void StartShowSignalListener()
    {
        var t = new Thread(() =>
        {
            try
            {
                while (_showSignal!.WaitOne())
                    Dispatcher.Invoke(ShowMainWindowFromSecondInstance);
            }
            catch { /* 监听线程退出不影响主进程 */ }
        })
        { IsBackground = true, Name = "SingleInstance-ShowListener" };
        t.Start();
    }

    private void ShowMainWindowFromSecondInstance()
    {
        if (MainWindow == null) return;
        if (MainWindow.WindowState == WindowState.Minimized)
            MainWindow.WindowState = WindowState.Normal;
        MainWindow.Show();
        MainWindow.Activate();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash("Dispatcher", e.Exception);
        Logging.Error("未处理异常", e.Exception);
        MessageBox.Show($"发生未处理异常：\n{e.Exception}", "提示词生成器",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>进程级未处理异常（非 UI 线程 / 逃逸到 CLR 的异常）。只记录，不阻止进程终止。</summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogCrash("AppDomain", e.ExceptionObject as Exception);
    }

    /// <summary>未观察的 Task 异常（后台任务失败但无人 await）。记录并标记已观察，防止进程被终结。</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogCrash("UnobservedTask", e.Exception);
        e.SetObserved();
    }

    /// <summary>崩溃统一落点：data\crash.log（UTF-8 追加，与常规日志分离，便于封装后排障）。</summary>
    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var path = Path.Combine(AppPaths.DataDir, "crash.log");
            var msg = $"\n[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}]\n{ex}\n";
            File.AppendAllText(path, msg, Encoding.UTF8);
        }
        catch { /* 崩溃记录失败不影响主流程 */ }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
