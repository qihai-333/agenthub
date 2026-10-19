using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace AgentHub
{
    public class Shell : Form
    {
        private const int MaxSlots = 3;
        private const int HotStripPx = 3;   // 侧栏滑出后留在屏幕左缘的热区宽度
        private const int HotkeyBase = 100;
        private const int HotkeyToggle = 200;
        // 非图标按压动画对象标记（图标用非负索引，misc 用负值区分）
        private const int PressPlus = -2;
        private const int PressExit = -3;

        // 方案 D · 纵深 Space：深空分层材质色板
        private static readonly Color Bg = Color.FromArgb(10, 12, 16);          // 深空底 #0A0C10
        private static readonly Color SideBg = Color.FromArgb(14, 16, 22);      // 岩板基色（浅端）
        private static readonly Color BgHover = Color.FromArgb(30, 32, 42);
        private static readonly Color Line = Color.FromArgb(36, 38, 48);
        private static readonly Color Orange = Color.FromArgb(255, 179, 64);    // 豆包·橙（全局兼容保留）
        private static readonly Color OrangeDim = Color.FromArgb(122, 58, 14);
        private static readonly Color TextMain = Color.FromArgb(232, 232, 238);
        private static readonly Color TextDim = Color.FromArgb(154, 154, 163);
        private static readonly Color TextFaint = Color.FromArgb(92, 92, 102);
        internal static readonly Color AccentDefault = Color.FromArgb(139, 123, 255);   // 默认环境光 #8B7BFF

        private readonly List<Agent> _agents;
        private readonly float _s;

        private int _sideW;

        private readonly HashSet<int> _chosen = new HashSet<int>();
        // 本次会话里"由我们启动"的应用（按 agent 索引）。Electron 的 exe 只是启动器，
        // 真正持有窗口的是另一个 pid，所以不能用 pid 反推归属，只能记住"是我点的启动"。
        private readonly HashSet<int> _launchedByMe = new HashSet<int>();
        // 点启动那一刻已经存在的同名进程：它们不是我启动的（单实例应用会把我的启动转交给它们）
        private readonly Dictionary<int, HashSet<int>> _priorPids = new Dictionary<int, HashSet<int>>();

        private bool OwnedNow(int i, int pid)
        {
            if (!_launchedByMe.Contains(i)) return false;
            HashSet<int> before;
            if (_priorPids.TryGetValue(i, out before) && before.Contains(pid)) return false;
            return true;
        }
        private int _shown = -1;
        private readonly Dictionary<int, IntPtr> _windows = new Dictionary<int, IntPtr>();
        private readonly Dictionary<int, int> _pids = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _dead = new Dictionary<int, int>();

        private int _hover = -1;
        private bool _hoverExit;
        private bool _plusHot;
        private int _rpHot = -1;   // 右侧栏按钮悬停：0=添加进程 1=切换模式

        private int _pendingIdx = -1;
        private DateTime _deadline;
        private DateTime _launchStamp;

        private readonly Timer _poll = new Timer();
        private readonly Timer _health = new Timer();
        private readonly Timer _anim = new Timer();

        private float _introT;
        private float _enterT;                 // 开屏入场进度：背景 -> 图标 -> 标题 -> 卡片
        private readonly System.Diagnostics.Stopwatch _enterClock = System.Diagnostics.Stopwatch.StartNew();
        private float _enterDur = 2.5f;        // 开屏总时长（秒）；切换模式时用 0.35
        private readonly bool _reduceMotion = !Native.AnimationsEnabled();
        private Bitmap _logo;
        private float _hoverT = 1f;
        private float _pressT;
        private int _pressIdx = -1;      // 最近按下的图标索引（动画期间保留）
        private bool _pressHeld;         // 左键是否仍按住（按压保持阶段）
        private float _relT = 1f;        // 松开回弹进度 0→1（配合 ReleaseSpring）
        private float _rippleT = 1f;     // 点击涟漪进度 1=结束
        private int _rippleIdx = -1;     // 涟漪所在图标
        private Point _rippleAt;         // 涟漪起点（客户区坐标）
        private float _popT = 1f;        // 选中胶囊弹出回弹进度 0→1
        private int _popIdx = -1;        // 正在弹出的应用索引
        private float _modeT = 1f;       // 模式转场进度 1=完成（模式选择页 → 工作区）
        private int _modeSel = -1;       // 本次转场选中的模式（0 普通 / 1 协同）
        private int _miscObj = -1;       // 非图标按压动画对象：-2=加号 -3=退出键（动画期间保留）
        private float _expandT = 1f;
        private float _spin;

        // 方案 D：环境光当前色（平滑过渡）、呼吸相位、星空/噪点缓存
        private Color _acc = AccentDefault;
        private float _breath;
        private Bitmap _starBmp;
        private Size _starSize;
        private Bitmap _noiseBmp;
        // 开屏绘制缓存（减少每帧 GDI+ 对象创建）
        private Font _fLogoBig;
        private Font _fBrandSub;
        private Font _fModeTitle;
        private Font _fCardTitle;
        private Font _fCardSub;
        private Bitmap _cardBmpA, _cardBmpB;   // 两张模式卡离屏缓存
        private Color _cardBmpAcc;             // 缓存时的环境光色（变化时重建）
        private PathGradientBrush _haloBrush;  // 开屏中央光晕刷缓存
        private PathGradientBrush _ambientBrush;   // 整层环境光刷缓存（每帧只改 CenterColor）
        private PathGradientBrush _modeHaloBrush;  // 转场确认光晕刷缓存
        private LinearGradientBrush _beamBrush;    // 光带刷缓存
        private LinearGradientBrush _slabBrush, _slabHiBrush, _slabShBrush;  // 岩板主体/顶高光/底阴影（纵向渐变）
        private TextureBrush _slabTexBrush;        // 岩板噪点纹理
        private Pen _slabPen;                      // 岩板顶线 + 右缘线
        private PathGradientBrush _pillHaloBrush;  // Dock 选中点亮刷缓存
        private Bitmap _bootBmp;               // 开屏停留页烘焙位图（入场完成后整页静态，提帧率）
        private Color _bootAcc;                // 烘焙时的环境光色（变化时重建）
        private Bitmap _bgBmp;                 // 工作区静态背景（星空+环境光+光带）烘焙位图
        private Color _bgAcc;                  // 烘焙时的环境光色（变化时重建）
        private int _bgShown;                  // 烘焙时的选中态（选中变化时重建）

        // 侧栏状态机：以前是 _hiddenToTray / _dockHidden / _slideT 三个变量互相协作，
        // 小 bug 全出在它们打架上。现在收敛成一个枚举，_slideT 只负责动画进度。
        //   Visible    —— 侧栏在屏幕上，正常交互
        //   Hidden     —— 滑到屏幕左缘外，只留 3px 热区（电源键/任务栏键触发）
        //   Background —— Hidden 且已选应用也被隐藏，进程继续跑，靠托盘唤回
        private enum DockState { Visible, Hidden, Background }

        private DockState _dock = DockState.Visible;
        private float _slideT;   // 0 = 完全可见，1 = 完全滑出；目标值由 _dock 决定
        private readonly Timer _hideTimer = new Timer();

        private bool DockGone { get { return _dock != DockState.Visible; } }
        private bool DockInBackground { get { return _dock == DockState.Background; } }

        // 关到后台：窗口滑出、应用隐藏、进程继续，靠托盘或左边缘叫回来
        private NotifyIcon _tray;
        private bool _reallyExit;

        private readonly List<string> _restore = new List<string>();
        private DateTime _restoreAt;
        private readonly Timer _restoreTimer = new Timer();

        // 显示顺序（按 id）与每个图标的动画位置
        private readonly List<string> _order = new List<string>();
        private readonly Dictionary<string, float> _y = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _delay = new Dictionary<string, float>();

        private readonly Font _fName;
        private readonly SolidBrush _bSide;
        private readonly SolidBrush _bOrange;
        private readonly Pen _pLine;
        private readonly Pen _pOrange;
        private readonly Pen _pRing;

        public Shell(List<Agent> agents)
        {
            _agents = agents;
            Text = "AGENT HUB";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            _s = DeviceDpi / 96f;
            _sideW = SideExpandedPx;

            Rectangle scr = Screen.PrimaryScreen.WorkingArea;
            Bounds = new Rectangle(scr.X, scr.Y, _sideW, scr.Height);
            BackColor = SideBg;

            _fName = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);

            // 开屏绘制缓存：Font / 模式卡位图 / 中央光晕刷（避免每帧创建 GDI+ 对象，显著提升开屏帧率）
            _fLogoBig = new Font("Segoe UI", Px(44), FontStyle.Bold, GraphicsUnit.Pixel);
            _fBrandSub = new Font("微软雅黑", Px(15), FontStyle.Regular, GraphicsUnit.Pixel);
            _fModeTitle = new Font("微软雅黑", Px(52), FontStyle.Bold, GraphicsUnit.Pixel);
            _fCardTitle = new Font("微软雅黑", Px(26), FontStyle.Bold, GraphicsUnit.Pixel);
            _fCardSub = new Font("微软雅黑", Px(15), FontStyle.Regular, GraphicsUnit.Pixel);

            _bSide = new SolidBrush(SideBg);
            _bOrange = new SolidBrush(Orange);
            _pLine = new Pen(Line);
            _pOrange = new Pen(Orange, Math.Max(1.5f, Px(2) * 0.9f));
            _pRing = new Pen(OrangeDim, Math.Max(1.6f, Px(2) * 0.8f));

            _noiseBmp = BuildNoise();

            try
            {
                this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

            _poll.Interval = 200;
            _poll.Tick += PollTick;
            _health.Interval = 1000;
            _health.Tick += HealthTick;
            _health.Start();
            _anim.Interval = 3;   // 约 300Hz，配合 timeBeginPeriod(1) 才能真正跑到这个节拍
            _anim.Tick += AnimTick;
            Native.timeBeginPeriod(1);   // 不设 1ms 精度时 7ms 定时器实际按 15.6ms 跳，动画会慢一倍
            _anim.Start();   // 启动时就要跑：否则 _introT 一直是 0，侧栏图标画不出来
            _hideTimer.Interval = 250;
            _hideTimer.Tick += HideTick;
            _hideTimer.Start();
            if (_reduceMotion) _enterDur = 0.30f;   // 系统关掉动态效果：只做一段短淡入
            SetupTray();
                        // 图标源：优先用同目录的 logo.png（512×512 源图）；没有再退回 exe 图标
            // 之前直接从 exe 图标取，ExtractAssociatedIcon 只给 32×32，放大到 160px 必然发糊
            try
            {
                string lp = System.IO.Path.Combine(Application.StartupPath, "logo.png");
                if (System.IO.File.Exists(lp))
                {
                    using (var fs = new System.IO.FileStream(lp, System.IO.FileMode.Open, System.IO.FileAccess.Read))
                    using (var tmp0 = Image.FromStream(fs))
                        _logo = new Bitmap(tmp0, 512, 512);
                }
                else
                {
                    using (var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                        if (ic != null)
                        {
                            using (var src0 = ic.ToBitmap())
                            {
                                _logo = new Bitmap(256, 256);
                                using (var lg = Graphics.FromImage(_logo))
                                {
                                    lg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                                    lg.DrawImage(src0, 0, 0, 256, 256);
                                }
                            }
                        }
                }
            }
            catch { }

            // 开屏改为深空底：白底 logo 去底，保留彩色主体
            try
            {
                if (_logo != null)
                {
                    Bitmap keyed = KeyWhite(_logo);
                    if (keyed != null) { _logo.Dispose(); _logo = keyed; }
                }
            }
            catch { }

            string shownId;
            List<string> ids;
            List<string> savedOrder;
            Config.LoadSession(out ids, out shownId, out savedOrder);
            BuildOrder(savedOrder);
            // 启动时不再拉起任何应用：只恢复侧栏顺序，进程由用户在选定模式后按需启动。
            // 原来的 _restore 队列会在这里直接 Launch 三个应用，既违反"预加载不得提前启动"，也会在开屏期间弹窗。
            _enterT = 0f;
            _mode = -1;
            _shown = -1;
            Program.Log("startup: 不自动启动应用，chosen=" + ids.Count);

            Program.Log("dock start sideW=" + _sideW + " agents=" + _agents.Count);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyBounds();
            StartPreload();   // 配置级预加载：只读，不启动任何进程
            Program.Log("dock bounds " + Bounds.X + "," + Bounds.Y + " " + Bounds.Width + "x" + Bounds.Height + " dpi=" + DeviceDpi);
        }

        // 预加载只做"读配置、探测已有窗口"这类不启动进程的准备，全部在后台线程；
        // 它不影响开屏动画和模式选择——加载没完也照样可以点。
        private bool _preloaded;
        private void StartPreload()
        {
            var th = new System.Threading.Thread(delegate()
            {
                try
                {
                    PipeConfig.Load();        // 解析流程配置，首次会生成 pipeline.json
                    Host.Windows();           // 探测当前已经存在的窗口（只读，不启动）
                    Config.LoadCustom();      // 自定义应用列表
                }
                catch { }
                finally { _preloaded = true; Program.Log("preload done"); }
            });
            th.IsBackground = true;
            th.Start();
        }

        private int Px(int v) { return (int)Math.Round(v * _s); }

        private static Color LerpColor(Color a, Color b, float t)
        {
            t = Clamp01(t);
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
        private int RightWPx { get { return Px(260); } }
        // 右侧栏只服务普通工作站。协同模式自带面板（轮次/阶段/审批），再叠一条会把它挤掉。
        // 协同面板里的「切换模式」按钮走这里
        public void SwitchModeFromPanel()
        {
            ExitSynergy();
            _mode = -1;
            _modeSel = -1; _modeT = 1f;   // 重置转场状态，回到正常开屏
            UpdateRightDock();
            _enterT = 0f;
            Kick();          // 必须重启动画定时器：否则入场进度停在 0，绘制直接 return，表现为白屏
            Invalidate();
        }

        // 右侧栏现在是独立小窗（RightDock），这里只表示"要不要给它留位置"
        private bool RightShown { get { return _rightDock != null && _rightDock.Visible; } }
        private RightDock _rightDock;
        private int _fpsFrames;
        private DateTime _fpsMark = DateTime.Now;

        public string ModeTitle() { return _mode == 1 ? "多 agent 协同" : "普通工作站"; }
        public int AgentCount { get { return _agents.Count; } }
        public void DockAddProcess() { ShowAdd(); }

        private void UpdateRightDock()
        {
            bool want = _mode == 0;
            if (want)
            {
                if (_rightDock == null) { _rightDock = new RightDock(this); }
                _rightDock.AttachToScreen(RightWPx);
                if (!_rightDock.Visible) _rightDock.Show();
                _rightDock.Invalidate();
            }
            else if (_rightDock != null && _rightDock.Visible) _rightDock.Hide();
            Invalidate();
        }
        private Rectangle RightPanelRect() { return new Rectangle(Width - RightWPx, 0, RightWPx, Height); }
        private Rectangle AddProcRect() { return new Rectangle(Width - RightWPx + Px(20), Px(150), RightWPx - Px(40), Px(64)); }
        private Rectangle SwitchModeRect() { return new Rectangle(Width - RightWPx + Px(20), Px(240), RightWPx - Px(40), Px(52)); }

        private int SideExpandedPx { get { return Px(92); } }
        private int ItemSizePx { get { return Px(52); } }
        private int ItemStepPx { get { return Px(64); } }
        private int ItemTopPx { get { return Px(30); } }
        private int ItemXPx { get { return Px(20); } }

        public IntPtr ActiveHandle
        {
            get
            {
                IntPtr h;
                return (_shown >= 0 && _windows.TryGetValue(_shown, out h)) ? h : IntPtr.Zero;
            }
        }

        public Rectangle ClientContentRect
        {
            get
            {
                Rectangle scr = Screen.PrimaryScreen.WorkingArea;
                return new Rectangle(_sideW, 0, Math.Max(1, scr.Width - _sideW), scr.Height);
            }
        }

        private Rectangle ContentRect()
        {
            Rectangle scr = Screen.PrimaryScreen.WorkingArea;
            int pad = RightShown ? RightWPx : 0;   // 给右侧栏让位，应用不钻到它下面
            return new Rectangle(scr.X + _sideW, scr.Y, Math.Max(1, scr.Width - _sideW - pad), scr.Height);
        }

        // ---------- 动效 ----------

        private static float Approach(float v, float target, float rate)
        {
            float d = target - v;
            if (Math.Abs(d) < 0.002f) return target;
            return v + d * rate;
        }

        private static float EaseOut(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            float u = 1f - t;
            return 1f - u * u * u;
        }

        private static int Lerp(int a, int b, float t) { return (int)Math.Round(a + (b - a) * t); }

        private static float Clamp01(float v) { return v < 0f ? 0f : (v > 1f ? 1f : v); }

        private static Color Blend(Color a, Color b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * t),
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        // 松开回弹曲线：按住态 0.84 起，先快后慢弹回 1.0，带一次轻微过冲（约 2.7%）
        private static float ReleaseSpring(float t)
        {
            if (t <= 0f) return 0.84f;
            if (t >= 1f) return 1f;
            return 1f + (float)(-0.16 * Math.Exp(-5.0 * t) * Math.Cos(9.0 * t));
        }

        // 弹出曲线：从 1.09 起跳，带回弹收敛到 1.0（卡片从槽里"pop"出来的感觉）
        private static float PopScale(float t)
        {
            if (t <= 0f) return 1.09f;
            if (t >= 1f) return 1f;
            return 1f + (float)(0.09 * Math.Exp(-5.0 * t) * Math.Cos(9.0 * t));
        }

        // 按中心缩放矩形（用于按压/弹出反馈）
        private static Rectangle ScaleRect(Rectangle r, float s)
        {
            if (s > 0.995f && s < 1.005f) return r;
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            int nw = (int)Math.Round(r.Width * s), nh = (int)Math.Round(r.Height * s);
            return new Rectangle(cx - nw / 2, cy - nh / 2, nw, nh);
        }

        // ---------- 方案 D · 纵深 Space：主题色与深空绘制 ----------

        // 每应用一盏主题色：优先配置里可选的 "color":"#rrggbb"，其次内置 id 映射，最后稳定散列回退
        internal static Color AppHue(Agent a)
        {
            if (a != null)
            {
                string c = a.Color;
                if (!string.IsNullOrEmpty(c))
                {
                    try
                    {
                        string t = c.Trim().TrimStart('#');
                        if (t.Length == 6)
                            return Color.FromArgb(255,
                                Convert.ToInt32(t.Substring(0, 2), 16),
                                Convert.ToInt32(t.Substring(2, 2), 16),
                                Convert.ToInt32(t.Substring(4, 2), 16));
                    }
                    catch { }
                }
                if (!string.IsNullOrEmpty(a.Id))
                {
                    switch (a.Id.ToLowerInvariant())
                    {
                        case "doubao": return Color.FromArgb(255, 179, 64);     // 豆包·橙
                        case "dsh": return Color.FromArgb(255, 179, 64);        // DSH·橙
                        case "trae": return Color.FromArgb(79, 168, 255);       // Trae·蓝
                        case "qoder": return Color.FromArgb(139, 123, 255);     // Qoder·紫
                        case "codex": return Color.FromArgb(53, 196, 138);      // Codex·绿
                        case "tabbit": return Color.FromArgb(110, 200, 230);    // Tabbit·青
                        case "workbuddy": return Color.FromArgb(230, 180, 70);  // WorkBuddy·琥珀
                    }
                }
            }
            Color[] pal = new Color[]
            {
                AccentDefault, Color.FromArgb(90, 140, 255), Color.FromArgb(70, 190, 150),
                Color.FromArgb(200, 110, 220), Color.FromArgb(230, 90, 110), Color.FromArgb(110, 200, 230)
            };
            int h = Math.Abs((a != null && a.Name != null ? a.Name : "?").GetHashCode());
            return pal[h % pal.Length];
        }

        private static bool ColorClose(Color a, Color b)
        {
            return Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2;
        }

        // 环境光强度：显示应用时最强（点亮整层岩板），其余时候弱染
        private float AmbientAlpha()
        {
            return _shown >= 0 ? 1f : 0.45f;
        }

        // 噪点玻璃纹理：极淡颗粒，抵消纯色塑料感（一次性生成）
        private static Bitmap BuildNoise()
        {
            var bmp = new Bitmap(128, 128, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rnd = new Random(7);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                for (int i = 0; i < 2600; i++)
                {
                    int x = rnd.Next(0, 128), y = rnd.Next(0, 128);
                    int v = 150 + rnd.Next(0, 90);
                    int a = 4 + rnd.Next(0, 9);
                    bmp.SetPixel(x, y, Color.FromArgb(a, v, v, Math.Min(255, v + 8)));
                }
            }
            return bmp;
        }

        // 把白底 logo 去底：近白像素按亮度做透明，保留彩色主体（开屏深空用）
        private static Bitmap KeyWhite(Bitmap src)
        {
            if (src == null) return null;
            var dst = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            int len = src.Width * src.Height;
            var rect = new Rectangle(0, 0, src.Width, src.Height);
            var bd = src.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            byte[] sb = new byte[len * 4];
            System.Runtime.InteropServices.Marshal.Copy(bd.Scan0, sb, 0, sb.Length);
            src.UnlockBits(bd);
            for (int i = 0; i < len; i++)
            {
                int k = i * 4;
                byte b = sb[k], gr = sb[k + 1], r = sb[k + 2], a = sb[k + 3];
                int mn = Math.Min(r, Math.Min(gr, b));
                if (mn > 180) a = (byte)Math.Max(0, Math.Min(255, (int)((255 - mn) * 3.2f)));
                sb[k + 3] = a;
            }
            var dd = dst.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(sb, 0, dd.Scan0, sb.Length);
            dst.UnlockBits(dd);
            return dst;
        }

        // 星空底图缓存：星云晕染 + 星点
        private void EnsureStarfield(int w, int h)
        {
            if (w <= 0 || h <= 0) return;
            if (_starBmp != null && _starSize == new Size(w, h)) return;
            if (_starBmp != null) { _starBmp.Dispose(); _starBmp = null; }
            _starBmp = new Bitmap(w, h);
            _starSize = new Size(w, h);
            using (var g = Graphics.FromImage(_starBmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.FromArgb(10, 12, 16));
                DrawNebula(g, w, h, 0.62f, 0.20f, Color.FromArgb(120, 100, 200), 0.13f, Math.Max(120, Px(420)));
                DrawNebula(g, w, h, 0.22f, 0.58f, Color.FromArgb(60, 140, 200), 0.10f, Math.Max(110, Px(360)));
                DrawNebula(g, w, h, 0.84f, 0.74f, Color.FromArgb(200, 120, 90), 0.07f, Math.Max(100, Px(300)));
                var rnd = new Random(20261019);
                for (int i = 0; i < 180; i++)
                {
                    int x = rnd.Next(0, w), y = rnd.Next(0, h);
                    int r2 = 1 + rnd.Next(0, 2);
                    int a = 30 + rnd.Next(0, 140);
                    int warm = rnd.Next(0, 5);
                    Color sc = warm == 0 ? Color.FromArgb(a, 255, 224, 196)
                              : warm == 1 ? Color.FromArgb(a, 186, 214, 255)
                              : warm == 2 ? Color.FromArgb(a, 226, 208, 255)
                              : warm == 3 ? Color.FromArgb(a, 200, 232, 255)
                              : Color.FromArgb(a, 255, 255, 255);
                    using (var b = new SolidBrush(sc))
                        g.FillEllipse(b, x, y, r2 + 1, r2 + 1);
                }
            }
        }

        private static void DrawNebula(Graphics g, int w, int h, float fx, float fy, Color c, float alpha, int rad)
        {
            int cx = (int)(w * fx), cy = (int)(h * fy);
            Rectangle grc = new Rectangle(cx - rad / 2, cy - rad / 2, rad, rad);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(grc);
                using (var pgb = new PathGradientBrush(path))
                {
                    pgb.CenterColor = Color.FromArgb((int)(255 * alpha), c.R, c.G, c.B);
                    pgb.SurroundColors = new Color[] { Color.FromArgb(0, c.R, c.G, c.B) };
                    g.FillEllipse(pgb, grc);
                }
            }
        }

        private void DrawStarfield(Graphics g, Rectangle r)
        {
            EnsureStarfield(r.Width, r.Height);
            if (_starBmp == null) { g.Clear(Bg); return; }
            g.DrawImageUnscaled(_starBmp, r.X, r.Y);
        }

        // 整层环境光：屏幕随选中应用色缓慢染上一层
        // 开屏停留页烘焙：入场完成后整页画进离屏位图，停留期每帧只贴图 + 动态星芒
        private void BakeBootPage()
        {
            if (_bootBmp != null && ColorClose(_acc, _bootAcc)) return;
            if (_bootBmp != null) { _bootBmp.Dispose(); _bootBmp = null; }
            try
            {
                _bootBmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
                using (var gg = Graphics.FromImage(_bootBmp))
                {
                    gg.SmoothingMode = SmoothingMode.AntiAlias;
                    gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    float savedEnter = _enterT, savedBreath = _breath;
                    _enterT = 1f; _breath = 0f;
                    DrawStarfield(gg, new Rectangle(0, 0, Width, Height));
                    DrawModeChooser(gg);
                    _enterT = savedEnter; _breath = savedBreath;
                }
                _bootAcc = _acc;
                Program.Log("bake ok " + Width + "x" + Height);
            }
            catch (Exception ex)
            {
                Program.Log("bake failed: " + ex.Message);
                if (_bootBmp != null) { _bootBmp.Dispose(); _bootBmp = null; }
            }
        }

        // 工作区静态背景烘焙：星空 + 整层环境光 + 光带 一次性画进位图。
        // 静止期每帧只按无效区贴背景（GDI+ 只 blit 该区域），避免每帧全屏重绘 + 5120px 渐变填充。
        private void BakeWorkBg()
        {
            bool wantBeam = _shown < 0 && _mode >= 0;
            if (_bgBmp != null && ColorClose(_acc, _bgAcc) && _bgShown == _shown) return;
            if (_bgBmp != null) { _bgBmp.Dispose(); _bgBmp = null; }
            try
            {
                _bgBmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
                using (var gg = Graphics.FromImage(_bgBmp))
                {
                    gg.SmoothingMode = SmoothingMode.AntiAlias;
                    gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    DrawStarfield(gg, new Rectangle(0, 0, Width, Height));
                    Color hue = _acc;
                    int gr = Math.Max(Width, Height) * 2;
                    Rectangle grc = new Rectangle(-gr / 3, -(gr - Height) / 2, gr, gr);
                    using (var path = new GraphicsPath())
                    {
                        path.AddEllipse(grc);
                        using (var pgb = new PathGradientBrush(path))
                        {
                            pgb.CenterColor = Color.FromArgb((int)(16 * AmbientAlpha() + 6), hue.R, hue.G, hue.B);
                            pgb.SurroundColors = new Color[] { Color.FromArgb(0, hue.R, hue.G, hue.B) };
                            gg.FillEllipse(pgb, grc);
                        }
                    }
                    if (wantBeam)
                    {
                        int bw = Math.Max(Px(24), Math.Min(Px(150), Width - _sideW));
                        if (bw > 1)
                        {
                            Rectangle rr = new Rectangle(_sideW, 0, bw, Height);
                            using (var lg = new LinearGradientBrush(rr,
                                Color.FromArgb((int)(55 * AmbientAlpha()), hue.R, hue.G, hue.B),
                                Color.FromArgb(0, hue.R, hue.G, hue.B), LinearGradientMode.Horizontal))
                                gg.FillRectangle(lg, rr);
                        }
                    }
                }
                _bgAcc = _acc; _bgShown = _shown;
                Program.Log("bg bake ok");
            }
            catch (Exception ex)
            {
                Program.Log("bg bake failed: " + ex.Message);
                if (_bgBmp != null) { _bgBmp.Dispose(); _bgBmp = null; }
            }
        }

        // 开屏中央光晕：PathGradientBrush 缓存复用（每帧仅改 CenterColor，不重建刷子，提帧率关键）
        private void DrawCenterHalo(Graphics g, int cx, int cy, int gr, Color center, Color surround)
        {
            if (_haloBrush == null)
            {
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(cx - gr / 2f, cy - gr / 2f, gr, gr);
                    _haloBrush = new PathGradientBrush(path);
                    _haloBrush.SurroundColors = new Color[] { surround };
                }
            }
            _haloBrush.CenterColor = center;
            g.FillEllipse(_haloBrush, new Rectangle(cx - gr / 2, cy - gr / 2, gr, gr));
        }

        private void DrawAmbient(Graphics g, Rectangle full)
        {
            Color hue = _acc;
            int gr = Math.Max(full.Width, full.Height) * 2;
            Rectangle grc = new Rectangle(full.X - gr / 3, full.Y - (gr - full.Height) / 2, gr, gr);
            if (_ambientBrush == null)
            {
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(grc);
                    _ambientBrush = new PathGradientBrush(path);
                    _ambientBrush.SurroundColors = new Color[] { Color.FromArgb(0, hue.R, hue.G, hue.B) };
                }
            }
            _ambientBrush.CenterColor = Color.FromArgb((int)(16 * AmbientAlpha() + 6), hue.R, hue.G, hue.B);
            g.FillEllipse(_ambientBrush, grc);
        }

        // 深空岩板 Dock：纵向明暗 + 顶部高光棱 + 噪点玻璃 + 底部内阴影 + 选中整层点亮
        private void DrawRockSlab(Graphics g, int x, int w, int h)
        {
            if (w <= 0 || h <= 0) return;
            Rectangle side = new Rectangle(x, 0, w, h);
            int hi = Math.Max(2, Px(30));
            int sh = Math.Max(2, Px(56));
            if (_slabBrush == null)
            {
                _slabBrush = new LinearGradientBrush(new Rectangle(0, 0, w, h),
                    Color.FromArgb(24, 26, 36), Color.FromArgb(12, 14, 20), LinearGradientMode.Vertical);
                _slabHiBrush = new LinearGradientBrush(new Rectangle(0, 0, w, hi),
                    Color.FromArgb(70, 255, 255, 255), Color.Transparent, LinearGradientMode.Vertical);
                _slabShBrush = new LinearGradientBrush(new Rectangle(0, h - sh, w, sh),
                    Color.Transparent, Color.FromArgb(110, 0, 0, 0), LinearGradientMode.Vertical);
                if (_noiseBmp != null)
                {
                    _slabTexBrush = new TextureBrush(_noiseBmp);
                    _slabTexBrush.WrapMode = WrapMode.Tile;
                }
                _slabPen = new Pen(Color.FromArgb(55, 255, 255, 255));
            }
            g.FillRectangle(_slabBrush, side);
            g.FillRectangle(_slabHiBrush, new Rectangle(side.X, 0, side.Width, hi));
            _slabPen.Color = Color.FromArgb(55, 255, 255, 255);
            g.DrawLine(_slabPen, side.X + Px(4), 1, side.Right - Px(4), 1);
            if (_slabTexBrush != null)
                g.FillRectangle(_slabTexBrush, side);
            g.FillRectangle(_slabShBrush, new Rectangle(side.X, side.Bottom - sh, side.Width, sh));
            if (_shown >= 0 && _shown < _agents.Count)
            {
                Rectangle pr = ShownPillRect(_shown);
                Color hue = AppHue(_agents[_shown]);
                int cx = pr.X + pr.Width / 2, cy = pr.Y + pr.Height / 2;
                int gr = Math.Max(Px(150), (int)(pr.Width * 2.4f));
                Rectangle grc = new Rectangle(cx - gr / 2, cy - gr / 2, gr, gr);
                if (_pillHaloBrush == null)
                {
                    using (var path = new GraphicsPath())
                    {
                        path.AddEllipse(grc);
                        _pillHaloBrush = new PathGradientBrush(path);
                        _pillHaloBrush.SurroundColors = new Color[] { Color.FromArgb(0, hue.R, hue.G, hue.B) };
                    }
                }
                float boost = (_popT < 1f && _popIdx == _shown) ? 1f + 0.9f * (1f - _popT) : 1f;   // 弹出瞬间 Dock 点亮脉冲
                _pillHaloBrush.CenterColor = Color.FromArgb((int)Math.Min(255, 58 * AmbientAlpha() * boost), hue.R, hue.G, hue.B);
                g.FillEllipse(_pillHaloBrush, grc);
            }
            _slabPen.Color = Color.FromArgb(44, 46, 58);
            g.DrawLine(_slabPen, side.Right - 1, 0, side.Right - 1, h);
        }

        // 光带：无应用铺满内容区时，从 Dock 右缘流向内容（圆锥软光面）
        private void DrawBeam(Graphics g)
        {
            if (_shown >= 0 || _mode < 0) return;
            int bw = Math.Max(Px(24), Math.Min(Px(150), Width - _sideW));
            if (bw <= 1) return;
            Rectangle rr = new Rectangle(_sideW, 0, bw, Height);
            Color hue = _acc;
            if (_beamBrush == null)
                _beamBrush = new LinearGradientBrush(rr, Color.Black, Color.Transparent, LinearGradientMode.Horizontal);
            _beamBrush.LinearColors = new Color[]
            {
                Color.FromArgb((int)(55 * AmbientAlpha()), hue.R, hue.G, hue.B),
                Color.FromArgb(0, hue.R, hue.G, hue.B)
            };
            g.FillRectangle(_beamBrush, rr);
        }

        // 星芒闪烁点缀：开屏用
        private static readonly float[,] SparkSpots = new float[,]
        {
            { 0.12f, 0.20f }, { 0.30f, 0.10f }, { 0.52f, 0.24f }, { 0.72f, 0.13f }, { 0.88f, 0.30f },
            { 0.20f, 0.50f }, { 0.44f, 0.38f }, { 0.64f, 0.55f }, { 0.84f, 0.68f }, { 0.36f, 0.72f },
            { 0.08f, 0.40f }, { 0.95f, 0.52f }
        };
        private void DrawSparks(Graphics g, Rectangle full)
        {
            using (var pen = new Pen(Color.FromArgb(214, 204, 255), Math.Max(1f, (float)Px(1))))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                for (int i = 0; i < SparkSpots.GetLength(0); i++)
                {
                    float tw = (float)Math.Sin(_breath * 1.7f + i * 0.9f);
                    float a = 0.12f + 0.55f * (tw * 0.5f + 0.5f);
                    int x = (int)(full.Width * SparkSpots[i, 0]);
                    int y = (int)(full.Height * SparkSpots[i, 1]);
                    int s = Px(3 + (i % 3));
                    pen.Color = Color.FromArgb((int)(255 * a), 214, 204, 255);
                    g.DrawLine(pen, x - s, y, x + s, y);
                    g.DrawLine(pen, x, y - s, x, y + s);
                }
            }
        }

        // 选中「浮起」的岩板亮面：外发光 + 应用色渐变 + 顶部高光 + 描边
        private void DrawSelectedPill(Graphics g, Rectangle pr, Color hue)
        {
            float boost = (_popT < 1f && _popIdx == _shown) ? 1f + 0.8f * (1f - _popT) : 1f;   // 弹出瞬间光晕脉冲
            using (var glow = new Pen(Color.FromArgb((int)Math.Min(255, 85 * boost), hue.R, hue.G, hue.B),
                Math.Max(4f, Px(6) * boost)))
            using (GraphicsPath gp = Round(new Rectangle(pr.X - Px(4), pr.Y - Px(4), pr.Width + Px(8), pr.Height + Px(8)), Px(14)))
                g.DrawPath(glow, gp);

            using (GraphicsPath p = Round(pr, Px(12)))
            {
                using (var lg = new LinearGradientBrush(pr, Blend(hue, Color.FromArgb(24, 27, 38), 0.72f), Color.FromArgb(12, 13, 18), LinearGradientMode.Vertical))
                    g.FillPath(lg, p);
                var clip = g.Save();
                g.SetClip(p);
                using (var lg2 = new LinearGradientBrush(new Rectangle(pr.X, pr.Y, pr.Width, Math.Max(2, pr.Height / 2)),
                    Color.FromArgb(50, 255, 255, 255), Color.Transparent, LinearGradientMode.Vertical))
                    g.FillRectangle(lg2, pr);
                g.Restore(clip);
                using (var pen = new Pen(Blend(hue, Color.White, 0.18f), Math.Max(1.2f, (float)Px(1))))
                    g.DrawPath(pen, p);
            }
        }

        private void Kick()
        {
            if (_anim.Enabled) return;
            Native.timeBeginPeriod(1);   // 让 7ms 的节拍真的跑起来
            _anim.Start();
        }

        private void AnimTick(object sender, EventArgs e)
        {
            bool active = false;
            _fpsFrames++;
            if ((DateTime.Now - _fpsMark).TotalMilliseconds >= 1000)
            {
                Program.Log("fps=" + _fpsFrames);
                _fpsFrames = 0;
                _fpsMark = DateTime.Now;
            }

            if (_introT < 1f) { _introT = Approach(_introT, 1f, 0.06f); active = true; }
            if (_mode < 0 && _shown < 0 && _enterT < 1f)
            {
                // 按真实时间推进，不数 tick：定时器精度不稳定时节奏才不会忽快忽慢
                _enterT = Clamp01((float)_enterClock.Elapsed.TotalSeconds / _enterDur);
                if (_enterT >= 1f) _enterT = 1f;
                _breath += 0.10f;   // 开屏星芒闪烁相位
                active = true;
            }
            else if (_mode < 0 && _shown < 0 && _enterT >= 1f && _modeSel < 0)
            {
                _breath += 0.07f;   // 停留模式选择页：星芒继续闪烁（页面已烘焙，开销极小）
                active = true;
            }
            if (_hoverT < 1f) { _hoverT = Approach(_hoverT, 1f, 0.22f); active = true; }
            if (_pressT > 0f)
            {
                // 按压保持阶段钉在 1，松开后平滑衰减（配合 ReleaseSpring 做回弹）
                _pressT = _pressHeld ? Approach(_pressT, 1f, 0.6f) : Approach(_pressT, 0f, 0.14f);
                active = true;
            }
            if (_relT < 1f) { _relT = Approach(_relT, 1f, 0.13f); active = true; }
            if (_rippleT < 1f) { _rippleT = Approach(_rippleT, 1f, 0.08f); active = true; }
            if (_popT < 1f) { _popT = Approach(_popT, 1f, 0.11f); active = true; }
            if (_modeT < 1f)
            {
                _modeT = Approach(_modeT, 1f, 0.032f);   // 模式转场约 300ms（原 600ms，已减半）
                if (_modeT >= 1f)
                {
                    _modeT = 1f;
                    if (_transFrames > 0)
                        Program.Log("trans fps=" + (int)(_transFrames / Math.Max(0.01, (DateTime.Now - _transMark).TotalSeconds)) +
                            " dur=" + (int)((DateTime.Now - _transMark).TotalMilliseconds) + "ms");
                    _transFrames = 0;
                }
                else
                {
                    if (_transFrames == 0) _transMark = DateTime.Now;
                    _transFrames++;
                }
                active = true;
            }
            else if (_modeSel >= 0) { _modeSel = -1; active = true; }   // 转场完成清理
            if (_expandT < 1f) { _expandT = Approach(_expandT, 1f, 0.20f); active = true; }
            if (_pendingIdx >= 0) { _spin = (_spin + 12) % 360; active = true; }
            if (_tiled && _synPanel == null) { TileTick(); UpdateThumbs(); active = true; }
            if (_tiled && _tileIn < 1f) { _tileIn = Approach(_tileIn, 1f, 0.16f); active = true; }

            // 图标位置动画：向目标槽位缓动，延迟用来做"依次弹出"
            for (int k = 0; k < _order.Count; k++)
            {
                string id = _order[k];
                float d;
                if (_delay.TryGetValue(id, out d) && d > 0f)
                {
                    _delay[id] = d - 0.016f;
                    active = true;
                    continue;
                }
                float cur;
                if (!_y.TryGetValue(id, out cur)) cur = ItemTopPx;
                float target = ItemTopPx + k * ItemStepPx;
                if (Math.Abs(cur - target) > 0.15f)
                {
                    _y[id] = cur + (target - cur) * 0.17f;
                    active = true;
                }
                else if (cur != target)
                {
                    _y[id] = target;
                    active = true;
                }
            }

            float st = DockGone ? 1f : 0f;
            if (Math.Abs(_slideT - st) > 0.002f)
            {
                _slideT = Approach(_slideT, st, 0.30f);
                ApplyBounds();
                active = true;
            }
            else if (_slideT != st)
            {
                _slideT = st;
                ApplyBounds();
            }

            // 方案 D：环境光向目标应用色平滑过渡（约 500ms 色彩交叉渐变）
            Color want = AccentDefault;
            if (_shown >= 0 && _shown < _agents.Count) want = AppHue(_agents[_shown]);
            else
                foreach (int ci in _chosen)
                    if (ci >= 0 && ci < _agents.Count) { want = AppHue(_agents[ci]); break; }
            if (_acc != want)
            {
                _acc = Blend(_acc, want, 0.045f);
                if (ColorClose(_acc, want)) _acc = want;
                active = true;
            }

            // 运行光点呼吸：已选应用常驻轻动效（只重绘 Dock 区域）
            bool dockBreath = _mode >= 0 && _chosen.Count > 0 && !DockGone;
            if (dockBreath)
            {
                _breath += 0.07f;
                active = true;
            }

            // 动画收敛后清理按压对象（松开回弹完成）
            if (!_pressHeld && _pressT <= 0f && _relT >= 1f)
            {
                if (_pressIdx >= 0) { _pressIdx = -1; active = true; }
                if (_miscObj != -1) { _miscObj = -1; active = true; }
            }

            if (active)
            {
                bool settled = Math.Abs(_slideT - st) < 0.002f && _acc == want;
                Invalidate(dockBreath && settled ? new Rectangle(0, 0, _sideW, Height) : ClientRectangle);
            }
            else { _anim.Stop(); Native.timeEndPeriod(1); }
        }

        private void ApplyBounds()
        {
            if (!IsHandleCreated || IsDisposed) return;
            Rectangle scr = Screen.PrimaryScreen.WorkingArea;
            // 没有显示任何应用时铺满工作区（模式选择页），有应用时只占侧栏
            int w = (DockGone || _shown >= 0) ? Math.Max(HotStripPx, _sideW) : scr.Width;
            int x = scr.X;
            if (DockGone || _shown >= 0) x -= (int)Math.Round(_slideT * Math.Max(0, w - HotStripPx));
            if (Bounds.Width != w || Bounds.X != x || Bounds.Y != scr.Y || Bounds.Height != scr.Height)
                Bounds = new Rectangle(x, scr.Y, w, scr.Height);
            LayoutSynergyPanel();   // 面板跟随主窗口/内容区尺寸变化
        }

        // ---------- 绘制 ----------

        private int AgentIndex(string id)
        {
            for (int i = 0; i < _agents.Count; i++) if (_agents[i].Id == id) return i;
            return -1;
        }

        private float ItemTopFor(string id)
        {
            int k = _order.IndexOf(id);
            if (k < 0) k = 0;
            return ItemTopPx + k * ItemStepPx;
        }

        private Rectangle ItemRectNow(int i)
        {
            if (i < 0 || i >= _agents.Count) return new Rectangle(ItemXPx, ItemTopPx, ItemSizePx, ItemSizePx);
            string id = _agents[i].Id;
            float y;
            if (!_y.TryGetValue(id, out y)) y = ItemTopFor(id);
            return new Rectangle(ItemXPx, (int)Math.Round(y), ItemSizePx, ItemSizePx);
        }

        // 顺序：优先用上次保存的，缺的按配置顺序补在后面。
        // 初始位置全部叠在最上面，靠延迟依次铺开 —— 就是启动时那一下"堆叠弹出"。
        private void BuildOrder(List<string> saved)
        {
            _order.Clear();
            if (saved != null)
                foreach (string id in saved)
                    if (AgentIndex(id) >= 0 && !_order.Contains(id)) _order.Add(id);
            foreach (Agent a in _agents)
                if (!_order.Contains(a.Id)) _order.Add(a.Id);

            _y.Clear();
            _delay.Clear();
            for (int k = 0; k < _order.Count; k++)
            {
                _y[_order[k]] = ItemTopPx + k * 6f;   // 先压成一叠，再散开
                _delay[_order[k]] = k * 0.085f;
            }
        }

        private void MoveToFront(int i)
        {
            if (i < 0 || i >= _agents.Count) return;
            string id = _agents[i].Id;
            if (_order.Count == 0 || _order[0] != id)
            {
                _order.Remove(id);
                _order.Insert(0, id);
            }
            foreach (string k in _order) _delay[k] = 0f;
            Kick();
            SaveSessionNow();
        }

        private Rectangle ShownPillRect(int i)
        {
            Rectangle baseR = ItemRectNow(i);
            int full = _sideW - Px(8);
            int w = Lerp(baseR.Width, Math.Max(baseR.Width, full), EaseOut(_expandT));
            return new Rectangle(Px(4), baseR.Y, w, baseR.Height);
        }

        private Rectangle ExitRect() { return new Rectangle(ItemXPx, Height - Px(124), ItemSizePx, ItemSizePx); }

        private static GraphicsPath Round(Rectangle r, int rad)
        {
            if (r.Width < 4 || r.Height < 4)
            {
                var flat = new GraphicsPath();
                flat.AddRectangle(new Rectangle(r.X, r.Y, Math.Max(1, r.Width), Math.Max(1, r.Height)));
                return flat;
            }
            int d = Math.Max(2, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // 诊断：记录谁把前台抢走了
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            try
            {
                IntPtr fg = Native.GetForegroundWindow();
                Program.Log("deactivate fg=" + Native.Title(fg) + " textFocus=" + AnyTextFocused(this));
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            bool boot = _shown < 0 && _mode < 0;   // 还没选模式：整屏只有动画和卡片，左右侧栏都不出现
            Rectangle full = new Rectangle(0, 0, Math.Max(1, Width), Math.Max(1, Height));

            if (boot)
            {
                // 方案 D：深空开屏（星空 + 星芒 + 模式选择）
                if (_enterT >= 1f && _modeSel < 0)
                {
                    // 停留期：烘焙静态页 + 动态星芒，避免每帧全屏重绘整个开屏页（帧率关键）
                    BakeBootPage();
                    if (_bootBmp != null)
                    {
                        g.DrawImageUnscaled(_bootBmp, 0, 0);
                        DrawSparks(g, full);
                        return;
                    }
                }
                DrawStarfield(g, full);
                DrawModeChooser(g);
                return;
            }

            // 非 boot：烘焙静态背景（星空+环境光+光带）按无效区贴图 → 岩板 → 转场/选择页 → 选中卡
            BakeWorkBg();
            Rectangle clipRect = e.ClipRectangle;
            if (clipRect.Width > 0 && clipRect.Height > 0)
            {
                if (_bgBmp != null)
                {
                    // 1:1 源区拷贝：GDI+ 只处理 ClipRectangle 内的像素（静止期只重绘 Dock 区域，极快）
                    g.DrawImage(_bgBmp, new Rectangle(clipRect.X, clipRect.Y, clipRect.Width, clipRect.Height),
                        clipRect.X, clipRect.Y, clipRect.Width, clipRect.Height, GraphicsUnit.Pixel);
                }
                else
                {
                    DrawStarfield(g, full);
                    DrawAmbient(g, full);
                }
            }
            else
            {
                DrawStarfield(g, full);
                DrawAmbient(g, full);
            }
            int dx = 0;
            if (_modeT < 1f && _modeSel >= 0)   // 模式转场：Dock 从左侧滑入
                dx = -(int)((1f - EaseOut(Clamp01(_modeT / 0.55f))) * _sideW);
            DrawRockSlab(g, dx, _sideW, Height);
            if (_bgBmp == null) DrawBeam(g);

            if (_shown < 0)
            {
                if (_modeT < 1f && _modeSel >= 0) DrawModeTransition(g);   // 转场：模式卡收拢淡出
                else DrawModeChooser(g);
            }

            if (_shown >= 0 && _shown < _agents.Count)
            {
                Rectangle pr = ShownPillRect(_shown);
                if (_popT < 1f && _popIdx == _shown) pr = ScaleRect(pr, PopScale(_popT));   // 选中瞬间卡片弹出回弹
                DrawSelectedPill(g, pr, AppHue(_agents[_shown]));
            }

            if (_introT <= 0f) return;

            bool interactive = _introT > 0.5f;

            for (int k = 0; k < _order.Count; k++)
            {
                int i = AgentIndex(_order[k]);
                if (i < 0) continue;

                float ap = EaseOut(Clamp01((_introT - k * 0.06f) / 0.30f));
                if (ap <= 0f) continue;

                Rectangle r = ItemRectNow(i);
                int dy = 0;

                bool isShown = i == _shown;
                bool isChosen = _chosen.Contains(i);
                Color hue = AppHue(_agents[i]);

                if (!isShown && i == _hover)
                {
                    Rectangle hr = r;
                    if (_pressIdx == i && _pressT > 0f)
                    {
                        int sh = (int)Math.Round(6 * _pressT * _s);   // 按压更明显的背景收缩
                        hr = new Rectangle(hr.X + sh, hr.Y + sh, hr.Width - sh * 2, hr.Height - sh * 2);
                    }
                    using (GraphicsPath p = Round(hr, Px(12)))
                    using (var b = new SolidBrush(Blend(SideBg, BgHover, _hoverT * ap)))
                        g.FillPath(b, p);
                }

                if (!isShown && isChosen)
                {
                    Rectangle cr = r;
                    if (_pressIdx == i && _pressT > 0f)
                    {
                        int sh = (int)Math.Round(5 * _pressT * _s);
                        cr = new Rectangle(cr.X + sh, cr.Y + sh, cr.Width - sh * 2, cr.Height - sh * 2);
                    }
                    using (GraphicsPath p = Round(cr, Px(12)))
                    using (var pen = new Pen(Blend(SideBg, hue, Math.Max(0.35f, ap)), Math.Max(1.6f, Px(2) * 0.9f)))
                        g.DrawPath(pen, p);
                }

                if (isShown)
                {
                    Rectangle pr = ShownPillRect(i);
                    pr.Offset(0, dy);

                    int isz = Px(18);
                    Rectangle ir2 = new Rectangle(pr.X + Px(8), pr.Y + (pr.Height - isz) / 2, isz, isz);
                    Bitmap b2 = Icons.Get(_agents[i]);
                    if (b2 == null) b2 = Icons.Letter(_agents[i].Name, 40);
                    if (b2 != null && pr.Width > Px(46))
                    {
                        // 图标底光：应用色微晕
                        int gr = Px(30);
                        Rectangle grc = new Rectangle(ir2.X - (gr - ir2.Width) / 2, ir2.Y - (gr - ir2.Height) / 2, gr, gr);
                        using (var path = new GraphicsPath())
                        {
                            path.AddEllipse(grc);
                            using (var pgb = new PathGradientBrush(path))
                            {
                                pgb.CenterColor = Color.FromArgb(110, hue.R, hue.G, hue.B);
                                pgb.SurroundColors = new Color[] { Color.FromArgb(0, hue.R, hue.G, hue.B) };
                                g.FillEllipse(pgb, grc);
                            }
                        }
                        g.DrawImage(b2, ir2);
                    }

                    if (pr.Width > Px(46))
                    {
                        Rectangle tr = new Rectangle(pr.X + Px(30), pr.Y, pr.Width - Px(34), pr.Height);
                        TextRenderer.DrawText(g, _agents[i].Name, _fName, tr, TextMain,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    }
                }
                else
                {
                    Rectangle ir = r;
                    ir.Offset(0, dy);
                    DrawTile(g, _agents[i], ir);

                    if (_pendingIdx == i)
                    {
                        int d = Px(18);
                        Rectangle rr = new Rectangle(ir.Right - d / 2 - Px(1), ir.Bottom - d / 2 - Px(1), d, d);
                        using (var pRing2 = new Pen(Color.FromArgb(70, hue.R, hue.G, hue.B), Math.Max(1.6f, Px(2) * 0.8f)))
                        using (var pOrange2 = new Pen(hue, Math.Max(1.6f, Px(2) * 0.9f)))
                        {
                            g.DrawArc(pRing2, rr, 0, 360);
                            g.DrawArc(pOrange2, rr, _spin, 100);
                        }
                    }
                }
            }

            if (interactive && _agents.Count < 18) DrawPlus(g);
            if (interactive) DrawExit(g, ExitRect(), _hoverExit);
        }

        private int _mode = -1;   // -1 未选，0 普通工作站，1 多 agent 协同

        private void CardRects(out Rectangle a, out Rectangle b)
        {
            int left0 = _mode < 0 ? 0 : _sideW;
            int cw = Width - left0 - (RightShown ? RightWPx : 0);
            int gap = Px(48);
            int w = Math.Max(Px(200), Math.Min(Px(540), (cw - gap * 3) / 2));
            int h = Px(300);
            int x0 = left0 + (cw - (w * 2 + gap)) / 2;
            int y = (int)(Height * 0.44f);
            a = new Rectangle(x0, y, w, h);
            b = new Rectangle(x0 + w + gap, y, w, h);
        }

        private Pipeline _pipe;
        private bool _picking;
        private bool _tiled;
        // 协同自有控件界面：非空时用它展示输入/输出/状态，替代嵌入 AI 窗口与 DWM 缩略图
        private SynergyPanel _synPanel;
        private float _tileIn;
        private readonly bool[] _tileDone = new bool[3];
        private readonly IntPtr[] _thumbs = new IntPtr[3];
        private int _focusIdx = -1;
        private int _hoverBtn = -1;
        private readonly List<int> _roles = new List<int>();
        private string _lastCopied = "";
        private string _lastAdvance = "";
        private int _lastAdvanceRound = -1;
        private int _lastAdvanceIndex = -1;
        private DateTime _pipeAt = DateTime.Now;

        // 协同模式的独立对话窗：每个角色一个，常驻不销毁
        private readonly AgentDialog[] _dialogs = new AgentDialog[3];
        // 任务需求输入框（协同未开始时显示在主区）
        private TextBox _taskBox;

        // 鼠标落点是否在可输入控件上（输入法需要活动窗口）
        private bool AnyTextFocused(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is TextBoxBase && c.Focused) return true;
                if (AnyTextFocused(c)) return true;
            }
            return false;
        }

        private bool OnInputAtCursor()
        {
            Point cp = PointToClient(Cursor.Position);
            Control c = GetChildAtPoint(cp, GetChildAtPointSkip.Invisible);
            // 逐层往下钻：GetChildAtPoint 只看直接子控件，SynergyPanel 这种容器里才是输入框
            while (c != null)
            {
                if (c is TextBoxBase) return true;
                Control inner = c.GetChildAtPoint(c.PointToClient(Cursor.Position), GetChildAtPointSkip.Invisible);
                if (inner == null || inner == c) break;
                c = inner;
            }
            return _taskBox != null && _taskBox.Visible && _taskBox.Bounds.Contains(cp);
        }
        // 轮次历史列表的悬停项
        private int _hoverRound = -1;

        // 三个角色的固定名称：卡片位置 = 角色位置，不随 agent 换而变
        internal static readonly string[] RoleNames = { "拆解", "执行", "自检" };

        // ---------- 会话/对话窗入口（供 AgentDialog 回调） ----------

        internal PipeSession SessionOf() { return _pipe != null ? _pipe.Session : null; }

        // 打开（或前置）某张卡的对话窗
        internal void OpenDialog(int k)
        {
            if (k < 0 || k >= _roles.Count) return;
            int idx = _roles[k];
            if (idx < 0 || idx >= _agents.Count) return;
            if (_dialogs[k] == null || _dialogs[k].IsDisposed)
                _dialogs[k] = new AgentDialog(this, k, _agents[idx].Id, _agents[idx].Name, RoleNames[k]);
            _dialogs[k].RefreshHistory();
            _dialogs[k].Show();
            _dialogs[k].Activate();
        }

        // AgentDialog 里的「补充需求并发送」
        internal string DialogAddSupplement(int k, string text)
        {
            if (_pipe == null) return null;
            int idx = _roles[k];
            var s = _pipe.AddSupplement(_agents[idx].Id, RoleNames[k], text);
            if (s == null) return null;
            string hint = _pipe.SupplementHint();
            Invalidate();
            return hint;
        }

        // AgentDialog 里的「打开应用窗口」
        internal void FocusRoleCard(int k) { FocusTile(k); }

        // 对话窗在退出协同/移除应用/程序退出时都要关掉
        private void CloseDialogs()
        {
            for (int k = 0; k < 3; k++)
                if (_dialogs[k] != null && !_dialogs[k].IsDisposed) _dialogs[k].Close();
        }

        // 协同入口已由「切换模式」取代，这里返回空矩形：不绘制、也不响应点击
        private Rectangle PipeEntryRect() { return Rectangle.Empty; }

        // ---------- 纵向卡片流水线 ----------

        // 三张卡片从上到下；右侧留出轮次面板的宽度（Px(340)），
        // 卡片宽度 = 内容区减面板。
        private Rectangle[] TileRects()
        {
            int left = _sideW + Px(20);
            int rightW = Px(340);
            int totalW = Width - left - rightW - Px(20);
            int top = Px(64);
            int bottomBar = Px(96);
            int gap = Px(12);
            int h = (Height - top - bottomBar - gap * 2) / 3;
            var r = new Rectangle[3];
            for (int k = 0; k < 3; k++) r[k] = new Rectangle(left, top + k * (h + gap), totalW, h);
            return r;
        }

        // 右侧轮次面板：当前轮次 / 已完成轮数 / 当前阶段 / 各轮结论
        private Rectangle RoundPanelRect()
        {
            return new Rectangle(Width - Px(340) - Px(20) + Px(12), Px(64), Px(340), Height - Px(64) - Px(96));
        }

        private Rectangle RoundItemRect(int i)
        {
            Rectangle p = RoundPanelRect();
            return new Rectangle(p.X + Px(10), p.Y + Px(120) + i * Px(60), p.Width - Px(20), Px(52));
        }

        private const int CardHeaderH = 40;   // 头部状态带高度（逻辑 px）

        // 缩略图区 = 卡片去掉头部后的剩余
        private Rectangle CardThumbRect(Rectangle card)
        {
            return new Rectangle(card.X, card.Y + Px(CardHeaderH), card.Width, card.Height - Px(CardHeaderH + 2));
        }

        // 卡片头部右侧「确认交给下一步」按钮（仅待确认的卡片有）
        private Rectangle CardConfirmRect(Rectangle card)
        {
            return new Rectangle(card.Right - Px(200), card.Y + Px(7), Px(190), Px(26));
        }

        // 卡片头部的「查看对话」「补充需求」按钮
        private Rectangle CardDialogRect(Rectangle card)
        {
            return new Rectangle(card.Right - Px(390), card.Y + Px(7), Px(88), Px(26));
        }
        private Rectangle CardSuppRect(Rectangle card)
        {
            return new Rectangle(card.Right - Px(294), card.Y + Px(7), Px(88), Px(26));
        }

        // 底部操作带的「自动接力」开关
        private Rectangle PipeAutoRect()
        {
            return new Rectangle(_sideW + Px(24), Height - Px(62), Px(170), Px(36));
        }

        // 阶段 → 角色卡片：固定四段式，拆解和对齐都在卡 1
        private int CardOfPhase(PipePhase ph)
        {
            switch (ph)
            {
                case PipePhase.Decompose: return 0;
                case PipePhase.Execute: return 1;
                case PipePhase.SelfCheck: return 2;
                case PipePhase.Align: return 0;
                default: return -1;
            }
        }

        // 卡片状态：0 等待 / 1 运行中 / 2 已完成待确认 / 3 已完成 / 4 失败
        private int CardStatus(int k)
        {
            if (_pipe == null) return 0;
            switch (_pipe.Phase)
            {
                case PipePhase.Idle: return 0;
                case PipePhase.Finished: return 3;
                case PipePhase.RoundDone: return 3;
                default: break;
            }
            RoundRecord r = _pipe.CurrentRound;
            bool hasOut = k == 0 ? (r != null && !string.IsNullOrEmpty(r.Decomposition))
                        : k == 1 ? (r != null && !string.IsNullOrEmpty(r.Execution))
                                 : (r != null && !string.IsNullOrEmpty(r.SelfCheck));
            int active = CardOfPhase(_pipe.Running ? _pipe.Phase : _pipe.RunPhaseForStatus);
            if (k == active)
            {
                if (_pipe.Phase == PipePhase.Failed) return 4;
                if (_pipe.Phase == PipePhase.Stopped) return 1;
                if (_pipe.ConfirmPending) return 2;
                return 1;
            }
            return hasOut ? 3 : 0;
        }

        // 卡片头部显示的角色标签：卡 1 要区分拆解与对齐
        private string CardRoleLabel(int k)
        {
            if (k == 0 && _pipe != null && (_pipe.Phase == PipePhase.Align ||
                (_pipe.Phase == PipePhase.RoundDone && _pipe.CurrentRound == null)))
                return "对齐";
            return RoleNames[k];
        }

        // 卡片的本轮结果摘要（该 agent 在本轮最近一次输出）
        private string CardSummary(int k)
        {
            if (_pipe == null || _pipe.CurrentRound == null) return "";
            RoundRecord r = _pipe.CurrentRound;
            string s = k == 0 ? (r.Decomposition ?? "") : k == 1 ? (r.Execution ?? "") : (r.SelfCheck ?? "");
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
        }

        private static string CardStatusText(int st)
        {
            switch (st)
            {
                case 1: return "运行中";
                case 2: return "已完成 · 待确认";
                case 3: return "已完成";
                case 4: return "失败";
                default: return "等待中";
            }
        }

        private static Color CardStatusColor(int st)
        {
            switch (st)
            {
                case 1: return Orange;
                case 2: return Color.FromArgb(214, 150, 30);
                case 3: return Color.FromArgb(58, 168, 106);
                case 4: return Color.FromArgb(214, 84, 72);
                default: return Color.FromArgb(170, 170, 176);
            }
        }

        // 注册缩略图前核对源窗口：必须可见、无主、够大。
        // 选错（比如拿到了 Chromium 启动时的隐藏主窗）就是"缩略图抓到的不是屏幕上那个窗口"。
        private void FixThumbSource(int k)
        {
            int idx = _roles[k];
            IntPtr cur;
            bool bad = !_windows.TryGetValue(idx, out cur) || cur == IntPtr.Zero || !Native.IsWindow(cur) ||
                       !Native.IsWindowVisible(cur) || Native.GetWindowTextLength(cur) == 0;
            if (!bad) return;

            IntPtr better = Host.FindVisible(_agents[idx], DateTime.MinValue);
            if (better != IntPtr.Zero && better != cur)
            {
                Program.Log("thumb " + k + " reswitch " + cur + " -> " + better + " title='" + Native.Title(better) + "'");
                _windows[idx] = better;
                // 换了句柄，旧缩略图指向的可能就是那个错窗口，一并换掉
                UnregisterThumb(k);
            }
        }

        private void UnregisterThumb(int k)
        {
            if (_thumbs[k] != IntPtr.Zero)
            {
                Native.DwmUnregisterThumbnail(_thumbs[k]);
                _thumbs[k] = IntPtr.Zero;
            }
        }

        private void UpdateThumbs()
        {
            if (_roles.Count < 3) return;
            Rectangle[] rr = TileRects();
            for (int k = 0; k < 3; k++)
            {
                FixThumbSource(k);
                if (_thumbs[k] == IntPtr.Zero)
                {
                    IntPtr h;
                    if (!_windows.TryGetValue(_roles[k], out h) || !Native.IsWindow(h)) continue;
                    if (!Native.IsWindowVisible(h)) continue;   // 不可见的宁可不注册，等下一轮
                    IntPtr th;
                    int hr = Native.DwmRegisterThumbnail(Handle, h, out th);
                    Program.Log("thumb " + k + " hr=" + hr + " src=" + h + " title='" + Native.Title(h) + "'");
                    if (hr != 0) continue;
                    _thumbs[k] = th;
                }
                var props = new Native.DWM_THUMBNAIL_PROPERTIES();
                props.dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY;
                // 缩略图只铺卡片的缩略区（头部留给状态徽章和确认按钮）
                Rectangle t = CardThumbRect(rr[k]);
                props.rcDestination = new Native.RECT();
                props.rcDestination.Left = t.X;
                props.rcDestination.Top = t.Y;
                props.rcDestination.Right = t.Right;
                props.rcDestination.Bottom = t.Bottom;
                props.opacity = 255;
                // 放大时三张小缩略图都藏（真实窗口盖在上面），只留右侧窄条那张继续当"预览"
                props.fVisible = _focusIdx < 0;
                Native.DwmUpdateThumbnailProperties(_thumbs[k], ref props);
            }
        }

        private void ParkOffscreen(int k, bool off)
        {
            if (k < 0 || k >= _roles.Count) return;
            IntPtr h;
            if (!_windows.TryGetValue(_roles[k], out h) || !Native.IsWindow(h)) return;
            Native.ShowWindow(h, Native.SW_SHOW);
            if (off)
            {
                // 最大化窗口无视 SetWindowPos 的位置参数，必须先取消最大化
                if (Native.IsZoomed(h)) Native.ShowWindow(h, Native.SW_RESTORE);
                // 停到整个虚拟桌面（含副屏）的右边外。只按主屏宽度算的话，
                // 主屏右侧插了副屏时"屏外"其实还在副屏上
                int parkX = SystemInformation.VirtualScreen.Right + 200;
                bool ok = Native.SetWindowPos(h, Handle, parkX, 0, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
                Native.RECT r;
                Native.GetWindowRect(h, out r);
                Program.Log("park " + k + " ok=" + ok + " -> " + r.Left + "," + r.Top + " " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) + " zoomed=" + Native.IsZoomed(h));
            }
        }

        private void FocusTile(int k)
        {
            if (k < 0 || k >= _roles.Count) return;
            int idx = _roles[k];
            if (idx < 0 || idx >= _agents.Count) return;
            IntPtr h;
            if (!_windows.TryGetValue(idx, out h) || !Native.IsWindow(h)) return;
            _focusIdx = k;
            _tileDone[k] = false;
            // 放大但不占满屏：右侧留 1/4 露出窄条（其他卡片预览 + 自己的小卡），
            // 底部留一条，能看到流程状态；对话框和上一次对话都还在窗口里
            Rectangle zoom = ZoomRect(k);
            Host.Apply(h, Handle, zoom, _agents[idx].OffsetFrame);
            Native.SetWindowPos(h, (IntPtr)0, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            Host.ForceForeground(h);
            PipeReturnButton.Show(BackToPipeView);   // 悬浮在应用窗口之上，点了就回卡片流
            ApplyBounds();
            Invalidate();
        }

        // 放大后的窗口矩形：不占满内容区——右侧留 stripW 给预览窄条，底部留 bottom 给状态
        private Rectangle ZoomRect(int k)
        {
            Rectangle content = ContentRect();
            int stripW = Math.Max(Px(180), content.Width / 4);
            int bottom = Px(72);
            return new Rectangle(content.X, content.Y, content.Width - stripW, content.Height - bottom);
        }

        // 放大时右侧窄条里自己的小卡（点它 = 回卡片流）
        private Rectangle ZoomMiniRect(int k)
        {
            Rectangle content = ContentRect();
            int stripW = Math.Max(Px(180), content.Width / 4);
            int stripX = content.Right - stripW;
            int top = Px(64);
            int bottomBar = Px(72);
            int gap = Px(14);
            int h = (content.Height - top - bottomBar - gap * 2) / 3;
            return new Rectangle(stripX + Px(12), top + k * (h + gap), stripW - Px(24), h);
        }

        // 聚焦应用时左上角悬浮的返回按钮：回到流程条视图，应用保持已选不取消
        private void BackToPipeView()
        {
            if (_focusIdx < 0) return;
            PipeReturnButton.Hide();
            ParkOffscreen(_focusIdx, true);
            _focusIdx = -1;
            ApplyBounds();
            Invalidate();
        }

        private void BackToGrid()
        {
            PipeReturnButton.Hide();
            if (_focusIdx >= 0) ParkOffscreen(_focusIdx, true);
            _focusIdx = -1;
            ApplyBounds();
            Invalidate();
        }

        private void TileTick()
        {
            if (!_tiled || _focusIdx >= 0 || _roles.Count < 3) return;
            for (int k = 0; k < 3; k++)
            {
                IntPtr h;
                if (!_windows.TryGetValue(_roles[k], out h) || !Native.IsWindow(h)) continue;
                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) continue;
                // 只要还露在虚拟桌面（含副屏）里就再挪出去
                if (r.Left < SystemInformation.VirtualScreen.Right) ParkOffscreen(k, true);
            }
        }

        // DWM 缩略图是有名字的内核资源，必须显式注销；
        // 直接把 _thumbs[k] 置零会让旧缩略图继续挂在画布上，直到进程退出
        private void UnregisterThumbs()
        {
            for (int k = 0; k < 3; k++)
            {
                if (_thumbs[k] != IntPtr.Zero)
                {
                    Native.DwmUnregisterThumbnail(_thumbs[k]);
                    _thumbs[k] = IntPtr.Zero;
                }
            }
        }

        // ---------- 协同自有控件界面（SynergyPanel） ----------

        // 建一次、复用：把协同主视图换成自有控件，并接管剪贴板接力
        private void EnsureSynergyPanel()
        {
            if (_synPanel == null || _synPanel.IsDisposed)
            {
                // 面板自管：自建 Pipeline、自读 agents.json、OnHandleCreated 里自挂剪贴板监听
                _synPanel = new SynergyPanel(PipeConfig.Load());
                Native.SetNoActivate(Handle, false);   // 协同模式有输入框：整个会话保持可激活
                Native.SetForegroundWindow(Handle);    // 必须真的成为前台，输入法才会附着
                Controls.Add(_synPanel);
                _synPanel.BringToFront();
            }
            LayoutSynergyPanel();
            // 任务输入框交给面板内的工作区，旧的内嵌框藏起来
            if (_taskBox != null) _taskBox.Visible = false;
            _synPanel.Visible = true;
            _synPanel.BringToFront();
        }

        // 面板铺满内容区（侧栏右侧），跟随主窗口/内容区变化
        private void LayoutSynergyPanel()
        {
            if (_synPanel == null || _synPanel.IsDisposed) return;
            Rectangle r = ClientContentRect;
            if (_synPanel.Bounds != r) _synPanel.Bounds = r;
        }

        // 离开协同时卸载面板并断开剪贴板监听
        private void RemoveSynergyPanel()
        {
            if (_synPanel == null) return;
            try { _synPanel.Shutdown(); } catch { }   // 停 pump、摘掉剪贴板监听
            try { Controls.Remove(_synPanel); } catch { }
            try { _synPanel.Dispose(); } catch { }
            _synPanel = null;
        }

        // 把选中的三个角色摆成三栏平铺；已选但非角色的应用不杀，留给用户自己决定
        private void StartSynergy()
        {
            // 名额判定：角色 + 已选合并后不能超过 3 个。
            // 超了也不自动杀（ADR 0001：程序不做自动淘汰），把选择权交回用户。
            var need = new HashSet<int>(_roles);
            foreach (int i in _chosen) need.Add(i);
            if (need.Count > MaxSlots)
            {
                Toast.Show("角色和已选应用合起来超过 " + MaxSlots + " 个，请先右键取消一些");
                _picking = true;   // 留在挑选状态，改完再点满三个即启动
                Invalidate();
                return;
            }

            _shown = -1;
            _tiled = true;
            _focusIdx = -1;
            UnregisterThumbs();
            EnsureSynergyPanel();   // 面板接管协同：用自有控件展示输入/输出/状态，不再嵌入 AI 窗口
            for (int k = 0; k < 3; k++) _tileDone[k] = false;
            // 面板模式下由 SynergyPanel 在发提示词时按需启动并前置 Agent；
            // 这里不再预先把角色窗口抓进主窗（否则又回到嵌入老路）
            if (_synPanel == null)
                foreach (int idx in _roles)
                    if (!_chosen.Contains(idx)) { _chosen.Add(idx); StartAgent(idx); }
            ApplyBounds();
            Invalidate();
        }

        // 会话正在进行时被打断（点「协」退出 / 移除角色应用）：
        // 结束会话并落盘，记录不丢——用户随时能在右侧面板查看
        private void EndSessionIfAny()
        {
            if (_pipe != null && _pipe.Session != null && !_pipe.Session.Ended)
            {
                _pipe.Finish();
                SavePipeResult();
            }
            if (_taskBox != null && _taskBox.Visible) _taskBox.Visible = false;
            CloseDialogs();
        }

        private void ExitSynergy()
        {
            EndSessionIfAny();
            var cur2 = new List<int>(_chosen);
            foreach (int idx in cur2) Cancel(idx);
            _roles.Clear();
            _tiled = false;
            RemoveSynergyPanel();
            PipeReturnButton.Hide();
            UnregisterThumbs();
            for (int k = 0; k < 3; k++) _tileDone[k] = false;
            _picking = false;
            _enterT = 0f;
            _mode = -1;
            _modeSel = -1; _modeT = 1f;   // 重置转场状态，回到正常开屏
            _shown = -1;
            ApplyBounds();
            Invalidate();
        }

        // 协同角色对应的应用被移除时调用：不杀其余应用（那是用户自己的选择），
        // 退出平铺、把它们留作普通已选，之后点侧栏图标即可查看
        private void AbandonSynergyKeepApps()
        {
            EndSessionIfAny();
            RemoveSynergyPanel();
            PipeReturnButton.Hide();
            UnregisterThumbs();
            _tiled = false;
            _focusIdx = -1;
            _roles.Clear();
            _picking = false;
            for (int k = 0; k < 3; k++) _tileDone[k] = false;
            ApplyBounds();
            Invalidate();
        }

        private void ShowPipelineView()
        {
            _mode = 1;
            if (_pipe == null) _pipe = new Pipeline(PipeConfig.Load());   // 每次新建都重读 json，改完立刻生效
            if (_roles.Count < 3) { _picking = true; _roles.Clear(); }
            else _picking = false;   // 角色还在：直接回卡片视图
            IntPtr h;
            if (_shown >= 0 && _windows.TryGetValue(_shown, out h) && Native.IsWindow(h)) Host.Hide(h);
            _shown = -1;
            _dock = DockState.Visible;
            ApplyBounds();
            Invalidate();
        }

        // ---------- 协同动作层：界面按钮 ↔ Pipeline 调度的接线 ----------

        private void PipeCopy(string text)
        {
            _lastCopied = text ?? "";
            try { Clipboard.SetText(_lastCopied); } catch { }
        }

        // 交付提示词：写剪贴板 + 记入该角色的对话记录
        private void DeliverPrompt(string prompt)
        {
            if (string.IsNullOrEmpty(prompt)) return;
            int k = CardOfPhase(_pipe.Phase);
            string aid = null;
            if (k >= 0 && k < _roles.Count)
            {
                int idx = _roles[k];
                if (idx >= 0 && idx < _agents.Count) aid = _agents[idx].Id;
            }
            _pipe.LogChat(aid, 0, prompt);
            PipeCopy(prompt);
        }

        // 下一接收者名字（确认交接前调用）
        private string NextRoleName()
        {
            if (_pipe == null || !_pipe.Running) return "";
            int next = _pipe.Phase == PipePhase.Decompose ? 1
                     : _pipe.Phase == PipePhase.Execute ? 2
                     : _pipe.Phase == PipePhase.SelfCheck ? 0 : -1;
            if (next < 0 || next >= _roles.Count) return "";
            int idx = _roles[next];
            return idx >= 0 && idx < _agents.Count ? _agents[idx].Name : "";
        }

        // 开始协同：从输入框读需求
        private void StartFromBox()
        {
            if (_pipe == null) return;
            string err = _pipe.Start(_taskBox != null ? _taskBox.Text : "");
            if (err != null) { Toast.Show(err); return; }   // 空需求 / 未结束会话，明确反馈
            _pipeAt = DateTime.Now;
            _lastAdvance = "";
            DeliverPrompt(_pipe.CurrentPrompt());
            Toast.Show("第 1 轮已开始，提示词在剪贴板，去「拆解」粘贴");
            FocusCurrent();
            Invalidate();
        }

        // 步级交接（人工确认 / 自动接力共用）：确认 + 交付 + 切窗
        private void Handoff()
        {
            if (_pipe == null || !_pipe.ConfirmPending) return;
            string next = NextRoleName();   // 确认前取名字
            string prompt = _pipe.Confirm();
            if (prompt == null) return;
            _pipeAt = DateTime.Now;
            _lastAdvance = "";
            DeliverPrompt(prompt);
            Toast.Show("已交给「" + (next.Length > 0 ? next : "下一步") + "」，提示词在剪贴板");
            FocusCurrent();
            Invalidate();
        }

        // 轮末「继续下一轮」：只能从这里进新轮
        private void NextRound()
        {
            string prompt = _pipe.ContinueRound();
            if (prompt == null) { Toast.Show("已达最大轮数（" + _pipe.Config.MaxRounds + "），请结束协同"); return; }
            _pipeAt = DateTime.Now;
            _lastAdvance = "";
            DeliverPrompt(prompt);
            Toast.Show("第 " + _pipe.CurrentRoundNo + " 轮开始，提示词已交给「拆解」");
            FocusCurrent();
            Invalidate();
        }

        private void ResumeFlow()
        {
            string prompt = _pipe.Resume();
            if (prompt == null) return;
            _pipeAt = DateTime.Now;
            DeliverPrompt(prompt);
            Toast.Show("已恢复，提示词已重新复制");
            FocusCurrent();
            Invalidate();
        }

        // 失败重试：重交当前步提示词；窗口没了就把应用再拉起来（无重复副作用：只复制提示词）
        private void RetryFlow()
        {
            string prompt = _pipe.Retry();
            if (prompt == null) return;
            _pipeAt = DateTime.Now;
            DeliverPrompt(prompt);
            int k = CardOfPhase(_pipe.Phase);
            if (k >= 0 && k < _roles.Count)
            {
                int idx = _roles[k];
                IntPtr h;
                if (!_windows.TryGetValue(idx, out h) || !Native.IsWindow(h))
                {
                    if (!_chosen.Contains(idx)) { _chosen.Add(idx); StartAgent(idx); }
                }
                else FocusCurrent();
            }
            Toast.Show("已重试，窗口就绪后粘贴继续");
            Invalidate();
        }

        // 结束协同：保留会话记录（落盘 .scratch + 内存 Session 供右侧面板查看）
        private void FinishFlow()
        {
            _pipe.Finish();
            SavePipeResult();
            Toast.Show("协同已结束，轮次记录在右侧，报告在 .scratch");
            Invalidate();
        }

        // 手动推进（没被剪贴板捕获时兜底）
        private void CaptureManual()
        {
            string clip = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            if (clip.Trim().Length == 0) { Toast.Show("先复制应用里的回复，再点这里"); return; }
            PipeAdvanceWith(clip, true);
        }

        private void SavePipeResult()
        {
            try
            {
                PipeSession s = _pipe.Session;
                if (s == null) return;
                string dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".scratch");
                System.IO.Directory.CreateDirectory(dir);
                string name = "pipeline-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".md";
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# 三 Agent 协同 · 会话记录").AppendLine();
                sb.AppendLine("- 开始时间：" + s.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("- 完成轮数：" + s.Rounds.Count);
                sb.AppendLine("- 原始需求：" + s.Task).AppendLine();
                foreach (RoundRecord r in s.Rounds)
                {
                    sb.AppendLine("## 第 " + r.Number + " 轮（" + r.FinishedAt.ToString("HH:mm:ss") + "）").AppendLine();
                    sb.AppendLine("### 拆解（含验收标准）").AppendLine(r.Decomposition ?? "").AppendLine();
                    sb.AppendLine("### 执行结果").AppendLine(r.Execution ?? "").AppendLine();
                    sb.AppendLine("### 自检").AppendLine(r.SelfCheck ?? "").AppendLine();
                    sb.AppendLine("### 对齐结论").AppendLine(r.Alignment ?? "").AppendLine();
                    if (!string.IsNullOrEmpty(r.UserAddition)) sb.AppendLine("### 本轮用户补充").AppendLine(r.UserAddition).AppendLine();
                }
                if (s.Supplements.Count > 0)
                {
                    sb.AppendLine("## 补充需求记录").AppendLine();
                    foreach (Supplement sp in s.Supplements)
                        sb.AppendLine("- [" + sp.Time.ToString("HH:mm:ss") + " 来自" + sp.RoleName + " · " +
                            (sp.Applied ? "已生效于" + sp.RoundTag : "未生效") + "] " + sp.Text);
                }
                System.IO.File.WriteAllText(System.IO.Path.Combine(dir, name), sb.ToString(), System.Text.Encoding.UTF8);
                Toast.Show("会话记录已存到 .scratch\\" + name);
            }
            catch { }
        }

        // manual：按钮点的还是剪贴板监听自己推进的。手动时要给反馈，
        // 否则剪贴板没换内容时按钮像坏了一样毫无反应
        private void PipeAdvanceWith(string output, bool manual)
        {
            if (_pipe == null || !_pipe.Running) return;
            // 迟到响应闸门：停确认处 / 已停止 / 已失败时，剪贴板变化一概不推进
            if (_pipe.Phase == PipePhase.RoundDone || _pipe.Phase == PipePhase.Stopped || _pipe.Phase == PipePhase.Failed) return;
            if (output == _lastCopied)
            {
                if (manual) Toast.Show("先复制应用里的回复，再点这里");
                return;   // 忽略我们自己写进去的那份
            }
            if (output.Length < 20)
            {
                if (manual) Toast.Show("复制的内容太短，不像是这一步的输出");
                return;   // 顺手复制个单词不该推进
            }
            // 同一份复制只消费一次（按 轮次+阶段 去重，重复点击无效果）
            if (output == _lastAdvance &&
                _pipe.CurrentRoundNo == _lastAdvanceRound && _pipe.StepIndex == _lastAdvanceIndex) return;
            _lastAdvance = output;
            _lastAdvanceRound = _pipe.CurrentRoundNo;
            _lastAdvanceIndex = _pipe.StepIndex;

            int card = CardOfPhase(_pipe.Phase);
            string aid = null;
            if (card >= 0 && card < _roles.Count)
            {
                int idx = _roles[card];
                if (idx >= 0 && idx < _agents.Count) aid = _agents[idx].Id;
            }
            if (!_pipe.Advance(output)) return;   // 状态闸门没过（迟到响应）
            _pipe.LogChat(aid, 1, output);         // agent 回复进对话记录
            _pipeAt = DateTime.Now;
            if (_pipe.ConfirmPending && _pipe.Config.AutoAdvance)
            {
                Handoff();   // 自动接力：步级不等确认；轮级仍停在用户确认
                return;
            }
            if (_pipe.Phase == PipePhase.Finished) { SavePipeResult(); return; }
            Toast.Show(_pipe.Note);
            Invalidate();
        }

        // 把主窗让开，让"当前步"的 agent 窗口到前台工作（不取消任何应用）
        private void FocusCurrent()
        {
            if (_pipe == null || !_pipe.Running) return;
            int k = CardOfPhase(_pipe.Phase);
            if (k < 0 || k >= _roles.Count) return;
            int idx = _roles[k];
            if (idx < 0 || idx >= _agents.Count) return;
            IntPtr h;
            if (!_windows.TryGetValue(idx, out h) || !Native.IsWindow(h)) return;
            _shown = -1;
            Host.Apply(h, Handle, ZoomRect(k), _agents[idx].OffsetFrame);
            Native.SetWindowPos(h, (IntPtr)0, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            Host.ForceForeground(h);
            _focusIdx = k;
            PipeReturnButton.Show(BackToPipeView);
            ApplyBounds();
        }

        // 剪贴板一变就看看是不是"这一步的输出复制好了"
        private void ClipboardChanged()
        {
            if (_synPanel != null) return;   // 面板激活时由 SynergyPanel 接管剪贴板接力，这里别再抢
            if (_mode != 1 || _pipe == null) return;
            if (!_pipe.Running || _pipe.ConfirmPending) return;   // 待确认/非运行时忽略（防迟到响应）
            string clip;
            try { clip = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { return; }
            if (clip.Length == 0 || clip == _lastCopied) return;
            PipeAdvanceWith(clip, false);
        }

        // ---------- 协同绘制 ----------

        private void DrawSynergy(Graphics g)
        {
            Rectangle[] rr = TileRects();
            bool running = _pipe != null && _pipe.Running;

            // 放大态：右侧窄条画三张小卡（点击切换/返回），其余被应用窗口盖住
            if (_focusIdx >= 0)
            {
                Rectangle content = ContentRect();
                int stripW = Math.Max(Px(180), content.Width / 4);
                int stripX = content.Right - stripW;
                using (var bg = new SolidBrush(Color.FromArgb(245, 245, 247)))
                    g.FillRectangle(bg, stripX, content.Y, stripW, content.Height);
                for (int k = 0; k < 3 && k < _roles.Count; k++)
                {
                    if (_roles[k] < 0 || _roles[k] >= _agents.Count) continue;
                    Rectangle mini = ZoomMiniRect(k);
                    int st = CardStatus(k);
                    using (var path = Round(mini, Px(10)))
                    using (var bg = new SolidBrush(k == _focusIdx ? Color.FromArgb(255, 243, 234) : Color.FromArgb(250, 250, 252)))
                        g.FillPath(bg, path);
                    using (var path = Round(mini, Px(10)))
                    using (var pen = new Pen(k == _focusIdx ? Orange : Color.FromArgb(220, 220, 224), k == _focusIdx ? Px(2) : Px(1)))
                        g.DrawPath(pen, path);
                    using (var f = new Font("微软雅黑", Px(15), k == _focusIdx ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var b = new SolidBrush(k == _focusIdx ? Orange : TextBlack))
                        g.DrawString((k + 1) + " " + CardRoleLabel(k) + " · " + _agents[_roles[k]].Name, f, b,
                            new Rectangle(mini.X + Px(10), mini.Y + Px(8), mini.Width - Px(20), Px(26)));
                    using (var f = new Font("微软雅黑", Px(12), FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var b = new SolidBrush(CardStatusColor(st)))
                        g.DrawString(CardStatusText(st), f, b, mini.X + Px(10), mini.Y + Px(38));
                    using (var f = new Font("微软雅黑", Px(11), FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var b = new SolidBrush(Color.FromArgb(150, 150, 158)))
                        g.DrawString(k == _focusIdx ? "点击返回流水线" : "点击切换到这里", f, b, mini.X + Px(10), mini.Bottom - Px(26));
                }
                // 底部状态带
                Rectangle bottom = new Rectangle(content.X, content.Bottom - Px(72), content.Width - stripW, Px(72));
                using (var bg = new SolidBrush(Color.FromArgb(245, 245, 247)))
                    g.FillRectangle(bg, bottom);
                using (var f = new Font("微软雅黑", Px(13), FontStyle.Regular, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(Color.FromArgb(110, 110, 118)))
                {
                    string tip = running
                        ? (_pipe.ConfirmPending ? "本步已完成 → 回流水线点确认（或开自动接力）" : "干完复制回复即自动捕获；Esc 或右上按钮回流水线")
                        : "Esc 或右上按钮回流水线";
                    g.DrawString(tip, f, b, bottom.X + Px(16), bottom.Y + Px(10));
                    g.DrawString("协同 · 第 " + (_pipe != null ? _pipe.CurrentRoundNo : 1) + " 轮 · 已完成 " +
                        (_pipe != null ? _pipe.CompletedRounds : 0) + " 轮", f, b, bottom.X + Px(16), bottom.Y + Px(36));
                }
                return;
            }

            // 标题
            using (var f = new Font("微软雅黑", Px(20), FontStyle.Bold, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(TextBlack))
            {
                string title = "三 Agent 协同";
                if (_pipe != null && (running || _pipe.Phase == PipePhase.RoundDone || _pipe.Phase == PipePhase.Stopped || _pipe.Phase == PipePhase.Failed))
                    title += " · 第 " + _pipe.CurrentRoundNo + " 轮";
                g.DrawString(title, f, b, rr[0].X, Px(16));
            }

            // 输入阶段（会话未开始）：第一张卡的位置放任务输入框
            bool inputPhase = _pipe == null || _pipe.Phase == PipePhase.Idle;

            for (int k = 0; k < 3 && k < _roles.Count; k++)
            {
                if (_roles[k] < 0 || _roles[k] >= _agents.Count) continue;
                Rectangle card = rr[k];
                if (inputPhase && k == 0) { DrawTaskInput(g, card); continue; }

                int st = CardStatus(k);
                Agent a = _agents[_roles[k]];

                // 卡片底与边
                using (var path = Round(card, Px(12)))
                using (var bg = new SolidBrush(st == 1 ? Color.FromArgb(252, 248, 242) : Color.FromArgb(247, 247, 249)))
                    g.FillPath(bg, path);
                using (var path = Round(card, Px(12)))
                using (var pen = new Pen(st >= 1 ? CardStatusColor(st) : Color.FromArgb(222, 222, 226), st == 1 ? Px(2) : Px(1)))
                    g.DrawPath(pen, path);

                // 头部：序号 + 角色 + Agent + 状态徽章
                Rectangle head = new Rectangle(card.X, card.Y, card.Width, Px(CardHeaderH));
                using (var f = new Font("微软雅黑", Px(16), FontStyle.Bold, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(st >= 1 ? CardStatusColor(st) : TextBlack))
                    g.DrawString((k + 1) + "  " + CardRoleLabel(k) + " · " + a.Name, f, b,
                        new Rectangle(head.X + Px(14), head.Y + Px(6), Px(330), head.Height - Px(10)),
                        new StringFormat { LineAlignment = StringAlignment.Center });

                string stText = CardStatusText(st);
                Size stSz = TextRenderer.MeasureText(stText, _fName);
                var badge = new Rectangle(head.X + Px(350), head.Y + (head.Height - Px(22)) / 2, stSz.Width + Px(20), Px(22));
                using (var path = Round(badge, Px(11)))
                using (var b = new SolidBrush(Color.FromArgb(30, CardStatusColor(st))))
                    g.FillPath(b, path);
                using (var f = new Font("微软雅黑", Px(12), FontStyle.Regular, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(CardStatusColor(st)))
                    TextRenderer.DrawText(g, stText, f, badge, CardStatusColor(st),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                // 头部按钮：查看对话 / 补充需求 / 确认交接
                DrawHeaderButton(g, CardDialogRect(card), "查看对话", Color.FromArgb(64, 64, 74), Color.White);
                DrawHeaderButton(g, CardSuppRect(card), "补充需求", Color.FromArgb(64, 64, 74), Color.White);
                if (st == 2)
                {
                    string next = NextRoleName();
                    DrawHeaderButton(g, CardConfirmRect(card), "确认 → 交给「" + next + "」", Orange, Color.White);
                }

                // 结果摘要行（本轮该 agent 最近一次输出）
                string sum = CardSummary(k);
                if (sum.Length > 0)
                {
                    using (var f = new Font("微软雅黑", Px(12), FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var b = new SolidBrush(Color.FromArgb(120, 120, 128)))
                        g.DrawString("摘要：" + sum, f, b,
                            new RectangleF(card.X + Px(14), card.Y + Px(CardHeaderH), card.Width - Px(28), Px(24)));
                }

                // 缩略图没注册成时给个占位说明
                if (_thumbs[k] == IntPtr.Zero)
                {
                    Rectangle t = new Rectangle(card.X, card.Y + Px(CardHeaderH + 24), card.Width, card.Height - Px(CardHeaderH + 26));
                    using (var f = new Font("微软雅黑", Px(13), FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var b = new SolidBrush(Color.FromArgb(160, 160, 168)))
                        TextRenderer.DrawText(g, "窗口就绪后这里会显示实时画面", f, t, Color.FromArgb(160, 160, 168),
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }

                // 右下角提示
                using (var f = new Font("微软雅黑", Px(11), FontStyle.Regular, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(Color.FromArgb(150, 150, 158)))
                    g.DrawString("点击卡片打开对话", f, b, card.Right - Px(140), card.Bottom - Px(24));
            }

            // 右侧轮次面板
            DrawRoundPanel(g);

            if (inputPhase) UpdateTaskBox(rr[0]);

            // 底部操作带（按阶段出按钮）
            var labels = new List<string>();
            var acts = new List<Action>();
            BuildBottomBar(labels, acts);
            if (labels.Count > 0)
            {
                Rectangle[] btns = BottomButtons(labels.Count);
                for (int i = 0; i < labels.Count; i++)
                    DrawHeaderButton(g, btns[i], labels[i], i == 0 ? Orange : Color.FromArgb(64, 64, 74), Color.White, Px(16));
            }

            // 自动接力开关（仅运行中有意义）
            if (running)
            {
                Rectangle ar = PipeAutoRect();
                bool auto = _pipe.Config.AutoAdvance;
                using (var path = Round(ar, Px(10)))
                using (var b = new SolidBrush(auto ? Orange : Color.FromArgb(238, 238, 241)))
                    g.FillPath(b, path);
                using (var f = new Font("微软雅黑", Px(13), auto ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
                    TextRenderer.DrawText(g, auto ? "自动接力：开" : "自动接力：关", f, ar,
                        auto ? Color.White : Color.FromArgb(120, 120, 128),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // 状态行
            using (var f = new Font("微软雅黑", Px(13), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(Color.FromArgb(150, 150, 158)))
            {
                string tip = _pipe != null && _pipe.Note.Length > 0 ? _pipe.Note : "";
                if (_pipe != null && _pipe.Phase == PipePhase.Failed && _pipe.FailedReason.Length > 0)
                    tip = "失败原因：" + _pipe.FailedReason;
                g.DrawString(tip, f, b, rr[0].X, Height - Px(24));
            }
        }

        private void DrawHeaderButton(Graphics g, Rectangle r, string text, Color bg, Color fg)
        {
            DrawHeaderButton(g, r, text, bg, fg, Px(13));
        }

        private void DrawHeaderButton(Graphics g, Rectangle r, string text, Color bg, Color fg, int fontPx)
        {
            using (var path = Round(r, Px(13)))
            using (var b = new SolidBrush(bg))
                g.FillPath(b, path);
            using (var f = new Font("微软雅黑", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, text, f, r, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        // 任务需求输入框（输入阶段盖在卡 1 上）
        private void DrawTaskInput(Graphics g, Rectangle card)
        {
            using (var path = Round(card, Px(12)))
            using (var b = new SolidBrush(Color.FromArgb(250, 250, 252)))
                g.FillPath(b, path);
            using (var path = Round(card, Px(12)))
            using (var pen = new Pen(Orange, Px(2)))
                g.DrawPath(pen, path);
            using (var f = new Font("微软雅黑", Px(17), FontStyle.Bold, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(TextBlack))
                g.DrawString("① 拆解 · 本次任务需求", f, b, card.X + Px(16), card.Y + Px(12));
            using (var f = new Font("微软雅黑", Px(12), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(Color.FromArgb(130, 130, 138)))
                g.DrawString("填好需求后点「开始协同」。下面两张卡是执行与自检，会在流程中自动接手。",
                    f, b, card.X + Px(16), card.Y + Px(40));
            Rectangle sr = TaskStartRect(card);
            DrawHeaderButton(g, sr, "开始协同", Orange, Color.White, Px(16));
        }

        private Rectangle TaskStartRect(Rectangle card)
        {
            return new Rectangle(card.Right - Px(180), card.Bottom - Px(60), Px(160), Px(44));
        }

        private Rectangle TaskBoxRect(Rectangle card)
        {
            return new Rectangle(card.X + Px(16), card.Y + Px(70), card.Width - Px(32), card.Height - Px(150));
        }

        private void UpdateTaskBox(Rectangle card)
        {
            if (_taskBox == null)
            {
                _taskBox = new TextBox();
                _taskBox.Multiline = true;
            _taskBox.ImeMode = ImeMode.On;
                _taskBox.ScrollBars = ScrollBars.Vertical;
                _taskBox.BorderStyle = BorderStyle.FixedSingle;
                _taskBox.BackColor = Color.White;
                _taskBox.ForeColor = Color.FromArgb(24, 24, 28);
                _taskBox.Font = new Font("Microsoft YaHei UI", 10.5f);
                Controls.Add(_taskBox);
            }
            Rectangle r = TaskBoxRect(card);
            if (!_taskBox.Visible) _taskBox.Visible = true;
            if (_taskBox.Bounds != r) _taskBox.Bounds = r;
            _taskBox.BringToFront();
        }

        // 右侧轮次面板：当前轮次 / 已完成轮数 / 当前阶段 / 各轮结论
        private void DrawRoundPanel(Graphics g)
        {
            Rectangle p = RoundPanelRect();
            using (var path = Round(p, Px(12)))
            using (var bg = new SolidBrush(Color.FromArgb(243, 243, 245)))
                g.FillPath(bg, path);
            using (var path = Round(p, Px(12)))
            using (var pen = new Pen(Color.FromArgb(224, 224, 228)))
                g.DrawPath(pen, path);

            int x = p.X + Px(16), y = p.Y + Px(14);
            using (var f = new Font("微软雅黑", Px(15), FontStyle.Bold, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(TextBlack))
            {
                g.DrawString("当前轮次：第 " + (_pipe != null ? _pipe.CurrentRoundNo : 0) + " 轮", f, b, x, y);
                y += Px(26);
            }
            using (var f = new Font("微软雅黑", Px(13), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(Color.FromArgb(110, 110, 118)))
            {
                g.DrawString("已完成：" + (_pipe != null ? _pipe.CompletedRounds : 0) + " 轮", f, b, x, y); y += Px(22);
                string ph = _pipe != null ? _pipe.PhaseName : "未开始";
                Color pc = _pipe != null && _pipe.Running ? Orange : Color.FromArgb(110, 110, 118);
                using (var pb = new SolidBrush(pc))
                {
                    g.DrawString("当前阶段：" + ph, f, pb, x, y); y += Px(22);
                }
                string st = "状态：";
                if (_pipe == null || _pipe.Phase == PipePhase.Idle) st += "等待输入需求";
                else if (_pipe.Phase == PipePhase.RoundDone) st += "本轮完成，等待你确认";
                else if (_pipe.Phase == PipePhase.Stopped) st += "已停止，可恢复";
                else if (_pipe.Phase == PipePhase.Failed) st += "失败，可重试";
                else if (_pipe.Phase == PipePhase.Finished) st += "已结束，记录可查看";
                else if (_pipe.ConfirmPending) st += "步完成，等待确认交接";
                else st += "进行中";
                g.DrawString(st, f, b, x, y);
            }

            // 分隔线
            using (var pen = new Pen(Color.FromArgb(224, 224, 228)))
                g.DrawLine(pen, p.X + Px(12), p.Y + Px(112), p.Right - Px(12), p.Y + Px(112));

            using (var f = new Font("微软雅黑", Px(13), FontStyle.Bold, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(Color.FromArgb(120, 120, 128)))
                g.DrawString("各轮结论（点击查看）", f, b, x, p.Y + Px(122));

            if (_pipe == null || _pipe.Session == null || _pipe.Session.Rounds.Count == 0)
            {
                using (var f = new Font("微软雅黑", Px(12), FontStyle.Regular, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(Color.FromArgb(160, 160, 168)))
                    g.DrawString("暂无已完成轮次", f, b, x, p.Y + Px(150));
                return;
            }

            for (int i = 0; i < _pipe.Session.Rounds.Count; i++)
            {
                RoundRecord r = _pipe.Session.Rounds[i];
                Rectangle it = RoundItemRect(i);
                if (it.Bottom > p.Bottom - Px(8)) break;   // 超出面板不画
                using (var path = Round(it, Px(8)))
                using (var bg = new SolidBrush(i == _hoverRound ? Color.FromArgb(255, 243, 234) : Color.FromArgb(250, 250, 252)))
                    g.FillPath(bg, path);
                string sum = (r.Alignment ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
                if (sum.Length > 34) sum = sum.Substring(0, 34) + "…";
                using (var f = new Font("微软雅黑", Px(13), FontStyle.Bold, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(TextBlack))
                    g.DrawString("第 " + r.Number + " 轮 · " + r.FinishedAt.ToString("HH:mm"), f, b, it.X + Px(10), it.Y + Px(6));
                using (var f = new Font("微软雅黑", Px(12), FontStyle.Regular, GraphicsUnit.Pixel))
                using (var b = new SolidBrush(Color.FromArgb(120, 120, 128)))
                    g.DrawString(sum, f, b, it.X + Px(10), it.Y + Px(28));
            }
        }

        // ---------- 底部操作带：按钮由阶段唯一决定，绘制与点击共用同一构建器 ----------

        private void BuildBottomBar(List<string> labels, List<Action> acts)
        {
            if (_pipe == null) return;
            switch (_pipe.Phase)
            {
                case PipePhase.Decompose:
                case PipePhase.Execute:
                case PipePhase.SelfCheck:
                case PipePhase.Align:
                    if (_pipe.ConfirmPending)
                    {
                        labels.Add("确认交给「" + NextRoleName() + "」");
                        acts.Add(Handoff);
                    }
                    else
                    {
                        labels.Add("手动推进（复制回复后点）");
                        acts.Add(CaptureManual);
                    }
                    labels.Add("停止");
                    acts.Add(delegate
                    {
                        _pipe.Stop();
                        Toast.Show("已停止，点「恢复」继续当前步");
                        Invalidate();
                    });
                    break;
                case PipePhase.RoundDone:
                    labels.Add("继续下一轮");
                    acts.Add(NextRound);
                    labels.Add("补充需求后继续");
                    acts.Add(delegate
                    {
                        OpenDialog(0);
                        Toast.Show("补充发送后，再点「继续下一轮」——会带上你的新要求");
                    });
                    labels.Add("结束协同");
                    acts.Add(FinishFlow);
                    break;
                case PipePhase.Stopped:
                    labels.Add("恢复当前步");
                    acts.Add(ResumeFlow);
                    labels.Add("结束协同");
                    acts.Add(FinishFlow);
                    break;
                case PipePhase.Failed:
                    labels.Add("重试当前步");
                    acts.Add(RetryFlow);
                    labels.Add("结束协同");
                    acts.Add(FinishFlow);
                    break;
                case PipePhase.Finished:
                    labels.Add("再来一次");
                    acts.Add(delegate
                    {
                        _pipe = new Pipeline(PipeConfig.Load());
                        Invalidate();
                    });
                    break;
            }
        }

        private Rectangle[] BottomButtons(int n)
        {
            int bw = Px(200), bh = Px(46), gap = Px(14);
            int cardsW = Width - _sideW - Px(372);
            int total = n * bw + (n - 1) * gap;
            int x0 = _sideW + Math.Max(Px(20), (cardsW - total) / 2);
            var r = new Rectangle[n];
            for (int i = 0; i < n; i++) r[i] = new Rectangle(x0 + i * (bw + gap), Height - Px(76), bw, bh);
            return r;
        }

        // ---------- 选角色视图（进入协同后第一步） ----------

        private void DrawPipeline(Graphics g)
        {
            // 现在协同只有这一个中间态：选角色。选满三个立即 StartSynergy 进卡片流。
            using (var f0 = new Font("微软雅黑", Px(34), FontStyle.Bold, GraphicsUnit.Pixel))
            using (var b0 = new SolidBrush(TextMain))
                g.DrawString("点侧栏选三个应用", f0, b0, _sideW + Px(80), Height * 0.18f);
            using (var f1 = new Font("微软雅黑", Px(18), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var b1 = new SolidBrush(TextDim))
            {
                g.DrawString("已选 " + _roles.Count + " / 3　·　第 1 个 = 需求拆解　第 2 个 = 任务执行　第 3 个 = 完成自检",
                    f1, b1, _sideW + Px(80), Height * 0.18f + Px(52));
                g.DrawString("拆解和最终对齐都由第 1 个应用承担（对齐属于当前轮，不算新轮）",
                    f1, b1, _sideW + Px(80), Height * 0.18f + Px(84));
            }
            int yy = (int)(Height * 0.36f);
            for (int k = 0; k < _roles.Count; k++)
            {
                if (_roles[k] < 0 || _roles[k] >= _agents.Count) continue;
                using (var f2 = new Font("微软雅黑", Px(20), FontStyle.Bold, GraphicsUnit.Pixel))
                using (var b2 = new SolidBrush(AppHue(_agents[_roles[k]])))
                    g.DrawString((k + 1) + "  " + RoleNames[k] + " ← " + _agents[_roles[k]].Name, f2, b2, _sideW + Px(80), yy);
                yy += Px(40);
            }
        }

        // 模式转场：点选模式卡后整页收拢退出 —— 选中卡确认弹跳 + 双卡缩小淡出
        // （沿用开屏全屏卡片布局，避免选完模式后卡片因 Dock 出现而跳位）
        private void DrawModeTransition(Graphics g)
        {
            float conf = Clamp01(_modeT / 0.30f);                 // 确认弹跳 0→1（前 30% 时长）
            float outT = Clamp01((_modeT - 0.30f) / 0.70f);       // 收拢淡出 0→1（后 70% 时长）

            int gap = Px(48);
            int w = Math.Max(Px(200), Math.Min(Px(540), (Width - gap * 3) / 2));
            int h = Px(300);
            int x0 = (Width - (w * 2 + gap)) / 2;
            int y = (int)(Height * 0.44f);
            Rectangle ca = new Rectangle(x0, y, w, h);
            Rectangle cb = new Rectangle(x0 + w + gap, y, w, h);

            float sc = 1f - 0.68f * EaseOut(outT);                // 双卡向中心收拢
            float alpha = Math.Max(0.02f, 1f - EaseOut(outT));    // 整体淡出
            float pop = 1f + 0.11f * (float)Math.Sin(Math.PI * Math.Min(1f, conf));   // 选中卡弹跳
            Rectangle sel = _modeSel == 1 ? ca : cb;
            Rectangle unsel = _modeSel == 1 ? cb : ca;
            Color hue = _acc;

            if (sc > 0.03f)
            {
                DrawModeCardFaded(g, ScaleRect(sel, pop * sc), "多 agent 协同模式",
                    "拆解 → 执行 → 自检 → 对齐，三个 agent 接力", true, alpha);
                DrawModeCardFaded(g, ScaleRect(unsel, sc), "普通工作站模式",
                    "一次专注一个，切换即换", false, alpha);

                // 选中卡确认光晕：弹跳期间向外扩散一圈应用色
                // （半径随进度收敛；加下限避免椭圆退化，PathGradientBrush 对空路径会抛异常）
                if (conf > 0.01f && conf < 0.97f)
                {
                    int gr = Math.Max(Px(24), (int)(Math.Max(sel.Width, sel.Height) * 1.55f * (1f - EaseOut(conf))));
                    Rectangle grc = new Rectangle(sel.X + sel.Width / 2 - gr / 2, sel.Y + sel.Height / 2 - gr / 2, gr, gr);
                    if (_modeHaloBrush == null)
                    {
                        using (var path = new GraphicsPath())
                        {
                            path.AddEllipse(grc);
                            _modeHaloBrush = new PathGradientBrush(path);
                            _modeHaloBrush.SurroundColors = new Color[] { Color.FromArgb(0, hue.R, hue.G, hue.B) };
                        }
                    }
                    _modeHaloBrush.CenterColor = Color.FromArgb((int)(95 * (1f - conf)), hue.R, hue.G, hue.B);
                    g.FillEllipse(_modeHaloBrush, grc);
                }
            }
        }

        private void DrawModeChooser(Graphics g)
        {
            bool boot = _mode < 0;
            int left = boot ? 0 : _sideW;

            if (boot) DrawSparks(g, ClientRectangle);

            if (Width - left < Px(300)) return;   // 侧栏宽度时没有内容区可画，别算负数
            // 输入框的显隐跟协同状态走，避免离开协同模式后残留文本框
            bool showTask = _mode == 1 && _tiled && !_picking && _synPanel == null && (_pipe == null || _pipe.Phase == PipePhase.Idle);
            if (_taskBox != null && _taskBox.Visible != showTask) _taskBox.Visible = showTask;
            if (_mode == 1)
            {
                if (_picking) DrawPipeline(g);
                else if (_synPanel == null) DrawSynergy(g);
                return;
            }

            if (_mode >= 0)
            {
                // 已选定模式：这里就是工作区。还没打开应用时给一句引导，别让整屏空着
                if (_mode == 0)
                    using (var f = new Font("微软雅黑", Px(20), FontStyle.Regular, GraphicsUnit.Pixel))
                        TextRenderer.DrawText(g, "从左侧选择一个应用开始", f,
                            new Rectangle(left, 0, Width - left - (RightShown ? RightWPx : 0), Height),
                            TextDim,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            // 入场顺序：图标 -> 标题 -> 卡片，总时长约 1.8 秒；点一下可跳过
            // 总时长 2.5 秒，按秒分阶段
            float T = _enterT * 2.5f;
            float pBg = Clamp01(T / 0.40f);
            float pDots = Clamp01((T - 0.40f) / 0.70f);
            float pLines = Clamp01((T - 0.55f) / 0.35f) * (1f - Clamp01((T - 1.05f) / 0.25f));
            float pIcon = Clamp01((T - 0.80f) / 0.80f);
            float pRing = Clamp01((T - 1.35f) / 0.45f);
            float pBrand = Clamp01((T - 1.40f) / 0.60f);
            float pMode = Clamp01((T - 1.90f) / 0.35f);
            float pCardA = Clamp01((T - 2.05f) / 0.45f);
            float pCardB = Clamp01((T - 2.12f) / 0.45f);
            float pCards = Math.Max(pCardA, pCardB);

            int pad0 = RightShown ? RightWPx : 0;
            int areaW = Width - left - pad0;
            Color hue = _acc;

            // ① 中心径向光晕（应用色）+ 细线 + 汇聚光点
            int logoSize = Px(150);
            int logoX = left + (areaW - logoSize) / 2;
            int logoY = (int)(Height * 0.10f + (1f - EaseOut(pIcon)) * Px(18));
            int cx = logoX + logoSize / 2;
            int cy = logoY + logoSize / 2;

            if (pBg > 0.01f)
            {
                int gr = Px(560);
                int ga = (int)(80 * pBg);
                DrawCenterHalo(g, cx, cy, gr,
                    Color.FromArgb(Math.Max(0, Math.Min(255, ga)), hue.R, hue.G, hue.B),
                    Color.FromArgb(0, 255, 150, 64));
            }

            if (pLines > 0.01f)
                using (var pen = new Pen(Color.FromArgb((int)(70 * pLines), hue.R, hue.G, hue.B), Px(1)))
                    for (int i = 0; i < 6; i++)
                    {
                        double ang = i * Math.PI / 3.0 + 0.26;
                        int ex = cx + (int)(Math.Cos(ang) * Px(78));
                        int ey = cy + (int)(Math.Sin(ang) * Px(78) * 0.74);
                        g.DrawLine(pen, cx, cy, ex, ey);
                    }

            if (pDots > 0.01f && pDots < 0.995f)
            {
                float ease = EaseOut(pDots);
                float al = (float)Math.Sin(Math.PI * Math.Min(1f, pDots));
                for (int i = 0; i < 6; i++)
                {
                    double ang = i * Math.PI / 3.0 + 0.26;
                    float r = Px(300) * (1f - ease);
                    int dx = (int)(Math.Cos(ang) * r);
                    int dy = (int)(Math.Sin(ang) * r * 0.74);
                    int a2 = (int)(170 * al);
                    using (var br = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, a2)), hue.R, hue.G, hue.B)))
                        g.FillEllipse(br, cx + dx - Px(3), cy + dy - Px(3), Px(6), Px(6));
                }
            }

            // ③ 图标：由小、低透明平稳浮现，稳定后一圈柔和光环
            if (pIcon > 0.01f && _logo != null)
            {
                float sc = 0.88f + 0.12f * EaseOut(pIcon);
                int sz = (int)(logoSize * sc);
                Rectangle dr = new Rectangle(cx - sz / 2, cy - sz / 2, sz, sz);
                var ia = new System.Drawing.Imaging.ImageAttributes();
                var cm = new System.Drawing.Imaging.ColorMatrix();
                cm.Matrix33 = pIcon;
                ia.SetColorMatrix(cm);
                // 圆角裁切：logo.png 是白底方形，直接贴到深色开屏上会是个白方块
                var stIcon = g.Save();
                using (var clipPath = Round(dr, Px(30)))
                    g.SetClip(clipPath);
                g.DrawImage(_logo, dr, 0, 0, _logo.Width, _logo.Height, GraphicsUnit.Pixel, ia);
                g.Restore(stIcon);
                ia.Dispose();

                if (pRing > 0.001f && pRing < 0.999f)
                {
                    int rr = (int)(logoSize * 0.62f + logoSize * 0.55f * EaseOut(pRing));
                    int ra = (int)(110 * (1f - pRing));
                    using (var pen = new Pen(Color.FromArgb(Math.Max(0, Math.Min(255, ra)), hue.R, hue.G, hue.B), Px(2)))
                        g.DrawEllipse(pen, cx - rr, cy - rr, rr * 2, rr * 2);
                }
            }

            // ④ 品牌名 + 副标题（沿用 README 的定位语，不新编宣传语）
            if (pBrand > 0.02f)
            {
                Color brandCol = Color.FromArgb(240, 240, 246);
                int rise = (int)((1f - EaseOut(pBrand)) * Px(10));
                using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                using (var br = new SolidBrush(Color.FromArgb((int)(255 * pBrand), brandCol)))
                    g.DrawString("AgentHub", _fLogoBig, br,
                        new RectangleF(left, Height * 0.238f + rise, areaW, Px(58)), sf);

                Color subCol = Color.FromArgb(150, 150, 160);
                using (var sf2 = new StringFormat { Alignment = StringAlignment.Center })
                using (var br2 = new SolidBrush(Color.FromArgb((int)(255 * pBrand), subCol)))
                    g.DrawString("把 AI 应用收进一条侧栏", _fBrandSub, br2,
                        new RectangleF(left, Height * 0.300f + rise, areaW, Px(28)), sf2);
            }

            // ⑤ 模式选择标题
            if (pMode > 0.02f)
            using (var sf = new StringFormat { Alignment = StringAlignment.Center })
            {
                var r = new RectangleF(left, Height * 0.335f + (1f - EaseOut(pMode)) * Px(12), areaW, Px(76));
                using (var tb = new SolidBrush(Color.FromArgb((int)(255 * pMode), 232, 232, 240)))
                    g.DrawString("选择你的工作模式", _fModeTitle, tb, r, sf);
            }
            if (pCards <= 0.02f) return;

            Rectangle ca, cb;
            CardRects(out ca, out cb);
            DrawModeCardFaded(g, ca, "多 agent 协同模式", "拆解 → 执行 → 自检 → 对齐，三个 agent 接力", _mode == 1, pCardA);
            DrawModeCardFaded(g, cb, "普通工作站模式", "一次专注一个，切换即换", _mode == 0, pCardB);
        }

        private static readonly Color TextBlack = Color.FromArgb(20, 20, 24);

        private void DrawRightPanel(Graphics g)
        {
            Rectangle rp = RightPanelRect();
            using (var b = new SolidBrush(Color.FromArgb(250, 250, 251)))
                g.FillRectangle(b, rp);
            using (var p = new Pen(Color.FromArgb(232, 232, 236)))
                g.DrawLine(p, rp.X, 0, rp.X, Height);

            using (var f = new Font("微软雅黑", Px(13), FontStyle.Regular, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(Color.FromArgb(150, 150, 158)))
                TextRenderer.DrawText(g, "当前模式", f, new Rectangle(rp.X + Px(20), Px(28), rp.Width - Px(40), Px(24)),
                    Color.FromArgb(150, 150, 158), TextFormatFlags.Left);

            using (var f = new Font("微软雅黑", Px(24), FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, _mode == 1 ? "多 agent 协同" : "普通工作站", f,
                    new Rectangle(rp.X + Px(20), Px(58), rp.Width - Px(40), Px(40)),
                    TextBlack, TextFormatFlags.Left);

            Rectangle add = AddProcRect();
            bool addOff = _agents.Count >= 18;
            Color addCol = addOff ? Color.FromArgb(205, 205, 210)
                          : (_rpHot == 0 ? ControlPaint.Light(Orange, 0.10f) : Orange);
            using (var path = Round(add, Px(14)))
            using (var b = new SolidBrush(addCol))
                g.FillPath(b, path);
            using (var f = new Font("微软雅黑", Px(18), FontStyle.Bold, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, addOff ? "已达上限" : "＋  添加进程", f, add, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            Rectangle sw = SwitchModeRect();
            using (var path = Round(sw, Px(12)))
            using (var b = new SolidBrush(_rpHot == 1 ? Color.FromArgb(228, 228, 233) : Color.FromArgb(240, 240, 243)))
                g.FillPath(b, path);
            using (var f = new Font("微软雅黑", Px(16), FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, "切换模式", f, sw, Color.FromArgb(70, 70, 80),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // 模式卡淡入：离屏缓存位图 + 透明混合（不再每帧重建 540×300 位图，开屏帧率关键优化）
        private void DrawModeCardFaded(Graphics g, Rectangle r, string title, string sub, bool selected, float p)
        {
            if (p <= 0.02f) return;
            int dy = (int)((1f - EaseOut(p)) * Px(16));
            Rectangle rr = new Rectangle(r.X, r.Y + dy, r.Width, r.Height);
            if (p >= 0.995f) { DrawModeCard(g, rr, title, sub, selected); return; }

            Bitmap b = CardCache(selected ? 0 : 1, title, sub, selected);
            if (b == null) return;
            var ia = new System.Drawing.Imaging.ImageAttributes();
            var cm = new System.Drawing.Imaging.ColorMatrix();
            cm.Matrix33 = p;
            ia.SetColorMatrix(cm);
            g.DrawImage(b, rr, 0, 0, b.Width, b.Height, GraphicsUnit.Pixel, ia);
            ia.Dispose();
        }

        // 模式卡尺寸（与 CardRects 一致；转场时沿用开屏全屏布局，避免卡片跳位）
        private void ModeCardSize(out int w, out int h)
        {
            int cw;
            if (_modeSel >= 0) cw = Width;                                            // 转场中：开屏全屏布局
            else if (_mode < 0) cw = Width - (RightShown ? RightWPx : 0);
            else cw = Width - _sideW - (RightShown ? RightWPx : 0);
            int gap = Px(48);
            w = Math.Max(Px(200), Math.Min(Px(540), (cw - gap * 3) / 2));
            h = Px(300);
        }

        // 模式卡离屏缓存：内容固定（标题/描边只随环境光色），_acc 变化时才重建
        private Bitmap CardCache(int which, string title, string sub, bool selected)
        {
            int w, h;
            ModeCardSize(out w, out h);
            Bitmap b = which == 0 ? _cardBmpA : _cardBmpB;
            if (b != null && b.Width == w && b.Height == h && ColorClose(_acc, _cardBmpAcc)) return b;
            if (b != null) b.Dispose();
            b = new Bitmap(w, h);
            using (var gg = Graphics.FromImage(b))
            {
                gg.SmoothingMode = SmoothingMode.AntiAlias;
                DrawModeCard(gg, new Rectangle(0, 0, w, h), title, sub, selected);
            }
            if (which == 0) _cardBmpA = b; else _cardBmpB = b;
            _cardBmpAcc = _acc;
            return b;
        }

        private void DrawModeCard(Graphics g, Rectangle r, string title, string sub, bool selected)
        {
            using (GraphicsPath p = Round(r, Px(18)))
            {
                using (var fb = new SolidBrush(Color.FromArgb(18, 18, 24)))
                    g.FillPath(fb, p);
                using (var pen = new Pen(selected ? _acc : Color.FromArgb(39, 39, 47), selected ? 2.4f : 1.2f))
                    g.DrawPath(pen, p);
            }

            TextRenderer.DrawText(g, title, _fCardTitle, new Rectangle(r.X + Px(24), r.Y + Px(28), r.Width - Px(48), Px(40)),
                TextMain, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            TextRenderer.DrawText(g, sub, _fCardSub, new Rectangle(r.X + Px(24), r.Y + Px(74), r.Width - Px(48), Px(30)),
                TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            using (var f = new Font("微软雅黑", Px(14), FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, selected ? "已选择" : "点这里进入", f,
                    new Rectangle(r.X + Px(24), r.Bottom - Px(46), r.Width - Px(48), Px(26)),
                    selected ? _acc : TextFaint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        }

        // 点击涟漪：从按下点扩散的应用色双环 + 淡晕（半径更大、更亮，反馈更明显）
        private void DrawRipple(Graphics g, Rectangle r, Color hue, float t)
        {
            if (t >= 1f) return;
            if (t < 0.03f) t = 0.03f;
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            float e = EaseOut(t);
            float rad = r.Width * 1.15f * e;
            int a = (int)(150 * (1f - t));
            if (a < 6) return;
            // 外环（淡、半径略大，构成双环层次）
            using (var p2 = new Pen(Color.FromArgb(a / 2, hue.R, hue.G, hue.B), Math.Max(1f, (float)Px(1))))
                g.DrawEllipse(p2, cx - rad * 1.08f, cy - rad * 1.08f, rad * 2.16f, rad * 2.16f);
            // 主环
            using (var p = new Pen(Color.FromArgb(a, hue.R, hue.G, hue.B), Math.Max(1.6f, (float)Px(2))))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawEllipse(p, cx - rad, cy - rad, rad * 2, rad * 2);
            }
            // 中心淡晕
            using (var b = new SolidBrush(Color.FromArgb(a / 3, hue.R, hue.G, hue.B)))
                g.FillEllipse(b, cx - rad * 0.55f, cy - rad * 0.55f, rad * 1.1f, rad * 1.1f);
        }

        private void DrawTile(Graphics g, Agent a, Rectangle r)
        {
            int idx = AgentIndex(a.Id);
            bool isChosen = idx >= 0 && _chosen.Contains(idx);
            Color hue = AppHue(a);

            // 点击反馈：按住深缩 0.84，松开带回弹过冲；涟漪从按下点扩散
            if (_pressIdx == idx)
                r = ScaleRect(r, _pressHeld ? 0.84f : ReleaseSpring(_relT));
            if (_rippleIdx == idx && _rippleT < 1f)
                DrawRipple(g, r, hue, _rippleT);

            int sz = Px(36);
            Rectangle ir = new Rectangle(r.X + (r.Width - sz) / 2, r.Y + (r.Height - sz) / 2, sz, sz);
            Bitmap bmp = Icons.Get(a);
            if (bmp == null) bmp = Icons.Letter(a.Name, 48);
            if (bmp == null) return;

            // 凹槽井：未选中图标嵌入井中（顶暗底亮的内阴影）
            Rectangle wr = new Rectangle(ir.X - Px(5), ir.Y - Px(5), ir.Width + Px(10), ir.Height + Px(10));
            using (GraphicsPath p = Round(wr, Px(11)))
            {
                using (var b = new SolidBrush(Color.FromArgb(10, 11, 15)))
                    g.FillPath(b, p);
                var clip = g.Save();
                g.SetClip(p);
                using (var lg = new LinearGradientBrush(new Rectangle(wr.X, wr.Y, wr.Width, Math.Max(2, wr.Height / 2)),
                    Color.FromArgb(50, 0, 0, 0), Color.Transparent, LinearGradientMode.Vertical))
                    g.FillRectangle(lg, wr);
                using (var lg = new LinearGradientBrush(new Rectangle(wr.X, wr.Bottom - Math.Max(2, wr.Height / 2), wr.Width, Math.Max(2, wr.Height / 2)),
                    Color.Transparent, Color.FromArgb(14, 255, 255, 255), LinearGradientMode.Vertical))
                    g.FillRectangle(lg, wr);
                g.Restore(clip);
            }

            // 已选：井内被该应用的光微微点亮
            if (isChosen)
            {
                int gr = Math.Max(Px(46), wr.Width + Px(14));
                Rectangle grc = new Rectangle(wr.X - (gr - wr.Width) / 2, wr.Y - (gr - wr.Height) / 2, gr, gr);
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(grc);
                    using (var pgb = new PathGradientBrush(path))
                    {
                        pgb.CenterColor = Color.FromArgb((int)(38 * AmbientAlpha()), hue.R, hue.G, hue.B);
                        pgb.SurroundColors = new Color[] { Color.FromArgb(0, hue.R, hue.G, hue.B) };
                        g.FillEllipse(pgb, grc);
                    }
                }
            }

            // 按压高光：按住瞬间井外泛一层应用色，随按压保持而亮
            if (_pressIdx == idx && _pressHeld)
            {
                int gr2 = wr.Width + Px(24);
                Rectangle grc2 = new Rectangle(wr.X - (gr2 - wr.Width) / 2, wr.Y - (gr2 - wr.Height) / 2, gr2, gr2);
                using (var path2 = new GraphicsPath())
                {
                    path2.AddEllipse(grc2);
                    using (var pgb2 = new PathGradientBrush(path2))
                    {
                        pgb2.CenterColor = Color.FromArgb((int)(78 * AmbientAlpha()), hue.R, hue.G, hue.B);
                        pgb2.SurroundColors = new Color[] { Color.FromArgb(0, hue.R, hue.G, hue.B) };
                        g.FillEllipse(pgb2, grc2);
                    }
                }
            }

            if (Icons.IsDark(bmp, a.Id))
            {
                using (GraphicsPath p = Round(new Rectangle(ir.X - 2, ir.Y - 2, ir.Width + 4, ir.Height + 4), Px(9)))
                using (var b = new SolidBrush(Color.FromArgb(44, 44, 54)))
                    g.FillPath(b, p);
            }

            InterpolationMode old = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(bmp, ir);
            g.InterpolationMode = old;

            // 运行光点：呼吸（显示中的应用由亮面胶囊表达，不再画点）
            if (isChosen && idx != _shown)
            {
                float ph = _reduceMotion ? 0.75f : (float)(Math.Sin(_breath) * 0.5 + 0.5);
                int d = Px(7);
                Rectangle dr = new Rectangle(wr.Right - d / 2 - Px(1), wr.Bottom - d / 2 - Px(1), d, d);
                using (var b = new SolidBrush(Color.FromArgb((int)(60 + 160 * ph), hue.R, hue.G, hue.B)))
                    g.FillEllipse(b, dr);
            }
        }

        private Rectangle PlusRect()
        {
            // 跟着最后一个图标的当前位置走，避免动画中途露出空档
            float maxY = ItemTopPx;
            foreach (string id in _order)
            {
                float y;
                if (_y.TryGetValue(id, out y) && y > maxY) maxY = y;
            }
            return new Rectangle(ItemXPx, (int)Math.Round(maxY) + ItemStepPx, ItemSizePx, ItemSizePx);
        }

        private void DrawPlus(Graphics g)
        {
            Rectangle r = PlusRect();
            if (_miscObj == PressPlus)
                r = ScaleRect(r, _pressHeld ? 0.84f : ReleaseSpring(_relT));   // 按压缩放 + 松开回弹
            bool hot = _plusHot;
            using (var pen = new Pen(hot ? _acc : Color.FromArgb(56, 56, 68), Math.Max(1.2f, Px(2) * 0.7f)))
            {
                pen.DashStyle = DashStyle.Dash;
                using (GraphicsPath p = Round(r, Px(12)))
                    g.DrawPath(pen, p);
            }
            using (var pen = new Pen(hot ? _acc : TextFaint, Math.Max(1.6f, Px(2) * 0.9f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, a = Px(7);
                g.DrawLine(pen, cx - a, cy, cx + a, cy);
                g.DrawLine(pen, cx, cy - a, cx, cy + a);
            }
            if (_rippleIdx == PressPlus && _rippleT < 1f) DrawRipple(g, r, _acc, _rippleT);
        }

        private void DrawExit(Graphics g, Rectangle r, bool hot)
        {
            if (_miscObj == PressExit)
                r = ScaleRect(r, _pressHeld ? 0.84f : ReleaseSpring(_relT));   // 按压缩放 + 松开回弹
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, a = Px(6);
            using (var pen = new Pen(hot ? _acc : TextFaint, Math.Max(1.4f, Px(2) * 0.8f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawArc(pen, cx - a, cy - a, a * 2, a * 2, -60, 300);
                g.DrawLine(pen, cx, cy - a - Px(3), cx, cy - Px(1));
            }
            if (_rippleIdx == PressExit && _rippleT < 1f) DrawRipple(g, r, _acc, _rippleT);
        }

        // ---------- 交互 ----------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // 协同模式聚焦应用时，Esc = 回到流程条（应用保持已选）
            if (keyData == Keys.Escape && _mode == 1 && _focusIdx >= 0)
            {
                BackToPipeView();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.AddClipboardFormatListener(Handle);
            for (int i = 0; i < _agents.Count && i < 9; i++)
                Native.RegisterHotKey(Handle, HotkeyBase + i, Native.MOD_ALT | Native.MOD_NOREPEAT, 0x31 + i);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            for (int i = 0; i < 9; i++) Native.UnregisterHotKey(Handle, HotkeyBase + i);
            Native.RemoveClipboardFormatListener(Handle);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_CLIPBOARDUPDATE) { ClipboardChanged(); return; }
            if (m.Msg == Native.WM_MOUSEACTIVATE)
            {
                // 点在可输入控件上时必须允许窗口激活，否则输入法无法附着、无法候选上屏
                bool onInput = OnInputAtCursor();
                Program.Log("activating fg=" + Native.Title(Native.GetForegroundWindow()) + " isSelf=" + (Native.GetForegroundWindow() == Handle) + " onInput=" + onInput + " ctl=" + (GetChildAtPoint(PointToClient(Cursor.Position), GetChildAtPointSkip.Invisible) != null ? GetChildAtPoint(PointToClient(Cursor.Position), GetChildAtPointSkip.Invisible).GetType().Name : "null"));
                if (onInput) Native.SetNoActivate(Handle, false);   // 必须摘掉 NOACTIVATE，否则输入法附着不上
                m.Result = (IntPtr)(onInput ? 1 : Native.MA_NOACTIVATE);
                return;
            }
            // 点任务栏图标：当作侧栏开关，不真的最小化
            if (m.Msg == 0x0112 && (m.WParam.ToInt32() & 0xFFF0) == 0xF020)
            {
                ToggleDock();
                return;
            }
            if (m.Msg == Native.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                if (id >= HotkeyBase && id < HotkeyBase + _agents.Count) { LeftClick(id - HotkeyBase); return; }
            }
            base.WndProc(ref m);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            {
                int rp = RightShown ? (AddProcRect().Contains(e.Location) ? 0 : (SwitchModeRect().Contains(e.Location) ? 1 : -1)) : -1;
                if (rp != _rpHot) { _rpHot = rp; Cursor = rp >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            }

            if (DockGone)
            {
                // 窗口滑到 x=-89，露在外面的是它右边的 3px
                // 必须已经完全滑出才认热区：滑行途中窗口左移会让光标落进最后 4px，否则会自己叫回自己
                if (_slideT > 0.95f && e.X >= _sideW - 4) ShowDock();
                return;
            }

            int idx = HitTest(e.Location);
            bool ex = ExitRect().Contains(e.Location);
            bool ph = _agents.Count < 18 && PlusRect().Contains(e.Location);
            // 协同态：卡片头部按钮 / 轮次条目 / 底部按钮 都给手型
            int hoverRound = -1;
            bool overCardBtn = false;
            if (_mode == 1 && _tiled && _focusIdx < 0)
            {
                foreach (Rectangle card in TileRects())
                {
                    if (CardDialogRect(card).Contains(e.Location) ||
                        CardSuppRect(card).Contains(e.Location) ||
                        (_pipe != null && _pipe.ConfirmPending && CardConfirmRect(card).Contains(e.Location)))
                    {
                        overCardBtn = true;
                        break;
                    }
                }
                if (_pipe != null && _pipe.Session != null)
                {
                    for (int i = 0; i < _pipe.Session.Rounds.Count; i++)
                        if (RoundItemRect(i).Contains(e.Location)) { hoverRound = i; break; }
                }
            }
            bool interactive = idx >= 0 || ex || ph || overCardBtn || hoverRound >= 0;
            if (idx != _hover || ex != _hoverExit || ph != _plusHot || hoverRound != _hoverRound)
            {
                if (idx != _hover) { _hoverT = 0f; Kick(); }
                _hover = idx;
                _hoverExit = ex;
                _plusHot = ph;
                _hoverRound = hoverRound;
                Cursor = interactive ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1; _hoverExit = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);

            if (e.Button == MouseButtons.Right)
            {
                int ri = HitTest(e.Location);
                if (ri < 0) return;

                var labels = new List<string>();
                var acts = new List<Action>();
                if (_chosen.Contains(ri))
                {
                    int k = ri;
                    labels.Add("取消「" + _agents[k].Name + "」");
                    acts.Add(Defer(delegate { Cancel(k); }));
                }
                if (_agents[ri].Custom)
                {
                    int k = ri;
                    labels.Add("编辑");
                    acts.Add(Defer(delegate { ShowEdit(k); }));
                    labels.Add("移除");
                    acts.Add(Defer(delegate { RemoveAgent(k); }));
                }
                if (labels.Count == 0) return;

                MiniMenu.ShowAt(PointToScreen(e.Location), labels.ToArray(), acts.ToArray());
                return;
            }

            if (e.Button != MouseButtons.Left) return;
            if (RightShown && RightPanelRect().Contains(e.Location))
            {
                if (AddProcRect().Contains(e.Location)) { ShowAdd(); return; }
                if (SwitchModeRect().Contains(e.Location)) { _mode = -1; _modeSel = -1; _modeT = 1f; _enterT = 0f; Kick(); Invalidate(); return; }
                return;
            }
            if (_shown < 0)
            {
                Rectangle ca, cb;
                CardRects(out ca, out cb);
                if (ca.Contains(e.Location))
                {
                    _modeSel = 1; _modeT = 0f; Kick();   // 模式转场：卡片确认收拢 → 工作区浮现
                    _mode = 1; UpdateRightDock(); ShowPipelineView();
                    return;
                }
                if (cb.Contains(e.Location))
                {
                    _modeSel = 0; _modeT = 0f; Kick();
                    _mode = 0; UpdateRightDock(); Invalidate();
                    return;   // 普通工作站：只负责选中，应用由侧栏点
                }

                // 协同：输入阶段（卡 1 是任务输入框）
                bool inputPhase = _mode == 1 && _tiled && !_picking && (_pipe == null || _pipe.Phase == PipePhase.Idle);
                if (inputPhase && _roles.Count > 0)
                {
                    Rectangle tr0 = TileRects()[0];
                    if (TaskStartRect(tr0).Contains(e.Location)) { StartFromBox(); return; }
                }

                // 卡片流态：卡头按钮 → 整卡打开对话窗；右侧面板 → 轮次查看；底部 → 操作带
                if (_mode == 1 && _tiled && _focusIdx < 0)
                {
                    Rectangle[] tr = TileRects();
                    for (int k = 0; k < 3 && k < _roles.Count; k++)
                    {
                        if (tr[k].Contains(e.Location))
                        {
                            // 卡头按钮优先
                            if (CardDialogRect(tr[k]).Contains(e.Location)) { OpenDialog(k); return; }
                            if (CardSuppRect(tr[k]).Contains(e.Location)) { OpenDialog(k); return; }
                            if (_pipe != null && _pipe.ConfirmPending && CardStatus(k) == 2 &&
                                CardConfirmRect(tr[k]).Contains(e.Location))
                            {
                                Handoff();
                                return;
                            }
                            OpenDialog(k);   // 整卡点击 = 打开该 Agent 的对话窗（不嵌入真实页面）
                            return;
                        }
                    }

                    // 各轮结论查看
                    if (_pipe != null && _pipe.Session != null)
                    {
                        for (int i = 0; i < _pipe.Session.Rounds.Count; i++)
                        {
                            if (RoundItemRect(i).Contains(e.Location))
                            {
                                RoundViewer.Show(_pipe.Session.Rounds[i]);
                                return;
                            }
                        }
                    }

                    // 底部操作带
                    if (_pipe != null)
                    {
                        var labels = new List<string>();
                        var acts = new List<Action>();
                        BuildBottomBar(labels, acts);
                        Rectangle[] btns = BottomButtons(labels.Count);
                        for (int i = 0; i < labels.Count; i++)
                        {
                            if (!btns[i].Contains(e.Location)) continue;
                            acts[i]();
                            return;
                        }
                    }

                    if (_pipe != null && _pipe.Running && PipeAutoRect().Contains(e.Location))
                    {
                        _pipe.Config.AutoAdvance = !_pipe.Config.AutoAdvance;
                        Toast.Show(_pipe.Config.AutoAdvance ? "自动接力已开：步完成后直接交给下一个" : "自动接力已关：每步等你确认");
                        if (_pipe.ConfirmPending && _pipe.Config.AutoAdvance) Handoff();
                        Invalidate();
                        return;
                    }
                }

                // 放大态：右侧窄条的小卡切换（点击当前卡 = 回卡片流）
                if (_mode == 1 && _tiled && _focusIdx >= 0)
                {
                    for (int k = 0; k < 3 && k < _roles.Count; k++)
                    {
                        if (ZoomMiniRect(k).Contains(e.Location))
                        {
                            if (k == _focusIdx) { BackToPipeView(); return; }
                            BackToPipeView();
                            FocusTile(k);
                            return;
                        }
                    }
                }
            }

            if (PipeEntryRect().Contains(e.Location))
            {
                if (_mode == 1 && _focusIdx >= 0) { BackToGrid(); }
                else if (_mode == 1) { ExitSynergy(); }
                else ShowPipelineView();
                return;
            }
            if (ExitRect().Contains(e.Location))
            {
                _miscObj = PressExit; _pressHeld = true; _pressT = 1f; _relT = 1f; _rippleT = 1f;
                _rippleAt = e.Location; _pressIdx = -1; Kick(); Invalidate();
                HideToBackground();
                return;
            }
            if (_agents.Count < 18 && PlusRect().Contains(e.Location))
            {
                _miscObj = PressPlus; _pressHeld = true; _pressT = 1f; _relT = 1f; _rippleT = 1f;
                _rippleAt = e.Location; _pressIdx = -1; Kick(); Invalidate();
                ShowAdd();
                return;
            }

            int idx2 = HitTest(e.Location);
            if (idx2 >= 0)
            {
                _pressIdx = idx2; _pressHeld = true; _pressT = 1f; _relT = 1f; _rippleT = 1f;
                _rippleAt = e.Location; _miscObj = -1; Kick();
                LeftClick(idx2);
            }
        }

        // 松开左键：结束按压保持，图标/按钮回弹，并从按下点发一层涟漪
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;

            _pressHeld = false;
            if (_relT >= 1f) { _relT = 0f; Kick(); }

            int obj = _pressIdx >= 0 ? _pressIdx : _miscObj;
            if (obj != -1)
            {
                _rippleIdx = obj;
                _rippleT = 0f;
                Kick();
            }
            Invalidate();
        }

        private int HitTest(Point p)
        {
            if (p.X < Px(4) || p.X > _sideW) return -1;
            for (int k = 0; k < _order.Count; k++)
            {
                int i = AgentIndex(_order[k]);
                if (i >= 0 && ItemRectNow(i).Contains(p)) return i;
            }
            return -1;
        }

        // ---------- 开屏预加载 ----------

        private Splash _splash;
        private bool _splashDone;

        private void BuildSplash()
        {
            var items = new List<Agent>();
            foreach (string id in _restore)
            {
                int i = AgentIndex(id);
                if (i >= 0) items.Add(_agents[i]);
            }
            if (items.Count == 0) return;

            Bitmap logo = null;
            try
            {
                using (var ic = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                    if (ic != null) logo = ic.ToBitmap();
            }
            catch { }

            _splash = new Splash(items, logo);
        }

        private void MarkSplash(string id, string text, int kind)
        {
            if (_splash == null || _splash.IsDisposed) return;
            try { _splash.Set(id, text, kind); } catch { }
            if (kind == 2 && !_splashDone) CloseSplash();   // 第一个就绪就进入
        }

        private void CloseSplash()
        {
            _splashDone = true;
            if (_splash == null) return;
            try { _splash.Close(); } catch { }
            _splash = null;
            ShowDock();
        }

        // ---------- 托盘 / 后台 ----------

        private void SetupTray()
        {
            _tray = new NotifyIcon();
            try { _tray.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { _tray.Icon = SystemIcons.Application; }
            _tray.Text = "AGENT HUB";
            _tray.Visible = true;
            _tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowFromBackground();
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("显示", null, delegate { BeginInvoke((MethodInvoker)ShowFromBackground); });
            menu.Items.Add("隐藏到托盘", null, delegate { BeginInvoke((MethodInvoker)HideToBackground); });
            menu.Items.Add("重新加载", null, delegate { BeginInvoke((MethodInvoker)ReloadApp); });
            menu.Items.Add("打开日志", null, delegate { BeginInvoke((MethodInvoker)OpenLog); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { BeginInvoke((MethodInvoker)QuitApp); });
            _tray.ContextMenuStrip = menu;
        }

        private void HideToBackground()
        {
            if (DockGone) return;
            _dock = DockState.Background;
            foreach (int i in _chosen)
            {
                IntPtr h;
                if (_windows.TryGetValue(i, out h) && h != IntPtr.Zero && Native.IsWindow(h))
                {
                    // 跟侧栏同向滑出，而不是"啪"一下消失
                    if (!Native.AnimateWindow(h, 220, Native.AW_HIDE | Native.AW_SLIDE | Native.AW_HOR_NEGATIVE))
                        Host.Hide(h);
                }
            }
            Kick();
            SaveSessionNow();
            Program.Log("to background");
        }

        private void ShowFromBackground()
        {
            ShowDock();
            Program.Log("from background");
        }

        private void OpenLog()
        {
            try { Process.Start("notepad.exe", "\"" + Program.LogPath + "\""); } catch { }
        }

        private void ReloadApp()
        {
            Program.Log("reload requested");
            try
            {
                Process.Start("cmd.exe", "/c timeout /t 2 >nul & start \"\" \"" + Application.ExecutablePath + "\"");
            }
            catch { }
            QuitApp();
        }

        private void QuitApp()
        {
            _reallyExit = true;
            Close();
        }

        private void HideTick(object sender, EventArgs e)
        {
            // 协同流水线：某步超过时限没有动作就暂停，状态不丢
            if (_mode == 1 && _pipe != null && _pipe.Running && !_pipe.Paused
                && (DateTime.Now - _pipeAt).TotalMinutes >= _pipe.Config.StepTimeoutMinutes)
            {
                _pipe.Paused = true;
                _pipe.Note = "已暂停：超过 " + _pipe.Config.StepTimeoutMinutes + " 分钟没有动作";
                Invalidate();
            }
        }

        private void ShowDock()
        {
            bool was = DockGone;
            _dock = DockState.Visible;
            if (was) { ReplayDeck(); Kick(); }

            // 窗口藏起来的时候应用也被隐藏了，唤回时要一起恢复
            IntPtr h;
            if (_shown >= 0 && _windows.TryGetValue(_shown, out h) && Native.IsWindow(h))
            {
                _expandT = 0f;
                _popT = 0f; _popIdx = _shown;   // 唤回时重新弹一次
                Host.Apply(h, Handle, ContentRect(), _agents[_shown].OffsetFrame);
                Host.ForceForeground(h);
                if (was) Native.AnimateWindow(h, 220, Native.AW_ACTIVATE | Native.AW_SLIDE | Native.AW_HOR_POSITIVE);
            }
            ApplyBounds();
            Invalidate();
        }

        private void ToggleDock()
        {
            if (DockGone) ShowDock(); else HideToBackground();
        }

        // 每次唤出都重放一次"堆叠散开"
        private void ReplayDeck()
        {
            for (int k = 0; k < _order.Count; k++)
            {
                _y[_order[k]] = ItemTopPx + k * 6f;
                _delay[_order[k]] = k * 0.038f;
            }
        }

        // ---------- 名额与切换 ----------

        // 菜单里的动作不能在菜单自己的消息处理里执行：
        // 那时 Close() 已经把菜单销毁，而"取消最后一个应用"会把主窗口从 92px 撑满屏幕，
        // 两者叠在一起会让 WinForms 在已释放的窗口上收尾时抛异常。
        private Action Defer(Action a)
        {
            return delegate
            {
                try { BeginInvoke(a); }
                catch { try { a(); } catch { } }
            };
        }

        public void Select(int idx) { LeftClick(idx); }

        public string AgentIdAt(int i) { return (i >= 0 && i < _agents.Count) ? _agents[i].Id : null; }

        public void CancelById(string id)
        {
            for (int i = 0; i < _agents.Count; i++) if (_agents[i].Id == id) { Cancel(i); return; }
        }

        // ---------- 会话与自定义 ----------

        private void SaveSessionNow()
        {
            var ids = new List<string>();
            foreach (int i in _chosen) if (i >= 0 && i < _agents.Count) ids.Add(_agents[i].Id);
            Config.SaveSession(ids, (_shown >= 0 && _shown < _agents.Count) ? _agents[_shown].Id : null, _order);
        }

        private void RestoreTick(object sender, EventArgs e)
        {
            if (_restore.Count == 0) { _restoreTimer.Stop(); return; }
            if (_pendingIdx >= 0) return;
            if (DateTime.Now < _restoreAt) return;

            string id = _restore[0];
            _restore.RemoveAt(0);
            _restoreAt = DateTime.Now.AddSeconds(4);

            int idx = -1;
            for (int i = 0; i < _agents.Count; i++) if (_agents[i].Id == id) { idx = i; break; }

            if (idx < 0)
            {
                Toast.Show("「" + id + "」已失效，本次跳过");
                return;
            }
            if (_chosen.Contains(idx)) return;
            if (_chosen.Count >= MaxSlots) return;

            _chosen.Add(idx);
            StartAgent(idx, true);
            Invalidate();
        }

        private void ShowAdd()
        {
            if (_agents.Count >= 18) { Toast.Show("最多 18 个应用"); return; }
            Agent a = AgentForm.Show(this, null);
            if (a == null) return;
            _agents.Add(a);

            _order.Add(a.Id);

            _y[a.Id] = ItemTopFor(a.Id);

            _delay[a.Id] = 0f;
            Config.Custom.Add(a);
            Config.SaveCustom();
            Toast.Show("已添加「" + a.Name + "」");
            Invalidate();
        }

        private void ShowEdit(int i)
        {
            Agent src = _agents[i];
            Agent a = AgentForm.Show(this, src);
            if (a == null) return;
            Config.SaveCustom();
            Icons.Forget(src.Id);
            Toast.Show("已保存「" + a.Name + "」");
            Invalidate();
        }

        private void RemoveAgent(int i)
        {
            if (i < 0 || i >= _agents.Count || !_agents[i].Custom) return;
            Agent gone = _agents[i];
            Cancel(i);
            _agents.RemoveAt(i);

            _order.Remove(gone.Id);

            _y.Remove(gone.Id);

            _delay.Remove(gone.Id);
            Config.Custom.RemoveAll(delegate(Agent x) { return x.Id == gone.Id; });
            Config.SaveCustom();
            Icons.Forget(gone.Id);
            RemapAfterRemove(i);
            // 被移除的正是协同角色：平铺凑不齐三个了，退出平铺但保留其余应用
            if (_tiled && _roles.Count < 3) AbandonSynergyKeepApps();
            Toast.Show("已移除「" + gone.Name + "」");
            Invalidate();
        }

        private void RemapAfterRemove(int removed)
        {
            var chosen = new HashSet<int>();
            foreach (int i in _chosen) chosen.Add(i > removed ? i - 1 : i);
            _chosen.Clear();
            foreach (int i in chosen) _chosen.Add(i);

            Rekey(_windows, removed);
            Rekey(_pids, removed);
            _launchedByMe.Clear();   // 索引会平移，宁可不杀（安全方向）也不要误杀

            // _dead：健康检查的连续未命中计数，键同样是 agent 索引
            var dead = new Dictionary<int, int>();
            foreach (KeyValuePair<int, int> kv in _dead) dead[kv.Key > removed ? kv.Key - 1 : kv.Key] = kv.Value;
            _dead.Clear();
            foreach (KeyValuePair<int, int> kv in dead) _dead[kv.Key] = kv.Value;

            // _roles：协同角色也是 agent 索引；_focusIdx 是角色在 _roles 里的位置
            for (int k = _roles.Count - 1; k >= 0; k--)
            {
                if (_roles[k] == removed)
                {
                    _roles.RemoveAt(k);
                    if (_focusIdx == k) _focusIdx = -1;
                    else if (_focusIdx > k) _focusIdx--;
                }
                else if (_roles[k] > removed) _roles[k]--;
            }

            if (_pendingIdx == removed) { _pendingIdx = -1; _poll.Stop(); }
            else if (_pendingIdx > removed) _pendingIdx--;

            if (_shown > removed) _shown--;
            else if (_shown == removed) _shown = -1;
            SaveSessionNow();
        }

        private static void Rekey(Dictionary<int, IntPtr> d, int removed)
        {
            var copy = new Dictionary<int, IntPtr>(d);
            d.Clear();
            foreach (KeyValuePair<int, IntPtr> kv in copy) d[kv.Key > removed ? kv.Key - 1 : kv.Key] = kv.Value;
        }

        private static void Rekey(Dictionary<int, int> d, int removed)
        {
            var copy = new Dictionary<int, int>(d);
            d.Clear();
            foreach (KeyValuePair<int, int> kv in copy) d[kv.Key > removed ? kv.Key - 1 : kv.Key] = kv.Value;
        }


        private void LeftClick(int i)
        {
            if (i < 0 || i >= _agents.Count) return;

            if (_picking)
            {
                if (_roles.Contains(i)) _roles.Remove(i);
                else if (_roles.Count < 3) _roles.Add(i);
                else { Toast.Show("最多三个角色，先点掉一个"); return; }
                if (_roles.Count == 3)
                {
                    _picking = false;
                    StartSynergy();
                    // 名额不够时 StartSynergy 会把自己打回挑选状态，别误报成功
                    if (_tiled) Toast.Show("三个角色就绪：在上方输入任务需求，点「开始协同」");
                }
                Invalidate();
                return;
            }
            if (_pendingIdx >= 0) return;

            if (i == _shown) { ForceShown(); return; }
            if (_chosen.Contains(i)) { ShowChosen(i); return; }

            if (_chosen.Count >= MaxSlots)
            {
                Toast.Show("已选满三个，右键取消一个再开");
                return;
            }

            _chosen.Add(i);
            StartAgent(i);
        }

        private void ForceShown()
        {
            IntPtr h;
            if (_shown >= 0 && _windows.TryGetValue(_shown, out h) && h != IntPtr.Zero && Native.IsWindow(h))
                Host.ForceForeground(h);
        }

        private void ShowChosen(int i)
        {
            IntPtr h;
            if (!_windows.TryGetValue(i, out h) || h == IntPtr.Zero || !Native.IsWindow(h))
            {
                StartAgent(i);
                return;
            }

            IntPtr old = ActiveHandle;
            _shown = i;
            MoveToFront(i);
            ApplyBounds();
            _expandT = 0f;
            _popT = 0f; _popIdx = i;   // 选中瞬间：卡片弹出回弹
            Kick();

            if (Host.Apply(h, Handle, ContentRect(), _agents[i].OffsetFrame))
            {
                if (old != IntPtr.Zero && old != h) Host.Hide(old);
                Host.ForceForeground(h);
            }
            else
            {
                if (old != IntPtr.Zero && old != h) { _shown = IndexOfWindow(old); }
                else _shown = -1;
                ApplyBounds();
                Toast.Show("「" + _agents[i].Name + "」的窗口拒绝被接管");
            }
            Invalidate();
        }

        private int IndexOfWindow(IntPtr h)
        {
            foreach (KeyValuePair<int, IntPtr> kv in _windows)
                if (kv.Value == h) return kv.Key;
            return -1;
        }

        private void StartAgent(int i) { StartAgent(i, false); }

        private void StartAgent(int i, bool hidden)
        {
            Agent a = _agents[i];
            Program.Log("start " + a.Id);
            _pendingIdx = i;
            _launchStamp = DateTime.Now;
            _deadline = DateTime.Now.AddSeconds(45);
            Invalidate();

            IntPtr h = Host.Find(a, DateTime.MinValue);
            if (h != IntPtr.Zero) { FinishStart(i, h); return; }

            MarkSplash(a.Id, "启动中", 1);
            _priorPids[i] = Host.PidsByName(a.Proc);   // 先记下别人已经开着的实例
            _launchedByMe.Add(i);   // 从这一刻起，这个应用的窗口进程算我方
            try { Host.Launch(a, hidden); }
            catch (Exception ex)
            {
                Program.Log("launch failed: " + ex.Message);
                _pendingIdx = -1;
                _chosen.Remove(i);
                Toast.Show("「" + a.Name + "」启动失败");
                FailSynergy("「" + a.Name + "」启动失败：" + ex.Message);
                Invalidate();
                return;
            }
            _poll.Start();
        }

        // 协同模式下某个应用启动失败：流程停住等用户重试/结束，绝不在失败后悄悄继续
        private void FailSynergy(string reason)
        {
            if (_mode == 1 && _tiled && _pipe != null && _pipe.Running)
            {
                _pipe.Fail(reason);
                Program.Log("synergy failed: " + reason);
            }
        }

        private void PollTick(object sender, EventArgs e)
        {
            if (_pendingIdx < 0) { _poll.Stop(); return; }

            // 任何输入框正聚焦打字（含协同面板就地输入）：暂缓接管与超时判定，抢前台会把中文输入法候选窗打断
            if (InputUiBusy.Busy()) { _deadline = _deadline.AddMilliseconds(_poll.Interval); return; }

            IntPtr h = Host.Find(_agents[_pendingIdx], _launchStamp);
            if (h != IntPtr.Zero) { _poll.Stop(); FinishStart(_pendingIdx, h); return; }

            if (DateTime.Now > _deadline)
            {
                _poll.Stop();
                int i = _pendingIdx;
                _pendingIdx = -1;
                _chosen.Remove(i);
                MarkSplash(_agents[i].Id, "超时", 3);

                Toast.Show("「" + _agents[i].Name + "」启动超时");
                Program.Log("TIMEOUT " + _agents[i].Id);
                FailSynergy("「" + _agents[i].Name + "」启动超时，未找到它的窗口");
                Invalidate();
            }
        }

        private void FinishStart(int i, IntPtr hwnd)
        {
            _pendingIdx = -1;
            IntPtr old = ActiveHandle;

            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            Program.Log("found " + _agents[i].Id + " hwnd=" + hwnd + " pid=" + pid +
                        " title='" + Native.Title(hwnd) + "' cls=" + Native.ClassName(hwnd));

            // 协同模式：不抢主视图，交给 TileTick 摆到自己的栏里
            if (_tiled)
            {
                // 平铺模式的缩略图注册的就是这个句柄，选错窗口三栏就"抓的不是屏幕上那个"。
                // Find 允许不可见的后备窗口兜底，这里必须等到可见的那个。
                if (!Native.IsWindowVisible(hwnd))
                {
                    IntPtr better = Host.FindVisible(_agents[i], DateTime.MinValue);
                    if (better != IntPtr.Zero) { hwnd = better; Native.GetWindowThreadProcessId(hwnd, out pid); }
                }
                Host.Register((int)pid, OwnedNow(i, (int)pid));   // 我启动的、且不是我点之前就存在的，才算我方
            _pids[i] = (int)pid;
                _windows[i] = hwnd;
                _shown = -1;
                ApplyBounds();
                for (int k = 0; k < _roles.Count; k++) if (_roles[k] == i) ParkOffscreen(k, true);
                // 会话进行中：窗口（重新）就绪 → 当前步提示词重新就位，防止用户不知道从哪继续。
                // 幂等：只是把已有提示词再放进剪贴板，不产生任何重复副作用
                if (_pipe != null && _pipe.Running && !_pipe.ConfirmPending)
                {
                    string prompt = _pipe.CurrentPrompt();
                    if (prompt.Length > 0)
                    {
                        PipeCopy(prompt);
                        Toast.Show("「" + _agents[i].Name + "」窗口就绪，提示词已在剪贴板");
                    }
                }
                SaveSessionNow();
                Invalidate();
                return;
            }

            if (!Host.SelfElevated() && Host.LooksElevated((int)pid))
            {
                _chosen.Remove(i);

                MarkSplash(_agents[i].Id, "失败", 3);
                Toast.Show("「" + _agents[i].Name + "」以管理员权限运行，无法接管");
                FailSynergy("「" + _agents[i].Name + "」以管理员权限运行，无法接管窗口");
                Invalidate();
                return;
            }

            // 先把自己缩成侧栏，否则铺满的窗口会盖住刚要显示的应用
            Host.Register((int)pid, OwnedNow(i, (int)pid));   // 我启动的、且不是我点之前就存在的，才算我方
            _pids[i] = (int)pid;
            _windows[i] = hwnd;
            _shown = i;
            MarkSplash(_agents[i].Id, "就绪", 2);
            MoveToFront(i);
            ApplyBounds();

            if (!Host.Apply(hwnd, Handle, ContentRect(), _agents[i].OffsetFrame))
            {
                _chosen.Remove(i);
                _pids.Remove(i);
            _launchedByMe.Remove(i);   // 进程没了，归属标记必须一起清，否则下次你手动开同一个应用会被误判为我方
                _windows.Remove(i);
                _shown = old == IntPtr.Zero ? -1 : IndexOfWindow(old);
                ApplyBounds();
                Toast.Show("「" + _agents[i].Name + "」的窗口拒绝被接管");
                Invalidate();
                return;
            }

            SaveSessionNow();
            _expandT = 0f;
            _popT = 0f; _popIdx = i;   // 启动就绪：卡片弹出回弹
            Kick();

            if (old != IntPtr.Zero && old != hwnd) Host.Hide(old);
            Host.ForceForeground(hwnd);
            Invalidate();
        }

        private void Cancel(int i)
        {
            if (!_chosen.Contains(i)) return;

            IntPtr h;
            if (_windows.TryGetValue(i, out h) && h != IntPtr.Zero && i == _shown)
            {
                Host.Hide(h);
                _shown = -1;
            }
            int pid;
            if (_pids.TryGetValue(i, out pid)) { if (Host.IsOwned(pid)) Host.KillTree(pid); else Host.Forget(pid); }

            _chosen.Remove(i);
            _windows.Remove(i);
            _pids.Remove(i);
            _launchedByMe.Remove(i);   // 进程没了，归属标记必须一起清，否则下次你手动开同一个应用会被误判为我方
            if (_pendingIdx == i) { _pendingIdx = -1; _poll.Stop(); }

            Program.Log("cancel " + _agents[i].Id);
            ApplyBounds();
            SaveSessionNow();
            Invalidate();
        }

        private void HealthTick(object sender, EventArgs e)
        {
            // 输入框不在焦点上时恢复 NOACTIVATE，避免之后点任何地方都抢焦点
            if (!AnyTextFocused(this) && _synPanel == null) Native.SetNoActivate(Handle, true);   // 协同面板在时保持可激活
            if (_chosen.Count == 0) return;
            foreach (int i in new List<int>(_chosen))
            {
                int pid;
                if (!_pids.TryGetValue(i, out pid)) continue;

                bool proc;
                try { using (Process p = Process.GetProcessById(pid)) proc = !p.HasExited; }
                catch { proc = false; }

                IntPtr hh;
                // 只有"当前显示"的应用才看窗口是否还在；其他应用是我们自己隐藏的
                bool win = true;
                if (i == _shown)
                    win = _windows.TryGetValue(i, out hh) && hh != IntPtr.Zero && Native.IsWindow(hh);

                if (proc && win)
                {
                    _dead[i] = 0;
                    continue;
                }

                int miss;
                _dead.TryGetValue(i, out miss);
                miss++;
                _dead[i] = miss;
                if (miss < 2) continue;

                if (proc) Host.KillTree(pid);
                Program.Log("drop " + _agents[i].Id + (proc ? " window-gone" : " process-gone"));

                _chosen.Remove(i);
                _windows.Remove(i);
                _pids.Remove(i);
            _launchedByMe.Remove(i);   // 进程没了，归属标记必须一起清，否则下次你手动开同一个应用会被误判为我方
                _dead.Remove(i);
                if (_shown == i) _shown = -1;
                ApplyBounds();
                Toast.Show("「" + _agents[i].Name + "」已关闭，名额已释放");
                SaveSessionNow();
                Invalidate();
            }
        }

        // ---------- 生命周期 ----------

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _poll.Stop(); _health.Stop(); _anim.Stop(); _restoreTimer.Stop();
            SaveSessionNow();

            // 未结束的协同会话：结束并落盘，记录不丢
            EndSessionIfAny();

            // 两段式结束：只处理本应用启动的进程（用户自己开的不动）；先请求正常退出，1.5 秒后强杀，总时长有上限
            int killed = Host.KillOwned(1500);
            if (killed > 0) Program.Log("exit: force-killed " + killed);

            IntPtr h;
            if (_shown >= 0 && _windows.TryGetValue(_shown, out h)) Host.Hide(h);
            PipeReturnButton.Hide();
            UnregisterThumbs();
            Host.ReleaseAll();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // 自绘用的字体/画笔/画刷不在 components 容器里，不会自动释放
                if (_fName != null) _fName.Dispose();
                if (_bSide != null) _bSide.Dispose();
                if (_bOrange != null) _bOrange.Dispose();
                if (_pLine != null) _pLine.Dispose();
                if (_pOrange != null) _pOrange.Dispose();
                if (_pRing != null) _pRing.Dispose();
                if (_starBmp != null) { _starBmp.Dispose(); _starBmp = null; }
                if (_noiseBmp != null) { _noiseBmp.Dispose(); _noiseBmp = null; }
                if (_fLogoBig != null) { _fLogoBig.Dispose(); _fLogoBig = null; }
                if (_fBrandSub != null) { _fBrandSub.Dispose(); _fBrandSub = null; }
                if (_fModeTitle != null) { _fModeTitle.Dispose(); _fModeTitle = null; }
                if (_fCardTitle != null) { _fCardTitle.Dispose(); _fCardTitle = null; }
                if (_fCardSub != null) { _fCardSub.Dispose(); _fCardSub = null; }
                if (_cardBmpA != null) { _cardBmpA.Dispose(); _cardBmpA = null; }
                if (_cardBmpB != null) { _cardBmpB.Dispose(); _cardBmpB = null; }
                if (_haloBrush != null) { _haloBrush.Dispose(); _haloBrush = null; }
                if (_ambientBrush != null) { _ambientBrush.Dispose(); _ambientBrush = null; }
                if (_modeHaloBrush != null) { _modeHaloBrush.Dispose(); _modeHaloBrush = null; }
                if (_beamBrush != null) { _beamBrush.Dispose(); _beamBrush = null; }
                if (_slabBrush != null) { _slabBrush.Dispose(); _slabBrush = null; }
                if (_slabHiBrush != null) { _slabHiBrush.Dispose(); _slabHiBrush = null; }
                if (_slabShBrush != null) { _slabShBrush.Dispose(); _slabShBrush = null; }
                if (_slabTexBrush != null) { _slabTexBrush.Dispose(); _slabTexBrush = null; }
                if (_slabPen != null) { _slabPen.Dispose(); _slabPen = null; }
                if (_pillHaloBrush != null) { _pillHaloBrush.Dispose(); _pillHaloBrush = null; }
                if (_bootBmp != null) { _bootBmp.Dispose(); _bootBmp = null; }
                if (_bgBmp != null) { _bgBmp.Dispose(); _bgBmp = null; }
                _poll.Dispose(); _health.Dispose(); _anim.Dispose();
                _hideTimer.Dispose(); _restoreTimer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
