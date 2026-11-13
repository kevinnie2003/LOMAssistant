using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LOMAssistant.Config;
using LOMAssistant.Features.Story;
using Mortal.Story;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 鼠标滚轮推进对白：剧情里滚轮往下一格推进一句（和按空格一样），往上打开回想（和按 L 一样）；
/// 回想开着、已经在最底下时再往下滚就关上回想（剧情和养成地图的回想都算）。
/// 走游戏自己的按键回调，游戏的菜单、状态页这些开着时照原样不响应；鼠标在咖啡伴侣的窗口上时滚轮归窗口。
/// 回想面板（StoryLogPanel）是 1.0.6001 才有的，这里全用反射找它，旧版游戏只是没有关回想这一项。
/// </summary>
internal sealed class MouseWheelDialogue : MonoBehaviour
{
	private const float StepGap = 0.1f; // 一格滚轮最多推进一句：两次之间至少隔这么久（触控板一次滑动会连发很多下）
	private const float BottomHold = 0.25f; // 回想在最底下停了这么久，往下滚才关（刚从上面滚下来到底，不顺手关掉）
	private const float Quiet = 0.3f; // 滚轮开、关回想的这一下滚动，停住这么久才算滚完

	private static readonly MethodInfo Submit = AccessTools.Method(typeof(StoryManager), "SubmitActionChanged");
	private static readonly MethodInfo LogAction = AccessTools.Method(typeof(StoryManager), "LogActionChanged");
	private static readonly Type LogPanelType = AccessTools.TypeByName("Mortal.Core.StoryLogPanel");
	private static readonly FieldInfo LogScroll = LogPanelType != null ? AccessTools.Field(LogPanelType, "_scrollRect") : null;
	private static readonly PropertyInfo LogIsOpen = LogPanelType != null ? AccessTools.Property(LogPanelType, "IsOpen") : null;
	private static readonly MethodInfo LogClose = LogPanelType != null ? AccessTools.Method(LogPanelType, "Close") : null;
	private static readonly object[] NoContext = { default(InputAction.CallbackContext) };

	private static MouseWheelDialogue _instance;

	private readonly List<MonoBehaviour> _panels = new List<MonoBehaviour>();
	private int _panelsScene = -1;
	private float _nextFind;
	private MonoBehaviour _open; // 现在开着的回想面板
	private float _lastAboveBottom; // 回想最近一次不在最底下（或刚打开）的时间
	private float _nextStep;
	private float _quietUntil; // 滚轮关掉回想后，这一下滚完以前不推进对白
	private float _pinUntil; // 滚轮打开回想后，这一下滚完以前回想停在最底下，不跟着往上翻

	public static void Ensure(GameObject host)
	{
		if (_instance == null)
		{
			_instance = host.GetComponent<MouseWheelDialogue>() ?? host.AddComponent<MouseWheelDialogue>();
		}
	}

	private void Update()
	{
		Mouse mouse = Mouse.current;
		if (mouse == null || !ConfigManager.EnableMouseWheelDialogue.Value)
		{
			return;
		}
		TrackLogPanel();
		float y = mouse.scroll.ReadValue().y;
		if (y == 0f)
		{
			return;
		}
		float now = Time.unscaledTime;
		if (now < _quietUntil || now < _pinUntil)
		{
			// 开、关回想的那一下还没滚完：一直往后推，滚轮停住才算完
			if (now < _quietUntil) _quietUntil = now + Quiet;
			if (now < _pinUntil) _pinUntil = now + Quiet;
			return;
		}
		if (!Application.isFocused || UiHider.Active || StoryReviewPanel.OwnsEscape || EventPicker.OwnsEscape
			|| GamepadWheel.IsOpen || InputGuard.PointerOverModWindow)
		{
			return;
		}
		if (_open != null)
		{
			// 回想开着：往上滚交给回想自己翻；往下滚到底了才关
			if (y < 0f && now - _lastAboveBottom >= BottomHold)
			{
				Run(LogClose, _open, null);
				_open = null;
				_quietUntil = now + Quiet;
			}
			return;
		}
		if (SceneManager.GetActiveScene().name != "Story" || now < _nextStep)
		{
			return;
		}
		StoryManager story = StoryManager.Instance;
		if (story == null)
		{
			return;
		}
		_nextStep = now + StepGap;
		if (y < 0f)
		{
			Run(Submit, story, NoContext);
		}
		else
		{
			Run(LogAction, story, NoContext);
			_pinUntil = now + Quiet;
		}
	}

	/// <summary>滚轮打开回想的这一下还在滚：回想里的滚动区跟着往上翻了，拉回最底下。</summary>
	private void LateUpdate()
	{
		if (_open != null && Time.unscaledTime < _pinUntil && LogScroll?.GetValue(_open) is ScrollRect scroll)
		{
			scroll.velocity = Vector2.zero;
			scroll.verticalNormalizedPosition = 0f;
		}
	}

	/// <summary>找到开着的回想面板，记下它最近一次不在最底下的时间。</summary>
	private void TrackLogPanel()
	{
		if (LogPanelType == null || LogIsOpen == null)
		{
			return;
		}
		int scene = SceneManager.GetActiveScene().handle;
		if (scene != _panelsScene || (_panels.Count == 0 && Time.unscaledTime >= _nextFind))
		{
			_panelsScene = scene;
			_nextFind = Time.unscaledTime + 2f;
			_panels.Clear();
			foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(LogPanelType))
			{
				if (o is MonoBehaviour panel && panel.gameObject.scene.IsValid())
				{
					_panels.Add(panel);
				}
			}
		}
		MonoBehaviour open = null;
		foreach (MonoBehaviour panel in _panels)
		{
			if (panel != null && LogIsOpen.GetValue(panel) is bool isOpen && isOpen)
			{
				open = panel;
				break;
			}
		}
		if (open != _open)
		{
			_open = open;
			_lastAboveBottom = Time.unscaledTime;
		}
		if (_open != null && !AtBottom(_open))
		{
			_lastAboveBottom = Time.unscaledTime;
		}
	}

	private static bool AtBottom(MonoBehaviour panel)
	{
		if (!(LogScroll?.GetValue(panel) is ScrollRect scroll) || scroll.content == null)
		{
			return true;
		}
		RectTransform view = scroll.viewport != null ? scroll.viewport : (RectTransform)scroll.transform;
		// 内容不满一屏时没得滚，算在底下
		return scroll.content.rect.height <= view.rect.height + 1f || scroll.verticalNormalizedPosition <= 0.01f;
	}

	private static void Run(MethodInfo method, object target, object[] args)
	{
		if (method == null)
		{
			return;
		}
		try
		{
			method.Invoke(target, args);
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[滚轮推进对白] " + method.Name + " 失败: " + (e.InnerException ?? e).Message);
		}
	}
}
