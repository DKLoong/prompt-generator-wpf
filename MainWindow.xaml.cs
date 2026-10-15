using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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

    public MainWindow()
    {
        InitializeComponent();
        _data = _store.Load();
        _settingsStore = new SettingsStore(GetDataDir());
        _settings = _settingsStore.Load();
        ActionButton.Executed += OnActionExecuted;
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        TrayIcon.IconSource = new BitmapImage(
            new Uri("pack://application:,,,/Assets/app-icon.png", UriKind.Absolute));
        // 加载后对每个已有模板存快照
        foreach (var t in _data.Templates) SaveSnapshot(t);
        Loaded += OnLoaded;
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
        }
        else
        {
            WindowState = WindowState.Normal;
            Show();
            Activate();
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

        double w = _settings.WindowWidth > 0 ? _settings.WindowWidth : 470;
        double h = _settings.WindowHeight > 0 ? _settings.WindowHeight : 700;
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
            return;
        }
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
        _expanded = false;
        _leftAt = DateTime.MinValue;
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

    private static Style? FindIconBtnStyle() =>
        Application.Current.TryFindResource("IconBtn") as Style;

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
        TemplateBox.ItemsSource = _data.Templates;
        TemplateBox.DisplayMemberPath = nameof(PromptGenerator.Models.Template.Name);
        if (_data.Templates.Count > 0) TemplateBox.SelectedIndex = 0;
        _loadingTemplates = false;
        LoadCurrentTemplate();
    }

    private void TemplateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTemplates) return;
        // 离开当前模板前：
        // - 有快照（保存过的模板）：从快照恢复，丢弃未保存的编辑
        // - 无快照（新建未保存）：空白则删除，有内容则保留在内存
        if (_current != null)
        {
            if (_savedSnapshots.ContainsKey(_current.Id))
                RestoreFromSnapshot(_current);
            else if (IsBlankNewTemplate(_current))
            {
                _data.Templates.Remove(_current);
                try { _store.Save(_data); } catch { }
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

    private void RestoreFromSnapshot(Template t)
    {
        if (!_savedSnapshots.TryGetValue(t.Id, out var snap)) return;
        t.Name = snap.Name;
        t.Fields.Clear();
        foreach (var f in snap.Fields)
            t.Fields.Add(new Field { Name = f.Name, Items = new List<string>(f.Items) });
    }

    /// <summary>可编辑下拉框：用户直接改模板名 → 写回当前模板。</summary>
    private void TemplateBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingTemplates || _current == null) return;
        string name = TemplateBox.Text.Trim();
        if (!string.IsNullOrEmpty(name) && _current.Name != name)
            _current.Name = name;
    }

    private void LoadCurrentTemplate()
    {
        _current = TemplateBox.SelectedItem as Template;
        RenderFields();
    }

    private void NewTemplate_Click(object sender, RoutedEventArgs e)
    {
        var t = new Template { Name = "新模板" };
        t.Fields.Add(new Field { Name = "", Items = { "" } });
        _data.Templates.Add(t);
        _loadingTemplates = true;
        TemplateBox.SelectedItem = t;
        _loadingTemplates = false;
        _current = t;
        RenderFields();
        FocusFirstInput();
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
        _current = null;
        try { _store.Save(_data); } catch { }
        RefreshTemplateBox();
        TemplateBox.Focus();
        ShowToast("已删除模板");
    }

    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateDup())
        {
            ShowToast("字段名重复，无法保存", false);
            return;
        }
        if (_current == null) return;
        try
        {
            _store.Save(_data);
            SaveSnapshot(_current);   // 更新已保存快照
            ShowToast("已保存到模板");
        }
        catch (Exception ex)
        {
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

    /// <summary>增量移除指定位置的字段行及其 RowGap。</summary>
    private void RemoveFieldAt(int rowIndex)
    {
        if (_current == null) return;
        _current.Fields.RemoveAt(rowIndex);
        int pos = 2 * rowIndex;
        if (pos < FieldHost.Children.Count) FieldHost.Children.RemoveAt(pos); // RowGap
        if (pos < FieldHost.Children.Count) FieldHost.Children.RemoveAt(pos); // Row
        UpdateDup();
    }

    private UIElement RenderRow(int rowIndex, Field field)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(127) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 动态读取本行在 FieldHost.Children 中的实际位置（字段行在偶数索引）
        Func<int> getRowIndex = () =>
        {
            int idx = FieldHost.Children.IndexOf(grid);
            return idx >= 0 ? idx / 2 : rowIndex;
        };

        // 左列：× + 字段名（内部垂直居中，整体贴第一条内容框顶端）
        var left = new StackPanel { Orientation = Orientation.Horizontal, Height = 32, VerticalAlignment = VerticalAlignment.Top };
        var delRow = new Button
        {
            Content = "×",
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9a, 0x9b, 0xb5)),
            VerticalAlignment = VerticalAlignment.Center,
            IsTabStop = false,
            ToolTip = "删除该字段"
        };
        delRow.Style = FindIconBtnStyle();
        delRow.Click += (_, _) =>
        {
            if (_current == null) return;
            RemoveFieldAt(getRowIndex());
        };

        var nameBox = new TextBox
        {
            Width = 103,
            Height = 32,
            FontSize = 13,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(4, 3, 4, 3),
            Text = field.Name
        };
        nameBox.Style = FindFieldInputStyle();
        WatermarkService.SetWatermark(nameBox, "标题，如“要求”");
        nameBox.TextChanged += (_, _) =>
        {
            field.Name = nameBox.Text;
            UpdateDup();
        };

        left.Children.Add(delRow);
        left.Children.Add(nameBox);

        // 右列：子项列表
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int j = 0; j < field.Items.Count; j++)
        {
            right.Children.Add(RenderItem(rowIndex, field, j));
        }

        grid.Children.Add(left);
        grid.Children.Add(right);
        Grid.SetColumn(right, 1);
        return grid;
    }

    private UIElement RenderItem(int rowIndex, Field field, int itemIndex)
    {
        var item = new Grid {
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 6)
        };
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
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
            Margin = new Thickness(0, 7, 0, 0),
            TextAlignment = TextAlignment.Right
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

        var delItem = new Button
        {
            Content = "×",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9a, 0x9b, 0xb5)),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Visible,
            IsTabStop = false,
            ToolTip = "删除此项"
        };
        delItem.Style = FindIconBtnStyle();
        delItem.Click += (_, _) =>
        {
            int idx = getIndex();
            if (field.Items.Count == 1)
            {
                field.Items[0] = "";
                ta.Text = "";
            }
            else { field.Items.RemoveAt(idx); RemoveItemUi(getRowIndex(), idx); }
            FocusFirstInput();
        };

        item.Children.Add(num);
        item.Children.Add(ta);
        item.Children.Add(delItem);
        Grid.SetColumn(ta, 1);
        Grid.SetColumn(delItem, 2);
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
        var right = rowGrid.Children.OfType<StackPanel>().ElementAt(1);
        var newItem = RenderItem(rowIndex, f, itemIndex);
        right.Children.Insert(itemIndex, newItem);
        RenumberItems(right);

        FocusItemInput(rowIndex, itemIndex);
    }

    /// <summary>增量移除指定位置的内容框 UI（不重建其他行）。</summary>
    private void RemoveItemUi(int rowIndex, int itemIndex)
    {
        var rowGrid = FieldHost.Children.OfType<Grid>().ElementAt(rowIndex);
        var right = rowGrid.Children.OfType<StackPanel>().ElementAt(1);
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
        foreach (var tb in EnumerateFieldNameBoxes())
        {
            bool isDup = dupSet.Contains(tb.Text.Trim());
            tb.BorderBrush = isDup
                ? new SolidColorBrush(Color.FromRgb(0xe5, 0x53, 0x4b))
                : new SolidColorBrush(Color.FromRgb(0x3c, 0x3d, 0x57));
            tb.Foreground = isDup
                ? new SolidColorBrush(Color.FromRgb(0xe5, 0x53, 0x4b))
                : new SolidColorBrush(Color.FromRgb(0xe5, 0xe6, 0xf0));
            if (isDup) dup = true;
        }
        return dup;
    }

    private IEnumerable<TextBox> EnumerateFieldNameBoxes()
    {
        var result = new List<TextBox>();
        foreach (var child in FieldHost.Children)
        {
            if (child is Grid row && row.Children.Count > 0 && row.Children[0] is StackPanel left)
            {
                foreach (var c in left.Children)
                {
                    if (c is TextBox tb) result.Add(tb);
                }
            }
        }
        return result;
    }

    // ---------------- 键盘全局快捷键 ----------------

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+Enter：生成并复制
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OnActionExecuted(this, ActionMode.Copy);
            return;
        }
        // 模板下拉展开时按 Enter：选中当前项并把焦点移到第一个字段框
        if (e.Key == Key.Enter && TemplateBox.IsDropDownOpen)
        {
            TemplateBox.IsDropDownOpen = false;
            FocusFirstInput();
        }
    }

    // ---------------- 生成与执行 ----------------

    private void OnActionExecuted(object? sender, ActionMode mode)
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
        if (mode == ActionMode.Copy)
        {
            try
            {
                Clipboard.SetText(prompt);
                ShowToast("已复制到剪贴板");
            }
            catch (Exception ex)
            {
                ShowToast($"复制失败：{ex.Message}", false);
            }
        }
        else
        {
            SubmitPrompt(prompt);
        }
    }

    private async void SubmitPrompt(string prompt)
    {
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
            MessageBox.Show(result.Item2, result.Item1 ? "claude 返回" : "提交失败",
                MessageBoxButton.OK, result.Item1 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ShowToast($"提交失败：{ex.Message}", false);
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

    /// <summary>打开设置弹窗，保存后应用：热键重注册 + 贴边开关持久化。</summary>
    private void OpenSettings()
    {
        var dlg = new SettingsWindow(_settings.Hotkey, _settings.EdgeDockEnabled) { Owner = this };
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

        try
        {
            _settingsStore.Save(_settings);
            ShowToast("设置已保存");
        }
        catch (Exception ex)
        {
            ShowToast($"保存失败：{ex.Message}", false);
        }
    }

    // ---------------- 工具 ----------------

    private void FocusFirstInput()
    {
        Dispatcher.BeginInvoke(() =>
        {
            var first = FieldHost.Children.OfType<Grid>().FirstOrDefault();
            if (first == null) return;
            var right = first.Children.Count > 1 ? first.Children[1] as StackPanel : null;
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
            if (rowIndex < rows.Count && rows[rowIndex].Children[0] is StackPanel left)
            {
                var tb = left.Children.OfType<TextBox>().FirstOrDefault();
                tb?.Focus();
                Keyboard.Focus(tb);
                tb?.SelectAll();
            }
        }, DispatcherPriority.Loaded);
    }

    private void FocusItemInput(int rowIndex, int itemIndex)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var rows = FieldHost.Children.OfType<Grid>().ToList();
            if (rowIndex >= rows.Count) return;
            if (rows[rowIndex].Children[1] is not StackPanel right) return;
            var items = right.Children.OfType<Grid>().ToList();
            if (itemIndex < items.Count)
            {
                var ta = items[itemIndex].Children.OfType<AutoGrowTextBox>().FirstOrDefault();
                ta?.Focus();
                Keyboard.Focus(ta);
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
        Toast.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += (_, _) =>
        {
            Toast.Visibility = Visibility.Collapsed;
            _toastTimer!.Stop();
        };
        _toastTimer.Start();
    }
}
