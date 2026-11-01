using System;
using HarmonyLib;
using Mortal.Battle;
using UnityEngine.UI;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 战役直接获胜：在战役暂停界面“放弃”按钮下方加一个“直接获胜”按钮，
/// 调用游戏自带（但隐藏）的测试按钮逻辑 PausePanel.WinButtonClick。
/// </summary>
public static class BattleWinPatch
{
	private const string ButtonName = "LOMAssistant_BattleWinButton";

	[HarmonyPostfix]
	[HarmonyPatch(typeof(PausePanel), "Awake")]
	public static void PausePanel_Awake_Postfix(PausePanel __instance)
	{
		try
		{
			var tr = Traverse.Create(__instance);
			Button giveup = tr.Field("_giveupButton").GetValue<Button>();
			if (giveup == null)
			{
				LOMAssistantPlugin.LogWarning("战役直接获胜：找不到放弃按钮。");
				return;
			}

			Button win = MenuButtonHelper.CloneButton(giveup, "直接获胜", ButtonName);
			MenuNavChain.Ensure(win.transform.parent);
			win.onClick.AddListener(() =>
			{
				LOMAssistantPlugin.LogInfo("战役直接获胜：已执行。");
				FileLogger.Log("战役直接获胜：已执行。");
				Traverse.Create(__instance).Method("WinButtonClick").GetValue();
			});

			// Let the panel disable it while inner panels (settings/load) are open.
			Button[] buttons = tr.Field("_interactableButtons").GetValue<Button[]>();
			if (buttons != null && Array.IndexOf(buttons, win) < 0)
			{
				tr.Field("_interactableButtons").SetValue(buttons.AddToArray(win));
			}

			FileLogger.Log("战役直接获胜：已添加按钮 " + MenuButtonHelper.DescribeHierarchy(win.transform));
		}
		catch (Exception e)
		{
			LOMAssistantPlugin.LogError("战役直接获胜：添加按钮失败: " + e);
		}
	}
}
