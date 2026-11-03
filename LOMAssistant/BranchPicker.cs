using System;
using System.Collections.Generic;
using System.Linq;
using LOMAssistant.Config;
using LOMAssistant.Features.Story;
using Mortal.Core;
using Mortal.Story;

namespace LOMAssistant;

/// <summary>
/// 分支判定手动选择（设置「分支判定手动选择」，默认关，只有全开档打开；思路参考玩家做的「分支前瞻」）：
/// 剧本走到不带随机值的分支判断时，算出每一支的条件、现在的值、对白看过没、接到哪里，
/// 换一支能看到没读过的对白或接到别的剧情才在随机事件选择框里停下（只是多一句、都看过的不打扰）。
/// 选了哪支由 BranchOverride 走过去：默认直接走、不改数值，打开「选分支时自动调整数值」时先改数值。
/// </summary>
internal static class BranchPicker
{
	internal sealed class Arm
	{
		/// <summary>第几支（1 起算；比条件数多 1 的是「以上都不满足」）。</summary>
		public int Branch;
		/// <summary>条件（带现在的值）；「以上都不满足」那支写这几个字。</summary>
		public string Condition = "";
		/// <summary>现在成不成立；读不出、「以上都不满足」那支为 null。</summary>
		public bool? Holds;
		public int Lines, Unread;
		/// <summary>接到哪里（剧情段名、回地图、结局），没有单独的去处为空。</summary>
		public string[] Exits = new string[0];
		// 显示时逐项翻译，不从拼好的句子反拆。
		public string Exit => string.Join("；", Exits);
		// 判断去处用完整的剧本编号和去处类型，不受标题语言、截短或显示条数影响。
		public HashSet<string> Destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	}

	public static bool Enabled => ConfigManager.EnableBranchPicker.Value;

	/// <summary>
	/// 这个判断的各支和游戏现在会走的那支（predicted，算不出为 0）。按次数判断的、找不到的、剧本里分不出哪句属于哪一支的（老写法每支都记成 0）返回 null。
	/// </summary>
	public static List<Arm> Arms(string name, string script, out int predicted)
	{
		predicted = 0;
		SwitchResultData data = SwitchRandom.Find(name);
		StoryScript s = StoryScripts.Get(script);
		if (data == null || s == null || data.GetFieldValue<bool>("_useCount"))
		{
			return null;
		}
		List<ConditionResultItem> items = data.GetFieldValue<List<ConditionResultItem>>("_items");
		if (items == null || items.Count == 0)
		{
			return null;
		}
		int fallback = items.Count + 1;
		var steps = s.Talk.SelectMany(t => t.Path).Concat(s.Exits.SelectMany(e => e.Path)).Where(st => st.Kind == StoryStep.Branch && st.Key == name).ToList();
		if (!steps.Any(st => st.Arm > 0))
		{
			return null;
		}
		// 「以上都不满足」那支在剧本里写成 else 时记 0，写成「== 条件数 + 1」时记条件数 + 1，两种都认
		bool On(List<StoryStep> path, int branch) => path.Any(st => st.Kind == StoryStep.Branch && st.Key == name && (st.Arm == branch || (branch == fallback && st.Arm == 0)));
		ReadStorySystem read = ReadStorySystem.Instance;
		var arms = new List<Arm>();
		for (int branch = 1; branch <= fallback; branch++)
		{
			var arm = new Arm { Branch = branch };
			if (branch <= items.Count)
			{
				arm.Condition = TextConverter.ToSimplifiedUnlessJapanese(DataHelper.ExtractConditions(items[branch - 1], out _));
				arm.Holds = ConditionSolver.Holds(items[branch - 1]);
			}
			else
			{
				arm.Condition = "以上条件都不满足";
			}
			foreach (var (path, _, key) in s.Talk.Where(t => On(t.Path, branch)))
			{
				arm.Lines++;
				if (read != null && !read.ExistKey(key))
				{
					arm.Unread++;
				}
			}
			var exits = s.Exits.Where(e => On(e.Path, branch)).ToList();
			arm.Destinations.UnionWith(exits.Select(DestinationKey).Where(x => x.Length > 0));
			arm.Exits = exits
				.Select(e => e.ToTitle ? "回到标题（结局）" : e.ToMap ? "回到地图" : e.Target.Length > 0 ? "接到「" + SwitchRandom.Segment(e.Target) + "」" : "")
				.Where(x => x.Length > 0).Distinct().Take(2).ToArray();
			arms.Add(arm);
		}
		// 现在会走哪支：前面的支都读得出才算得准
		foreach (Arm arm in arms)
		{
			if (arm.Branch == fallback)
			{
				predicted = fallback;
				break;
			}
			if (arm.Holds == null)
			{
				break;
			}
			if (arm.Holds == true)
			{
				predicted = arm.Branch;
				break;
			}
		}
		return arms;
	}

	// 返回地图、标题和接到剧本分别编号；剧本编号的大小写按 StoryScripts 的查找方式比较。
	private static string DestinationKey(StoryExit exit) =>
		exit.ToTitle ? "title" : exit.ToMap ? "map" : exit.Target.Length > 0 ? "script:" + exit.Target : "";

	/// <summary>
	/// 值得停下来问：换一支能看到没读过的对白，或者接到和现在会走的那支不一样的地方。
	/// 设置里打开「已读分支也暂停」时，换一支有对白就停，看没看过都算。
	/// </summary>
	public static bool Worth(List<Arm> arms, int predicted)
	{
		Arm now = arms.Find(a => a.Branch == predicted);
		bool seen = ConfigManager.BranchPickerSeen.Value;
		return arms.Any(a => a != now && (a.Unread > 0 || (seen && a.Lines > 0) || (now != null && a.Destinations.Count > 0 && !a.Destinations.SetEquals(now.Destinations))));
	}

	/// <summary>这一支的对白看过没。</summary>
	public static string ReadState(Arm arm) =>
		arm.Lines == 0 ? "没有对白" : arm.Unread == arm.Lines ? RichText.Golden("没看过") : arm.Unread > 0 ? RichText.Golden("看过一半") : "看过";
}
