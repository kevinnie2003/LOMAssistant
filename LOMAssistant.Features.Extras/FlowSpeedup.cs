using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Fungus;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Core;
using Mortal.Free;
using Mortal.Story;
using UnityEngine;
using UnityEngine.UI;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 流程提速：去掉游戏里玩家看不到的空耗。
/// - 全局存档（universe）每次切场景都会多写一份没人读的 JSON 副本（约 1 MB，随已读对白增长），不再写；
/// - 按名字找旗标原本每次遍历上千个旗标，读档时每个旗标都找一遍，改成查表；
/// - 快进时每条剧本指令至少耗一两帧，能马上做完的指令改成当帧做完；
/// - 数值变化后剧本固定等 0.5 秒，没显示变化时（隐藏属性、变化为 0、不显示的）不再等；显示时等多久见配置。
/// 自由行动的过场（行动后的等待、转场）加快 3 倍；数值变化显示后的停顿缩到 0.2 秒。都在配置文件里可调。各项分在不同的补丁类里，单独应用。
/// </summary>
public static class FlowSpeedup
{
	internal static bool On => ConfigManager.EnableFlowSpeedup.Value;

	public static float StatPauseSeconds(float original)
	{
		return On ? Mathf.Min(original, 0.2f) : original;
	}

	// ---------- 全局存档不写 JSON 副本 ----------
	public static class UniverseJsonPatch
	{

		private static bool _savingUniverse;

		[HarmonyPrefix]
		[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.SaveUniverseData))]
		public static void SaveUniverse_Prefix() => _savingUniverse = true;

		[HarmonyFinalizer]
		[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.SaveUniverseData))]
		public static void SaveUniverse_Finalizer() => _savingUniverse = false;

		[HarmonyPrefix]
		[HarmonyPatch(typeof(SaveSystem), "ExecuteSaveJsonData")]
		public static bool SaveJson_Prefix() => !(FlowSpeedup.On && _savingUniverse);
	}

	// ---------- 旗标查表 ----------
	public static class FlagIndexPatch
	{

		private static FlagCollectionData[] _indexedFrom;
		private static readonly Dictionary<string, FlagData> FlagIndex = new Dictionary<string, FlagData>(StringComparer.Ordinal);

		[HarmonyPrefix]
		[HarmonyPatch(typeof(MissionManagerData), nameof(MissionManagerData.GetFlag))]
		public static bool GetFlag_Prefix(MissionManagerData __instance, string name, ref FlagData __result)
		{
			if (!FlowSpeedup.On || name == null)
			{
				return true;
			}
			FlagCollectionData[] collections = __instance.FlagsCollection;
			if (collections == null)
			{
				return true;
			}
			if (!ReferenceEquals(_indexedFrom, collections))
			{
				FlagIndex.Clear();
				foreach (FlagCollectionData collection in collections)
				{
					if (collection == null || collection.List == null)
					{
						continue;
					}
					foreach (FlagData flag in collection.List)
					{
						// 和原版一样取第一个同名的
						if (flag != null && !FlagIndex.ContainsKey(flag.name))
						{
							FlagIndex[flag.name] = flag;
						}
					}
				}
				_indexedFrom = collections;
			}
			if (!FlagIndex.TryGetValue(name, out FlagData found) || found == null)
			{
				return true; // 没有这个旗标：交给原版，保留它的警告
			}
			__result = found;
			return false;
		}
	}

	// ---------- 数值变化后的停顿 ----------
	public static class StatPausePatch
	{

		private static bool _statShown;

		[HarmonyPrefix]
		[HarmonyPatch(typeof(StoryMainUI), nameof(StoryMainUI.DisplayStatModification))]
		public static void StatShown_Prefix(GameStat stat)
		{
			if (stat != null && stat.PropertyType != GameStatPropertyType.隱藏)
			{
				_statShown = true;
			}
		}

		[HarmonyPrefix]
		[HarmonyPatch(typeof(StoryMainUI), nameof(StoryMainUI.DisplayRelationshipModification))]
		public static void RelationShown_Prefix() => _statShown = true;

		[HarmonyPostfix]
		[HarmonyPatch(typeof(StatModifyManager), nameof(StatModifyManager.GetDisplayTime))]
		public static void DisplayTime_Postfix(ref float __result)
		{
			if (FlowSpeedup.On)
			{
				__result = _statShown ? FlowSpeedup.StatPauseSeconds(__result) : 0f;
			}
			_statShown = false;
		}
	}

	// ---------- 快进时指令当帧做完 ----------
	public static class SkipPatch
	{

		[HarmonyPostfix]
		[HarmonyPatch(typeof(LuaEnvironment), "RunUnityCoroutineImpl")]
		public static void RunCoroutine_Postfix(IEnumerator coroutine, ref IEnumerator __result)
		{
			if (FlowSpeedup.On && coroutine != null && Skipping())
			{
				__result = Inline(coroutine);
			}
		}

		internal static bool Skipping()
		{
			StoryManager story = StoryManager.Instance;
			return story != null && story.GetFieldValue<bool>("_skipDialog");
		}

		/// <summary>就地跑完指令和它里面嵌套的协程，只有真正要等的（等时间、等动画、等条件）才让出一帧。</summary>
		internal static IEnumerator Inline(IEnumerator root)
		{
			var stack = new Stack<IEnumerator>();
			stack.Push(root);
			while (stack.Count > 0)
			{
				IEnumerator top = stack.Peek();
				bool more;
				try
				{
					more = top.MoveNext();
				}
				catch (Exception ex)
				{
					FileLogger.LogWarning("[流程提速] 快进指令出错: " + ex.Message);
					yield break;
				}
				if (!more)
				{
					stack.Pop();
					continue;
				}
				object current = top.Current;
				if (current is IEnumerator nested && !(current is CustomYieldInstruction))
				{
					stack.Push(nested);
					continue;
				}
				yield return current;
			}
		}
	}

	// ---------- 自由行动过场加快 ----------

	/// <summary>养成地图上正在执行行动（行动后的等待、转场）时，时间额外加快的倍数；GameSpeed 每帧乘上它。</summary>
	public static float FreeBoost()
	{
		if (!On)
		{
			return 1f;
		}
		FreeManager free = FreeManager.Instance;
		return free != null && free.isActiveAndEnabled && free.IsProcessing ? 3f : 1f;
	}
}
