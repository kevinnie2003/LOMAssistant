using System.Collections;
using System.Collections.Generic;
using System.Linq;
using LOMAssistant.Config;

namespace LOMAssistant.Features.Ai;

/// <summary>
/// AI 攻略对话：玩家问一句，AI 自己决定查哪些资料（AiKnowledge 的工具），查完再回答。
/// 一个问题最多查 MaxRounds 轮（深度模式 30 轮），最后一轮不带工具、拿着查到的资料作答。
/// 答完以后只留问答本身，查资料的来回丢掉，下一个问题不用再为它们付 token。
/// </summary>
public static class AiChat
{
	public enum Kind { User, Assistant, Activity, Error, Proposal }

	public sealed class Entry
	{
		public Kind Kind;
		public string Text = "";
		/// <summary>女主角回答时的表情（立绘文件名）。</summary>
		public string Mood = "";
		/// <summary>修改提议：要改的项、玩家点了什么。</summary>
		public List<Change> Changes;
		public ProposalState State;
	}

	public enum ProposalState { Pending, Applied, Declined, Expired }

	/// <summary>提议里的一项：数值页的一行，当前值、新值，确认后执行 Apply。</summary>
	public sealed class Change
	{
		public string Name = "", From = "", To = "";
		[Newtonsoft.Json.JsonIgnore]
		public System.Action Apply;
	}

	/// <summary>现在用的人设：空 = 攻略助手，否则是女主角的角色编号（AiPersona.Id）。</summary>
	public static string PersonaId = AiPersona.AssistantId;

	/// <summary>这位现在用的对话（存在磁盘上，见 AiSessions）。</summary>
	public static AiSession Session => AiSessions.Current(PersonaId);

	private static AiSession Current => Session;

	// 查资料的轮数上限（每轮可以同时查好几处）：只防死循环，正常问题远用不到；深度模式放宽，极速模式查几次就得答
	private static int MaxRounds => Deep ? 30 : Fast ? 6 : 20;
	// 发给模型的问答历史按字数算：超过 HistoryChars 时，较早的部分压缩成一段摘要，最近 HistoryKeepChars 字原样留着；
	// 两次压缩之间前缀不变，缓存一直命中。一问里查资料最多要 8 万 token 左右，历史再加 3 万字，128K 窗口的模型也放得下
	// 极速模式给的是本地小模型、免费额度：上下文小，历史和查到的资料都压到三分之一
	private static int HistoryChars => Fast ? 10000 : 30000;
	private static int HistoryKeepChars => Fast ? 4000 : 12000;
	private const string SummaryHead = "（前面聊过的内容摘要）";
	private static int MaxToolChars => Fast ? 4000 : 12000;

	/// <summary>给玩家看的对话（含「正在查…」和出错提示）。</summary>
	public static List<Entry> Shown => Current.Shown;

	/// <summary>最近一次女主角回答时的表情，立绘用。</summary>
	public static string Mood => Current.Shown.LastOrDefault(e => e.Kind == Kind.Assistant)?.Mood ?? "";

	public static bool Busy { get; private set; }
	public static int TokensUsed { get; private set; }
	/// <summary>其中命中服务商缓存的输入 token（按缓存价计费，便宜得多）。</summary>
	public static int CachedUsed { get; private set; }
	/// <summary>正在写的回答（流式收到的部分），写完清空。</summary>
	public static string Partial { get; private set; } = "";
	private static bool _stop;

	public static LlmProvider Provider => LlmProvider.At(ConfigManager.AiProvider.Value);

	public static bool Deep => ConfigManager.AiDeepMode.Value;

	/// <summary>极速模式：少查几轮、只给常用的几个工具、提示词精简、不让模型多想。和深度模式同时开着时按深度模式算。</summary>
	public static bool Fast => ConfigManager.AiFastMode.Value && !Deep;

	/// <summary>这一问给模型的工具：极速模式只给最常用的几个，小模型工具多了容易选错、或把调用写进正文。</summary>
	public static IEnumerable<(string Name, string Description, Newtonsoft.Json.Linq.JObject Parameters)> Tools
		=> Fast ? AiKnowledge.Tools.Where(t => FastTools.Contains(t.Name)) : AiKnowledge.Tools;

	private static readonly HashSet<string> FastTools = new HashSet<string> { "search", "script", "heroine", "player_state", "propose_change" };

	/// <summary>这次用的模型：深度模式且填了深度模型就用它。</summary>
	public static string Model => Deep && ConfigManager.AiDeepModel.Value.Trim().Length > 0 ? ConfigManager.AiDeepModel.Value.Trim() : ConfigManager.AiModel.Value;

	private static string DeepRule => Deep ? AiPrompts.DeepRule : Fast ? AiPrompts.FastRule : "";

	public static bool Configured => ConfigManager.AiBaseUrl.Value.Trim().Length > 0 && ConfigManager.AiModel.Value.Trim().Length > 0
		&& (Provider.NoKey || ConfigManager.AiApiKey.Value.Trim().Length > 0);

	/// <summary>开一段新对话（旧的还在，可以切回去）。</summary>
	public static void NewSession()
	{
		if (!Busy)
		{
			AiSessions.New(PersonaId);
		}
	}

	/// <summary>删掉现在这段对话。</summary>
	public static void DeleteSession()
	{
		if (!Busy)
		{
			AiSessions.Delete(Current);
		}
	}

	public static void Stop()
	{
		_stop = true;
		LlmClient.Abort();
	}

	public static void Ask(string question)
	{
		question = (question ?? "").Trim();
		if (Busy || question.Length == 0)
		{
			return;
		}
		// 小伙伴正要开口：让给玩家的问题
		AiCompanion.Cancel();
		// 中途换了人设，这一问还是记在原来那位的对话里
		LOMAssistantPlugin.Instance.StartCoroutine(Run(question, Current, AiPersona.Find(PersonaId), null, true));
	}

	/// <summary>问一句：答完（或出错、停止）后 done 拿到回答和查了几处资料；save 为假时不存盘（测试题用）。</summary>
	internal static IEnumerator Run(string question, AiSession session, AiPersona persona, System.Action<string, int> done, bool save)
	{
		Busy = true;
		_stop = false;
		List<Entry> Shown = session.Shown;
		List<ChatMessage> History = session.History;
		Shown.Add(new Entry { Kind = Kind.User, Text = question });
		// 玩家上次点了确认或不改：接在这次问题前面告诉 AI
		string asked = session.Note.Length > 0 ? "（" + session.Note + "）\n" + question : question;
		session.Note = "";
		var turn = new List<ChatMessage> { new ChatMessage { Role = ChatMessage.User, Text = asked } };
		var proposals = new List<Entry>();
		LlmProvider provider = Provider;
		string answer = null;
		// 系统提示词这一问只算一次，每轮都一样（缓存靠它）；会变的现状接在这次问题前面
		string system = (persona != null ? persona.Prompt(LanguageRule, Spoiler, Fast) : SystemPrompt()) + DeepRule;
		string context = persona != null ? persona.Context() : "";
		// 现状的字段和关系说明仍有中文，游戏对白、选项沿用游戏当前语言；标出玩家的话，避免把背景资料当成提问或回答语言
		var asking = new ChatMessage { Role = ChatMessage.User, Text = context.Length > 0 ? context + "\n\n【赵活说】\n" + asked : asked };
		// 同一个问题里一模一样的查询不再重查、不再把结果再发一遍
		var looked = new HashSet<string>();
		AiDebug.Turn(persona?.Name ?? "攻略助手", system, asking.Text);
		// 前几轮边说话边查资料时已经写出来的话：接着往下写的时候还留在屏幕上，最后接在回答前面（不然流式显示的那段一换轮就没了）
		string said = "";
		bool rewrite = false;
		for (int round = 0; round < MaxRounds + (rewrite ? 1 : 0) && !_stop; round++)
		{
			LlmReply reply = null;
			// 最后一轮（查满了，或者上一条回答被工具调用格式截断）：不带工具，把这一问查到的资料整理成一段文字交给它直接写。
			// 带着工具调用的历史再禁止调用，DeepSeek 常会把调用格式写进正文，回答只剩半截
			bool last = round >= MaxRounds - 1 || rewrite;
			// Claude 查满轮数时不换成摘要：原样带着这一问的查询记录和工具定义，只是不许再调用，前面的缓存全部接得上
			bool keep = last && !rewrite && provider.Claude;
			var send = keep
				? History.Concat(new[] { asking }).Concat(turn.Skip(1)).Concat(new[] { new ChatMessage { Role = ChatMessage.User, Text = AiPrompts.RoundsUsedUp } }).ToList()
				: last
				? History.Concat(new[] { asking, new ChatMessage { Role = ChatMessage.User, Text = Digest(turn) } }).ToList()
				: History.Concat(new[] { asking }).Concat(turn.Skip(1)).ToList();
			Partial = said;
			yield return LlmClient.Complete(provider, ConfigManager.AiBaseUrl.Value, ConfigManager.AiApiKey.Value, Model,
				system, send, !last || keep, last ? (Deep ? 8000 : Fast ? 3000 : 6000) : Deep ? 4000 : Fast ? 1500 : 2000, r => reply = r, keep, text => Partial = Join(said, WrongLanguage(text, question, TextConverter.DisplayLanguage) ? "" : Showable(text)));
			Partial = said;
			if (_stop)
			{
				break;
			}
			if (reply == null || reply.Error != null)
			{
				Shown.Add(new Entry { Kind = Kind.Error, Text = reply?.Error ?? "没有回复" });
				FileLogger.LogWarning("[AI] " + (reply?.Error ?? "没有回复"));
				break;
			}
			TokensUsed += reply.InputTokens + reply.OutputTokens;
			CachedUsed += reply.CachedTokens;
			AiDebug.Reply(reply);
			if (reply.Calls.Count == 0)
			{
				// 回复是空的（Claude 查完资料有时直接结束这一轮，一个字不写）：不带工具、把查到的资料交给它再写一次
				if ((reply.Text ?? "").Trim().Length == 0)
				{
					FileLogger.LogWarning("[AI] 回复是空的（停止原因 " + (reply.StopReason.Length > 0 ? reply.StopReason : "未知") + "，第 " + (round + 1) + " 轮）"
						+ (rewrite ? "，重写也是空的" : "，让它根据查到的资料直接写"));
					if (!rewrite)
					{
						rewrite = true;
						continue;
					}
					answer = "（查了很多资料还没整理出答案，换个具体点的问法再问一次吧。）";
					break;
				}
				string cut = StripToolMarkup(reply.Text);
				// 模型把调用工具的格式写进了正文，从那里往后都被剪掉，回答只剩半截：让它不调工具、从头写一遍
				if (cut.Length < (reply.Text ?? "").Length && !rewrite)
				{
					FileLogger.Log("[AI] 回答里夹着工具调用格式，后半截被剪掉，让它根据查到的资料重写一遍");
					rewrite = true;
					continue;
				}
				answer = StripIds(cut, persona != null).Trim();
				if (answer.Length == 0 && reply.Text.Trim().Length > 0)
				{
					answer = "（查了很多资料还没整理出答案，换个具体点的问法再问一次吧。）";
				}
				break;
			}
			string early = MoodTag.Replace(StripIds(StripToolMarkup(reply.Text ?? ""), persona != null), "").Trim();
			if (early.Length > 0 && !WrongLanguage(early, question, TextConverter.DisplayLanguage))
			{
				said = Join(said, early);
			}
			turn.Add(new ChatMessage { Role = ChatMessage.Assistant, Text = reply.Text, Calls = reply.Calls, Raw = reply.Raw, Reasoning = reply.Reasoning ?? "" });
			foreach (ToolCall call in reply.Calls)
			{
				Shown.Add(new Entry { Kind = Kind.Activity, Text = AiKnowledge.Activity(call.Name, call.Args) });
				string result;
				if (call.Name != "propose_change" && !looked.Add(call.Name + call.Args.ToString(Newtonsoft.Json.Formatting.None, new Newtonsoft.Json.JsonConverter[0])))
				{
					result = AiPrompts.AlreadyLooked;
				}
				else
				{
					result = call.Name == "propose_change" ? Propose(call.Args, proposals) : AiKnowledge.Run(call.Name, call.Args);
				}
				if (result.Length > MaxToolChars)
				{
					result = result.Substring(0, MaxToolChars) + "\n……（太长，后面省略）";
				}
				AiDebug.Tool(call.Name, call.Args.ToString(Newtonsoft.Json.Formatting.None, new Newtonsoft.Json.JsonConverter[0]), result);
				turn.Add(new ChatMessage { Role = ChatMessage.Tool, CallId = call.Id, Text = result });
			}
		}
		Partial = "";
		if (!_stop && answer != null && said.Length > 0)
		{
			answer = answer.StartsWith("（查了很多资料") ? said : Join(said, answer);
		}
		// 界面是英、韩、日文，玩家也没用中文问，回答却是中文（游戏原话随当前语言，Mod 的人设补充和工具说明仍可能带来中文）：
		// 拿这段回答再问一次，只让它改写成界面语言。繁体界面回答成简体时直接转成繁体
		if (!_stop && answer != null && answer.Length > 0)
		{
			if (TextConverter.DisplayLanguage == "ChineseTraditional")
			{
				answer = TextConverter.ToTraditional(answer);
			}
			else if (AiPrompts.RewriteRule != null && WrongLanguage(answer, question, TextConverter.DisplayLanguage))
			{
				FileLogger.Log("[AI] 回答不是界面语言，让它改写一遍");
				LlmReply rewritten = null;
				Partial = "";
				yield return LlmClient.Complete(provider, ConfigManager.AiBaseUrl.Value, ConfigManager.AiApiKey.Value, Model,
					AiPrompts.RewriteRule + AiPrompts.RewriteSpeaker(persona), new List<ChatMessage> { new ChatMessage { Role = ChatMessage.User, Text = answer } }, false, Deep ? 8000 : 4000, r => rewritten = r);
				string text = rewritten != null && rewritten.Error == null ? StripIds(StripToolMarkup(rewritten.Text ?? ""), persona != null).Trim() : "";
				if (text.Length > 0)
				{
					answer = text;
					TokensUsed += rewritten.InputTokens + rewritten.OutputTokens;
				}
			}
		}
		if (_stop)
		{
			Shown.Add(new Entry { Kind = Kind.Error, Text = "已停止" });
		}
		else if (answer != null)
		{
			string mood = "";
			if (persona != null)
			{
				answer = TakeMood(answer, persona, out mood);
				// 攻略提示的记号有的模型会写成 [攻略提示]、**【攻略提示】**，统一成界面认的写法
				answer = System.Text.RegularExpressions.Regex.Replace(answer, @"\**[\[【]\s*攻略提示\s*[\]】]\**", "【攻略提示】");
			}
			Shown.Add(new Entry { Kind = Kind.Assistant, Text = answer.Length > 0 ? answer : "（AI 没有说话）", Mood = mood });
			Shown.AddRange(proposals);
			History.Add(turn[0]);
			History.Add(new ChatMessage { Role = ChatMessage.Assistant, Text = answer });
			if (History.Sum(m => m.Text.Length) > HistoryChars)
			{
				yield return Compress(provider, History);
			}
		}
		if (save)
		{
			AiSessions.Save(session);
		}
		Busy = false;
		done?.Invoke(answer ?? Shown.LastOrDefault(e => e.Kind == Kind.Error)?.Text ?? "", Shown.Count(e => e.Kind == Kind.Activity));
	}

	// 问答历史太长：较早的部分（连同上次的摘要）请模型压成一段摘要放在最前面，最近的原样留着。
	// 摘要只记玩家说过的情况和打算、聊过什么、确认过哪些修改；回答里的数字和结论不当事实记，免得一错再错。压缩失败就直接丢掉较早的部分
	private static IEnumerator Compress(LlmProvider provider, List<ChatMessage> history)
	{
		int keep = history.Count, chars = 0;
		while (keep >= 2 && chars + history[keep - 1].Text.Length + history[keep - 2].Text.Length <= HistoryKeepChars)
		{
			chars += history[keep - 1].Text.Length + history[keep - 2].Text.Length;
			keep -= 2;
		}
		keep = System.Math.Min(keep, history.Count - 2);
		if (keep <= 0)
		{
			yield break;
		}
		var old = new System.Text.StringBuilder();
		for (int i = 0; i < keep; i++)
		{
			old.Append(history[i].Role == ChatMessage.User ? "玩家：" : "回答：").Append(history[i].Text).Append("\n\n");
		}
		string system = "下面是《活侠传》玩家和游戏 Mod 里 AI 的一段对话。把它压缩成摘要，给接着聊的 AI 当记忆用，不是向玩家发出的回答。历史保留原有语言，游戏提供的名称和对白随游戏当前语言；中文角色标签和 Mod 标记只是内部结构。沿用已有的人物名和术语，不因中文标签把韩语等原有名称改成中文。\n"
			+ "- 只记：玩家说过的自己的情况、打算和偏好（走哪条线、想和谁结缘、不想知道什么）；聊过哪些话题；玩家确认或拒绝过哪些修改；说好的约定；还没解决的问题。\n"
			+ "- 回答里说过的数字、条件、剧情结论不要当事实记，最多写「聊过某某的条件」，之后要用时会重新查。\n"
			+ "- 开头已经有摘要的，把它和后面的内容合在一起重写成一份。\n"
			+ "- 分条写，不超过 800 字，用" + ReplyLanguage + "。只输出摘要。";
		LlmReply reply = null;
		yield return LlmClient.Complete(provider, ConfigManager.AiBaseUrl.Value, ConfigManager.AiApiKey.Value, ConfigManager.AiModel.Value,
			system, new List<ChatMessage> { new ChatMessage { Role = ChatMessage.User, Text = old.ToString() } }, false, 1500, r => reply = r);
		history.RemoveRange(0, keep);
		string summary = reply != null && reply.Error == null ? StripToolMarkup(reply.Text ?? "").Trim() : "";
		if (summary.Length == 0)
		{
			FileLogger.LogWarning("[AI] 压缩对话失败，较早的 " + keep + " 条直接丢掉：" + (reply?.Error ?? "没有回复"));
			yield break;
		}
		TokensUsed += reply.InputTokens + reply.OutputTokens;
		CachedUsed += reply.CachedTokens;
		AiDebug.Reply(reply);
		history.InsertRange(0, new[]
		{
			new ChatMessage { Role = ChatMessage.User, Text = SummaryHead + "\n" + summary },
			new ChatMessage { Role = ChatMessage.Assistant, Text = "（记下了）" },
		});
		FileLogger.Log("[AI] 对话太长，较早的 " + keep + " 条压缩成摘要（" + summary.Length + " 字）");
	}

	// AI 的修改提议：找到的项做成一张卡片（等回答说完再显示），找不到的告诉 AI
	private static string Propose(Newtonsoft.Json.Linq.JObject args, List<Entry> proposals)
	{
		var missing = new List<string>();
		List<Change> changes = LOMAssistant.Features.Story.StoryReviewPanel.AiResolveChanges(args?["changes"] as Newtonsoft.Json.Linq.JArray, missing);
		string reason = args?["reason"]?.ToString().Trim() ?? "";
		if (changes.Count > 0)
		{
			proposals.Add(new Entry { Kind = Kind.Proposal, Text = reason, Changes = changes });
		}
		var sb = new System.Text.StringBuilder();
		if (changes.Count > 0)
		{
			sb.Append("已经把修改卡片给玩家看了（").Append(string.Join("；", changes.Select(c => c.Name + " " + c.From + " → " + c.To).ToArray())).Append("），玩家点确认才会改。告诉玩家去确认，不要说已经改好了。");
		}
		if (missing.Count > 0)
		{
			sb.Append("这些没找到，没放进卡片：").Append(string.Join("、", missing.ToArray())).Append("。可以用数值页里的名字再试一次。");
		}
		return sb.Length > 0 ? sb.ToString() : "没有要改的项。";
	}

	// 有的模型不能调用工具时会把自己的调用格式当正文写出来（DeepSeek 的「<｜DSML｜function_calls>」、<invoke …>、<tool_call>），剪掉
	private static readonly System.Text.RegularExpressions.Regex ToolMarkup = new System.Text.RegularExpressions.Regex(
		@"<[｜|]?\s*DSML[\s\S]*$|<\s*/?\s*(?:function_calls|invoke|parameter|tool_call|tool_use)\b[\s\S]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

	internal static string StripToolMarkup(string text) => ToolMarkup.Replace(text ?? "", "");

	private const int MaxDigestChars = 40000;

	/// <summary>
	/// 这一问查到的资料拼成一段文字，给最后一轮不带工具直接作答用。按查的先后全放进去，太长时每条按比例截短：
	/// 只留最近查的会把一开始就查到的关键条件丢掉（最早查的往往最对题）。
	/// </summary>
	private static string Digest(List<ChatMessage> turn)
	{
		var results = turn.Where(m => m.Role == ChatMessage.Tool && m.Text.Length > 0).Select(m => m.Text).ToList();
		int total = results.Sum(r => r.Length);
		int each = total <= MaxDigestChars || results.Count == 0 ? int.MaxValue : System.Math.Max(600, MaxDigestChars / results.Count);
		var parts = results.Select(r => r.Length > each ? r.Substring(0, each) + "…" : r).ToList();
		if (parts.Count == 0)
		{
			return AiPrompts.DigestNone;
		}
		return AiPrompts.DigestHead
			+ string.Join("\n\n———\n\n", parts.ToArray());
	}

	// 流式显示的半截回答：去掉工具调用格式和末尾的表情标记（可能只写了一半）
	/// <summary>前面已经说过的话和接着写的话，中间空一行。</summary>
	private static string Join(string before, string after) =>
		before.Length == 0 ? after : after.Length == 0 ? before : before + "\n\n" + after;

	internal static string Showable(string text)
	{
		string t = StripToolMarkup(text);
		int tag = t.LastIndexOf('[');
		if (tag >= 0 && t.IndexOf(']', tag) < 0)
		{
			t = t.Substring(0, tag);
		}
		return MoodTag.Replace(t, "").Trim();
	}

	// 剧本编号（ch4_9_3_8、S2700_01_03）；女主角口中连任务编号、旗标编号（S2700_01、G11006）也不要
	private const string ScriptId = @"ch\d+(?:_[A-Za-z0-9]+)+|[A-Z]\d{4}(?:_[A-Za-z0-9]+){2,}";
	private const string AnyId = ScriptId + @"|[A-Z]{1,2}\d{4,}(?:_[A-Za-z0-9]+)*";
	private static readonly System.Text.RegularExpressions.Regex ScriptIds = IdPattern(ScriptId);
	private static readonly System.Text.RegularExpressions.Regex AnyIds = IdPattern(AnyId);

	private static System.Text.RegularExpressions.Regex IdPattern(string id) => new System.Text.RegularExpressions.Regex(
		@"[（(「]\s*(?:" + id + @")\s*[）)」]|[ \t]?(?<![A-Za-z0-9_])(?:" + id + @")(?![A-Za-z0-9_])[ \t]?");

	/// <summary>模型不听话写出来的编号，显示前去掉。</summary>
	internal static string StripIds(string text, bool all) => (all ? AnyIds : ScriptIds).Replace(text, "");

	/// <summary>玩家点了卡片上的确认或不改。</summary>
	public static void Resolve(AiSession session, Entry proposal, bool apply)
	{
		if (proposal.State != ProposalState.Pending || proposal.Changes.Any(c => c.Apply == null))
		{
			return;
		}
		string items = string.Join("；", proposal.Changes.Select(c => c.Name + " " + c.From + " → " + c.To).ToArray());
		if (apply)
		{
			foreach (Change c in proposal.Changes)
			{
				try
				{
					c.Apply();
				}
				catch (System.Exception ex)
				{
					FileLogger.LogWarning("[AI] 修改 " + c.Name + " 失败: " + ex.Message);
				}
			}
			FileLogger.Log("[AI] 玩家确认修改：" + items);
		}
		proposal.State = apply ? ProposalState.Applied : ProposalState.Declined;
		session.Note = (apply ? "玩家确认了修改：" : "玩家没有同意修改：") + items;
		AiSessions.Save(session);
	}

	private static readonly System.Text.RegularExpressions.Regex MoodTag = new System.Text.RegularExpressions.Regex(@"\[\s*(?:表情|mood)\s*[:：]\s*([^\[\]\s:：]+)\s*\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

	// 回复末尾的「[表情: shy]」拿出来，不显示；不在她能用的表情里就用默认。
	// 表情名不限英文：自建人设的表情名是玩家起的文件名；内置人设写成别的（比如中文的意思）也照样拿掉，换成默认
	internal static string TakeMood(string answer, AiPersona persona, out string mood)
	{
		mood = persona.Moods[0];
		System.Text.RegularExpressions.Match m = MoodTag.Match(answer);
		if (m.Success)
		{
			string raw = m.Groups[1].Value.ToLowerInvariant();
			mood = persona.Moods.Contains(raw) ? raw : persona.Moods.FirstOrDefault(x => x.TrimEnd('1', '2', '3') == raw.TrimEnd('1', '2', '3')) ?? persona.Moods[0];
		}
		return MoodTag.Replace(answer, "").Trim();
	}

	/// <summary>试一下能不能连上：不带工具，问一句很短的话。</summary>
	public static IEnumerator Test(System.Action<string> done)
	{
		LlmReply reply = null;
		yield return LlmClient.Complete(Provider, ConfigManager.AiBaseUrl.Value, ConfigManager.AiApiKey.Value, ConfigManager.AiModel.Value,
			"用一句话回答。", new List<ChatMessage> { new ChatMessage { Text = "你好，请回复「连上了」。" } }, false, 30, r => reply = r);
		done(reply == null ? "没有回复" : reply.Error ?? "");
	}

	internal static string ReplyLanguage => TextConverter.DisplayLanguage switch
	{
		"ChineseTraditional" => "繁體中文（正體字）",
		UiText.Korean => "韩语（한국어）",
		UiText.Japanese => "日语（日本語）",
		UiText.English => "英语（English）",
		_ => "简体中文",
	};

	/// <summary>回答用的语言：用界面语言写的那份规则里说。</summary>
	internal static string LanguageRule => AiPrompts.LanguageRule;

	/// <summary>
	/// 回答是不是用错了语言：界面是英文、韩文时汉字比本国文字多，界面是日文时汉字很多却几乎没有假名。
	/// 玩家自己用中文问的（问题里有汉字、日文界面下没有假名）不算错。「」里引的原文不计。
	/// </summary>
	internal static bool WrongLanguage(string answer, string question, string language, int min = 20)
	{
		string body = System.Text.RegularExpressions.Regex.Replace(answer, "「[^」]*」|\\[表情:[^\\]]*\\]|【[^】]*】", "");
		int han = 0, kana = 0, hangul = 0, latin = 0, qHan = 0, qKana = 0;
		foreach (char c in body)
		{
			if (c >= '一' && c <= '鿿') han++;
			else if (c >= 'ぁ' && c <= 'ヿ') kana++;
			else if (c >= '가' && c <= '힣') hangul++;
			else if (c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z') latin++;
		}
		foreach (char c in question ?? "")
		{
			if (c >= '一' && c <= '鿿') qHan++;
			else if (c >= 'ぁ' && c <= 'ヿ') qKana++;
		}
		switch (language)
		{
			// min：汉字多过这么多才算。问答的回答长，20 个以内当是引的名字；主动陪玩的话只有一两句，整句中文也不到 20 个字，要用小得多的数
			case UiText.English:
				return qHan == 0 && han > min && han * 2 > latin / 5;
			case UiText.Korean:
				return qHan == 0 && han > min && han > hangul;
			case UiText.Japanese:
				return (qKana > 0 || qHan == 0) && han > min && kana * 10 < han;
			default:
				return false;
		}
	}

	internal static string Spoiler => AiPrompts.Spoiler(ConfigManager.AiSpoiler.Value);

	// 攻略助手的规则按界面语言各写了一份，见 AiPrompts
	private static string SystemPrompt() => Fast ? AiPrompts.FastAssistant(Spoiler) : AiPrompts.Assistant(Spoiler);
}
