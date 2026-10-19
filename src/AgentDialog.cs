using System;
using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace AgentHub
{
    // 每个角色的独立对话窗：消息记录（我方提示词 / agent 回复 / 用户补充）、
    // 多行输入框、发送按钮、顶部身份标识。
    //
    // 注意架构边界：管理的 agent 是外部桌面应用，没有 API 也没有流式接口，
    // 这里的"对话记录"是剪贴板接力实际发生过的内容（发出去的提示词 + 捕获回来的回复）。
    // 「补充需求」点击发送后进入 Pipeline 的队列（Session.Supplements），
    // 注明来源、时间和生效状态，在下一次拼提示词时真实进入调用。
    internal class AgentDialog : Form
    {
        private static readonly Color Bg = Color.FromArgb(11, 11, 13);
        private static readonly Color Fg = Color.FromArgb(232, 232, 236);
        private static readonly Color Dim = Color.FromArgb(126, 126, 136);
        private static readonly Color Orange = Color.FromArgb(255, 122, 26);

        private readonly Shell _shell;
        private readonly int _card;
        private readonly string _agentId;
        private readonly string _agentName;
        private readonly string _roleName;

        private readonly Label _identity = new Label();
        private readonly TextBox _history = new TextBox();
        private readonly TextBox _input = new TextBox();
        private readonly Button _send = new Button();
        private readonly Button _openApp = new Button();
        private readonly Label _hint = new Label();

        // 所有对话窗实例：InputUiBusy 用它判断是否有对话窗正开着（开着就不许抢前台，保护输入法候选窗）
        internal static readonly System.Collections.Generic.List<AgentDialog> Open =
            new System.Collections.Generic.List<AgentDialog>();

        public AgentDialog(Shell shell, int card, string agentId, string agentName, string roleName)
        {
            _shell = shell;
            _card = card;
            _agentId = agentId;
            _agentName = agentName;
            _roleName = roleName;
            Open.Add(this);

            Text = "对话 · " + roleName + " · " + agentName;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 520);
            BackColor = Bg;
            ForeColor = Fg;
            MinimizeBox = false;
            MaximizeBox = false;

            _identity.AutoSize = false;
            _identity.SetBounds(16, 14, ClientSize.Width - 32, 22);
            _identity.ForeColor = Dim;
            _identity.Text = "角色：" + roleName + "　Agent：" + agentName + "　（消息 = 剪贴板接力中真实发生的内容）";

            _history.Multiline = true;
            _history.ReadOnly = true;
            _history.ScrollBars = ScrollBars.Vertical;
            _history.BackColor = Color.FromArgb(18, 18, 22);
            _history.ForeColor = Fg;
            _history.BorderStyle = BorderStyle.FixedSingle;
            _history.Font = new Font("Microsoft YaHei UI", 9f);

            _input.Multiline = true;
            _input.BackColor = Color.FromArgb(20, 20, 24);
            _input.ForeColor = Fg;
            _input.BorderStyle = BorderStyle.FixedSingle;
            _input.Font = new Font("Microsoft YaHei UI", 9.5f);

            StyleBtn(_openApp, false);
            _openApp.Text = "打开应用窗口";
            _openApp.Click += delegate { _shell.FocusRoleCard(_card); };

            StyleBtn(_send, true);
            _send.Text = "补充需求并发送";
            _send.Click += SendClick;

            _hint.AutoSize = false;
            _hint.ForeColor = Dim;
            _hint.Text = "";

            Controls.Add(_identity);
            Controls.Add(_history);
            Controls.Add(_input);
            Controls.Add(_openApp);
            Controls.Add(_send);
            Controls.Add(_hint);

            LayoutAll();
        }

        static void StyleBtn(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = primary ? Orange : Color.FromArgb(52, 52, 60);
            b.BackColor = primary ? Orange : Color.FromArgb(20, 20, 24);
            b.ForeColor = primary ? Color.FromArgb(11, 11, 13) : Fg;
        }

        private void LayoutAll()
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            _identity.SetBounds(16, 14, w - 32, 22);
            _history.SetBounds(16, 44, w - 32, h - 44 - 132);
            _input.SetBounds(16, h - 80, w - 32 - 240, 56);
            _openApp.SetBounds(w - 224, h - 80, 104, 34);
            _send.SetBounds(w - 224, h - 44, 104, 34);
            _hint.SetBounds(16, h - 20, w - 32, 18);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_history != null) LayoutAll();
        }

        // 消息记录重排：我方交给它的提示词（kind 0）、它回复的（1）、用户补充（2）
        public void RefreshHistory()
        {
            PipeSession s = _shell.SessionOf();
            var sb = new StringBuilder();
            if (s == null)
            {
                _history.Text = "（协同尚未开始）";
                return;
            }
            System.Collections.Generic.List<ChatMsg> list;
            if (!s.Chats.TryGetValue(_agentId, out list) || list.Count == 0)
            {
                _history.Text = "（还没有和「" + _agentName + "」的接力记录）";
                return;
            }
            foreach (ChatMsg m in list)
            {
                string who = m.Kind == 0 ? "交给它" : (m.Kind == 1 ? "它回复" : "用户补充");
                sb.Append("【").Append(who).Append(" ").Append(m.Time.ToString("HH:mm:ss")).Append("】")
                  .AppendLine().Append(m.Text).AppendLine().AppendLine();
            }
            _history.Text = sb.ToString();
            _history.SelectionStart = _history.Text.Length;
            _history.ScrollToCaret();
        }

        // EventHandler 签名：Button.Click 要求 (object, EventArgs)
        void SendClick(object sender, EventArgs e)
        {
            string text = _input.Text.Trim();
            if (text.Length == 0) { _hint.Text = "先写点内容"; return; }
            string effect = _shell.DialogAddSupplement(_card, text);
            if (effect == null)
            {
                _hint.Text = "会话未开始或已结束，补充未生效";
                return;
            }
            _input.Text = "";
            _hint.Text = effect;
            RefreshHistory();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 不销毁，只隐藏：对话记录和输入保留，下次打开接着看
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }

    // 轮次结论查看窗：点右侧面板的某轮条目打开，完整展示该轮的四段结果
    internal class RoundViewer : Form
    {
        private static RoundViewer _current;

        private static readonly Color VB = Color.FromArgb(11, 11, 13);
        private static readonly Color VF = Color.FromArgb(232, 232, 236);

        private readonly TextBox _text = new TextBox();

        private RoundViewer(RoundRecord r)
        {
            Text = "第 " + r.Number + " 轮 · 完整记录";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(680, 560);
            BackColor = VB;
            ForeColor = VF;
            MinimizeBox = false;
            MaximizeBox = false;

            _text.Multiline = true;
            _text.ReadOnly = true;
            _text.ScrollBars = ScrollBars.Vertical;
            _text.BackColor = Color.FromArgb(18, 18, 22);
            _text.ForeColor = VF;
            _text.BorderStyle = BorderStyle.FixedSingle;
            _text.Font = new Font("Microsoft YaHei UI", 9f);
            _text.Dock = DockStyle.Fill;

            var sb = new StringBuilder();
            sb.AppendLine("第 " + r.Number + " 轮 · 完成于 " + r.FinishedAt.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("──────────────────────────────");
            sb.AppendLine();
            sb.AppendLine("【拆解 · 含验收标准】");
            sb.AppendLine(r.Decomposition ?? "（无）");
            sb.AppendLine();
            sb.AppendLine("【执行结果】");
            sb.AppendLine(r.Execution ?? "（无）");
            sb.AppendLine();
            sb.AppendLine("【自检】");
            sb.AppendLine(r.SelfCheck ?? "（无）");
            sb.AppendLine();
            sb.AppendLine("【对齐结论】");
            sb.AppendLine(r.Alignment ?? "（无）");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(r.UserAddition))
            {
                sb.AppendLine("【本轮用户补充】");
                sb.AppendLine(r.UserAddition);
            }
            _text.Text = sb.ToString();
            _text.SelectionStart = 0;

            Controls.Add(_text);
        }

        public static void Show(RoundRecord r)
        {
            try { if (_current != null && !_current.IsDisposed) _current.Close(); }
            catch { }
            _current = new RoundViewer(r);
            _current.Show();
            _current.Activate();
        }
    }
}
