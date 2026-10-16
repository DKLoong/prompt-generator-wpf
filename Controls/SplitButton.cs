using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PromptGenerator.Controls;

public enum ActionMode { Copy, Submit }

/// <summary>
/// 底部"生成并复制/生成并提交"分割按钮：
/// 左半为执行区（当前模式文案），右半 36px 正方形触发区（▼ 箭头 17px，垂直居中）点击展开菜单。
/// 选中菜单项即切换模式并执行。
/// </summary>
public class SplitButton : UserControl
{
    public event EventHandler<ActionMode>? Executed;

    private ActionMode _mode = ActionMode.Copy;

    public SplitButton()
    {
        var root = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(0x7c, 0x6c, 0xf0)),
            Height = 36,
            Cursor = System.Windows.Input.Cursors.Hand,
            ClipToBounds = true
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        _leftBtn = new Button
        {
            Content = ModeText(ActionMode.Copy),
            Background = Brushes.Transparent,
            Foreground = Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null,
            Template = BuildButtonTemplate(new CornerRadius(6, 0, 0, 6))
        };
        _leftBtn.Click += (_, _) =>
        {
            Keyboard.ClearFocus();   // 点击后立即失焦，避免 IsKeyboardFocused 高亮残留
            Executed?.Invoke(this, _mode);
        };

        _rightBtn = new Button
        {
            Content = "▼",
            FontSize = 12,
            Foreground = Brushes.White,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            FocusVisualStyle = null,
            Template = BuildButtonTemplate(new CornerRadius(0, 6, 6, 0))
        };
        _rightBtn.Click += (_, _) => ShowMenu();

        grid.Children.Add(_leftBtn);
        grid.Children.Add(_rightBtn);
        Grid.SetColumn(_rightBtn, 1);

        // 两列之间的 1px 分隔竖线（原来由默认 Button 模板绘制，自定义模板后需手动补回）
        var divider = new Border
        {
            Width = 1,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromArgb(0x55, 0xff, 0xff, 0xff)),
            IsHitTestVisible = false
        };
        Grid.SetColumn(divider, 0);
        divider.HorizontalAlignment = HorizontalAlignment.Right;
        grid.Children.Add(divider);

        root.Child = grid;
        Content = root;
    }

    /// <summary>生成透明底、无边框的按钮模板；hover 时叠淡紫高亮（白 25% 叠加在紫底上），圆角与按钮所在半区一致。</summary>
    private static ControlTemplate BuildButtonTemplate(CornerRadius radius)
    {
        var templ = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(0));
        border.SetValue(Border.CornerRadiusProperty, radius);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        presenter.SetValue(ContentPresenter.MarginProperty, new Thickness(8, 0, 8, 0));
        border.AppendChild(presenter);

        templ.VisualTree = border;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x40, 0xff, 0xff, 0xff)), "bd"));
        templ.Triggers.Add(hover);

        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x60, 0xff, 0xff, 0xff)), "bd"));
        templ.Triggers.Add(pressed);

        var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x40, 0xff, 0xff, 0xff)), "bd"));
        templ.Triggers.Add(focused);

        return templ;
    }

    private readonly Button _leftBtn;
    private readonly Button _rightBtn;

    private static string ModeText(ActionMode m) => m == ActionMode.Copy ? "生成并复制" : "生成并提交";

    private void ShowMenu()
    {
        var menu = new ContextMenu();
        var miCopy = new MenuItem { Header = "生成并复制", IsCheckable = true, IsChecked = _mode == ActionMode.Copy };
        var miSubmit = new MenuItem { Header = "生成并提交", IsCheckable = true, IsChecked = _mode == ActionMode.Submit };
        miCopy.Click += (_, _) => { _mode = ActionMode.Copy; _leftBtn.Content = ModeText(_mode); Executed?.Invoke(this, _mode); };
        miSubmit.Click += (_, _) => { _mode = ActionMode.Submit; _leftBtn.Content = ModeText(_mode); Executed?.Invoke(this, _mode); };
        menu.Items.Add(miCopy);
        menu.Items.Add(miSubmit);
        menu.PlacementTarget = _rightBtn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
