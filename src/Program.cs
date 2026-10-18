using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AgentHub
{
    internal static class Program
    {
        internal static readonly string LogPath =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "app.log");

        internal static void Log(string s)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss.fff ") + s + "\r\n", Encoding.UTF8);
            }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            Log("---- start ----");
            try
            {
                Run(args);
                Log("---- exit ----");
            }
            catch (Exception ex)
            {
                Log("FATAL " + ex);
                Host.ReleaseAll();
                MessageBox.Show("启动失败：\r\n" + ex.Message, "AGENT HUB", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void Run(string[] args)
        {
            if (args != null && args.Length > 0 && args[0] == "--restore")
            {
                int n = Host.RestoreStale();
                MessageBox.Show("已还原 " + n + " 个被接管的窗口。", "AGENT HUB", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (args != null && Array.IndexOf(args, "--doctor") >= 0)
            {
                string rep = Host.DoctorReport(Config.Load(null));
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "doctor.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, rep, Encoding.UTF8);
                Log("doctor -> " + path);
                try { Process.Start("notepad.exe", "\"" + path + "\""); } catch { }
                return;
            }

            if (args != null && args.Length >= 2 && args[0] == "--launchtest")
            {
                Agent target = null;
                foreach (Agent x in Config.Load(null)) if (x.Id == args[1]) target = x;
                if (target == null) { Log("launchtest: agent not found " + args[1]); return; }
                Log("launchtest id=" + target.Id + " exe=" + target.Exe);
                try
                {
                    Host.Launch(target);
                    Log("launchtest: Process.Start returned");
                }
                catch (Exception ex)
                {
                    Log("launchtest: threw " + ex.GetType().Name + " " + ex.Message);
                    return;
                }
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 24)
                {
                    System.Threading.Thread.Sleep(2000);
                    Log("launchtest t=" + (int)sw.Elapsed.TotalSeconds + "s pids=" + Host.CountPids(target));
                }
                Log("launchtest windows:\r\n" + Host.DumpWindows());
                return;
            }

            bool created;
            string tag = "default";
            if (args != null)
            {
                for (int i = 0; i < args.Length - 1; i++)
                {
                    if (args[i] == "--config") tag = Path.GetFileNameWithoutExtension(args[i + 1]);
                }
            }
            using (var mutex = new Mutex(true, "Local\\AgentHub." + tag, out created))
            {
                if (!created) return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
                {
                    Log("EXCEPTION " + e.Exception.ToString());
                    Host.ReleaseAll();
                    MessageBox.Show("程序遇到错误，已尝试还原被接管的窗口：\r\n\r\n" + e.Exception.Message,
                        "AGENT HUB", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    Application.Exit();
                };

                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Exception ex = e.ExceptionObject as Exception;
                    Log("UNHANDLED " + (ex != null ? ex.ToString() : "?"));
                    Host.ReleaseAll();
                };

                Host.RestoreStale();

                int rid = Host.IntegrityRid();
                Log("integrity rid=" + rid);
                if (rid > 0 && rid < 0x2000)
                {
                    Log("low integrity, asking for elevation");
                    bool auto = args != null && Array.IndexOf(args, "--auto-elevate") >= 0;
                    DialogResult res = auto ? DialogResult.Yes : MessageBox.Show(
                        "AGENT HUB 当前以「受限权限」运行。\r\n\r\n" +
                        "这种权限下它既不能启动其他应用，也不能接管它们的窗口，" +
                        "所以点任何应用都会一直停在“正在启动”。\r\n\r\n" +
                        "是否以管理员身份重新启动？\r\n" +
                        "（会弹出系统的用户账户控制提示，点“是”即可）",
                        "AGENT HUB 需要更高权限",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
                    if (auto) Log("auto-elevate");
                    if (res == DialogResult.Yes)
                    {
                        try
                        {
                            var sbArgs = new StringBuilder();
                            string[] raw = Environment.GetCommandLineArgs();
                            for (int i = 1; i < raw.Length; i++)
                            {
                                if (sbArgs.Length > 0) sbArgs.Append(' ');
                                sbArgs.Append('"').Append(raw[i]).Append('"');
                            }
                            var psi = new ProcessStartInfo();
                            psi.FileName = Application.ExecutablePath;
                            psi.Arguments = sbArgs.ToString();
                            psi.UseShellExecute = true;
                            psi.Verb = "runas";
                            Process.Start(psi);
                            Log("elevation requested");
                        }
                        catch (Exception ex)
                        {
                            Log("elevation failed: " + ex.Message);
                            MessageBox.Show("提权失败：" + ex.Message + "\r\n\r\n请右键 AgentHub.exe，选择「以管理员身份运行」。",
                                "AGENT HUB", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                    return;
                }

                if (args != null && Array.IndexOf(args, "--nostyle") >= 0) Host.NoStyle = true;

                if (args != null && args.Length == 2 && args[0] == "--movetest")
                {
                    IntPtr h = new IntPtr(long.Parse(args[1]));
                    Native.RECT before;
                    Native.GetWindowRect(h, out before);
                    bool ok = Native.SetWindowPos(h, IntPtr.Zero, 120, 80, 1000, 720, 0x0040);
                    int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                    Native.RECT after;
                    Native.GetWindowRect(h, out after);
                    Log(string.Format("movetest ok={0} err={1} before={2},{3} {4}x{5} after={6},{7} {8}x{9}",
                        ok, err, before.Left, before.Top, before.Width, before.Height,
                        after.Left, after.Top, after.Width, after.Height));
                    return;
                }

                if (args != null && Array.IndexOf(args, "--probe") >= 0)
                {
                    foreach (Agent a in Config.Load(null))
                    {
                        Host.Probe(a);
                    }
                    return;
                }

                if (args != null && Array.IndexOf(args, "--probe-all") >= 0)
                {
                    Host.ProbeAll();
                    return;
                }

                string cfgPath = null;
                int selftestIndex = -1;
                if (args != null)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (args[i] == "--config" && i + 1 < args.Length) { cfgPath = args[i + 1]; i++; }
                        else if (args[i] == "--selftest")
                        {
                            selftestIndex = 0;
                            int n;
                            if (i + 1 < args.Length && int.TryParse(args[i + 1], out n)) { selftestIndex = n; i++; }
                        }
                    }
                }

                var shell = new Shell(Config.LoadMerged(cfgPath));

                if (selftestIndex >= 0)
                {
                    bool windowed = args != null && Array.IndexOf(args, "--windowed") >= 0;
                    if (windowed) shell.Bounds = new Rectangle(60, 60, 1240, 820);

                    IntPtr con = Native.GetConsoleWindow();
                    if (con != IntPtr.Zero) Native.ShowWindow(con, Native.SW_HIDE);

                    bool switchTest = args != null && Array.IndexOf(args, "--switch-test") >= 0;

                    if (switchTest)
                    {
                        Schedule(600, delegate { shell.Select(0); });
                        Schedule(4000, delegate { shell.Select(1); });
                        Schedule(7500, delegate { shell.Select(0); });
                        Schedule(9500, delegate { Capture(shell, false); shell.Close(); });
                    }
                    else
                    {
                        var kick = new System.Windows.Forms.Timer();
                        kick.Interval = 600;
                        kick.Tick += delegate { kick.Stop(); shell.Select(selftestIndex); };
                        kick.Start();

                        int cancelMs = -1;
                        if (args != null)
                            for (int i = 0; i < args.Length; i++)
                                if (args[i] == "--cancel-after" && i + 1 < args.Length) int.TryParse(args[i + 1], out cancelMs);
                        if (cancelMs > 0)
                        {
                            string cid = shell.AgentIdAt(selftestIndex);
                            Schedule(cancelMs, delegate { Log("TEST cancel " + cid); shell.CancelById(cid); });
                        }
                    }

                    var shot = new System.Windows.Forms.Timer();
                    int shotMs = 6000;
                    if (args != null)
                    {
                        for (int i = 0; i < args.Length; i++)
                        {
                            if (args[i] == "--shot-ms" && i + 1 < args.Length) { int.TryParse(args[i + 1], out shotMs); }
                        }
                    }
                    shot.Interval = shotMs;
                    shot.Tick += delegate
                    {
                        shot.Stop();
                        bool pw = args != null && Array.IndexOf(args, "--printwindow") >= 0;
                        bool top = args != null && Array.IndexOf(args, "--topmost-shot") >= 0;
                        if (top)
                        {
                            Native.SetWindowPos(shell.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                                Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE);
                            if (shell.ActiveHandle != IntPtr.Zero)
                            {
                                Native.SetWindowPos(shell.ActiveHandle, shell.Handle, 0, 0, 0, 0,
                                    Native.SWP_NOACTIVATE | Native.SWP_NOMOVE | Native.SWP_NOSIZE);
                            }
                            Application.DoEvents();
                            System.Threading.Thread.Sleep(500);
                        }
                        Capture(shell, pw);
                        if (args != null && Array.IndexOf(args, "--full-shot") >= 0)
                        {
                            Rectangle scr = Screen.PrimaryScreen.Bounds;
                            using (var bmp = new Bitmap(scr.Width, scr.Height))
                            using (var g = Graphics.FromImage(bmp))
                            {
                                g.CopyFromScreen(scr.Location, Point.Empty, scr.Size);
                                bmp.Save(Path.Combine(Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "shots")), "full.png"), ImageFormat.Png);
                            }
                            Log("fullshot done active=" + shell.ActiveHandle);
                        }
                        shell.Close();
                    };
                    shot.Start();
                }

                Application.Run(shell);
            }
        }

        private static readonly System.Collections.Generic.List<System.Windows.Forms.Timer> _keepTimers =
            new System.Collections.Generic.List<System.Windows.Forms.Timer>();

        private static void Schedule(int ms, Action action)
        {
            var t = new System.Windows.Forms.Timer();
            t.Interval = ms;
            t.Tick += delegate { t.Stop(); action(); };
            _keepTimers.Add(t);
            t.Start();
        }

        private static Bitmap WindowShot(IntPtr h, int w, int hgt)
        {
            var bmp = new Bitmap(Math.Max(1, w), Math.Max(1, hgt));
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                Native.PrintWindow(h, hdc, 2);
                g.ReleaseHdc(hdc);
            }
            return bmp;
        }

        private static void Capture(Shell shell, bool printWindow)
        {
            try
            {
                string dir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "shots"));
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "selftest.png");

                Bitmap bmp;
                if (printWindow)
                {
                    bmp = WindowShot(shell.Handle, shell.Width, shell.Height);
                    IntPtr child = shell.ActiveHandle;
                    if (child != IntPtr.Zero)
                    {
                        Rectangle cr = shell.ClientContentRect;
                        using (Bitmap cb = WindowShot(child, cr.Width, cr.Height))
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.DrawImage(cb, cr.X, cr.Y, cr.Width, cr.Height);
                        }
                    }
                }
                else
                {
                    Rectangle area = shell.Bounds;
                    bmp = new Bitmap(area.Width, area.Height);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                    }
                }

                bmp.Save(file, ImageFormat.Png);
                bmp.Dispose();
                Log("screenshot " + file);
            }
            catch (Exception ex)
            {
                Log("screenshot failed: " + ex.Message);
            }
        }
    }
}
