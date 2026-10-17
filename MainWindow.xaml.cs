using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PromptGenerator.Controls;
using PromptGenerator.Models;
using PromptGenerator.Services;

namespace PromptGenerator;

public partial class MainWindow : Window
{
    private readonly TemplateStore _store = new();
    private TemplateStoreData _data = null!;
    private Template? _current;
    private bool _loadingTemplates;
    private bool _suppressReclaim;
    private readonly HashSet<(int, int)> _fresh = new();
    private bool _allowClose; // 托盘"退出"触发时置 true，允许真正关闭
    private readonly SettingsStore _settingsStore;
    private AppSettings _settings;
    private HotkeyManager? _hotkey;
    /// <summary>已保存快照（key=Template.Id）。切模板时未保存的编辑从快照恢复。</summary>
    private readonly Dictionary<string, Template> _savedSnapshots = new();
    /// <summary>未保存的新模板（"新增模板"创建后尚未保存；非空时禁止再新增）。</summary>
    private Template? _pendingNewTemplate;
    /// <summary>历史记录面板当前实例（非空表示已打开）。点击历史按钮在打开/关闭间切换。</summary>
    private HistoryWindow? _historyWindow;
    /// <summary>上次"生成并复制"成功的提示词；内容未改动时重复复制将被拦截。</summary>
    private string? _lastCopiedPrompt;
    /// <summary>上次"生成并提交"成功的提示词；内容未改动时重复提交将被拦截。</summary>
    private string? _lastSubmittedPrompt;
    /// <summary>输入过滤态：用户在模板框输入了非当前模板名的文本，下拉按包含匹配过滤。</summary>
    private bool _filterActive;
    /// <summary>双击改名态：框内文本作为新模板名编辑，Enter/失焦确认，Esc 取消。</summary>
    private bool _renaming;

    /// <summary>异步回写免疫现场：RefreshFilteredList 置空选中项后，ComboBox 异步把 Text 回写为旧选中项名；
    /// 该回写晚于 _loadingTemplates 短路期，TextChanged 据此识别并恢复用户输入、保持过滤态。</summary>
    private bool _filterResetPending;
    private string _filterResetText = "";

    /// <summary>本次文本变化是否源于用户键入（PreviewTextInput 置位，TextChanged 过滤分支消费）。
    /// 为 true 时进入编辑态（置 null + SetCurrentValue 恢复过滤词）；方向键导航（false）保留 SelectedItem，维持候选项高亮。</summary>
    private bool _userTyping;

    /// <summary>本次选中变化是否由鼠标点击候选项引发（窗口级 PreviewMouseLeftButtonDown 置位，SelectionChanged 消费）。
    /// 点击=确认选择（应切换模板）；键盘方向键导航经 _loadingTemplates 短路，不经此分支。</summary>
    private bool _mousePick;
    /// <summary>模板下拉的集合视图：ItemsSource 常驻此视图，过滤只切换 Filter 谓词，
    /// 避免重建 ItemsSource 破坏 IsEditable 下拉的输入与选中态（首字符被吞、方向键高亮丢失的根因）。</summary>
    private ICollectionView? _templatesView;
    /// <summary>键盘（Enter/Space）即将触发点击的按钮。Click 时据此区分键盘/鼠标触发：键盘触发保留焦点，鼠标触发清除焦点。</summary>
    private readonly HashSet<Button> _keyboardClickTargets = new();
    /// <summary>键盘聚焦（Tab）时已由本类即时打开的提示所在按钮；LostKeyboardFocus 时据此关闭，避免误关悬停路径打开的提示。</summary>
    private static readonly HashSet<Button> _keyboardTooltipButtons = new();
    /// <summary>键盘导航标记（Tab/方向键）：仅键盘导航引起的焦点变化才即时弹提示；鼠标点击、窗口关闭等焦点归还不触发。</summary>
    private static bool _keyboardNavPending;

    public MainWindow()
    {
        InitializeComponent();
        _data = _store.Load();
        _settingsStore = new SettingsStore(GetDataDir());
        _settings = _settingsStore.Load();
        ActionButton.Executed += OnActionExecuted;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        // 按钮点击后的焦点策略（统一覆盖新增/删除/保存等）：
        // 键盘（Enter/Space）触发的点击保留焦点，保证键盘导航连续（Tab 后 Enter，焦点仍在该按钮）；
        // 鼠标触发的点击清除键盘焦点，避免 IsKeyboardFocused 高亮残留（与既有鼠标行为完全一致）。
        // 判定方式：PreviewKeyDown 预先标记键盘激活（Enter 在 KeyDown、Space 在 KeyUp 触发 Click），
        // PreviewMouseLeftButtonDown 使标记失效，避免 Space 按住期间穿插鼠标点击时误判为键盘触发。
        EventManager.RegisterClassHandler(typeof(Button), Button.PreviewKeyDownEvent,
            new KeyEventHandler((sender, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Space)
                    _keyboardClickTargets.Add((Button)sender);
            }));
        EventManager.RegisterClassHandler(typeof(Button), Button.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler((sender, _) =>
            {
                _keyboardClickTargets.Remove((Button)sender);
                _keyboardNavPending = false;
            }));
        EventManager.RegisterClassHandler(typeof(Button), Button.ClickEvent,
            new RoutedEventHandler((sender, _) =>
            {
                // 键盘触发的点击：不做任何焦点操作，按钮自然保持键盘焦点
                if (sender is Button btn && _keyboardClickTargets.Remove(btn)) return;
                Keyboard.ClearFocus();
            }));
        // ToolTip 键盘聚焦策略：WPF 内建"键盘聚焦显示提示"同样走 InitialShowDelay（悬停延迟），
        // 导致 Tab 聚焦时提示被延迟。已在样式层用 ShowsToolTipOnKeyboardFocus=False 关闭内建键盘提示，
        // 改由 Got/LostKeyboardFocus 即时开/关（悬停路径仍走 800ms + BetweenShowDelay，行为不变）。
        EventManager.RegisterClassHandler(typeof(Button), Button.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnButtonGotKeyboardFocus));
        EventManager.RegisterClassHandler(typeof(Button), Button.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnButtonLostKeyboardFocus));
        // 键盘导航标记：Tab/方向键的 KeyDown 先于键盘导航处理到达（隧道阶段），据此区分
        // "键盘导航引起的聚焦"与"窗口关闭等焦点归还"，后者不即时弹提示。
        EventManager.RegisterClassHandler(typeof(UIElement), UIElement.PreviewKeyDownEvent,
            new KeyEventHandler((sender, e) =>
            {
                if (e.Key == Key.Tab || e.Key == Key.Up || e.Key == Key.Down
                    || e.Key == Key.Left || e.Key == Key.Right)
                    _keyboardNavPending = true;
            }));
        TrayIcon.IconSource = new BitmapImage(
            new Uri("pack://application:,,,/Assets/app-icon.png", UriKind.Absolute));
        // 加载后对每个已有模板存快照
        foreach (var t in _data.Templates) SaveSnapshot(t);
        Loaded += OnLoaded;
        Logging.Info($"MainWindow 构造完成，模板数={_data.Templates.Count} 热键={_settings.Hotkey}");
    }

    // ---------------- ToolTip 键盘聚焦（即时弹出） ----------------

    private static void OnButtonGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // 仅 Tab/方向键键盘导航引起的聚焦即时弹提示；鼠标点击（IsMouseOver 为真）与
        // 窗口关闭等焦点归还（无导航标记）均不弹。标记单次消费。
        bool fromKeyboardNav = _keyboardNavPending;
        _keyboardNavPending = false;
        if (sender is not Button b || b.IsMouseOver || !fromKeyboardNav) return;
        if (GetOrCreateToolTip(b) is not ToolTip tip) return;
        tip.PlacementTarget = b;
        tip.IsOpen = true;
        _keyboardTooltipButtons.Add(b);
    }

    private static void OnButtonLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not Button b || !_keyboardTooltipButtons.Remove(b)) return;
        if (b.ToolTip is ToolTip tip) tip.IsOpen = false;
    }

    /// <summary>获取按钮的 ToolTip 实例；字符串 ToolTip 首次键盘聚焦时物化为 ToolTip（便于程序化即时显示，隐式样式在显示时生效）。</summary>
    private static ToolTip? GetOrCreateToolTip(Button b)
    {
        if (b.ToolTip is ToolTip tip) return tip;
        if (b.ToolTip is string text)
        {
            tip = new ToolTip { Content = text };
            b.ToolTip = tip;
            return tip;
        }
        return null;
    }

    /// <summary>数据目录（与模板存储一致）：程序目录 data\，失败回退 %LOCALAPPDATA%\PromptGenerator\data。</summary>
    private static string GetDataDir()
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dir);
            return dir;
        }
        catch
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PromptGenerator", "data");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    // ---------------- 托盘 ----------------

    /// <summary>左键单击托盘图标 / 菜单"显示/隐藏" / 全局热键：切换窗口可见状态。</summary>
    private void TrayToggle_Click(object sender, RoutedEventArgs e) => ToggleWindow();

    private void ToggleWindow()
    {
        // 语义：仅"窗口在桌面可见（非最小化）"为显示态；最小化视为未显示，热键应恢复到桌面。
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            Hide();
            Logging.Debug("ToggleWindow: 隐藏");
        }
        else
        {
            Logging.Debug("ToggleWindow: 显示");
            WindowState = WindowState.Normal;
            Show();
            Activate();
            // 贴边收起状态下唤起：先与鼠标悬停触发的 Expand() 走同一路径——
            // 把窗口移回收起前的正常位置、并将光标吸进标题栏。
            // 否则 ComboBox 下拉框会基于屏幕外的收起坐标计算 Popup 位置，导致下拉框错位；
            // 后续 Expand() 移动窗口后 Popup 也不会自动重新定位。
            if (_docked && !_expanded)
            {
                Expand();
            }
            // 唤起后自动展开模板列表，方向键选择，Enter 进入
            Dispatcher.BeginInvoke(new Action(() =>
            {
                TemplateBox.Focus();
                TemplateBox.IsDropDownOpen = true;
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    /// <summary>托盘"退出"：真正退出程序（关闭按钮仅隐藏到托盘）。</summary>
    private void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        // 检查是否有未保存的新模板（无快照的）
        bool hasUnsaved = _data.Templates.Any(t => !_savedSnapshots.ContainsKey(t.Id));
        if (hasUnsaved)
        {
            var r = MessageBox.Show(this,
                "您还有未保存的模板内容，退出后将丢失。是否确认退出？",
                "未保存的内容", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;
        }
        _allowClose = true;
        Logging.Info("托盘退出：关闭程序");
        Application.Current.Shutdown();
    }

    /// <summary>点标题栏 × / Alt+F4 / 系统关闭：默认隐藏到托盘而非退出。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        SaveWindowState();
        _hotkey?.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Logging.Debug("OnLoaded: 刷新模板列表并注册热键");
        RefreshTemplateBox();
        FocusFirstInput();
        UpdatePinVisual();
        RegisterHotkeyFromSettings();
    }

    /// <summary>HWND 创建后恢复窗口位置/尺寸（比 OnLoaded 更可靠）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int workW = NativeMethods.GetSystemMetrics(61);
        int workH = NativeMethods.GetSystemMetrics(62);
        double workWDip = workW / scale;
        double workHDip = workH / scale;

        double w = _settings.WindowWidth > 0 ? _settings.WindowWidth : 365;
        double h = _settings.WindowHeight > 0 ? _settings.WindowHeight : 600;
        Width = Math.Min(w, workWDip * 0.95);
        Height = Math.Min(h, workHDip * 0.95);

        if (!double.IsNaN(_settings.WindowX) && !double.IsNaN(_settings.WindowY))
        {
            Left = Math.Max(0, Math.Min(_settings.WindowX, workWDip - Width));
            Top = Math.Max(0, Math.Min(_settings.WindowY, workHDip - Height));
        }
        else
        {
            Left = workWDip - Width - 24;
            Top = Math.Max(0, (workHDip - Height) / 2);
        }

        // 监听尺寸/位置变化，防抖保存
        SizeChanged += (_, _) => ScheduleSaveWindowState();
        LocationChanged += (_, _) => ScheduleSaveWindowState();
    }

    private DispatcherTimer? _saveDebounce;

    /// <summary>尺寸/位置变化后 500ms 防抖落盘。</summary>
    private void ScheduleSaveWindowState()
    {
        if (_docked || WindowState == WindowState.Minimized) return;
        _saveDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveDebounce.Stop();
        _saveDebounce.Start();
        if (!_saveDebounce.IsEnabled) return;
        // Tick 只挂一次
        _saveDebounce.Tick -= SaveDebounce_Tick;
        _saveDebounce.Tick += SaveDebounce_Tick;
    }

    private void SaveDebounce_Tick(object? sender, EventArgs e)
    {
        _saveDebounce?.Stop();
        SaveWindowState();
    }

    /// <summary>保存窗口位置与尺寸到设置文件。</summary>
    private void SaveWindowState()
    {
        try
        {
            if (WindowState == WindowState.Minimized) return;
            if (_docked) return;   // 贴边收起中，位置在屏幕外，不保存
            _settings.WindowX = Left;
            _settings.WindowY = Top;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
            _settingsStore.Save(_settings);
        }
        catch { /* 保存失败不影响使用 */ }
    }

    /// <summary>窗口失焦（用户拖完/调完尺寸点到别处）时落盘一次。</summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        SaveWindowState();
    }

    // ---------------- 全局热键 ----------------

    private void RegisterHotkeyFromSettings()
    {
        _hotkey = new HotkeyManager(this);
        _hotkey.HotkeyPressed += ToggleWindow;
        if (_hotkey.TryRegister(_settings.Hotkey, out var reason))
        {
            Logging.Info($"全局热键注册成功：{_settings.Hotkey}");
            return;
        }
        Logging.Warn($"全局热键注册失败：{_settings.Hotkey}，原因={reason}");
        TrayIcon.ShowBalloonTip("全局热键注册失败", reason, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning);
    }

    // ---------------- 贴边收起 ----------------

    private enum DockSide { None, Left, Right, Top }

    private DockSide _dockSide = DockSide.None;
    private bool _docked;      // 处于贴边模式（收起或悬停展开）
    private bool _expanded;    // 当前悬停展开
    private Rect _normalRect;  // 收起前窗口位置
    private double _dockPosLeft, _dockPosTop; // 收起位置
    private const double DockEdge = 8;        // 收起时露出的边缘宽度（DIP）
    private const double DockThreshold = 15;  // 拖动靠近边缘的判定阈值（DIP）
    private DispatcherTimer? _dockTimer;      // 贴边模式下的光标位置轮询
    private bool _peekSeen;                   // 收起后鼠标是否离开过露出条（防止松手瞬间误弹）
    private DateTime _leftAt = DateTime.MinValue; // 展开态下鼠标开始离开窗口的时间

    /// <summary>拖动松手后调用：靠近屏幕边缘则吸附收起（仅露出 DockEdge 宽度）。</summary>
    private void MaybeDock()
    {
        _docked = false;
        _expanded = false;
        _dockSide = DockSide.None;
        if (!_settings.EdgeDockEnabled || WindowState == WindowState.Maximized) return;

        var wa = SystemParameters.WorkArea;
        if (Left <= wa.Left + DockThreshold)
        {
            _docked = true;
            _dockSide = DockSide.Left;
            _normalRect = new Rect(Left, Top, Width, Height);
            _dockPosLeft = wa.Left - Width + DockEdge;
            _dockPosTop = Top;
            Left = _dockPosLeft;
        }
        else if (Left + Width >= wa.Right - DockThreshold)
        {
            _docked = true;
            _dockSide = DockSide.Right;
            _normalRect = new Rect(Left, Top, Width, Height);
            _dockPosLeft = wa.Right - DockEdge;
            _dockPosTop = Top;
            Left = _dockPosLeft;
        }
        else if (Top <= wa.Top + DockThreshold)
        {
            _docked = true;
            _dockSide = DockSide.Top;
            _normalRect = new Rect(Left, Top, Width, Height);
            _dockPosLeft = Left;
            _dockPosTop = wa.Top - Height + DockEdge;
            Top = _dockPosTop;
        }

        if (_docked)
        {
            _peekSeen = false;                // 松手后需鼠标离开露出条再回来才展开
            _leftAt = DateTime.MinValue;
            StartDockTimer();
        }
        else
        {
            _dockTimer?.Stop();               // 未贴边：停止轮询
        }
    }

    /// <summary>启动贴边轮询：每 60ms 检查一次光标位置，决定展开/收回。</summary>
    private void StartDockTimer()
    {
        if (_dockTimer == null)
        {
            _dockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _dockTimer.Tick += DockTick;
        }
        _dockTimer.Start();
    }

    private void DockTick(object? sender, EventArgs e)
    {
        if (!_docked)
        {
            _dockTimer?.Stop();
            return;
        }
        NativeMethods.GetCursorPos(out var p);
        // GetCursorPos 返回物理像素，需换算成 DIP 才能与 WPF 的 Left/Top/Width/Height 比较
        var dpi = VisualTreeHelper.GetDpi(this);
        double px = p.X / dpi.DpiScaleX;
        double py = p.Y / dpi.DpiScaleY;
        if (!_expanded)
        {
            // 收起态：光标悬停在露出条附近 → 展开；
            // 但必须"离开过露出条再回来"（_peekSeen），松手瞬间鼠标停在露出条上不会误弹。
            bool overPeek = _dockSide switch
            {
                DockSide.Left  => px <= DockEdge + 4 && py >= _dockPosTop - 4 && py <= _dockPosTop + Height + 4,
                DockSide.Right => px >= _dockPosLeft - 4 && py >= _dockPosTop - 4 && py <= _dockPosTop + Height + 4,
                DockSide.Top   => py <= DockEdge + 4 && px >= _dockPosLeft - 4 && px <= _dockPosLeft + Width + 4,
                _ => false
            };
            if (!overPeek) _peekSeen = true;
            else if (_peekSeen) Expand();
        }
        else
        {
            // 展开态：光标不在窗口（含 ±8 容差）内，连续超过 300ms 才收回，防边界抖动
            bool inWindow = px >= Left - 8 && px <= Left + Width + 8 &&
                            py >= Top - 8 && py <= Top + Height + 8;
            // 下拉框弹出时，把 Popup 实际占据的屏幕矩形也算作窗口内：
            // 否则鼠标移到下拉选项上选择模板时，会被误判为离开窗口，300ms 后误触发 Collapse。
            if (!inWindow && TemplateBox.IsDropDownOpen)
            {
                if (TemplateBox.Template?.FindName("PART_Popup", TemplateBox) is Popup popup
                    && popup.Child is FrameworkElement child && child.IsVisible)
                {
                    var pt = child.PointToScreen(new Point(0, 0));
                    double cpx = pt.X / dpi.DpiScaleX;
                    double cpy = pt.Y / dpi.DpiScaleY;
                    if (px >= cpx - 4 && px <= cpx + child.ActualWidth + 4 &&
                        py >= cpy - 4 && py <= cpy + child.ActualHeight + 4)
                    {
                        inWindow = true;
                    }
                }
            }
            if (!inWindow)
            {
                if (_leftAt == DateTime.MinValue) _leftAt = DateTime.Now;
                else if (DateTime.Now - _leftAt > TimeSpan.FromMilliseconds(300)) Collapse();
            }
            else _leftAt = DateTime.MinValue;
        }
    }

    /// <summary>滑出展开：位置钳制在工作区内，并把光标吸进窗口避免边缘抖动。</summary>
    private void Expand()
    {
        Logging.Debug("贴边展开");
        _expanded = true;
        _leftAt = DateTime.MinValue;
        var wa = SystemParameters.WorkArea;
        Left = Math.Max(wa.Left, Math.Min(_normalRect.Left, wa.Right - Width));
        Top = Math.Max(wa.Top, Math.Min(_normalRect.Top, wa.Bottom - Height));
        NativeMethods.GetCursorPos(out var p);
        var dpi = VisualTreeHelper.GetDpi(this);
        double px = p.X / dpi.DpiScaleX;
        double py = p.Y / dpi.DpiScaleY;
        // 若光标落在窗口外（收起时贴在边缘导致），把它带进窗口标题栏，保证展开后可直接操作
        if (px < Left + 8 || px > Left + Width - 8 || py < Top + 8 || py > Top + Height - 8)
            NativeMethods.SetCursorPos((int)((Left + 100) * dpi.DpiScaleX), (int)((Top + 20) * dpi.DpiScaleY));
    }

    /// <summary>收回贴边：回到收起位置。</summary>
    private void Collapse()
    {
        Logging.Debug("贴边收回");
        _expanded = false;
        _leftAt = DateTime.MinValue;
        // 收回前关闭模板下拉框：窗口滑到屏幕外后，Popup 不会自动跟随隐藏，会残留在桌面上
        if (TemplateBox.IsDropDownOpen)
            TemplateBox.IsDropDownOpen = false;
        Left = _dockPosLeft;
        Top = _dockPosTop;
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _dockTimer?.Stop();
    }

    // ---------------- 置顶 ----------------

    private bool _pinned = true;

    private void PinBtn_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        Topmost = _pinned;
        UpdatePinVisual();
    }

    private void UpdatePinVisual()
    {
        // 置顶态由模板的 Tag="pinned" 触发器渲染（紫色描边），未置顶时 Tag=null
        PinBtn.Tag = _pinned ? "pinned" : null;
    }

    private static Style? FindIconBtnSoftStyle() =>
        Application.Current.TryFindResource("IconBtnSoft") as Style;

    private static Style? FindFieldInputStyle() =>
        Application.Current.TryFindResource("FieldInput") as Style;

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCursorPos(int x, int y);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }
    }

    // ---------------- 模板管理 ----------------

    private void RefreshTemplateBox()
    {
        _loadingTemplates = true;
        _filterActive = false;
        _templatesView = new ListCollectionView(GetDisplayOrder().ToList());
        _templatesView.Filter = null;
        TemplateBox.ItemsSource = _templatesView;
        TemplateBox.DisplayMemberPath = nameof(PromptGenerator.Models.Template.Name);
        if (_data.Templates.Count > 0)
        {
            TemplateBox.SelectedIndex = 0;
            TemplateBox.Text = (TemplateBox.SelectedItem as Template)?.Name ?? "";
        }
        _loadingTemplates = false;
        LoadCurrentTemplate();
    }

    /// <summary>视图层显示顺序：开启"最近使用置顶"时，最近使用的 5 个模板排最前，其余保持存储顺序；不改变 _data.Templates 本身。</summary>
    private IEnumerable<Template> GetDisplayOrder()
    {
        if (!_settings.PinRecentTemplates) return _data.Templates;
        var pinned = _data.Templates
            .Where(t => t.LastUsedAt.HasValue)
            .OrderByDescending(t => t.LastUsedAt)
            .Take(5)
            .ToList();
        var pinnedIds = new HashSet<string>(pinned.Select(t => t.Id));
        return pinned.Concat(_data.Templates.Where(t => !pinnedIds.Contains(t.Id)));
    }

    /// <summary>LastUsedAt 更新后重建显示顺序，保留当前选中项（不重置到首项），过滤谓词同步保留。</summary>
    private void RefreshOrderKeepSelection()
    {
        var sel = TemplateBox.SelectedItem as Template;
        _loadingTemplates = true;
        _templatesView = new ListCollectionView(GetDisplayOrder().ToList());
        _templatesView.Filter = _filterActive
            ? new Predicate<object>(t => (t as Template)?.Name?.IndexOf(TemplateBox.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
            : null;
        TemplateBox.ItemsSource = _templatesView;
        if (sel != null)
        {
            TemplateBox.SelectedItem = sel;
            TemplateBox.Text = sel.Name ?? "";
        }
        _loadingTemplates = false;
    }

    private void TemplateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTemplates) return;
        if (_renaming) return;   // 改名态：编辑文本引发的选中变化不切换模板、不恢复快照、不重渲染字段
        // 过滤态：键盘方向键导航的选中只是"高亮候选"（Enter 确认才切换）；鼠标点击候选项（_mousePick）
        // 是确认选择，放行切换。放行时退出过滤态、回填选中名并收起下拉，下方统一走离开清理 + LoadCurrentTemplate。
        bool pick = _mousePick;
        _mousePick = false;
        if (_filterActive && !pick) return;
        if (_filterActive)
        {
            _filterActive = false;
            _filterResetPending = false;
            _userTyping = false;
            _loadingTemplates = true;
            if (TemplateBox.SelectedItem is Template sel)
            {
                TemplateBox.Text = sel.Name;   // 回填选中模板名（原生已同步；短路使后续 TextChanged 不重入过滤分支）
                TemplateBox.IsDropDownOpen = false;
            }
            _loadingTemplates = false;
        }
        Logging.Debug($"SelChanged sel={(TemplateBox.SelectedItem as Template)?.Name} filter={_filterActive} loading={_loadingTemplates}");
        // 离开当前模板前：
        // - 历史编辑产生的临时模板（未点"保存到模板"）：全部清除。
        //   除当前模板外，也清理列表中残留的其他历史编辑模板——例如：编辑历史A后未切走
        //   又直接编辑历史B，onEdit 中 _loadingTemplates 会屏蔽本次切换的离开清理，
        //   导致 A 残留在列表，需在后续任意一次切换时一并清除。
        // - 有快照（保存过的模板）：从快照恢复，丢弃未保存的编辑
        // - 无快照（新建未保存）：空白则删除，有内容则保留在内存
        if (_current != null)
        {
            var tempToRemove = _data.Templates
                .Where(x => x.IsTemp && !_savedSnapshots.ContainsKey(x.Id))
                .ToList();
            if (tempToRemove.Count > 0)
            {
                foreach (var t in tempToRemove)
                {
                    _data.Templates.Remove(t);
                    // 同步清理"未保存新模板"标记（历史编辑模板不会命中，统一处理更稳妥）
                    if (_pendingNewTemplate == t) _pendingNewTemplate = null;
                }
                try { _store.Save(_data); } catch { }
            }
            // 当前模板若已被上述清理移除（历史编辑模板），跳过快照恢复等后续处理
            if (_current != null && _data.Templates.Contains(_current))
            {
                if (_savedSnapshots.ContainsKey(_current.Id))
                    RestoreFromSnapshot(_current);
                else if (IsBlankNewTemplate(_current))
                {
                    _data.Templates.Remove(_current);
                    // 同步清理"未保存新模板"标记，否则下次点"新增模板"会被误判为仍有未保存模板
                    if (_pendingNewTemplate == _current) _pendingNewTemplate = null;
                    try { _store.Save(_data); } catch { }
                }
            }
        }
        LoadCurrentTemplate();
    }

    /// <summary>空白新模板判定：名字仍是默认"新模板"，且所有字段名/内容都为空。</summary>
    private static bool IsBlankNewTemplate(Template t)
    {
        if (t.Name != "新模板") return false;
        foreach (var f in t.Fields)
        {
            if (!string.IsNullOrWhiteSpace(f.Name)) return false;
            foreach (var it in f.Items)
                if (!string.IsNullOrWhiteSpace(it)) return false;
        }
        return true;
    }

    /// <summary>深拷贝模板快照。</summary>
    private static Template DeepCopy(Template t)
    {
        var c = new Template { Id = t.Id, Name = t.Name };
        foreach (var f in t.Fields)
            c.Fields.Add(new Field { Name = f.Name, Items = new List<string>(f.Items) });
        return c;
    }

    private void SaveSnapshot(Template t) => _savedSnapshots[t.Id] = DeepCopy(t);

 /// <summary>
    /// 非"保存到模板"场景（生成、历史删除）落盘：只写入最近一次显式保存的状态。
    /// 当前模板若有未保存的编辑，写盘时临时用快照内容替换，写完立即还原——
    /// 既不把未保存的编辑落盘，也不影响用户继续编辑。
    /// </summary>
    private void PersistData()
    {
        if (_current == null || !_savedSnapshots.TryGetValue(_current.Id, out var snap))
        {
            _store.Save(_data);
            return;
        }
        string liveName = _current.Name;
        var liveFields = _current.Fields;   // 实时编辑用的列表（UI 闭包持有其 Field 对象）
        _loadingTemplates = true;           // 避免临时改名触发下拉框文本回写
        try
        {
            _current.Name = snap.Name;
            _current.Fields = snap.Fields;
            _store.Save(_data);
        }
        finally
        {
            _current.Name = liveName;
            _current.Fields = liveFields;
            _loadingTemplates = false;
        }
    }
 
    private void RestoreFromSnapshot(Template t)
    {
        if (!_savedSnapshots.TryGetValue(t.Id, out var snap)) return;
        t.Name = snap.Name;
        t.Fields.Clear();
        foreach (var f in snap.Fields)
            t.Fields.Add(new Field { Name = f.Name, Items = new List<string>(f.Items) });
    }

    /// <summary>可编辑下拉框：输入文本即时按模板名过滤（不区分大小写的包含匹配）；改名输入（双击态）不触发过滤。</summary>
    private void TemplateBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingTemplates || _current == null) return;
        if (_renaming) return;
        string text = TemplateBox.Text.Trim();
        bool equalsCurrent = string.Equals(text, _current.Name?.Trim(), StringComparison.Ordinal);
        Logging.Debug($"TextChanged text={TemplateBox.Text} filter={_filterActive} renaming={_renaming} equals={equalsCurrent} focus={Keyboard.FocusedElement?.GetType().Name}");
        if (string.IsNullOrEmpty(text) || equalsCurrent)
        {
            // 回写免疫：视图 Refresh() 后 ComboBox 异步把 Text 回写为当前模板名（日志实证，回写时选中项非空），
            // 该文本并非用户本次输入 → 恢复过滤词并保持过滤态。
            // 恢复必须用 SetCurrentValue（与用户键入同路径，ComboBox 视为编辑态不校正）；
            // SetValue/TextBox 直设都会触发 ComboBox 的一致性校正（清空或回写，日志实证）。
            if (_filterActive && _filterResetPending && equalsCurrent
                && !string.Equals(text, _filterResetText, StringComparison.Ordinal))
            {
                Logging.Debug($"IgnoreAsyncReset text={TemplateBox.Text} restore={_filterResetText}");
                _filterResetPending = false;
                _loadingTemplates = true;
                TemplateBox.SetCurrentValue(ComboBox.TextProperty, _filterResetText);
                _loadingTemplates = false;
                return;
            }
            // 用户删除/清空导致空文本：进入编辑态（置 null）防止 ComboBox 把空文本回写为选中项名（日志实证）
            if (string.IsNullOrEmpty(text))
            {
                _userTyping = false;
                _filterResetPending = false;
                if (TemplateBox.SelectedItem != null)
                {
                    _loadingTemplates = true;
                    TemplateBox.SelectedItem = null;
                    _loadingTemplates = false;
                }
                if (_filterActive)
                {
                    _filterActive = false;
                    RefreshFilteredList();
                }
                return;
            }
            // 空文本或恰好等于当前模板名：视为回到选中态，恢复全量列表
            if (_filterActive)
            {
                _filterActive = false;
                RefreshFilteredList();
            }
            return;
        }
        // 用户输入过滤词：记录过滤词与免疫现场，展开下拉显示过滤结果。
        // 仅当文本变化源于用户键入（_userTyping）才进入编辑态（置 null + SetCurrentValue 恢复过滤词）：
        // 置 null 的 UpdateText 会清空文本（日志实证），需 SetCurrentValue（与键入同路径）恢复；
        // 方向键导航（_userTyping=false）保留 SelectedItem，维持候选项高亮与 Enter 确认语义。
        _filterResetPending = true;
        _filterResetText = text;
        _filterActive = true;
        if (!TemplateBox.IsDropDownOpen) TemplateBox.IsDropDownOpen = true;
        if (_userTyping)
        {
            _userTyping = false;
            if (TemplateBox.SelectedItem != null)
            {
                _loadingTemplates = true;
                TemplateBox.SelectedItem = null;
                _loadingTemplates = false;
            }
            _loadingTemplates = true;
            TemplateBox.SetCurrentValue(ComboBox.TextProperty, text);
            _loadingTemplates = false;
        }
        RefreshFilteredList();
    }

    /// <summary>字符输入前（隧道阶段）先进入过滤态并展开下拉，使随后由视图/ComboBox 触发的选中变化被过滤态屏蔽，避免输入即自动切换模板。</summary>
    private void TemplateBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_renaming || _current == null) return;
        _filterResetPending = false;   // 新一轮真实输入：异步回写免疫失效，由本次输入接管
        _userTyping = true;            // 标记本次文本变化来自用户键入（TextChanged 过滤分支消费）
        Logging.Debug($"PreviewTextInput focus={Keyboard.FocusedElement?.GetType().Name} text={TemplateBox.Text} filter={_filterActive}");
        _filterActive = true;
        if (!TemplateBox.IsDropDownOpen) TemplateBox.IsDropDownOpen = true;
    }

    /// <summary>按当前过滤词切换视图过滤：置顶顺序（GetDisplayOrder）基础上按模板名包含过滤；只改 Filter 谓词，不重建 ItemsSource。
    /// 不复位 SelectedItem/Text：用户输入经免疫恢复走编辑框路径（编辑态），ComboBox 不校正；
    /// 选中项保持原值，方向键在过滤视图中正常导航，Enter 由命中校验把关。</summary>
    private void RefreshFilteredList()
    {
        if (_templatesView == null) return;
        string keepText = TemplateBox.Text;
        Logging.Debug($"Refresh keep={keepText} filter={_filterActive} sel={(TemplateBox.SelectedItem as Template)?.Name}");
        _templatesView.Filter = _filterActive
            ? new Predicate<object>(t => (t as Template)?.Name?.IndexOf(TemplateBox.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
            : null;
        // Refresh() 会触发 ComboBox 内部把 Text 同步回写为当前选中项名（日志实证），该回写的 TextChanged
        // 会被误判"回到选中态"而取消过滤；用 _loadingTemplates 包住，使该次回写短路，保持过滤态与用户输入。
        _loadingTemplates = true;
        _templatesView.Refresh();
        _loadingTemplates = false;
        Logging.Debug($"RefreshEnd keep={keepText} filter={_filterActive} text={TemplateBox.Text} sel={(TemplateBox.SelectedItem as Template)?.Name}");
    }

    /// <summary>清空过滤：回填当前模板名并恢复全量列表（Esc 第一级）。</summary>
    private void ClearFilter()
    {
        if (!_filterActive) return;
        _userTyping = false;          // 离开过滤态：编辑标记失效
        _filterResetPending = false;   // 离开过滤态：免疫现场失效
        _filterActive = false;
        _loadingTemplates = true;
        TemplateBox.Text = _current?.Name ?? "";
        _loadingTemplates = false;
        RefreshFilteredList();
        // 过滤态方向键可能已选中候选：Esc 后恢复选中指向当前模板
        if (_current != null && TemplateBox.SelectedItem != _current)
        {
            _loadingTemplates = true;
            TemplateBox.SelectedItem = _current;
            _loadingTemplates = false;
        }
    }

    /// <summary>文本/箭头分区点击：箭头区=切换列表；文本区=全选+输入。改名态点击箭头：忽略并提示先退出编辑。
    /// 拦截必须在此隧道阶段完成——点击箭头时焦点会移向 ComboBox 触发失焦确认（TryConfirmRename 退出改名态），
    /// 若等冒泡阶段再拦，改名态已被确认、列表已弹出。</summary>
    private void TemplateBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        bool onToggle = IsHitOnToggle(e.OriginalSource as DependencyObject);
        Logging.Debug($"MouseDown onToggle={onToggle} focus={Keyboard.FocusedElement?.GetType().Name} text={TemplateBox.Text}");
        if (_renaming && onToggle)
        {
            e.Handled = true;   // 阻止焦点转移与列表切换
            ShowToast("请先退出编辑操作，按Enter（保存）或ESC（取消）", false);
            return;
        }
        if (e.ClickCount == 1 && !onToggle)
        {
            if (TemplateBox.Template?.FindName("PART_EditableTextBox", TemplateBox) is TextBox tb)
                tb.SelectAll();
        }
    }

    /// <summary>命中测试：点击点是否落在下拉箭头区（模板内 ToggleButton 及其子元素上）。</summary>
    private static bool IsHitOnToggle(DependencyObject? source)
    {
        for (DependencyObject? d = source; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is ToggleButton) return true;
        return false;
    }

    /// <summary>命中测试：点击点是否落在下拉列表项（Popup 内 ComboBoxItem）上——点击候选项=确认选择。</summary>
    private static bool IsHitOnComboBoxItem(DependencyObject? source)
    {
        for (DependencyObject? d = source; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is ComboBoxItem) return true;
        return false;
    }

    /// <summary>窗口级隧道点击检测：点击落在下拉候选项上时置位 _mousePick，
    /// 使 SelectionChanged 放行（点击=确认选择，切换模板）；键盘方向键导航不经此路径。</summary>
    private void MainWindow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mousePick = IsHitOnComboBoxItem(e.OriginalSource as DependencyObject);
    }

    /// <summary>重命名按钮：进入改名态——收起下拉、文本预填当前模板名并全选、聚焦编辑框，键入即覆盖。
    /// 注意：焦点必须留在编辑框内（tb.Focus），不能再 Keyboard.Focus 回 ComboBox——那会触发失焦确认立即退出改名态。</summary>
    private void RenameTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        _renaming = true;
        TemplateBox.IsDropDownOpen = false;
        TemplateBox.Text = _current.Name ?? "";
        if (TemplateBox.Template?.FindName("PART_EditableTextBox", TemplateBox) is TextBox tb)
        {
            tb.Focus();
            tb.SelectAll();
        }
    }

    /// <summary>失焦：改名态→确认改名（焦点已移走，无需再安排落点）；
    /// 过滤态/文本与当前模板不一致且焦点离开模板框（点外部控件/键盘 Tab）→恢复显示当前模板名并取消过滤。</summary>
    private void TemplateBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Logging.Debug($"LostFocus renaming={_renaming} newFocus={e.NewFocus?.GetType().Name}");
        if (_renaming)
        {
            TryConfirmRename();
            return;
        }
        // 焦点离开模板框（到外部控件）：过滤词/空文本不再需要，恢复当前模板名（用户预期：清空后失焦恢复）
        if (!IsWithinTemplateBox(e.NewFocus as DependencyObject)
            && (_filterActive || !string.Equals(TemplateBox.Text?.Trim(), _current?.Name?.Trim(), StringComparison.Ordinal)))
        {
            _userTyping = false;
            _filterResetPending = false;
            _filterActive = false;
            _loadingTemplates = true;
            TemplateBox.Text = _current?.Name ?? "";
            if (TemplateBox.SelectedItem != _current)
                TemplateBox.SelectedItem = _current;
            _loadingTemplates = false;
            RefreshFilteredList();
        }
    }

    /// <summary>判断焦点目标是否仍在模板框（或其模板内子元素）内：点箭头/文本区等内部操作不触发失焦恢复。</summary>
    private bool IsWithinTemplateBox(DependencyObject? node)
    {
        for (DependencyObject? d = node; d != null; d = VisualTreeHelper.GetParent(d))
            if (d == TemplateBox) return true;
        return false;
    }

    /// <summary>确认改名：非空、不重名才写回并退出改名态；空名还原退出；重名提示并保持改名态。</summary>
    private bool TryConfirmRename()
    {
        if (!_renaming) return false;
        string newName = TemplateBox.Text.Trim();
        if (string.IsNullOrEmpty(newName))
        {
            TemplateBox.Text = _current?.Name ?? "";   // 空名：还原退出（视同取消）
            _renaming = false;
            return true;
        }
        var dup = _data.Templates.FirstOrDefault(t =>
            t != _current && string.Equals(t.Name?.Trim(), newName, StringComparison.Ordinal));
        if (dup != null)
        {
            ShowToast($"模板名「{newName}」已存在", false);
            return false;   // 保持改名态，用户可继续修改
        }
        if (_current != null && _current.Name != newName)
        {
            _current.Name = newName;
            SaveSnapshot(_current);   // 同步快照，防止切走模板时被旧快照恢复覆盖改名
            try { PersistData(); } catch { }
        }
        _renaming = false;
        // 改名输入可能已使选中项与文本脱节：恢复选中指向当前模板，保证下拉展开显示正常
        if (TemplateBox.SelectedItem != _current)
        {
            _loadingTemplates = true;
            TemplateBox.SelectedItem = _current;
            _loadingTemplates = false;
        }
        return true;
    }

    /// <summary>取消改名：恢复原名并退出改名态。</summary>
    private void CancelRename()
    {
        if (!_renaming) return;
        _renaming = false;
        TemplateBox.Text = _current?.Name ?? "";
    }

    private void LoadCurrentTemplate()
    {
        _current = TemplateBox.SelectedItem as Template;
        RenderFields();
    }

    private void NewTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingNewTemplate != null)
        {
            ShowToast("当前存在未保存的模板，请保存后再新增", false);
            return;
        }
        var t = new Template { Name = "新模板" };
        t.Fields.Add(new Field { Name = "", Items = { "" } });
        _data.Templates.Add(t);
        _loadingTemplates = true;
        TemplateBox.SelectedItem = t;
        _loadingTemplates = false;
        _current = t;
        _pendingNewTemplate = t;
        RenderFields();
        // 新模板首字段名默认为空，直接聚焦字段名框并全选，便于立即输入
        FocusFieldName(0);
        Logging.Info($"新增模板：id={t.Id}");
        ShowToast("已创建新模板");
    }

    private void DeleteTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var r = MessageBox.Show($"删除模板「{_current.Name}」？", "删除模板",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        // 保存本地引用：_data.Templates.Remove 会同步触发 SelectionChanged → LoadCurrentTemplate
        // → 把 _current 置 null，后面再访问 _current.Id 会 NRE。
        var current = _current;
        _data.Templates.Remove(current);
        _savedSnapshots.Remove(current.Id);
        if (_pendingNewTemplate == current) _pendingNewTemplate = null;   // 未保存的新模板被删除
        _current = null;
        try { _store.Save(_data); } catch { }
        RefreshTemplateBox();
        TemplateBox.Focus();
        Logging.Info($"删除模板：{current.Name}");
        ShowToast("已删除模板");
    }

    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        // 同步 ComboBox 文本到模板名
        _current.Name = TemplateBox.Text?.Trim() ?? "";
        // 模板名重复校验
        var dup = _data.Templates.FirstOrDefault(t =>
            t != _current && string.Equals(t.Name?.Trim(), _current.Name.Trim(), StringComparison.Ordinal));
        if (dup != null)
        {
            ShowToast($"模板名「{_current.Name}」已存在", false);
            return;
        }
        if (UpdateDup())
        {
            ShowToast("字段名重复，无法保存", false);
            return;
        }
        try
        {
            _store.Save(_data);
            SaveSnapshot(_current);   // 更新已保存快照
            _pendingNewTemplate = null;   // 已保存，允许再次新增
            Logging.Info($"保存模板：{_current.Name}");
            ShowToast("已保存到模板");
        }
        catch (Exception ex)
        {
            Logging.Error($"保存模板失败：{_current.Name}，原因={ex.Message}");
            ShowToast($"保存失败：{ex.Message}", false);
        }
    }

    // ---------------- 字段渲染 ----------------

    private void RenderFields()
    {
        _suppressReclaim = true;
        FieldHost.Children.Clear();
        var fields = _current?.Fields ?? new List<Field>();
        for (int i = 0; i < fields.Count; i++)
        {
            FieldHost.Children.Add(RenderRow(i, fields[i]));
            FieldHost.Children.Add(MakeRowGap(i + 1));
        }
        _suppressReclaim = false;
        UpdateDup();
        // 布局完成后同步重算所有内容框高度（同步而非异步，避免重建后先 32px 再长高的闪烁）
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateLayout();
            foreach (var tb in FindVisualChildren<AutoGrowTextBox>(this))
            {
                tb.SyncResize();
            }
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
        }
    }

    private RowGap MakeRowGap(int insertIndex)
    {
        var gap = new RowGap();
        gap.AddClicked += (_, _) =>
        {
            if (_current == null) return;
            AddFieldAt(insertIndex);
        };
        return gap;
    }

    /// <summary>在指定位置增量插入一个字段行，不重建其他字段。</summary>
    private void AddFieldAt(int rowIndex)
    {
        if (_current == null) return;
        var f = new Field { Name = "", Items = { "" } };
        _current.Fields.Insert(rowIndex, f);
        // 字段行在 FieldHost.Children 中位于 2*rowIndex（后面跟一个 RowGap）
        var newRow = RenderRow(rowIndex, f);
        var newGap = MakeRowGap(rowIndex + 1);
        int pos = 2 * rowIndex;
        FieldHost.Children.Insert(pos, newGap);
        FieldHost.Children.Insert(pos, newRow);
        FocusFieldName(rowIndex);
    }

    /// <summary>
    /// 增量移除指定位置的字段行及其 RowGap。
    /// 仅剩一个字段时不删除：清空字段名与全部内容框，聚焦字段框。
    /// 删除后焦点：删非首字段 → 上一字段末条段尾；删首字段 → 下一字段首条段尾。
    /// </summary>
    private void RemoveFieldAt(int rowIndex)
    {
        if (_current == null) return;
        if (_current.Fields.Count == 1)
        {
            // 仅剩一个字段：清空字段名 + 全部内容框，聚焦字段框（重建 UI 同步全部文本）
            var f = _current.Fields[0];
            f.Name = "";
            for (int j = 0; j < f.Items.Count; j++) f.Items[j] = "";
            RenderFields();
            FocusFieldName(0);
            ShowToast("仅剩一个字段，已清空字段名与内容");
            return;
        }
        _current.Fields.RemoveAt(rowIndex);
        int pos = 2 * rowIndex;
        if (pos < FieldHost.Children.Count) FieldHost.Children.RemoveAt(pos); // RowGap
        if (pos < FieldHost.Children.Count) FieldHost.Children.RemoveAt(pos); // Row
        UpdateDup();
        if (rowIndex == 0)
            FocusItemInput(0, 0, caretToEnd: true);   // 删首字段 → 下一字段首条段尾
        else
            FocusItemInput(rowIndex - 1, _current.Fields[rowIndex - 1].Items.Count - 1, caretToEnd: true); // 上一字段末条段尾
    }

    /// <summary>线性小图标（viewBox 16）：× / ＋，颜色跟随按钮 Foreground（Muted → hover AccentHover）。</summary>
    private static System.Windows.Shapes.Path MakeGlyph(string data)
    {
        var p = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 12,
            Height = 12,
            Stretch = Stretch.None,
            IsHitTestVisible = false
        };
        return p;
    }

    // 数据坐标以 1~11 中心对称：Stretch=None 时布局盒取几何 bounds 最大值，
    // 用 [1,11] 使视觉中心与按钮（16×16）中心重合，线条完整显示不贴边。
    private const string GlyphX = "M 1,1 L 11,11 M 11,1 L 1,11";
    private const string GlyphPlus = "M 6,1 L 6,11 M 1,6 L 11,6";

    private static void BindStrokeToForeground(System.Windows.Shapes.Path p, Button btn) =>
        p.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
            new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = btn });

    private UIElement RenderRow(int rowIndex, Field field)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 名头行
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 内容框列表

        // 动态读取本行在 FieldHost.Children 中的实际位置（字段行在偶数索引）
        Func<int> getRowIndex = () =>
        {
            int idx = FieldHost.Children.IndexOf(grid);
            return idx >= 0 ? idx / 2 : rowIndex;
        };

        // 名头行：[*名头 | 20px ×列]，高 24 + 4px 缝（缝归名头 hover 区）
        // Background=Transparent：让名头整个矩形可命中，鼠标经缝/空隙移向 × 时不脱离 head 子树，
        // 避免 ghost MouseLeave 把 × 隐藏后无法恢复（根治 hover 时 × 不可选取/点击）。
        var head = new Grid { Height = 28, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });

        // slim 名头：无描边、透明底、紫字、左对齐，右缘=内容框右缘（第 1 列 20px 留给 ×）
        var nameBox = new TextBox
        {
            Height = 24,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x7c, 0x6c, 0xf0)),
            CaretBrush = new SolidColorBrush(Color.FromRgb(0x7c, 0x6c, 0xf0)),
            SelectionBrush = new SolidColorBrush(Color.FromRgb(0x7c, 0x6c, 0xf0)),
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 0, 4, 0),
            Text = field.Name
        };
        WatermarkService.SetWatermark(nameBox, "标题，如“要求”");
        nameBox.TextChanged += (_, _) =>
        {
            field.Name = nameBox.Text;
            UpdateDup();
        };

        // 字段删除 ×：名头行最右端，hover 名头（含 4px 缝）显示；与字段框垂直居中对齐
        var delRow = new Button
        {
            Style = FindIconBtnSoftStyle(),
            ToolTip = "删除该字段",
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 0),
            Opacity = 0,
            IsHitTestVisible = false
        };
        var delRowIcon = MakeGlyph(GlyphX);
        BindStrokeToForeground(delRowIcon, delRow);
        delRow.Content = delRowIcon;
        delRow.Click += (_, _) =>
        {
            if (_current == null) return;
            RemoveFieldAt(getRowIndex());
        };

        head.Children.Add(nameBox);
        Grid.SetColumn(nameBox, 0);
        head.Children.Add(delRow);
        Grid.SetColumn(delRow, 1);
        head.MouseEnter += (_, _) => { delRow.Opacity = 1; delRow.IsHitTestVisible = true; };
        head.MouseLeave += (_, _) => { delRow.Opacity = 0; delRow.IsHitTestVisible = false; };

        // 右列：子项列表
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int j = 0; j < field.Items.Count; j++)
        {
            right.Children.Add(RenderItem(rowIndex, field, j));
        }

        grid.Children.Add(head);
        Grid.SetRow(head, 0);
        grid.Children.Add(right);
        Grid.SetRow(right, 1);
        return grid;
    }

    private UIElement RenderItem(int rowIndex, Field field, int itemIndex)
    {
        var item = new Grid {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 6),
            // Background=Transparent：让本行整个矩形可命中。否则鼠标从内容框移向 ×
            // 经过按钮两侧 4px 空隙时命中测试脱离 item 子树，触发 ghost MouseLeave
            // 把 × 隐藏后无法恢复（× 侧无兜底元素，+ 侧有编号 TextBlock 故可恢复）。
            Background = Brushes.Transparent
        };
        // 编号列 34px（预留两位数"10."），文本框自适应，× 列 20px
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });

        // 动态读取本行在 right.Children 中的实际位置；增量插入/删除后旧闭包自动得到正确索引
        Func<int> getIndex = () =>
            item.Parent is StackPanel right ? right.Children.IndexOf(item) : itemIndex;
        // 动态读取本行所属字段在 FieldHost.Children 中的实际行号；
        // 字段级增量插入后旧闭包的 rowIndex 会错位，必须通过视觉树反查。
        Func<int> getRowIndex = () =>
            item.Parent is StackPanel right && right.Parent is Grid rowGrid
                ? FieldHost.Children.IndexOf(rowGrid) / 2
                : rowIndex;

        var num = new TextBlock
        {
            Text = (itemIndex + 1) + ".",
            Foreground = new SolidColorBrush(Color.FromRgb(0x7c, 0x6c, 0xf0)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right
        };

        // 内容框插入 ＋：编号左侧（不与编号重叠）、垂直居中；hover 该框与 × 成对显示
        var addItem = new Button
        {
            Style = FindIconBtnSoftStyle(),
            ToolTip = "在此后插入一条",
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0),
            Opacity = 0,
            IsHitTestVisible = false
        };
        var addIcon = MakeGlyph(GlyphPlus);
        BindStrokeToForeground(addIcon, addItem);
        addItem.Content = addIcon;
        addItem.Click += (_, _) =>
        {
            if (_current == null) return;
            InsertItem(getRowIndex(), getIndex() + 1);   // 在该框之后插入（=鼠标化 Enter）
        };

        var ta = new AutoGrowTextBox { Text = field.Items[itemIndex] };
        ta.Style = FindFieldInputStyle();
        ta.Padding = new Thickness(4, 3, 4, 3);
        ta.HorizontalAlignment = HorizontalAlignment.Stretch;
        WatermarkService.SetWatermark(ta, "正文，回车加条，Shift+Enter换行");
        ta.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                // 空框内按 Enter 不做任何操作：避免连续产生多个空框，
                // 也避免 LostKeyboardFocus 回收后 _fresh 索引错位。
                if (string.IsNullOrWhiteSpace(ta.Text))
                {
                    e.Handled = true;
                    return;
                }
                e.Handled = true;
                InsertItem(getRowIndex(), getIndex() + 1);
            }
        };
        ta.TextChanged += (_, _) => field.Items[getIndex()] = ta.Text;
        ta.LostKeyboardFocus += (_, _) =>
        {
            if (_suppressReclaim) return;
            int idx = getIndex();
            int ri = getRowIndex();
            // 仅回收"刚插入且未输入"的空子项，避免打断正常编辑
            if (_fresh.Contains((ri, idx))
                && string.IsNullOrWhiteSpace(ta.Text)
                && field.Items.Count > 1)
            {
                _fresh.Remove((ri, idx));
                field.Items.RemoveAt(idx);
                RemoveItemUi(ri, idx);
            }
        };
        // 聚焦到有内容的框时，回收当前字段所有残留的"刚插入未输入"空框；
        // 直接移除对应 UI 行，不重建，焦点保持在用户点击的框上。
        ta.GotKeyboardFocus += (_, _) =>
        {
            if (_suppressReclaim) return;
            if (string.IsNullOrWhiteSpace(ta.Text)) return;
            if (_fresh.Count == 0) return;
            if (ta.Parent is not Grid itemGrid || itemGrid.Parent is not StackPanel right) return;
            int ri = getRowIndex();
            var stale = _fresh.Where(k => k.Item1 == ri
                && k.Item2 < field.Items.Count
                && string.IsNullOrWhiteSpace(field.Items[k.Item2])).ToList();
            if (stale.Count == 0) return;
            foreach (var k in stale.OrderByDescending(k => k.Item2))
            {
                _fresh.Remove(k);
                field.Items.RemoveAt(k.Item2);
                if (k.Item2 < right.Children.Count) right.Children.RemoveAt(k.Item2);
            }
            RenumberItems(right);
        };

        // 内容框 ×：该行最右端、垂直居中；hover 该框显示（与 + 成对）
        var delItem = new Button
        {
            Style = FindIconBtnSoftStyle(),
            ToolTip = "删除此项",
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0),
            Opacity = 0,
            IsHitTestVisible = false
        };
        var delIcon = MakeGlyph(GlyphX);
        BindStrokeToForeground(delIcon, delItem);
        delItem.Content = delIcon;
        delItem.Click += (_, _) =>
        {
            int idx = getIndex();
            int ri = getRowIndex();
            if (field.Items.Count == 1)
            {
                // 仅一条：清空文本，并把光标聚焦到被清空的这个框
                field.Items[0] = "";
                ta.Text = "";
                ta.Focus();
                Keyboard.Focus(ta);
            }
            else
            {
                // 多条：删除该框后，删的是第一条则聚焦新的第一条（原下一条），否则聚焦上一条
                field.Items.RemoveAt(idx);
                RemoveItemUi(ri, idx);
                FocusItemInput(ri, idx == 0 ? 0 : idx - 1, caretToEnd: true);
            }
        };

        item.Children.Add(num);
        Grid.SetColumn(num, 0);
        item.Children.Add(addItem);
        Grid.SetColumn(addItem, 0);
        item.Children.Add(ta);
        Grid.SetColumn(ta, 1);
        item.Children.Add(delItem);
        Grid.SetColumn(delItem, 2);
        // hover 该内容框：＋ 与 × 成对显示（opacity 渐变，占位不跳动）
        item.MouseEnter += (_, _) =>
        {
            addItem.Opacity = 1; addItem.IsHitTestVisible = true;
            delItem.Opacity = 1; delItem.IsHitTestVisible = true;
        };
        item.MouseLeave += (_, _) =>
        {
            addItem.Opacity = 0; addItem.IsHitTestVisible = false;
            delItem.Opacity = 0; delItem.IsHitTestVisible = false;
        };
        return item;
    }

    /// <summary>在指定位置增量插入一个空内容框 UI，不重建其他行。</summary>
    private void InsertItem(int rowIndex, int itemIndex)
    {
        if (_current == null) return;
        var f = _current.Fields[rowIndex];
        f.Items.Insert(itemIndex, "");
        // 调整 _fresh：原 >= itemIndex 的记录索引 +1（因为后续行整体后移）
        var freshList = _fresh.ToList();
        _fresh.Clear();
        foreach (var k in freshList)
        {
            _fresh.Add(k.Item1 == rowIndex && k.Item2 >= itemIndex ? (k.Item1, k.Item2 + 1) : k);
        }
        _fresh.Add((rowIndex, itemIndex));

        var rowGrid = FieldHost.Children.OfType<Grid>().ElementAt(rowIndex);
        var right = rowGrid.Children.OfType<StackPanel>().FirstOrDefault();
        if (right == null) return;
        var newItem = RenderItem(rowIndex, f, itemIndex);
        right.Children.Insert(itemIndex, newItem);
        RenumberItems(right);

        FocusItemInput(rowIndex, itemIndex);
    }

    /// <summary>增量移除指定位置的内容框 UI（不重建其他行）。</summary>
    private void RemoveItemUi(int rowIndex, int itemIndex)
    {
        var rowGrid = FieldHost.Children.OfType<Grid>().ElementAt(rowIndex);
        var right = rowGrid.Children.OfType<StackPanel>().FirstOrDefault();
        if (right == null) return;
        if (itemIndex < right.Children.Count) right.Children.RemoveAt(itemIndex);
        RenumberItems(right);
    }

    /// <summary>重新编号 right.Children 中各内容框的序号。</summary>
    private static void RenumberItems(StackPanel right)
    {
        for (int j = 0; j < right.Children.Count; j++)
        {
            if (right.Children[j] is Grid ig && ig.Children.Count > 0 && ig.Children[0] is TextBlock tb)
                tb.Text = (j + 1) + ".";
        }
    }

    // ---------------- 字段名重复检测 ----------------

    /// <summary>字段名重复检测：重复的字段名以红字提示（名头常态为紫字、无描边）。</summary>
    private bool UpdateDup()
    {
        bool dup = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var dupSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tb in EnumerateFieldNameBoxes())
        {
            var v = tb.Text.Trim();
            if (v.Length == 0) continue;
            if (!seen.Add(v)) dupSet.Add(v);
        }
        var dupBrush = new SolidColorBrush(Color.FromRgb(0xe5, 0x53, 0x4b));
        var normalBrush = new SolidColorBrush(Color.FromRgb(0x7c, 0x6c, 0xf0));
        foreach (var tb in EnumerateFieldNameBoxes())
        {
            bool isDup = dupSet.Contains(tb.Text.Trim());
            tb.Foreground = isDup ? dupBrush : normalBrush;
            if (isDup) dup = true;
        }
        return dup;
    }

    private IEnumerable<TextBox> EnumerateFieldNameBoxes()
    {
        var result = new List<TextBox>();
        foreach (var child in FieldHost.Children)
        {
            if (child is Grid row && row.Children.Count > 0 && row.Children[0] is Grid head)
            {
                var tb = head.Children.OfType<TextBox>().FirstOrDefault();
                if (tb != null) result.Add(tb);
            }
        }
        return result;
    }

    // ---------------- 键盘全局快捷键 ----------------

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 过滤态下 Backspace/Delete：标记为用户编辑（进入编辑态置 null，防止回写选中项名）；
        // 方向键不动标志（导航保留选中高亮）。
        if (_filterActive && !_renaming && (e.Key == Key.Back || e.Key == Key.Delete))
            _userTyping = true;
        // ESC：改名态→取消改名（焦点回模板框）；过滤态→先清过滤恢复全列表，再按收起；下拉展开→交还下拉收起；其余→关闭窗口
        if (e.Key == Key.Escape)
        {
            if (_renaming)
            {
                CancelRename();
                TemplateBox.Focus();
                e.Handled = true;
                return;
            }
            if (_filterActive)
            {
                ClearFilter();   // 第一级：恢复全量列表，第二次 Esc 再走收起/关窗
                e.Handled = true;
                return;
            }
            if (TemplateBox.IsDropDownOpen) return;
            e.Handled = true;
            Close();
            return;
        }
        // 改名态：拦截所有"打开列表"的入口（箭头已拦，F4/Alt+Down 一并拦），提示先退出编辑
        if (_renaming && (e.Key == Key.F4 || (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.Alt)))
        {
            e.Handled = true;
            ShowToast("请先退出编辑操作，按Enter（保存）或ESC（取消）", false);
            return;
        }
        // Ctrl+Enter：生成并复制
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OnActionExecuted(this, ActionMode.Copy);
            return;
        }
        // Ctrl+S：保存到模板（复用按钮逻辑，含重名校验、落盘与 Toast）
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SaveTemplate_Click(this, new RoutedEventArgs());
            return;
        }
        // 过滤态 + 下拉展开：接管方向键导航。编辑态 SelectedItem=null 时 ComboBox 原生键盘导航被 TextBox
        // 吞掉（↓ 无响应）；或导航文本同步被免疫误判恢复为过滤词。此处显式在过滤视图中移动选中，
        // _loadingTemplates 短路 TextChanged/SelectionChanged，免疫不介入；保持候选项高亮与 Enter 确认语义。
        if (_filterActive && !_renaming && TemplateBox.IsDropDownOpen && (e.Key == Key.Down || e.Key == Key.Up))
        {
            int count = _templatesView?.Cast<object>().Count() ?? 0;
            if (count == 0) { e.Handled = true; return; }
            int idx = TemplateBox.SelectedIndex;   // 过滤视图内索引（Items=过滤后的视图）
            int next = e.Key == Key.Down
                ? (idx < 0 ? 0 : (idx + 1) % count)
                : (idx < 0 ? count - 1 : (idx - 1 + count) % count);
            _loadingTemplates = true;
            TemplateBox.SelectedIndex = next;
            _loadingTemplates = false;
            e.Handled = true;
            return;
        }
        // 模板下拉展开时方向键循环导航：末项按下键回到首项，首项按上键跳到末项
        if (TemplateBox.IsDropDownOpen && TemplateBox.Items.Count > 0)
        {
            if (e.Key == Key.Down && TemplateBox.SelectedIndex >= TemplateBox.Items.Count - 1)
            {
                TemplateBox.SelectedIndex = 0;
                e.Handled = true;
            }
            else if (e.Key == Key.Up && TemplateBox.SelectedIndex <= 0)
            {
                TemplateBox.SelectedIndex = TemplateBox.Items.Count - 1;
                e.Handled = true;
            }
        }
        // Enter：改名态→确认改名并焦点到第一个字段名框；展开态→选中当前项、回填文本、收起、焦点到第一个内容框；过滤无匹配→不动作
        if (e.Key == Key.Enter)
        {
            if (_renaming)
            {
                e.Handled = true;
                if (TryConfirmRename()) FocusFieldName(0);   // 改名确认后进入该模板第一个字段框
                return;   // 重名时留在改名态
            }
            if (TemplateBox.IsDropDownOpen)
            {
                if (_filterActive)
                {
                    // 过滤命中校验：选中项为空或不在过滤结果内 → 视为无匹配
                    var filteredSel = TemplateBox.SelectedItem as Template;
                    bool hit = filteredSel != null
                        && filteredSel.Name?.IndexOf(TemplateBox.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit)
                    {
                        e.Handled = true;   // 无匹配：保持过滤词，不动作
                        return;
                    }
                }
                TemplateBox.IsDropDownOpen = false;
                if (_filterActive && TemplateBox.SelectedItem is Template sel)
                {
                    // 过滤态确认：切换模板由本处显式完成（过滤态 SelectionChanged 已屏蔽自动切换）
                    _filterActive = false;
                    _loadingTemplates = true;
                    TemplateBox.Text = sel.Name;   // 回填选中模板名
                    _loadingTemplates = false;
                    RefreshFilteredList();          // 恢复全量列表（含置顶顺序）
                    _current = sel;
                    RenderFields();
                    FocusFirstInput();
                    return;
                }
                FocusFirstInput();
            }
        }
    }

    // ---------------- 生成与执行 ----------------

    private async void OnActionExecuted(object? sender, ActionMode mode)
    {
        if (UpdateDup())
        {
            ShowToast("字段名重复，请修改后再生成", false);
            return;
        }
        var prompt = PromptBuilder.Build(_current?.Fields ?? new List<Field>());
        if (string.IsNullOrEmpty(prompt))
        {
            ShowToast("内容为空，无法生成", false);
            return;
        }
        // 相同内容重复操作拦截：自上次成功复制/提交以来内容未改动，不重复复制/提交
        // （拦截发生在写历史之前，因此重复操作也不会产生重复历史记录）
        if (mode == ActionMode.Copy)
        {
            if (_lastCopiedPrompt == prompt)
            {
                ShowToast("当前内容已在剪贴板中，请勿重复操作", false);
                return;
            }
        }
        else if (_lastSubmittedPrompt == prompt)
        {
            ShowToast("当前内容已提交，请勿重复操作", false);
            return;
        }
        // 写入历史（历史中已有相同提示词则不重复写入，避免重复记录；拦截与历史去重双保险）
        var entry = new HistoryEntry
        {
            TemplateName = _current?.Name ?? "",
            Fields = DeepCopyFields(_current?.Fields ?? new List<Field>()),
            Prompt = prompt
        };
        if (_data.History.All(h => h.Prompt != prompt))
        {
            _data.History.Insert(0, entry);
            if (_data.History.Count > 50) _data.History.RemoveRange(50, _data.History.Count - 50);
            try { PersistData(); } catch { }
        }

        if (mode == ActionMode.Copy)
        {
            try
            {
                Clipboard.SetText(prompt);
                _lastCopiedPrompt = prompt;   // 复制成功后才记录，失败允许重试
                TouchRecentUse();             // 记录"最近使用"并刷新置顶
                Logging.Prompt(LogLevel.Info, "复制", _current?.Name, prompt);
                ShowToast("已复制到剪贴板");
            }
            catch (Exception ex)
            {
                Logging.Error($"复制到剪贴板失败，原因={ex.Message}");
                ShowToast($"复制失败：{ex.Message}", false);
            }
        }
        else
        {
            Logging.Prompt(LogLevel.Info, "提交", _current?.Name, prompt);
            if (await SubmitPrompt(prompt))
            {
                _lastSubmittedPrompt = prompt; // 提交成功后才记录，失败允许重试
                TouchRecentUse();               // 记录"最近使用"并刷新置顶
            }
        }
    }

    /// <summary>记录当前模板"最近使用"时间并落盘、刷新置顶显示顺序。</summary>
    private void TouchRecentUse()
    {
        if (_current == null) return;
        _current.LastUsedAt = DateTime.Now;
        try { PersistData(); } catch { }
        RefreshOrderKeepSelection();
    }

    private static List<Field> DeepCopyFields(List<Field> src)
    {
        var list = new List<Field>();
        foreach (var f in src)
        {
            list.Add(new Field { Name = f.Name, Items = new List<string>(f.Items) });
        }
        return list;
    }

    private async Task<bool> SubmitPrompt(string prompt)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Logging.Info($"开始提交 claude：prompt 长度={prompt.Length}");
        ShowToast("正在提交 claude…");
        try
        {
            var result = await Task.Run(async () =>
            {
                var psi = new ProcessStartInfo("claude")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(prompt);
                using var proc = new Process { StartInfo = psi };
                proc.Start();
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                var exited = await Task.WhenAny(proc.WaitForExitAsync(), Task.Delay(120_000));
                if (exited is not Task<bool>)
                {
                    try { proc.Kill(); } catch { }
                    return (false, "claude 执行超时（120 秒）");
                }
                var stdout = await outTask;
                var stderr = await errTask;
                var text = (stdout ?? "").Trim();
                if (text.Length == 0) text = (stderr ?? "").Trim();
                return (true, text.Length == 0 ? "(无输出)" : text);
            });
            sw.Stop();
            if (result.Item1)
                Logging.Info($"claude 提交成功：耗时={sw.ElapsedMilliseconds}ms 输出长度={result.Item2.Length}");
            else
                Logging.Warn($"claude 提交失败：耗时={sw.ElapsedMilliseconds}ms 原因={result.Item2}");
            MessageBox.Show(result.Item2, result.Item1 ? "claude 返回" : "提交失败",
                MessageBoxButton.OK, result.Item1 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            return result.Item1;
        }
        catch (Exception ex)
        {
            sw.Stop();
            Logging.Error($"claude 提交异常：耗时={sw.ElapsedMilliseconds}ms", ex);
            ShowToast($"提交失败：{ex.Message}", false);
            return false;
        }
    }

    // ---------------- 标题栏 ----------------

    private void TitleBar_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 点击按钮区域不触发拖拽
        if (e.OriginalSource is DependencyObject src)
        {
            DependencyObject? cur = src;
            while (cur != null)
            {
                if (cur is Button) return;
                cur = VisualTreeHelper.GetParent(cur);
            }
        }
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();      // 阻塞直到用户松手
            MaybeDock();     // 松手后判断是否贴边收起
        }
    }

    private void MinBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void HistoryBtn_Click(object sender, RoutedEventArgs e)
    {
        // 点击在打开/关闭之间切换：面板已打开时再点则关闭，避免重复创建多个面板
        if (_historyWindow != null)
        {
            _historyWindow.Close();
            _historyWindow = null;
            return;
        }
        var win = new HistoryWindow(_data.History,
            onEdit: entry =>
            {
                // 基于历史快照新建临时模板，不覆盖当前模板；切走时自动删除，
                // 除非用户点了"保存到模板"（那时会 SaveSnapshot 持久化）。
                var t = new Template
                {
                    Name = $"{entry.TemplateName}（历史）",
                    Fields = DeepCopyFields(entry.Fields),
                    IsTemp = true
                };
                _data.Templates.Add(t);
                _loadingTemplates = true;
                TemplateBox.SelectedItem = t;
                _loadingTemplates = false;
                _current = t;
                RenderFields();
                FocusFirstInput();
            },
            onDelete: entry =>
            {
                try { PersistData(); } catch { }
            })
        {
            Owner = this
        };
        // 面板通过编辑/复制/✕ 等任何途径关闭后，同步清引用，保证下次点击可重新打开
        win.Closed += (_, _) => _historyWindow = null;
        _historyWindow = win;
        win.Show();
    }

    /// <summary>打开设置弹窗，保存后应用：热键重注册 + 贴边开关持久化。</summary>
    private void OpenSettings()
    {
        var dlg = new SettingsWindow(_settings.Hotkey, _settings.EdgeDockEnabled, _settings.PinRecentTemplates) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        bool hotkeyChanged = !string.Equals(dlg.HotkeyText, _settings.Hotkey, StringComparison.OrdinalIgnoreCase);
        if (hotkeyChanged && _hotkey != null)
        {
            if (!_hotkey.TryRegister(dlg.HotkeyText, out var reason))
            {
                TrayIcon.ShowBalloonTip("热键注册失败", reason, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning);
                ShowToast(reason, false);
                return; // 注册失败则不写入设置，保持旧热键
            }
            _settings.Hotkey = dlg.HotkeyText;
        }

        _settings.EdgeDockEnabled = dlg.DockEnabled;
        _settings.PinRecentTemplates = dlg.PinRecentEnabled;

        try
        {
            _settingsStore.Save(_settings);
            ShowToast("设置已保存");
        }
        catch (Exception ex)
        {
            ShowToast($"保存失败：{ex.Message}", false);
        }

        // 置顶开关可能已变化：重建模板下拉显示顺序（保留当前选中项）
        RefreshOrderKeepSelection();
    }

    // ---------------- 工具 ----------------

    private void FocusFirstInput()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var first = FieldHost.Children.OfType<Grid>().FirstOrDefault();
            if (first == null) return;
            var right = first.Children.OfType<StackPanel>().FirstOrDefault();
            var ta = right?.Children.OfType<Grid>().FirstOrDefault()?.Children.OfType<AutoGrowTextBox>().FirstOrDefault();
            ta?.Focus();
            Keyboard.Focus(ta);
        }, DispatcherPriority.Loaded);
    }

    private void FocusFieldName(int rowIndex)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var rows = FieldHost.Children.OfType<Grid>().ToList();
            if (rowIndex >= rows.Count) return;
            var head = rows[rowIndex].Children.OfType<Grid>().FirstOrDefault();
            var tb = head?.Children.OfType<TextBox>().FirstOrDefault();
            tb?.Focus();
            Keyboard.Focus(tb);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>聚焦到指定字段行的某个内容框。<paramref name="caretToEnd"/> 为 true 时光标落在文本末尾。</summary>
    private void FocusItemInput(int rowIndex, int itemIndex, bool caretToEnd = false)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var rows = FieldHost.Children.OfType<Grid>().ToList();
            if (rowIndex >= rows.Count) return;
            var right = rows[rowIndex].Children.OfType<StackPanel>().FirstOrDefault();
            if (right == null) return;
            var items = right.Children.OfType<Grid>().ToList();
            if (itemIndex < items.Count)
            {
                var ta = items[itemIndex].Children.OfType<AutoGrowTextBox>().FirstOrDefault();
                if (ta == null) return;
                ta.Focus();
                Keyboard.Focus(ta);
                if (caretToEnd) ta.CaretIndex = ta.Text.Length;   // 段尾
            }
        }, DispatcherPriority.Loaded);
    }

    private DispatcherTimer? _toastTimer;

    private void ShowToast(string msg, bool ok = true)
    {
        Toast.Text = msg;
        Toast.Foreground = ok
            ? (Brush)FindResource("OkBrush")
            : (Brush)FindResource("DangerBrush");
        ToastLayer.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) =>
        {
            ToastLayer.Visibility = Visibility.Collapsed;
            _toastTimer!.Stop();
        };
        _toastTimer.Start();
    }
}
