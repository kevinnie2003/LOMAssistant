using System.Collections.Generic;
using System.Linq;
using System.Text;
using LOMAssistant.Features.Story;
using Mortal.Core;

namespace LOMAssistant.Features.Ai;

/// <summary>
/// 主动陪玩发给模型的文字：攻略助手陪玩时的规则（人设的在 AiPersona.CompanionPrompt）、剧透规则、
/// 每次开口前的【此刻】【刚才】【事件】。规则按界面语言各写一份；现状和事件是代码整理的事实，和问答的【此刻】一样用中文写，
/// 里面的对白、选项、数值名沿用游戏当前语言。
/// </summary>
internal static class AiCompanionPrompts
{
	private static string L(string zh, string en, string ko, string ja, string hant) => AiPrompts.L(zh, en, ko, ja, hant);

	/// <summary>调试记录里标出这是小伙伴的话。</summary>
	public static string DebugWho(AiPersona persona) => "主动陪玩·" + (persona?.Name ?? "攻略助手");

	/// <summary>主动开口时的剧透规则：请求不带工具，模型只看得到事件里写的；「全说」时事件里会多带没选的路、差的条件。</summary>
	public static string Spoiler(int level)
	{
		switch (level)
		{
			case 1:
				return L("只说已经发生的事，不谈之后会怎样。",
					"Talk only about what has already happened, not about what comes after.",
					"이미 일어난 일만 말하고, 앞으로 어떻게 될지는 말하지 마세요.",
					"すでに起きたことだけを話し、この先どうなるかは話さないでください。",
					"只講已經發生的事，不談之後會怎樣。");
			case 2:
				return L("只说已经发生的事，不谈之后会怎样；不说具体数字，只说升降和该往哪个方向使劲。",
					"Talk only about what has already happened, not about what comes after; give no exact numbers, only whether things went up or down and which way to push.",
					"이미 일어난 일만 말하고, 앞으로 어떻게 될지는 말하지 마세요. 구체적인 숫자는 말하지 말고, 올랐는지 내렸는지와 어느 방향으로 힘써야 할지만 말하세요.",
					"すでに起きたことだけを話し、この先どうなるかは話さないでください。具体的な数字は言わず、上がったか下がったか、どの方向に力を入れるかだけを伝えてください。",
					"只講已經發生的事，不談之後會怎樣；不講具體數字，只講升降和該往哪個方向努力。");
			default:
				return L("【事件】里有时会写没选的那条路会怎样、还差什么条件、之后接到哪里：可以拿来提醒他。",
					"The 【事件】 sometimes says what the path not taken would have done, which condition fell short or where things lead next: you may use that to give him a heads-up.",
					"【事件】에는 고르지 않은 쪽이 어떻게 되는지, 어떤 조건이 모자랐는지, 다음에 어디로 이어지는지가 적혀 있을 때가 있습니다. 그것으로 그에게 귀띔해 주어도 됩니다.",
					"【事件】には、選ばなかった道がどうなるか、どの条件が足りなかったか、この先どこへ続くかが書かれていることがあります。それを使って彼に一言伝えてもかまいません。",
					"【事件】裡有時會寫沒選的那條路會怎樣、還差什麼條件、之後接到哪裡：可以拿來提醒他。");
		}
	}

	/// <summary>系统提示词：选了人设用她的，没选就是攻略助手。同一位、同一档设置下每次都一样，服务商的缓存才命中。</summary>
	public static string SystemPrompt(AiPersona persona, bool fast)
	{
		string spoiler = Spoiler(LOMAssistant.Config.ConfigManager.AiSpoiler.Value);
		return persona != null ? persona.CompanionPrompt(AiPrompts.LanguageRule, spoiler, fast) : Assistant(spoiler);
	}

	private static string Assistant(string spoiler)
	{
		return L(
			"你是武侠游戏《活侠传》的攻略助手，在游戏 Mod「咖啡伴侣」里陪玩家玩。玩家操控的主角是赵活。\n\n"
				+ "每隔一阵你会收到三段说明：【此刻】是现状；【刚才】是刚发生的对白，「某人：……」是那个人说的话；【事件】是玩家刚做的事和结果。\n"
				+ "- 对【事件】说一句看法：这个结果意味着什么、接下来留意什么。一两句，不超过 60 字，语气平实。\n"
				+ "- 只根据给你的内容说：没给的剧情、数字、条件不要编，之后的剧情不要猜。\n"
				+ "- 不复述原话，不提工具、文件、数据这些词，不写任何编号。\n",
			"You are the strategy guide for the wuxia game Legend of Mortal (活侠传), keeping the player company inside the game mod Coffee Companion. The protagonist the player controls is Zhao Huo (赵活).\n\n"
				+ "From time to time you receive three notes: 【此刻】 is the current situation; 【刚才】 is the dialogue that just happened, where 「someone：……」 is what that person said; 【事件】 is what the player just did and how it turned out.\n"
				+ "- Say one thing about the 【事件】: what the result means and what to watch next. One or two sentences, under 40 words, in a plain tone.\n"
				+ "- Speak only from what you are given: do not invent story, numbers or conditions that were not given, and do not guess at later story.\n"
				+ "- Do not repeat the original lines, do not mention tools, files or data, and never write IDs.\n",
			"당신은 무협 게임 『활협전』의 공략 도우미로, 게임 모드 「커피 동반자」 안에서 플레이어와 함께합니다. 플레이어가 조작하는 주인공은 조활(赵活)입니다.\n\n"
				+ "이따금 세 가지 자료를 받습니다. 【此刻】는 현재 상황, 【刚才】는 방금 오간 대사로 「누구：……」는 그 사람이 한 말이고, 【事件】는 플레이어가 방금 한 일과 그 결과입니다. 표식은 모드의 중국어 내부 표기입니다.\n"
				+ "- 【事件】에 대해 한마디 하세요. 이 결과가 무엇을 뜻하는지, 다음에 무엇을 눈여겨볼지를 한두 문장, 80자 이내로 담담하게 말하세요.\n"
				+ "- 주어진 내용만으로 말하세요. 주어지지 않은 스토리, 숫자, 조건을 지어내지 말고, 이후 스토리를 추측하지 마세요.\n"
				+ "- 원문을 되풀이하지 말고, 도구, 파일, 데이터라는 말은 하지 말고, 번호는 쓰지 마세요.\n",
			"あなたは武侠ゲーム『活俠傳』の攻略アシスタントで、ゲームMod「珈琲のおとも」の中でプレイヤーに付き添います。プレイヤーが操作する主人公は趙活（赵活）です。\n\n"
				+ "ときどき三つの説明が届きます。【此刻】は今の状況、【刚才】はいま交わされた台詞で「誰々：……」はその人の言葉、【事件】はプレイヤーがいましたことと、その結果です。\n"
				+ "- 【事件】について一言。この結果が何を意味するか、次に何に気をつけるかを、一、二文、80字以内で、淡々と伝えてください。\n"
				+ "- 渡された内容だけをもとに話してください。渡されていないストーリー、数字、条件を作らず、この先のストーリーを推測しないでください。\n"
				+ "- 原文を繰り返さず、ツール、ファイル、データという言葉は使わず、番号は書かないでください。\n",
			"你是武俠遊戲《活俠傳》的攻略助手，在遊戲 Mod「咖啡伴侶」裡陪玩家玩。玩家操控的主角是趙活（資料裡寫作「赵活」）。\n\n"
				+ "每隔一陣你會收到三段說明：【此刻】是現況；【刚才】是剛發生的對白，「某人：……」是那個人說的話；【事件】是玩家剛做的事和結果。\n"
				+ "- 對【事件】說一句看法：這個結果代表什麼、接下來留意什麼。一兩句，不超過 60 字，語氣平實。\n"
				+ "- 只根據給你的內容說：沒給的劇情、數字、條件不要編，之後的劇情不要猜。\n"
				+ "- 不複述原話，不提工具、檔案、資料這些詞，不寫任何編號。\n")
			+ "- " + RecentRule + "\n"
			+ "- " + spoiler + "\n"
			+ "- " + AiPrompts.LanguageRule;
	}

	/// <summary>【此刻】：关系、日期、在做什么。</summary>
	public static string State(AiPersona persona, string scene)
	{
		var sb = new StringBuilder(persona != null ? persona.CompanionState() : AiPersona.RelationText(null));
		GameTime time = PlayerStatManagerData.Instance?.GameTime;
		if (!ReferenceEquals(time, null))
		{
			sb.Append("游戏里现在是").Append(StoryAtlas.DateText(time)).Append("。");
		}
		switch (scene)
		{
			case "Story": sb.Append("赵活正在经历一段剧情。"); break;
			case "Free": sb.Append("赵活在养成地图上安排这一旬的行动。"); break;
			case "Combat": sb.Append("赵活在决斗。"); break;
			case "GameOver": sb.Append("赵活这一局已经结束。"); break;
			case "End": sb.Append("赵活走到了结局。"); break;
		}
		return sb.ToString();
	}

	/// <summary>
	/// 每次开口前发的那一条：现状、上次开口之后的对白、最近说过的几句话、这次的事件。
	/// 最近说过的话：换一段剧情前面的往来就不带了，她看不到自己刚说过什么，会一再说出同一句；带上最近几句让她知道。
	/// </summary>
	public static string Turn(string state, IList<string> lines, IList<string> recent, string card)
	{
		var sb = new StringBuilder("【此刻】").Append(state).Append('\n');
		if (lines.Count > 0)
		{
			sb.Append("【刚才】\n");
			foreach (string line in lines)
			{
				sb.Append(line).Append('\n');
			}
		}
		if (recent.Count > 0)
		{
			sb.Append("【你最近说过】\n");
			foreach (string said in recent)
			{
				sb.Append("- ").Append(said.Replace("\n", " ")).Append('\n');
			}
		}
		return sb.Append("【事件】\n").Append(card).ToString();
	}

	/// <summary>规则里解释【你最近说过】的那一条（人设和攻略助手共用）。只说这是什么、别原样重复；怎么说不多管，管多了话反而刻意。</summary>
	public static string RecentRule => L(
		"有时还会收到【你最近说过】，是你前几次说的话，别原样重复。",
		"Sometimes you also receive 【你最近说过】, the last few things you said; do not repeat them word for word.",
		"가끔 【你最近说过】도 받습니다. 당신이 최근 몇 번 한 말이니, 그대로 되풀이하지 마세요.",
		"ときどき【你最近说过】も届きます。あなたが最近言った言葉なので、そのまま繰り返さないでください。",
		"有時還會收到【你最近说过】，是你前幾次說的話，別原樣重複。");

	private static string Quoted(IEnumerable<string> texts) => string.Join("、", texts.Select(t => "「" + t + "」").ToArray());

	private static string After(IList<string> changes) => changes.Count > 0 ? "随后：" + string.Join("、", changes.ToArray()) + "。" : "";

	/// <summary>选了一个选项。note、otherNotes 只在剧透「全说」时给（选项提示里那行后果说明）。</summary>
	public static string Choice(string chosen, IList<string> others, IList<string> changes, string note, IList<(string Text, string Note)> otherNotes)
	{
		var sb = new StringBuilder("赵活选了「").Append(chosen).Append("」");
		if (others.Count > 0)
		{
			sb.Append("（当时还摆着：").Append(Quoted(others)).Append("）");
		}
		sb.Append("。").Append(After(changes));
		if (note.Length > 0)
		{
			sb.Append("这个选项的后果说明：").Append(note).Append("。");
		}
		foreach (var (text, other) in otherNotes)
		{
			sb.Append("没选的「").Append(text).Append("」会是：").Append(other).Append("。");
		}
		return sb.ToString();
	}

	public static string Dice(string shown, bool fate, IList<string> changes) =>
		"赵活掷了一次骰，游戏记下：" + shown + (fate ? "（用了命运点）" : "") + "。" + After(changes);

	/// <summary>掷骰时玩家直接点了线路（Mod 的骰子手动选择）：没有可信的点数，只说判定走到了哪个结果。</summary>
	public static string DicePicked(string outcome, IList<string> changes) => "赵活遇上一次判定，走到的结果是：" + outcome + "。" + After(changes);

	public static string Changes(IList<string> changes) => "赵活身上有了变化：" + string.Join("、", changes.ToArray()) + "。";

	/// <summary>这次变化里有她自己的好感。</summary>
	public static string SelfAffection(bool up) => up ? "其中你对赵活的好感升了。" : "其中你对赵活的好感降了。";

	public static string Duel(bool win) => "赵活刚打完一场决斗，" + (win ? "赢了。" : "输了。");

	/// <summary>战役结束：游戏的结束类型（PlayerDie、FriendWin、EnemyWin、Timeout）。</summary>
	public static string Battle(string type)
	{
		switch (type)
		{
			case "FriendWin": return "赵活这一边打赢了一场战役。";
			case "EnemyWin": return "赵活这一边打输了一场战役。";
			case "PlayerDie": return "赵活在战役里倒下了。";
			default: return "一场战役打到了时限，结束了。";
		}
	}

	public static string Ending(string title, string desc, bool death)
	{
		string text = death ? "赵活死了，这一局到此为止" : "赵活走到了一个结局";
		return text + (title.Length > 0 ? "：《" + title + "》。" : "。") + desc;
	}

	public static string Record(string legend) => "一段剧情告一段落，游戏的剧情记录写下：" + legend;

	public static string Picture() => "画面上出现了一幅插画，是这段剧情的要紧处。";

	public static string NewRound(string date, int actions) => "新的一旬开始了：" + date + "，这一旬能行动 " + actions + " 次。";

	public static string Deadline(IList<string> items) => "有几件条件已经满足、快要过期的事：" + string.Join("；", items.ToArray()) + "。";

	public static string DeadlineItem(string title, int left, string place) =>
		"「" + title + "」" + (left == 0 ? "这一旬是最后机会" : "还剩 " + left + " 旬") + (place.Length > 0 ? "，要去「" + place + "」" : "");

	/// <summary>玩家点了气泡去追问：刚才那句话和它说的事，接在下一个问题前面告诉 AI。</summary>
	public static string FollowUp(string remark, string card) =>
		"刚才在游戏画面上，你对一件事说过：「" + remark.Replace("\n", " ") + "」。当时的事：" + card.Replace("\n", " ");
}
