using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LOMAssistant.Features.Story;
using Mortal.Core;
using Newtonsoft.Json.Linq;

namespace LOMAssistant.Features.Ai;

/// <summary>
/// AI 查资料用的工具：都从 Mod 已经读好的剧情数据（StoryAtlas、StoryScripts）现场整理成文字，不另外打包知识库。
/// 剧情知识里不带玩家当前的数值（条件里的「→ 当前…」都去掉），玩家现在的进度、数值只从 player_state 拿。
/// 每个工具的结果有长度上限，太长的分页，免得一次塞给模型太多。
/// </summary>
public static class AiKnowledge
{
	private const int PageChars = 6000;

	/// <summary>给模型看的工具说明（名字、用途、参数）；说明用中文，游戏文本随当前语言。OpenAI 和 Claude 的格式由 LlmClient 各自包装。</summary>
	public static readonly (string Name, string Description, JObject Parameters)[] Tools =
	{
		("search", "按关键词搜游戏剧情：任务名、任务说明和摘要、对白原文、结局和成就、可结缘的女主角。不知道编号时先用它。返回匹配的任务编号、剧本编号、结局编号和片段。",
			Schema(("query", "string", "关键词，如人名、任务名、地点、一句对白里的词。优先用游戏资料中已有的写法：对白和本地化名称随游戏当前语言，内部备注可能是中文。不要因工具说明是中文就把韩语等原有关键词改成中文"))),
		("quest", "看一个任务的详情：所属任务组、主线还是支线、发生时间、地点、触发条件、从哪些剧本接进来、之后接到哪些任务、包含的剧本和每个剧本的剧情摘要，以及玩家读过多少（全部周目）、这一局走过没有。",
			Schema(("key", "string", "任务编号，如 S2700_01（从 search 结果里拿）"))),
		("script", "看一个剧本的详情：游戏的剧情记录、选项、每条分岔路径上的数值和旗标变化、接到哪个剧本、结局和死亡、决斗；以及按分岔分开的对白原文。每个选项、去处标出玩家读过多少（全部周目）和这一局走过没有。对白多时分页。",
			Schema(("name", "string", "剧本编号，如 S2700_02_01"), ("page", "integer", "对白第几页，从 1 开始，默认 1"))),
		("endings", "按关键词查结局（汗青书）、死亡（生死簿）、成就、结局 CG：名称、说明，以及在哪个剧本、走哪条分岔能达成。也能按图鉴编号查，如「生死簿3」「结局4」。关键词带「奖励」「二周目」「命运」时，先列出图鉴奖励（收集多少条生死簿、汗青书、哪些成就，开二周目时加什么）。关键词留空则列出全部名称。",
			Schema(("query", "string", "关键词；传空字符串列出全部名称"))),
		("events", "查场所事件和随机事件：在某个场所修炼、工作、闲逛时随机抽到的事件（如求仙问道、跑步邀请、赤脚大仙），列出在哪个场所、事件名、抽中的权重和加减权重的条件、可能的结果、对应剧本（再用 script 看分岔）。任务以外的事件用它找，比搜对白快。",
			Schema(("query", "string", "事件名、场所名或事件里的一句对白，如 求仙问道、飞天殿、赤脚大仙"))),
		("item", "查物品和开发项目：物品（书、杂物、剧情物品、消耗品）的说明、这一局有几个、使用效果、哪些剧情给它判定它收走它、哪家店卖、哪个开发项目练满送它；"
			+ "铁铺、炼丹、制毒的开发项目（武器、暗器、陷阱、防具、丹药、毒）的门派规模要求、花费、各级点数、这一局开发到哪、从哪个项目练到几级进化出来、能进化成什么。也能查武学秘笈（类型、门派、每级要几点、修练条件、练成加减什么、每次修练消耗什么、这一局练到几级）和天赋（说明、最高几级、哪本秘笈或哪个开发项目练到几级给）。问某个物品做什么用、哪里拿、某件装备怎么开发出来、某门武功怎么练、某个天赋有什么用时先用它。",
			Schema(("name", "string", "物品、开发项目、秘笈或天赋的名字，如 睡丹、逆鳞宝衣、唐门小剑、南宫护心诀"))),
		("shop", "看四师兄的商店：现在架上有什么（价格、剩几个、杂物和消耗品吃了加什么），以及之后哪个月会进什么新货。问商店卖什么、买什么划算、某样东西什么时候有卖时用它。",
			new JObject { ["type"] = "object", ["properties"] = new JObject() }),
		("sect", "看门派经营：门派规模、门派资产、名声、人数、向心力，以及各设施（锻冶场、炼丹房等）现在几级、每旬收入、每级每旬加减哪些数值、追加项要什么旗标。问门派资产怎么来、为什么是负的、门派规模怎么升、设施有什么用时用它。",
			new JObject { ["type"] = "object", ["properties"] = new JObject() }),
		("person", "看一个人物的游戏人物志：称号、出身、门派、亲属、性格（游戏「人物」页的介绍）。问某人是谁、什么来历、哪一门、父母师父是谁时先用它，一次就够。",
			Schema(("name", "string", "人名或称呼，如 唐默铃、小梅、二师兄、火龙仙君"))),
		("heroine", "看一位可结缘女主角的结缘路线：要经过哪些任务、每一站的好感门槛和要设的结缘旗标、玩家现在做到哪一步。",
			Schema(("name", "string", "女主角的名字或称呼，如 小师妹、唐默铃、小竹、郁竹"))),
		("usage", "反查：一面旗标（名字或编号）、某人的好感、一项数值或技能（心相、性情、变心、门派规模、南宫护心诀……），在哪些剧情里被改、被判定（剧本、所属任务、走哪条分岔、改成多少或要多少），旗标还给出这一局现在的值。问「某个旗标在哪设」「某人好感在哪被判定」「心相要多少」「变心怎么攒」「要满足什么才能触发」时用。",
			Schema(("name", "string", "旗标的名字（说明里的词，如 小竹告白）或编号（如 G11006）；或人名（查她的好感在哪被判定）；或数值、技能名（如 心相、变心、南宫护心诀）"))),
		("routes", "整条路线算账：从一个任务的开头剧本走到底，列出每一条完整的路（要选什么、掷骰掷多少、满足什么条件），以及这一条路上加减的好感、数值合计和设的旗标、结局。分岔互斥，一次只走一条，所以「这个任务最多能加多少好感」「哪条路最好」一定要用它，不要把各剧本的变化自己加起来。",
			Schema(("key", "string", "任务编号（如 S2700_01）或剧本编号"), ("focus", "string", "关心的那一项，如 郁竹好感、心相；按它从多到少排，并给出最多、最少。不关心就传空字符串"))),
		("settings", "看咖啡伴侣（本 Mod）的设置项：名字、现在的值、可选值或范围、说明、改了要不要重启。玩家想改 Mod 的设置时先用它，再用 propose_change 提出修改。",
			Schema(("query", "string", "关键词，如 倍速、骰子、随时存档；传空字符串列出全部"))),
		("player_state", "看玩家现在的进度：游戏日期、主线和可触发的支线、正在进行的剧情、主要数值、所有人的好感、结缘对象。不含旗标：进的哪一门、任务进度计数、某段剧情触发过没有，用 usage 查旗标的当前值。回答和玩家当前进度有关的问题前先看一次。",
			new JObject { ["type"] = "object", ["properties"] = new JObject() }),
		("propose_change", "向玩家提议修改数值：会在对话里显示一张卡片，写着每项的当前值和新值，玩家点「确认」才会改，点「不改」就不改。"
			+ "能改的是 Mod 数值页里有的：各项数值（心相、银两、体力……）、好感（写成「好感·人名」）、心上人（新值可以写女主角的名字）、天赋、旗标（写编号或名字，如「旗标·小竹告白」，新值 1 是做到、0 是没做到）、书籍和物品数量等；"
			+ "也能改咖啡伴侣（本 Mod）的设置项（写成「设置·设置项名字」，名字和可选值先用 settings 查，开关写 开 / 关，几选一写选项原文）。"
			+ "调用后告诉玩家去确认，不要说已经改好了。"
			+ "数值的正常范围：体力、心相、性格、学识等大多是 0 到 100，好感 0 到 100，心上人是 0 到 8（女主角的编号）；玩家没明说要超过上限时，「拉满」就是改到 100，不要写 999 这类数。",
			new JObject
			{
				["type"] = "object",
				["properties"] = new JObject
				{
					["changes"] = new JObject
					{
						["type"] = "array",
						["description"] = "要改的项",
						["items"] = new JObject
						{
							["type"] = "object",
							["properties"] = new JObject
							{
								["name"] = new JObject { ["type"] = "string", ["description"] = "数值页里的名字，如 心相、银两、好感·虞小梅、心上人" },
								["value"] = new JObject { ["type"] = "string", ["description"] = "改成多少（整数）；心上人可以写女主角的名字" },
							},
							["required"] = new JArray("name", "value"),
						},
					},
					["reason"] = new JObject { ["type"] = "string", ["description"] = "一句话说明为什么改，会显示在卡片上；用当前界面语言，沿用已提供的人物名和术语，不照抄内部中文字段或标记" },
				},
				["required"] = new JArray("changes"),
			}),
	};

	private static JObject Schema(params (string Name, string Type, string Description)[] props)
	{
		var properties = new JObject();
		foreach (var (name, type, description) in props)
		{
			properties[name] = new JObject { ["type"] = type, ["description"] = description };
		}
		// 字符串参数都必填，页码可省
		return new JObject { ["type"] = "object", ["properties"] = properties, ["required"] = new JArray(props.Where(p => p.Type == "string").Select(p => p.Name)) };
	}

	/// <summary>执行一个工具调用，返回给模型的文字（出错也返回文字，让模型自己换个办法）。</summary>
	public static string Run(string name, JObject args)
	{
		try
		{
			if (!StoryAtlas.Ready)
			{
				return "剧情数据还在读取（打开一次咖啡伴侣窗口的「路线图」会开始读），请稍后再问。";
			}
			string Arg(string key) => args?[key]?.ToString().Trim() ?? "";
			switch (name)
			{
				case "search": return Search(Arg("query"));
				case "quest": return Quest(Arg("key"));
				case "script": return Script(Arg("name"), int.TryParse(Arg("page"), out int p) ? p : 1);
				case "endings":
				{
					string q = Arg("query");
					// 「图鉴奖励」「二周目有什么奖励」「收集多少条加命运」
					bool awards = System.Text.RegularExpressions.Regex.IsMatch(TextConverter.ToSimplified(q), "奖励|周目|命运|新游戏");
					if (!awards)
					{
						return EndingList(q);
					}
					string rest = System.Text.RegularExpressions.Regex.Replace(TextConverter.ToSimplified(q), "奖励|二周目|周目|命运|新游戏\\+?|图鉴|收集|有什么|的", "").Trim();
					return AiItems.LibraryAwards() + (rest.Length > 0 ? "\n" + EndingList(rest) : "");
				}
				case "heroine": return Heroine(Arg("name"));
				case "person": return Person(Arg("name"));
				case "item": return AiItems.Lookup(Arg("name"));
				case "shop": return AiItems.Shop();
				case "sect": return AiItems.Sect();
				case "events": return Events(Arg("query"));
				case "player_state": return StoryReviewPanel.AiPlayerState();
				case "settings": return StoryReviewPanel.AiSettingsText(Arg("query"));
				case "usage": return AiAnalysis.Usage(Arg("name"));
				case "routes": return AiAnalysis.Routes(Arg("key"), Arg("focus"));
				default: return "没有这个工具：" + name;
			}
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[AI] 工具 " + name + " 出错: " + ex);
			return "查询出错：" + ex.Message;
		}
	}

	/// <summary>给玩家看的一行说明：AI 正在查什么。</summary>
	public static string Activity(string name, JObject args)
	{
		string Arg(string key) => args?[key]?.ToString().Trim() ?? "";
		// 「查了 N 处资料」点开后的明细，按界面语言写（简体、英、韩、日、繁体）；引号里是 AI 查的原词，照原样
		string F(string zh, string en, string ko, string ja, string hant, params string[] a) => string.Format(AiPrompts.L(zh, en, ko, ja, hant), a);
		switch (name)
		{
			case "search": return F("搜索「{0}」", "Searched 「{0}」", "「{0}」 검색", "「{0}」を検索", "搜尋「{0}」", Arg("query"));
			case "quest":
				string title = StoryAtlas.Find(Arg("key")) is StoryMission m ? "「" + Clean(m.Title) + "」" : "";
				return F("查看任务 {0}{1}", "Quest {0}{1}", "임무 {0}{1} 확인", "任務 {0}{1} を確認", "查看任務 {0}{1}", Arg("key"), title);
			case "script": return F("查看剧本 {0}", "Scene {0}", "대본 {0} 확인", "台本 {0} を確認", "查看劇本 {0}", Arg("name"));
			case "endings": return F("查结局「{0}」", "Endings 「{0}」", "결말 「{0}」 찾기", "結末「{0}」を検索", "查結局「{0}」", Arg("query"));
			case "heroine": return F("查看「{0}」的结缘路线", "Romance route of 「{0}」", "「{0}」 인연 루트 확인", "「{0}」の縁のルートを確認", "查看「{0}」的結緣路線", Arg("name"));
			case "person": return F("查看「{0}」的人物志", "Biography of 「{0}」", "「{0}」 인물지 확인", "「{0}」の人物誌を確認", "查看「{0}」的人物誌", Arg("name"));
			case "item": return F("查物品「{0}」", "Item 「{0}」", "물건 「{0}」 찾기", "物品「{0}」を検索", "查物品「{0}」", Arg("name"));
			case "shop": return F("查看商店", "Shop", "상점 확인", "店を確認", "查看商店");
			case "sect": return F("查看门派经营", "Sect management", "문파 경영 확인", "門派経営を確認", "查看門派經營");
			case "events": return F("查场所事件「{0}」", "Place events 「{0}」", "장소 이벤트 「{0}」 찾기", "場所のイベント「{0}」を検索", "查場所事件「{0}」", Arg("query"));
			case "player_state": return F("查看当前进度", "Current progress", "현재 진행 확인", "今の進行を確認", "查看目前進度");
			case "settings":
				string q = Arg("query").Length > 0 ? "「" + Arg("query") + "」" : "";
				return F("查看设置项{0}", "Settings {0}", "설정 항목 {0} 확인", "設定項目{0}を確認", "查看設定項目{0}", q);
			case "usage": return F("反查「{0}」", "Where 「{0}」 is used", "「{0}」 역추적", "「{0}」を逆引き", "反查「{0}」", Arg("name"));
			case "routes": return F("算任务 {0} 每条路线", "Routes of quest {0}", "임무 {0}의 모든 루트 계산", "任務 {0} の全ルートを計算", "算任務 {0} 每條路線", Arg("key"));
			case "propose_change": return F("提议修改数值", "Proposed a change", "수치 변경 제안", "数値の変更を提案", "提議修改數值");
			default: return name;
		}
	}

	private static readonly Regex Current = new Regex(@" ?→ ?当前[^ ，；、。)）\n]*");

	/// <summary>去掉富文本标记、条件里的当前值；已有汉字转简体，不把游戏提供的韩语等文字译成中文。</summary>
	internal static string Clean(string text) => AiAnalysis.ExplainTime(Current.Replace(TextConverter.ToSimplified(RichText.Strip(text ?? "")), "").Trim());

	private static string PathText(List<StoryStep> path) => path.Count == 0 ? "（开头，不经分岔）" : string.Join(" → ", path.Select(s => Clean(StoryScripts.Describe(s))).ToArray());

	internal static string Speaker(string id)
	{
		if (id.Length == 0)
		{
			return "旁白";
		}
		try
		{
			string text = LocalizationManager.Instance.LocaleResolver.GetString("Character/" + id);
			return string.IsNullOrWhiteSpace(text) || text.StartsWith("Character/") ? id : TextConverter.ToSimplified(text);
		}
		catch
		{
			return id;
		}
	}

	private static List<string> _people;

	/// <summary>剧本里说过话的人物编号（girl5_3 这类变体并到 girl5）。</summary>
	private static List<string> People()
	{
		if (_people == null || _people.Count == 0)
		{
			_people = StoryScripts.All.SelectMany(s => s.Talk.Select(t => t.Speaker)).Where(id => id.Length > 0)
				.Select(id => Regex.Replace(id, @"_\d+$", "")).Distinct().ToList();
		}
		return _people;
	}

	private static string Loc(string key)
	{
		try
		{
			string text = LocalizationManager.Instance.LocaleResolver.GetString(key);
			return string.IsNullOrWhiteSpace(text) || text == key ? "" : Clean(text);
		}
		catch
		{
			return "";
		}
	}

	// 人物志：剧透选全知时给全部段落；另外两档只给第 0、1 段（外貌和公开身份），身世和真相不给
	private static string Person(string name)
	{
		string q = TextConverter.ToSimplified(name ?? "").Trim();
		if (q.Length == 0)
		{
			return "请给人名。";
		}
		var heroine = FindHeroine(q);
		var ids = People().Where(id => Speaker(id) == q).ToList();
		if (ids.Count == 0 && heroine != null)
		{
			ids = People().Where(id => Speaker(id) == StoryRomance.Name(heroine)).ToList();
		}
		if (ids.Count == 0)
		{
			ids = People().Where(id => Speaker(id).Contains(q) || (Loc("CharacterTitle/" + id)).Contains(q)).Take(3).ToList();
		}
		ids = ids.Where(id => Loc("CharacterIntro0/" + id).Length > 0 || Loc("CharacterIntro1/" + id).Length > 0).ToList();
		if (ids.Count == 0)
		{
			return "没有「" + name + "」的人物志。可以用 search 搜这个名字。";
		}
		bool all = LOMAssistant.Config.ConfigManager.AiSpoiler.Value == 0;
		var sb = new StringBuilder();
		foreach (string id in ids.Take(2))
		{
			string title = Loc("CharacterTitle/" + id);
			sb.Append("【").Append(Speaker(id)).Append(title.Length > 0 && title != "{title}" ? "（" + title + "）" : "").Append("】\n");
			for (int i = 0; i <= (all ? 4 : 1); i++)
			{
				string intro = Loc("CharacterIntro" + i + "/" + id);
				if (intro.Length > 0)
				{
					sb.Append(intro).Append('\n');
				}
			}
			if (!all)
			{
				sb.Append("（身世和真相的段落按剧透设置不给。）\n");
			}
		}
		return sb.ToString().Trim();
	}

	/// <summary>决斗关卡的说明和回合条件（谁在第几回合加入、第几回合结束、输了会不会死）。</summary>
	internal static string CombatText(string level, string indent)
	{
		if (!ConditionBook.Combats.TryGetValue(level ?? "", out List<string> lines) || lines.Count == 0)
		{
			return "";
		}
		var sb = new StringBuilder();
		sb.Append(indent).Append("关卡说明：").Append(lines[0]).Append('\n');
		if (lines.Count > 1)
		{
			sb.Append(indent).Append("战斗中的条件（「计数」是这场战斗自己记的数，如嘴攻唤醒的次数；没写判赢判输的「战斗结束」按当时的结果走剧情）：\n");
			foreach (string line in lines.Skip(1))
			{
				sb.Append(indent).Append("- ").Append(line).Append('\n');
			}
		}
		return sb.ToString();
	}

	private const int MaxEvents = 8;

	// 场所事件：按事件名、场所名、事件剧本里的对白找
	private static string Events(string query)
	{
		string q = TextConverter.ToSimplified(query ?? "").Trim();
		if (q.Length == 0)
		{
			return "请给关键词。";
		}
		var all = LOMAssistant.PositionEventCache.Instance.AllEvents();
		if (all.Count == 0)
		{
			return "场所事件表还没载入（游戏读档后进过一次剧情才拿得到）。可以先用 search 搜对白。";
		}
		ReadStorySystem read = ReadStorySystem.Instance;
		var hits = new List<(string Pool, Mortal.Story.PositionEventData Data, string Script, int Score)>();
		foreach (var (pool, data) in all)
		{
			string name = Clean(DataHelper.GetEventName(data, false));
			string place = LOMAssistant.PositionEventCache.PlaceName(pool);
			string script = LOMAssistant.PositionEventCache.Instance.ResultScript(data) ?? "";
			int score = name.Contains(q) ? 3 : place.Contains(q) ? 2 : 0;
			if (score == 0 && script.Length > 0)
			{
				StoryScript s = StoryScripts.Get(script);
				if (s != null && s.Talk.Any(t => StoryAtlas.LineText(t.Key).Contains(q)))
				{
					score = 1;
				}
			}
			if (score > 0)
			{
				hits.Add((pool, data, script, score));
			}
		}
		if (hits.Count == 0)
		{
			return "没有找到「" + query + "」相关的场所事件。";
		}
		var sb = new StringBuilder();
		foreach (var h in hits.OrderByDescending(x => x.Score).Take(MaxEvents))
		{
			sb.Append("【").Append(LOMAssistant.PositionEventCache.PlaceName(h.Pool)).Append(" · ").Append(Clean(DataHelper.GetEventName(h.Data, false))).Append("】");
			if (h.Script.Length > 0)
			{
				sb.Append("剧本 ").Append(h.Script).Append(LOMAssistant.MapEventHints.Unread(h.Script) ? "（玩家没看过）" : "");
			}
			sb.Append('\n');
			string desc;
			try
			{
				desc = Clean(LOMAssistant.PositionEventCache.Instance.GetPositionEventDesc(h.Data));
			}
			catch
			{
				desc = "";
			}
			sb.Append(desc.Length > 900 ? desc.Substring(0, 900) + "…" : desc).Append('\n');
		}
		if (hits.Count > MaxEvents)
		{
			sb.Append("……还有 ").Append(hits.Count - MaxEvents).Append(" 个，换个更具体的关键词。\n");
		}
		return sb.ToString().Trim();
	}

	// 读过多少（全部周目，按对白算）：没有对白时不写
	private static string ReadNote(IEnumerable<string> lines)
	{
		int? read = StoryScripts.ReadPercent(lines);
		return read == null ? "" : read == 0 ? "〔没读过〕" : read == 100 ? "〔读过〕" : "〔读过 " + read + "%〕";
	}

	// 这一局走过：剧本写下的剧情记录出现在这一局的剧情记录里
	private static bool VisitedThisRun(IEnumerable<StoryScript> scripts)
	{
		List<string> keys = PlayerStatManagerData.Instance?.StoryKeyList;
		if (keys == null || keys.Count == 0)
		{
			return false;
		}
		var visited = new HashSet<string>(keys, StringComparer.Ordinal);
		return scripts.Any(x => x != null && x.Records.Any(visited.Contains));
	}

	// 一个去处：这条分岔上的对白加接下去那个剧本的对白读过多少，接下去的剧本这一局走过没有
	private static string ExitReadNote(StoryScript s, StoryExit exit)
	{
		var lines = s.Talk.Where(t => t.Path.Count > 0 && StoryScripts.IsPrefix(t.Path, exit.Path)).Select(t => t.Key).ToList();
		StoryScript next = exit.Target.Length > 0 ? StoryScripts.Get(exit.Target) : null;
		if (next != null)
		{
			lines.AddRange(next.Lines);
		}
		string note = ReadNote(lines);
		if (next != null && VisitedThisRun(new[] { next }))
		{
			note += "（这一局走过）";
		}
		return note;
	}

	private static string MissionLine(StoryMission m) => m.Key + "「" + Clean(m.Title) + "」" + (m.Main ? "（主线）" : "（支线）") + (m.When.Length > 0 ? " " + Clean(m.When) : "");

	private static string Search(string query)
	{
		if (query.Length == 0)
		{
			return "请给关键词。";
		}
		string q = TextConverter.ToSimplified(query);
		var sb = new StringBuilder();
		var missions = StoryAtlas.Missions.Where(m => (m.Key + m.Title + m.Desc + m.Summary + m.SeriesTitle).Contains(q) || TextConverter.ToSimplified(m.Summary).Contains(q)).Take(15).ToList();
		if (missions.Count > 0)
		{
			sb.Append("【任务】\n");
			foreach (StoryMission m in missions)
			{
				sb.Append("- ").Append(MissionLine(m));
				string summary = Clean(m.Summary).Replace("\n", " ");
				if (summary.Length > 0)
				{
					sb.Append("：").Append(summary.Length > 60 ? summary.Substring(0, 60) + "…" : summary);
				}
				sb.Append('\n');
			}
		}
		if (FindHeroine(q) is StoryRomance.Heroine heroine)
		{
			sb.Append("【可结缘女主角】").Append(StoryRomance.Name(heroine)).Append("（用 heroine 看她的结缘路线）\n");
		}
		var endings = StoryAtlas.Endings.Concat(StoryAtlas.Achievements).Concat(StoryAtlas.Cgs).Where(e => (e.Title + e.Desc).Contains(q)).Take(10).ToList();
		if (endings.Count > 0)
		{
			sb.Append("【结局、死亡、成就、CG】（用 endings 看达成方法）\n");
			foreach (StoryEnding e in endings)
			{
				sb.Append("- ").Append(Clean(e.Title)).Append(e.Death ? "（死亡）" : "").Append('\n');
			}
		}
		var duels = ConditionBook.Combats.Where(c => c.Value.Count > 0 && c.Value[0].Contains(q)).Take(3).ToList();
		if (duels.Count > 0)
		{
			sb.Append("【决斗关卡】\n");
			foreach (var d in duels)
			{
				sb.Append("- 关卡 ").Append(d.Key).Append('\n').Append(CombatText(d.Key, "  "));
			}
		}
		try
		{
			var events = LOMAssistant.PositionEventCache.Instance.AllEvents()
				.Select(e => (Place: LOMAssistant.PositionEventCache.PlaceName(e.Pool), Name: Clean(DataHelper.GetEventName(e.Data, false))))
				.Where(e => e.Name.Contains(q) || e.Place.Contains(q)).Distinct().Take(8).ToList();
			if (events.Count > 0)
			{
				sb.Append("【场所事件】（用 events 看权重、条件和结果）\n");
				foreach (var e in events)
				{
					sb.Append("- ").Append(e.Place).Append(" · ").Append(e.Name).Append('\n');
				}
			}
		}
		catch
		{
		}
		var flags = AiAnalysis.FlagsNamed(q, 15);
		if (flags.Count > 0)
		{
			sb.Append("【旗标】（用 usage 看在哪改、在哪判定）\n");
			foreach (string f in flags)
			{
				sb.Append("- ").Append(f).Append('\n');
			}
		}
		var lines = StoryAtlas.SearchDialogue(q, 15);
		// 任务以外的剧本（地点事件、随机事件）也搜
		lines.AddRange(AiAnalysis.SearchOtherDialogue(q, 10));
		if (lines.Count > 0)
		{
			sb.Append("【对白】（剧本编号：片段）\n");
			foreach (var (script, snippet) in lines)
			{
				sb.Append("- ").Append(script).Append(StoryAtlas.Owner.TryGetValue(script, out StoryMission owner) ? "（任务 " + owner.Key + "「" + Clean(owner.Title) + "」）" : "").Append("：").Append(Clean(snippet)).Append('\n');
			}
		}
		return sb.Length > 0 ? sb.ToString() : "没有找到「" + query + "」。换个说法试试（人名、地名、任务名里的词）。";
	}

	/// <summary>这一局旗标的当前值（玩家在标题画面、没读档时为空）。</summary>
	internal static int? FlagNow(string id)
	{
		if (PlayerStatManagerData.Instance == null || UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Title")
		{
			return null;
		}
		try
		{
			return MissionManagerData.Instance?.GetFlag(id)?.State;
		}
		catch
		{
			return null;
		}
	}

	private static string FlagsNow(IEnumerable<string> ids)
	{
		var parts = new List<string>();
		foreach (string id in ids.Distinct())
		{
			if (FlagNow(id) is int v)
			{
				parts.Add(Clean(DataHelper.GetFlagName(id)) + " (" + id + ") = " + v);
			}
		}
		return string.Join("，", parts.ToArray());
	}

	private static string Quest(string key)
	{
		StoryMission m = StoryAtlas.Find(key) ?? StoryAtlas.Missions.FirstOrDefault(x => Clean(x.Title).Contains(key));
		if (m == null)
		{
			return "没有任务「" + key + "」。先用 search 找编号。";
		}
		var sb = new StringBuilder("任务 ").Append(MissionLine(m)).Append('\n');
		bool walked = m.Visited || VisitedThisRun(m.Scripts.Select(StoryScripts.Get));
		sb.Append("玩家进度：对白读过 ").Append(m.Read == null ? "（没有对白）" : m.Read + "%").Append("（全部周目合计）")
			.Append(PlayerStatManagerData.Instance != null ? (walked ? "；这一局走过" : "；这一局还没走过") : "").Append('\n');
		sb.Append("任务组：").Append(Clean(m.SeriesTitle)).Append(m.Inactive ? "；不会在地图上自己触发，只能由别的剧本接进来" : "").Append('\n');
		if (m.Desc.Length > 0)
		{
			sb.Append("说明：").Append(Clean(m.Desc)).Append('\n');
		}
		sb.Append("时间：").Append(Clean(m.When)).Append("；地点：").Append(Clean(m.Place)).Append('\n');
		string cond = Clean(StoryAtlas.ConditionText(m)).Replace("\n", " ");
		if (cond.Length > 0)
		{
			sb.Append("触发条件：").Append(cond).Append('\n');
			string now = FlagsNow(Regex.Matches(cond, @"\(([A-Z]\d{5})\)").Cast<Match>().Select(x => x.Groups[1].Value));
			if (now.Length > 0)
			{
				sb.Append("条件里的旗标这一局现在：").Append(now).Append("（拿它对照触发条件，判断玩家还能不能触发）\n");
			}
		}
		foreach (StoryEntry e in m.Entries)
		{
			sb.Append("从").Append(e.From != null ? "任务 " + e.From.Key + "「" + Clean(e.From.Title) + "」的" : "").Append("剧本 ").Append(e.Script).Append(" 接进来：").Append(PathText(e.Path)).Append('\n');
		}
		if (m.Next.Count > 0)
		{
			sb.Append("之后接到：").Append(string.Join("、", m.Next.Select(k => StoryAtlas.Find(k) is StoryMission n ? n.Key + "「" + Clean(n.Title) + "」" : k).ToArray())).Append('\n');
		}
		sb.Append("\n剧本（编号：剧情记录）：\n");
		foreach (string name in m.Scripts.Where(s => StoryAtlas.Owner.TryGetValue(s, out StoryMission o) && o == m))
		{
			StoryScript s = StoryScripts.Get(name);
			string first = s == null ? "" : s.Records.Select(StoryScripts.Legend).FirstOrDefault(t => t.Length > 0) ?? "";
			sb.Append("- ").Append(name).Append(s != null && s.Branches ? "（有分岔）" : "").Append(first.Length > 0 ? "：" + (first.Length > 80 ? first.Substring(0, 80) + "…" : first) : "").Append('\n');
		}
		return sb.ToString();
	}

	private static string Script(string name, int page)
	{
		StoryScript s = StoryScripts.Get(name);
		if (s == null)
		{
			return "没有剧本「" + name + "」。先用 search 或 quest 找编号。";
		}
		var sb = new StringBuilder("剧本 ").Append(name);
		if (StoryAtlas.Owner.TryGetValue(name, out StoryMission owner))
		{
			sb.Append("，属于任务 ").Append(MissionLine(owner));
		}
		sb.Append('\n');
		if (page <= 1)
		{
			var legends = s.Records.Select(StoryScripts.Legend).Where(t => t.Length > 0).Distinct().ToList();
			if (legends.Count > 0)
			{
				sb.Append("\n【剧情记录】（游戏回想里的文字，按剧本顺序，分岔各自的都在）\n");
				foreach (string t in legends)
				{
					sb.Append("- ").Append(t).Append('\n');
				}
			}
			if (s.Menus.Count > 0)
			{
				sb.Append("\n【选项】（〔〕里是这个选项后面的对白玩家读过多少，全部周目合计）\n");
				foreach (List<StoryStep> menu in s.Menus)
				{
					sb.Append("- ").Append(string.Join(" / ", menu.Select(x => Clean(StoryScripts.Describe(x)) + ReadNote(s.Talk.Where(t => t.Path.Any(p => p.Kind == x.Kind && p.Key == x.Key)).Select(t => t.Key))).ToArray())).Append('\n');
				}
			}
			if (s.Exits.Count > 0)
			{
				sb.Append("\n【去处】（分岔路径 ⇒ 接到哪；变化是这条路上的数值、好感、旗标、物品变化；〔〕里是这条路和接下去的剧本玩家读过多少（全部周目），以及这一局走过没有）\n");
				foreach (StoryExit exit in s.Exits)
				{
					string target = exit.ToTitle ? "回到标题（结局）" : exit.ToMap ? "回地图" : exit.Target.Length > 0 ? "剧本 " + exit.Target + (StoryAtlas.Owner.TryGetValue(exit.Target, out StoryMission t) && t != owner ? "（任务 " + t.Key + "「" + Clean(t.Title) + "」）" : "") : "（剧本结束）";
					sb.Append("- ").Append(PathText(exit.Path)).Append(exit.AfterScene ? "，战斗后" : "").Append(" ⇒ ").Append(target).Append(ExitReadNote(s, exit));
					if (exit.MainMission.Length > 0)
					{
						sb.Append("，主线推进到 ").Append(exit.MainMission);
					}
					var effects = s.EffectsOn(exit).Select(Clean).ToList();
					if (effects.Count > 0)
					{
						sb.Append("\n  变化：").Append(string.Join("；", effects.ToArray()));
					}
					sb.Append('\n');
				}
			}
			foreach (var (path, death, id) in s.Endings)
			{
				StoryEnding e = StoryAtlas.Endings.FirstOrDefault(x => x.Id == id);
				sb.Append(death ? "【死亡】" : "【结局】").Append(e != null ? Clean(e.Title) : id).Append("：").Append(PathText(path)).Append('\n');
			}
			foreach (var (path, level) in s.Fights)
			{
				sb.Append("【决斗】关卡 ").Append(level).Append("：").Append(PathText(path)).Append('\n');
				sb.Append(CombatText(level, "  "));
			}
		}
		// 对白按分岔分组：同一条路上的连在一起
		var dialogue = new StringBuilder();
		string lastPath = null;
		foreach (var (path, speaker, key) in s.Talk)
		{
			string text = StoryAtlas.LineText(key);
			if (text.Length == 0)
			{
				continue;
			}
			string p = PathText(path);
			if (p != lastPath)
			{
				dialogue.Append("\n〔").Append(p).Append("〕\n");
				lastPath = p;
			}
			dialogue.Append(Speaker(speaker)).Append("：").Append(text).Append('\n');
		}
		if (dialogue.Length > 0)
		{
			string all = dialogue.ToString();
			int pages = (all.Length + PageChars - 1) / PageChars;
			page = Math.Max(1, Math.Min(page, pages));
			string part = all.Substring((page - 1) * PageChars, Math.Min(PageChars, all.Length - (page - 1) * PageChars));
			sb.Append("\n【对白】第 ").Append(page).Append('/').Append(pages).Append(" 页（〔〕里是这段对白所在的分岔）\n").Append(part);
			if (page < pages)
			{
				sb.Append("\n……还有下一页：script 加 page=").Append(page + 1);
			}
		}
		return sb.ToString();
	}

	/// <summary>问题里的编号：阿拉伯数字或一到九十九的中文数字；没有为 0。</summary>
	private static int ChineseNumber(string q)
	{
		// 只认「第三」「生死簿3」「结局四」「3条」这种位置的数字，别把「一如人间」当成 1
		var m = System.Text.RegularExpressions.Regex.Match(q, @"(?:第|簿|局|亡)\s*([一二三四五六七八九十\d]+)|([一二三四五六七八九十\d]+)\s*[条号]");
		if (!m.Success)
		{
			return 0;
		}
		string s = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
		if (char.IsDigit(s[0]))
		{
			return int.TryParse(s, out int n) ? n : 0;
		}
		const string digits = "一二三四五六七八九";
		int ten = s.IndexOf('十');
		if (ten < 0)
		{
			return s.Length == 1 ? digits.IndexOf(s[0]) + 1 : 0;
		}
		int tens = ten == 0 ? 1 : digits.IndexOf(s[ten - 1]) + 1;
		int ones = ten + 1 < s.Length ? digits.IndexOf(s[ten + 1]) + 1 : 0;
		return tens * 10 + ones;
	}

	private static string EndingList(string query)
	{
		string q = TextConverter.ToSimplified(query);
		var sb = new StringBuilder();
		// 「生死簿第三条」「结局4」：按图鉴编号找
		int number = ChineseNumber(q);
		bool deaths = q.Contains("生死簿") || q.Contains("死亡") || q.Contains("死法");
		bool endings = q.Contains("汗青") || q.Contains("结局");
		bool byNumber = number > 0 && deaths != endings;
		void List(string title, List<StoryEnding> items)
		{
			var hits = items.Where(e => byNumber ? e.Number == number && e.Death == deaths : q.Length == 0 || (e.Title + e.Desc).Contains(q)).ToList();
			if (hits.Count == 0)
			{
				return;
			}
			sb.Append("【").Append(title).Append("】\n");
			foreach (StoryEnding e in hits.Take(q.Length == 0 ? 200 : 12))
			{
				sb.Append("- ").Append(e.Number > 0 ? e.Number + ". " : "").Append(Clean(e.Title)).Append(e.Death ? "（死亡）" : "").Append(e.Achieved ? "（玩家达成过）" : "");
				if (q.Length > 0)
				{
					if (e.Desc.Length > 0)
					{
						sb.Append("：").Append(Clean(e.Desc).Replace("\n", " "));
					}
					if (e.Ways.Count == 0)
					{
						// 游戏只有两条路记生死簿、汗青书：剧本跳到死亡或结局画面（ChangeScene），决斗输了（关卡上写着死法）；两样都没有就是哪里都触发不到
						sb.Append("\n  · 剧本和决斗里都没有走到它的地方。游戏代码里也只有这两条路会记下死法和结局，所以当前版本大概触发不到（没用上的条目）。照实告诉玩家，不要拿别的剧情去猜。");
					}
					foreach (StoryEndingWay w in e.Ways.Take(6))
					{
						sb.Append("\n  · 剧本 ").Append(w.Script).Append(w.Mission != null ? "（任务 " + w.Mission.Key + "「" + Clean(w.Mission.Title) + "」）" : "").Append("：").Append(PathText(w.Path)).Append(w.LostDuel ? "，决斗输了" : "");
					}
				}
				sb.Append('\n');
			}
		}
		List("结局与死亡", StoryAtlas.Endings);
		List("成就", StoryAtlas.Achievements);
		List("结局 CG", StoryAtlas.Cgs);
		return sb.Length > 0 ? sb.ToString() : "没有找到「" + query + "」相关的结局。";
	}

	// 玩家常用的叫法（好感里的人名、昵称），按 StoryRomance.All 的顺序
	/// <summary>按 StoryRomance.All 的顺序（即 Lover 编号）。</summary>
	internal static readonly string[][] Aliases =
	{
		new[] { "小师妹", "唐默铃", "默铃" },
		new[] { "瑞杏", "杏花仙", "咖啡" },
		new[] { "叶云裳", "云裳" },
		new[] { "虞小梅", "小梅" },
		new[] { "上官萤", "上官" },
		new[] { "夏侯兰", "小兰" },
		new[] { "郁竹", "小竹" },
		new[] { "魏菊", "小菊", "魏招弟" },
		new[] { "龙湘", "湘姊" },
	};

	internal static StoryRomance.Heroine FindHeroine(string name)
	{
		string q = TextConverter.ToSimplified(name ?? "");
		if (q.Length == 0)
		{
			return null;
		}
		for (int i = 0; i < StoryRomance.All.Length && i < Aliases.Length; i++)
		{
			StoryRomance.Heroine h = StoryRomance.All[i];
			if (Aliases[i].Any(a => q.Contains(a)) || q.Contains(StoryRomance.Name(h)))
			{
				return h;
			}
		}
		return null;
	}

	private static string Heroine(string name)
	{
		var h = FindHeroine(name);
		if (h == null)
		{
			return "「" + name + "」不是可结缘的女主角。可结缘的有：" + string.Join("、", StoryRomance.All.Select(StoryRomance.Name).ToArray());
		}
		var stops = StoryRomance.StopsOf(h);
		if (stops == null)
		{
			return "结缘路线还在整理，请稍后再问。";
		}
		var sb = new StringBuilder(StoryRomance.Name(h)).Append(" 的结缘路线");
		sb.Append("（玩家现在对她的好感 ").Append(StoryRomance.Affection(h)).Append(StoryRomance.IsLover(h) ? "，是现在的心上人" : "").Append(AiPersona.Bonded(h) ? "，已经结缘" : "，还没结缘").Append("）\n");
		foreach (var stop in stops)
		{
			sb.Append("- ").Append(MissionLine(stop.Mission));
			if (stop.Affection != 0)
			{
				sb.Append("，剧本里有加减她好感的地方（各分岔合计 ").Append(stop.Affection > 0 ? "+" : "").Append(stop.Affection).Append("；分岔互斥，实际走一条路拿到的少得多，具体哪条路加多少用 routes 查，不要把合计说成「最多能加」）");
			}
			if (stop.Steps.Count > 0)
			{
				sb.Append("，结缘旗标：").Append(string.Join("、", stop.Steps.Select(f => StoryRomance.StepName(f) + (StoryRomance.StepDone(f) ? "（已做到）" : "（未做到）")).ToArray()));
			}
			sb.Append('\n');
		}
		return sb.ToString();
	}
}
