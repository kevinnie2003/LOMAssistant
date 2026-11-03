using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Mortal.Core;
using Mortal.Story;

namespace LOMAssistant;

/// <summary>
/// 分支判断按指定的那一支走（剧情跳转确认页选的、分支判定手动选择选的）：默认不改数值，判断完直接给这一支的结果。
/// 设置里打开「选分支时自动调整数值」时，判断执行前先改数值：要走的这一支补到满足（属性、好感、旗标，同「一键设置」），
/// 排在它前面、现在会先成立的几支压到刚好不满足；改不动的（随机、物品、时间，或改了这支又把那支带坏）再直接给结果。
/// 改数值是一直留着的，会影响后面的剧情，所以默认关。每个判断只管一次，回到地图、读档后作废。
/// </summary>
internal static class BranchOverride
{
	private static readonly Dictionary<string, int> Arms = new Dictionary<string, int>(StringComparer.Ordinal);
	private static bool _hooked;

	/// <summary>换成这一组（分支判断名 → 第几支；0 和比条件数多 1 都是「以上都不满足」）；null 清空。</summary>
	public static void Set(Dictionary<string, int> arms)
	{
		Arms.Clear();
		if (arms != null)
		{
			foreach (var pair in arms)
			{
				Arms[pair.Key] = pair.Value;
			}
		}
		Hook();
	}

	/// <summary>只加这一个判断。</summary>
	public static void Set(string name, int arm)
	{
		Arms[name] = arm;
		Hook();
	}

	private static void Hook()
	{
		if (_hooked)
		{
			return;
		}
		_hooked = true;
		UnityEngine.SceneManagement.SceneManager.activeSceneChanged += (from, to) =>
		{
			if (to.name != "Story")
			{
				Arms.Clear();
			}
		};
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(SwitchResultData), nameof(SwitchResultData.Execute))]
	public static void Execute_Prefix(SwitchResultData __instance, out int __state)
	{
		__state = 0;
		try
		{
			if (Arms.Count == 0 || !Arms.TryGetValue(__instance.name, out int arm) || LOMAssistant.Features.AnytimeSave.AnytimeSave.Replaying)
			{
				return;
			}
			Arms.Remove(__instance.name);
			bool byCount = __instance.GetFieldValue<bool>("_useCount");
			int arms = byCount ? __instance.GetFieldValue<List<ValueCompareItem>>("_countItems").Count : __instance.GetFieldValue<List<ConditionResultItem>>("_items").Count;
			__state = arm == 0 || arm > arms ? arms + 1 : arm;
			if (!byCount && LOMAssistant.Config.ConfigManager.BranchChangeValues.Value)
			{
				Steer(__instance.name, __instance.GetFieldValue<List<ConditionResultItem>>("_items"), __state);
			}
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[分支] 改数值失败，判断完直接走选的那一支: " + ex.Message);
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(SwitchResultData), nameof(SwitchResultData.Execute))]
	public static void Execute_Postfix(SwitchResultData __instance, int __state, ref int __result)
	{
		if (__state == 0 || __result == __state)
		{
			return;
		}
		FileLogger.Log($"[分支] {__instance.name}：游戏判断第 {__result} 支，直接走第 {__state} 支");
		__result = __state;
	}

	// 改数值让游戏自己判断到 want：want 补到满足，前面会先成立的压到不满足
	private static void Steer(string name, List<ConditionResultItem> items, int want)
	{
		var done = new List<string>();
		if (want <= items.Count)
		{
			List<ConditionSolver.Write> writes = ConditionSolver.Satisfy(items[want - 1]);
			if (writes == null || !writes.All(ConditionSolver.Apply))
			{
				FileLogger.Log($"[分支] {name}：第 {want} 支的条件补不上");
				return;
			}
			done.AddRange(writes.Select(w => w.Target + "=" + w.Value));
		}
		for (int i = 0; i < Math.Min(want - 1, items.Count); i++)
		{
			if (ConditionSolver.Holds(items[i]) != true)
			{
				continue;
			}
			List<ConditionSolver.Write> broke = ConditionSolver.Violate(items[i]).FirstOrDefault(option => option.All(ConditionSolver.Apply));
			if (broke == null)
			{
				FileLogger.Log($"[分支] {name}：第 {i + 1} 支压不下去");
				return;
			}
			done.AddRange(broke.Select(w => w.Target + "=" + w.Value));
		}
		if (done.Count > 0)
		{
			FileLogger.Log($"[分支] {name}：为走第 {want} 支改了 " + string.Join("，", done.ToArray()));
		}
	}
}
