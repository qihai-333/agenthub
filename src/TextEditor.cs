using System;
using System.Drawing;
using System.Windows.Forms;

namespace AgentHub
{
    /// <summary>
    /// 独立的多行文本编辑窗口。
    /// 协同面板里的输入区在 WS_EX_NOACTIVATE 的主窗口中无法正常使用输入法（候选窗一闪即消），
    /// 所以改为：面板里的文本框只读，点一下弹出这个普通窗口来编辑，确认后回填。
    /// </summary>
    internal class TextEditorForm : Form
    {
        private readonly TextBox _box;
        private string _result;

        // 打开中的编辑窗数量：>0 时任何代码都不许抢前台（否则中文输入法候选窗会被打断）
        internal static int OpenCount;

        private TextEditorForm(string title, string initial)
        {
            OpenCount++;
            Text = title;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = true;
            TopMost = true;   // 主窗口是全屏的，不置顶会被盖住
            Font = new Font("Microsoft YaHei UI", 10.5f);
            ClientSize = new Size(760, 520);
            MinimumSize = new Size(460, 320);

            _box = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                AcceptsReturn = true,
                AcceptsTab = true,
                ImeMode = ImeMode.On,
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Microsoft YaHei UI", 11f),
                Text = initial ?? ""
            };
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 0) };
            host.Controls.Add(_box);

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 56,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(12, 10, 12, 10)
            };
            var ok = new Button { Text = "确定（Ctrl+Enter）", AutoSize = true, Height = 32 };
            var cancel = new Button { Text = "取消", AutoSize = true, Height = 32 };
            ok.Click += delegate { Accept(); };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            bar.Controls.Add(ok);
            bar.Controls.Add(cancel);

            Controls.Add(host);
            Controls.Add(bar);
            AcceptButton = null;
            KeyPreview = true;
            KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.Enter) { Accept(); e.Handled = true; }
                else if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };
            Shown += delegate { _box.Focus(); _box.SelectionStart = _box.TextLength; };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (OpenCount > 0) OpenCount--;
            base.OnFormClosed(e);
        }

        private void Accept()
        {
            _result = _box.Text;
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>返回编辑后的文本；取消返回 null</summary>
        internal static string Edit(IWin32Window owner, string title, string initial)
        {
            using (var f = new TextEditorForm(title, initial))
            {
                if (f.ShowDialog(owner) == DialogResult.OK) return f._result;
                return null;
            }
        }
    }

    /// <summary>把协同面板里的多行文本框改成“点击弹出编辑窗口”</summary>
    internal static class ExternalFields
    {
        private static readonly System.Collections.Generic.HashSet<Control> Hooked =
            new System.Collections.Generic.HashSet<Control>();

        // 可编辑守卫：业务方按控件注册；已注册且返回 false 时点击不弹编辑窗（防止在锁定期改内容）
        internal static readonly System.Collections.Generic.Dictionary<Control, System.Func<bool>> Guards =
            new System.Collections.Generic.Dictionary<Control, System.Func<bool>>();

        internal static void Attach(Control root)
        {
            // 面板的子控件是分批创建的：挂上 ControlAdded，之后新增的控件也会被处理
            if (Hooked.Add(root))
                root.ControlAdded += delegate { Attach(root); };

            foreach (Control c in root.Controls)
            {
                var tb = c as TextBoxBase;
                if (tb != null && tb.Multiline && (tb.Tag as string) != "ext")
                {
                    tb.Tag = "ext";
                    Program.Log("editor hook: " + tb.GetType().Name + " parent=" + (tb.Parent != null ? tb.Parent.GetType().Name : "?"));
                    tb.ReadOnly = true;
                    tb.BackColor = Color.FromArgb(252, 252, 253);
                    tb.Cursor = Cursors.Hand;
                    tb.ShortcutsEnabled = true;
                    tb.Click += delegate
                    {
                        System.Func<bool> guard;
                        if (Guards.TryGetValue(tb, out guard) && guard != null && !guard()) return;   // 当前阶段不可编辑，不弹窗
                        Program.Log("editor open title=" + TitleOf(tb) + " owner=" + (tb.FindForm() != null));
                        string s = TextEditorForm.Edit(tb.FindForm(), TitleOf(tb), tb.Text);
                        Program.Log("editor closed result=" + (s == null ? "null" : s.Length.ToString()));
                        // Modified=true：让输出框 Leave 时的 CommitEdit 把弹窗编辑提交进流水线，否则下次刷新会把它冲掉
                        if (s != null && s != tb.Text) { tb.Text = s; tb.Modified = true; }
                    };
                }
                else
                {
                    Attach(c);
                }
            }
        }

        private static string TitleOf(Control c)
        {
            // 用最近的 Label 文字作为窗口标题，没有就用类型名
            Control p = c.Parent;
            if (p != null)
                foreach (Control sib in p.Controls)
                {
                    var lb = sib as Label;
                    if (lb != null && lb.Text.Length > 0) return lb.Text.Trim();
                }
            return "编辑内容";
        }
    }

    /// <summary>当前是否正在输入（文本编辑窗 / agent 对话窗开着，或任何窗口里有文本框聚焦打字）。是就绝不能抢前台，否则中文输入法候选窗会被打断。</summary>
    internal static class InputUiBusy
    {
        internal static bool Busy()
        {
            if (TextEditorForm.OpenCount > 0) return true;
            try
            {
                for (int i = 0; i < AgentDialog.Open.Count; i++)
                    if (AgentDialog.Open[i].Visible) return true;
            }
            catch { }
            // 就地输入也算忙：任何窗口里有文本框正聚焦（含协同面板内直接打字），定时器不许抢前台
            try
            {
                foreach (Form f in Application.OpenForms)
                    if (HasFocusedText(f)) return true;
            }
            catch { }
            return false;
        }

        private static bool HasFocusedText(Control root)
        {
            foreach (Control c in root.Controls)
            {
                var tb = c as TextBoxBase;
                if (tb != null && tb.Focused) return true;
                if (c.HasChildren && HasFocusedText(c)) return true;
            }
            return false;
        }
    }
}
