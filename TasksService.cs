using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

sealed class TasksService
{
    public readonly record struct Entry(IntPtr Hwnd, string Title, string App, ImageSource? Icon);

    const uint GwOwner = 4;
    const int SwRestore = 9;
    const uint ProcessQueryLimited = 0x1000;
    const int TitleCapacity = 512, PathCapacity = 1024, ClassCapacity = 64;
    const int DwmwaCloaked = 14;
    const uint ShgfiIcon = 0x100, ShgfiLargeIcon = 0;
    const long WsExAppWindow = 0x00040000, WsExToolWindow = 0x00000080, WsExNoActivate = 0x08000000;
    const int IconSize = 32;
    static readonly string[] HiddenClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "XamlExplorerHostIslandWindow", "Windows.UI.Core.CoreWindow"];

    delegate bool WindowProc(IntPtr hwnd, IntPtr param);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShellFile
    {
        public IntPtr Icon;
        public int Index;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string Type;
    }

    [DllImport("user32.dll")] static extern bool EnumWindows(WindowProc proc, IntPtr param);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint relation);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShellFile info, uint size, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);

    readonly Dictionary<string, ImageSource?> _icons = [];
    readonly StringBuilder _title = new(TitleCapacity);
    readonly StringBuilder _class = new(ClassCapacity);
    int _ownProcess = Environment.ProcessId;
    IntPtr _lastReal;

    public List<Entry> Snapshot(int limit = 8)
    {
        var found = new List<Entry>();
        EnumWindows((hwnd, _) =>
        {
            if (found.Count >= limit || !IsSwitcherWindow(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint process);
            if (process == (uint)_ownProcess) return true;
            string title = _title.ToString();
            string path = ExecutableOf(process);
            found.Add(new Entry(hwnd, title, AppName(path, title), IconOf(path)));
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public Entry? Foreground()
    {
        IntPtr hwnd = Native.ForegroundWindow;
        GetWindowThreadProcessId(hwnd, out uint process);
        if (hwnd == IntPtr.Zero || process == (uint)_ownProcess)
            return Read(_lastReal);

        if (IsSwitcherWindow(hwnd)) _lastReal = hwnd;
        return Read(_lastReal);
    }

    public void Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
        SwitchToThisWindow(hwnd, true);
        _lastReal = hwnd;
    }

    public void Cycle(int step)
    {
        List<Entry> list = Snapshot(12);
        if (list.Count == 0) return;
        IntPtr now = Foreground()?.Hwnd ?? IntPtr.Zero;
        int at = list.FindIndex(entry => entry.Hwnd == now);
        if (at < 0) at = 0;
        Activate(list[(at + step % list.Count + list.Count) % list.Count].Hwnd);
    }

    Entry? Read(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsSwitcherWindow(hwnd)) return null;
        GetWindowThreadProcessId(hwnd, out uint process);
        string title = _title.ToString();
        string path = ExecutableOf(process);
        return new Entry(hwnd, title, AppName(path, title), IconOf(path));
    }

    bool IsSwitcherWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != IntPtr.Zero) return false;
        if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        long ex = GetWindowLongPtr(hwnd, -20).ToInt64();
        bool tool = (ex & WsExToolWindow) != 0, app = (ex & WsExAppWindow) != 0;
        if (tool && !app) return false;
        if ((ex & WsExNoActivate) != 0 && !app) return false;
        if (GetWindowText(hwnd, _title, _title.Capacity) <= 0) return false;
        GetClassName(hwnd, _class, _class.Capacity);
        return Array.IndexOf(HiddenClasses, _class.ToString()) < 0;
    }

    static string ExecutableOf(uint process)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimited, false, process);
        if (handle == IntPtr.Zero) return "";
        try
        {
            var path = new StringBuilder(PathCapacity);
            int size = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : "";
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    static string AppName(string path, string title)
    {
        if (path.Length == 0) return FirstWord(title);
        string file = Path.GetFileNameWithoutExtension(path);
        if (file.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
            || file.Equals("explorer", StringComparison.OrdinalIgnoreCase))
            return FirstWord(title);
        try
        {
            string? described = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(described)) return described.Trim();
        }
        catch { }
        return file;
    }

    static string FirstWord(string title)
    {
        int at = title.IndexOfAny(['-', '—', '|', '·']);
        return at > 0 ? title[..at].Trim() : title;
    }

    ImageSource? IconOf(string path)
    {
        if (path.Length == 0) return null;
        if (_icons.TryGetValue(path, out ImageSource? cached)) return cached;
        ImageSource? icon = LoadIcon(path);
        _icons[path] = icon;
        return icon;
    }

    static ImageSource? LoadIcon(string path)
    {
        var info = new ShellFile();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShellFile>(), ShgfiIcon | ShgfiLargeIcon) == IntPtr.Zero || info.Icon == IntPtr.Zero)
            return null;
        try
        {
            BitmapSource image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(IconSize, IconSize));
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return null;
        }
        finally
        {
            DestroyIcon(info.Icon);
        }
    }
}
