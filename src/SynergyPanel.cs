using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AgentHub
{
    // =====================================================================
    // 协同模式主界面（改造版）
    //
    // 原则：
    // - 全部使用程序自有控件展示输入/输出/状态，不嵌入、不截取、不缩放 AI 软件窗口。
    // - 通信沿用项目已有的「剪贴板接力」机制（Agent 无 API，这是现有硬限制）：
    //   发送 = 把已组好的提示词快照写入剪贴板并聚焦对应 Agent 窗口（可选自动 Ctrl+V）；
    //   接收 = 监听剪贴板变化，且仅当「前台窗口是当前 Agent」时才自动捕获，
    //          防止用户在本程序里复制文字被误当作回复；另有「以剪贴板为回复」手动兜底。
    // - 本控件自包含：自行加载 agents.json、自行做窗口查找/聚焦、自行监听剪贴板。
    //   对 Shell.cs 只要求：进入协同模式时 new SynergyPanel(PipeConfig.Load()) 并 Dock=Fill。
    // =====================================================================
    public class SynergyPanel : UserControl
    {
        // ---------------- agents.json（宽容解析：字典或数组两种形态都接受） ----------------

        class AgentJson
        {
            public string Id;
            public string Name;
            public string Exe;
            public string Proc;
            public string Title;
        }

        internal class AgentEntry
        {
            public string Id = "";
            public string Name = "";
            public string Exe = "";
            public string Proc = "";
            public string Title = "";
            public override string ToString() { return (Id.Length > 0 ? Id : "?") + " · " + (Name.Length > 0 ? Name : "未命名"); }
        }

        static List<AgentEntry> LoadAgents()
        {
            var list = new List<AgentEntry>();
            try
            {
                string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agents.json");
                if (!File.Exists(p)) return list;
                string json = File.ReadAllText(p, Encoding.UTF8);
                var ser = new JavaScriptSerializer();

                // 形态一：{ "A1": {...}, "A2": {...} }
                try
                {
                    var dict = ser.Deserialize<Dictionary<string, AgentJson>>(json);
                    if (dict != null && dict.Count > 0)
                    {
                        foreach (var kv in dict)
                        {
                            var a = kv.Value ?? new AgentJson();
                            list.Add(new AgentEntry
                            {
                                Id = string.IsNullOrEmpty(a.Id) ? kv.Key : a.Id,
                                Name = string.IsNullOrEmpty(a.Name) ? kv.Key : a.Name,
                                Exe = a.Exe ?? "",
                                Proc = a.Proc ?? "",
                                Title = a.Title ?? ""
                            });
                        }
                        if (list.Count > 0) return list;
                    }
                }
                catch { }

                // 形态二：[ { "id": "A1", ... }, ... ]
                try
                {
                    var arr = ser.Deserialize<List<AgentJson>>(json);
                    if (arr != null)
                    {
                        foreach (var a in arr)
                        {
                            if (a == null) continue;
                            list.Add(new AgentEntry
                            {
                                Id = a.Id ?? "",
                                Name = string.IsNullOrEmpty(a.Name) ? (a.Id ?? "") : a.Name,
                                Exe = a.Exe ?? "",
                                Proc = a.Proc ?? "",
                                Title = a.Title ?? ""
                            });
                        }
                    }
                }
                catch { }
            }
            catch { }
            return list;
        }

        // ---------------- 窗口查找 / 聚焦 / 剪贴板监听（自包含 P/Invoke） ----------------

        static class Win32
        {
            public const int WM_CLIPBOARDUPDATE = 0x031D;

            [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
            [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
            [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
            [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
            [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hwnd);
            [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
            [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
            [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
            [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
            [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

            public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
            [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

            public static string WindowTitle(IntPtr hwnd)
            {
                var sb = new StringBuilder(512);
                GetWindowText(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }

            public static string ProcName(string s)
            {
                if (string.IsNullOrEmpty(s)) return "";
                try { return Path.GetFileNameWithoutExtension(s) ?? s; } catch { return s; }
            }

            // 按进程名优先、标题兜底找窗口
            public static IntPtr FindAgentWindow(AgentEntry a)
            {
                if (a == null) return IntPtr.Zero;
                if (!string.IsNullOrEmpty(a.Proc))
                {
                    IntPtr h = FindByProcess(ProcName(a.Proc));
                    if (h != IntPtr.Zero) return h;
                }
                if (!string.IsNullOrEmpty(a.Exe))
                {
                    IntPtr h = FindByProcess(ProcName(a.Exe));
                    if (h != IntPtr.Zero) return h;
                }
                if (!string.IsNullOrEmpty(a.Title))
                {
                    IntPtr found = IntPtr.Zero;
                    string want = a.Title;
                    EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
                    {
                        if (!IsWindowVisible(hwnd)) return true;
                        string t = WindowTitle(hwnd);
                        if (t.Length > 0 && t.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            found = hwnd;
                            return false;
                        }
                        return true;
                    }, IntPtr.Zero);
                    return found;
                }
                return IntPtr.Zero;
            }

            static IntPtr FindByProcess(string procName)
            {
                if (string.IsNullOrEmpty(procName)) return IntPtr.Zero;
                try
                {
                    foreach (var p in Process.GetProcessesByName(procName))
                    {
                        try { if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle; } catch { }
                    }
                }
                catch { }
                return IntPtr.Zero;
            }

            public static void FocusWindow(IntPtr hwnd)
            {
                if (hwnd == IntPtr.Zero) return;
                try
                {
                    IntPtr fg = GetForegroundWindow();
                    uint tmp1, tmp2;
                    uint fgThread = GetWindowThreadProcessId(fg, out tmp1);
                    uint myThread = GetCurrentThreadId();
                    uint targetThread = GetWindowThreadProcessId(hwnd, out tmp2);
                    AttachThreadInput(myThread, targetThread, true);
                    if (fgThread != 0) AttachThreadInput(fgThread, targetThread, true);
                    ShowWindow(hwnd, 9); // SW_RESTORE
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                    AttachThreadInput(myThread, targetThread, false);
                    if (fgThread != 0) AttachThreadInput(fgThread, targetThread, false);
                }
                catch { }
            }
        }

        // ---------------- 成员 ----------------

        readonly Pipeline _pipe;
        readonly List<AgentEntry> _agents;

        readonly Timer _pump;                 // 200ms：取走待投递提示词、聚焦/自动粘贴、查超时、刷新状态
        bool _suppressClipboard;              // 我们自己写剪贴板后的回声抑制
        string _lastDelivered = "";
        PipePhase _lastPhase = PipePhase.Idle;

        class FocusJob { public AgentEntry Agent; public DateTime Until; public bool Pasted; }
        FocusJob _focusJob;

        readonly WorkspacePanel _ws1;   // Agent1：需求拆解 / 最终对齐（同一工作区两段，可切换查看）
        readonly WorkspacePanel _ws2;   // Agent2：任务执行
        readonly WorkspacePanel _ws3;   // Agent3：完成自检

        readonly Label _lbRound, _lbDone, _lbPhase, _lbState, _lbQueue, _lbNote, _lbModeNow;
        readonly ListBox _lstHistory;
        readonly TextBox _txtSupplement;
        readonly Button _btnAddSupp, _btnContinue, _btnSuppContinue, _btnEnd, _btnStopResume, _btnRetry;
        readonly RadioButton _rbManual, _rbAuto;
        readonly CheckBox _ckAutoPaste;
        bool _syncingRadios;

        // ================= 构造与布局 =================

        public SynergyPanel(PipeConfig cfg)
        {
            _pipe = new Pipeline(cfg ?? PipeConfig.Load());
            _agents = LoadAgents();
            DoubleBuffered = true;
            BackColor = Color.FromArgb(245, 246, 248);

            // ---- 根布局：左（工作区，填充） | 右（状态栏，固定 280px）----
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = new Padding(6)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            Controls.Add(root);

            // ---- 左：三个工作区，整体滚动（窗口变小时不压扁输入框）----
            var scrollHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
            var wsTable = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 1,
                RowCount = 3,
                Margin = Padding.Empty
            };
            wsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 3; i++) wsTable.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33f));

            _ws1 = new WorkspacePanel(this, "Agent 1 · 需求拆解 / 最终对齐", 0, true);
            _ws2 = new WorkspacePanel(this, "Agent 2 · 任务执行（编辑结果只改交接内容；需改产物请「重新生成」）", 1, false);
            _ws3 = new WorkspacePanel(this, "Agent 3 · 完成自检", 2, false);
            wsTable.Controls.Add(_ws1, 0, 0);
            wsTable.Controls.Add(_ws2, 0, 1);
            wsTable.Controls.Add(_ws3, 0, 2);
            scrollHost.Controls.Add(wsTable);
            scrollHost.Resize += delegate
            {
                int min = 3 * 170;
                wsTable.Height = Math.Max(scrollHost.ClientSize.Height - 2, min);
            };
            root.Controls.Add(scrollHost, 0, 0);

            // ---- 右：状态栏（上信息 / 中历史+补充 / 底审批模式固定）----
            var side = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Margin = Padding.Empty,
                Padding = new Padding(8, 0, 0, 0)
            };
            side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 状态信息
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 55f));  // 历史轮次
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 45f));  // 补充队列
            side.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // 审批模式（固定底部）
            root.Controls.Add(side, 1, 0);

            var info = new GroupBox { Text = "协同状态", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 4, 8, 8) };
            var infoT = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            infoT.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            infoT.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _lbRound = InfoRow(infoT, "当前轮次");
            _lbDone = InfoRow(infoT, "已完成轮数");
            _lbPhase = InfoRow(infoT, "当前执行阶段");
            _lbState = InfoRow(infoT, "当前状态");
            _lbQueue = InfoRow(infoT, "补充队列");
            _lbNote = new Label
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                ForeColor = Color.FromArgb(90, 90, 90),
                MaximumSize = new Size(250, 0),
                Text = ""
            };
            info.Controls.Add(infoT);
            info.Controls.Add(_lbNote);
            side.Controls.Add(info, 0, 0);

            var gbHis = new GroupBox { Text = "历史轮次（双击查看明细）", Dock = DockStyle.Fill };
            _lstHistory = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            _lstHistory.DoubleClick += delegate { ShowRoundDetail(); };
            gbHis.Controls.Add(_lstHistory);
            side.Controls.Add(gbHis, 0, 1);

            var gbSupp = new GroupBox { Text = "补充需求（排队，下一次交接生效）", Dock = DockStyle.Fill };
            var suppT = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            suppT.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            suppT.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _txtSupplement = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical };
            _btnAddSupp = new Button { Text = "添加补充", AutoSize = true };
            _btnAddSupp.Click += delegate { AddSupplement(); };
            var suppBtnRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            suppBtnRow.Controls.Add(_btnAddSupp);
            suppT.Controls.Add(_txtSupplement, 0, 0);
            suppT.Controls.Add(suppBtnRow, 0, 1);
            gbSupp.Controls.Add(suppT);
            side.Controls.Add(gbSupp, 0, 2);

            // 底部固定：审批模式 + 轮控制（不被长日志挤出可视范围）
            var bottom = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
            var gbMode = new GroupBox { Text = "审批模式", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 2, 8, 6) };
            var modeT = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
            _rbManual = new RadioButton { Text = "手动审批（每阶段输出后暂停）", AutoSize = true, Checked = true };
            _rbAuto = new RadioButton { Text = "自动审批（输出有效即自动交接）", AutoSize = true };
            _lbModeNow = new Label { Text = "当前模式：手动审批", AutoSize = true, ForeColor = Color.FromArgb(30, 90, 160) };
            var lbModeHint = new Label
            {
                Text = "切换从下一个交接点生效，不重发已完成步骤",
                AutoSize = true,
                ForeColor = Color.FromArgb(120, 120, 120),
                MaximumSize = new Size(240, 0)
            };
            _rbManual.CheckedChanged += delegate { OnModeChanged(); };
            _rbAuto.CheckedChanged += delegate { OnModeChanged(); };
            modeT.Controls.Add(_rbManual);
            modeT.Controls.Add(_rbAuto);
            modeT.Controls.Add(_lbModeNow);
            modeT.Controls.Add(lbModeHint);
            gbMode.Controls.Add(modeT);
            bottom.Controls.Add(gbMode);

            _ckAutoPaste = new CheckBox
            {
                Text = "发送后自动粘贴到 Agent 窗口（失败则手动 Ctrl+V）",
                Checked = true,
                AutoSize = true,
                MaximumSize = new Size(240, 0)
            };
            bottom.Controls.Add(_ckAutoPaste);

            var gbRound = new GroupBox { Text = "轮控制", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
            var roundT = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
            roundT.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            roundT.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            _btnContinue = MkBtn("继续下一轮", delegate { DoContinue(false); });
            _btnSuppContinue = MkBtn("补充后继续", delegate { DoContinue(true); });
            _btnEnd = MkBtn("结束协同", delegate { DoEnd(); });
            _btnStopResume = MkBtn("停止", delegate { DoStopResume(); });
            _btnRetry = MkBtn("重试当前棒", delegate { DoRetry(); });
            roundT.Controls.Add(_btnContinue, 0, 0);
            roundT.Controls.Add(_btnSuppContinue, 1, 0);
            roundT.Controls.Add(_btnStopResume, 0, 1);
            roundT.Controls.Add(_btnRetry, 1, 1);
            roundT.Controls.Add(_btnEnd, 0, 2);
            roundT.SetColumnSpan(_btnEnd, 2);
            gbRound.Controls.Add(roundT);
            bottom.Controls.Add(gbRound);
            side.Controls.Add(bottom, 0, 3);

            MinimumSize = new Size(720, 560);

            _pump = new Timer { Interval = 200 };
            _pump.Tick += delegate { Pump(); };
            _pump.Start();

            RefreshAll();
        }

        static Label InfoRow(TableLayoutPanel t, string name)
        {
            int row = t.RowCount;
            t.RowCount = row + 1;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var k = new Label { Text = name, AutoSize = true, ForeColor = Color.FromArgb(90, 90, 90), Margin = new Padding(0, 2, 8, 2) };
            var v = new Label { Text = "-", AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), Margin = new Padding(0, 2, 0, 2) };
            t.Controls.Add(k, 0, row);
            t.Controls.Add(v, 1, row);
            return v;
        }

        static Button MkBtn(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(2) };
            b.Click += onClick;
            return b;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { Win32.AddClipboardFormatListener(Handle); } catch { }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            try { Win32.RemoveClipboardFormatListener(Handle); } catch { }
            base.OnHandleDestroyed(e);
        }

        public void Shutdown()
        {
            try { if (_pump != null) _pump.Stop(); } catch { }
            try { if (IsHandleCreated) Win32.RemoveClipboardFormatListener(Handle); } catch { }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Shutdown();
            base.Dispose(disposing);
        }

        // ================= 剪贴板接力 =================

        internal void SuppressNextClipboard()
        {
            _suppressClipboard = true;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Win32.WM_CLIPBOARDUPDATE)
            {
                OnClipboardChanged();
            }
            base.WndProc(ref m);
        }

        void OnClipboardChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(OnClipboardChanged)); } catch { } return; }
            if (_suppressClipboard) { _suppressClipboard = false; return; }   // 自己写入的回声，吞掉一次
            StageRecord st = _pipe.Cur;
            if (st == null || !_pipe.Running || st.Status != StageState.Running) return;
            // 仅当前台窗口是当前 Agent 时才自动捕获，防止误捕获用户在其他地方的复制
            AgentEntry agent = FindAgent(st.AgentId);
            if (!ForegroundIsAgent(agent)) return;
            string text = TryGetClipboardText();
            if (string.IsNullOrWhiteSpace(text)) return;
            if (text == _lastDelivered) return;                               // 双保险：内容相同视为回声
            _pipe.ReceiveReply(text);   // 迟到/重复由 Pipeline.ReceiveReply 内部再挡一道
            RefreshAll();
        }

        // 前台窗口是否属于当前 Agent；无法判断（未配置任何可匹配信息）时放行
        static bool ForegroundIsAgent(AgentEntry a)
        {
            if (a == null) return true;
            if (string.IsNullOrEmpty(a.Title) && string.IsNullOrEmpty(a.Proc) && string.IsNullOrEmpty(a.Exe)) return true;
            IntPtr fg = Win32.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            string title = Win32.WindowTitle(fg);
            if (!string.IsNullOrEmpty(a.Title) &&
                title.IndexOf(a.Title, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            try
            {
                uint pid;
                Win32.GetWindowThreadProcessId(fg, out pid);
                var p = Process.GetProcessById((int)pid);
                string pn = p.ProcessName ?? "";
                if (!string.IsNullOrEmpty(a.Proc) &&
                    pn.Equals(Win32.ProcName(a.Proc), StringComparison.OrdinalIgnoreCase)) return true;
                if (!string.IsNullOrEmpty(a.Exe) &&
                    pn.Equals(Win32.ProcName(a.Exe), StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
            return false;
        }

        static string TryGetClipboardText()
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    if (Clipboard.ContainsText()) return Clipboard.GetText();
                    return null;
                }
                catch { System.Threading.Thread.Sleep(60); }
            }
            return null;
        }

        static bool TrySetClipboardText(string text)
        {
            for (int i = 0; i < 3; i++)
            {
                try { Clipboard.SetText(text); return true; }
                catch { System.Threading.Thread.Sleep(80); }
            }
            return false;
        }

        // ================= 泵：投递 / 聚焦 / 超时 / 刷新 =================

        void Pump()
        {
            if (IsDisposed) return;

            // 1) 待投递提示词（SendCurrent/ApproveAndPass/ContinueRound/Retry 的产物）
            string deliver = _pipe.PendingDeliver;
            if (!string.IsNullOrEmpty(deliver))
            {
                _pipe.PendingDeliver = null;
                DeliverPrompt(deliver);
            }

            // 2) 聚焦任务（找到窗口后聚焦；开自动粘贴则补一次 Ctrl+V）
            if (_focusJob != null)
            {
                if (DateTime.Now > _focusJob.Until)
                {
                    _focusJob = null;
                }
                else
                {
                    IntPtr hwnd = Win32.FindAgentWindow(_focusJob.Agent);
                    if (hwnd != IntPtr.Zero)
                    {
                        Win32.FocusWindow(hwnd);
                        if (_ckAutoPaste.Checked && !_focusJob.Pasted)
                        {
                            _focusJob.Pasted = true;
                            System.Threading.Thread.Sleep(250);
                            try { SendKeys.SendWait("^v"); } catch { }
                        }
                        _focusJob = null;
                    }
                }
            }

            // 3) 超时检查：当前棒运行中超时 → 失败（不重发、不计轮次），等用户重试
            StageRecord st = _pipe.Cur;
            if (st != null && st.Status == StageState.Running && st.SentAt != default(DateTime))
            {
                if ((DateTime.Now - st.SentAt).TotalMinutes > Math.Max(1, _pipe.Config.StepTimeoutMinutes))
                    _pipe.Fail("等待回复超时（" + _pipe.Config.StepTimeoutMinutes + " 分钟）");
            }

            RefreshAll();
        }

        void DeliverPrompt(string prompt)
        {
            StageRecord st = _pipe.Cur;
            AgentEntry agent = FindAgent(st != null ? st.AgentId : "");
            _suppressClipboard = true;
            _lastDelivered = prompt;
            bool ok = TrySetClipboardText(prompt);
            if (!ok)
            {
                _suppressClipboard = false;
                _pipe.Fail("无法写入剪贴板，提示词未送出");
                return;
            }
            if (agent != null)
            {
                EnsureAgentRunning(agent);
                _focusJob = new FocusJob { Agent = agent, Until = DateTime.Now.AddSeconds(20) };
            }
        }

        void EnsureAgentRunning(AgentEntry a)
        {
            if (a == null) return;
            if (Win32.FindAgentWindow(a) != IntPtr.Zero) return;
            if (string.IsNullOrEmpty(a.Exe) || !File.Exists(a.Exe)) return;
            try { Process.Start(a.Exe); } catch { }
        }

        AgentEntry FindAgent(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var a in _agents)
                if (string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        // ================= 工作区回调（由 WorkspacePanel 调用） =================

        internal StageRecord StageFor(int wsIndex, bool showAlign)
        {
            RoundRecord r = _pipe.CurrentRound;
            if (r == null) return null;
            switch (wsIndex)
            {
                case 0: return showAlign ? r.SAlign : r.SDec;
                case 1: return r.SExec;
                default: return r.SCheck;
            }
        }

        // 该工作区是否正处于当前执行阶段
        internal bool IsCurrentPhase(int wsIndex)
        {
            switch (wsIndex)
            {
                case 0: return _pipe.Phase == PipePhase.Decompose || _pipe.Phase == PipePhase.Align;
                case 1: return _pipe.Phase == PipePhase.Execute;
                default: return _pipe.Phase == PipePhase.SelfCheck;
            }
        }

        // 空闲（未开始/已结束）：只有 Agent1 工作区可编辑、可启动
        internal bool IdleState
        {
            get
            {
                return _pipe.Session == null || _pipe.Session.Ended ||
                       _pipe.Phase == PipePhase.Idle || _pipe.Phase == PipePhase.Finished;
            }
        }

        internal bool AlignIsCurrent { get { return _pipe.Phase == PipePhase.Align; } }

        internal void SendFrom(int wsIndex, string leftText, StageRecord viewStage)
        {
            // 未开始：Agent1 的「发送」即开始协同（初始需求由用户点击发送）
            if (IdleState)
            {
                if (wsIndex != 0) return;
                string err = _pipe.Start(leftText);
                if (err != null) { MessageBox.Show(err, "无法开始", MessageBoxButtons.OK, MessageBoxIcon.Warning); RefreshAll(); return; }
                _pipe.SendCurrent(leftText);
                RefreshAll();
                return;
            }
            if (!IsCurrentPhase(wsIndex)) return;
            if (_pipe.Phase == PipePhase.Stopped || _pipe.Phase == PipePhase.Failed) return;   // 先恢复/重试
            StageRecord st = _pipe.Cur;
            if (st == null || st != viewStage) return;    // 只能发送当前阶段的这一棒
            if (st.Status == StageState.Running) return;  // 防重复发送
            _pipe.SendCurrent(leftText);
            RefreshAll();
        }

        internal void ApproveFrom(int wsIndex, string editedOutput, StageRecord viewStage)
        {
            if (!IsCurrentPhase(wsIndex)) return;
            if (_pipe.Cur == null || _pipe.Cur != viewStage) return;
            _pipe.EditHandoff(editedOutput);      // 先提交右侧编辑版
            _pipe.ApproveAndPass();               // 交接并启动下一棒（对齐棒则完成本轮）
            RefreshAll();
        }

        internal void FillNextFrom(int wsIndex, string editedOutput, StageRecord viewStage)
        {
            if (!IsCurrentPhase(wsIndex)) return;
            if (_pipe.Cur == null || _pipe.Cur != viewStage) return;
            _pipe.EditHandoff(editedOutput);
            _pipe.FillNextOnly();
            RefreshAll();
        }

        internal void RerunFrom(int wsIndex, StageRecord viewStage)
        {
            if (!IsCurrentPhase(wsIndex)) return;
            if (_pipe.Cur == null || _pipe.Cur != viewStage) return;
            _pipe.RerunCurrent();                 // 归档当前尝试后重新调用
            RefreshAll();
        }

        internal void PasteReplyFrom(int wsIndex, StageRecord viewStage)
        {
            if (!IsCurrentPhase(wsIndex)) return;
            StageRecord st = _pipe.Cur;
            if (st == null || st != viewStage || st.Status != StageState.Running) return;
            string text = TryGetClipboardText();
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show("剪贴板为空。请先在 Agent 窗口中复制回复。", "手动推进", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _pipe.ReceiveReply(text);
            RefreshAll();
        }

        internal void EditHandoffFrom(int wsIndex, string text, StageRecord viewStage)
        {
            if (!IsCurrentPhase(wsIndex)) return;
            if (_pipe.Cur == null || _pipe.Cur != viewStage) return;
            _pipe.EditHandoff(text);              // 只改交接依据，不重新调用 Agent
        }

        internal List<AgentEntry> Agents { get { return _agents; } }

        internal void PickAgent(int wsIndex, string agentId)
        {
            if (wsIndex == 0)
            {
                // Agent1 同时负责拆解与对齐两棒
                _pipe.Config.Steps[0].Agent = agentId;
                _pipe.Config.Steps[_pipe.StepCount - 1].Agent = agentId;
            }
            else
            {
                _pipe.Config.Steps[wsIndex].Agent = agentId;
            }
            RoundRecord r = _pipe.CurrentRound;
            if (r != null)
            {
                if (wsIndex == 0) { r.SDec.AgentId = agentId; r.SAlign.AgentId = agentId; }
                else if (wsIndex == 1) r.SExec.AgentId = agentId;
                else r.SCheck.AgentId = agentId;
            }
        }

        // ================= 状态栏动作 =================

        void OnModeChanged()
        {
            if (_syncingRadios) return;
            _pipe.Mode = _rbAuto.Checked ? ApprovalMode.Auto : ApprovalMode.Manual;
            _lbModeNow.Text = "当前模式：" + (_rbAuto.Checked ? "自动审批" : "手动审批");
            _pipe.Note = "审批模式已切换为「" + (_rbAuto.Checked ? "自动" : "手动") + "」，从下一交接点生效";
            RefreshAll();
        }

        void AddSupplement()
        {
            string text = _txtSupplement.Text.Trim();
            if (text.Length == 0) return;
            StageRecord st = _pipe.Cur;
            string roleName = st != null ? st.Role : _pipe.PhaseName;
            string agentId = st != null ? st.AgentId : "";
            var s = _pipe.AddSupplement(agentId, roleName, text);
            if (s == null)
            {
                MessageBox.Show("当前没有进行中的会话，补充未加入队列。", "补充需求", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _txtSupplement.Clear();
            _pipe.Note = "补充已排队：" + _pipe.SupplementHint();
            RefreshAll();
        }

        void DoContinue(bool withSupplement)
        {
            if (_pipe.Phase != PipePhase.RoundDone) return;
            if (withSupplement)
            {
                string text = _txtSupplement.Text.Trim();
                if (text.Length > 0)
                {
                    _pipe.AddSupplement("A1", "拆解", text);
                    _txtSupplement.Clear();
                }
            }
            _pipe.ContinueRound();   // 手动模式备好拆解待发送；自动模式直接发送
            if (_pipe.Mode == ApprovalMode.Manual)
                _pipe.Note = "第 " + _pipe.CurrentRoundNo + " 轮 · 请确认/修改左侧需求后点「发送给 Agent 1」";
            RefreshAll();
        }

        void DoEnd()
        {
            _pipe.Finish();
            RefreshAll();
        }

        void DoStopResume()
        {
            if (_pipe.Phase == PipePhase.Stopped) _pipe.Resume();
            else if (_pipe.Running) _pipe.Stop();
            RefreshAll();
        }

        void DoRetry()
        {
            if (_pipe.Phase != PipePhase.Failed) return;
            _pipe.Retry();
            RefreshAll();
        }

        // ================= 刷新 =================

        void RefreshAll()
        {
            if (IsDisposed) return;
            _lbRound.Text = _pipe.CurrentRoundNo > 0 ? "第 " + _pipe.CurrentRoundNo + " 轮" : "未开始";
            _lbDone.Text = _pipe.CompletedRounds + " 轮";
            _lbPhase.Text = _pipe.PhaseName;
            StageRecord st = _pipe.Cur;
            _lbState.Text = StateText(st);
            _lbQueue.Text = _pipe.PendingSupplementCount + " 条待生效";
            _lbNote.Text = _pipe.Note ?? "";

            int want = _pipe.CompletedRounds;
            if (_lstHistory.Items.Count != want)
            {
                _lstHistory.Items.Clear();
                if (_pipe.Session != null)
                {
                    foreach (RoundRecord r in _pipe.Session.Rounds)
                        _lstHistory.Items.Add("第 " + r.Number + " 轮 · " + r.FinishedAt.ToString("HH:mm:ss"));
                }
            }

            bool roundDone = _pipe.Phase == PipePhase.RoundDone;
            _btnContinue.Enabled = roundDone && _pipe.CompletedRounds < _pipe.Config.MaxRounds;
            _btnSuppContinue.Enabled = roundDone && _pipe.CompletedRounds < _pipe.Config.MaxRounds;
            _btnEnd.Enabled = _pipe.Session != null && !_pipe.Session.Ended && _pipe.Phase != PipePhase.Idle;
            _btnStopResume.Enabled = _pipe.Running || _pipe.Phase == PipePhase.Stopped;
            _btnStopResume.Text = _pipe.Phase == PipePhase.Stopped ? "恢复" : "停止";
            _btnRetry.Enabled = _pipe.Phase == PipePhase.Failed;

            _syncingRadios = true;
            _rbManual.Checked = _pipe.Mode == ApprovalMode.Manual;
            _rbAuto.Checked = _pipe.Mode == ApprovalMode.Auto;
            _lbModeNow.Text = "当前模式：" + (_pipe.Mode == ApprovalMode.Manual ? "手动审批" : "自动审批");
            _syncingRadios = false;

            // 阶段切换时，Agent1 工作区自动切到对应视图（拆解/对齐）
            if (_pipe.Phase != _lastPhase)
            {
                _lastPhase = _pipe.Phase;
                if (_pipe.Phase == PipePhase.Align) _ws1.SwitchToAlign();
                else if (_pipe.Phase == PipePhase.Decompose) _ws1.SwitchToDecompose();
            }

            _ws1.RefreshView();
            _ws2.RefreshView();
            _ws3.RefreshView();
        }

        static string StateText(StageRecord st)
        {
            if (st == null) return "—";
            string agent = string.IsNullOrEmpty(st.AgentId) ? "" : (" · " + st.AgentId);
            switch (st.Status)
            {
                case StageState.Draft: return "草稿（待发送）" + agent;
                case StageState.Running: return "运行中（等待 " + st.Role + " 回复）" + agent;
                case StageState.AwaitingApproval: return "待审批" + agent;
                case StageState.HandedOff: return "已传递" + agent;
                case StageState.Failed: return "失败" + agent;
                default: return "—";
            }
        }

        void ShowRoundDetail()
        {
            int i = _lstHistory.SelectedIndex;
            if (_pipe.Session == null || i < 0 || i >= _pipe.Session.Rounds.Count) return;
            RoundRecord r = _pipe.Session.Rounds[i];
            var sb = new StringBuilder();
            sb.AppendLine("第 " + r.Number + " 轮明细（" + r.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss") + "）");
            AppendStage(sb, "① 需求拆解（Agent1）", r.SDec);
            AppendStage(sb, "② 任务执行（Agent2）", r.SExec);
            AppendStage(sb, "③ 完成自检（Agent3）", r.SCheck);
            AppendStage(sb, "④ 最终对齐（Agent1）", r.SAlign);
            if (!string.IsNullOrEmpty(r.UserAddition))
            {
                sb.AppendLine();
                sb.AppendLine("【本轮生效的用户补充】");
                sb.AppendLine(r.UserAddition);
            }
            ShowTextDialog("第 " + r.Number + " 轮", sb.ToString());
        }

        static void AppendStage(StringBuilder sb, string title, StageRecord s)
        {
            sb.AppendLine();
            sb.AppendLine("========== " + title + " ==========");
            if (s == null) { sb.AppendLine("（无记录）"); return; }
            sb.AppendLine("状态：" + s.Status + "  生成次数：" + s.AttemptCount + (s.HandoffEdited ? "  （交接版已被用户编辑）" : ""));
            sb.AppendLine("--- 发送快照（" + (s.SentAt == default(DateTime) ? "未发送" : s.SentAt.ToString("HH:mm:ss")) + "） ---");
            sb.AppendLine(string.IsNullOrEmpty(s.SentPrompt) ? "（空）" : s.SentPrompt);
            sb.AppendLine("--- 原始回复（" + (s.ReplyAt == default(DateTime) ? "无" : s.ReplyAt.ToString("HH:mm:ss")) + "） ---");
            sb.AppendLine(string.IsNullOrEmpty(s.RawReply) ? "（无）" : s.RawReply);
            if (s.HandoffEdited)
            {
                sb.AppendLine("--- 用户编辑后的交接版（实际交接依据） ---");
                sb.AppendLine(s.HandoffText ?? "");
            }
            for (int k = 0; k < s.History.Count; k++)
            {
                var h = s.History[k];
                sb.AppendLine("--- 历史第 " + (k + 1) + " 次（" + h.SentAt.ToString("HH:mm:ss") + "）回复 ---");
                sb.AppendLine(string.IsNullOrEmpty(h.RawReply) ? "（无）" : h.RawReply);
            }
        }

        internal static void ShowTextDialog(string title, string text)
        {
            var f = new Form
            {
                Text = title,
                Width = 760,
                Height = 560,
                StartPosition = FormStartPosition.CenterParent
            };
            var tb = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both,
                Font = new Font("Consolas", 9.5f),
                Text = text ?? ""
            };
            f.Controls.Add(tb);
            f.ShowDialog();
        }

        // =====================================================================
        // 单个工作区：标题行 + 左输入(40%) / 右输出(60%) SplitContainer + 按钮行
        // =====================================================================
        class WorkspacePanel : UserControl
        {
            readonly SynergyPanel _owner;
            readonly int _wsIndex;
            readonly bool _dualView;              // Agent1：拆解 / 对齐 两段切换
            bool _showAlign;
            bool _userSplit;

            static readonly Font FontHead = new Font(SystemFonts.DefaultFont.FontFamily, 10f, FontStyle.Bold);
            static readonly Font FontMono = new Font("Consolas", 9.5f);
            static readonly Font FontTabOn = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
            static readonly Font FontTabOff = new Font(SystemFonts.DefaultFont, FontStyle.Regular);

            readonly Label _lbRole, _lbState, _lbLeftCap, _lbRightCap, _lbUpdated;
            readonly ComboBox _cmbAgent;
            readonly RichTextBox _input, _output;
            readonly SplitContainer _split;
            readonly Button _btnSend, _btnApprove, _btnFillNext, _btnRerun, _btnPasteReply, _btnRaw, _btnCopy;
            readonly Button _tabDec, _tabAlign;
            bool _syncAgent;

            public WorkspacePanel(SynergyPanel owner, string title, int wsIndex, bool dualView)
            {
                _owner = owner;
                _wsIndex = wsIndex;
                _dualView = dualView;
                Dock = DockStyle.Fill;
                Margin = new Padding(0, 0, 0, 6);
                MinimumSize = new Size(300, 160);
                BackColor = Color.White;
                BorderStyle = BorderStyle.FixedSingle;

                var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6, 4, 6, 4) };
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 标题行
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));   // 输入/输出
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 按钮行
                Controls.Add(root);

                // ---- 标题行 ----
                var head = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
                _lbRole = new Label { Text = title, AutoSize = true, Font = FontHead, Margin = new Padding(0, 4, 10, 0) };
                head.Controls.Add(_lbRole);
                if (_dualView)
                {
                    _tabDec = new Button { Text = "需求拆解", AutoSize = true, Margin = new Padding(0, 0, 4, 0), FlatStyle = FlatStyle.Flat };
                    _tabAlign = new Button { Text = "最终对齐", AutoSize = true, Margin = new Padding(0, 0, 10, 0), FlatStyle = FlatStyle.Flat };
                    _tabDec.Click += delegate { _showAlign = false; RefreshView(); };
                    _tabAlign.Click += delegate { _showAlign = true; RefreshView(); };
                    head.Controls.Add(_tabDec);
                    head.Controls.Add(_tabAlign);
                }
                _cmbAgent = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150, Margin = new Padding(0, 0, 10, 0) };
                foreach (var a in owner.Agents) _cmbAgent.Items.Add(a);
                _cmbAgent.SelectedIndexChanged += delegate
                {
                    if (_syncAgent) return;
                    var a = _cmbAgent.SelectedItem as AgentEntry;
                    if (a != null) owner.PickAgent(_wsIndex, a.Id);
                };
                head.Controls.Add(_cmbAgent);
                _lbState = new Label { Text = "", AutoSize = true, ForeColor = Color.FromArgb(30, 90, 160), Margin = new Padding(0, 4, 0, 0) };
                head.Controls.Add(_lbState);
                root.Controls.Add(head, 0, 0);

                // ---- 左输入 / 右输出 ----
                _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 5 };
                _split.SplitterMoved += delegate { _userSplit = true; };
                _split.SizeChanged += delegate { KeepRatio(); };

                var leftBox = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
                leftBox.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                leftBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                var leftCapRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
                _lbLeftCap = new Label { Text = "输入", AutoSize = true, ForeColor = Color.FromArgb(90, 90, 90) };
                _lbUpdated = new Label { Text = "", AutoSize = true, ForeColor = Color.FromArgb(200, 90, 20), Cursor = Cursors.Hand };
                _lbUpdated.Click += delegate { ForcePull(); };
                leftCapRow.Controls.Add(_lbLeftCap);
                leftCapRow.Controls.Add(_lbUpdated);
                _input = new RichTextBox
                {
                    Dock = DockStyle.Fill,
                    AcceptsTab = true,
                    WordWrap = false,
                    ScrollBars = RichTextBoxScrollBars.Both,
                    Font = FontMono,
                    DetectUrls = false
                };
                leftBox.Controls.Add(leftCapRow, 0, 0);
                leftBox.Controls.Add(_input, 0, 1);

                var rightBox = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
                rightBox.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                rightBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
                _lbRightCap = new Label { Text = "输出", AutoSize = true, ForeColor = Color.FromArgb(90, 90, 90) };
                _output = new RichTextBox
                {
                    Dock = DockStyle.Fill,
                    AcceptsTab = true,
                    WordWrap = false,
                    ScrollBars = RichTextBoxScrollBars.Both,
                    Font = FontMono,
                    DetectUrls = false
                };
                _output.Leave += delegate { CommitEdit(); };
                if (wsIndex == 1)
                {
                    var tip = new ToolTip();
                    tip.SetToolTip(_output, "编辑结果文本只改变交接内容，不代表文件/代码等产物已被修改；需要修改产物时请用「重新生成」。");
                }
                rightBox.Controls.Add(_lbRightCap, 0, 0);
                rightBox.Controls.Add(_output, 0, 1);

                _split.Panel1.Controls.Add(leftBox);
                _split.Panel2.Controls.Add(rightBox);
                root.Controls.Add(_split, 0, 1);

                // ---- 按钮行 ----
                var btns = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
                _btnSend = Mk("发送", delegate { _owner.SendFrom(_wsIndex, _input.Text, Stage); });
                _btnApprove = Mk("批准并传递", delegate { CommitEdit(); _owner.ApproveFrom(_wsIndex, _output.Text, Stage); });
                _btnFillNext = Mk("仅填入下一阶段", delegate { CommitEdit(); _owner.FillNextFrom(_wsIndex, _output.Text, Stage); });
                _btnRerun = Mk("重新生成", delegate { _owner.RerunFrom(_wsIndex, Stage); });
                _btnPasteReply = Mk("以剪贴板为回复", delegate { _owner.PasteReplyFrom(_wsIndex, Stage); });
                _btnRaw = Mk("原始回复/历史", delegate { ShowRaw(); });
                _btnCopy = Mk("复制输出", delegate { CopyOutput(); });
                btns.Controls.Add(_btnSend);
                btns.Controls.Add(_btnApprove);
                btns.Controls.Add(_btnFillNext);
                btns.Controls.Add(_btnRerun);
                btns.Controls.Add(_btnPasteReply);
                btns.Controls.Add(_btnRaw);
                btns.Controls.Add(_btnCopy);
                root.Controls.Add(btns, 0, 2);
            }

            static Button Mk(string text, EventHandler onClick)
            {
                var b = new Button { Text = text, AutoSize = true, Margin = new Padding(0, 2, 6, 2), Padding = new Padding(6, 0, 6, 0) };
                b.Click += onClick;
                return b;
            }

            internal void SwitchToAlign() { if (_dualView) { _showAlign = true; RefreshView(); } }
            internal void SwitchToDecompose() { if (_dualView) { _showAlign = false; RefreshView(); } }

            void KeepRatio()
            {
                // 默认左 40% / 右 60%；用户拖动分隔线后不再强制
                if (_userSplit) return;
                try
                {
                    if (_split.Width > 150)
                        _split.SplitterDistance = (int)(_split.Width * 0.40);
                }
                catch { }
            }

            StageRecord Stage { get { return _owner.StageFor(_wsIndex, _showAlign); } }

            public void RefreshView()
            {
                StageRecord st = Stage;
                StageRecord cur = _owner._pipe.Cur;
                bool thisIsCur = st != null && cur == st && _owner.IsCurrentPhase(_wsIndex);
                bool idleStart = _wsIndex == 0 && !_showAlign && _owner.IdleState;

                if (_dualView && _tabDec != null)
                {
                    _tabDec.Font = _owner.AlignIsCurrent ? FontTabOff : FontTabOn;
                    _tabAlign.Font = _owner.AlignIsCurrent ? FontTabOn : FontTabOff;
                }

                // Agent 选择框同步
                _syncAgent = true;
                string wantId = st != null ? st.AgentId : "";
                int sel = -1;
                for (int i = 0; i < _cmbAgent.Items.Count; i++)
                {
                    var a = _cmbAgent.Items[i] as AgentEntry;
                    if (a != null && string.Equals(a.Id, wantId, StringComparison.OrdinalIgnoreCase)) { sel = i; break; }
                }
                if (sel >= 0 && _cmbAgent.SelectedIndex != sel) _cmbAgent.SelectedIndex = sel;
                _syncAgent = false;

                // 标题后缀与说明
                if (_wsIndex == 0)
                    _lbLeftCap.Text = _showAlign ? "对齐输入（可编辑、可补充）" : "任务需求输入（可编辑、可补充）";
                else if (_wsIndex == 1)
                    _lbLeftCap.Text = "接收到的任务（可编辑、可补充执行要求）";
                else
                    _lbLeftCap.Text = "待检查内容（可补充检查要求）";
                _lbRightCap.Text = (st != null ? st.Role : "结果") + "结果" +
                    (st != null && st.HandoffEdited ? "（已编辑·交接以此版为准）" : "（输出就绪后可编辑）");

                // 输入框：聚焦编辑中不被界面刷新覆盖，提示有新版本可拉取
                string wantInput = st != null ? (st.PreparedInput ?? "") : "";
                if (_input.Focused)
                {
                    _lbUpdated.Text = (_input.Text != wantInput && wantInput.Length > 0) ? " 有新交接内容，点击拉取" : "";
                }
                else
                {
                    if (_input.Text != wantInput) { _input.Text = wantInput; _input.Modified = false; }
                    _lbUpdated.Text = "";
                }
                bool inputEditable = idleStart || (thisIsCur && st.Status != StageState.Running);
                _input.ReadOnly = !inputEditable;
                _input.BackColor = _input.ReadOnly ? Color.FromArgb(246, 246, 246) : Color.White;

                // 输出框：输出完成后才允许编辑（运行中锁定，避免刷新覆盖用户编辑）
                string wantOutput = st != null ? (st.HandoffText ?? "") : "";
                bool outputEditable = thisIsCur && (st.Status == StageState.AwaitingApproval || st.Status == StageState.HandedOff);
                _output.ReadOnly = !outputEditable;
                _output.BackColor = _output.ReadOnly ? Color.FromArgb(246, 246, 246) : Color.White;
                if (!_output.Focused && _output.Text != wantOutput) { _output.Text = wantOutput; _output.Modified = false; }

                // 状态徽标
                _lbState.Text = thisIsCur ? ("● " + StateText(st)) : (st != null && st.Status != StageState.Draft ? StateText(st) : "");
                _lbState.ForeColor = StateColor(st, thisIsCur);

                // 按钮可用性（只在当前阶段且状态匹配时可用，杜绝重复点击造成重复执行）
                _btnSend.Text = idleStart ? "开始并发送给 Agent 1" : "发送给 " + AgentLabel();
                _btnSend.Enabled = idleStart || (thisIsCur && st.Status == StageState.Draft);
                _btnApprove.Enabled = thisIsCur && st.Status == StageState.AwaitingApproval;
                _btnFillNext.Enabled = _btnApprove.Enabled && !(_wsIndex == 0 && _showAlign);   // 对齐是末棒，无下一阶段
                _btnRerun.Enabled = thisIsCur && st.Status == StageState.AwaitingApproval;
                _btnPasteReply.Enabled = thisIsCur && st.Status == StageState.Running;
                _btnRaw.Enabled = st != null && (st.AttemptCount > 0 || st.RawReply.Length > 0 || st.History.Count > 0);
                _btnCopy.Enabled = st != null && wantOutput.Length > 0;
            }

            string AgentLabel()
            {
                var a = _cmbAgent.SelectedItem as AgentEntry;
                return a != null ? a.Id : ("Agent " + (_wsIndex + 1));
            }

            Color StateColor(StageRecord st, bool isCurrent)
            {
                if (st == null) return Color.Gray;
                switch (st.Status)
                {
                    case StageState.Running: return Color.FromArgb(200, 130, 20);
                    case StageState.AwaitingApproval: return Color.FromArgb(30, 90, 200);
                    case StageState.HandedOff: return Color.FromArgb(40, 140, 60);
                    case StageState.Failed: return Color.FromArgb(190, 40, 40);
                    default: return isCurrent ? Color.FromArgb(30, 90, 160) : Color.Gray;
                }
            }

            void ForcePull()
            {
                StageRecord st = Stage;
                if (st == null) return;
                _input.Text = st.PreparedInput ?? "";
                _input.Modified = false;
                _lbUpdated.Text = "";
            }

            void CommitEdit()
            {
                StageRecord st = Stage;
                if (st == null) return;
                if (_output.Modified) { _owner.EditHandoffFrom(_wsIndex, _output.Text, st); _output.Modified = false; }
            }

            void CopyOutput()
            {
                if (_output.Text.Length == 0) return;
                _owner.SuppressNextClipboard();
                try { Clipboard.SetText(_output.Text); } catch { }
            }

            void ShowRaw()
            {
                StageRecord st = Stage;
                if (st == null) return;
                var sb = new StringBuilder();
                sb.AppendLine("【" + st.Role + "】状态：" + st.Status + "  生成次数：" + st.AttemptCount);
                sb.AppendLine();
                sb.AppendLine("===== 发送快照（" + (st.SentAt == default(DateTime) ? "未发送" : st.SentAt.ToString("yyyy-MM-dd HH:mm:ss")) + "） =====");
                sb.AppendLine(string.IsNullOrEmpty(st.SentPrompt) ? "（空）" : st.SentPrompt);
                sb.AppendLine();
                sb.AppendLine("===== Agent 原始回复（只读，" + (st.ReplyAt == default(DateTime) ? "无" : st.ReplyAt.ToString("yyyy-MM-dd HH:mm:ss")) + "） =====");
                sb.AppendLine(string.IsNullOrEmpty(st.RawReply) ? "（无）" : st.RawReply);
                for (int k = 0; k < st.History.Count; k++)
                {
                    var h = st.History[k];
                    sb.AppendLine();
                    sb.AppendLine("===== 历史第 " + (k + 1) + " 次 · 发送 " + h.SentAt.ToString("HH:mm:ss") + " =====");
                    sb.AppendLine(h.SentPrompt ?? "");
                    sb.AppendLine("----- 对应回复 -----");
                    sb.AppendLine(string.IsNullOrEmpty(h.RawReply) ? "（无）" : h.RawReply);
                }
                ShowTextDialog(st.Role + " · 原始回复与历史", sb.ToString());
            }
        }
    }
}
