using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace AgentHub
{
    public class PipeStep
    {
        public string Role { get; set; }
        public string Agent { get; set; }
        public string Prompt { get; set; }
    }

    public class PipeConfig
    {
        public int MaxRounds { get; set; }
        public int StepTimeoutMinutes { get; set; }
        public List<PipeStep> Steps { get; set; }

        public static PipeConfig Defaults()
        {
            var c = new PipeConfig();
            c.MaxRounds = 3;
            c.StepTimeoutMinutes = 10;
            c.Steps = new List<PipeStep>();
            c.Steps.Add(Step("拆解", "dsh", "把这个需求拆成可直接编码的任务，列出要改的文件和步骤：\n\n{{task}}"));
            c.Steps.Add(Step("编码", "trae", "按下面的任务写代码，只输出代码和必要说明：\n\n{{input}}"));
            c.Steps.Add(Step("审查", "qoder", "审查下面的代码是否满足任务要求，逐条指出问题，最后给出结论：\n\n{{input}}"));
            c.Steps.Add(Step("判定", "dsh", "结合审查意见判断任务是否完成。第一行只写「满足」或「不满足」，然后说明理由：\n\n{{input}}"));
            return c;
        }

        static PipeStep Step(string role, string agent, string prompt)
        {
            var s = new PipeStep();
            s.Role = role;
            s.Agent = agent;
            s.Prompt = prompt;
            return s;
        }

        public static string PathOf()
        {
            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pipeline.json");
        }

        public static PipeConfig Load()
        {
            try
            {
                string p = PathOf();
                if (!File.Exists(p))
                {
                    var d = Defaults();
                    File.WriteAllText(p, new JavaScriptSerializer().Serialize(d), Encoding.UTF8);
                    return d;
                }
                var ser = new JavaScriptSerializer();
                var c = ser.Deserialize<PipeConfig>(File.ReadAllText(p, Encoding.UTF8));
                if (c == null || c.Steps == null || c.Steps.Count == 0) return Defaults();
                if (c.MaxRounds <= 0) c.MaxRounds = 3;
                if (c.StepTimeoutMinutes <= 0) c.StepTimeoutMinutes = 10;
                return c;
            }
            catch { return Defaults(); }
        }
    }

    // 流水线状态：只做"到第几步、第几轮、拼提示词"这些纯逻辑，不碰窗口
    public class Pipeline
    {
        public PipeConfig Config;
        public string Task = "";
        public string LastOutput = "";
        public int Round = 1;
        public int Index;          // 当前步（0 起）
        public bool Running;
        public bool Paused;
        public bool Done;
        public bool Aborted;
        public string Note = "";

        public Pipeline(PipeConfig cfg) { Config = cfg; }

        public int StepCount { get { return Config.Steps.Count; } }

        public PipeStep Current
        {
            get
            {
                if (Index < 0 || Index >= StepCount) return null;
                return Config.Steps[Index];
            }
        }

        public bool IsJudgeStep { get { return Index == StepCount - 1; } }

        public void Start(string task)
        {
            Task = task ?? "";
            LastOutput = "";
            Round = 1;
            Index = 0;
            Running = true;
            Paused = false;
            Done = false;
            Aborted = false;
            Note = "已开始第 1 轮";
        }

        // 把当前步的提示词拼出来
        public string ComposePrompt()
        {
            PipeStep s = Current;
            if (s == null) return "";
            string p = s.Prompt ?? "";
            p = p.Replace("{{task}}", Task);
            p = p.Replace("{{input}}", LastOutput);
            return p;
        }

        // 拿到这一步的输出，推进状态机
        public void Advance(string output)
        {
            if (!Running) return;
            Paused = false;
            LastOutput = output ?? "";

            if (IsJudgeStep)
            {
                bool satisfied = Judge(LastOutput);
                if (satisfied)
                {
                    Running = false;
                    Done = true;
                    Note = "第 " + Round + " 轮判定满足，任务完成";
                    return;
                }
                if (Round >= Config.MaxRounds)
                {
                    Running = false;
                    Aborted = true;
                    Note = "已跑满 " + Config.MaxRounds + " 轮仍未通过";
                    return;
                }
                Round++;
                Index = 0;
                Note = "判定不满足，进入第 " + Round + " 轮";
                return;
            }

            Index++;
            Note = "第 " + Round + " 轮 · 第 " + (Index + 1) + " 步";
        }

        static bool Judge(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string head = text.TrimStart();
            if (head.Length > 12) head = head.Substring(0, 12);
            if (head.IndexOf("不满足") >= 0) return false;
            if (head.IndexOf("满足") >= 0) return true;
            // 没写结论时按"不满足"处理，宁可多跑一轮也不误判完成
            return text.IndexOf("不满足") < 0 && text.IndexOf("满足") >= 0;
        }
    }
}
