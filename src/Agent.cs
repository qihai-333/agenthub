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
            public List<string> Order { get; set; }
        }

        public static string SessionPath
        {
            get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "session.json"); }
        }

        public static void LoadSession(out List<string> chosen, out string shown, out List<string> order)
        {
            chosen = new List<string>();
            order = new List<string>();
            shown = null;
            try
            {
                if (!File.Exists(SessionPath)) return;
                var ser = new JavaScriptSerializer();
                SessionState st = ser.Deserialize<SessionState>(File.ReadAllText(SessionPath, Encoding.UTF8));
                if (st != null)
                {
                    if (st.Chosen != null) chosen = st.Chosen;
                    if (st.Order != null) order = st.Order;
                    shown = st.Shown;
                }
            }
            catch { }
        }

        public static void SaveSession(IEnumerable<string> chosen, string shown, IEnumerable<string> order)
        {
            try
            {
                var st = new SessionState();
                st.Chosen = new List<string>(chosen);
                st.Shown = shown;
                st.Order = order != null ? new List<string>(order) : new List<string>();
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
            // 别人的机器上这些路径不存在，硬编码一堆失效条目只会更迷惑。
            // 没有配置时从空白开始，用侧栏的「+」自己添加。
            return new List<Agent>();
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
