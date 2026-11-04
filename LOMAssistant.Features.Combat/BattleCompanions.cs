using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Core;

namespace LOMAssistant.Features.Combat;

/// <summary>
/// 战役助战选择（设置「战役助战选择」，进阶及以上打开）：游戏只让玩家选剧本这一场点名的同伴绝招。
/// 开战面板的绝招列表靠 BattleSkillData.Check 决定列不列，这里让认识且好感够的同伴也通过；
/// 同伴绝招不走「自动上阵」那条路（Condition 为假），所以只多出可选项，不会自己占格。
/// </summary>
internal static class BattleCompanions
{
	private const int MinAffection = 40;

	private static readonly Dictionary<string, string> Owners = new Dictionary<string, string>
	{
		["brother1"] = "Relationship_Brother1",
		["brother2"] = "Relationship_Brother2",
		["brother4"] = "Relationship_Brother4",
		["sister1_1"] = "Relationship_Sister1_小師妹",
		["special3"] = "Relationship_Special3_葉雲舟",
		["girl4"] = "Relationship_Girl4_龍湘",
		["girl9"] = "Relationship_Girl9_上官螢",
		["girl6_1"] = "Relationship_Girl7_郁竹",
		["girl6_2"] = "Relationship_Girl7_郁竹",
	};

	[HarmonyPostfix]
	[HarmonyPatch(typeof(BattleSkillData), nameof(BattleSkillData.Check))]
	public static void Unlock(BattleSkillData __instance, ref bool __result)
	{
		if (__result || !ConfigManager.BattleCompanions.Value || !Owners.TryGetValue(__instance.Key, out string owner))
		{
			return;
		}
		RelationshipStat stat = PlayerStatManagerData.Instance.Relationships?.List.FirstOrDefault(s => s.name == owner);
		__result = stat != null && stat.Active && stat.Value >= MinAffection;
	}
}
