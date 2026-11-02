using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Core;
using Mortal.Free;
using Mortal.Story;
using UnityEngine;
using UnityEngine.UI;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 空过：在养成（唐门地图）界面的 ESC 菜单加一个“空过”按钮。
/// 消耗 1 次行动但不执行任何场所脚本，然后走与剧情结束回到地图相同的流程
/// （LuaManager.ChangeScene("Free")）：检查任务、行动次数归零时自动进入下一旬。
/// 留学地图和在外行动时的几处自由行动菜单（见 ActionMenuScripts），剧情 ESC 菜单里也有“空过”，
/// 只在这几个菜单里显示：消耗行动后重新载入剧情场景，和做完一项活动回到菜单的流程相同。
/// </summary>
public static class SkipActionPatch
{
	private const string ButtonName = "LOMAssistant_SkipButton";

	[HarmonyPostfix]
	[HarmonyPatch(typeof(FreeManager), "Awake")]
	public static void FreeManager_Awake_Postfix(FreeManager __instance)
	{
		try
		{
			CommonPanel menuPanel = Traverse.Create(__instance).Field("_menuPanel").GetValue<CommonPanel>();
			MenuPanel menu = menuPanel != null ? menuPanel.GetComponent<MenuPanel>() : null;
			if (menu == null)
			{
				LOMAssistantPlugin.LogWarning("空过：找不到养成界面菜单。");
				return;
			}

			var tr = Traverse.Create(menu);
			Button template = tr.Field("_continueButton").GetValue<Button>()
				?? tr.Field("_loadButton").GetValue<Button>()
				?? tr.Field("_titleButton").GetValue<Button>();
			if (template == null)
			{
				LOMAssistantPlugin.LogWarning("空过：菜单中没有可复制的按钮。");
				return;
			}

			Button skip = MenuButtonHelper.CloneButton(template, "空过", ButtonName);
			MenuNavChain.Ensure(skip.transform.parent);
			skip.onClick.AddListener(OnSkipClick);
			skip.gameObject.SetActive(ConfigManager.EnableSkipAction.Value);
			FileLogger.Log("空过：已添加按钮 " + MenuButtonHelper.DescribeHierarchy(skip.transform));
		}
		catch (Exception e)
		{
			LOMAssistantPlugin.LogError("空过：添加按钮失败: " + e);
		}
	}

	/// <summary>设置页开关「空过」：已经加好的按钮马上跟着显示、隐藏（剧情菜单里的下次打开菜单时按场合显示）。</summary>
	internal static void SettingChanged(bool on)
	{
		MenuButtonHelper.SetVisible(ButtonName, on);
		if (!on)
		{
			MenuButtonHelper.SetVisible(StoryButtonName, false);
		}
	}

	/// <summary>空过热键（默认不绑定）：养成地图上同 ESC 菜单的「空过」，剧情里的自由行动菜单同剧情 ESC 菜单的「空过」。</summary>
	internal static void CheckHotkey()
	{
		KeyCode key = ConfigManager.SkipActionKey.Value;
		if (key == KeyCode.None || !ConfigManager.EnableSkipAction.Value || GUIUtility.keyboardControl != 0 || InputGuard.ModWindowOpen || !InputGuard.HotkeyDown(key))
		{
			return;
		}
		string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
		if (scene == "Free" && FreeManager.Instance != null)
		{
			OnSkipClick();
		}
		else if (scene == "Story" && InBreakMenu())
		{
			OnStorySkipClick();
		}
	}

	private static void OnSkipClick()
	{
		FreeManager free = FreeManager.Instance;
		if (free == null || free.IsProcessing)
		{
			return;
		}
		free.Menu(enabled: false);
		Time.timeScale = 1f;

		MissionManagerData missions = MissionManagerData.Instance;
		if (missions.MainMissionStart && missions.MainMissionPosition != PositionType.無)
		{
			free.StartCoroutine(free.SayDialog(PortraitType.平常, "眼下还有要事在身，不能虚度光阴。"));
			return;
		}
		if (PlayerStatManagerData.Instance.CurrentActionCount <= 0)
		{
			free.StartCoroutine(free.SayDialog(PortraitType.平常, "這會兒有些晚了，改日再說吧。"));
			return;
		}
		SceneController scenes = SceneController.Instance;
		if (scenes == null || scenes.IsPrepare || scenes.IsLoading)
		{
			return;
		}
		free.StartCoroutine(SkipRoutine(free));
	}

	private static IEnumerator SkipRoutine(FreeManager free)
	{
		free.SetHotKey(active: false);
		free.DisableButton();
		free.SetProcessing();

		PlayerStatManagerData stats = PlayerStatManagerData.Instance;
		GameTime before = stats.GameTime;
		string beforeText = $"{before.Year}年{before.Month}月{before.Stage}";
		string msg;
		if (ActionModePatch.Mode == ActionModePatch.Unlimited)
		{
			// 行动点不消耗时，空过就是「结束本旬」，否则永远到不了下一旬
			yield return ActionModePatch.SpendAll(free);
			msg = $"空过：{beforeText} 行动点不消耗模式，结束本旬。";
		}
		else
		{
			yield return free.UpdateActionValue(-1);
			msg = $"空过：{beforeText} 消耗 1 次行动，剩余 {stats.CurrentActionCount}。";
		}
		LOMAssistantPlugin.LogInfo(msg);
		FileLogger.Log(msg);

		yield return new WaitForSeconds(0.5f);
		yield return free.TransitionScreen();

		// Same as LuaManager.ChangeScene("Free", ...) at the end of a location script.
		SaveSystem.Instance.SaveUniverseData();
		MissionManagerData missions = MissionManagerData.Instance;
		missions.UpdateCheckMissions();
		if (missions.HasAnyMissionTrigger())
		{
			LOMAssistantPlugin.LogInfo("空过：触发任务剧情，进入剧情场景。");
			SceneController.Instance.LoadStory();
		}
		else
		{
			SceneController.Instance.LoadFree();
		}
	}

	private const string StoryButtonName = "LOMAssistant_StorySkipButton";

	[HarmonyPostfix]
	[HarmonyPatch(typeof(StoryManager), "Awake")]
	public static void StoryManager_Awake_Postfix(StoryManager __instance)
	{
		try
		{
			MenuPanel menu = Traverse.Create(__instance).Field("_menuPanelController").GetValue<MenuPanel>();
			if (menu == null)
			{
				LOMAssistantPlugin.LogWarning("空过：找不到剧情界面菜单。");
				return;
			}
			var tr = Traverse.Create(menu);
			Button template = tr.Field("_continueButton").GetValue<Button>()
				?? tr.Field("_loadButton").GetValue<Button>()
				?? tr.Field("_titleButton").GetValue<Button>();
			if (template == null)
			{
				return;
			}
			Button skip = MenuButtonHelper.CloneButton(template, "空过", StoryButtonName);
			MenuNavChain.Ensure(skip.transform.parent);
			skip.onClick.AddListener(OnStorySkipClick);
			skip.gameObject.SetActive(false);
		}
		catch (Exception e)
		{
			LOMAssistantPlugin.LogError("空过：添加剧情菜单按钮失败: " + e);
		}
	}

	// 打开剧情 ESC 菜单时，只在自由行动菜单的脚本里显示“空过”
	[HarmonyPostfix]
	[HarmonyPatch(typeof(StoryManager), "Menu")]
	public static void StoryManager_Menu_Postfix(StoryManager __instance, bool enabled)
	{
		try
		{
			MenuPanel menu = Traverse.Create(__instance).Field("_menuPanelController").GetValue<MenuPanel>();
			Transform skip = menu != null ? FindDeep(menu.transform, StoryButtonName) : null;
			if (skip != null)
			{
				skip.gameObject.SetActive(enabled && InBreakMenu() && ConfigManager.EnableSkipAction.Value);
			}
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("空过：切换剧情菜单按钮失败: " + e.Message);
		}
	}

	private static Transform FindDeep(Transform root, string name)
	{
		foreach (Transform t in root.GetComponentsInChildren<Transform>(includeInactive: true))
		{
			if (t.name == name)
			{
				return t;
			}
		}
		return null;
	}

	private static bool InBreakMenu()
	{
		string script = PlayerStatManagerData.Instance != null ? PlayerStatManagerData.Instance.CurrentStoryScript : null;
		return !string.IsNullOrEmpty(script) && ActionMenuScripts.Contains(script)
			&& PlayerStatManagerData.Instance.CurrentActionCount > 0;
	}

	// 剧情里会消耗行动次数的自由行动菜单（按剧本数据查出：选项里有「行动次数 -1」并回到本菜单）。
	// 其余 break_start 菜单只是闲谈，行动次数是从养成带过来的，在那里空过会把游戏时间往后推，导致剧情被跳过、提前。
	private static readonly HashSet<string> ActionMenuScripts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"ch2_4_3_break_start_01",
		"ch3_6_1_break_start_01",
		"S2503_break_start_01",
		"ch6_4_3_break_start_01",
		// 留学地图（每项活动「行动次数 -1」，做完经 section_0X_free_start_01 重新载入本菜单）
		"section_01_free_start_02",
		"section_02_free_start_02",
	};

	private static void OnStorySkipClick()
	{
		StoryManager story = StoryManager.Instance;
		SceneController scenes = SceneController.Instance;
		if (story == null || LuaManager.Instance == null || scenes == null || scenes.IsPrepare || scenes.IsLoading || !InBreakMenu())
		{
			return;
		}
		story.Menu(enabled: false);
		Time.timeScale = 1f;
		PlayerStatManagerData stats = PlayerStatManagerData.Instance;
		GameTime before = stats.GameTime;
		string script = stats.CurrentStoryScript;
		int spend = ActionModePatch.Mode == ActionModePatch.Unlimited ? stats.CurrentActionCount : 1;
		stats.Stats.Add(GameStatType.行動次數, -spend);
		string msg = $"空过（剧情 {script}）：{before.Year}年{before.Month}月{before.Stage} 消耗 {spend} 次行动，剩余 {stats.CurrentActionCount}。";
		LOMAssistantPlugin.LogInfo(msg);
		FileLogger.Log(msg);
		// 和自由行动菜单里做完一项活动后的流程一样：重新载入剧情场景，行动次数归零就进入下一旬
		LuaManager.Instance.ChangeScene("Story", "", "");
	}
}
