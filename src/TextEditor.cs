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

        private TextEditorForm(string title, string initial)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
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
        internal static void Attach(Control root)
        {
            foreach (Control c in root.Controls)
            {
                var tb = c as TextBoxBase;
                if (tb != null && tb.Multiline && (tb.Tag as string) != "ext")
                {
                    tb.Tag = "ext";
                    tb.ReadOnly = true;
                    tb.BackColor = Color.FromArgb(252, 252, 253);
                    tb.Cursor = Cursors.Hand;
                    tb.ShortcutsEnabled = true;
                    tb.Click += delegate
                    {
                        string s = TextEditorForm.Edit(tb.FindForm(), TitleOf(tb), tb.Text);
                        if (s != null && s != tb.Text) tb.Text = s;
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
}
