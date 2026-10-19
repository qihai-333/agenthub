using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace AgentHub
{
    internal class Takeover
    {
        public IntPtr Hwnd;
        public int Pid;
        public int Style;
        public int ExStyle;
        public Native.RECT Rect;
        public bool PlainFallback;
    }

    internal static class Host
    {
        private const int WS_MAXIMIZE = 0x01000000;

        public static bool NoStyle = false;

        private static readonly Dictionary<IntPtr, Takeover> Map = new Dictionary<IntPtr, Takeover>();

        public static string DataDir
        {
            get
            {
                // 数据目录跟着实例走：默认实例 data\，--config 实例 data-<配置名>\（见 Program.SetDataDir）
                try { if (!Directory.Exists(Program.DataDir)) Directory.CreateDirectory(Program.DataDir); }
                catch { }
                return Program.DataDir;
            }
        }

        public static string JournalPath
        {
            get { return Path.Combine(DataDir, "journal.txt"); }
        }

        private static HashSet<int> PidsOf(Agent a)
        {
            var set = new HashSet<int>();
            foreach (string n in a.ProcNames())
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(n))
                    {
                        set.Add(p.Id);
                        p.Dispose();
                    }
                }
                catch { }
            }
            return set;
        }

        public static void ProbeAll()
        {
            Program.Log("probe-all visible top-level windows");
            Native.EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                if (!Native.IsWindowVisible(h)) return true;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                string pname = "?";
                try { using (Process p = Process.GetProcessById((int)pid)) { pname = p.ProcessName; } }
                catch { }
                Native.RECT r;
                Native.GetWindowRect(h, out r);
                if (r.Width < 80 || r.Height < 80) return true;
                Program.Log(string.Format("  pid={0} proc={1} cls={2} title='{3}' rect={4},{5},{6}x{7}",
                    pid, pname, Native.ClassName(h), Native.Title(h), r.Left, r.Top, r.Width, r.Height));
                return true;
            }, IntPtr.Zero);
        }

        public static void Probe(Agent a)
        {
            var pids = PidsOf(a);
            Program.Log("probe " + a.Id + " proc=" + a.Proc + " pids=" + pids.Count);
            Native.EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains((int)pid)) return true;
                Native.RECT r;
                Native.GetWindowRect(h, out r);
                Program.Log(string.Format(
                    "  hwnd={0} pid={1} vis={2} iconic={3} cls={4} title='{5}' ex=0x{6:X} owner={7} area={8}x{9}",
                    h, pid, Native.IsWindowVisible(h), Native.IsIconic(h), Native.ClassName(h), Native.Title(h),
                    Native.GetWindowLong(h, Native.GWL_EXSTYLE), Native.GetWindow(h, Native.GW_OWNER),
                    r.Width, r.Height));
                return true;
            }, IntPtr.Zero);
        }

        public static string DumpWindows()
        {
            var sb = new StringBuilder();
            Native.EnumWindows(delegate(IntPtr h, IntPtr lp)
            {
                if (!Native.IsWindowVisible(h)) return true;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                Native.RECT r;
                Native.GetWindowRect(h, out r);
                if (r.Width < 80 || r.Height < 80) return true;
                sb.AppendLine(string.Format("  pid={0} cls={1} ex=0x{2:X} title='{3}' rect={4},{5} {6}x{7}",
                    pid, Native.ClassName(h), Native.GetWindowLong(h, Native.GWL_EXSTYLE),
                    Native.Title(h), r.Left, r.Top, r.Width, r.Height));
                return true;
            }, IntPtr.Zero);
            return sb.ToString();
        }

        public static string DoctorReport(List<Agent> agents)
        {
            var sb = new StringBuilder();
            sb.AppendLine("AGENT HUB doctor  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("user=" + Environment.UserName + "  elevated=" + SelfElevated());
            sb.AppendLine("base=" + AppDomain.CurrentDomain.BaseDirectory);
            sb.AppendLine("screen=" + Screen.PrimaryScreen.Bounds);
            sb.AppendLine();
            foreach (Agent a in agents)
            {
                sb.AppendLine("=== " + a.Id + "  " + a.Name);
                sb.AppendLine("  exe=" + a.Exe);
                try { sb.AppendLine("  exeExists=" + (!string.IsNullOrEmpty(a.Exe) && File.Exists(a.Exe))); }
                catch { }
                sb.AppendLine("  proc=" + a.Proc + "  title=" + a.Title);
                HashSet<int> pids = PidsOf(a);
                sb.Append("  pids=" + pids.Count + " [");
                foreach (int p in pids) sb.Append(p + " ");
                sb.AppendLine("]");
                IntPtr h = Find(a, DateTime.MinValue);
                sb.AppendLine("  find=" + h + (h != IntPtr.Zero ? ("  title='" + Native.Title(h) + "'") : ""));
                sb.AppendLine();
            }
            sb.AppendLine("=== visible top-level windows ===");
            sb.Append(DumpWindows());
            return sb.ToString();
        }

        public class WindowInfo
        {
            public IntPtr Hwnd;
            public uint Pid;
            public string Title;
            public string Class;
            public string Proc;
            public string Exe;
        }

        public static List<WindowInfo> Windows()
        {
            var list = new List<WindowInfo>();
            int self = Process.GetCurrentProcess().Id;

            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                string t = Native.Title(h);
                if (t.Length == 0) return true;

                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if ((int)pid == self) return true;

                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) return true;
                if (r.Width < 200 || r.Height < 120) return true;

                string cls = Native.ClassName(h);
                if (cls == "Progman" || cls == "Shell_TrayWnd" || cls == "ApplicationFrameWindow") return true;

                string proc = "?";
                string exe = "";
                try
                {
                    using (Process p = Process.GetProcessById((int)pid))
                    {
                        proc = p.ProcessName;
                        try { exe = p.MainModule.FileName; } catch { }
                    }
                }
                catch { }

                list.Add(new WindowInfo { Hwnd = h, Pid = pid, Title = t, Class = cls, Proc = proc, Exe = exe });
                return true;
            }, IntPtr.Zero);

            return list;
        }

        public static int CountPids(Agent a)
        {
            return PidsOf(a).Count;
        }

        public static bool IsRunning(Agent a)
        {
            return PidsOf(a).Count > 0;
        }

        // 标题兜底匹配的防误接管检查：标题里含「豆包」的浏览器标签页不该被当成豆包接走。
        // 进程路径可读时，要求它与配置里的 exe 同目录，或两者文件名互为包含（launcher 场景，
        // 如 "Qoder CN Launcher" 启动的真正窗口是 "Qoder CN"）。读不到路径就放行。
        private static bool PathPlausible(IntPtr h, Agent a)
        {
            if (string.IsNullOrEmpty(a.Exe)) return true;
            if (a.Exe.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                a.Exe.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) return true;
            if (!File.Exists(a.Exe)) return true;   // 配置路径本机不存在，无从比起

            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            string procPath = null;
            try { using (Process p = Process.GetProcessById((int)pid)) procPath = p.MainModule.FileName; }
            catch { return true; }
            if (string.IsNullOrEmpty(procPath)) return true;

            string d1 = Path.GetDirectoryName(a.Exe);
            string d2 = Path.GetDirectoryName(procPath);
            if (string.Equals(d1, d2, StringComparison.OrdinalIgnoreCase)) return true;

            string n1 = Path.GetFileNameWithoutExtension(a.Exe);
            string n2 = Path.GetFileNameWithoutExtension(procPath);
            if (n1.IndexOf(n2, StringComparison.OrdinalIgnoreCase) >= 0 ||
                n2.IndexOf(n1, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            Program.Log("find: title match rejected, proc=" + procPath + " want=" + a.Exe);
            return false;
        }

        public static IntPtr Find(Agent a, DateTime since)
        {
            var pids = PidsOf(a);
            if (a.IsUrl && since != DateTime.MinValue)
            {
                foreach (string n in a.ProcNames())
                {
                    try
                    {
                        foreach (Process p in Process.GetProcessesByName(n))
                        {
                            try
                            {
                                if (p.StartTime >= since.AddSeconds(-3)) pids.Add(p.Id);
                            }
                            catch { }
                            p.Dispose();
                        }
                    }
                    catch { }
                }
            }
            if (pids.Count == 0 && string.IsNullOrEmpty(a.Title)) return IntPtr.Zero;

            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            IntPtr fallback = IntPtr.Zero;
            long fallbackArea = 0;

            if (pids.Count > 0)
            {
            Native.EnumWindowsProc cb = delegate(IntPtr h, IntPtr lp)
            {
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains((int)pid)) return true;
                if (Native.GetWindowTextLength(h) == 0) return true;
                if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;

                string cls = Native.ClassName(h);
                if (cls == "Windows.UI.Core.CoreWindow") return true;

                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) return true;
                long area = (long)r.Width * r.Height;
                if (area < 120 * 120) return true;

                if (Native.IsWindowVisible(h))
                {
                    if (area > bestArea) { bestArea = area; best = h; }
                }
                else
                {
                    if (area > fallbackArea) { fallbackArea = area; fallback = h; }
                }
                return true;
            };
            Native.EnumWindows(cb, IntPtr.Zero);
            }

            if (best == IntPtr.Zero && fallback != IntPtr.Zero) best = fallback;

            if (best == IntPtr.Zero && !string.IsNullOrEmpty(a.Title) && !a.IsUrl)
            {
                best = IntPtr.Zero;
                bestArea = 0;
                int selfPid = Process.GetCurrentProcess().Id;
                Native.EnumWindowsProc cb2 = delegate(IntPtr h, IntPtr lp)
                {
                    if (!Native.IsWindowVisible(h)) return true;
                    uint pid;
                    Native.GetWindowThreadProcessId(h, out pid);
                    if ((int)pid == selfPid) return true;
                    string t = Native.Title(h);
                    if (t.Length == 0) return true;
                    if (t.IndexOf(a.Title, StringComparison.OrdinalIgnoreCase) < 0) return true;
                    if (t.IndexOf("AGENT HUB", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    string cls = Native.ClassName(h);
                    if (cls == "Progman" || cls == "Shell_TrayWnd" || cls == "ApplicationFrameWindow") return true;
                    Native.RECT r;
                    if (!Native.GetWindowRect(h, out r)) return true;
                    long area = (long)r.Width * r.Height;
                    if (area < 120 * 120) return true;
                    if (!PathPlausible(h, a)) return true;
                    if (area > bestArea) { bestArea = area; best = h; }
                    return true;
                };
                Native.EnumWindows(cb2, IntPtr.Zero);
            }

            return best;
        }

        // 协同平铺用的找窗：DWM 缩略图注册哪个句柄三栏就显示哪个，
        // 绝不能落到不可见后备窗口上（那正是"抓到的不是屏幕上那个窗口"的根源）。
        // 先找可见窗口；没有就返回 0，让调用方稍后重试。
        public static IntPtr FindVisible(Agent a, DateTime since)
        {
            var pids = PidsOf(a);
            if (pids.Count == 0) return IntPtr.Zero;

            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            Native.EnumWindowsProc cb = delegate(IntPtr h, IntPtr lp)
            {
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains((int)pid)) return true;
                if (!Native.IsWindowVisible(h)) return true;
                if (Native.GetWindowTextLength(h) == 0) return true;
                if ((Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;

                string cls = Native.ClassName(h);
                if (cls == "Windows.UI.Core.CoreWindow") return true;

                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) return true;
                long area = (long)r.Width * r.Height;
                if (area < 120 * 120) return true;

                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            };
            Native.EnumWindows(cb, IntPtr.Zero);

            if (best == IntPtr.Zero && !string.IsNullOrEmpty(a.Title) && !a.IsUrl)
            {
                int selfPid = Process.GetCurrentProcess().Id;
                Native.EnumWindowsProc cb2 = delegate(IntPtr h, IntPtr lp)
                {
                    if (!Native.IsWindowVisible(h)) return true;
                    uint pid;
                    Native.GetWindowThreadProcessId(h, out pid);
                    if ((int)pid == selfPid) return true;
                    string t = Native.Title(h);
                    if (t.Length == 0) return true;
                    if (t.IndexOf(a.Title, StringComparison.OrdinalIgnoreCase) < 0) return true;
                    if (t.IndexOf("AGENT HUB", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    string cls = Native.ClassName(h);
                    if (cls == "Progman" || cls == "Shell_TrayWnd" || cls == "ApplicationFrameWindow") return true;
                    Native.RECT r;
                    if (!Native.GetWindowRect(h, out r)) return true;
                    long area = (long)r.Width * r.Height;
                    if (area < 120 * 120) return true;
                    if (!PathPlausible(h, a)) return true;
                    if (area > bestArea) { bestArea = area; best = h; }
                    return true;
                };
                Native.EnumWindows(cb2, IntPtr.Zero);
            }
            return best;
        }

        // 进程归属：pid -> 是否由本应用 CreateProcess 启动。只对我方的进程负责。
        private static readonly Dictionary<int, bool> Owned = new Dictionary<int, bool>();

        public static void Register(int pid, bool mine)
        {
            if (pid <= 0) return;
            if (!Owned.ContainsKey(pid)) Owned[pid] = mine;
        }

        // 按进程名列出当前已存在的 pid：用来分辨“点按钮之前就开着的实例”
        public static HashSet<int> PidsByName(string proc)
        {
            var set = new HashSet<int>();
            if (string.IsNullOrEmpty(proc)) return set;
            foreach (string one in proc.Split(new char[] { ',', ';' }))
            {
                string n = one.Trim();
                if (n.Length == 0) continue;
                try { foreach (Process p in Process.GetProcessesByName(n)) set.Add(p.Id); }
                catch { }
            }
            return set;
        }

        public static bool IsOwned(int pid)
        {
            bool mine;
            return Owned.TryGetValue(pid, out mine) && mine;
        }

        public static void Forget(int pid) { Owned.Remove(pid); }

        public static List<int> OwnedPids()
        {
            var list = new List<int>();
            foreach (KeyValuePair<int, bool> kv in Owned) if (kv.Value) list.Add(kv.Key);
            return list;
        }

        // 两段式结束：先请求正常退出，给 graceMs 收尾，剩下的强杀整棵树
        public static int KillOwned(int graceMs)
        {
            List<int> pids = OwnedPids();
            foreach (int p in pids)
            {
                try { using (Process pp = Process.GetProcessById(p)) Program.Log("exit owned pid=" + p + " " + pp.ProcessName); }
                catch { Program.Log("exit owned pid=" + p + " (已退出)"); }
            }
            foreach (int pid in pids)
            {
                try { using (Process p = Process.GetProcessById(pid)) p.CloseMainWindow(); }
                catch { }
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < graceMs && pids.Count > 0)
            {
                pids.RemoveAll(delegate(int pid)
                {
                    try { using (Process p = Process.GetProcessById(pid)) return p.HasExited; }
                    catch { return true; }
                });
                if (pids.Count == 0) break;
                System.Threading.Thread.Sleep(60);
            }
            foreach (int pid in pids) KillTree(pid);
            int killed = pids.Count;
            Owned.Clear();
            return killed;
        }

        public static void Launch(Agent a) { Launch(a, false); }

        public static void Launch(Agent a, bool hidden)
        {
            if (a.IsUrl)
            {
                LaunchEdgeApp(a);
                return;
            }

            bool isLink = a.Exe.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                          a.Exe.EndsWith(".url", StringComparison.OrdinalIgnoreCase);

            var psi = new ProcessStartInfo();
            if (isLink)
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c start \"\" \"" + a.Exe + "\"";
                psi.CreateNoWindow = true;
            }
            else
            {
                psi.FileName = a.Exe;
                if (!string.IsNullOrEmpty(a.Args)) psi.Arguments = a.Args;
            }
            psi.UseShellExecute = false;
            if (hidden) psi.WindowStyle = ProcessWindowStyle.Hidden;   // SW_HIDE：进程创建时窗口就不可见
            try
            {
                string dir = Path.GetDirectoryName(a.Exe);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
            }
            catch { }
            Process pr = Process.Start(psi);
            if (pr != null) Register(pr.Id, true);   // 记下来：这个是我们启动的
        }

        public static string EdgePath()
        {
            string p1 = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
            string p2 = @"C:\Program Files\Microsoft\Edge\Application\msedge.exe";
            if (File.Exists(p1)) return p1;
            if (File.Exists(p2)) return p2;
            return "";
        }

        private static void LaunchEdgeApp(Agent a)
        {
            string edge = EdgePath();
            if (edge == "")
            {
                throw new Exception("未找到 Microsoft Edge");
            }
            string profile = Path.Combine(DataDir, "edge-" + a.Id);
            if (!Directory.Exists(profile)) Directory.CreateDirectory(profile);
            var psi = new ProcessStartInfo();
            psi.FileName = edge;
            psi.UseShellExecute = false;
            psi.Arguments = "--app=" + a.Url + " --user-data-dir=\"" + profile + "\" --no-first-run --no-default-browser-check --window-size=1400,900";
            Process pr = Process.Start(psi);
            if (pr != null) Register(pr.Id, true);   // 记下来：这个是我们启动的
        }

        public static bool Apply(IntPtr hwnd, IntPtr shell, Rectangle rect, bool offsetFrame)
        {
            Takeover t;
            // Host.Hide 用 SW_HIDE 藏过，切回来必须显式显示；这一步与"是否首次接管"无关
            Native.ShowWindow(hwnd, Native.SW_SHOW);
            bool first = !Map.TryGetValue(hwnd, out t);
            if (first)
            {
                t = new Takeover();
                t.Hwnd = hwnd;
                uint pid;
                Native.GetWindowThreadProcessId(hwnd, out pid);
                t.Pid = (int)pid;
                t.Style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
                t.ExStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);

                Native.ShowWindow(hwnd, Native.SW_SHOW);   // 先还原尺寸再接管
                if (Native.IsIconic(hwnd) || (t.Style & WS_MAXIMIZE) != 0)
                {
                    Native.ShowWindow(hwnd, Native.SW_RESTORE);
                }
                Native.RECT r;
                Native.GetWindowRect(hwnd, out r);
                t.Rect = r;
                Map[hwnd] = t;
                SaveJournal();

                if (!NoStyle)
                {
                    int style = Native.GetWindowLong(hwnd, Native.GWL_STYLE);
                    int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                    if (offsetFrame)
                    {
                        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW);
                    }
                    else
                    {
                        style &= ~(Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_SYSMENU | Native.WS_MINIMIZEBOX |
                                   Native.WS_MAXIMIZEBOX | Native.WS_BORDER | Native.WS_DLGFRAME);
                        style |= Native.WS_VISIBLE | Native.WS_CLIPSIBLINGS | Native.WS_CLIPCHILDREN;
                        Native.SetWindowLong(hwnd, Native.GWL_STYLE, style);
                        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, (ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW);
                    }
                }
            }

            int x = rect.X;
            int y = rect.Y;
            int w = rect.Width;
            int h = rect.Height;

            if (offsetFrame && t.PlainFallback) offsetFrame = false;

            if (offsetFrame)
            {
                // 保留应用自己的边框，把非客户区移到屏幕外，使客户区正好铺满内容区
                Native.RECT wr = new Native.RECT();
                Native.RECT cr = new Native.RECT();
                Native.POINT pt = new Native.POINT();
                bool okW = Native.GetWindowRect(hwnd, out wr);
                bool okC = Native.GetClientRect(hwnd, out cr);
                bool okP = Native.ClientToScreen(hwnd, ref pt);
                Program.Log(string.Format("offset probe okW={0} okC={1} okP={2} wr={3},{4} {5}x{6} cr={7}x{8} pt={9},{10}",
                    okW, okC, okP, wr.Left, wr.Top, wr.Width, wr.Height, cr.Width, cr.Height, pt.X, pt.Y));
                if (okW && okC && okP)
                {
                    int dx = pt.X - wr.Left;
                    int dy = pt.Y - wr.Top;
                    int extraW = wr.Width - cr.Width;
                    int extraH = wr.Height - cr.Height;
                    if (Math.Abs(extraW) < 300 && Math.Abs(extraH) < 300)
                    {
                        x = rect.X - dx;
                        y = rect.Y - dy;
                        w = rect.Width + extraW;
                        h = rect.Height + extraH;
                    }
                }
            }

            uint flags = Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW;
            if (first && !offsetFrame) flags |= Native.SWP_FRAMECHANGED;

            bool moved = Native.SetWindowPos(hwnd, shell, x, y, w, h, flags);
            Native.ShowWindow(hwnd, Native.SW_SHOW);

            bool ok = RectMatches(hwnd, x, y, w, h);

            if (!ok && offsetFrame)
            {
                // 有些窗口拒绝负坐标（控制台、被系统约束的窗口），退回普通定位：
                // 标题栏会露在内容区顶部，但至少内容能显示。
                x = rect.X;
                y = rect.Y;
                w = rect.Width;
                h = rect.Height;
                Native.SetWindowPos(hwnd, shell, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                ok = RectMatches(hwnd, x, y, w, h);
                if (ok)
                {
                    Program.Log("offset rejected, fell back to plain rect");
                    Takeover tt;
                    if (Map.TryGetValue(hwnd, out tt)) tt.PlainFallback = true;
                }
            }

            if (!ok)
            {
                Native.RECT chk = new Native.RECT();
                Native.GetWindowRect(hwnd, out chk);
                Program.Log(string.Format("apply rejected: moved={0} rect={1},{2},{3}x{4} want={5},{6},{7}x{8}",
                    moved, chk.Left, chk.Top, chk.Width, chk.Height, x, y, w, h));
            }
            return ok;
        }

        private static bool RectMatches(IntPtr hwnd, int x, int y, int w, int h)
        {
            Native.RECT c = new Native.RECT();
            if (!Native.GetWindowRect(hwnd, out c)) return false;
            return Math.Abs(c.Left - x) <= 4 && Math.Abs(c.Top - y) <= 4 &&
                   Math.Abs(c.Width - w) <= 8 && Math.Abs(c.Height - h) <= 8;
        }

        public static void KillTree(int pid)
        {
            if (pid <= 0) return;
            try
            {
                var psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
                Program.Log("kill tree pid=" + pid);
            }
            catch (Exception ex)
            {
                Program.Log("kill failed pid=" + pid + ": " + ex.Message);
            }
        }

        public static void Hide(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero && Native.IsWindow(hwnd)) Native.ShowWindow(hwnd, Native.SW_HIDE);
        }

        public static void ReleaseAll()
        {
            foreach (Takeover t in new List<Takeover>(Map.Values))
            {
                try
                {
                    if (!Native.IsWindow(t.Hwnd)) continue;
                    Native.SetWindowLong(t.Hwnd, Native.GWL_STYLE, t.Style);
                    Native.SetWindowLong(t.Hwnd, Native.GWL_EXSTYLE, t.ExStyle);
                    Native.SetWindowPos(t.Hwnd, IntPtr.Zero, t.Rect.Left, t.Rect.Top, t.Rect.Width, t.Rect.Height,
                        Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
                    Native.ShowWindow(t.Hwnd, Native.SW_SHOW);
                }
                catch { }
            }
            Map.Clear();
            ClearJournal();
        }

        public static void SaveJournal()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (Takeover t in Map.Values)
                {
                    sb.AppendLine(string.Join(";", new string[] {
                        t.Hwnd.ToInt64().ToString(CultureInfo.InvariantCulture),
                        t.Pid.ToString(CultureInfo.InvariantCulture),
                        t.Style.ToString(CultureInfo.InvariantCulture),
                        t.ExStyle.ToString(CultureInfo.InvariantCulture),
                        t.Rect.Left.ToString(CultureInfo.InvariantCulture),
                        t.Rect.Top.ToString(CultureInfo.InvariantCulture),
                        t.Rect.Right.ToString(CultureInfo.InvariantCulture),
                        t.Rect.Bottom.ToString(CultureInfo.InvariantCulture)
                    }));
                }
                File.WriteAllText(JournalPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public static void ClearJournal()
        {
            try { if (File.Exists(JournalPath)) File.Delete(JournalPath); } catch { }
        }

        public static int RestoreStale()
        {
            try
            {
                if (!File.Exists(JournalPath)) return 0;
                int n = 0;
                foreach (string line in File.ReadAllLines(JournalPath, Encoding.UTF8))
                {
                    if (line.Trim().Length == 0) continue;
                    string[] p = line.Split(';');
                    if (p.Length < 8) continue;
                    IntPtr h = new IntPtr(long.Parse(p[0], CultureInfo.InvariantCulture));
                    int pid = int.Parse(p[1], CultureInfo.InvariantCulture);
                    int style = int.Parse(p[2], CultureInfo.InvariantCulture);
                    int ex = int.Parse(p[3], CultureInfo.InvariantCulture);
                    int l = int.Parse(p[4], CultureInfo.InvariantCulture);
                    int t = int.Parse(p[5], CultureInfo.InvariantCulture);
                    int r = int.Parse(p[6], CultureInfo.InvariantCulture);
                    int b = int.Parse(p[7], CultureInfo.InvariantCulture);
                    if (!Native.IsWindow(h)) continue;
                    uint cur;
                    Native.GetWindowThreadProcessId(h, out cur);
                    if ((int)cur != pid) continue;
                    Native.SetWindowLong(h, Native.GWL_STYLE, style);
                    Native.SetWindowLong(h, Native.GWL_EXSTYLE, ex);
                    Native.SetWindowPos(h, IntPtr.Zero, l, t, r - l, b - t,
                        Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
                    Native.ShowWindow(h, Native.SW_SHOW);
                    n++;
                }
                ClearJournal();
                return n;
            }
            catch { return 0; }
        }

        public static bool LooksElevated(int pid)
        {
            IntPtr p = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (p == IntPtr.Zero)
            {
                // 打不开不等于提权：进程可能刚退出（ERROR_INVALID_PARAMETER）。
                // 只有 ERROR_ACCESS_DENIED 才说明目标完整性级别比我们高。
                return Marshal.GetLastWin32Error() == 5;
            }
            Native.CloseHandle(p);
            return false;
        }

        public static int IntegrityRid()
        {
            IntPtr h = IntPtr.Zero;
            IntPtr tok = IntPtr.Zero;
            IntPtr buf = IntPtr.Zero;
            try
            {
                h = Native.GetCurrentProcess();
                if (!Native.OpenProcessToken(h, 0x0008, out tok)) return -1;   // 0x0008 = TOKEN_QUERY
                buf = Marshal.AllocHGlobal(64);
                int ret;
                if (!Native.GetTokenInformation(tok, 25, buf, 64, out ret)) return -1;
                IntPtr sid = Marshal.ReadIntPtr(buf, 0);
                int cnt = Marshal.ReadByte(Native.GetSidSubAuthorityCount(sid));
                IntPtr sub = Native.GetSidSubAuthority(sid, cnt - 1);
                return Marshal.ReadInt32(sub);
            }
            catch { return -1; }
            finally
            {
                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                if (tok != IntPtr.Zero) Native.CloseHandle(tok);
            }
        }

        public static bool SelfElevated()
        {
            try
            {
                var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                var pr = new System.Security.Principal.WindowsPrincipal(id);
                return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static void ForceForeground(IntPtr h)
        {
            if (Native.GetForegroundWindow() == h) return;
            Native.SetForegroundWindow(h);
            if (Native.GetForegroundWindow() != h) Native.SwitchToThisWindow(h, true);
        }
    }
}
