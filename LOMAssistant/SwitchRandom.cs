using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LOMAssistant.Features.Story;
using Mortal.Core;
using Mortal.Story;
using UnityEngine;

namespace LOMAssistant;

/// <summary>
/// 条件里带随机值的分支判断（SwitchResultData，如「随机值_0_100 ≤ 好感度_虞小梅」）：
/// 算出这次随机值取每个数时会走哪条路，得到每条路的概率；随机事件选择框里选了哪条路，
/// 游戏执行这次判断时就把随机值定成让那条路成立的数（好感、旗标等其他条件照常判断）。
/// 没在选择框里点名时，按「总是」「不出」（EventRules，编号「Switch:判断名#第几支」）重新分配概率。
/// </summary>
internal static class SwitchRandom
{
	internal sealed class Arm
	{
		/// <summary>第几支（1 起算；比分支数多 1 的是「以上都不满足」）。</summary>
		public int Branch;
		/// <summary>随机值取多少个数会走到这里（即权重）。</summary>
		public int Count;
		/// <summary>走到这里的一个随机值。</summary>
		public int Value;
	}

	/// <summary>选择框里选定的路：判断名和要用的随机值，游戏下次执行这个判断时用掉。</summary>
	internal static (string Name, int Value)? Pending;

	/// <summary>不为 null 时，读随机值一律返回它（限在各自的范围内）。</summary>
	private static int? _override;

	internal static string RuleKey(string name, int branch) => "Switch:" + name + "#" + branch;

	internal static SwitchResultData Find(string name)
	{
		foreach (SwitchResultConfig config in DataHelper.SwitchConfigs())
		{
			SwitchResultData data = config.GetData(name);
			if (data != null)
			{
				return data;
			}
		}
		return null;
	}

	/// <summary>
	/// 这个判断现在会走第几支（1 起算，比条件数多 1 的是「以上都不满足」），只读条件、不执行（Execute 会加执行次数、用掉选定的分支）。
	/// 按次数判断的、条件里带随机值的、用到 unstable 里的变量（剧本自己在前面会改的旗标）的、找不到的返回 null。
	/// </summary>
	internal static int? Peek(string name, ICollection<string> unstable)
	{
		SwitchResultData data = Find(name);
		if (data == null || Traverse.Create(data).Field("_useCount").GetValue<bool>() || Arms(data) != null)
		{
			return null;
		}
		List<ConditionResultItem> items = Traverse.Create(data).Field("_items").GetValue<List<ConditionResultItem>>();
		if (items == null)
		{
			return null;
		}
		// 条件里用到剧本自己在前面会改的旗标：现在的值不准
		foreach (ConditionResultItem item in items)
		{
			foreach (StatCompareItem compare in item.GetFieldValue<List<StatCompareItem>>("_items") ?? new List<StatCompareItem>())
			{
				foreach (StatGroupVariable variable in new[] { compare.GetFieldValue<StatGroupVariable>("_value1"), compare.GetFieldValue<StatGroupVariable>("_value2") })
				{
					if (variable != null && unstable.Any(id => variable.name.Contains(id)))
					{
						return null;
					}
				}
			}
		}
		for (int i = 0; i < items.Count; i++)
		{
			if (items[i].Check())
			{
				return i + 1;
			}
		}
		return items.Count + 1;
	}

	/// <summary>这个判断能走到的各条路（只有条件里带随机值时才算；没有随机值、按次数判断的返回 null）。</summary>
	internal static List<Arm> Arms(string name)
	{
		SwitchResultData data = Find(name);
		return data != null ? Arms(data) : null;
	}

	private static List<Arm> Arms(SwitchResultData data)
	{
		var tr = Traverse.Create(data);
		if (tr.Field("_useCount").GetValue<bool>())
		{
			return null;
		}
		List<ConditionResultItem> items = tr.Field("_items").GetValue<List<ConditionResultItem>>();
		if (items == null || items.Count == 0)
		{
			return null;
		}
		int lo = int.MaxValue, hi = int.MinValue;
		foreach (ConditionResultItem item in items)
		{
			foreach (StatCompareItem compare in item.GetFieldValue<List<StatCompareItem>>("_items") ?? new List<StatCompareItem>())
			{
				foreach (StatGroupVariable group in new[] { compare.GetFieldValue<StatGroupVariable>("_value1"), compare.GetFieldValue<StatGroupVariable>("_value2") })
				{
					Ranges(group, ref lo, ref hi, 0);
				}
			}
		}
		if (lo > hi)
		{
			return null;
		}
		// 随机值每个数都试一遍，数出每条路占几个数（游戏的随机值是 Random.Range(min, max + 1)，每个数机会相同）
		int step = Mathf.Max(1, (hi - lo + 1) / 2000);
		var arms = new Dictionary<int, Arm>();
		try
		{
			for (long v = lo; v <= hi; v += step)
			{
				_override = (int)v;
				int branch = items.Count + 1;
				for (int j = 0; j < items.Count; j++)
				{
					if (items[j].Check())
					{
						branch = j + 1;
						break;
					}
				}
				if (!arms.TryGetValue(branch, out Arm arm))
				{
					arms[branch] = arm = new Arm { Branch = branch, Value = (int)v };
				}
				arm.Count++;
			}
		}
		finally
		{
			_override = null;
		}
		return arms.Values.OrderBy(a => a.Branch).ToList();
	}

	private static void Ranges(StatGroupVariable group, ref int lo, ref int hi, int depth)
	{
		if (group == null || depth > 4)
		{
			return;
		}
		foreach (StatValueReference r in group.GetFieldValue<List<StatValueReference>>("_value") ?? new List<StatValueReference>())
		{
			if (r == null)
			{
				continue;
			}
			if (r.CheckType == StatCheckType.隨機值)
			{
				lo = Mathf.Min(lo, r.GetFieldValue<int>("_randomMin"));
				hi = Mathf.Max(hi, r.GetFieldValue<int>("_randomMax"));
			}
			else if (r.CheckType == StatCheckType.數值群組)
			{
				Ranges(r.GetFieldValue<StatGroupVariable>("_statGroup"), ref lo, ref hi, depth + 1);
			}
		}
	}

	/// <summary>一条路写成「接到哪段剧情」（选项名），条件写在下面一行。读条件文字时随机值定住，不动游戏的随机数。</summary>
	internal static (string Name, string Condition) Describe(string name, string script, int branch, int value)
	{
		string condition;
		_override = value;
		try
		{
			condition = RichText.Strip(DataHelper.SwitchBranchCondition(name, branch)).Trim();
			// 「随机值_0_100 需<=72 → 当前0」：当前值是定住的数，没有意义，写成「随机数（0～100）≤ 72」
			condition = System.Text.RegularExpressions.Regex.Replace(condition, @"随机值_(-?\d+)_(-?\d+)\s*需\s*(<=|>=|<|>|=|!=)\s*(-?\d+)\s*→\s*当前\s*-?\d+",
				m => "随机数（" + m.Groups[1].Value + "～" + m.Groups[2].Value + "）" + (m.Groups[3].Value == "<=" ? "≤" : m.Groups[3].Value == ">=" ? "≥" : m.Groups[3].Value) + " " + m.Groups[4].Value);
		}
		finally
		{
			_override = null;
		}
		string where = "";
		try
		{
			StoryScript s = StoryScripts.Get(script);
			int arm = Find(name) is SwitchResultData data && branch > (data.GetFieldValue<List<ConditionResultItem>>("_items")?.Count ?? 0) ? 0 : branch;
			if (s != null)
			{
				where = string.Join("；", s.Exits
					.Where(e => e.Path.Any(p => p.Kind == StoryStep.Branch && p.Key == name && p.Arm == arm))
					.Select(e => e.ToTitle ? "回到标题（结局）" : e.ToMap ? "回到地图" : e.Target.Length > 0 ? "接到「" + Segment(e.Target) + "」" : "")
					.Where(x => x.Length > 0).Distinct().Take(2).ToArray());
			}
		}
		catch
		{
			where = "";
		}
		return where.Length > 0 ? (where, condition) : (condition.Length > 0 ? condition : "第 " + branch + " 条路", "");
	}

	// 剧情段的名字：有剧情记录用记录，没有就用它的第一句台词（剧本名玩家看不懂）
	private static string Segment(string script)
	{
		string name = StoryScripts.SegmentName(script);
		if (name != script)
		{
			return name;
		}
		StoryScript s = StoryScripts.Get(script);
		string line = s != null ? s.Lines.Select(StoryAtlas.LineText).FirstOrDefault(x => x.Length > 0) ?? "" : "";
		return line.Length == 0 ? script : line.Length > 20 ? line.Substring(0, 20) + "…" : line;
	}

	private static bool HasRules(string name)
	{
		string prefix = "Switch:" + name + "#";
		string raw = LOMAssistant.Config.ConfigManager.EventRules.Value ?? "";
		return raw.Contains(prefix);
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(SwitchResultData), nameof(SwitchResultData.Execute))]
	public static void Execute_Prefix(SwitchResultData __instance)
	{
		try
		{
			string name = __instance.name;
			if (Pending is (string pendingName, int value) && pendingName == name)
			{
				Pending = null;
				_override = value;
				return;
			}
			if (!HasRules(name))
			{
				return;
			}
			List<Arm> arms = Arms(__instance);
			if (arms == null || arms.Count == 0)
			{
				return;
			}
			var keys = arms.Select(a => RuleKey(name, a.Branch)).ToList();
			var weighted = arms.Select(a => (a, Rate: EventRules.EffectiveRate(RuleKey(name, a.Branch), a.Count, keys))).Where(x => x.Rate > 0).ToList();
			int total = weighted.Sum(x => x.Rate);
			if (total <= 0)
			{
				return;
			}
			int roll = Random.Range(0, total);
			foreach (var (arm, rate) in weighted)
			{
				if (roll < rate)
				{
					_override = arm.Value;
					FileLogger.Log("[事件规则] " + name + " 按规则走第 " + arm.Branch + " 条路");
					return;
				}
				roll -= rate;
			}
		}
		catch (System.Exception ex)
		{
			_override = null;
			FileLogger.LogWarning("[分支自选] 定随机值失败，按原本随机: " + ex.Message);
		}
	}

	[HarmonyFinalizer]
	[HarmonyPatch(typeof(SwitchResultData), nameof(SwitchResultData.Execute))]
	public static void Execute_Finalizer() => _override = null;

	[HarmonyPrefix]
	[HarmonyPatch(typeof(StatValueReference), nameof(StatValueReference.GetValue))]
	public static bool GetValue_Prefix(StatValueReference __instance, ref int __result)
	{
		if (_override == null || __instance.CheckType != StatCheckType.隨機值)
		{
			return true;
		}
		__result = Mathf.Clamp(_override.Value, __instance.GetFieldValue<int>("_randomMin"), __instance.GetFieldValue<int>("_randomMax"));
		return false;
	}
}
