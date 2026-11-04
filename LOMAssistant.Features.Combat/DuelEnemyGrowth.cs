using System;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Combat;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Combat;

/// <summary>
/// 决斗敌人随主角成长（设置「决斗敌人随主角成长」）：敌人的数值是写死的，主角练高以后第一回合就能打赢。
/// 开战时按敌人在全部敌人里的强弱排出档位，再按主角六项属性总和算出这一档该有的实力，不够就同比例提高血量和属性。
/// 比例用档位算，所以杂兵仍弱于主角，顶尖的敌人仍强于主角；已经够强的敌人不变。
/// 只改这一场用的副本，不动游戏资源，存档也不受影响。
/// </summary>
internal static class DuelEnemyGrowth
{
	private const float WeakestShare = 0.4f; // 最弱的敌人至少有主角的四成
	private const float ShareSpan = 1.1f; // 最强的敌人约为主角的一点五倍
	private const float MaxScale = 5f;
	private const int FixedHealth = 5000; // 血量这么高的是剧情安排打不死的

	// 全部决斗敌人六项属性总和的分布：总和 → 排在前百分之多少
	private static readonly (float Total, float Rank)[] Ranks =
	{
		(10f, 0f), (60f, 0.1f), (130f, 0.25f), (190f, 0.5f), (275f, 0.75f), (350f, 0.9f), (415f, 0.97f), (470f, 1f),
	};

	[HarmonyPrefix]
	[HarmonyPatch(typeof(CombatActionController), nameof(CombatActionController.SetStat))]
	public static void Scale(CombatActionController __instance, ref CombatStat data)
	{
		try
		{
			CombatManager manager = CombatManager.Instance;
			if (!ConfigManager.DuelEnemyGrowth.Value || manager == null || __instance != manager.EnemyAction || data == null || data.MaxHealth >= FixedHealth)
			{
				return;
			}
			float enemy = Mathf.Max(1f, data.Strength + data.Stamina + data.Dexterity + data.Fist + data.Sword + data.MartialWeapon);
			float player = PlayerTotal();
			float scale = Mathf.Clamp(player * (WeakestShare + ShareSpan * Rank(enemy)) / enemy, 1f, MaxScale);
			FileLogger.Log($"[决斗] 敌人成长：主角 {player:0}，敌人 {enemy:0}，倍率 {scale:0.00}");
			if (scale <= 1f)
			{
				return;
			}
			CombatStat copy = UnityEngine.Object.Instantiate(data);
			copy.MaxHealth = Up(data.MaxHealth, scale);
			copy.DefaultHealth = Up(data.DefaultHealth, scale);
			copy.Strength = Up(data.Strength, scale);
			copy.Stamina = Up(data.Stamina, scale);
			copy.Dexterity = Up(data.Dexterity, scale);
			copy.Fist = Up(data.Fist, scale);
			copy.Sword = Up(data.Sword, scale);
			copy.MartialWeapon = Up(data.MartialWeapon, scale);
			copy.Talking = Up(data.Talking, scale);
			data = copy;
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[决斗] 敌人成长失败（已忽略）：" + e.Message);
		}
	}

	private static int Up(int value, float scale) => Mathf.RoundToInt(value * scale);

	private static float PlayerTotal()
	{
		var stats = PlayerStatManagerData.Instance.Stats;
		return stats.Get(GameStatType.體力).FinalValue + stats.Get(GameStatType.內力).FinalValue + stats.Get(GameStatType.輕功).FinalValue
			+ stats.Get(GameStatType.武功拳掌).FinalValue + stats.Get(GameStatType.武功刀劍).FinalValue + stats.Get(GameStatType.武功暗器).FinalValue;
	}

	private static float Rank(float total)
	{
		for (int i = 1; i < Ranks.Length; i++)
		{
			if (total <= Ranks[i].Total)
			{
				float part = Mathf.InverseLerp(Ranks[i - 1].Total, Ranks[i].Total, total);
				return Mathf.Lerp(Ranks[i - 1].Rank, Ranks[i].Rank, part);
			}
		}
		return 1f;
	}
}
