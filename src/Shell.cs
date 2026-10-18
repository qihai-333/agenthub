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
        private const int HotkeyBase = 100;
        private const int HotkeyToggle = 200;

        private static readonly Color Bg = Color.FromArgb(11, 11, 13);
        private static readonly Color SideBg = Color.FromArgb(8, 8, 10);
        private static readonly Color BgHover = Color.FromArgb(24, 24, 28);
        private static readonly Color Line = Color.FromArgb(28, 28, 33);
        private static readonly Color Orange = Color.FromArgb(255, 122, 26);
        private static readonly Color OrangeDim = Color.FromArgb(122, 58, 14);
        private static readonly Color TextMain = Color.FromArgb(234, 234, 238);
        private static readonly Color TextDim = Color.FromArgb(138, 138, 148);
        private static readonly Color TextFaint = Color.FromArgb(74, 74, 82);

        private readonly List<Agent> _agents;
        private readonly float _s;

        private int _sideW;
        private bool _expanded = true;

        private readonly HashSet<int> _chosen = new HashSet<int>();
        private int _shown = -1;
        private readonly Dictionary<int, IntPtr> _windows = new Dictionary<int, IntPtr>();
        private readonly Dictionary<int, int> _pids = new Dictionary<int, int>();
        private readonly Dictionary<int, int> _dead = new Dictionary<int, int>();

        private int _hover = -1;
        private bool _hoverCollapse;
        private bool _hoverExit;
        private bool _hoverStrip;
        private bool _plusHot;

        private int _pendingIdx = -1;
        private DateTime _deadline;
        private DateTime _launchStamp;

        private readonly Timer _poll = new Timer();
        private readonly Timer _health = new Timer();
        private readonly Timer _anim = new Timer();

        private float _introT;
        private float _hoverT = 1f;
        private float _pressT;
        private int _pressIdx = -1;
        private float _collapseT = 1f;
        private bool _collapseMoving;
        private float _expandT = 1f;
        private float _spin;

        private readonly List<string> _restore = new List<string>();
        private DateTime _restoreAt;
        private readonly Timer _restoreTimer = new Timer();

        private readonly Font _fIcon;
        private readonly Font _fName;
        private readonly SolidBrush _bSide;
        private readonly SolidBrush _bOrange;
        private readonly Pen _pLine;
        private readonly Pen _pOrange;
        private readonly Pen _pRing;
        private readonly Pen _pFaint;

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

            _fIcon = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold);
            _fName = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);

            _bSide = new SolidBrush(SideBg);
            _bOrange = new SolidBrush(Orange);
            _pLine = new Pen(Line);
            _pOrange = new Pen(Orange, Math.Max(1.5f, Px(2) * 0.9f));
            _pRing = new Pen(OrangeDim, Math.Max(1.6f, Px(2) * 0.8f));
            _pFaint = new Pen(TextFaint, Math.Max(1.4f, Px(2) * 0.8f));

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
            _anim.Interval = 16;
            _anim.Tick += AnimTick;
            _anim.Start();

            string shownId;
            List<string> ids;
            Config.LoadSession(out ids, out shownId);
            foreach (string id in ids) _restore.Add(id);
            if (!string.IsNullOrEmpty(shownId))
            {
                _restore.Remove(shownId);
                _restore.Insert(0, shownId);
            }
            _restoreTimer.Interval = 500;
            _restoreTimer.Tick += RestoreTick;
            if (_restore.Count > 0)
            {
                _restoreTimer.Start();
                Program.Log("restore queue: " + string.Join(",", _restore.ToArray()));
            }

            Program.Log("dock start sideW=" + _sideW + " agents=" + _agents.Count);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyBounds();
            Program.Log("dock bounds " + Bounds.X + "," + Bounds.Y + " " + Bounds.Width + "x" + Bounds.Height + " dpi=" + DeviceDpi);
        }

        private int Px(int v) { return (int)Math.Round(v * _s); }
        private int SideExpandedPx { get { return Px(92); } }
        private int SideCollapsedPx { get { return Px(6); } }
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
            return new Rectangle(scr.X + _sideW, scr.Y, Math.Max(1, scr.Width - _sideW), scr.Height);
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

        private void Kick() { if (!_anim.Enabled) _anim.Start(); }

        private void AnimTick(object sender, EventArgs e)
        {
            bool active = false;

            if (_introT < 1f) { _introT = Approach(_introT, 1f, 0.06f); active = true; }
            if (_hoverT < 1f) { _hoverT = Approach(_hoverT, 1f, 0.22f); active = true; }
            if (_pressT > 0f) { _pressT = Approach(_pressT, 0f, 0.30f); active = true; }
            if (_expandT < 1f) { _expandT = Approach(_expandT, 1f, 0.20f); active = true; }
            if (_pendingIdx >= 0) { _spin = (_spin + 12) % 360; active = true; }

            float ct = _expanded ? 1f : 0f;
            if (Math.Abs(_collapseT - ct) > 0.001f)
            {
                _collapseT = Approach(_collapseT, ct, 0.24f);
                _collapseMoving = true;
                _sideW = Lerp(SideCollapsedPx, SideExpandedPx, EaseOut(_collapseT));
                ApplyBounds();
                active = true;
            }
            else if (_collapseMoving)
            {
                _collapseMoving = false;
                _sideW = _expanded ? SideExpandedPx : SideCollapsedPx;
                ApplyBounds();
                RepositionShown();
                active = true;
            }

            if (active) Invalidate();
            else _anim.Stop();
        }

        private void ApplyBounds()
        {
            Rectangle scr = Screen.PrimaryScreen.WorkingArea;
            int w = Math.Max(SideCollapsedPx, _sideW);
            if (Bounds.Width != w || Bounds.X != scr.X || Bounds.Y != scr.Y || Bounds.Height != scr.Height)
                Bounds = new Rectangle(scr.X, scr.Y, w, scr.Height);
        }

        // ---------- 绘制 ----------

        private Rectangle ItemRect(int i)
        {
            return new Rectangle(ItemXPx, ItemTopPx + i * ItemStepPx, ItemSizePx, ItemSizePx);
        }

        private Rectangle ShownPillRect(int i)
        {
            Rectangle baseR = ItemRect(i);
            int full = _sideW - Px(8);
            int w = Lerp(baseR.Width, Math.Max(baseR.Width, full), EaseOut(_expandT));
            return new Rectangle(Px(4), baseR.Y, w, baseR.Height);
        }

        private Rectangle CollapseRect() { return new Rectangle(ItemXPx, Height - Px(68), ItemSizePx, ItemSizePx); }
        private Rectangle ExitRect() { return new Rectangle(ItemXPx, Height - Px(124), ItemSizePx, ItemSizePx); }

        private static GraphicsPath Round(Rectangle r, int rad)
        {
            int d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(SideBg);

            if (_sideW <= SideCollapsedPx + 1)
            {
                using (var b = new SolidBrush(_hoverStrip ? Orange : Color.FromArgb(40, 40, 46)))
                    g.FillRectangle(b, Px(2), 0, Math.Max(2, Px(2)), Height);
                return;
            }

            g.FillRectangle(_bSide, 0, 0, _sideW, Height);
            g.DrawLine(_pLine, _sideW - 1, 0, _sideW - 1, Height);

            if (_shown >= 0 && _shown < _agents.Count)
            {
                Rectangle pr = _expandT > 0f ? ShownPillRect(_shown) : ItemRect(_shown);
                using (GraphicsPath p = Round(pr, Px(12)))
                    g.FillPath(_bOrange, p);
            }

            if (_introT <= 0f) return;

            bool interactive = _introT > 0.5f;

            for (int i = 0; i < _agents.Count; i++)
            {
                float ap = EaseOut(Clamp01((_introT - i * 0.06f) / 0.30f));
                if (ap <= 0f) continue;

                Rectangle r = ItemRect(i);
                int dy = (int)Math.Round((1f - ap) * Px(10));

                bool isShown = i == _shown;
                bool isChosen = _chosen.Contains(i);

                if (!isShown && i == _hover)
                {
                    Rectangle hr = r;
                    if (_pressIdx == i && _pressT > 0f)
                    {
                        int sh = (int)Math.Round(4 * _pressT * _s);
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
                        int sh = (int)Math.Round(3 * _pressT * _s);
                        cr = new Rectangle(cr.X + sh, cr.Y + sh, cr.Width - sh * 2, cr.Height - sh * 2);
                    }
                    using (GraphicsPath p = Round(cr, Px(12)))
                    using (var pen = new Pen(Blend(SideBg, Orange, ap), Math.Max(1.6f, Px(2) * 0.9f)))
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
                    if (b2 != null && pr.Width > Px(46)) g.DrawImage(b2, ir2);

                    if (pr.Width > Px(46))
                    {
                        Rectangle tr = new Rectangle(pr.X + Px(30), pr.Y, pr.Width - Px(34), pr.Height);
                        TextRenderer.DrawText(g, _agents[i].Name, _fName, tr, Bg,
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
                        g.DrawArc(_pRing, rr, 0, 360);
                        g.DrawArc(_pOrange, rr, _spin, 100);
                    }
                }
            }

            if (interactive && _agents.Count < 18) DrawPlus(g);
            if (interactive) DrawExit(g, ExitRect(), _hoverExit);
        }

        private void DrawTile(Graphics g, Agent a, Rectangle r)
        {
            int sz = Px(36);
            Rectangle ir = new Rectangle(r.X + (r.Width - sz) / 2, r.Y + (r.Height - sz) / 2, sz, sz);
            Bitmap bmp = Icons.Get(a);
            if (bmp == null) bmp = Icons.Letter(a.Name, 48);
            if (bmp == null) return;

            if (Icons.IsDark(bmp, a.Id))
            {
                using (GraphicsPath p = Round(new Rectangle(ir.X - 2, ir.Y - 2, ir.Width + 4, ir.Height + 4), Px(9)))
                using (var b = new SolidBrush(Color.FromArgb(40, 40, 48)))
                    g.FillPath(b, p);
            }

            InterpolationMode old = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(bmp, ir);
            g.InterpolationMode = old;
        }

        private Rectangle PlusRect()
        {
            return new Rectangle(ItemXPx, ItemTopPx + _agents.Count * ItemStepPx, ItemSizePx, ItemSizePx);
        }

        private void DrawPlus(Graphics g)
        {
            Rectangle r = PlusRect();
            bool hot = _plusHot;
            using (var pen = new Pen(hot ? Orange : Color.FromArgb(52, 52, 60), Math.Max(1.2f, Px(2) * 0.7f)))
            {
                pen.DashStyle = DashStyle.Dash;
                using (GraphicsPath p = Round(r, Px(12)))
                    g.DrawPath(pen, p);
            }
            using (var pen = new Pen(hot ? Orange : TextFaint, Math.Max(1.6f, Px(2) * 0.9f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, a = Px(7);
                g.DrawLine(pen, cx - a, cy, cx + a, cy);
                g.DrawLine(pen, cx, cy - a, cx, cy + a);
            }
        }

        private void DrawChevron(Graphics g, Rectangle r, bool hot)
        {
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, a = Px(6);
            using (var pen = new Pen(hot ? Orange : TextFaint, Math.Max(1.5f, Px(2) * 0.9f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawLine(pen, cx + a / 2, cy - a, cx - a / 2, cy);
                g.DrawLine(pen, cx - a / 2, cy, cx + a / 2, cy + a);
            }
        }

        private void DrawExit(Graphics g, Rectangle r, bool hot)
        {
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, a = Px(6);
            using (var pen = new Pen(hot ? Orange : TextFaint, Math.Max(1.4f, Px(2) * 0.8f)))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawArc(pen, cx - a, cy - a, a * 2, a * 2, -60, 300);
                g.DrawLine(pen, cx, cy - a - Px(3), cx, cy - Px(1));
            }
        }

        // ---------- 交互 ----------

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            for (int i = 0; i < _agents.Count && i < 9; i++)
                Native.RegisterHotKey(Handle, HotkeyBase + i, Native.MOD_ALT | Native.MOD_NOREPEAT, 0x31 + i);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            for (int i = 0; i < 9; i++) Native.UnregisterHotKey(Handle, HotkeyBase + i);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
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

            if (!_expanded)
            {
                bool hot = e.X <= Px(10);
                if (hot != _hoverStrip) { _hoverStrip = hot; Invalidate(); }
                if (hot) SetExpanded(true);
                return;
            }

            int idx = HitTest(e.Location);
            bool col = CollapseRect().Contains(e.Location);
            bool ex = ExitRect().Contains(e.Location);
            bool ph = _agents.Count < 18 && PlusRect().Contains(e.Location);
            if (idx != _hover || col != _hoverCollapse || ex != _hoverExit || ph != _plusHot)
            {
                if (idx != _hover) { _hoverT = 0f; Kick(); }
                _hover = idx;
                _hoverCollapse = col;
                _hoverExit = ex;
                _plusHot = ph;
                Cursor = (idx >= 0 || col || ex || ph) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1; _hoverCollapse = false; _hoverExit = false; _hoverStrip = false;
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
                    acts.Add(delegate { Cancel(k); });
                }
                if (_agents[ri].Custom)
                {
                    int k = ri;
                    labels.Add("编辑");
                    acts.Add(delegate { ShowEdit(k); });
                    labels.Add("移除");
                    acts.Add(delegate { RemoveAgent(k); });
                }
                if (labels.Count == 0) return;

                MiniMenu.ShowAt(PointToScreen(e.Location), labels.ToArray(), acts.ToArray());
                return;
            }

            if (e.Button != MouseButtons.Left) return;
            if (!_expanded) { SetExpanded(true); return; }
            if (ExitRect().Contains(e.Location)) { Close(); return; }
            if (CollapseRect().Contains(e.Location)) { SetExpanded(false); return; }
            if (_agents.Count < 18 && PlusRect().Contains(e.Location)) { ShowAdd(); return; }

            int idx2 = HitTest(e.Location);
            if (idx2 >= 0) { _pressIdx = idx2; _pressT = 1f; Kick(); LeftClick(idx2); }
        }

        private int HitTest(Point p)
        {
            if (p.X < Px(4) || p.X > _sideW) return -1;
            for (int i = 0; i < _agents.Count; i++) if (ItemRect(i).Contains(p)) return i;
            return -1;
        }

        public void SetExpanded(bool value)
        {
            // 侧栏固定展开：不再收起，右侧应用区域也就不会跟着移动
        }

        // ---------- 名额与切换 ----------

        public void Select(int idx) { LeftClick(idx); }

        // ---------- 会话与自定义 ----------

        private void SaveSessionNow()
        {
            var ids = new List<string>();
            foreach (int i in _chosen) if (i >= 0 && i < _agents.Count) ids.Add(_agents[i].Id);
            Config.SaveSession(ids, (_shown >= 0 && _shown < _agents.Count) ? _agents[_shown].Id : null);
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
            StartAgent(idx);
            Invalidate();
        }

        private void ShowAdd()
        {
            if (_agents.Count >= 18) { Toast.Show("最多 18 个应用"); return; }
            Agent a = AgentForm.Show(this, null);
            if (a == null) return;
            _agents.Add(a);
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
            Config.Custom.RemoveAll(delegate(Agent x) { return x.Id == gone.Id; });
            Config.SaveCustom();
            Icons.Forget(gone.Id);
            RemapAfterRemove(i);
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
            _expandT = 0f;
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

        private void StartAgent(int i)
        {
            Agent a = _agents[i];
            Program.Log("start " + a.Id);
            _pendingIdx = i;
            _launchStamp = DateTime.Now;
            _deadline = DateTime.Now.AddSeconds(45);
            Invalidate();

            IntPtr h = Host.Find(a, DateTime.MinValue);
            if (h != IntPtr.Zero) { FinishStart(i, h); return; }

            try { Host.Launch(a); }
            catch (Exception ex)
            {
                Program.Log("launch failed: " + ex.Message);
                _pendingIdx = -1;
                _chosen.Remove(i);
                Toast.Show("「" + a.Name + "」启动失败");
                Invalidate();
                return;
            }
            _poll.Start();
        }

        private void PollTick(object sender, EventArgs e)
        {
            if (_pendingIdx < 0) { _poll.Stop(); return; }

            IntPtr h = Host.Find(_agents[_pendingIdx], _launchStamp);
            if (h != IntPtr.Zero) { _poll.Stop(); FinishStart(_pendingIdx, h); return; }

            if (DateTime.Now > _deadline)
            {
                _poll.Stop();
                int i = _pendingIdx;
                _pendingIdx = -1;
                _chosen.Remove(i);
                Toast.Show("「" + _agents[i].Name + "」启动超时");
                Program.Log("TIMEOUT " + _agents[i].Id);
                Invalidate();
            }
        }

        private void FinishStart(int i, IntPtr hwnd)
        {
            _pendingIdx = -1;
            IntPtr old = ActiveHandle;

            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);

            if (!Host.SelfElevated() && Host.LooksElevated((int)pid))
            {
                _chosen.Remove(i);
                Toast.Show("「" + _agents[i].Name + "」以管理员权限运行，无法接管");
                Invalidate();
                return;
            }

            if (!Host.Apply(hwnd, Handle, ContentRect(), _agents[i].OffsetFrame))
            {
                _chosen.Remove(i);
                Toast.Show("「" + _agents[i].Name + "」的窗口拒绝被接管");
                Invalidate();
                return;
            }

            _pids[i] = (int)pid;
            _windows[i] = hwnd;
            _shown = i;
            SaveSessionNow();
            _expandT = 0f;
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
            if (_pids.TryGetValue(i, out pid)) Host.KillTree(pid);

            _chosen.Remove(i);
            _windows.Remove(i);
            _pids.Remove(i);
            if (_pendingIdx == i) { _pendingIdx = -1; _poll.Stop(); }

            Program.Log("cancel " + _agents[i].Id);
            SaveSessionNow();
            Invalidate();
        }

        private void HealthTick(object sender, EventArgs e)
        {
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
                _dead.Remove(i);
                if (_shown == i) _shown = -1;
                Toast.Show("「" + _agents[i].Name + "」已关闭，名额已释放");
                SaveSessionNow();
                Invalidate();
            }
        }

        private void RepositionShown()
        {
            IntPtr h;
            if (_shown < 0 || !_windows.TryGetValue(_shown, out h) || !Native.IsWindow(h)) return;
            Rectangle r = ContentRect();
            Native.SetWindowPos(h, Handle, r.X, r.Y, r.Width, r.Height,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        // ---------- 生命周期 ----------

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _poll.Stop(); _health.Stop(); _anim.Stop(); _restoreTimer.Stop();
            SaveSessionNow();

            foreach (int i in new List<int>(_chosen))
            {
                if (i == _shown) continue;
                int pid;
                if (_pids.TryGetValue(i, out pid))
                {
                    Program.Log("exit kill " + _agents[i].Id);
                    Host.KillTree(pid);
                }
            }

            IntPtr h;
            if (_shown >= 0 && _windows.TryGetValue(_shown, out h)) Host.Hide(h);
            Host.ReleaseAll();
            base.OnFormClosing(e);
        }
    }
}
