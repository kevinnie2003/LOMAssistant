using System.Collections;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Story;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LOMAssistant.Features.Dice;

public static class DiceManualSelectionPatch
{
	public static bool IsClicked;

	public static int IsClickedIndex;

	public static DiceMenuDialog Menu;

	public static bool RollHasStarted;

	/// <summary>上一次直接点线路是什么时候（真实时间）：这样选的结果，游戏记下的点数不作数，主动陪玩用它分辨。</summary>
	public static float ChosenAt = -100f;

	[HarmonyPostfix]
	[HarmonyPatch(typeof(DiceMenuDialog), "OnEnable")]
	public static void OnEnable_Postfix(DiceMenuDialog __instance)
	{
		if (!ConfigManager.EnableManualDiceSelection.Value)
		{
			return;
		}
		Menu = __instance;
		IsClicked = false;
		RollHasStarted = false;
		Button[] cachedButtons = __instance.CachedButtons;
		if (cachedButtons == null)
		{
			return;
		}
		for (int i = 0; i < cachedButtons.Length; i++)
		{
			if (!(cachedButtons[i] == null))
			{
				DiceManualSelectionButton diceManualSelectionButton = cachedButtons[i].gameObject.GetComponent<DiceManualSelectionButton>();
				if (diceManualSelectionButton == null)
				{
					diceManualSelectionButton = cachedButtons[i].gameObject.AddComponent<DiceManualSelectionButton>();
					diceManualSelectionButton.PointerClickEvent += OnButtonClick;
				}
				diceManualSelectionButton.index = i + 1;
			}
		}
		DiceGamepadSelector.Attach(__instance);
		LOMAssistantPlugin.LogInfo("骰子手动选择：已注册按钮点击事件。");
		FileLogger.Log("骰子手动选择：已注册按钮点击事件。");
	}

	public static void OnButtonClick(PointerEventData obj, int i)
	{
		if (obj.button == PointerEventData.InputButton.Left)
		{
			Choose(i);
		}
	}

	/// <summary>选定第 i 条线路（鼠标点击和手柄共用）。</summary>
	public static void Choose(int i)
	{
		// 设置页里中途关掉后，已经挂上的点击不再生效
		if (!(Menu == null) && ConfigManager.EnableManualDiceSelection.Value)
		{
			LOMAssistantPlugin.LogInfo("骰子手动选择：选中线路 " + i);
			FileLogger.Log("[DicePatch] OnButtonClick: line=" + i);
			Traverse traverse = Traverse.Create(Menu);
			IsClickedIndex = i;
			IsClicked = true;
			ChosenAt = UnityEngine.Time.realtimeSinceStartup;
			traverse.Field("_resultSelection").SetValue(i);
			Menu.UpdateSelection();
			FileLogger.Log("[DicePatch] OnButtonClick: RollHasStarted=" + RollHasStarted);
			if (!RollHasStarted)
			{
				Menu.PressStartButton();
				RollHasStarted = true;
				FileLogger.Log("[DicePatch] OnButtonClick: called PressStartButton (Roll not started)");
			}
			else
			{
				traverse.Field("_triggerNextInput").SetValue(true);
				Menu.PressContinueButton();
				FileLogger.Log("[DicePatch] OnButtonClick: set _triggerNextInput=true + PressContinueButton (in Roll/after)");
			}
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(DiceMenuDialog), "Roll")]
	public static bool Roll_Prefix(DiceMenuDialog __instance, ref IEnumerator __result, bool __runOriginal)
	{
		if (!__runOriginal)
		{
			// 随时存档读档时已经换成了恢复结果的协程
			RollHasStarted = true;
			return false;
		}
		Traverse traverse = Traverse.Create(__instance);
		if (IsClicked)
		{
			traverse.Field("_resultSelection").SetValue(IsClickedIndex);
			__instance.UpdateSelection();
			traverse.Field("_useFatePoint").SetValue(false);
			__result = EmptyEnumerator();
			LOMAssistantPlugin.LogInfo("骰子手动选择：跳过 Roll 协程，直接设置结果为线路 " + IsClickedIndex);
			FileLogger.Log("[DicePatch] Roll_Prefix: IsClicked=true, skipped Roll, set _resultSelection=" + IsClickedIndex + ", cleared _useFatePoint");
			return false;
		}
		// 重骰（逆天改命）和第一次一样照原版转：重新抽点数、转盘显示数字；转的时候或转完点线路照样能直接选
		RollHasStarted = true;
		FileLogger.Log("[DicePatch] Roll_Prefix: normal Roll started, RollHasStarted=true");
		return true;
	}

	private static IEnumerator EmptyEnumerator()
	{
		yield break;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(DiceMenuDialog), "CheckRevolution")]
	public static bool CheckRevolution_Prefix(DiceMenuDialog __instance, ref bool __result)
	{
		FileLogger.Log("[DicePatch] CheckRevolution_Prefix: called, IsClicked=" + IsClicked);
		if (!IsClicked)
		{
			return true;
		}
		Traverse traverse = Traverse.Create(__instance);
		traverse.Field("_resultSelection").SetValue(IsClickedIndex);
		__instance.UpdateSelection();
		__result = false;
		IsClicked = false;
		traverse.Field("_triggerNextInput").SetValue(true);
		__instance.PressContinueButton();
		LOMAssistantPlugin.LogInfo("骰子手动选择：已跳过逆天判定，直接继续。");
		FileLogger.Log("[DicePatch] CheckRevolution_Prefix: forced __result=false, set _triggerNextInput=true, reset IsClicked=false");
		return false;
	}
}
