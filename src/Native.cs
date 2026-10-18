using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentHub
{
    internal static class Native
    {
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;

        public const int WS_CAPTION = 0x00C00000;
        public const int WS_THICKFRAME = 0x00040000;
        public const int WS_SYSMENU = 0x00080000;
        public const int WS_MINIMIZEBOX = 0x00020000;
        public const int WS_MAXIMIZEBOX = 0x00010000;
        public const int WS_BORDER = 0x00800000;
        public const int WS_DLGFRAME = 0x00400000;
        public const int WS_VISIBLE = 0x10000000;
        public const int WS_CLIPSIBLINGS = 0x04000000;
        public const int WS_CLIPCHILDREN = 0x02000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const uint SWP_NOOWNERZORDER = 0x0200;

        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;
        public const int SW_SHOWMINIMIZED = 2;
        public const int SW_SHOWMAXIMIZED = 3;
        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;

        public const int WM_MOUSEACTIVATE = 0x0021;
        public const int MA_NOACTIVATE = 3;
        public const int WM_HOTKEY = 0x0312;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_NOREPEAT = 0x4000;

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        public const uint GW_OWNER = 4;
        public const uint GW_HWNDNEXT = 2;
        public const uint GW_HWNDPREV = 3;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
        public static readonly IntPtr HWND_TOP = IntPtr.Zero;

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr h, out RECT r);

        [DllImport("user32.dll")]
        public static extern bool ClientToScreen(IntPtr h, ref POINT p);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr h);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr h);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr h, StringBuilder s, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr h);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr h, StringBuilder s, int max);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr h, out RECT r);

        [DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr h, int idx);

        [DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr h, int idx, int val);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr h, int cmd);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr h);

        [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

        public const int AW_HIDE = 0x10000;
        public const int AW_ACTIVATE = 0x20000;
        public const int AW_SLIDE = 0x40000;
        public const int AW_HOR_POSITIVE = 0x1;
        public const int AW_HOR_NEGATIVE = 0x2;
        [DllImport("user32.dll")] public static extern bool AnimateWindow(IntPtr h, int ms, int flags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SwitchToThisWindow(IntPtr h, bool alt);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr h, uint cmd);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint a, uint b, bool attach);

        [DllImport("user32.dll")]
        public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, int vk);

        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        public static extern bool UnregisterHotKey(IntPtr h, int id);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr p, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf, int len, out int ret);

        [DllImport("advapi32.dll")]
        public static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

        [DllImport("advapi32.dll")]
        public static extern IntPtr GetSidSubAuthority(IntPtr sid, int idx);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        public static string Title(IntPtr h)
        {
            int n = GetWindowTextLength(h);
            if (n <= 0) return "";
            var sb = new StringBuilder(n + 2);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string ClassName(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
