using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace AgentHub
{
    // ================= 配置层 =================

    public class PipeStep
    {
        public string Role { get; set; }
        public string Agent { get; set; }
        public string Prompt { get; set; }
    }

    public class PipeConfig
    {
        public int MaxRounds { get; set; }          // 安全上限：达到后「继续」按钮拒绝，只能结束（防失控）
        public int StepTimeoutMinutes { get; set; }
        public bool AutoAdvance { get; set; }       // 步级接力是否自动确认；轮级永远停在用户确认处
        public List<PipeStep> Steps { get; set; }

        // 固定四段式：拆解(Agent1) → 执行(Agent2) → 自检(Agent3) → 对齐(回到Agent1)
        // config 只改提示词，不改结构
        public static PipeConfig Defaults()
        {
            var c = new PipeConfig();
            c.MaxRounds = 10;
            c.StepTimeoutMinutes = 10;
            c.AutoAdvance = false;
            c.Steps = new List<PipeStep>();
            c.Steps.Add(Step("拆解", "A1",
                "请拆解下面的需求，明确：① 目标 ② 执行步骤 ③ 可判断的验收标准（每条都要能验证「达成/未达成」）。\n\n【需求】{{task}}{{history}}{{additions}}"));
            c.Steps.Add(Step("执行", "A2",
                "请按下面的拆解结果执行任务。完成后返回：产物、完成项、未完成项、遇到的问题。\n\n【拆解结果】{{decomposition}}{{additions}}"));
            c.Steps.Add(Step("自检", "A3",
                "请依据验收标准逐项检查执行结果。对无法验证的内容必须标注「未验证」，不得仅凭执行方声称通过。\n\n【原始需求】{{task}}\n【验收标准】{{decomposition}}\n【执行结果】{{execution}}{{additions}}"));
            c.Steps.Add(Step("对齐", "A1",
                "请对照原始需求做最终对齐，输出：本轮总结、剩余问题、下一轮建议。第一行写「满足」或「不满足」。\n\n【原始需求】{{task}}\n【执行结果】{{execution}}\n【自检结果】{{selfcheck}}{{additions}}"));
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
                if (c.MaxRounds <= 0) c.MaxRounds = 10;
                if (c.StepTimeoutMinutes <= 0) c.StepTimeoutMinutes = 10;
                return c;
            }
            catch { return Defaults(); }
        }
    }

    // ================= 数据结构（交接与会话） =================

    // 对话消息：Kind 0 = 交给 agent 的提示词；1 = agent 的回复；2 = 用户补充
    public class ChatMsg
    {
        public int Kind;
        public string Text;
        public DateTime Time;
    }

    // 用户补充需求：来源（哪个角色）、时间、生效状态，缺一不可
    public class Supplement
    {
        public string Text;
        public string AgentId;
        public string RoleName;     // 来源角色（拆解/执行/自检）
        public DateTime Time;
        public bool Applied;        // 是否已进入某次调用
        public string RoundTag;     // 生效于第几轮
    }

    // 一轮的完整记录。最终对齐属于本轮，不算新轮（Number 在对齐完成时才写入）
    public class RoundRecord
    {
        public int Number;
        public string Decomposition;   // Agent 1 拆解（含验收标准）
        public string Execution;       // Agent 2 执行结果
        public string SelfCheck;       // Agent 3 自检（未验证项须标注）
        public string Alignment;       // Agent 1 对齐：本轮总结/剩余问题/下一轮建议
        public string UserAddition;    // 本轮生效的用户补充汇总
        public DateTime FinishedAt;
    }

    // 会话：从「开始协同」到「结束协同」之间的全部内容
    public class PipeSession
    {
        public string Task = "";
        public readonly List<RoundRecord> Rounds = new List<RoundRecord>();
        public readonly List<Supplement> Supplements = new List<Supplement>();
        public readonly Dictionary<string, List<ChatMsg>> Chats = new Dictionary<string, List<ChatMsg>>();
        public DateTime StartedAt;
        public bool Ended;
    }

    public enum PipePhase { Idle, Decompose, Execute, SelfCheck, Align, RoundDone, Stopped, Failed, Finished }

    // ================= 调度层（纯逻辑，不碰窗口） =================

    // 状态机要点（对应验收场景）：
    // - 只有 Align 完成才计数：RoundRecord 只在那一刻进入 Session.Rounds
    // - RoundDone 必停：ContinueRound 只能从 RoundDone 发起，绝不自动循环
    // - 失败/停止不算轮次：Fail/Stop 不触碰 Session.Rounds
    // - 迟到响应忽略：Advance 有 Running + !ConfirmPending 双重闸门
    // - 重复启动拒绝：Start 对空需求/未结束会话直接拒绝
    public class Pipeline
    {
        public PipeConfig Config;
        public PipeSession Session;
        public PipePhase Phase = PipePhase.Idle;
        public RoundRecord CurrentRound;   // 本轮工作副本，对齐完成前不进 Rounds
        public bool ConfirmPending;        // 步级接力待人工确认
        public bool Paused;                // 超时暂停（沿用旧交互）
        public string FailedReason = "";
        public string Note = "";

        private readonly List<Supplement> _pending = new List<Supplement>();
        private PipePhase _lastRunPhase = PipePhase.Idle;

        public Pipeline(PipeConfig cfg) { Config = cfg; }

        public int StepCount { get { return Config.Steps.Count; } }

        public bool Running
        {
            get
            {
                return Phase == PipePhase.Decompose || Phase == PipePhase.Execute ||
                       Phase == PipePhase.SelfCheck || Phase == PipePhase.Align;
            }
        }

        public int CompletedRounds { get { return Session != null ? Session.Rounds.Count : 0; } }

        // 当前轮次 = 已完成 + 1（进行中/待确认/停止/失败时）；未开始或已结束则显示已完成数
        public int CurrentRoundNo
        {
            get
            {
                if (Running || Phase == PipePhase.RoundDone || Phase == PipePhase.Stopped || Phase == PipePhase.Failed)
                    return CompletedRounds + 1;
                return CompletedRounds;
            }
        }

        public int StepIndex
        {
            get
            {
                switch (Phase)
                {
                    case PipePhase.Execute: return 1;
                    case PipePhase.SelfCheck: return 2;
                    case PipePhase.Align: return StepCount - 1;
                    default: return 0;
                }
            }
        }

        public PipeStep Current
        {
            get { return StepIndex >= 0 && StepIndex < StepCount ? Config.Steps[StepIndex] : null; }
        }

        public string PhaseName
        {
            get
            {
                switch (Phase)
                {
                    case PipePhase.Decompose: return "需求拆解";
                    case PipePhase.Execute: return "任务执行";
                    case PipePhase.SelfCheck: return "完成自检";
                    case PipePhase.Align: return "最终对齐";
                    case PipePhase.RoundDone: return "等待确认";
                    case PipePhase.Stopped: return "已停止";
                    case PipePhase.Failed: return "已失败";
                    case PipePhase.Finished: return "已结束";
                    default: return "未开始";
                }
            }
        }

        // 给界面状态显示用：停止/失败时显示的是"当时卡在哪一步"
        public PipePhase RunPhaseForStatus
        {
            get { return Running ? Phase : _lastRunPhase; }
        }

        public string CurrentPrompt()
        {
            return ComposeFor(StepIndex);
        }

        // 开始会话。返回 null = 成功；否则返回失败原因（界面直接展示）
        public string Start(string task)
        {
            if (Session != null && !Session.Ended) return "会话已在进行，先结束再开始";
            if (string.IsNullOrWhiteSpace(task)) return "需求为空，先填写任务内容";
            Session = new PipeSession();
            Session.Task = task.Trim();
            Session.StartedAt = DateTime.Now;
            Session.Ended = false;
            CurrentRound = new RoundRecord();
            Phase = PipePhase.Decompose;
            _lastRunPhase = Phase;
            ConfirmPending = false;
            FailedReason = "";
            Note = "第 1 轮 · 拆解中";
            return null;
        }

        // 拼某一步的提示词；顺带把排队中的补充需求打进去并标记生效
        public string ComposeFor(int stepIdx)
        {
            if (Session == null || stepIdx < 0 || stepIdx >= StepCount) return "";
            string p = Config.Steps[stepIdx].Prompt ?? "";
            RoundRecord r = CurrentRound;
            p = p.Replace("{{task}}", Session.Task ?? "");
            p = p.Replace("{{decomposition}}", r != null ? (r.Decomposition ?? "") : "");
            p = p.Replace("{{execution}}", r != null ? (r.Execution ?? "") : "");
            p = p.Replace("{{selfcheck}}", r != null ? (r.SelfCheck ?? "") : "");
            p = p.Replace("{{input}}", OutputOf(stepIdx - 1));
            p = p.Replace("{{history}}", HistoryText());
            p = p.Replace("{{additions}}", DrainAdditions());
            return p;
        }

        string OutputOf(int stepIdx)
        {
            RoundRecord r = CurrentRound;
            if (r == null) return "";
            switch (stepIdx)
            {
                case 0: return r.Decomposition ?? "";
                case 1: return r.Execution ?? "";
                case 2: return r.SelfCheck ?? "";
                default: return "";
            }
        }

        // 下一轮携带：此前成果、自检问题、对齐结论、用户补充
        string HistoryText()
        {
            if (Session == null || Session.Rounds.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (RoundRecord r in Session.Rounds)
            {
                sb.Append("\n【第 ").Append(r.Number).Append(" 轮】");
                if (!string.IsNullOrEmpty(r.Execution)) sb.Append("\n  执行结果：").Append(Cut(r.Execution, 400));
                if (!string.IsNullOrEmpty(r.SelfCheck)) sb.Append("\n  自检发现：").Append(Cut(r.SelfCheck, 400));
                if (!string.IsNullOrEmpty(r.Alignment)) sb.Append("\n  对齐结论：").Append(Cut(r.Alignment, 400));
            }
            sb.Append("\n（以上结果不要再重复执行，直接在其基础上继续）");
            return sb.ToString();
        }

        static string Cut(string s, int n)
        {
            s = (s ?? "").Replace("\r", " ").Trim();
            return s.Length > n ? s.Substring(0, n) + "…" : s;
        }

        // 用户补充：来源/时间/生效状态写全。返回生效时机提示给界面
        public Supplement AddSupplement(string agentId, string roleName, string text)
        {
            if (Session == null || Session.Ended || string.IsNullOrWhiteSpace(text)) return null;
            var s = new Supplement
            {
                Text = text.Trim(),
                AgentId = agentId ?? "",
                RoleName = roleName ?? "",
                Time = DateTime.Now,
                Applied = false
            };
            Session.Supplements.Add(s);
            _pending.Add(s);
            LogChat(agentId, 2, s.Text);
            return s;
        }

        public string SupplementHint()
        {
            if (Phase == PipePhase.RoundDone) return "已加入队列，下一轮拆解时生效";
            if (Phase == PipePhase.Idle || Phase == PipePhase.Finished) return "已加入队列，下一轮开始时生效";
            return "已加入队列，将在下一次交接时生效";
        }

        // 拼提示词时把队列清空：补充即生效
        string DrainAdditions()
        {
            if (_pending.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (Supplement s in _pending)
            {
                sb.Append("\n【用户补充 · 来自").Append(s.RoleName).Append(" · ")
                  .Append(s.Time.ToString("HH:mm")).Append("】").Append(s.Text);
                s.Applied = true;
                s.RoundTag = "第 " + CurrentRoundNo + " 轮";
            }
            _pending.Clear();
            return sb.ToString();
        }

        string JoinAppliedAdditions()
        {
            var sb = new StringBuilder();
            foreach (Supplement s in Session.Supplements)
                if (s.Applied && s.RoundTag == "第 " + (CompletedRounds + 1) + " 轮")
                    sb.Append(sb.Length > 0 ? "；" : "").Append(s.Text);
            return sb.ToString();
        }

        public void LogChat(string agentId, int kind, string text)
        {
            if (Session == null || string.IsNullOrEmpty(agentId)) return;
            List<ChatMsg> list;
            if (!Session.Chats.TryGetValue(agentId, out list))
            {
                list = new List<ChatMsg>();
                Session.Chats[agentId] = list;
            }
            list.Add(new ChatMsg { Kind = kind, Text = text ?? "", Time = DateTime.Now });
        }

        // 拿到这一步的输出。迟到响应直接返回 false（Running/ConfirmPending 闸门）
        public bool Advance(string output)
        {
            if (!Running || ConfirmPending) return false;
            Paused = false;
            RoundRecord r = CurrentRound;
            if (r == null) return false;

            switch (Phase)
            {
                case PipePhase.Decompose: r.Decomposition = output; break;
                case PipePhase.Execute: r.Execution = output; break;
                case PipePhase.SelfCheck: r.SelfCheck = output; break;
                case PipePhase.Align: r.Alignment = output; break;
            }

            if (Phase == PipePhase.Align)
            {
                // 最终对齐完成 → 本轮计数 +1，停在用户确认处
                r.Number = CompletedRounds + 1;
                r.UserAddition = JoinAppliedAdditions();
                r.FinishedAt = DateTime.Now;
                Session.Rounds.Add(r);
                CurrentRound = null;
                Phase = PipePhase.RoundDone;
                Note = "第 " + r.Number + " 轮已完成，等待确认";
                return true;
            }

            ConfirmPending = true;
            Note = "「" + Config.Steps[StepIndex].Role + "」已完成，待确认交给「" + Config.Steps[StepIndex + 1].Role + "」";
            return true;
        }

        // 人工确认交接：返回下一步提示词（此时才拼，队列里的补充能赶上）
        public string Confirm()
        {
            if (!Running || !ConfirmPending) return null;
            ConfirmPending = false;
            switch (Phase)
            {
                case PipePhase.Decompose: Phase = PipePhase.Execute; break;
                case PipePhase.Execute: Phase = PipePhase.SelfCheck; break;
                case PipePhase.SelfCheck: Phase = PipePhase.Align; break;
                default: return null;
            }
            _lastRunPhase = Phase;
            Note = "第 " + CurrentRoundNo + " 轮 · " + PhaseName + "中";
            return ComposeFor(StepIndex);
        }

        // 轮末「继续下一轮」：只能从这里进入新轮，返回第 1 步提示词
        public string ContinueRound()
        {
            if (Phase != PipePhase.RoundDone) return null;
            if (CompletedRounds >= Config.MaxRounds) return null;   // 安全上限
            CurrentRound = new RoundRecord();
            Phase = PipePhase.Decompose;
            _lastRunPhase = Phase;
            Note = "第 " + CurrentRoundNo + " 轮 · 拆解中";
            return ComposeFor(StepIndex);
        }

        // 停止：不算失败，不计轮次，可恢复
        public void Stop()
        {
            if (!Running) return;
            _lastRunPhase = Phase;
            Phase = PipePhase.Stopped;
            Note = "已停止（点「恢复」继续）";
        }

        public string Resume()
        {
            if (Phase != PipePhase.Stopped) return null;
            Phase = _lastRunPhase;
            ConfirmPending = false;
            Note = "已恢复 · " + PhaseName;
            return ComposeFor(StepIndex);
        }

        // 调用失败：停止推进，展示原因，轮次不变；Retry 重新交付当前步提示词（无副作用）
        public void Fail(string reason)
        {
            if (!Running) return;
            _lastRunPhase = Phase;
            Phase = PipePhase.Failed;
            FailedReason = reason ?? "";
            Note = "失败：" + FailedReason;
        }

        public string Retry()
        {
            if (Phase != PipePhase.Failed) return null;
            Phase = _lastRunPhase;
            ConfirmPending = false;
            FailedReason = "";
            Note = "重试 · " + PhaseName;
            return ComposeFor(StepIndex);
        }

        // 结束：保留会话记录供查看（轮次列表 + 对话都在 Session 里，并落盘 .scratch）
        public void Finish()
        {
            if (Session != null) Session.Ended = true;
            Phase = PipePhase.Finished;
            Note = "协同已结束";
        }
    }
}
