using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Battle;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 战役修改（开关在「设置」页，即时生效）：
/// 不会受伤：我方（主角和同伴）挨打、中毒都不扣血，也不显示伤害数字；敌人照常受伤。剧情要求的强制战败不靠扣血，不受影响。
/// 绝招无冷却：底部的绝招按钮放完马上又能用。
/// </summary>
public static class BattleCheatPatch
{
	private static readonly AccessTools.FieldRef<Entity, EntityType> OriginType = AccessTools.FieldRefAccess<Entity, EntityType>("_originType");

	// 是我方：以开场时的阵营为准（中途被控、变成中立的也算）
	private static bool IsOurSide(Character character)
	{
		Entity entity = character != null ? character.Entity : null;
		if (entity == null)
		{
			return false;
		}
		EntityType type = OriginType(entity);
		return type == EntityType.Player || type == EntityType.Friend;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(Character), nameof(Character.TakeDamage))]
	public static bool TakeDamage_Prefix(Character __instance, ref bool __result)
	{
		if (ConfigManager.CheatBattleInvincible.Value && IsOurSide(__instance))
		{
			__result = true; // 算挨到了（攻击方照常结算），只是不扣血
			return false;
		}
		return true;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(SkillButton), "Update")]
	public static void SkillButton_Update_Prefix(ref bool ____isCoolDown, ref float ____currentCooldownTime)
	{
		if (ConfigManager.CheatBattleNoCooldown.Value && ____isCoolDown)
		{
			____currentCooldownTime = 0f;
		}
	}
}
