using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using static DynamicIsland.Motion;

namespace DynamicIsland;

enum ChipSide { Left, Right }

sealed class ChipWindow : Window
{
    const double HostWidth = 360, HostHeight = 260;
    const double CompactHeight = 28, MinPill = 22;
    const double PressedScale = 0.93, HoverScale = 1.07;
    const double IntroScale = 0.3, IntroOffset = -40;
    const double OffscreenMargin = 24, MinScale = 0.01;
    const double CollapseSeconds = 0.55;
    const int LargestScale = 110, LargestGap = 24;
    const double ChipGap = 8;
    static readonly Duration TintTime = Ms(280);
    static readonly TimeSpan GlassTime = TimeSpan.FromMilliseconds(300);

    readonly Spring _width = new(CompactHeight), _height = new(CompactHeight), _radius = new(CompactHeight / 2);
    readonly Spring _scale = new(1, 320, 20), _offsetY = new(0, 260, 26);
    readonly Spring _userScale = new(Settings.BarScale / 100.0, 240, 26), _topGap = new(Settings.Gap, 240, 26);
    readonly Spring _sideShift = new(0, 280, 28), _compactWidth = new(64, 280, 26);
    readonly FrameLoop _loop;
    readonly Glass _glass;
    readonly Goo _body = new() { IsHitTestVisible = false };
    readonly Border _pill = new() { Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    readonly Grid _root, _island, _host;
    readonly Path _shadow;
    readonly ScaleTransform _islandScale = new(1, 1), _rootSize = new(1, 1);
    readonly TranslateTransform _rootMove = new();
    readonly DelayedAction _collapse;
    readonly Spring[] _springs;
    readonly double _openWidth, _openHeight;
    readonly double _minCompact, _maxCompact;

    IntPtr _hwnd;
    ChipSide _side;
    Rect _clip = Rect.Empty;
    double _clipRadius;
    bool _hovered, _pressed, _open, _away, _closing, _placed;

    public ChipWindow(string name, ChipSide side, FrameworkElement compact, FrameworkElement open,
        double openWidth, double openHeight, double minCompact, double maxCompact)
    {
        Title = name;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        UseLayoutRounding = true;
        FontFamily = new FontFamily("./Fonts/#SF Pro Text, SF Pro Text, Segoe UI Variable Display, Segoe UI");
        Foreground = Brushes.White;
        Width = HostWidth * LargestScale / 100.0;
        Height = HostHeight * LargestScale / 100.0 + LargestGap;

        Compact = compact;
        Open = open;
        _openWidth = openWidth;
        _openHeight = openHeight;
        _minCompact = minCompact;
        _maxCompact = maxCompact;
        _side = side;
        _springs = [_width, _height, _radius, _scale, _offsetY, _userScale, _topGap, _sideShift, _compactWidth];
        _loop = new FrameLoop(Advance);
        _collapse = new DelayedAction(Collapse);
        _glass = new Glass(this);

        compact.RenderTransformOrigin = open.RenderTransformOrigin = new Point(0.5, 0.5);
        compact.RenderTransform = new ScaleTransform(1, 1);
        open.RenderTransform = new ScaleTransform(1, 1);
        compact.HorizontalAlignment = open.HorizontalAlignment = side == ChipSide.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        compact.VerticalAlignment = open.VerticalAlignment = VerticalAlignment.Top;
        open.Visibility = Visibility.Collapsed;
        open.Opacity = 0;
        open.IsHitTestVisible = false;

        _shadow = new Path
        {
            Fill = Brushes.Black,
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0.55, Color = Colors.Black
            }
        };

        _host = new Grid { Children = { compact, open } };
        _island = new Grid { RenderTransformOrigin = new Point(_side == ChipSide.Left ? 0 : 1, 0), RenderTransform = _islandScale };
        _island.Children.Add(_shadow);
        _island.Children.Add(_body);
        _island.Children.Add(_pill);
        _island.Children.Add(_host);
        _island.MouseEnter += (_, _) => { _hovered = true; _collapse.Cancel(); Aim(); };
        _island.MouseLeave += (_, _) => { _hovered = _pressed = false; Aim(); if (_open) _collapse.Start(TimeSpan.FromSeconds(CollapseSeconds)); };

        _root = new Grid
        {
            Width = HostWidth,
            Height = HostHeight,
            Opacity = 0,
            HorizontalAlignment = side == ChipSide.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransformOrigin = new Point(_side == ChipSide.Left ? 0 : 1, 0),
            RenderTransform = new TransformGroup { Children = { _rootMove, _rootSize } }
        };
        _root.Children.Add(_island);
        _root.MouseLeftButtonDown += (_, e) => { _pressed = true; Aim(); e.Handled = true; };
        _root.MouseLeftButtonUp += OnClick;
        _root.MouseWheel += OnWheel;
        Content = _root;

        Loaded += OnLoaded;
        DpiChanged += (_, _) => ApplyShape();
    }

    public FrameworkElement Compact { get; }
    public FrameworkElement Open { get; }
    public double CompactWidth => Math.Max(_compactWidth.Target, _minCompact);
    public bool IsOpen => _open;
    public event Action? CompactChanged;
    public event Action? Clicked;
    public event Action<int>? Wheeled;
    public event Action<bool>? OpenChanged;

    public void FitCompact()
    {
        Compact.Measure(new Size(_maxCompact, CompactHeight));
        double width = Math.Clamp(Math.Ceiling(Compact.DesiredSize.Width), _minCompact, _maxCompact);
        if (Math.Abs(width - _compactWidth.Target) < 0.5) return;
        _compactWidth.Target = width;
        CompactChanged?.Invoke();
        Aim();
    }

    public void SetOpen(bool open)
    {
        if (open == _open) return;
        _open = open;
        if (open) _collapse.Cancel();
        OpenChanged?.Invoke(open);
        Morph();
        Aim();
    }

    public void Collapse() => SetOpen(false);

    public void Tint(Color? color) => _body.Tint(color, TintTime);

    public void ApplyLook()
    {
        _userScale.Target = Settings.BarScale / 100.0;
        _topGap.Target = Settings.Gap;
        Aim();
    }

    public void Place(Rect work, ChipSide side, int slot, IReadOnlyList<double> widthsFromEdge)
    {
        _side = side;
        _root.HorizontalAlignment = side == ChipSide.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        _island.RenderTransformOrigin = _root.RenderTransformOrigin = new Point(side == ChipSide.Left ? 0 : 1, 0);
        Compact.HorizontalAlignment = Open.HorizontalAlignment = side == ChipSide.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        Left = side == ChipSide.Left ? work.Left : work.Right - Width;
        Top = work.Top;

        double scale = Math.Max(_userScale.Value, MinScale);
        double ahead = Settings.BarInset;
        for (int i = 0; i < slot; i++) ahead += widthsFromEdge[i] * scale + ChipGap * scale;
        _sideShift.Target = (side == ChipSide.Left ? ahead : -ahead) / scale;
        _placed = true;
        KeepOnTop();
        Aim();
    }

    public void SetAway(bool hidden)
    {
        if (hidden == _away) return;
        _away = hidden;
        if (hidden) Collapse();
        Aim();
    }

    public void SyncGlass(bool animate)
    {
        if (Settings.Glass)
        {
            _glass.Show();
            _clip = Rect.Empty;
            KeepOnTop();
        }
        _body.Thin(Settings.Glass, animate ? GlassTime : TimeSpan.Zero, HideGlassOnceCovered);
        ApplyShape();
    }

    public void KeepOnTop()
    {
        if (_hwnd != IntPtr.Zero) Native.KeepOnTop(_hwnd);
        _glass.Place();
    }

    public void Shutdown()
    {
        _closing = true;
        _loop.Stop();
        _glass.Hide();
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.HideFromTaskSwitcher(_hwnd);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closing) e.Cancel = true;
        base.OnClosing(e);
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        _scale.Value = IntroScale;
        _offsetY.Value = IntroOffset;
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(260)));
        FitCompact();
        Morph(true);
        Aim();
        SyncGlass(false);
    }

    void OnClick(object sender, MouseButtonEventArgs e)
    {
        _pressed = false;
        Aim();
        e.Handled = true;
        if (_open)
        {
            Clicked?.Invoke();
            Collapse();
        }
        else SetOpen(true);
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        Wheeled?.Invoke(e.Delta > 0 ? 1 : -1);
    }

    void Morph(bool snap = false)
    {
        double w = _open ? _openWidth : _compactWidth.Value, h = _open ? _openHeight : CompactHeight;
        if (snap)
        {
            _width.Snap(w);
            _height.Snap(h);
            _radius.Snap(h / 2);
        }
        else
        {
            bool growing = _open;
            _width.Tune(growing ? 330 : 260, growing ? 22 : 27);
            _height.Tune(growing ? 220 : 400, growing ? 22 : 33);
            _width.Target = w;
            _height.Target = h;
            _radius.Target = h / 2;
        }

        FrameworkElement show = _open ? Open : Compact, hide = _open ? Compact : Open;
        hide.IsHitTestVisible = false;
        hide.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Ms(140)));
        hide.RenderTransform.AnimateScale(new DoubleAnimation(0.9, Ms(170)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } });
        show.Visibility = Visibility.Visible;
        show.IsHitTestVisible = true;
        var grow = new DoubleAnimation(1, Ms(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (show.Opacity < 0.05) grow.From = 0.86;
        show.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ms(200)));
        show.RenderTransform.AnimateScale(grow);
    }

    void Aim()
    {
        bool compact = !_open;
        _scale.Target = _pressed ? PressedScale : _hovered && compact ? HoverScale : 1;
        double h = _open ? _openHeight : CompactHeight;
        _offsetY.Target = _away ? -(h + OffscreenMargin + Settings.Gap * 100.0 / Math.Max(Settings.BarScale, 1)) : 0;
        if (!_open)
        {
            _width.Target = _compactWidth.Value;
            _height.Target = CompactHeight;
            _radius.Target = CompactHeight / 2;
        }
        _loop.Start();
    }

    bool Advance(double dt)
    {
        bool moving = false;
        foreach (Spring spring in _springs) moving |= spring.Advance(dt);
        if (!_open)
        {
            _width.Target = _compactWidth.Value;
            _height.Target = CompactHeight;
            _radius.Target = CompactHeight / 2;
        }
        ApplyShape();
        return moving;
    }

    void ApplyShape()
    {
        double w = Math.Max(_width.Value, MinPill), h = Math.Max(_height.Value, MinPill);
        double r = Math.Clamp(_radius.Value, 0, Math.Min(w, h) / 2);
        double scale = Math.Max(_scale.Value, MinScale), size = Math.Max(_userScale.Value, MinScale);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var pill = new Rect(_side == ChipSide.Left ? 0 : HostWidth - w, 0, w, h);

        _pill.Width = w;
        _pill.Height = h;
        _pill.Margin = new Thickness(pill.X, 0, 0, 0);
        _pill.CornerRadius = new CornerRadius(r);
        _shadow.Opacity = Math.Clamp((h - CompactHeight) / 40.0, 0, 1);
        if (pill != _clip || r != _clipRadius)
        {
            _clip = pill;
            _clipRadius = r;
            _host.Clip = _shadow.Data = Squircle.Of(pill, r);
        }

        Compact.Height = CompactHeight;
        Open.Width = _openWidth;
        Open.Height = _openHeight;

        _islandScale.ScaleX = _islandScale.ScaleY = scale;
        _rootSize.ScaleX = _rootSize.ScaleY = size;
        _rootMove.Y = _offsetY.Value + _topGap.Value / size;
        _rootMove.X = Math.Round(_sideShift.Value * size * dpi) / Math.Max(size * dpi, MinScale);

        _body.SetShape(pill, r, Rect.Empty);
        if (_glass.IsOn && _body.TransformToAncestor(this) is Transform place)
        {
            Matrix toPixels = place.Value;
            toPixels.Scale(dpi, dpi);
            _glass.Cut(_body.PillInside, _body.BubbleInside, toPixels);
        }
    }

    void HideGlassOnceCovered()
    {
        if (Settings.Glass || !_glass.IsOn) return;
        _glass.Hide();
        _clip = Rect.Empty;
        ApplyShape();
    }
}
