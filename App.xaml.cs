using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using PromptGenerator.Models;
using PromptGenerator.Services;

namespace PromptGenerator;

public partial class App : Application
{
    /// <summary>ToolTip 统一摆放回调：按钮下方、水平居中、距按钮外边 2px。</summary>
    public static readonly CustomPopupPlacementCallback ToolTipPlacementCallback = ToolTipPlacement;

    private static CustomPopupPlacement[] ToolTipPlacement(Size popupSize, Size targetSize, Point offset)
        => new[]
        {
            new CustomPopupPlacement(
                new Point((targetSize.Width - popupSize.Width) / 2, targetSize.Height + 2),
                PopupPrimaryAxis.Horizontal)
        };

    protected override void OnStartup(StartupEventArgs e)
    {
        // 初始化统一日志：从设置读取 LogLevel，命令行 --debug 可临时提升到 Debug
        int settingsLevel = 2; // Info
        try
        {
            var settings = new SettingsStore(GetDataDir()).Load();
            settingsLevel = settings.LogLevel;
        }
        catch { /* 设置读取失败时用默认 Info */ }
        Logging.Init(e.Args, (LogLevel)Math.Clamp(settingsLevel, 0, 3));
        Logging.Info($"进程启动：版本={Environment.Version} 参数={string.Join(" ", e.Args ?? Array.Empty<string>())}");

        // 显式声明 PerMonitorV2 DPI 感知（必须在任何窗口创建前）
        SetProcessDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
        Logging.Info("主窗口已显示");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Logging.Info($"进程退出，退出码={e.ApplicationExitCode}");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logging.Error("未处理异常", e.Exception);
        MessageBox.Show($"发生未处理异常：\n{e.Exception}", "提示词生成器",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    /// <summary>数据目录（与模板/设置一致）：程序目录 data\，失败回退 %LOCALAPPDATA%\PromptGenerator\data。</summary>
    private static string GetDataDir()
    {
        try
        {
            var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "data");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PromptGenerator", "data");
            System.IO.Directory.CreateDirectory(dir);
            return dir;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
