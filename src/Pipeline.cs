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
        public bool AutoAdvance { get; set; }       // 旧字段，保留兼容 pipeline.json；审批模式以 Pipeline.Mode 为准
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
                "请按下面的拆解结果执行任务。完成后返回：产物、完成项、未完成项、遇到的问题。\n\n【原始需求】{{task}}\n【拆解结果与验收标准】{{decomposition}}{{additions}}"));
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

    // 阶段内容状态：草稿(尚未发送/未收到回复) → 运行中(已发送等待回复) → 待审批 → 已传递；失败单列
    public enum StageState { Draft, Running, AwaitingApproval, HandedOff, Failed }

    // 审批模式：手动 = 每阶段输出后暂停等用户「批准并传递」；自动 = 输出有效即自动交接。
    // 切换只影响下一个尚未发生的交接点，绝不重发已完成步骤、不中断当前调用。
    public enum ApprovalMode { Manual, Auto }

    // 一次发送的快照（重跑当前棒时旧记录进历史，不被覆盖）
    public class StageAttempt
    {
        public string SentPrompt;    // 发送时的完整提示词快照（之后任何编辑都不改它）
        public DateTime SentAt;
        public string RawReply;      // Agent 原始回复（只读，供对照）
        public DateTime ReplyAt;
    }

    // 一个阶段的完整记录：左侧输入 / 发送快照 / 原始回复 / 用户编辑后的交接版本，四者分离。
    // 交接只以 HandoffText（编辑版）为准；HandoffEdited 记录用户是否改过。
    public class StageRecord
    {
        public string Role = "";
        public string AgentId = "";

        // 左侧框内容：发送前可编辑/补充。开始=原始需求；之后=上一棒交接过来的版本，用户可再改。
        public string PreparedInput = "";

        public string SentPrompt = "";
        public DateTime SentAt;
        public string RawReply = "";
        public DateTime ReplyAt;

        // 右侧框内容：回复到达时 = RawReply，用户可改；批准/交接后冻结为交接依据。
        public string HandoffText = "";
        public bool HandoffEdited;

        public StageState Status = StageState.Draft;
        public int AttemptCount;                       // 当前是第几次生成（>1 表示重跑过）
        public readonly List<StageAttempt> History = new List<StageAttempt>();  // 之前各次的完整快照

        // 实际交接给下一棒的有效内容（用户编辑版优先，未编辑则原始回复）
        public string Effective { get { return HandoffText ?? ""; } }

        // 把当前这次发送+回复归档进历史，供重跑当前棒时保留旧记录
        public void Archive()
        {
            if (AttemptCount <= 0 && string.IsNullOrEmpty(SentPrompt) && string.IsNullOrEmpty(RawReply)) return;
            History.Add(new StageAttempt
            {
                SentPrompt = SentPrompt ?? "",
                SentAt = SentAt,
                RawReply = RawReply ?? "",
                ReplyAt = ReplyAt
            });
        }
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

    // 一轮的完整记录。最终对齐属于本轮，不算新轮（Number 在对齐完成时才写入）。
    // 拆解与对齐都是 Agent1 的两段处理，分别存 SDec / SAlign，互不覆盖、都留痕。
    public class RoundRecord
    {
        public int Number;
        public StageRecord SDec = new StageRecord { Role = "拆解", AgentId = "A1" };
        public StageRecord SExec = new StageRecord { Role = "执行", AgentId = "A2" };
        public StageRecord SCheck = new StageRecord { Role = "自检", AgentId = "A3" };
        public StageRecord SAlign = new StageRecord { Role = "对齐", AgentId = "A1" };
        public string UserAddition;    // 本轮生效的用户补充汇总
        public DateTime FinishedAt;

        // 兼容旧查看器/落盘：返回各棒交接有效内容。
        // 保留 setter 兼容旧 Shell.cs 协同代码：写入时同步到对应棒的原始回复与交接版。
        public string Decomposition { get { return SDec != null ? SDec.Effective : ""; } set { if (SDec != null) { SDec.RawReply = value ?? ""; SDec.HandoffText = value ?? ""; } } }
        public string Execution { get { return SExec != null ? SExec.Effective : ""; } set { if (SExec != null) { SExec.RawReply = value ?? ""; SExec.HandoffText = value ?? ""; } } }
        public string SelfCheck { get { return SCheck != null ? SCheck.Effective : ""; } set { if (SCheck != null) { SCheck.RawReply = value ?? ""; SCheck.HandoffText = value ?? ""; } } }
        public string Alignment { get { return SAlign != null ? SAlign.Effective : ""; } set { if (SAlign != null) { SAlign.RawReply = value ?? ""; SAlign.HandoffText = value ?? ""; } } }
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
    // - 只有 Align 批准才计数：RoundRecord 只在那一刻进入 Session.Rounds
    // - RoundDone 必停：ContinueRound 只能从 RoundDone 发起，绝不自动循环
    // - 失败/停止不算轮次：Fail/Stop 不触碰 Session.Rounds
    // - 迟到/重复响应忽略：ReceiveReply 只在当前棒处于 Running 时接受
    // - 重复发送拒绝：SendCurrent 对 Running 中的棒直接拒绝
    // - 手动/自动审批：ReceiveReply 到达交接点时读 Mode，切换即时对下一交接点生效
    public class Pipeline
    {
        public PipeConfig Config;
        public PipeSession Session;
        public PipePhase Phase = PipePhase.Idle;
        public RoundRecord CurrentRound;   // 本轮工作副本，对齐完成前不进 Rounds
        public ApprovalMode Mode = ApprovalMode.Manual;  // 审批模式，默认手动
        public bool Paused;                // 超时暂停（沿用旧交互）
        public string FailedReason = "";
        public string Note = "";

        // 有待交付给当前 Agent 的提示词（界面刷新时取走并清空，然后写剪贴板+切窗口）
        public string PendingDeliver;

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

        // 当前轮次 = 已完成 + 1（进行中/待审批/停止/失败时）；未开始或已结束则显示已完成数
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

        // 当前正在处理的棒（拆解/执行/自检/对齐）
        public StageRecord Cur
        {
            get
            {
                if (CurrentRound == null) return null;
                switch (Phase)
                {
                    case PipePhase.Decompose: return CurrentRound.SDec;
                    case PipePhase.Execute: return CurrentRound.SExec;
                    case PipePhase.SelfCheck: return CurrentRound.SCheck;
                    case PipePhase.Align: return CurrentRound.SAlign;
                    default: return null;
                }
            }
        }

        public string CurrentPrompt()
        {
            StageRecord st = Cur;
            return ComposeFor(StepIndex, st != null ? st.PreparedInput : "");
        }

        // 开始会话。返回 null = 成功；否则返回失败原因（界面直接展示）
        // 手动/自动都不在此发送：初始需求由用户在 Agent1 工作区点「发送给 Agent 1」启动。
        public string Start(string task)
        {
            if (Session != null && !Session.Ended) return "会话已在进行，先结束再开始";
            if (string.IsNullOrWhiteSpace(task)) return "需求为空，先填写任务内容";
            Session = new PipeSession();
            Session.Task = task.Trim();
            Session.StartedAt = DateTime.Now;
            Session.Ended = false;
            CurrentRound = NewRound();
            Phase = PipePhase.Decompose;
            _lastRunPhase = Phase;
            Paused = false;
            FailedReason = "";
            CurrentRound.SDec.PreparedInput = Session.Task;  // Agent1 左侧预填原始需求
            CurrentRound.SDec.Status = StageState.Draft;
            Note = "第 1 轮 · 拆解待发送（点「发送给 Agent 1」）";
            return null;
        }

        RoundRecord NewRound()
        {
            var r = new RoundRecord();
            r.SDec.AgentId = AgentIdOf(0);
            r.SExec.AgentId = AgentIdOf(1);
            r.SCheck.AgentId = AgentIdOf(2);
            r.SAlign.AgentId = AgentIdOf(StepCount - 1);
            return r;
        }

        string AgentIdOf(int stepIdx)
        {
            if (stepIdx < 0 || stepIdx >= StepCount) return "";
            return Config.Steps[stepIdx].Agent ?? "";
        }

        // 拼某一棒的提示词：主载荷用左侧框内容（用户编辑版），其余上下文用已提交有效版本。
        // 顺带把排队中的补充需求打进去并标记生效。
        public string ComposeFor(int stepIdx, string primary)
        {
            if (Session == null || stepIdx < 0 || stepIdx >= StepCount) return "";
            string p = Config.Steps[stepIdx].Prompt ?? "";
            RoundRecord r = CurrentRound;
            string dec = r != null && r.SDec != null ? r.SDec.Effective : "";
            string exe = r != null && r.SExec != null ? r.SExec.Effective : "";
            string chk = r != null && r.SCheck != null ? r.SCheck.Effective : "";

            // 先用左侧框内容替换当前棒的主占位符（primary 为 null 时不替换，走下方通用值），再用通用值兜底其余占位符
            if (primary != null)
            {
                switch (stepIdx)
                {
                    case 0: p = p.Replace("{{task}}", primary); break;
                    case 1: p = p.Replace("{{decomposition}}", primary); break;
                    case 2: p = p.Replace("{{execution}}", primary); break;
                    case 3: p = p.Replace("{{selfcheck}}", primary); break;
                }
            }
            p = p.Replace("{{task}}", Session.Task ?? "");
            p = p.Replace("{{decomposition}}", dec);
            p = p.Replace("{{execution}}", exe);
            p = p.Replace("{{selfcheck}}", chk);
            p = p.Replace("{{history}}", HistoryText());
            p = p.Replace("{{additions}}", DrainAdditions());
            return p;
        }

        // 兼容旧 Shell 的单参版本：不指定主载荷，主占位符由通用值（已存有效内容）兜底
        public string ComposeFor(int stepIdx) { return ComposeFor(stepIdx, null); }

        // 发送当前棒：左侧框内容做主载荷，组提示词、拍快照、置运行中。返回待交付提示词。
        public string SendCurrent(string leftText)
        {
            StageRecord st = Cur;
            if (st == null || !Running) return null;
            if (st.Status == StageState.Running) return null;   // 防重复发送
            if (st.AttemptCount > 0 || !string.IsNullOrEmpty(st.SentPrompt) || !string.IsNullOrEmpty(st.RawReply))
                st.Archive();                                    // 重跑前把上一次归档
            st.PreparedInput = leftText ?? "";
            string prompt = ComposeFor(StepIndex, st.PreparedInput);
            st.SentPrompt = prompt;
            st.SentAt = DateTime.Now;
            st.AttemptCount++;
            st.RawReply = "";
            st.HandoffText = "";
            st.HandoffEdited = false;
            st.Status = StageState.Running;
            LogChat(st.AgentId, 0, prompt);
            Note = "第 " + CurrentRoundNo + " 轮 · " + st.Role + " 调用中";
            PendingDeliver = prompt;
            return prompt;
        }

        // 收到当前棒的回复。迟到/重复响应（非 Running 状态）直接忽略，返回 false。
        // 手动审批 → 置待审批并暂停；自动审批 → 立刻批准并交接下一棒。
        public bool ReceiveReply(string text)
        {
            StageRecord st = Cur;
            if (st == null || !Running) return false;
            if (st.Status != StageState.Running) return false;   // 迟到/重复忽略
            if (string.IsNullOrWhiteSpace(text)) return false;   // 输出无效不盲目推进
            Paused = false;
            st.RawReply = text;
            st.ReplyAt = DateTime.Now;
            st.HandoffText = text;      // 编辑版初始=原始回复
            st.HandoffEdited = false;
            st.Status = StageState.AwaitingApproval;
            LogChat(st.AgentId, 1, text);
            if (Mode == ApprovalMode.Auto)
            {
                Note = "「" + st.Role + "」已完成，自动交接";
                ApproveAndPass();       // 自动批准并尝试发送下一棒（结果写进 PendingDeliver）
            }
            else
            {
                Note = "「" + st.Role + "」输出已就绪，等待审批";
            }
            return true;
        }

        // 编辑右侧交接内容：只改交接依据，不重新调用 Agent（与「重新生成」明确区分）
        public void EditHandoff(string text)
        {
            StageRecord st = Cur;
            if (st == null) return;
            if (st.Status != StageState.AwaitingApproval && st.Status != StageState.HandedOff) return;
            st.HandoffText = text ?? "";
            st.HandoffEdited = true;
        }

        // ================= 旧 Shell 协同界面兼容垫片（新 SynergyPanel 不使用；待 Shell.cs 完成界面替换后可删） =================

        // 旧代码的步级确认闸门：等价于「当前棒待审批」
        public bool ConfirmPending
        {
            get { StageRecord st = Cur; return st != null && st.Status == StageState.AwaitingApproval; }
        }

        // 旧剪贴板监听入口：等价于新版的「收到当前棒回复」
        public bool Advance(string reply) { return ReceiveReply(reply); }

        // 兼容旧 Shell：确认交接 = 批准并传递当前棒（返回待交付提示词）
        public string Confirm() { return ApproveAndPass(); }

        // 旧两参补充入口
        public Supplement AddSupplement(string agentId, string text) { return AddSupplement(agentId, "", text); }

        // 批准并传递：提交当前棒 → 推进到下一棒 → 用编辑后版本填下一棒左侧 → 立即启动下一棒。
        // 对齐棒批准则本轮完成。返回待交付提示词（写进 PendingDeliver）。
        public string ApproveAndPass()
        {
            StageRecord st = Cur;
            if (st == null || st.Status != StageState.AwaitingApproval) return null;
            st.Status = StageState.HandedOff;
            if (Phase == PipePhase.Align)
            {
                FinishRound();
                return null;            // 轮结束，等待用户决定继续/补充后继续/结束
            }
            AdvancePhase();
            StageRecord nx = Cur;
            if (nx == null) return null;
            nx.PreparedInput = st.Effective;    // 关键：交接用的是用户编辑后的版本
            nx.Status = StageState.Draft;
            Note = "已交接给「" + nx.Role + "」，自动启动";
            return SendCurrent(nx.PreparedInput);   // 批准即启动下一棒
        }

        // 仅填入下一阶段：提交当前棒 → 推进 → 填下一棒左侧，但不发送，等用户在该工作区点「发送」
        public string FillNextOnly()
        {
            StageRecord st = Cur;
            if (st == null || st.Status != StageState.AwaitingApproval) return null;
            st.Status = StageState.HandedOff;
            if (Phase == PipePhase.Align)
            {
                FinishRound();
                return null;
            }
            AdvancePhase();
            StageRecord nx = Cur;
            if (nx == null) return null;
            nx.PreparedInput = st.Effective;
            nx.Status = StageState.Draft;       // 待发送，由用户点「发送给 Agent N」
            Note = "已填入「" + nx.Role + "」左侧，待发送";
            return null;
        }

        // 重新生成/重新执行当前棒：归档当前尝试后用当前左侧内容重新调用 Agent。
        // 只允许未交接的棒重跑（待审批/失败/运行中），已传递的不回退，避免推翻下游。
        public string RerunCurrent()
        {
            StageRecord st = Cur;
            if (st == null || !Running) return null;
            if (st.Status == StageState.HandedOff) return null;
            // 运行中重跑视为放弃本次等待
            string prompt = SendCurrent(st.PreparedInput);
            if (prompt != null) Note = "「" + st.Role + "」重新生成中";
            return prompt;
        }

        void AdvancePhase()
        {
            switch (Phase)
            {
                case PipePhase.Decompose: Phase = PipePhase.Execute; break;
                case PipePhase.Execute: Phase = PipePhase.SelfCheck; break;
                case PipePhase.SelfCheck: Phase = PipePhase.Align; break;
            }
            _lastRunPhase = Phase;
        }

        void FinishRound()
        {
            RoundRecord r = CurrentRound;
            if (r == null) return;
            r.Number = CompletedRounds + 1;
            r.UserAddition = JoinAppliedAdditions();
            r.FinishedAt = DateTime.Now;
            Session.Rounds.Add(r);
            CurrentRound = null;
            Phase = PipePhase.RoundDone;
            Note = "第 " + r.Number + " 轮已完成，等待确认（继续 / 补充后继续 / 结束）";
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

        // 排队中的补充（自动模式运行中收到的补充在此排队，提示生效阶段，不丢失、不触发重复调用）
        public int PendingSupplementCount { get { return _pending.Count; } }

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

        // 轮末「继续下一轮」：只能从 RoundDone 进入新轮。手动→备好拆解待发送；自动→立即发送拆解。
        public string ContinueRound()
        {
            if (Phase != PipePhase.RoundDone) return null;
            if (CompletedRounds >= Config.MaxRounds) return null;   // 安全上限
            CurrentRound = NewRound();
            Phase = PipePhase.Decompose;
            _lastRunPhase = Phase;
            CurrentRound.SDec.PreparedInput = Session.Task;   // 预填原始需求，用户可改/补充
            CurrentRound.SDec.Status = StageState.Draft;
            Note = "第 " + CurrentRoundNo + " 轮 · 拆解待发送";
            if (Mode == ApprovalMode.Auto)
                return SendCurrent(CurrentRound.SDec.PreparedInput);
            return null;
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
            Note = "已恢复 · " + PhaseName;
            return Cur != null ? CurrentPrompt() : null;   // 重投当前棒提示词，供旧 Shell 重新交付
        }

        // 调用失败：停止推进，展示原因，轮次不变；Retry 重新发送当前棒
        public void Fail(string reason)
        {
            if (!Running) return;
            StageRecord st = Cur;
            if (st != null) st.Status = StageState.Failed;
            _lastRunPhase = Phase;
            Phase = PipePhase.Failed;
            FailedReason = reason ?? "";
            Note = "失败：" + FailedReason;
        }

        public string Retry()
        {
            if (Phase != PipePhase.Failed) return null;
            Phase = _lastRunPhase;
            FailedReason = "";
            StageRecord st = Cur;
            Note = "重试 · " + PhaseName;
            if (st != null) return SendCurrent(st.PreparedInput);
            return null;
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
