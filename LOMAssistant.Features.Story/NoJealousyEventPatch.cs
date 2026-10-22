using System;
using System.Text.RegularExpressions;
using Fungus;
using HarmonyLib;
using LOMAssistant.Config;

namespace LOMAssistant.Features.Story;

/// <summary>
/// 取消修罗场事件。
///
/// 原版流程（由任务表与剧本共同确认）：
///   主线任务状态 M6002_02 / M6003_04 → 剧本 ch7_5_6_1 → ch7_5_6_2（「大战前夕」）
///   ch7_5_6_2 里每个女角的剧情是一段并列的 if（不是 elseif），谁攻略了谁就播谁的，
///   多个同时成立会按顺序依次播完。
///   剧本末尾按开关 Ch_7_5_6_2_002（「旗标 攻略人数_排除梅竹菊 需&gt;=2」）判断：
///   若同时攻略了 2 个以上，就 SetFlag("M70059", 1) 点亮「啟動修羅場事件」，
///   并用 SetMissionTime("M7005", …) 给 10 个任务 M7005_01～M7005_10 设定触发日期。
///   之后这些任务启动 ch7_5_6_3（对峙、进战斗）→ ch7_5_6_3_1（战斗结果，可能 GameOver）
///   → ch7_5_6_3_2（置旗标 M70054、把 M7005_01～M7005_10 全部 clear、回养成地图）。
///
/// 打开本开关后，在 Lua 交给 Fungus 编译前改写源码：
///   1. 拦掉 SetFlag("M70059", 1)——修罗场不再被武装，各女角的「大战前夕」剧情
///      就是这条线的正常收尾。任意结缘人数（0～8）、任意线路都成立，因为那本来就是
///      原版的单角行为，而单人剧情的循环是剧本自带的。
///   2. 兜底：万一还是走到清算那一段，拦掉 SetFlag("M70054", 1) 与
///      Mission("M7005_XX", "clear")，不让攻略任务被清空。
///
/// 注意：M70054 也参与第八章节的结局分支（Ch_8_1_1_1_003 要求它为 0，
/// Ch_8_3_2_1_3_002 要求它为 1 或心上人是叶云裳），所以结局走向会和原版不同。
/// </summary>
public static class NoJealousyEventPatch
{
	// ch7_5_6_2 末尾：点亮「啟動修羅場事件」
	private static readonly Regex Arm = new Regex(
		"statmodifymanager\\.SetFlag\\(\\s*\"M70059\"\\s*,\\s*1\\s*\\)", RegexOptions.Compiled);

	// ch7_5_6_3_2：置「發生修羅場事件」
	private static readonly Regex FlagSet = new Regex(
		"statmodifymanager\\.SetFlag\\(\\s*\"M70054\"\\s*,\\s*1\\s*\\)", RegexOptions.Compiled);

	// ch7_5_6_3_2：清空 10 个攻略任务
	private static readonly Regex MissionClear = new Regex(
		"statmodifymanager\\.Mission\\(\\s*\"M7005_\\d+\"\\s*,\\s*\"clear\"\\s*\\)", RegexOptions.Compiled);

	private static int _armed;
	private static int _cleaned;

	[HarmonyPrefix]
	// 排在选项解析之后：选项提示、剧情回顾要看到的是原剧本
	[HarmonyPriority(Priority.LowerThanNormal)]
	[HarmonyPatch(typeof(LuaEnvironment), "LoadLuaFunction")]
	public static void LoadLuaFunction_Prefix(ref string luaString, string friendlyName)
	{
		if (luaString == null || luaString.Length == 0 || !ConfigManager.EnableNoJealousyEvent.Value)
		{
			return;
		}
		// 只有含这几个名字的剧本才需要处理，先做便宜的排除
		bool hasArm = luaString.IndexOf("M70059", StringComparison.Ordinal) >= 0;
		bool hasCleanup = luaString.IndexOf("M70054", StringComparison.Ordinal) >= 0
			|| luaString.IndexOf("M7005_", StringComparison.Ordinal) >= 0;
		if (!hasArm && !hasCleanup)
		{
			return;
		}
		try
		{
			bool changed = false;
			if (Arm.IsMatch(luaString))
			{
				luaString = Arm.Replace(luaString, "-- [咖啡伴侣] 已取消修罗场：不点亮 M70059");
				_armed++;
				changed = true;
			}
			if (FlagSet.IsMatch(luaString))
			{
				luaString = FlagSet.Replace(luaString, "-- [咖啡伴侣] 已取消修罗场：不置 M70054");
				changed = true;
			}
			if (MissionClear.IsMatch(luaString))
			{
				luaString = MissionClear.Replace(luaString, "-- [咖啡伴侣] 已取消修罗场：保留攻略任务");
				changed = true;
			}
			if (changed)
			{
				FileLogger.Log($"[修罗场] 已改写剧本 {friendlyName}（不点亮 {_armed} 次，兜底拦截 {_cleaned} 次）");
			}
		}
		catch (Exception ex)
		{
			// 改写失败就让原剧本照原样跑，不能因为这一项把对话卡住
			LOMAssistantPlugin.LogWarning("取消修罗场事件：改写剧本失败，已按原版执行：" + ex.Message);
			FileLogger.LogWarning("[修罗场] 改写剧本失败: " + ex);
		}
	}
}
