using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Core;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 开发项目直接更换（设置「开发项目直接更换」，默认开，不归预设管：这是补原版的体验漏洞，原汁原味也开着），锻冶场、炼丹房都算：游戏的开发界面只列当前项目这一支能进化到的，
/// 想换到别的分支得先退回默认项目、重新练到要求等级。这里把同类里其他分支的项目也列进去，
/// 沿用它们原来的等级、门派规模和剧情要求，等级看当前项目，所以不用再绕回默认项目。
/// 上一级没开发过的不列（默认项目算开发过），剧情没解锁的也不列。
/// 剧情临时换上的项目不动，它们要在剧情结束时换回去。
/// </summary>
internal static class ForgeSwapPatch
{
	[HarmonyPostfix]
	[HarmonyPatch(typeof(UpgradeItemData), nameof(UpgradeItemData.GetCurrentEvolutionItems))]
	public static void AddOtherBranches(UpgradeItemData __instance, List<UpgradeDevelopItem> __result)
	{
		try
		{
			PlayerStatManagerData player = PlayerStatManagerData.Instance;
			// 列表本来就空的是剧情给的项目
			if (!ConfigManager.ForgeDirectSwap.Value || __result.Count == 0
				|| player.HasSwappedUpgradeItem(__instance.ItemType))
			{
				return;
			}
			var shown = new HashSet<UpgradeItemData>(__result.Select(item => item.NextItem)) { __instance };
			var collection = Traverse.Create(player).Field(__instance.ItemType < UpgradeItemType.平射 ? "_poisonUpgradeItems" : "_weaponUpgradeItems").GetValue<UpgradeItemCollectionData>();
			var best = new Dictionary<UpgradeItemData, UpgradeDevelopItem>();
			foreach (UpgradeItemData parent in collection.List.Where(item => item.ItemType == __instance.ItemType && item != __instance && Developed(item)))
			{
				foreach (UpgradeDevelopItem edge in Branches(parent).Where(edge => !shown.Contains(edge.NextItem) && edge.CheckConditions()))
				{
					if (!best.TryGetValue(edge.NextItem, out UpgradeDevelopItem known) || edge.RequiredLevel < known.RequiredLevel)
					{
						best[edge.NextItem] = edge;
					}
				}
			}
			__result.AddRange(best.Values.OrderBy(edge => edge.NextItem.RequiredSectLevel).ThenBy(edge => edge.RequiredLevel));
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[开发] 列其他分支失败（已忽略）：" + e.Message);
		}
	}

	private static bool Developed(UpgradeItemData item) => item.AlreadyDevelop || item.Key.EndsWith("_01");

	private static UpgradeDevelopItem[] Branches(UpgradeItemData item) =>
		Traverse.Create(item).Field("_evolutionItems").GetValue<UpgradeDevelopItem[]>() ?? new UpgradeDevelopItem[0];
}
