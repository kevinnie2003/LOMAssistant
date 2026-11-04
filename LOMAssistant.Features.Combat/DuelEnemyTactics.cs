using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Combat;
using UnityEngine;

namespace LOMAssistant.Features.Combat;

/// <summary>
/// 决斗玩法重做（设置「决斗玩法重做」）的敌人出招：敌人原本的出招概率几乎平均，无从判断。
/// 现在以原有概率为底，再按看得见的局面调整：气多偏绝招，气少偏嘴攻和备揍，血少偏备揍和绝招，
/// 主角气足时偏嘴攻防绝招，主角连续出同一招时偏克制那一招。剧情强制的出招不受影响。
/// </summary>
internal static class DuelEnemyTactics
{
	private const float LowShare = 0.35f;
	private const int MaxStreak = 3;

	private static readonly CombatActionType[] Commands =
	{
		CombatActionType.絕招1, CombatActionType.暗器, CombatActionType.捅人, CombatActionType.嘴砲, CombatActionType.備揍,
	};

	private static readonly Dictionary<CombatActionType, CombatActionType[]> Answers = new Dictionary<CombatActionType, CombatActionType[]>
	{
		[CombatActionType.捅人] = new[] { CombatActionType.暗器, CombatActionType.絕招1 },
		[CombatActionType.暗器] = new[] { CombatActionType.絕招1 },
		[CombatActionType.絕招1] = new[] { CombatActionType.嘴砲 },
		[CombatActionType.嘴砲] = new[] { CombatActionType.捅人, CombatActionType.暗器 },
		[CombatActionType.備揍] = new[] { CombatActionType.嘴砲 },
	};

	private static CombatActionType _last;
	private static int _streak;

	private static CombatActionType Slot(CombatActionType action) =>
		action == CombatActionType.絕招2 || action == CombatActionType.絕招3 ? CombatActionType.絕招1 : action;

	[HarmonyPrefix]
	[HarmonyPatch(typeof(CombatManager), "InitCombatRound")]
	public static void Reset()
	{
		_last = CombatActionType.無動作;
		_streak = 0;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(CombatManager), "ActionRoundHandle")]
	public static void Remember(CombatActionResult playerActionResult)
	{
		CombatActionType action = Slot(playerActionResult.Action);
		_streak = action == _last ? Mathf.Min(_streak + 1, MaxStreak) : 1;
		_last = action;
	}

	private static bool Usable(CombatActionController enemy, CombatActionType command) => command switch
	{
		CombatActionType.絕招1 => enemy.CanUseUltimateAction(),
		CombatActionType.暗器 => enemy.CanUseWeaponAction(),
		CombatActionType.捅人 => enemy.CanUseAttackAction(),
		CombatActionType.嘴砲 => enemy.CanUseTalkAction(),
		_ => enemy.CanUseBlockAction(),
	};

	private static float BaseRate(CombatStatController stat, CombatActionType command) => command switch
	{
		CombatActionType.絕招1 => stat.UltimateRate,
		CombatActionType.暗器 => stat.WeaponRate,
		CombatActionType.捅人 => stat.AttackRate,
		CombatActionType.嘴砲 => stat.TalkRate,
		_ => stat.BlockRate,
	};

	private static float Situation(CombatActionController enemy, CombatActionController player, CombatActionType command)
	{
		float rate = 1f;
		float stamina = (float)enemy.Stat.CurrentStamina / Mathf.Max(1, enemy.Stat.MaxStamina.FinalValue);
		if (command == CombatActionType.絕招1)
		{
			rate *= enemy.GetStaminaLevel() switch { 3 => 2.2f, 2 => 1.6f, _ => 1.2f };
		}
		if (stamina < LowShare)
		{
			rate *= command switch { CombatActionType.嘴砲 => 2f, CombatActionType.備揍 => 1.5f, CombatActionType.捅人 => 0.7f, _ => 1f };
		}
		if (enemy.Stat.CurrentHealthPct < LowShare * 100f)
		{
			rate *= command switch { CombatActionType.備揍 => 1.6f, CombatActionType.絕招1 => 1.4f, _ => 1f };
		}
		if (command == CombatActionType.嘴砲 && player.GetStaminaLevel() >= 2)
		{
			rate *= 1.6f;
		}
		if (Answers.TryGetValue(_last, out CombatActionType[] answers) && answers.Contains(command))
		{
			rate *= 1f + 0.5f * _streak;
		}
		return rate;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(CombatActionController), nameof(CombatActionController.GetActionByStat))]
	public static bool Choose(CombatActionController __instance, ref CombatActionResult __result)
	{
		CombatManager manager = CombatManager.Instance;
		if (!ConfigManager.DuelRework.Value || manager == null || __instance != manager.EnemyAction)
		{
			return true;
		}
		try
		{
			var weights = Commands.Where(command => Usable(__instance, command))
				.Select(command => (Command: command, Weight: BaseRate(__instance.Stat, command) * Situation(__instance, manager.PlayerAction, command)))
				.ToList();
			float total = weights.Sum(item => item.Weight);
			if (total <= 0f)
			{
				return true;
			}
			float roll = UnityEngine.Random.value * total;
			CombatActionType picked = weights[weights.Count - 1].Command;
			foreach (var item in weights)
			{
				roll -= item.Weight;
				if (roll <= 0f)
				{
					picked = item.Command;
					break;
				}
			}
			FileLogger.Log("[决斗] 敌人选招 " + picked + "：" + string.Join(" ", weights.Select(item => $"{item.Command} {item.Weight / total:P0}")));
			__result = new CombatActionResult { Action = picked == CombatActionType.絕招1 ? __instance.GetCurrentUltimateLevel() : picked };
			return false;
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[决斗] 敌人选招失败（已忽略）：" + e.Message);
			return true;
		}
	}
}
