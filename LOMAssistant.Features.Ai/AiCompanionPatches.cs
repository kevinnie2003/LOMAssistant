using System;
using HarmonyLib;
using Mortal.Battle;
using Mortal.Combat;
using Mortal.Story;

namespace LOMAssistant.Features.Ai;

/// <summary>
/// 主动陪玩要知道的、回想记录里没有的几件事：决斗和战役打完了、游戏写下了新的剧情记录、画面上出了插画。
/// 都只是看一眼、记一笔，不改游戏的任何东西；出错只写日志。
/// </summary>
public static class AiCompanionPatches
{
	[HarmonyPrefix]
	[HarmonyPatch(typeof(CombatManager), nameof(CombatManager.GameOver))]
	public static void CombatManager_GameOver_Prefix(bool win)
	{
		Safely(() => AiCompanion.OnDuel(win));
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(GameLevelManager), nameof(GameLevelManager.ShowGameOver))]
	public static void GameLevelManager_ShowGameOver_Prefix(GameLevelManager __instance, GameOverType type)
	{
		// 游戏可能连着叫好几次，只有头一次算数
		if (!__instance.IsGameOver)
		{
			Safely(() => AiCompanion.OnBattle(type.ToString()));
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(StatModifyManager), nameof(StatModifyManager.AddStory))]
	public static void StatModifyManager_AddStory_Prefix(string key)
	{
		Safely(() => AiCompanion.OnRecord(key));
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(StoryMainUI), nameof(StoryMainUI.ShowPicture))]
	public static void StoryMainUI_ShowPicture_Prefix(string key)
	{
		Safely(() => AiCompanion.OnPicture(key));
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(StoryMainUI), nameof(StoryMainUI.ShowBigPicture))]
	public static void StoryMainUI_ShowBigPicture_Prefix(string key)
	{
		Safely(() => AiCompanion.OnPicture(key));
	}

	private static void Safely(Action action)
	{
		try
		{
			action();
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[AI] 主动陪玩记事件失败（已忽略）: " + ex.Message);
		}
	}
}
