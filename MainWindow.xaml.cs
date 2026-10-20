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
    /// <summary>条目移动事务标记：被移动条目移出视觉树触发失焦时，抑制失焦删除逻辑（防跨字段/拖拽移动空条目误删）。</summary>
    private bool _suppressItemRemove;
    /// <summary>拖拽调序当前高亮的目标行（条目行/字段头行）；原 Background 均为 Transparent，可安全恢复。</summary>
    private Grid? _dragHighlightItem;
    /// <summary>拖拽落点高亮刷（半透明淡紫，与主题紫 0x7c6cf0 同色系）。</summary>
    private static readonly Brush DragHighlightBrush = new SolidColorBrush(Color.FromArgb(0x26, 0x7c, 0x6c, 0xf0));
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
    private string _filterResetText = "";

    /// <summary>用户最近一次键入后的过滤词（用户输入分支更新）。异步回写吞字（乱序覆盖新输入）时据此恢复，防止输入字符被回写吞掉。</summary>
    private string _lastUserText = "";
    /// <summary>鼠标单击文本区定位后的光标位置（MouseUp 冒泡记录）。ComboBox 打开下拉会重置 caret，据此还原点击定位。</summary>
    private int _lastClickCaret = -1;

    /// <summary>本次文本变化是否源于用户键入（PreviewTextInput 置位，TextChanged 过滤分支消费）。
    /// 为 true 时进入编辑态（置 null + SetCurrentValue 恢复过滤词）；方向键导航（false）保留 SelectedItem，维持候选项高亮。</summary>
    private bool _userTyping;

    /// <summary>本次选中变化是否由鼠标点击候选项引发（窗口级 PreviewMouseLeftButtonDown 置位，SelectionChanged 消费）。
    /// 点击=确认选择（应切换模板）；键盘方向键导航经 _loadingTemplates 短路，不经此分支。</summary>
    private bool _mousePick;
    /// <summary>本次下拉展开是否由鼠标点击箭头触发（MouseDown onToggle 置位，DropDownOpened 消费）。
    /// 箭头点击=进入检索模式（展开时全选模板名，输入即替换+过滤）；键盘（↓/F4/Alt+Down）弹列表不改文本、不全选。</summary>
    private bool _arrowClickPending;
    /// <summary>模板下拉的集合视图：ItemsSource 常驻此视图，过滤只切换 Filter 谓词，
    /// 避免重建 ItemsSource 破坏 IsEditable 下拉的输入与选中态（首字符被吞、方向键高亮丢失的根因）。</summary>
    private ICollectionView? _templatesView;
    /// <summary>键盘（Enter/Space）即将触发点击的按钮。Click 时据此区分键盘/鼠标触发：键盘触发保留焦点，鼠标触发清除焦点。</summary>
    private readonly HashSet<Button> _keyboardClickTargets = new();
    /// <summary>键盘聚焦（Tab）时已由本类即时打开的提示所在按钮；LostKeyboardFocus 时据此关闭，避免误关悬停路径打开的提示。</summary>
    private static readonly HashSet<Button> _keyboardTooltipButtons = new();
    public MainWindow()
    {
        InitializeComponent();
        _data = _store.Load();
        _settingsStore = new SettingsStore(AppPaths.DataDir);
        _settings = _settingsStore.Load();
        ActionButton.Executed += OnActionExecuted;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        // 按钮点击后的焦点策略（统一覆盖新增/删除/保存等顶部/操作按钮）：
        // 键盘（Enter/Space）触发的点击保留焦点——键盘导航连续、可再次 Enter 触发同一按钮；
        // 鼠标触发的点击清除键盘焦点并清空焦点作用域，避免按钮保持焦点造成高亮残留（弹窗关闭/窗口还原时焦点归还按钮）。
        // 键盘导航连续操作依赖 Tab 重新导航（每次点击后焦点回窗口）。
        // 区分依据：PreviewKeyDown(Enter/Space) 加入 _keyboardClickTargets，鼠标按下时移除，Click 时据此判断触发来源。
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
            }));
        EventManager.RegisterClassHandler(typeof(Button), Button.ClickEvent,
            new RoutedEventHandler((sender, _) =>
            {
                // 键盘（Enter/Space）触发的点击保留焦点：保证键盘导航连续、可再次 Enter 触发同一按钮；
                // 鼠标触发的点击清除焦点并清空焦点作用域，避免高亮残留（弹窗关闭/窗口还原时焦点归还按钮）。
                if (sender is Button kb && _keyboardClickTargets.Remove(kb)) return;
                if (sender is not Button btn) return;
                Keyboard.ClearFocus();
                if (Window.GetWindow(btn) is Window win)
                    FocusManager.SetFocusedElement(win, null);
            }));
        // ToolTip 键盘聚焦策略：WPF 内建"键盘聚焦显示提示"同样走 InitialShowDelay（悬停延迟），
        // 导致 Tab 聚焦时提示被延迟。已在样式层用 ShowsToolTipOnKeyboardFocus=False 关闭内建键盘提示，
        // 改由 Got/LostKeyboardFocus 即时开/关（悬停路径仍走 800ms + BetweenShowDelay，行为不变）。
        EventManager.RegisterClassHandler(typeof(Button), Button.GotKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnButtonGotKeyboardFocus));
        EventManager.RegisterClassHandler(typeof(Button), Button.LostKeyboardFocusEvent,
            new KeyboardFocusChangedEventHandler(OnButtonLostKeyboardFocus));
        // 键盘聚焦 ToolTip 一律走 OnButtonGotKeyboardFocus 的 IsMouseOver 判断（区分键盘聚焦/鼠标悬停路径）。
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
        // 任何方式聚焦到按钮（Tab 键盘导航、鼠标点击、焦点归还）时：
        // 仅当鼠标不在按钮上（IsMouseOver=false）即时弹提示并设聚焦视觉。
        // 鼠标在按钮上（IsMouseOver=true）跳过：那是鼠标点击/悬停路径，系统 hover 提示已覆盖，
        // 且鼠标点击不应触发聚焦视觉（避免点击切换置顶时闪紫填充）。
        if (sender is not Button b || b.IsMouseOver) return;
        if (GetOrCreateToolTip(b) is not ToolTip tip) return;
        Logging.Debug($"键盘聚焦ToolTip: btn={b.Name ?? b.Content?.ToString() ?? "?"} 已就绪");
        // 放置走全局 ToolTip 样式（按钮下方居中 2px、超出翻转）；显式设置以防代码创建的
        // ToolTip 未套用隐式样式（否则 Placement 默认 Mouse，键盘聚焦时鼠标在别处会看不到）。
        tip.PlacementTarget = b;
        tip.Placement = PlacementMode.Custom;
        tip.CustomPopupPlacementCallback = App.ToolTipPlacementCallback;
        tip.IsOpen = true;
        _keyboardTooltipButtons.Add(b);
        // 键盘聚焦视觉：淡紫底 + 白色图标（与鼠标按压反馈一致：色块衬底、图标白色清晰可见，不被色块吞没）。
        b.Background = (Brush)Application.Current.FindResource("AccentHoverBrush");
        b.Foreground = Brushes.White;
    }

    private static void OnButtonLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not Button b) return;
        if (!_keyboardTooltipButtons.Contains(b)) return;
        // 延迟清理：窗口 Topmost 切换（Enter 开启置顶）等可能引起短暂失焦后焦点立即恢复，
        // 此时应保留 ToolTip 与键盘聚焦视觉（白色图标）；焦点真正移走（Tab 导航离开）后才清理。
        // 用 Input 优先级延迟到焦点状态稳定后判断。
        b.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (b.IsKeyboardFocused) return;
            _keyboardTooltipButtons.Remove(b);
            if (b.ToolTip is ToolTip tip) tip.IsOpen = false;
            b.ClearValue(Button.BackgroundProperty);
            b.ClearValue(Button.ForegroundProperty);
        }));
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
                // 与鼠标点击箭头展开一致：进入检索模式（全选模板名，输入即替换+过滤）。
                // 置位标记让 DropDownOpened 统一走 selectAll 分支；仅未开时置位——
                // 已开时不触发 DropDownOpened，置位会残留导致下次展开误全选。
                if (!TemplateBox.IsDropDownOpen)
                {
                    _arrowClickPending = true;
                    TemplateBox.IsDropDownOpen = true;
                }
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
        catch (Exception ex) { Logging.Warn($"窗口状态保存失败：{ex.Message}"); }
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
        // 键盘切换置顶后焦点仍在本按钮：强制白色图标（本地值优先级最高），
        // 保持"键盘聚焦=白图标"的视觉（覆盖置顶触发器紫）；同时登记到焦点视觉集合，
        // 失焦时 OnButtonLostKeyboardFocus 才能 ClearValue 回落置顶紫/非置顶浅灰。
        if (PinBtn.IsKeyboardFocused)
        {
            PinBtn.Foreground = Brushes.White;
            _keyboardTooltipButtons.Add(PinBtn);
        }
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
        UpdateTemplateBoxVisual();   // 启动/刷新后按显示态刷新模板框文字颜色（灰字）
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
            _userTyping = false;
            _loadingTemplates = true;
            if (TemplateBox.SelectedItem is Template sel)
            {
                TemplateBox.Text = sel.Name;   // 回填选中模板名（原生已同步；短路使后续 TextChanged 不重入过滤分支）
                TemplateBox.IsDropDownOpen = false;
            }
            _loadingTemplates = false;
            RefreshFilteredList();   // 清过滤条件恢复全量列表：无删除时视图不重建，漏清 Filter 会导致下次展开残留过滤结果（截图实证）
        }
        Logging.Debug($"SelChanged sel={(TemplateBox.SelectedItem as Template)?.Name} filter={_filterActive} loading={_loadingTemplates}");
        bool removedAny = LeaveCurrentTemplate();   // 离开清理：未保存历史临时模板移除 + 当前模板快照恢复/空白删除
        LoadCurrentTemplate();
        if (removedAny)
        {
            // 离开清理删除了空白/临时模板：_templatesView 是静态快照不反映删除，重建视图避免列表残留已删模板，
            // 保持当前选中项（切换目标），不重置到首项
            _loadingTemplates = true;
            var sel = TemplateBox.SelectedItem;
            _templatesView = new ListCollectionView(GetDisplayOrder().ToList());
            _templatesView.Filter = _filterActive
                ? new Predicate<object>(t => (t as Template)?.Name?.IndexOf(TemplateBox.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                : null;
            TemplateBox.ItemsSource = _templatesView;
            if (sel != null) TemplateBox.SelectedItem = sel;
            _loadingTemplates = false;
            Logging.Debug($"SelChanged rebuiltView removedAny={removedAny} sel={(sel as Template)?.Name}");
        }
        UpdateTemplateBoxVisual();   // 切换完成：强制刷新显示态灰字——点击候选项的 enterInput 分支（显示态误命中候选项点击）已把文字置白，
                                     // 而场景1/历史编辑切换的 rebuiltView 在 _loadingTemplates 下同步 Text、短路 TextChanged，切换路径无刷新则白字残留（日志实证）
        if (pick) FocusFirstInput();   // 鼠标点击候选项=确认切换：退出编辑态，聚焦第一个内容框段首（显示态保持灰字）
    }

    /// <summary>离开当前模板前的清理，返回是否有模板被移除/删除（调用方需据此重建视图避免列表残留）：
    /// - 历史编辑产生的临时模板（未点"保存到模板"）：全部清除。除当前模板外，也清理列表中残留的其他历史编辑模板——
    ///   例如：编辑历史A后未切走又直接编辑历史B，onEdit 中 _loadingTemplates 会屏蔽本次切换的离开清理，
    ///   导致 A 残留在列表，需在后续任意一次切换时一并清除。
    /// - 有快照（保存过的模板）：从快照恢复，丢弃未保存的编辑
    /// - 无快照（新建未保存）：空白则删除，有内容则保留在内存
    /// 历史编辑进入临时模板（HistoryWindow onEdit）也调用本方法：否则原模板的未保存改动会泄漏进原模板
    /// （回归根因：_templatesView 是静态快照、不含临时模板，onEdit 的 SelectedItem=t 实际落空，
    /// _loadingTemplates 又屏蔽了 SelectionChanged 的切换清理，快照恢复从未执行）。</summary>
    private bool LeaveCurrentTemplate()
    {
        if (_current == null) return false;
        var tempToRemove = _data.Templates
            .Where(x => x.IsTemp && !_savedSnapshots.ContainsKey(x.Id))
            .ToList();
        bool removedAny = tempToRemove.Count > 0;
        if (tempToRemove.Count > 0)
        {
            foreach (var t in tempToRemove)
            {
                _data.Templates.Remove(t);
                // 同步清理"未保存新模板"标记（历史编辑模板不会命中，统一处理更稳妥）
                if (_pendingNewTemplate == t) _pendingNewTemplate = null;
            }
            try { _store.Save(_data); }
            catch (Exception ex) { Logging.Warn($"模板改名落盘失败：{ex.Message}"); }
        }
        bool removedBlank = false;
        // 当前模板若已被上述清理移除（历史编辑模板），跳过快照恢复等后续处理
        if (_data.Templates.Contains(_current))
        {
            if (_savedSnapshots.ContainsKey(_current.Id))
                RestoreFromSnapshot(_current);
            else if (IsBlankNewTemplate(_current))
            {
                _data.Templates.Remove(_current);
                // 同步清理"未保存新模板"标记，否则下次点"新增模板"会被误判为仍有未保存模板
                if (_pendingNewTemplate == _current) _pendingNewTemplate = null;
                removedBlank = true;
                try { _store.Save(_data); }
                catch (Exception ex) { Logging.Warn($"删除空白模板落盘失败：{ex.Message}"); }
            }
        }
        return removedAny || removedBlank;
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
        if (_renaming)
        {
            // 改名态输入日志：定位 ComboBox 一致性回写覆盖输入（文本被回写回选中项名、光标跳段首）
            Logging.Debug($"RenameTyping text={TemplateBox.Text} sel={(TemplateBox.SelectedItem as Template)?.Name}");
            return;
        }
        UpdateTemplateBoxVisual();   // 文本变化后刷新显示态灰字/编辑态黑字
        string text = TemplateBox.Text.Trim();
        bool equalsCurrent = string.Equals(text, _current.Name?.Trim(), StringComparison.Ordinal);
        Logging.Debug($"TextChanged text={TemplateBox.Text} filter={_filterActive} renaming={_renaming} equals={equalsCurrent} focus={Keyboard.FocusedElement?.GetType().Name}");
        // 用户输入识别（提前于回写免疫判定）：中文 TSF 组合提交不触发 PreviewTextInput（日志实证），
        // 若仅在下方编辑态判定，第二次中文输入（text≠旧 _lastUserText）会先被 IgnoreAsyncReset2 误判为
        // 异步回写、恢复成旧过滤词（日志实证"为我通用"restore"通用"→ 内容丢失）。此处先识别并更新
        // _lastUserText=本次输入，使回写免疫基准正确：随后 787 的 text!=_lastUserText 不满足，误伤消除。
        // 判定与编辑态一致：PTextInput 键入（_userTyping）、无选中项（用户输入后编辑态置 null）、
        // 或文本与选中项名不一致（组合提交/直插）。回写（text==选中项名）与方向键导航不满足，不更新。
        bool isUserText = _userTyping
            || TemplateBox.SelectedItem == null
            || (TemplateBox.SelectedItem is Template selTU
                && !string.Equals(text, selTU.Name?.Trim(), StringComparison.Ordinal));
        if (isUserText)
        {
            _userTyping = false;
            _lastUserText = text;
        }
        // 异步回写吞字免疫：刷新视图产生的异步回写可能乱序覆盖用户刚键入的文本（日志实证 iu→u）；
        // 回写非用户输入（_userTyping=false）且仍在过滤态、文本与用户最近输入不一致时，恢复用户输入。
        // 点击候选项确认切换（_filterActive 已清）不受影响；方向键导航文本保持过滤词（==_lastUserText）不受影响。
        // 追加两条排除：① 无选中项（编辑态，用户输入后 SelectedItem 已置 null，TSF 中文提交不置 _userTyping，
        // 日志实证"为我通用"被误恢复成旧词"通用"）→ 属用户新输入而非回写，跳过恢复；
        // ② 文本以过滤词开头（代→代码）→ 用户继续输入，跳过恢复。回写需要选中项名（SelectedItem 非空）。
        if (!_userTyping && _filterActive && !string.IsNullOrEmpty(_lastUserText)
            && !string.Equals(text, _lastUserText, StringComparison.Ordinal)
            && TemplateBox.SelectedItem != null
            && !text.StartsWith(_lastUserText, StringComparison.Ordinal))
        {
            Logging.Debug($"IgnoreAsyncReset2 text={TemplateBox.Text} restore={_lastUserText}");
            // 同样置 null 消除"文本≠选中项名"悬置不一致（恢复的是用户输入词，必与选中项名不同，
            // 悬置态下 ComboBox 内部会吞掉后续输入，日志实证热键唤起后回写恢复无第二次 TextChanged）。
            if (TemplateBox.SelectedItem != null)
            {
                _loadingTemplates = true;
                TemplateBox.SelectedItem = null;
                _loadingTemplates = false;
            }
            _loadingTemplates = true;
            TemplateBox.SetCurrentValue(ComboBox.TextProperty, _lastUserText);
            _loadingTemplates = false;
            // 恢复后同步设置光标（不再 Background 兜底）：兜底在用户输入事件后执行会取消 IME 组合，
            // 偶发"第二次输入被吞、框内保持旧词"（日志实证 RestoreCaret 在组合激活期执行）。
            SetCaretAt(_lastUserText.Length);
            return;
        }
        if (string.IsNullOrEmpty(text) || equalsCurrent)
        {
            // 回写免疫：视图 Refresh() 后 ComboBox 异步把 Text 回写为当前模板名（日志实证，回写时选中项非空），
            // 该文本并非用户本次输入 → 恢复过滤词并保持过滤态。免疫不带现场标记、持续生效：
            // 第二次输入组合激活时 ComboBox 再次把文本回写为选中项名（日志实证 10:54:37），若免疫只对
            // 单次回写生效（消费即失效），此时会退出过滤态 → IsDisplayMode() 成立 → PreviewTextInput
            // 补发事件把输入框清空（第二次输入内容丢失）。过滤态下文本回写为选中项名（≠过滤词）一律恢复。
            // 恢复必须用 SetCurrentValue（与用户键入同路径，ComboBox 视为编辑态不校正）；
            // SetValue/TextBox 直设都会触发 ComboBox 的一致性校正（清空或回写，日志实证）。
            if (_filterActive && equalsCurrent
                && !string.Equals(text, _filterResetText, StringComparison.Ordinal))
            {
                Logging.Debug($"IgnoreAsyncReset text={TemplateBox.Text} restore={_filterResetText}");
                // 恢复过滤词时 SelectedItem 仍为当前模板（非 null）→ "文本≠选中项名"悬置不一致，
                // ComboBox 内部会吞掉后续输入（日志实证：热键唤起回写恢复后第二次输入无 TextChanged）。
                // 置 null 进入编辑态，保持与用户键入后的状态一致（Enter 确认/方向键导航不受影响）。
                if (TemplateBox.SelectedItem != null)
                {
                    _loadingTemplates = true;
                    TemplateBox.SelectedItem = null;
                    _loadingTemplates = false;
                }
                _loadingTemplates = true;
                TemplateBox.SetCurrentValue(ComboBox.TextProperty, _filterResetText);
                _loadingTemplates = false;
                // 恢复后同步设置光标（不再 Background 兜底，理由同上：避免取消 IME 组合）。
                SetCaretAt(_filterResetText.Length);
                return;
            }
            // 用户删除/清空导致空文本：进入编辑态（置 null）防止 ComboBox 把空文本回写为选中项名（日志实证）
            if (string.IsNullOrEmpty(text))
            {
                _userTyping = false;
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
        // 编辑态判定放宽：中文 TSF 组合提交不触发 PreviewTextInput（日志实证），_userTyping 保持 false，
        // 若只认 _userTyping 会跳过编辑态 → SelectedItem 不置 null → ComboBox 回写覆盖输入、_lastUserText 不更新。
        // 进入编辑态：PTextInput 键入（_userTyping）、无选中项（用户输入后，ComboBox 无选中项名可回写）、
        // 或文本与选中项名不一致（组合提交/直插）。方向键导航（text==选中项名）与点击候选项确认不受影响。
        _filterResetText = text;
        _filterActive = true;
        if (!TemplateBox.IsDropDownOpen) TemplateBox.IsDropDownOpen = true;
        if (isUserText)
        {
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
        // Refresh() 会把 caret 重置回 0（内部文本同步回写），必须在 Refresh 后设置。
        // 同步设置即已生效（日志实证 EditEnd tbCaret=0 → SetCaretAt 后 RestoreCaret tbCaret=2），
        // 不再追加 Background 兜底：兜底在用户输入事件之后执行，若用户紧接着输入（连续输入场景）
        // 会把光标设回末尾、干扰输入法组合（偶发"第二次输入被吞、框内保持旧词"）。
        // 强制段尾符合过滤态输入的核心预期（方向键导航不进编辑态，不受影响）。
        if (isUserText)
        {
            if (TemplateBox.Template?.FindName("PART_EditableTextBox", TemplateBox) is TextBox tbE)
                Logging.Debug($"EditEnd tbText={tbE.Text} tbSel={tbE.SelectionLength} tbCaret={tbE.CaretIndex} combo={TemplateBox.Text}");
            SetCaretAt(text.Length);
        }
    }

    /// <summary>把模板框编辑文本框的光标设置到指定位置并取消选中：置 null/SetCurrentValue 恢复文本后 caret 会停在段首，
    /// 需还原到操作前位置（用户用 ←/鼠标移动过的光标不被强制拉回末尾）。</summary>
    private void SetCaretAt(int caret)
    {
        if (TemplateBox.Template?.FindName("PART_EditableTextBox", TemplateBox) is TextBox tb)
        {
            tb.CaretIndex = Math.Clamp(caret, 0, tb.Text.Length);
            tb.SelectionLength = 0;
        }
    }

    /// <summary>显示态判定：非改名、非过滤，且框内文本即当前模板名。显示态模板名只读呈现（灰字占位符），
    /// 点击文本/键入才清空进入输入态（空框检索，无全选/替换冲突）。</summary>
    private bool IsDisplayMode() =>
        !_renaming && !_filterActive && _current != null
        && string.Equals(TemplateBox.Text?.Trim(), _current.Name?.Trim(), StringComparison.Ordinal);

    /// <summary>按状态切换编辑框文字颜色：显示态灰字（只读占位符感）；输入态/过滤态/改名态用主题前景色（暗底白字可辨）。</summary>
    private void UpdateTemplateBoxVisual()
    {
        if (TemplateBox.Template?.FindName("PART_EditableTextBox", TemplateBox) is TextBox tb)
            tb.Foreground = IsDisplayMode() ? Brushes.Gray : TemplateBox.Foreground;
    }

    /// <summary>字符输入前（隧道阶段）先进入过滤态并展开下拉，使随后由视图/ComboBox 触发的选中变化被过滤态屏蔽，避免输入即自动切换模板。</summary>
    private void TemplateBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_renaming || _current == null) return;
        if (TemplateBox.Template?.FindName("PART_EditableTextBox", TemplateBox) is TextBox tb)
            Logging.Debug($"PTextInput sel={tb.SelectionLength} caret={tb.CaretIndex} text={tb.Text}");
        if (IsDisplayMode())
        {
            // 显示态键入：先清空进入输入态（置 null 防回写），随后的字符输入到空框并触发检索
            _loadingTemplates = true;
            TemplateBox.SelectedItem = null;
            TemplateBox.SetCurrentValue(ComboBox.TextProperty, "");
            _loadingTemplates = false;
            UpdateTemplateBoxVisual();
            Logging.Debug($"PTextInput enterInput text={TemplateBox.Text}");
        }
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
        _filterActive = false;
        _loadingTemplates = true;
        TemplateBox.Text = _current?.Name ?? "";
        _loadingTemplates = false;
        RefreshFilteredList();
        UpdateTemplateBoxVisual();   // 恢复显示态：灰字
        // 过滤态方向键可能已选中候选：Esc 后恢复选中指向当前模板
        if (_current != null && TemplateBox.SelectedItem != _current)
        {
            _loadingTemplates = true;
            TemplateBox.SelectedItem = _current;
            _loadingTemplates = false;
        }
    }

    /// <summary>文本/箭头分区点击：箭头区=切换列表；文本区=显示态清空进入输入态（空框检索，无全选/替换冲突）、
    /// 输入态正常光标定位（TextBox 原生：单击定位、双击选词）。改名态点击箭头：忽略并提示先退出编辑。
    /// 拦截必须在此隧道阶段完成——点击箭头时焦点会移向 ComboBox 触发失焦确认（TryConfirmRename 退出改名态），
    /// 若等冒泡阶段再拦，改名态已被确认、列表已弹出。</summary>
    private void TemplateBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        bool onToggle = IsHitOnToggle(e.OriginalSource as DependencyObject);
        Logging.Debug($"MouseDown onToggle={onToggle} src={e.OriginalSource?.GetType().Name} anc={AncestorChain(e.OriginalSource as DependencyObject)} focus={Keyboard.FocusedElement?.GetType().Name} text={TemplateBox.Text}");
        if (onToggle && !_renaming) _arrowClickPending = true;   // 箭头点击：展开时全选进入检索模式（键盘弹列表不标记）
        if (_renaming && onToggle)
        {
            e.Handled = true;   // 阻止焦点转移与列表切换
            ShowToast("请先退出编辑操作，按Enter（保存）或ESC（取消）", false);
            return;
        }
        if (!_renaming && !onToggle && IsDisplayMode())
        {
            // 显示态点击文本：清空进入输入态（置 null + 编辑态语义，防 ComboBox 聚焦时校正回写选中项名）。
            // 首次点击（焦点从未聚焦移到 TextBox）的聚焦校正由 TemplateBox_GotKeyboardFocus 的延迟补偿兜底。
            _userTyping = true;
            _loadingTemplates = true;
            TemplateBox.SelectedItem = null;
            TemplateBox.SetCurrentValue(ComboBox.TextProperty, "");
            _loadingTemplates = false;
            UpdateTemplateBoxVisual();
            Logging.Debug($"MouseDown enterInput text={TemplateBox.Text}");
        }
    }

    /// <summary>下拉打开时清除编辑框全选：WPF 可编辑 ComboBox 打开下拉会全选编辑框且把 caret 重置为 0（覆盖点击定位，
    /// 导致"点击定位后输入替换全文/插错位置"——日志实证 PTextInput sel=0 caret=1 → DropDownOpened caret=0）。
    /// 清除后按点击定位恢复 caret（_lastClickCaret），后续输入在光标处插入。改名态不开下拉，不受影响。</summary>
    private void TemplateBox_DropDownOpened(object sender, EventArgs e)
    {
        if (_renaming) return;
        if (TemplateBox.Template?.FindName("PART_EditableTextBox", TemplateBox) is TextBox tb)
        {
            if (_arrowClickPending)
            {
                // 箭头点击展开：全选进入检索模式（输入即替换+过滤）。键盘弹列表（↓/F4/Alt+Down）不标记，保持只读文本。
                _arrowClickPending = false;
                tb.SelectAll();
                Logging.Debug($"DropDownOpened selectAll text={tb.Text} sel={tb.SelectionLength}");
            }
            else
            {
                if (tb.SelectionLength > 0) tb.SelectionLength = 0;
                // 输入触发开下拉（_filterActive=true，如删除/历史编辑后键入首字符）：不还原光标——
                // _lastClickCaret 是点击文本的残留值，此时 SetCaretAt 会把组合提交后的光标移回段首（日志实证
                // DropDownOpened clearSel caret=0 → 编辑态 SetCaretAt(0)）；仅"用户点击文本→点箭头"的定位还原场景
                // （_filterActive=false）才按点击位置恢复。
                if (!_filterActive && _lastClickCaret >= 0) SetCaretAt(_lastClickCaret);
                Logging.Debug($"DropDownOpened clearSel caret={tb.CaretIndex}");
            }
        }
    }

    /// <summary>点击释放（隧道）：记录点击定位后的光标（_lastClickCaret 供 DropDownOpened 还原）。
    /// 冒泡 MouseUp 会被 ComboBox 类处理 Handled（不触发），故在隧道记录；单击文本区才记录（箭头点击不定位）。</summary>
    private void TemplateBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (TemplateBox.Template?.FindName("PART_EditableTextBox", TemplateBox) is TextBox tb)
        {
            if (e.ClickCount == 1 && !IsHitOnToggle(e.OriginalSource as DependencyObject))
                _lastClickCaret = tb.CaretIndex;   // 点击定位后的光标（MouseDown 已定位）
            Logging.Debug($"MouseUpPre click={e.ClickCount} sel={tb.SelectionLength} caret={tb.CaretIndex} text={tb.Text}");
        }
    }

    /// <summary>点击释放（冒泡，所有类处理之后）：可编辑 ComboBox 在冒泡阶段会把编辑框全选（覆盖点击定位，
    /// 导致后续输入替换全文）；单击文本区后清除全选，保留 TextBox 已定位的光标——输入在光标处插入。双击选词（ClickCount=2）不受影响。</summary>
    private void TemplateBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_renaming || e.ClickCount != 1) return;
        if (IsHitOnToggle(e.OriginalSource as DependencyObject)) return;
        if (TemplateBox.Template?.FindName("PART_EditableTextBox", TemplateBox) is TextBox tb)
        {
            _lastClickCaret = tb.CaretIndex;   // 点击定位后的光标（DropDownOpened 重置 caret 时据此还原）
            if (tb.SelectionLength > 0) tb.SelectionLength = 0;
            Logging.Debug($"MouseUp sel={tb.SelectionLength} caret={tb.CaretIndex} text={tb.Text}");
        }
    }

    /// <summary>命中测试：点击点是否落在下拉箭头区（模板内 ToggleButton 及其子元素上）。</summary>
    private static bool IsHitOnToggle(DependencyObject? source)
    {
        for (DependencyObject? d = source; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is ToggleButton) return true;
        return false;
    }

    /// <summary>调试用：打印元素祖先链类型（定位命中判定来源）。</summary>
    private static string AncestorChain(DependencyObject? node)
    {
        var names = new List<string>();
        for (DependencyObject? d = node; d != null && names.Count < 8; d = VisualTreeHelper.GetParent(d))
            names.Add(d.GetType().Name);
        return string.Join(">", names);
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
        // 点击非输入区（空白/标题栏/字段区空隙）时交还窗口焦点，让聚焦中的空内容框失焦删除：
        // WPF 点击无焦点目标的空白处默认不移焦，空框会一直保留；输入框/按钮自身正常拿焦点，不走此分支。
        if (_suppressReclaim) return;
        var src = e.OriginalSource as DependencyObject;
        if (IsWithinInput(src) || IsWithinButton(src)) return;
        // 点击下拉候选项（_mousePick）=确认选择切换模板，不是空白点击：
        // 若误走清焦点分支，模板框失焦会触发下拉自动关闭，候选项 MouseUp 不再命中，选择被中断，
        // 随后失焦恢复显示原模板名（日志实证：ClickBlank → LostFocus newFocus=MainWindow → Refresh 恢复 Bug修复，无 SelChanged）。
        if (_mousePick) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase fe)
        {
            Logging.Debug($"ClickBlank clearfocus focused={fe.GetType().Name}");
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                FocusManager.SetFocusedElement(this, null);
                Focus();
            }));
        }
    }

    /// <summary>点击点是否落在输入框（文本框/组合框）内：沿视觉树向上找 TextBoxBase / ComboBox。</summary>
    private static bool IsWithinInput(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.Primitives.TextBoxBase || d is System.Windows.Controls.ComboBox) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>点击点是否落在按钮内：沿视觉树向上找 ButtonBase（按钮点击自聚焦，不走清焦点分支）。</summary>
    private static bool IsWithinButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>重命名按钮：进入改名态——收起下拉、文本预填当前模板名并全选、聚焦编辑框，键入即覆盖。
    /// 注意：焦点必须留在编辑框内（tb.Focus），不能再 Keyboard.Focus 回 ComboBox——那会触发失焦确认立即退出改名态。</summary>
    private void RenameTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        Logging.Debug($"RenameEnter current={_current?.Name}");
        _renaming = true;
        // 离开过滤态：清过滤条件，防改名输入受过滤态残留/异步回写干扰（日志实证过滤残留）
        _filterActive = false;
        _userTyping = false;
        RefreshFilteredList();
        TemplateBox.IsDropDownOpen = false;
        // 先预填文本、后置空选中项：若先置 null 再设 Text，ComboBox 会因 Text 匹配选中项名反向同步 SelectedItem 回 _current，
        // 改名输入随即被一致性校正回写覆盖（实测输入'Bug'仍显示'Bug修复'）。无选中项时文本完全自由，改名确认/取消后再恢复指向当前模板。
        TemplateBox.Text = _current?.Name ?? "";
        _loadingTemplates = true;
        TemplateBox.SelectedItem = null;
        _loadingTemplates = false;
        UpdateTemplateBoxVisual();   // 改名态：黑字可编辑
        Logging.Debug($"RenameReady sel={(TemplateBox.SelectedItem as Template)?.Name}");
        if (TemplateBox.Template?.FindName("PART_EditableTextBox", TemplateBox) is TextBox tb)
        {
            // 编辑框失焦（点击外部控件/空白）即确认改名：LostKeyboardFocus 是直接路由事件，不冒泡到 TemplateBox，
            // 模板框自身的 LostKeyboardFocus 处理器收不到内部编辑框的焦点变化——不移焦确认会导致改名态残留、模板名保持白字
            tb.LostKeyboardFocus -= OnRenameEditBoxLostFocus;
            tb.LostKeyboardFocus += OnRenameEditBoxLostFocus;
            tb.Focus();
            tb.SelectAll();
        }
    }

    /// <summary>改名态编辑框失焦：确认改名并退出改名态（一次性 handler，失焦后自动卸载）。
    /// Enter 确认/ESC 取消后编辑框失焦也会触发本方法，此时 _renaming 已退出，仅卸载无副作用。</summary>
    private void OnRenameEditBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var tb = (TextBox)sender;
        tb.LostKeyboardFocus -= OnRenameEditBoxLostFocus;
        Logging.Debug($"RenameEditLostFocus confirm renaming={_renaming} newFocus={e.NewFocus?.GetType().Name}");
        if (_renaming) TryConfirmRename();
    }

    /// <summary>模板框获得键盘焦点：鼠标点击聚焦（且当前为显示态）时，延迟到 ComboBox 聚焦全选/校正完成后清空进入输入态——
    /// 消除"点击模板框全选闪烁、光标落到段首"（启动后首次点击的 MouseDown 可能被窗口激活流程消耗，聚焦校正会回写选中项名）。
    /// Tab/键盘聚焦（鼠标不在编辑框上）保持显示态，不进入输入态。</summary>
    private void TemplateBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_renaming || _current == null) return;
        if (!IsDisplayMode()) return;
        // 仅"鼠标点击编辑框"聚焦才进入输入态（点箭头聚焦 ToggleButton 不算）；Tab 聚焦保持显示态
        bool mouseOnText = IsWithinTextBox(Mouse.DirectlyOver as DependencyObject);
        if (!mouseOnText) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_renaming || _current == null || !IsDisplayMode()) return;
            _userTyping = true;
            _loadingTemplates = true;
            TemplateBox.SelectedItem = null;
            TemplateBox.SetCurrentValue(ComboBox.TextProperty, "");
            _loadingTemplates = false;
            UpdateTemplateBoxVisual();
            Logging.Debug($"GotFocus enterInput(deferred) text={TemplateBox.Text}");
        });
    }

    /// <summary>命中测试：元素（或其祖先）是否落在模板框的编辑文本框（PART_EditableTextBox）内。</summary>
    private static bool IsWithinTextBox(DependencyObject? node)
    {
        for (DependencyObject? d = node; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is TextBox) return true;
        return false;
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
            _filterActive = false;
            _loadingTemplates = true;
            TemplateBox.Text = _current?.Name ?? "";
            if (TemplateBox.SelectedItem != _current)
                TemplateBox.SelectedItem = _current;
            _loadingTemplates = false;
            RefreshFilteredList();
            UpdateTemplateBoxVisual();   // 恢复显示态：灰字
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
            UpdateTemplateBoxVisual();   // 退出改名态：恢复显示态灰字（与正常/取消路径一致，防空名退出后白字残留）
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
            // 重命名 Enter 只保存模板名称：仅更新快照的名称、保留快照原有字段（最近一次显式保存的内容），
            // 使 PersistData 只把新名称落盘、不把未保存的内容编辑一并写盘（收窄"保存整个模板"为"仅保存名称"）。
            if (_savedSnapshots.TryGetValue(_current.Id, out var snap))
                snap.Name = newName;
            else
                SaveSnapshot(_current);
            try { PersistData(); }
            catch (Exception ex) { Logging.Warn($"模板改名落盘失败：{ex.Message}"); }
        }
        _renaming = false;
        // 改名输入可能已使选中项与文本脱节：恢复选中指向当前模板，保证下拉展开显示正常
        if (TemplateBox.SelectedItem != _current)
        {
            _loadingTemplates = true;
            TemplateBox.SelectedItem = _current;
            _loadingTemplates = false;
        }
        UpdateTemplateBoxVisual();   // 退出改名态：恢复显示态灰字
        // ComboBox 选中项回写 Text 走异步路径：延迟补刷一次，覆盖"改名后聚焦内容框偶发白字"竞态
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(UpdateTemplateBoxVisual));
        return true;
    }

    /// <summary>取消改名：恢复原名并退出改名态。</summary>
    private void CancelRename()
    {
        if (!_renaming) return;
        _renaming = false;
        TemplateBox.Text = _current?.Name ?? "";
        // 改名入口已置空选中项：取消后恢复指向当前模板，保证下拉展开/显示态正常
        if (TemplateBox.SelectedItem != _current)
        {
            _loadingTemplates = true;
            TemplateBox.SelectedItem = _current;
            _loadingTemplates = false;
        }
        UpdateTemplateBoxVisual();   // 恢复显示态灰字
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
        // 重建显示视图（_templatesView 是静态快照，不反映新增），退出过滤态，选中新模板并回填文本
        _loadingTemplates = true;
        _filterActive = false;
        _templatesView = new ListCollectionView(GetDisplayOrder().ToList());
        _templatesView.Filter = null;
        TemplateBox.ItemsSource = _templatesView;
        TemplateBox.DisplayMemberPath = nameof(PromptGenerator.Models.Template.Name);
        TemplateBox.SelectedItem = t;
        TemplateBox.Text = t.Name;
        _loadingTemplates = false;
        _current = t;
        _pendingNewTemplate = t;
        RenderFields();
        UpdateTemplateBoxVisual();
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
        try { _store.Save(_data); }
        catch (Exception ex) { Logging.Warn($"删除模板落盘失败：{ex.Message}"); }
        RefreshTemplateBox();
        TemplateBox.Focus();
        Logging.Info($"删除模板：{current.Name}");
        ShowToast("已删除模板");
    }

    private void ResetTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var r = MessageBox.Show($"将模板「{_current.Name}」恢复到上一次保存的状态？", "重置模板",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

        // 重置 = 恢复到上一次保存的状态（快照），适用于所有已保存的模板（含预置模板，不区分出厂/自建）
        if (!_savedSnapshots.TryGetValue(_current.Id, out var snap))
        {
            ShowToast("该模板尚未保存过，无可重置的状态", false);
            return;
        }

        // 退出改名/过滤态，防止 ComboBox 一致性回写干扰（点击按钮前 LostFocus 已先行处理，此处兜底）
        _renaming = false;
        _filterActive = false;
        _userTyping = false;

        _current.Name = snap.Name;
        _current.Fields.Clear();
        foreach (var f in snap.Fields)
            _current.Fields.Add(new Field { Name = f.Name, Items = new List<string>(f.Items) });

        // 无需 SaveSnapshot / 落盘：快照即磁盘内容，重置只是把内存恢复到该状态，写盘内容与磁盘现状相同（冗余）

        _loadingTemplates = true;
        TemplateBox.Text = _current.Name;
        if (TemplateBox.SelectedItem != _current) TemplateBox.SelectedItem = _current;
        _loadingTemplates = false;
        RefreshFilteredList();
        RenderFields();
        UpdateTemplateBoxVisual();
        Logging.Info($"重置模板：{_current.Name}");
        ShowToast("已恢复到上次保存状态");
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
            // 以 RowGap 在 FieldHost.Children 中的实时位置推导插入点（间隙位于奇数索引 2i+1 → 在字段 i 后插入 → i+1），
            // 不依赖创建时捕获的 insertIndex：删除字段后该值会过时（> Fields.Count 时 List.Insert 抛 ArgumentOutOfRangeException）。
            int idx = FieldHost.Children.IndexOf(gap);
            AddFieldAt(idx >= 0 ? (idx + 1) / 2 : insertIndex);
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
        PairCompletion.Attach(nameBox);
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
        // 拖拽落点：字段头行——拖到字段名区域 = 插到该字段开头；同样用 Preview 隧道先于字段名框 TextBox 收到。
        head.PreviewDragOver += (_, e) =>
        {
            if (_current == null || !e.Data.GetDataPresent("PGItemDrag")) return;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            SetDragHighlight(head);
        };
        head.PreviewDrop += (_, e) =>
        {
            if (_current == null || !e.Data.GetDataPresent("PGItemDrag")) return;
            e.Handled = true;
            if (e.Data.GetData("PGItemDrag") is not int[] data || data.Length != 2) return;
            ClearDragHighlight();
            if (MoveItemTo(data[0], data[1], getRowIndex(), 0))
                FocusItemInput(getRowIndex(), 0);
        };
        head.DragLeave += (_, e) =>
        {
            // 进入子元素（字段名框）也会触发父 DragLeave：仅当鼠标真正移出本行矩形才清除高亮
            if (_dragHighlightItem != head) return;
            var pos = e.GetPosition(head);
            if (pos.X < 0 || pos.Y < 0 || pos.X > head.ActualWidth || pos.Y > head.ActualHeight)
                ClearDragHighlight();
        };

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
        // 条目可能被跨字段移动（键盘/拖拽）：闭包 field 仍是创建时的源字段，数据操作必须反查当前所属字段，
        // 否则 getIndex()（目标位置）与 field.Items（源字段列表）错位导致索引越界（TextChanged/× 删除实测崩溃）。
        Func<Field> getField = () =>
        {
            int ri = getRowIndex();
            return _current != null && ri >= 0 && ri < _current.Fields.Count
                ? _current.Fields[ri]
                : field;   // 兜底：异常态回退创建时字段，避免越界崩溃
        };

        var num = new TextBlock
        {
            Text = (itemIndex + 1) + ".",
            Foreground = new SolidColorBrush(Color.FromRgb(0x7c, 0x6c, 0xf0)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right
        };
        // 序号列：拖拽调序把手（移动超 5px 启动）+ 单击全选（聚焦本行内容框并全选文本，便于快速删除/覆盖）。
        // 按下进入潜在拖拽并捕获鼠标；未超阈值松开视为单击。
        bool numPress = false;
        Point numPressPos = default;
        bool numDrag = false;
        num.MouseLeftButtonDown += (_, e) =>
        {
            if (_current == null) return;
            numPress = true;
            numDrag = false;
            numPressPos = e.GetPosition(num);
            num.CaptureMouse();
            e.Handled = true;
        };
        num.MouseMove += (_, e) =>
        {
            if (!numPress || numDrag) return;
            var pos = e.GetPosition(num);
            if (Math.Abs(pos.X - numPressPos.X) > 5 || Math.Abs(pos.Y - numPressPos.Y) > 5)
            {
                numDrag = true;
                num.ReleaseMouseCapture();   // 触发 LostMouseCapture 清 numPress（DoDragDrop 用已捕获的 fr/fi）
                int fr = getRowIndex();
                int fi = getIndex();
                item.Opacity = 0.5;   // 源行半透明拖影反馈
                try
                {
                    if (_current != null && fr >= 0 && fi >= 0)
                        DragDrop.DoDragDrop(num, new DataObject("PGItemDrag", new[] { fr, fi }), DragDropEffects.Move);
                }
                finally
                {
                    item.Opacity = 1;
                    ClearDragHighlight();
                }
            }
        };
        num.MouseLeftButtonUp += (_, e) =>
        {
            if (!numPress || numDrag) return;
            numPress = false;
            num.ReleaseMouseCapture();
            // 单击：聚焦本行内容框并全选（num 在 ta 之前创建，运行时从 item.Children 反查）
            var targetTa = item.Children.OfType<AutoGrowTextBox>().FirstOrDefault();
            if (targetTa != null)
            {
                targetTa.Focus();
                Keyboard.Focus(targetTa);
                targetTa.SelectAll();
            }
            e.Handled = true;
        };
        num.LostMouseCapture += (_, _) => { numPress = false; numDrag = false; };

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
        PairCompletion.Attach(ta);
        ta.PreviewKeyDown += (_, e) =>
        {
            // Ctrl+Z：IME 符号替换选中文本（包裹/补右/跳过/替换）产生多个撤销单元，且 WPF Undo 不恢复选区。
            // ① 符号操作后未做任何操作（文本与光标匹配输入后快照）→ 循环 Undo 到输入前原文 + 手动 Select 恢复选中；
            // ② 已恢复到"原文+原选区选中"（上次整体回退已完成）→ 循环跳过 WPF 的"选区/光标"撤销单元，
            //    按输入粒度继续撤销原文输入（"东大"一次输入则一次变空，多次输入则逐次回退）。
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control
                && PairCompletion.TryGetLastSymbolEditState(ta,
                    out string beforeText, out int beforeStart, out int beforeLen,
                    out string afterText, out int afterCaret))
            {
                if (ta.Text == afterText && ta.SelectionStart == afterCaret && ta.SelectionLength == 0)
                {
                    e.Handled = true;
                    int guard = 0;
                    while (ta.CanUndo && ta.Text != beforeText && guard++ < 20) ta.Undo();
                    if (ta.Text != beforeText) ta.Text = beforeText;   // 撤销栈无法还原原文（IME 不记录/已清栈）时兜底写回
                    ta.Select(beforeStart, beforeLen);
                    return;
                }
                if (ta.Text == beforeText && ta.SelectionStart == beforeStart && ta.SelectionLength == beforeLen)
                {
                    e.Handled = true;
                    int g2 = 0;
                    while (ta.CanUndo && ta.Text == beforeText && g2++ < 20) ta.Undo();
                    return;
                }
            }
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                // 空框内按 Enter 不做任何操作：避免连续产生多个空框，
                // 也避免失焦删除空框后新框索引错位。
                if (string.IsNullOrWhiteSpace(ta.Text))
                {
                    e.Handled = true;
                    return;
                }
                e.Handled = true;
                InsertItem(getRowIndex(), getIndex() + 1);
            }
        };
        ta.TextChanged += (_, _) => getField().Items[getIndex()] = ta.Text;
        ta.LostKeyboardFocus += (_, e) =>
        {
            if (_suppressReclaim) return;
            if (_suppressItemRemove) return;   // 移动事务中：被移动条目移出视觉树触发的失焦不执行删除
            int idx = getIndex();
            int ri = getRowIndex();
            Logging.Debug($"ItemLostFocus ri={ri} idx={idx} empty={string.IsNullOrWhiteSpace(ta.Text)} count={getField().Items.Count} newFocus={e.NewFocus?.GetType().Name ?? "null"}");
            // 字段内非唯一的空内容框（新增插入或手动清空产生）统一在失焦后删除、编号自动重排；
            // 该字段仅剩这一个内容框时保留（watermark 态），与 × 的"仅一条清空"语义一致。
            if (string.IsNullOrWhiteSpace(ta.Text)
                && getField().Items.Count > 1)
            {
                Logging.Debug($"ItemEmptyRemoved ri={ri} idx={idx}");
                getField().Items.RemoveAt(idx);
                RemoveItemUi(ri, idx);
            }
        };
        // 空框失焦即删（点击转移焦点本身就会触发空框失焦删除）

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
            if (getField().Items.Count == 1)
            {
                // 仅一条：清空文本，并把光标聚焦到被清空的这个框
                getField().Items[0] = "";
                ta.Text = "";
                ta.Focus();
                Keyboard.Focus(ta);
            }
            else
            {
                // 多条：删除该框后，删的是第一条则聚焦新的第一条（原下一条），否则聚焦上一条
                getField().Items.RemoveAt(idx);
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
        // 拖拽落点：条目行——鼠标在行上半插其前、下半插其后；整行高亮指示。
        // Preview 隧道事件先于内容框 TextBox 收到（TextBox 自身会吞冒泡拖放），且仅处理本应用的条目拖拽
        // （DataObject 格式 PGItemDrag），文本拖拽等外部拖放仍放行给 TextBox。
        item.PreviewDragOver += (_, e) =>
        {
            if (_current == null || !e.Data.GetDataPresent("PGItemDrag")) return;
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            SetDragHighlight(item);
        };
        item.PreviewDrop += (_, e) =>
        {
            if (_current == null || !e.Data.GetDataPresent("PGItemDrag")) return;
            e.Handled = true;
            if (e.Data.GetData("PGItemDrag") is not int[] data || data.Length != 2) return;
            var pos = e.GetPosition(item);
            int tr = getRowIndex();
            int ti = getIndex() + (pos.Y >= item.ActualHeight / 2 ? 1 : 0);
            if (tr == data[0] && ti > data[1]) ti--;   // 同字段目标在源之后：换算为移除后索引
            ClearDragHighlight();
            if (MoveItemTo(data[0], data[1], tr, ti))
                FocusItemInput(tr, ti);
        };
        item.DragLeave += (_, e) =>
        {
            // 进入子元素（内容框）也会触发父 DragLeave：仅当鼠标真正移出本行矩形才清除高亮
            if (_dragHighlightItem != item) return;
            var pos = e.GetPosition(item);
            if (pos.X < 0 || pos.Y < 0 || pos.X > item.ActualWidth || pos.Y > item.ActualHeight)
                ClearDragHighlight();
        };
        return item;
    }

    /// <summary>在指定位置增量插入一个空内容框 UI，不重建其他行。</summary>
    private void InsertItem(int rowIndex, int itemIndex)
    {
        if (_current == null) return;
        var f = _current.Fields[rowIndex];
        f.Items.Insert(itemIndex, "");

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

    // ---------------- 字段编辑区焦点定位（键盘快捷键用） ----------------

    /// <summary>定位键盘焦点所在字段行（字段名框或内容框内）；不在字段编辑区返回 -1。</summary>
    private int GetFocusedRowIndex()
    {
        if (Keyboard.FocusedElement is not DependencyObject fo) return -1;
        var rows = FieldHost.Children.OfType<Grid>().ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            var head = rows[i].Children.OfType<Grid>().FirstOrDefault();
            if (head?.Children.OfType<TextBox>().FirstOrDefault() is TextBox nb && nb == fo) return i;
            var right = rows[i].Children.OfType<StackPanel>().FirstOrDefault();
            if (right != null && right.Children.OfType<Grid>().Any(g => g.Children.OfType<AutoGrowTextBox>().Any(t => t == fo)))
                return i;
        }
        return -1;
    }

    /// <summary>定位键盘焦点所在字段行与条目索引（焦点须在内容框内）；否则返回 false。</summary>
    private bool TryGetFocusedItem(out int rowIndex, out int itemIndex)
    {
        rowIndex = -1;
        itemIndex = -1;
        if (Keyboard.FocusedElement is not AutoGrowTextBox ta) return false;
        var rows = FieldHost.Children.OfType<Grid>().ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            var right = rows[i].Children.OfType<StackPanel>().FirstOrDefault();
            if (right == null) continue;
            for (int j = 0; j < right.Children.Count; j++)
            {
                if (right.Children[j] is Grid g && g.Children.OfType<AutoGrowTextBox>().FirstOrDefault() == ta)
                {
                    rowIndex = i;
                    itemIndex = j;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 按"逻辑相邻位置"移动条目（支持跨字段，Alt+↑/↓ 调序）：
    /// 字段内：与相邻条目交换；上移越字段顶 → 进入上一字段末尾，下移越字段底 → 进入下一字段开头；
    /// 全局边界（首字段首条上移 / 末字段末条下移）：无操作。移动后焦点跟随条目到新位置。
    /// </summary>
    private void MoveItem(int fromRow, int fromIdx, int delta)
    {
        if (_current == null) return;
        var fields = _current.Fields;
        if (fromRow < 0 || fromRow >= fields.Count) return;
        var src = fields[fromRow];
        if (fromIdx < 0 || fromIdx >= src.Items.Count) return;

        // 同字段内相邻移动
        int sameTarget = fromIdx + delta;
        if (sameTarget >= 0 && sameTarget < src.Items.Count)
        {
            MoveItemTo(fromRow, fromIdx, fromRow, sameTarget);
            FocusItemInput(fromRow, sameTarget);
            return;
        }

        // 跨字段：上移溢出 → 上一字段末尾；下移溢出 → 下一字段开头
        int toRow = fromRow + (delta > 0 ? 1 : -1);
        if (toRow < 0 || toRow >= fields.Count) return;   // 全局边界：无操作
        int toIdx = delta > 0 ? 0 : fields[toRow].Items.Count;

        MoveItemTo(fromRow, fromIdx, toRow, toIdx);
        FocusItemInput(toRow, toIdx);
    }

    /// <summary>
    /// 把条目从任意位置移动到任意位置（键盘 Alt+↑/↓ 与鼠标拖拽共用的核心）：
    /// 数据与 UI 同步移除/插入；同字段目标在源之后时目标索引前移 1；源字段仅剩此条目且被移走 →
    /// 在原位置补一条空条目（维持"每字段至少一条"不变量）；移动事务内置 _suppressItemRemove，
    /// 抑制被移动条目移出视觉树触发失焦导致的误删（空条目场景）；最后重编号受影响栏。
    /// 不做焦点处理：调用方在完成后 FocusItemInput 跟随。返回是否实际移动（false = 无效/原位）。
    /// </summary>
    private bool MoveItemTo(int fromRow, int fromIdx, int toRow, int toIdx)
    {
        if (_current == null) return false;
        var fields = _current.Fields;
        if (fromRow < 0 || fromRow >= fields.Count || toRow < 0 || toRow >= fields.Count) return false;
        var src = fields[fromRow];
        if (fromIdx < 0 || fromIdx >= src.Items.Count) return false;
        if (toIdx < 0 || toIdx > fields[toRow].Items.Count) return false;

        var grids = FieldHost.Children.OfType<Grid>().ToList();
        if (fromRow >= grids.Count || toRow >= grids.Count) return false;
        var fromRight = grids[fromRow].Children.OfType<StackPanel>().FirstOrDefault();
        var toRight = grids[toRow].Children.OfType<StackPanel>().FirstOrDefault();
        if (fromRight == null || toRight == null || fromIdx >= fromRight.Children.Count) return false;
        if (toIdx > toRight.Children.Count) return false;

        // 同字段原位：无操作（目标索引统一为"移除后语义"——同字段且目标在源之后时由调用方换算）
        if (fromRow == toRow && toIdx == fromIdx) return false;

        _suppressItemRemove = true;
        try
        {
            var v = src.Items[fromIdx];
            src.Items.RemoveAt(fromIdx);
            fields[toRow].Items.Insert(toIdx, v);

            var ui = fromRight.Children[fromIdx];
            fromRight.Children.RemoveAt(fromIdx);
            toRight.Children.Insert(toIdx, ui);

            // 源字段变空 → 原位置补一条空条目（保持每字段至少一条的不变量）
            if (src.Items.Count == 0)
            {
                src.Items.Add("");
                var newItem = RenderItem(fromRow, src, 0);
                fromRight.Children.Insert(0, newItem);
            }
        }
        finally
        {
            _suppressItemRemove = false;
        }

        RenumberItems(fromRight);
        if (fromRight != toRight) RenumberItems(toRight);
        return true;
    }

    // ---------------- 鼠标拖拽调序（序号列把手；复用 MoveItemTo 核心） ----------------

    /// <summary>拖拽落点高亮：恢复上一个高亮行，高亮当前目标行（条目行/字段头行，原 Background 均为 Transparent 可安全恢复）。</summary>
    private void SetDragHighlight(Grid target)
    {
        if (_dragHighlightItem == target) return;
        if (_dragHighlightItem != null) _dragHighlightItem.Background = Brushes.Transparent;
        _dragHighlightItem = target;
        target.Background = DragHighlightBrush;
    }

    /// <summary>清除拖拽落点高亮（拖拽结束/取消/离开时调用）。</summary>
    private void ClearDragHighlight()
    {
        if (_dragHighlightItem != null)
        {
            _dragHighlightItem.Background = Brushes.Transparent;
            _dragHighlightItem = null;
        }
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
        // ---- 字段编辑区快捷键（全键盘操作；条件均限定焦点在字段编辑区，模板框焦点下自动放行）----
        // Alt+Enter：当前字段下方插入新字段（与字段间隙"+"同路径）。Ctrl+Shift+Enter 易触发输入法切换，
        // 已替换；WPF 中 Alt 组合键 Key 报 Key.System、实际键在 SystemKey，两种形式都兼容。
        if ((e.Key == Key.Enter || (e.Key == Key.System && e.SystemKey == Key.Enter))
            && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            int row = GetFocusedRowIndex();
            if (row >= 0)
            {
                e.Handled = true;
                AddFieldAt(row + 1);
            }
            return;
        }
        // Ctrl+D：删除当前条目（仅剩一条时清空并聚焦，语义与内容框 × 一致）
        if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control
            && TryGetFocusedItem(out int dRow, out int dIdx))
        {
            e.Handled = true;
            var f = _current!.Fields[dRow];
            if (f.Items.Count == 1)
            {
                f.Items[0] = "";
                var rowGrid = FieldHost.Children.OfType<Grid>().ElementAt(dRow);
                var right = rowGrid.Children.OfType<StackPanel>().FirstOrDefault();
                var ta = right?.Children.OfType<Grid>().FirstOrDefault()?.Children.OfType<AutoGrowTextBox>().FirstOrDefault();
                if (ta != null) { ta.Text = ""; ta.Focus(); Keyboard.Focus(ta); }
            }
            else
            {
                f.Items.RemoveAt(dIdx);
                RemoveItemUi(dRow, dIdx);
                FocusItemInput(dRow, dIdx == 0 ? 0 : dIdx - 1, caretToEnd: true);
            }
            return;
        }
        // Alt+↑/↓：上下移动当前条目（字段内交换，跨字段溢出：上移→上字段末尾、下移→下字段开头）。
        // WPF 中 Alt 组合键 Key 报 Key.System、实际键在 SystemKey，两种形式都兼容。
        {
            bool altUp = e.Key == Key.Up || (e.Key == Key.System && e.SystemKey == Key.Up);
            bool altDown = e.Key == Key.Down || (e.Key == Key.System && e.SystemKey == Key.Down);
            if ((altUp || altDown) && Keyboard.Modifiers == ModifierKeys.Alt
                && TryGetFocusedItem(out int mvRow, out int mvIdx))
            {
                e.Handled = true;
                MoveItem(mvRow, mvIdx, altDown ? 1 : -1);
                return;
            }
        }
        // Ctrl+↑/↓：跳转上一个/下一个字段的首个输入框（越界静默无操作；焦点不在字段区则放行后续逻辑）
        if ((e.Key == Key.Up || e.Key == Key.Down) && Keyboard.Modifiers == ModifierKeys.Control)
        {
            int ctrlRow = GetFocusedRowIndex();
            if (ctrlRow >= 0)
            {
                int count = _current?.Fields.Count ?? 0;
                int target = ctrlRow + (e.Key == Key.Down ? 1 : -1);
                if (target >= 0 && target < count) FocusItemInput(target, 0);
                e.Handled = true;
                return;
            }
        }
        // Ctrl+R：重置当前模板（与"重置模板"按钮一致，含确认弹窗）
        if (e.Key == Key.R && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ResetTemplate_Click(this, new RoutedEventArgs());
            return;
        }
        // 改名态：拦截所有"打开列表"的入口（箭头已拦，F4/Alt+Down 一并拦），提示先退出编辑
        if (_renaming && (e.Key == Key.F4
            || ((e.Key == Key.Down || (e.Key == Key.System && e.SystemKey == Key.Down)) && Keyboard.Modifiers == ModifierKeys.Alt)))
        {
            e.Handled = true;
            ShowToast("请先退出编辑操作，按Enter（保存）或ESC（取消）", false);
            return;
        }
        // 显示态：F4/Alt+Down 弹列表浏览（不改只读模板名）；↓ 首次弹列表，列表已开则交导航块
        if (IsDisplayMode() && (e.Key == Key.F4
            || ((e.Key == Key.Down || (e.Key == Key.System && e.SystemKey == Key.Down)) && Keyboard.Modifiers == ModifierKeys.Alt)))
        {
            e.Handled = true;
            if (!TemplateBox.IsDropDownOpen) TemplateBox.IsDropDownOpen = true;
            return;
        }
        // 显示态：↓ 首次弹列表，列表已开则交导航块。仅当键盘焦点在模板框内时触发——
        // 焦点在其他按钮（置顶/历史/设置/最小化/关闭/重命名/新建/保存/删除）按↓ 应走 WPF
        // 方向键导航在按钮间切换，而非误弹模板列表（列表在错误焦点下打开后 Esc 也无法收起）。
        if (IsDisplayMode() && e.Key == Key.Down && TemplateBox.IsKeyboardFocusWithin && !TemplateBox.IsDropDownOpen)
        {
            e.Handled = true;
            TemplateBox.IsDropDownOpen = true;
            return;
        }
        // 显示态：模板框收起状态裸 ↑ 循环切换——首条继续按 ↑ 跳到最后一条（其余上移仍走 ComboBox
        // 原生逐位切换，SelectionChanged 路径一致、焦点保持模板框；↓ 既有逻辑完全不动）。
        if (IsDisplayMode() && e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.None
            && TemplateBox.IsKeyboardFocusWithin && !TemplateBox.IsDropDownOpen
            && TemplateBox.SelectedIndex == 0)
        {
            int count = _templatesView?.Cast<object>().Count() ?? 0;
            if (count > 1)
            {
                TemplateBox.SelectedIndex = count - 1;
                e.Handled = true;
                return;
            }
        }
        // 输入态（点击文本区定位光标/键入清空后，非显示态）与改名态：裸 ↓/↑ 不做任何操作——
        // 可编辑 ComboBox 原生行为会在下拉关闭时选中首项并回填模板名（全选、触发 SelChanged 切换当前模板），
        // 二次按下再展开下拉（日志实证 SelChanged/DropDownOpened）；改名态下则直接覆盖正在输入的
        // 新模板名（日志实证 RenameTyping 文本被改写为模板名循环）。两种状态都与编辑语义冲突。
        // 下拉展开后的方向键导航仍走下方 NavBlock；显示态弹列表由上一分支覆盖；Alt+Down/F4 等显式开列表键不受影响。
        if ((e.Key == Key.Down || e.Key == Key.Up)
            && Keyboard.Modifiers == ModifierKeys.None
            && TemplateBox.IsKeyboardFocusWithin && !TemplateBox.IsDropDownOpen && !IsDisplayMode())
        {
            e.Handled = true;
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
        // 过滤态/显示态弹列表 + 下拉展开：接管方向键导航。编辑态 SelectedItem=null 时 ComboBox 原生键盘导航被 TextBox
        // 吞掉（↓ 无响应）；或导航文本同步被免疫误判恢复为过滤词。此处显式在过滤视图中移动选中：
        // SelectedIndex 变化驱动候选项 IsSelected 紫色高亮，随后用编辑态路径（SetCurrentValue）把框内文本
        // 恢复为过滤词（显示态=模板名）——导航只移高亮、不改用户输入；Enter 才确认切换。_loadingTemplates 短路
        // TextChanged/SelectionChanged，免疫不介入。
        // 条件不依赖 _filterActive/IsDisplayMode：下拉展开即代表用户意图方向键选候选——偶发回归是
        // 文本被清空/输入过程（enterInput）导致状态判定失败，方向键退化为原生导航、焦点移走、Enter 失效。
        if (!_renaming && TemplateBox.IsDropDownOpen && (e.Key == Key.Down || e.Key == Key.Up))
        {
            Logging.Debug($"NavBlock open={TemplateBox.IsDropDownOpen} filt={_filterActive} disp={IsDisplayMode()} idx={TemplateBox.SelectedIndex} count={_templatesView?.Cast<object>().Count() ?? 0}");
            int count = _templatesView?.Cast<object>().Count() ?? 0;
            if (count == 0) { e.Handled = true; return; }
            int idx = TemplateBox.SelectedIndex;   // 过滤视图内索引（Items=过滤后的视图）
            int next = e.Key == Key.Down
                ? (idx < 0 ? 0 : (idx + 1) % count)
                : (idx < 0 ? count - 1 : (idx - 1 + count) % count);
            string filterText = TemplateBox.Text;   // 导航前框内即过滤词（SetCurrentValue 恢复的编辑态文本）
            int caret = -1;
            if (TemplateBox.Template?.FindName("PART_EditableTextBox", TemplateBox) is TextBox tb)
                caret = tb.CaretIndex;   // 导航前光标位置；恢复过滤词后还原，导航不改变光标位置
            _loadingTemplates = true;
            TemplateBox.SelectedIndex = next;       // 高亮候选项（IsSelected 紫）
            TemplateBox.SetCurrentValue(ComboBox.TextProperty, filterText);   // 框内保持过滤词（编辑态路径，不触发一致性校正）
            _loadingTemplates = false;
            if (caret >= 0) SetCaretAt(caret);
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
        // Enter：仅"无修饰键"且"焦点在模板框内/下拉展开"时接管（改名确认、显示态进入、候选项确认）；
        // 焦点在内容框时放行给内容框处理（Enter 加条、Shift+Enter 换行），避免误触发 FocusFirstInput
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None
            && (TemplateBox.IsKeyboardFocusWithin || TemplateBox.IsDropDownOpen))
        {
            Logging.Debug($"EnterBranch focus={TemplateBox.IsKeyboardFocusWithin} open={TemplateBox.IsDropDownOpen} renaming={_renaming} filt={_filterActive} disp={IsDisplayMode()} sel={(TemplateBox.SelectedItem as Template)?.Name}");
            if (_renaming)
            {
                e.Handled = true;
                if (TryConfirmRename()) FocusFieldName(0);   // 改名确认后进入该模板第一个字段框
                return;   // 重名时留在改名态
            }
            if (IsDisplayMode() && !TemplateBox.IsDropDownOpen)
            {
                e.Handled = true;
                FocusFirstInput();   // 显示态 Enter（未开列表）：直接进入第一个字段
                return;
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
                // 确认切换不再依赖显示/过滤态判定：下拉展开 + 有效选中即确认（偶发回归：
                // 文本被清空/输入过程使 IsDisplayMode 为 false，Enter 只 FocusFirstInput 不切换）。
                if (TemplateBox.SelectedItem is Template sel)
                {
                    // 过滤态/显示态弹列表导航确认：切换模板（过滤态 SelectionChanged 已屏蔽自动切换）
                    _filterActive = false;
                    _loadingTemplates = true;
                    TemplateBox.Text = sel.Name;   // 回填选中模板名
                    _loadingTemplates = false;
                    RefreshFilteredList();          // 恢复全量列表（含置顶顺序）
                    UpdateTemplateBoxVisual();      // 切换后显示态：灰字
                    // 离开清理：删除空白新模板/历史临时模板 + 快照恢复——与鼠标点击切换（SelectionChanged）一致。
                    // 键盘路径此前缺失：方向键导航的 SelectionChanged 被 _filterActive && !pick 短路，
                    // Enter 确认分支只回填文本/切换 _current，未删除未保存的空白新模板
                    // （现象：重新展开下拉列表新模板仍在、再点新增提示"存在未保存的模板"）。
                    bool removedAny = LeaveCurrentTemplate();
                    if (removedAny)
                    {
                        // 删除空白/临时模板后重建视图：_templatesView 是静态快照不反映删除，重建避免列表残留已删模板
                        _loadingTemplates = true;
                        var keepSel = TemplateBox.SelectedItem;
                        _templatesView = new ListCollectionView(GetDisplayOrder().ToList());
                        _templatesView.Filter = null;
                        TemplateBox.ItemsSource = _templatesView;
                        if (keepSel != null) TemplateBox.SelectedItem = keepSel;
                        _loadingTemplates = false;
                    }
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
            try { PersistData(); }
            catch (Exception ex) { Logging.Warn($"历史记录保存失败：{ex.Message}"); }
        }

        if (mode == ActionMode.Copy)
        {
            try
            {
                // 剪贴板被其他进程瞬时占用（CLIPBRD_E_CANT_OPEN）时自动重试（最多 5 次、间隔 80ms），
                // 避免"第一次 Ctrl+Enter 复制失败、第二次才成功"
                for (int attempt = 0; attempt < 5; attempt++)
                {
                    try { Clipboard.SetText(prompt); break; }
                    catch (Exception) when (attempt < 4) { await Task.Delay(80); }
                }
                _lastCopiedPrompt = prompt;   // 复制成功后才记录，失败允许重试
                TouchRecentUse();             // 记录"最近使用"并刷新置顶
                Logging.Prompt(LogLevel.Info, "复制", _current?.Name, prompt);
                ShowToast("已复制到剪贴板");
                Keyboard.ClearFocus();        // 复制成功：光标移出内容框（失焦）
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
        try { PersistData(); }
        catch (Exception ex) { Logging.Warn($"模板使用时间保存失败：{ex.Message}"); }
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
                    RedirectStandardInput = true,
                    StandardInputEncoding = System.Text.Encoding.UTF8,   // 中文 prompt 按 UTF-8 传入，避免乱码
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                // print 模式：prompt 通过 stdin 传入（claude -p 无位置参数时读取 stdin），
                // 不再拼进命令行参数，规避 Windows 命令行 32767 字符上限
                psi.ArgumentList.Add("-p");
                using var proc = new Process { StartInfo = psi };
                proc.Start();
                await proc.StandardInput.WriteAsync(prompt);
                proc.StandardInput.Close();
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                var exited = await Task.WhenAny(proc.WaitForExitAsync(), Task.Delay(120_000));
                if (exited is not Task<bool>)
                {
                    try { proc.Kill(); }
                    catch (Exception ex) { Logging.Warn($"终止超时 claude 进程失败：{ex.Message}"); }
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
                // 离开当前模板：未保存的历史临时模板移除、当前模板从快照恢复（丢弃未保存编辑）或空白即删。
                // 若不先清理，原模板的未保存改动会泄漏进原模板——回归根因：_templatesView 是静态快照、
                // 不含临时模板，下方 SelectedItem=t 实际落空（模板框仍显示旧模板名），_loadingTemplates 又
                // 屏蔽了 SelectionChanged 的切换清理，快照恢复从未执行，未保存字段随切换被写盘。
                LeaveCurrentTemplate();
                // 基于历史快照新建临时模板，不覆盖当前模板；切走时自动删除，
                // 除非用户点了"保存到模板"（那时会 SaveSnapshot 持久化）。
                var t = new Template
                {
                    Name = $"{entry.TemplateName}（历史）",
                    Fields = DeepCopyFields(entry.Fields),
                    IsTemp = true
                };
                _data.Templates.Add(t);
                // 重建显示视图（_templatesView 是静态快照，不反映新增/删除）：与"新增模板"一致的路径，
                // 否则临时模板不在视图内，SelectedItem=t 落空、模板框仍显示旧模板名（回归现象A）
                _loadingTemplates = true;
                _filterActive = false;
                _templatesView = new ListCollectionView(GetDisplayOrder().ToList());
                _templatesView.Filter = null;
                TemplateBox.ItemsSource = _templatesView;
                TemplateBox.DisplayMemberPath = nameof(PromptGenerator.Models.Template.Name);
                TemplateBox.SelectedItem = t;
                TemplateBox.Text = t.Name;
                _loadingTemplates = false;
                _current = t;
                RenderFields();
                UpdateTemplateBoxVisual();
                FocusFirstInput();
            },
            onDelete: entry =>
            {
                try { PersistData(); }
                catch (Exception ex) { Logging.Warn($"历史删除保存失败：{ex.Message}"); }
            })
        {
            Owner = this
        };
        // 面板通过编辑/复制/✕ 等任何途径关闭后，同步清引用，保证下次点击可重新打开
        win.Closed += (_, _) => _historyWindow = null;
        _historyWindow = win;
        win.Show();
    }

    /// <summary>打开设置弹窗，保存后应用：热键重注册 + 贴边开关持久化。
    /// 设置期间禁用全局热键，避免用户在热键框按下当前热键时触发主界面显隐；
    /// 窗口关闭后统一恢复注册（保存→新热键，取消→原热键），新热键注册失败时回退注册原热键，保证热键不丢失。</summary>
    private void OpenSettings()
    {
        _hotkey?.Unregister();
        Logging.Debug("设置期间禁用全局热键");

        var dlg = new SettingsWindow(_settings.Hotkey, _settings.EdgeDockEnabled, _settings.PinRecentTemplates) { Owner = this };
        bool saved = dlg.ShowDialog() == true;

        if (_hotkey != null)
        {
            string target = saved ? dlg.HotkeyText : _settings.Hotkey;
            if (_hotkey.TryRegister(target, out var reason))
            {
                Logging.Debug($"热键注册成功：{target}");
                if (saved && !string.Equals(dlg.HotkeyText, _settings.Hotkey, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.Hotkey = dlg.HotkeyText;
                }
            }
            else
            {
                TrayIcon.ShowBalloonTip("热键注册失败", reason, Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning);
                ShowToast(reason, false);
                // 新热键注册失败：回退注册原热键，避免全局热键丢失（TryRegister 内部会先注销旧注册）
                if (!string.Equals(target, _settings.Hotkey, StringComparison.OrdinalIgnoreCase)
                    && !_hotkey.TryRegister(_settings.Hotkey, out var fallbackReason))
                {
                    Logging.Warn($"回退注册原热键失败：{fallbackReason}");
                }
            }
        }

        if (!saved) return;

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
        void FocusRow()
        {
            var rows = FieldHost.Children.OfType<Grid>().ToList();
            Logging.Debug($"FocusRow rows={rows.Count} idx={rowIndex}");
            if (rowIndex >= rows.Count) return;
            var head = rows[rowIndex].Children.OfType<Grid>().FirstOrDefault();
            var tb = head?.Children.OfType<TextBox>().FirstOrDefault();
            Logging.Debug($"FocusRow tb={tb != null} kind={tb?.GetType().Name}");
            tb?.Focus();
            Keyboard.Focus(tb);
            Logging.Debug($"FocusRow afterFocus={Keyboard.FocusedElement?.GetType().Name}");
        }
        Dispatcher.BeginInvoke(FocusRow, DispatcherPriority.Loaded);
        // 双保险：新建模板时 ItemsSource 重建/ComboBox 异步聚焦会抢回焦点（日志实证），Background 级最后聚焦确保落在字段名框
        Dispatcher.BeginInvoke(FocusRow, DispatcherPriority.Background);
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
