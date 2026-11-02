using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static DynamicIsland.Motion;

namespace DynamicIsland;

sealed class Bar
{
    const double CompactHeight = 28;
    const double StatusOpenWidth = 220, StatusOpenHeight = 72;
    const double VolumeOpenWidth = 236, VolumeOpenHeight = 28;
    const double TaskOpenWidth = 268, TaskRowHeight = 36, TaskOpenPad = 10, TaskMaxRows = 6;
    const double MinTask = 72, MaxTask = 176, MinStatus = 36, MaxStatus = 140;
    const double VolumeTrackWidth = 148;
    const float WheelVolumeStep = 0.02f;
    const double QuietVolumeBelow = 0.34, MidVolumeBelow = 0.67;
    static readonly Color BluetoothBlue = Color.FromRgb(0x0A, 0x84, 0xFF);
    static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
    static readonly Brush Faint = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
    static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));

    readonly AudioService _audio;
    readonly NetworkService _network;
    readonly BluetoothService _bluetooth = new();
    readonly TasksService _tasks = new();
    readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly ChipWindow _task, _wifi, _bluetoothChip, _volume;
    readonly Image _taskIcon = new() { Width = 16, Height = 16, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _taskName = Label(13);
    readonly Icon _wifiIcon = StatusIcon(Glyph.Wifi), _wifiOpenIcon = StatusIcon(Glyph.Wifi);
    readonly TextBlock _wifiText = Label(12);
    readonly TextBlock _wifiTitle = Title(), _wifiDetail = Detail();
    readonly Icon _btIcon = StatusIcon(Glyph.Bluetooth), _btOpenIcon = StatusIcon(Glyph.Bluetooth);
    readonly TextBlock _btText = Label(12);
    readonly TextBlock _btTitle = Title(), _btDetail = Detail();
    readonly Icon _volIcon = StatusIcon(Glyph.Loud);
    readonly Digits _volText = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    readonly Icon _volOpenIcon = StatusIcon(Glyph.Loud);
    readonly Border _volFill = new() { CornerRadius = new CornerRadius(2.5), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    readonly Digits _volOpenText = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly RowList _taskRows = new();
    readonly ChipWindow[] _all;
    float _lastVolume = -1;
    bool _lastMuted;
    IntPtr _shownTask;
    string _shownWifi = "", _shownBluetooth = "";
    bool _hidden, _started;

    public Bar(AudioService audio, NetworkService network)
    {
        _audio = audio;
        _network = network;
        Dim.Freeze();
        Faint.Freeze();
        Green.Freeze();
        Red.Freeze();

        _task = new ChipWindow("Остров приложения", ChipSide.Left, TaskCompact(), TaskOpen(), TaskOpenWidth, OpenTaskHeight((int)TaskMaxRows), MinTask, MaxTask);
        _wifi = new ChipWindow("Остров Wi-Fi", ChipSide.Right, StatusCompact(_wifiIcon, _wifiText), StatusOpen(_wifiOpenIcon, _wifiTitle, _wifiDetail), StatusOpenWidth, StatusOpenHeight, MinStatus, MaxStatus);
        _bluetoothChip = new ChipWindow("Остров Bluetooth", ChipSide.Right, StatusCompact(_btIcon, _btText), StatusOpen(_btOpenIcon, _btTitle, _btDetail), StatusOpenWidth, StatusOpenHeight, MinStatus, MaxStatus);
        _volume = new ChipWindow("Остров громкости", ChipSide.Right, VolumeCompact(), VolumeOpen(), VolumeOpenWidth, VolumeOpenHeight, 58, 88);
        _all = [_task, _wifi, _bluetoothChip, _volume];

        _task.Wheeled += step => _tasks.Cycle(-step);
        _task.OpenChanged += open => { if (open) FillTaskList(); };
        _wifi.Wheeled += _ => { };
        _bluetoothChip.Clicked += () => _ = _bluetooth.ToggleAsync();
        _volume.Wheeled += step => _audio.AdjustVolume(step * WheelVolumeStep);
        foreach (ChipWindow chip in _all) chip.CompactChanged += Place;

        _network.Changed += (_, _) => SyncWifi();
        _bluetooth.Changed += SyncBluetooth;
        _ticker.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ticker.Start();
        _ = _bluetooth.RefreshAsync(true);
        SyncWifi();
        SyncBluetooth();
        SyncVolume(true);
        SyncTask();
        ApplyShown();
        Place();
    }

    public void ApplyLook()
    {
        foreach (ChipWindow chip in _all) chip.ApplyLook();
        SyncWifi();
        SyncBluetooth();
        SyncVolume(true);
        Place();
    }

    public void SyncGlass(bool animate)
    {
        foreach (ChipWindow chip in _all) chip.SyncGlass(animate);
    }

    public void SetAway(bool hidden)
    {
        _hidden = hidden;
        ApplyShown();
    }

    public void KeepOnTop()
    {
        foreach (ChipWindow chip in _all)
            if (chip.IsVisible) chip.KeepOnTop();
    }

    public void Refresh()
    {
        SyncTask();
        _ = _bluetooth.RefreshAsync();
    }

    public void Place()
    {
        Rect work = SystemParameters.WorkArea;
        ApplyShown();
        if (_task.IsVisible) _task.Place(work, ChipSide.Left, 0, [_task.CompactWidth]);

        ChipWindow[] right = RightChips();
        double[] widths = right.Select(chip => chip.CompactWidth).ToArray();
        for (int i = 0; i < right.Length; i++) right[i].Place(work, ChipSide.Right, i, widths);
    }

    public void Shutdown()
    {
        _ticker.Stop();
        foreach (ChipWindow chip in _all) chip.Shutdown();
    }

    void Tick()
    {
        SyncVolume(false);
        SyncTask();
        _ = _bluetooth.RefreshAsync();
        foreach (ChipWindow chip in _all)
            if (chip.IsVisible) chip.FitCompact();
    }

    void ApplyShown()
    {
        Show(_task, Settings.Bar && Settings.BarTask);
        Show(_wifi, Settings.Bar && Settings.BarWifi);
        Show(_bluetoothChip, Settings.Bar && Settings.BarBluetooth);
        Show(_volume, Settings.Bar && Settings.BarVolume);
        foreach (ChipWindow chip in _all)
            if (chip.IsVisible) chip.SetAway(_hidden);
    }

    void Show(ChipWindow chip, bool shown)
    {
        if (shown == chip.IsVisible) return;
        if (shown) chip.Show();
        else
        {
            chip.Collapse();
            chip.Hide();
        }
    }

    ChipWindow[] RightChips() => new[] { _volume, _bluetoothChip, _wifi }.Where(chip => chip.IsVisible).ToArray();

    void SyncVolume(bool first)
    {
        if (!_audio.TryGetVolume(out float level, out bool muted)) return;
        if (!first && Math.Abs(level - _lastVolume) < 0.004 && muted == _lastMuted) return;
        _lastVolume = level;
        _lastMuted = muted;

        int percent = (int)Math.Round(level * 100);
        Glyph glyph = muted || percent == 0 ? Glyph.Mute : level < QuietVolumeBelow ? Glyph.Quiet : level < MidVolumeBelow ? Glyph.Mid : Glyph.Loud;
        _volIcon.Kind = _volOpenIcon.Kind = glyph;
        _volText.Text = _volOpenText.Text = percent.ToString();
        _volFill.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(muted ? 0 : VolumeTrackWidth * level, Ms(first ? 0 : 140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        _volume.FitCompact();
        _volume.Tint(Settings.Rim && muted ? Red.Color : null);
    }

    void SyncWifi()
    {
        NetworkService.State net = _network.Current;
        Glyph glyph = net.Link switch
        {
            NetworkService.Link.None => Glyph.Offline,
            NetworkService.Link.Wired => Glyph.Wired,
            NetworkService.Link.Wifi when net.Bars <= 1 => Glyph.WifiLow,
            NetworkService.Link.Wifi when net.Bars <= 3 => Glyph.WifiMid,
            NetworkService.Link.Wifi => Glyph.Wifi,
            _ => Glyph.Wifi
        };
        _wifiIcon.Kind = _wifiOpenIcon.Kind = glyph;
        _wifiIcon.Fill = _wifiOpenIcon.Fill = net.Link == NetworkService.Link.None ? Red : Brushes.White;
        string name = net.Link == NetworkService.Link.None ? "Нет сети" : net.Name.Length > 0 ? net.Name : net.Link == NetworkService.Link.Wired ? "Ethernet" : "Wi-Fi";
        _wifiText.Text = net.Link == NetworkService.Link.None ? "" : Truncate(name, 14);
        _wifiTitle.Text = name;
        _wifiDetail.Text = net.Link == NetworkService.Link.None ? "Нет подключения"
            : net.Internet ? (net.Link == NetworkService.Link.Wifi ? "Сеть · " + net.Bars + "/5" : "Подключено")
            : "Без интернета";
        _wifi.Tint(Settings.Rim && net.Link != NetworkService.Link.None && net.Internet ? Green.Color : Settings.Rim && net.Link == NetworkService.Link.None ? Red.Color : null);
        if (name != _shownWifi)
        {
            _shownWifi = name;
            _wifi.FitCompact();
        }
    }

    void SyncBluetooth()
    {
        BluetoothService.State bt = _bluetooth.Current;
        _btIcon.Kind = _btOpenIcon.Kind = Glyph.Bluetooth;
        _btIcon.Fill = _btOpenIcon.Fill = bt.On ? Brushes.White : Faint;
        string name = !bt.Present ? "Нет" : !bt.On ? "" : bt.Device.Length > 0 ? bt.Device : "Вкл";
        _btText.Text = Truncate(name, 14);
        _btTitle.Text = !bt.Present ? "Bluetooth" : bt.On ? "Bluetooth включён" : "Bluetooth выключен";
        _btDetail.Text = !bt.Present ? "Адаптер не найден" : bt.Device.Length > 0 ? bt.Device : bt.On ? "Нет устройств" : "Нажмите, чтобы включить";
        _bluetoothChip.Tint(Settings.Rim && bt.On ? BluetoothBlue : null);
        if (name != _shownBluetooth)
        {
            _shownBluetooth = name;
            _bluetoothChip.FitCompact();
        }
    }

    void SyncTask()
    {
        TasksService.Entry? now = _tasks.Foreground();
        IntPtr hwnd = now?.Hwnd ?? IntPtr.Zero;
        string name = now?.App ?? "Рабочий стол";
        _taskName.Text = name;
        _taskIcon.Source = now?.Icon;
        _taskIcon.SetVisible(now?.Icon != null);
        if (hwnd != _shownTask)
        {
            _shownTask = hwnd;
            _task.FitCompact();
        }
        if (_task.IsOpen) FillTaskList();
    }

    void FillTaskList()
    {
        List<TasksService.Entry> list = _tasks.Snapshot((int)TaskMaxRows);
        if (list.Count == 0)
        {
            if (_taskRows.Children.Count != 1 || _taskRows.Children[0] is not TextBlock)
            {
                _taskRows.Children.Clear();
                _taskRows.Children.Add(new TextBlock
                {
                    Text = "Нет окон",
                    Foreground = Dim,
                    FontSize = 13,
                    Margin = new Thickness(12, 10, 12, 0)
                });
            }
            return;
        }
        while (_taskRows.Children.Count > list.Count) _taskRows.Children.RemoveAt(_taskRows.Children.Count - 1);
        for (int i = 0; i < list.Count; i++)
        {
            TasksService.Entry entry = list[i];
            if (i < _taskRows.Children.Count && _taskRows.Children[i] is Button old && Equals(old.Tag, entry.Hwnd))
            {
                PaintTaskRow(old, entry);
                continue;
            }
            var row = new Button
            {
                Focusable = false,
                Height = TaskRowHeight,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(10, 0, 10, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = entry.Hwnd,
                Template = TaskRowTemplate()
            };
            row.Click += (_, _) =>
            {
                _tasks.Activate(entry.Hwnd);
                _task.Collapse();
            };
            PaintTaskRow(row, entry);
            if (i < _taskRows.Children.Count) _taskRows.Children[i] = row;
            else _taskRows.Children.Add(row);
        }
    }

    void PaintTaskRow(Button row, TasksService.Entry entry)
    {
        row.Tag = entry.Hwnd;
        row.Content = new Grid
        {
            Children =
            {
                new Image { Source = entry.Icon, Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = entry.App,
                    FontSize = 13,
                    FontWeight = entry.Hwnd == _shownTask ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = entry.Hwnd == _shownTask ? Brushes.White : Dim,
                    Margin = new Thickness(26, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            }
        };
    }

    static ControlTemplate TaskRowTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        return template;
    }

    FrameworkElement TaskCompact()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _taskName.Margin = new Thickness(8, 0, 12, 0);
        _taskName.MaxWidth = 120;
        row.Children.Add(_taskIcon);
        row.Children.Add(_taskName);
        return Box(row);
    }

    FrameworkElement TaskOpen()
    {
        _taskRows.Margin = new Thickness(6, 6, 6, 6);
        return Box(_taskRows, TaskOpenWidth, OpenTaskHeight((int)TaskMaxRows));
    }

    static double OpenTaskHeight(int rows) => TaskOpenPad * 2 + Math.Max(rows, 1) * TaskRowHeight;

    FrameworkElement VolumeCompact()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _volIcon.Margin = new Thickness(10, 0, 6, 0);
        row.Children.Add(_volIcon);
        row.Children.Add(_volText);
        return Box(row);
    }

    FrameworkElement VolumeOpen()
    {
        var grid = new Grid { Width = VolumeOpenWidth, Height = VolumeOpenHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(VolumeTrackWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(_volOpenIcon);
        var track = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) });
        track.Children.Add(_volFill);
        Grid.SetColumn(track, 1);
        Grid.SetColumn(_volOpenText, 2);
        grid.Children.Add(track);
        grid.Children.Add(_volOpenText);
        _volOpenIcon.MouseLeftButtonUp += (_, e) => { _audio.ToggleMute(); e.Handled = true; };
        return grid;
    }

    static FrameworkElement StatusCompact(Icon icon, TextBlock text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        icon.Margin = new Thickness(10, 0, 0, 0);
        text.Margin = new Thickness(6, 0, 10, 0);
        row.Children.Add(icon);
        row.Children.Add(text);
        return Box(row);
    }

    static FrameworkElement StatusOpen(Icon icon, TextBlock title, TextBlock detail)
    {
        var grid = new Grid { Width = StatusOpenWidth, Height = StatusOpenHeight };
        icon.Width = 22;
        icon.Height = 22;
        icon.HorizontalAlignment = HorizontalAlignment.Left;
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(16, 0, 0, 0);
        var copy = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(48, 0, 16, 0) };
        copy.Children.Add(title);
        copy.Children.Add(detail);
        grid.Children.Add(icon);
        grid.Children.Add(copy);
        return grid;
    }

    static Grid Box(UIElement child, double? width = null, double? height = null)
    {
        var box = new Grid { Height = height ?? CompactHeight };
        if (width is { } w) box.Width = w;
        box.Children.Add(child);
        return box;
    }

    static Icon StatusIcon(Glyph kind) => new()
    {
        Kind = kind,
        Width = 16,
        Height = 16,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center
    };

    static TextBlock Label(double size) => new()
    {
        FontSize = size,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    static TextBlock Title() => new()
    {
        FontSize = 13.5,
        FontWeight = FontWeights.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    static TextBlock Detail() => new()
    {
        FontSize = 12,
        Foreground = Dim,
        Margin = new Thickness(0, 2, 0, 0),
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    static string Truncate(string text, int letters) =>
        text.Length <= letters ? text : text[..Math.Max(0, letters - 1)].TrimEnd() + "…";
}
