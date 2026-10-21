using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace PromptGenerator.Controls;

/// <summary>为 TextBox 提供占位符（Watermark）的附加属性。</summary>
public static class WatermarkService
{
    public static readonly DependencyProperty WatermarkProperty =
        DependencyProperty.RegisterAttached(
            "Watermark",
            typeof(string),
            typeof(WatermarkService),
            new FrameworkPropertyMetadata(string.Empty, OnWatermarkChanged));

    public static string GetWatermark(DependencyObject obj) => (string)obj.GetValue(WatermarkProperty);
    public static void SetWatermark(DependencyObject obj, string value) => obj.SetValue(WatermarkProperty, value);

    /// <summary>占位符起点 X 偏移（分框校准）：各输入框文本渲染起点不同（单行/多行偏移机制不一致），
    /// 不能用一个全局值对齐所有框，须按框实测校准后分别设置。</summary>
    public static readonly DependencyProperty WatermarkOffsetXProperty =
        DependencyProperty.RegisterAttached(
            "WatermarkOffsetX",
            typeof(double),
            typeof(WatermarkService),
            new PropertyMetadata(15.0));

    public static double GetWatermarkOffsetX(DependencyObject obj) => (double)obj.GetValue(WatermarkOffsetXProperty);
    public static void SetWatermarkOffsetX(DependencyObject obj, double value) => obj.SetValue(WatermarkOffsetXProperty, value);

    private static void OnWatermarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;
        tb.Loaded -= OnLoaded;
        tb.Loaded += OnLoaded;
        if (tb.IsLoaded) OnLoaded(tb, new RoutedEventArgs());
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var tb = (TextBox)sender;
        tb.Loaded -= OnLoaded;
        var adornerLayer = AdornerLayer.GetAdornerLayer(tb);
        if (adornerLayer == null) return;
        adornerLayer.Add(new WatermarkAdorner(tb, GetWatermark(tb)));
    }

    private class WatermarkAdorner : Adorner
    {
        private readonly string _text;
        private readonly TextBox _tb;
        private readonly FormattedText _ft;

        public WatermarkAdorner(TextBox tb, string text) : base(tb)
        {
            _tb = tb;
            _text = text;
            _ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                tb.FontSize,
                (Brush)Application.Current.FindResource("FaintBrush"),
                VisualTreeHelper.GetDpi(tb).PixelsPerDip);
            IsHitTestVisible = false;
            tb.TextChanged += OnTextChanged;
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e) => InvalidateVisual();

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (string.IsNullOrEmpty(_tb.Text) && _text.Length > 0)
            {
                // 每次渲染取当前主题 FaintBrush（占位符为置灰提示档），主题切换后重绘即跟随
                _ft.SetForegroundBrush((Brush)Application.Current.FindResource("FaintBrush"));
                double top = (_tb.ActualHeight - _ft.Height) / 2;
                // 占位符起点 = 该框的校准值（WatermarkOffsetX，分框设置），保证提示语与输入内容起点对齐
                dc.DrawText(_ft, new Point(GetWatermarkOffsetX(_tb), Math.Max(0, top)));
            }
        }
    }
}
