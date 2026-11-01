using System;
using HarmonyLib;
using Mortal.Battle;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 战役诊断日志：开打时记下关卡、胜负规则、限时、双方人数和游戏速度，各势力门人的血量怎么设的，结束时记下胜负类型、打了几秒、双方剩多少人。
/// 只写进 Mod 日志，不改游戏。排查「一进战役就直接胜利 / 失败」这类问题用。
/// </summary>
public static class BattleLogPatch
{
	private static float _startedAt;

	[HarmonyPostfix]
	[HarmonyPatch(typeof(GameLevelManager), "SetSessionTime")]
	public static void SetSessionTime_Postfix(GameLevelManager __instance)
	{
		try
		{
			var tr = Traverse.Create(__instance);
			BattleLevel level = tr.Field("_levelData").GetValue<BattleLevel>();
			_startedAt = Time.realtimeSinceStartup;
			FileLogger.Log($"[战役] 开打：{(level != null ? level.name : "?")}（场景键 {PlayerStatManagerData.Instance?.CurrentSceneKey}）"
				+ $"，限时取胜 {level?.VictoryTimeout}、全灭判负 {level?.VitoryDefeat}，限时 {tr.Field("_maxSessionTime").GetValue<int>()} 秒"
				+ $"，我方 {level?.GetFriendPeople()} 人、敌方 {level?.GetEnemyPeople()} 人，游戏速度 {Time.timeScale}");
		}
		catch (Exception e)
		{
			FileLogger.Log("[战役] 记录开打失败（已忽略）：" + e.Message);
		}
	}

	// 开打时按势力的「默认血量」设门人血量：记下势力、默认血量的值和上下限、最后设进去多少
	[HarmonyPostfix]
	[HarmonyPatch(typeof(BattleNpcConfigData), nameof(BattleNpcConfigData.Setup))]
	public static void NpcConfigSetup_Postfix(BattleNpcConfigData __instance, HealthData ____hpData)
	{
		try
		{
			BattleTeamStat team = __instance.TeamStat;
			GameStat hp = team?.DefaultPeopleHealth;
			FileLogger.Log($"[战役] 势力设定 {__instance.name}：势力 {(team != null ? team.name + "#" + team.GetInstanceID() : "无")}"
				+ $"，默认血量 {(hp != null ? hp.name + "#" + hp.GetInstanceID() + " 值 " + hp.Value + "、加成 " + hp.AdditionValue + "、范围 " + hp.Min + "～" + hp.Max + "、最终 " + hp.FinalValue : "无")}"
				+ $"，门人血量设成 {(____hpData != null ? ____hpData.name + "=" + ____hpData.Health : "无")}");
		}
		catch (Exception e)
		{
			FileLogger.Log("[战役] 记录势力设定失败（已忽略）：" + e.Message);
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(GameLevelManager), nameof(GameLevelManager.ShowGameOver))]
	public static void ShowGameOver_Prefix(GameLevelManager __instance, GameOverType type, bool finish)
	{
		try
		{
			var tr = Traverse.Create(__instance);
			if (__instance.IsGameOver)
			{
				return;
			}
			FileLogger.Log($"[战役] 结束：{type}（finish={finish}），游戏内第 {tr.Field("_currentSessionTime").GetValue<int>()} 秒、真实 {Time.realtimeSinceStartup - _startedAt:0.0} 秒"
				+ $"，我方剩 {tr.Field("_currentFriendNpcs").GetValue<int>()} 人、敌方剩 {tr.Field("_currentEnemyNpcs").GetValue<int>()} 人，游戏速度 {Time.timeScale}");
		}
		catch (Exception e)
		{
			FileLogger.Log("[战役] 记录结束失败（已忽略）：" + e.Message);
		}
	}
}
