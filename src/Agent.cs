using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace AgentHub
{
    public class Agent
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Short { get; set; }
        public string Kind { get; set; }
        public string Exe { get; set; }
        public string Args { get; set; }
        public string Url { get; set; }
        public string Proc { get; set; }
        public string Title { get; set; }
        public string Frame { get; set; }
        public bool KeepAlive { get; set; }
        public bool Custom { get; set; }

        public bool OffsetFrame
        {
            get { return string.Equals(Frame, "offset", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsUrl
        {
            get { return string.Equals(Kind, "url", StringComparison.OrdinalIgnoreCase); }
        }

        public string[] ProcNames()
        {
            if (!string.IsNullOrEmpty(Proc))
            {
                return Proc.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            }
            if (!string.IsNullOrEmpty(Exe) && !IsUrl)
            {
                return new string[] { System.IO.Path.GetFileNameWithoutExtension(Exe) };
            }
            return new string[0];
        }
    }

    public static class Config
    {
        public static string ConfigPath
        {
            get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "agents.json"); }
        }

        public static string CustomPath
        {
            get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "custom.json"); }
        }

        public class SessionState
        {
            public List<string> Chosen { get; set; }
            public string Shown { get; set; }
        }

        public static string SessionPath
        {
            get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "session.json"); }
        }

        public static void LoadSession(out List<string> chosen, out string shown)
        {
            chosen = new List<string>();
            shown = null;
            try
            {
                if (!File.Exists(SessionPath)) return;
                var ser = new JavaScriptSerializer();
                SessionState st = ser.Deserialize<SessionState>(File.ReadAllText(SessionPath, Encoding.UTF8));
                if (st != null)
                {
                    if (st.Chosen != null) chosen = st.Chosen;
                    shown = st.Shown;
                }
            }
            catch { }
        }

        public static void SaveSession(IEnumerable<string> chosen, string shown)
        {
            try
            {
                var st = new SessionState();
                st.Chosen = new List<string>(chosen);
                st.Shown = shown;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SessionPath));
                File.WriteAllText(SessionPath, new JavaScriptSerializer().Serialize(st), Encoding.UTF8);
            }
            catch { }
        }

        public static readonly List<Agent> Custom = new List<Agent>();

        public static List<Agent> LoadCustom()
        {
            Custom.Clear();
            try
            {
                if (!File.Exists(CustomPath)) return Custom;
                var ser = new JavaScriptSerializer();
                var list = ser.Deserialize<List<Agent>>(File.ReadAllText(CustomPath, Encoding.UTF8));
                if (list != null)
                {
                    foreach (Agent a in list)
                    {
                        a.Custom = true;
                        Custom.Add(a);
                    }
                }
            }
            catch { }
            return Custom;
        }

        public static void SaveCustom()
        {
            try
            {
                var ser = new JavaScriptSerializer();
                File.WriteAllText(CustomPath, ser.Serialize(Custom), Encoding.UTF8);
            }
            catch { }
        }

        public static List<Agent> LoadMerged(string path)
        {
            List<Agent> builtin = Load(path);
            LoadCustom();
            foreach (Agent c in Custom)
            {
                bool dup = false;
                foreach (Agent b in builtin) if (b.Id == c.Id) dup = true;
                if (!dup) builtin.Add(c);
            }
            return builtin;
        }

        public static List<Agent> Load()
        {
            return Load(null);
        }

        public static List<Agent> Load(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) path = ConfigPath;
                if (path == null || !File.Exists(path)) return Defaults();
                string json = File.ReadAllText(path, Encoding.UTF8);
                var ser = new JavaScriptSerializer();
                var list = ser.Deserialize<List<Agent>>(json);
                if (list == null || list.Count == 0) return Defaults();
                return list;
            }
            catch
            {
                return Defaults();
            }
        }

        public static List<Agent> Defaults()
        {
            var l = new List<Agent>();
            l.Add(Make("dsh", "DSH 桌面", "DS", "exe", @"D:\dsh-zhuomian\DeepSeek Harness.exe", "DeepSeek Harness"));
            l.Add(Make("dshweb", "DSH 网页", "DW", "url", "", "msedge"));
            l.Add(Make("doubao", "豆包", "豆", "exe", @"D:\Doubao\Doubao.exe", "Doubao"));
            l.Add(Make("trae", "Trae", "TR", "exe", @"D:\Trae CN\Trae CN.exe", "Trae CN"));
            l.Add(Make("qoder", "Qoder", "QD", "exe", @"C:\Users\zhangdy\AppData\Local\Qoder CN\Qoder CN Launcher\Qoder CN Launcher.exe", "Qoder CN,Qoder CN Launcher"));
            l.Add(Make("workbuddy", "WorkBuddy", "WB", "exe", @"D:\workbuddy\WorkBuddy.exe", "WorkBuddy"));
            l.Add(Make("codex", "Codex", "CX", "exe", @"C:\Users\zhangdy\Desktop\ChatGPT - 快捷方式.lnk", "ChatGPT"));
            l.Add(Make("tabbit", "Tabbit", "TB", "exe", @"D:\Tabbit Browser\Application\Tabbit Browser.exe", "Tabbit Browser"));
            l.Add(Make("ccswitch", "CC Switch", "CC", "exe", @"C:\Users\zhangdy\AppData\Local\Programs\cc-switch.exe", "cc-switch"));
            return l;
        }

        private static Agent Make(string id, string name, string sh, string kind, string exe, string proc)
        {
            var a = new Agent();
            a.Id = id;
            a.Name = name;
            a.Short = sh;
            a.Kind = kind;
            a.Exe = exe;
            a.Proc = proc;
            return a;
        }
    }
}
