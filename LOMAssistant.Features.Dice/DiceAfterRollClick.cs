using System.Collections.Generic;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Story;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace LOMAssistant.Features.Dice;

/// <summary>
/// 掷骰转完以后也能直接点线路：转完游戏会打开一块铺满全屏的「点击继续」面板（_continuePanel），盖在线路上面，
/// 点线路其实点到的是它，就按掷出的结果继续了，鼠标停在线路上也不会高亮。这里在面板开着时看鼠标的位置：
/// 最上面挡着的是这块面板、位置落在某条线路上，就把那条高亮（移开后恢复成掷出的结果）；左键点下去就按那条线路选（和转之前点线路走同一套）。
/// 点在面板的其他地方、点在逆天改命等按钮上照旧。挂在骰子界面上，界面关闭时不运行。
/// </summary>
public class DiceAfterRollClick : MonoBehaviour
{
	private DiceMenuDialog _menu;
	private GameObject _panel;
	private int _hover; // 鼠标停着的线路（1 起算），0 是没有
	private readonly List<RaycastResult> _hits = new List<RaycastResult>();

	public static void Attach(DiceMenuDialog menu)
	{
		DiceAfterRollClick click = menu.gameObject.GetComponent<DiceAfterRollClick>() ?? menu.gameObject.AddComponent<DiceAfterRollClick>();
		click._menu = menu;
		click._panel = Traverse.Create(menu).Field("_continuePanel").GetValue<GameObject>();
		click._hover = 0;
	}

	private void Update()
	{
		if (_menu == null || _panel == null || !_panel.activeInHierarchy || !ConfigManager.EnableManualDiceSelection.Value)
		{
			_hover = 0;
			return;
		}
		Mouse mouse = Mouse.current;
		int line = mouse == null ? 0 : LineUnder(mouse.position.ReadValue());
		if (line != _hover)
		{
			_hover = line;
			Highlight(line);
		}
		if (line > 0 && mouse.leftButton.wasPressedThisFrame)
		{
			FileLogger.Log("[DicePatch] 转完后点线路 " + line);
			_hover = 0;
			DiceManualSelectionPatch.Choose(line);
		}
	}

	/// <summary>被面板挡住的那条线路（1 起算）；最上面是线路本身（它自己会处理）、别的按钮、Mod 窗口，或者不在线路上时是 0。</summary>
	private int LineUnder(Vector2 position)
	{
		if (InputGuard.PointerOverModWindow || EventSystem.current == null)
		{
			return 0;
		}
		_hits.Clear();
		EventSystem.current.RaycastAll(new PointerEventData(EventSystem.current) { position = position }, _hits);
		if (_hits.Count == 0 || !_hits[0].gameObject.transform.IsChildOf(_panel.transform))
		{
			return 0;
		}
		Button[] buttons = _menu.CachedButtons;
		if (buttons == null)
		{
			return 0;
		}
		for (int i = 0; i < buttons.Length; i++)
		{
			if (buttons[i] == null || !buttons[i].gameObject.activeInHierarchy)
			{
				continue;
			}
			Canvas canvas = buttons[i].GetComponentInParent<Canvas>();
			Camera camera = canvas == null || canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.rootCanvas.worldCamera;
			if (RectTransformUtility.RectangleContainsScreenPoint((RectTransform)buttons[i].transform, position, camera))
			{
				return i + 1;
			}
		}
		return 0;
	}

	/// <summary>高亮鼠标停着的那条；移开了（line 为 0）就恢复成掷出的结果。</summary>
	private void Highlight(int line)
	{
		if (line == 0)
		{
			_menu.UpdateSelection();
			return;
		}
		Button[] buttons = _menu.CachedButtons;
		for (int i = 0; i < buttons.Length; i++)
		{
			if (buttons[i] != null && buttons[i].gameObject.activeSelf)
			{
				DiceOption option = buttons[i].GetComponent<DiceOption>();
				if (option != null)
				{
					if (i + 1 == line)
					{
						option.Select();
					}
					else
					{
						option.UnSelect();
					}
				}
			}
		}
	}
}
