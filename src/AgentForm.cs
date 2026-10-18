using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace AgentHub
{
    internal class AgentForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(11, 11, 13);
        private static readonly Color Fg = Color.FromArgb(232, 232, 236);
        private static readonly Color Dim = Color.FromArgb(126, 126, 136);
        private static readonly Color Orange = Color.FromArgb(255, 122, 26);

        private readonly TextBox _name = new TextBox();
        private readonly TextBox _path = new TextBox();
        private readonly ListBox _list = new ListBox();
        private readonly Button _ok = new Button();
        private readonly Button _cancel = new Button();
        private readonly Button _browse = new Button();
        private readonly List<Host.WindowInfo> _windows;

        private Host.WindowInfo _picked;
        private readonly Agent _editing;
        public Agent Result;

        private AgentForm(Agent editing)
        {
            _editing = editing;
            _windows = Host.Windows();

            Text = editing == null ? "添加应用" : "编辑应用";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(552, 400);
            BackColor = Bg;
            ForeColor = Fg;

            var l1 = MakeLabel("名称", 16, 18);
            _name.SetBounds(76, 14, 458, 26);
            _name.BackColor = Color.FromArgb(20, 20, 24);
            _name.ForeColor = Fg;
            _name.BorderStyle = BorderStyle.FixedSingle;
            _name.Text = editing != null ? editing.Name : "";

            var l2 = MakeLabel("从正在运行的窗口里挑（可选）", 16, 52);

            _list.SetBounds(16, 76, 518, 150);
            _list.BackColor = Color.FromArgb(18, 18, 22);
            _list.ForeColor = Fg;
            _list.BorderStyle = BorderStyle.FixedSingle;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.ItemHeight = 26;
            _list.DrawItem += DrawItem;
            _list.SelectedIndexChanged += ListChanged;
            foreach (Host.WindowInfo w in _windows) _list.Items.Add(w);

            var l3 = MakeLabel("或程序路径", 16, 240);
            _path.SetBounds(16, 262, 428, 26);
            _path.BackColor = Color.FromArgb(20, 20, 24);
            _path.ForeColor = Fg;
            _path.BorderStyle = BorderStyle.FixedSingle;
            _path.Text = editing != null ? editing.Exe : "";

            _browse.SetBounds(452, 260, 82, 28);
            _browse.Text = "浏览…";
            StyleButton(_browse, false);
            _browse.Click += BrowseClick;

            _cancel.SetBounds(378, 340, 78, 32);
            _cancel.Text = "取消";
            StyleButton(_cancel, false);
            _cancel.Click += delegate { Close(); };

            _ok.SetBounds(464, 340, 78, 32);
            _ok.Text = editing == null ? "添加" : "保存";
            StyleButton(_ok, true);
            _ok.Click += OkClick;

            Controls.Add(l1);
            Controls.Add(_name);
            Controls.Add(l2);
            Controls.Add(_list);
            Controls.Add(l3);
            Controls.Add(_path);
            Controls.Add(_browse);
            Controls.Add(_cancel);
            Controls.Add(_ok);

            AcceptButton = _ok;
            CancelButton = _cancel;
        }

        private static Label MakeLabel(string text, int x, int y)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Location = new Point(x, y);
            l.ForeColor = Dim;
            return l;
        }

        private static void StyleButton(Button b, bool primary)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.BorderColor = primary ? Orange : Color.FromArgb(52, 52, 60);
            b.BackColor = primary ? Orange : Color.FromArgb(20, 20, 24);
            b.ForeColor = primary ? Bg : Fg;
        }

        private void DrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var w = (Host.WindowInfo)_list.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Color.FromArgb(255, 122, 26) : Color.FromArgb(18, 18, 22)))
                e.Graphics.FillRectangle(b, e.Bounds);

            string text = w.Title + "   ·   " + w.Proc;
            TextRenderer.DrawText(e.Graphics, text, _list.Font,
                new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height),
                sel ? Bg : Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void ListChanged(object sender, EventArgs e)
        {
            if (_list.SelectedIndex < 0) return;
            _picked = (Host.WindowInfo)_list.Items[_list.SelectedIndex];

            string t = _picked.Title;
            int dash = t.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0) t = t.Substring(0, dash);
            if (t.Length > 40) t = t.Substring(0, 40);
            _name.Text = t;
            if (!string.IsNullOrEmpty(_picked.Exe)) _path.Text = _picked.Exe;
        }

        private void BrowseClick(object sender, EventArgs e)
        {
            using (var d = new OpenFileDialog())
            {
                d.Filter = "程序 (*.exe)|*.exe|所有文件 (*.*)|*.*";
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    _path.Text = d.FileName;
                    if (_name.Text.Trim().Length == 0)
                        _name.Text = Path.GetFileNameWithoutExtension(d.FileName);
                }
            }
        }

        private void OkClick(object sender, EventArgs e)
        {
            string name = _name.Text.Trim();
            string path = _path.Text.Trim();
            if (name.Length == 0) { MessageBox.Show("请填写名称", "添加应用"); return; }
            if (path.Length == 0) { MessageBox.Show("请选择窗口，或填写程序路径", "添加应用"); return; }

            var a = _editing ?? new Agent();
            a.Name = name;
            a.Short = name.Length > 2 ? name.Substring(0, 2) : name;
            a.Kind = "exe";
            a.Exe = path;
            if (_picked != null)
            {
                a.Proc = _picked.Proc;
                a.Title = _picked.Title;
            }
            else
            {
                string fn = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrEmpty(a.Proc)) a.Proc = fn;
                if (string.IsNullOrEmpty(a.Title)) a.Title = name;
            }
            if (_editing == null)
            {
                a.Id = "u" + DateTime.Now.Ticks.ToString("x");
                a.Custom = true;
            }

            Result = a;
            DialogResult = DialogResult.OK;
            Close();
        }

        public static Agent Show(IWin32Window owner, Agent editing)
        {
            using (var f = new AgentForm(editing))
            {
                return f.ShowDialog(owner) == DialogResult.OK ? f.Result : null;
            }
        }
    }
}
