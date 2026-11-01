using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Mortal.Core;

namespace LOMAssistant.Features.Story;

/// <summary>旗标、物品在剧本里的一处用法：在哪个剧本、走哪条分岔、做了什么（改成多少、给了几个、判定要多少）。</summary>
public sealed class StoryUse
{
	public string Script = "";
	/// <summary>剧本所属任务；没归到任务的（地点事件、战役等单独的剧本）为 null。</summary>
	public StoryMission Mission;
	/// <summary>改、给、收、判定、触发条件。</summary>
	public string Kind = "";
	public string Detail = "";
	public List<StoryStep> Path = new List<StoryStep>();
	/// <summary>改动（改旗标、给收物品）为 true，判定为 false。</summary>
	public bool Changes;
	/// <summary>分支判定：满足这条时走的那一支（Path 后面接上它就是这一支里面）；别的为 null。</summary>
	public StoryStep Arm;
}

/// <summary>
/// 反查：一面旗标、一件物品在哪些剧情里被改（给、收）、在哪些剧情里被判定。
/// 剧本读完后从 StoryScripts 的大概（Modifies、Checks）、任务的触发条件、条件文字本（ConditionBook）里整理出来；
/// 剧本重读（StoryAtlas.Version 变了）就重新整理。商店、弹珠、战役掉落这些不经过剧本的找不到。
/// </summary>
public static class StoryUsage
{
	private static readonly Dictionary<string, List<StoryUse>> Flags = new Dictionary<string, List<StoryUse>>(StringComparer.Ordinal);
	private static readonly Dictionary<string, List<StoryUse>> Items = new Dictionary<string, List<StoryUse>>(StringComparer.Ordinal);
	private static int _builtVersion = -1;

	// 条件里的旗标：运行时写的「名字 (M20062) 需=1」、Mod 自带的「旗标M20062〔名字〕 需=1」、没有说明的「A10001 需=1」
	private static readonly Regex FlagInCondition = new Regex(@"旗标([A-Za-z][A-Za-z0-9_]*)|\(([A-Za-z][A-Za-z0-9_]*)\)(?= 需| *$)|(?<=^|\s)([A-Z][A-Za-z]*\d[A-Za-z0-9_]*)(?= 需)");
	// 条件里的物品：「杂物_烤鱼 需>=1」
	private static readonly Regex ItemInCondition = new Regex(@"(书籍|杂物|贵重品)_(\S+?)(?= 需)");
	private static readonly Regex Clauses = new Regex(@" 且 | 或 |\n");
	// 条件里的好感：「好感度_虞小梅 需>=30」
	private static readonly Regex RelationInCondition = new Regex(@"好感度_(\S+?)(?= 需)");
	// 数值、技能、天赋（心相、性情、技能_南宫护心诀、变心、门派规模……）：条件里的「心相 需>=60」、分岔上的「心相 -30」
	private static readonly Regex StatInCondition = new Regex(@"(?<=^|\s)([^\s()（）]+) 需");
	private static readonly Regex StatEffect = new Regex(@"^(\S.*?) ([+-]?\d+)$");
	private static readonly Dictionary<string, List<StoryUse>> Stats = new Dictionary<string, List<StoryUse>>(StringComparer.Ordinal);

	/// <summary>剧本里判定过、改过的数值名（简体，条件里的写法，如「心相」「技能_南宫护心诀」）；剧本还没读完为空。</summary>
	public static IEnumerable<string> StatNames => Ensure() ? Stats.Keys.ToList() : new List<string>();

	/// <summary>剧本还没读完返回 null。</summary>
	public static List<StoryUse> Stat(string name) => Ensure() ? Stats.TryGetValue(name, out var list) ? list : new List<StoryUse>() : null;

	// 好感（按人名，简体）：只收判定的地方（分支判定、选项条件、任务触发条件）
	private static readonly Dictionary<string, List<StoryUse>> Relations = new Dictionary<string, List<StoryUse>>(StringComparer.Ordinal);

	/// <summary>这个人的好感在哪些剧情里被判定（人名用简体）。剧本还没读完返回 null。</summary>
	public static List<StoryUse> Relation(string name) => Ensure() ? Relations.TryGetValue(name, out var list) ? list : new List<StoryUse>() : null;

	/// <summary>剧本里改过、判定过的全部旗标编号（按名字找旗标用）；剧本还没读完为空。</summary>
	public static IEnumerable<string> FlagIds => Ensure() ? Flags.Keys.ToList() : new List<string>();

	/// <summary>剧本还没读完返回 null。</summary>
	public static List<StoryUse> Flag(string id) => Ensure() ? Flags.TryGetValue(id, out var list) ? list : new List<StoryUse>() : null;

	/// <summary>剧本还没读完返回 null。</summary>
	public static List<StoryUse> Item(GameItemType type, string id) => Ensure() ? Items.TryGetValue(ItemKey(type, id), out var list) ? list : new List<StoryUse>() : null;

	private static string ItemKey(GameItemType type, string id) => (int)type + ":" + id;

	private static bool Ensure()
	{
		if (!StoryAtlas.Ready)
		{
			return false;
		}
		if (_builtVersion != StoryAtlas.Version)
		{
			_builtVersion = StoryAtlas.Version;
			Build();
		}
		return true;
	}

	private static void Build()
	{
		var timer = Stopwatch.StartNew();
		Flags.Clear();
		Items.Clear();
		Relations.Clear();
		Stats.Clear();
		Dictionary<string, string> itemNames = ItemNames();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (StoryScript script in StoryScripts.All)
		{
			StoryAtlas.Owner.TryGetValue(script.Name, out StoryMission mission);
			foreach (var (path, method, id, value) in script.Modifies)
			{
				var use = new StoryUse { Script = script.Name, Mission = mission, Path = path, Changes = true };
				int.TryParse(value, out int amount);
				switch (method)
				{
					// 改好感：结缘路线用（StoryRomance），反查不收
					case "Character":
						continue;
					case "SetFlag":
						use.Kind = "改";
						use.Detail = "设成 " + amount;
						Add(Flags, id, use, seen);
						continue;
					case "AddFlag":
						use.Kind = "改";
						use.Detail = amount >= 0 ? "+" + amount : amount.ToString();
						Add(Flags, id, use, seen);
						continue;
				}
				GameItemType type = method.EndsWith("Book") ? GameItemType.書籍 : method.EndsWith("Misc") ? GameItemType.雜物 : GameItemType.貴重品;
				bool add = method.StartsWith("Add");
				use.Kind = add ? "获得" : "失去";
				use.Detail = add && amount > 1 ? "×" + amount : !add && amount > 0 ? "×" + amount : "";
				Add(Items, ItemKey(type, id), use, seen);
			}
			foreach (var (path, text) in script.Effects)
			{
				Match m = StatEffect.Match(text.Trim());
				// 好感另有反查；「暂存」是剧本里临时用的
				if (!m.Success || m.Groups[1].Value.Contains("好感") || m.Groups[1].Value.Contains("暂存") || m.Groups[1].Value.Contains("合计"))
				{
					continue;
				}
				string amount = m.Groups[2].Value;
				Add(Stats, m.Groups[1].Value, new StoryUse { Script = script.Name, Mission = mission, Path = path, Changes = true, Kind = "改", Detail = amount.StartsWith("-") || amount.StartsWith("+") ? amount : "+" + amount }, seen);
			}
			foreach (var (path, checkpoint, isSwitch) in script.Checks)
			{
				IEnumerable<string> texts = isSwitch
					? ConditionBook.Switches.TryGetValue(checkpoint, out List<string> arms) ? arms : Enumerable.Empty<string>()
					: ConditionBook.Conditions.TryGetValue(checkpoint, out string single) ? new[] { single } : Enumerable.Empty<string>();
				int arm = 0;
				foreach (string text in texts)
				{
					arm++;
					var template = new StoryUse { Script = script.Name, Mission = mission, Path = path, Arm = isSwitch ? new StoryStep { Kind = StoryStep.Branch, Key = checkpoint, Arm = arm } : null };
					AddConditions(text, isSwitch ? "分支判定" : "选项条件", template, itemNames, seen);
				}
			}
		}
		// 任务的触发条件（地图上什么时候出现）
		foreach (StoryMission mission in StoryAtlas.Missions)
		{
			foreach (ConditionResultItem condition in mission.Conditions)
			{
				string text;
				try
				{
					text = condition == null ? "" : DataHelper.ConditionTemplate(condition);
				}
				catch
				{
					continue;
				}
				AddConditions(text, "触发条件", new StoryUse { Script = mission.StartScript, Mission = mission }, itemNames, seen);
			}
		}
		FileLogger.Log($"[剧情回顾] 旗标、物品反查：{Flags.Count} 面旗标、{Items.Count} 件物品、{Stats.Count} 项数值，用时 {timer.ElapsedMilliseconds} 毫秒");
	}

	// 一段条件文字里每个用到旗标、物品的小条件各记一处（同一处同一个旗标只记一次）
	private static void AddConditions(string text, string kind, StoryUse template, Dictionary<string, string> itemNames, HashSet<string> seen)
	{
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		foreach (string raw in Clauses.Split(text))
		{
			string clause = raw.Trim();
			if (clause.Length == 0)
			{
				continue;
			}
			string shown = Readable(clause);
			foreach (Match m in FlagInCondition.Matches(clause))
			{
				string id = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
				Add(Flags, id, Copy(template, kind, shown), seen);
			}
			foreach (Match m in RelationInCondition.Matches(clause))
			{
				Add(Relations, TextConverter.ToSimplified(m.Groups[1].Value), Copy(template, kind, shown), seen);
			}
			foreach (Match m in StatInCondition.Matches(clause))
			{
				string name = TextConverter.ToSimplified(m.Groups[1].Value);
				// 旗标编号、好感、物品、时间、掷骰另有反查或不用查
				if (Regex.IsMatch(name, @"^[A-Za-z0-9_]+$") || name.StartsWith("好感度_") || name.StartsWith("时间_") || name.StartsWith("随机值") || name.StartsWith("旗标")
					|| Regex.IsMatch(name, @"^(书籍|杂物|贵重品)_") || name.Contains("暂存"))
				{
					continue;
				}
				Add(Stats, name, Copy(template, kind, shown), seen);
			}
			foreach (Match m in ItemInCondition.Matches(clause))
			{
				if (itemNames.TryGetValue(m.Groups[1].Value + "_" + m.Groups[2].Value, out string key))
				{
					Add(Items, key, Copy(template, kind, shown), seen);
				}
			}
		}
	}

	// Mod 自带条件文字里的「旗标M20062〔名字〕」写成和运行时一样的「名字 (M20062)」
	private static string Readable(string clause) =>
		Regex.Replace(Regex.Replace(clause, @"旗标(\w+)〔([^〕]*)〕", "$2 ($1)"), @"旗标(\w+)", "$1");

	private static StoryUse Copy(StoryUse template, string kind, string detail) =>
		new StoryUse { Script = template.Script, Mission = template.Mission, Path = template.Path, Arm = template.Arm, Kind = kind, Detail = detail };

	private static void Add(Dictionary<string, List<StoryUse>> index, string key, StoryUse use, HashSet<string> seen)
	{
		string path = string.Join("|", use.Path.Select(s => s.Kind + s.Key + s.Arm).ToArray());
		if (!seen.Add(key + "\u0001" + use.Script + "\u0001" + path + "\u0001" + use.Kind + "\u0001" + use.Detail))
		{
			return;
		}
		if (!index.TryGetValue(key, out List<StoryUse> list))
		{
			index[key] = list = new List<StoryUse>();
		}
		list.Add(use);
	}

	// 条件里写的是物品的中文名（「杂物_烤鱼」）：用游戏里的名字和资源名（「Misc0001_唐門改良金創藥」）对上编号
	private static Dictionary<string, string> ItemNames()
	{
		var names = new Dictionary<string, string>(StringComparer.Ordinal);
		ItemDatabase items = ItemDatabase.Instance;
		if (items == null)
		{
			return names;
		}
		foreach (var (prefix, collection) in new[] { ("书籍", items.Books), ("杂物", items.Miscs), ("贵重品", items.Special) })
		{
			foreach (ItemData item in collection?.List?.OfType<ItemData>() ?? Enumerable.Empty<ItemData>())
			{
				string key = ItemKey(item.ItemType, item.Id);
				string local = DataHelper.GetItemName(item.ItemType, item.Id);
				int cut = item.name.IndexOf('_');
				foreach (string name in new[] { local, cut >= 0 ? item.name.Substring(cut + 1) : "" })
				{
					if (!string.IsNullOrEmpty(name))
					{
						names[prefix + "_" + TextConverter.ToSimplified(name)] = key;
					}
				}
			}
		}
		return names;
	}
}
