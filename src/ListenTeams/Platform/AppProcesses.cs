using System.Runtime.InteropServices;
using System.Text;
using ListenTeams.Core.Apps;

namespace ListenTeams.Platform;

/// <summary>Finds the processes of the calling applications (Teams, Discord) and their windows.</summary>
internal static class AppProcesses
{
    /// <summary>One snapshot of the running processes, shared by every lookup of a tick.</summary>
    public sealed class Snapshot
    {
        private readonly List<ProcessInfo> _processes;

        private Snapshot(List<ProcessInfo> processes)
        {
            _processes = processes;
        }

        public static Snapshot Take() => new(TakeProcesses());

        /// <summary>
        /// Top-most processes of the application. Capturing their process tree also captures
        /// the child processes that actually play the call audio (WebView2, Electron…).
        /// </summary>
        public IReadOnlyList<int> RootProcessIds(CallApp app)
        {
            var own = _processes.Where(p => CallApps.IsExecutableOf(app, p.Name)).ToDictionary(p => p.Id);
            return own.Values.Where(p => !own.ContainsKey(p.ParentId)).Select(p => p.Id).ToList();
        }

        /// <summary>Process ids of every executable of the application (root and children).</summary>
        public HashSet<int> AllProcessIds(CallApp app) =>
            _processes.Where(p => CallApps.IsExecutableOf(app, p.Name)).Select(p => p.Id).ToHashSet();
    }

    /// <summary>Visible top-level windows that belong to the given processes.</summary>
    public static List<(IntPtr Handle, string Title)> FindWindows(HashSet<int> processIds)
    {
        var windows = new List<(IntPtr, string)>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (!processIds.Contains((int)pid)) return true;
            int length = GetWindowTextLength(hwnd);
            if (length == 0) return true;
            var text = new StringBuilder(length + 1);
            GetWindowText(hwnd, text, text.Capacity);
            windows.Add((hwnd, text.ToString()));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private readonly record struct ProcessInfo(int Id, int ParentId, string Name);

    private static List<ProcessInfo> TakeProcesses()
    {
        var result = new List<ProcessInfo>();
        IntPtr snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == InvalidHandle) return result;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry)) return result;
            do
            {
                result.Add(new ProcessInfo((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile));
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return result;
    }

    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
}

/// <summary>
/// Guesses the meeting title from the Teams windows: the call opens its own window, whose
/// title is the meeting subject or the name of the person called.
/// </summary>
internal sealed class MeetingTitleTracker
{
    private static readonly string[] GenericTitles =
    [
        "microsoft teams", "teams", "chat", "conversation", "activité", "activity", "calendrier", "calendar",
        "équipes", "appels", "calls", "fichiers", "files", "onedrive", "applications", "apps", "aide", "help",
        "paramètres", "settings", "communautés", "communities", "copilot", "notifications", "notification",
        "réunion", "meeting", "appel", "call", "partage", "sharing", "écran de partage",
    ];

    private readonly HashSet<IntPtr> _windowsBeforeCall = [];

    public string? Title { get; private set; }

    public void OnCallStarted(HashSet<int> teamsProcessIds)
    {
        _windowsBeforeCall.Clear();
        Title = null;
        foreach (var (handle, _) in AppProcesses.FindWindows(teamsProcessIds))
        {
            _windowsBeforeCall.Add(handle);
        }
    }

    public void Update(HashSet<int> teamsProcessIds)
    {
        var windows = AppProcesses.FindWindows(teamsProcessIds);
        // Prefer windows opened by the call; fall back to any meaningful title.
        var candidates = windows.Where(w => !_windowsBeforeCall.Contains(w.Handle))
                                .Concat(windows.Where(w => _windowsBeforeCall.Contains(w.Handle)));
        foreach (var (_, rawTitle) in candidates)
        {
            if (Clean(rawTitle) is { } title)
            {
                Title = title;
                return;
            }
        }
    }

    internal static string? Clean(string rawTitle)
    {
        var parts = rawTitle.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                            .Where(p => !p.StartsWith("Microsoft Teams", StringComparison.OrdinalIgnoreCase))
                            .ToList();
        if (parts.Count == 0) return null;
        // Main window titles look like "Chat | Jane Doe | Microsoft Teams": skip the section name.
        if (parts.Count > 1 && GenericTitles.Contains(parts[0].ToLowerInvariant())) return null;
        string title = parts[0];
        if (GenericTitles.Contains(title.ToLowerInvariant()) || title.Length < 2) return null;
        return title;
    }
}
