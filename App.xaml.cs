using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace PromptGenerator;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 显式声明 PerMonitorV2 DPI 感知（必须在任何窗口创建前）
        SetProcessDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"发生未处理异常：\n{e.Exception}", "提示词生成器",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
