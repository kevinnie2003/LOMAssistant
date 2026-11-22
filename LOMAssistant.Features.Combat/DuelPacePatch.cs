using System.Collections;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Combat;

namespace LOMAssistant.Features.Combat;

/// <summary>
/// 决斗回合之间的等待缩短：回合开始和每次出招的流程是一层套一层的协程，Unity 在嵌套协程结束后还要再等一帧才继续，帧数不受加速影响。
/// 游戏 1.0.6001.2 起自己在状态效果那一层用 CombatCoroutine.Inline 让嵌套协程就地跑完，这里把同一个方法套到回合开始和出招这两段外层流程上，
/// 只有真正的等待（动画、延时、点击）才让出一帧；效果、数值和结算顺序都不变。
/// </summary>
internal static class DuelPacePatch
{
	// 属于「流程提速」
	private static bool On => ConfigManager.EnableFlowSpeedup.Value;

	[HarmonyPostfix]
	[HarmonyPatch(typeof(CombatManager), "InitCombatRound")]
	[HarmonyPatch(typeof(CombatManager), "StartAction")]
	public static void Flat(ref IEnumerator __result)
	{
		if (On && __result != null)
		{
			__result = Inline(__result);
		}
	}

	private static IEnumerator Inline(IEnumerator root)
	{
		foreach (object step in CombatCoroutine.Inline(root))
		{
			yield return step;
		}
	}
}
