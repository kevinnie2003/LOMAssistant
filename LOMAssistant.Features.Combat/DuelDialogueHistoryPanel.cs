using System;
using HarmonyLib;
using LOMAssistant.Config;
using LOMAssistant.Features.Extras;
using LOMAssistant.Features.Story;
using Mortal.Combat;
using Mortal.Core;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LOMAssistant.Features.Combat;

/// <summary>决斗回想：记录显示入口，不预读随机台词；按 L 打开（和游戏别处打开回想的键一样），打开时暂停并隔离输入。</summary>
internal sealed class DuelDialogueHistoryPanel : MonoBehaviour
{
	private static DuelDialogueHistoryPanel _instance;
	private static readonly DuelDialogueHistory History = new DuelDialogueHistory();
	private bool _visible;
	private int _closedFrame = -10;
	private int _screenWidth;
	private int _screenHeight;
	private Rect _window;
	private Vector2 _scroll;
	private float _scale = -1f;
	private bool _dark;
	private int _font = -1;
	private GUIStyle _frame, _title, _name, _body, _narration, _round, _muted, _button;
	private float _nameWidth;
	private int _nameWidthCount = -1;
	private bool _scrollToEnd;

	internal static bool IsOpen => _instance != null && _instance._visible;
	internal static bool BlocksInput => _instance != null && (IsOpen || Time.frameCount - _instance._closedFrame <= 1);
	private static bool Enabled => ConfigManager.EnableDuelDialogueHistory?.Value ?? true;
	private static bool InDuel => CombatManager.Instance != null && SceneManager.GetActiveScene().name == "Combat";

	internal static void Ensure(GameObject host)
	{
		if (_instance == null)
		{
			_instance = host.GetComponent<DuelDialogueHistoryPanel>() ?? host.AddComponent<DuelDialogueHistoryPanel>();
			InputGuard.Ensure();
		}
	}

	internal static void Reset()
	{
		_instance?.Close();
		History.Clear();
	}

	internal static void Record(string speaker, string text)
	{
		if (!Enabled || !InDuel)
		{
			return;
		}
		// 不往游戏自己的回想里写：那边最多 300 条，一场决斗的嘴攻会把前面的剧情挤掉
		History.Add(CombatManager.Instance.CurrentRounds, speaker, text);
	}

	/// <summary>快进按钮上方的「回想」按钮点了：开着就关，关着就开。</summary>
	internal static void Toggle()
	{
		if (_instance == null)
		{
			return;
		}
		if (_instance._visible)
		{
			_instance.Close();
		}
		else
		{
			_instance.Open();
		}
	}

	private void LateUpdate() => DuelBacklogButton.Follow(Enabled && InDuel && !UiHider.Active);

	private void Update()
	{
		if (!Enabled || !InDuel || UiHider.Active || StoryReviewPanel.OwnsEscape || EventPicker.IsWaiting)
		{
			Close();
			return;
		}
		bool key = GUIUtility.keyboardControl == 0 && InputGuard.HotkeyDown(KeyCode.L);
		if (_visible)
		{
			bool back = (Keyboard.current?.escapeKey.wasPressedThisFrame ?? false)
				|| (Mouse.current?.rightButton.wasPressedThisFrame ?? false)
				|| (Gamepad.current?.buttonEast.wasPressedThisFrame ?? false);
			if (key || back)
			{
				Close();
			}
		}
		else if (key && !InputGuard.BlockKeyboard)
		{
			Open();
		}
	}

	private void Open()
	{
		if (_visible || !Enabled || !InDuel)
		{
			return;
		}
		_visible = true;
		_scrollToEnd = true;
		ClearPendingInput();
		InputGuard.Report("DuelDialogueHistory", new Rect(0, 0, Screen.width, Screen.height));
		// Tick 保留游戏的基础倍速，只在回想开着时输出 0，关闭后不会把倍速乘两次。
		GameSpeed.Tick();
	}

	private void Close()
	{
		if (!_visible)
		{
			return;
		}
		_visible = false;
		_closedFrame = Time.frameCount;
		DuelBacklogButton.Reset();
		ClearPendingInput();
		GUIUtility.keyboardControl = 0;
		GameSpeed.Tick();
	}

	private static void ClearPendingInput()
	{
		if (CombatManager.Instance != null)
		{
			Traverse.Create(CombatManager.Instance).Field("_triggerNextInput").SetValue(false);
		}
	}

	private void OnDisable() => Close();
	private void OnDestroy()
	{
		Close();
		if (_instance == this)
		{
			_instance = null;
		}
	}

	private void OnGUI()
	{
		if (!Enabled || !InDuel || UiHider.Active || StoryReviewPanel.OwnsEscape || EventPicker.IsWaiting)
		{
			return;
		}
		float scale = PanelTheme.UiScale;
		if (scale <= 0f)
		{
			return;
		}
		Styles(scale);
		if (!_visible)
		{
			// 这次启动还没进过养成地图、没有游戏的回想按钮可复制时，在同一位置画个简单按钮
			if (DuelBacklogButton.FallbackRect() is Rect r)
			{
				InputGuard.Report("DuelDialogueHistoryButton", r);
				if (GUI.Button(r, TextConverter.ForDisplay("决斗回想"), _button))
				{
					Open();
				}
			}
			return;
		}
		InputGuard.Report("DuelDialogueHistory", new Rect(0, 0, Screen.width, Screen.height));
		int depth = GUI.depth;
		GUI.depth = -1000;
		Color color = GUI.color;
		GUI.color = new Color(0, 0, 0, 0.55f);
		GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
		GUI.color = color;
		if (_screenWidth != Screen.width || _screenHeight != Screen.height)
		{
			_screenWidth = Screen.width;
			_screenHeight = Screen.height;
			float width = Mathf.Min(820f * scale, Screen.width * 0.9f);
			float height = Mathf.Min(720f * scale, Screen.height * 0.8f);
			_window = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
		}
		GUI.Box(_window, GUIContent.none, _frame);
		GUILayout.BeginArea(new Rect(_window.x + 24f * scale, _window.y + 14f * scale, _window.width - 48f * scale, _window.height - 28f * scale));
		GUILayout.BeginHorizontal();
		GUILayout.Label(TextConverter.ForDisplay("决斗回想"), _title);
		GUILayout.FlexibleSpace();
		if (GUILayout.Button("×", _button, GUILayout.Width(36f * scale), GUILayout.Height(30f * scale)))
		{
			Close();
		}
		GUILayout.EndHorizontal();
		Line(GUILayoutUtility.GetRect(1f, 1f, GUILayout.ExpandWidth(true)), PanelTheme.InkLine);
		GUILayout.Space(6f * scale);
		if (_scrollToEnd)
		{
			_scroll.y = float.MaxValue;
		}
		_scroll = GUILayout.BeginScrollView(_scroll);
		if (History.Entries.Count == 0)
		{
			GUILayout.Space(12f * scale);
			GUILayout.Label(TextConverter.ForDisplay("本场决斗还没有对白记录。"), _muted);
		}
		// 名字一栏按最长的名字定宽，台词左边对齐
		if (_nameWidthCount != History.Entries.Count)
		{
			_nameWidthCount = History.Entries.Count;
			_nameWidth = 0f;
			foreach (DuelDialogueHistory.Entry entry in History.Entries)
			{
				_nameWidth = Mathf.Max(_nameWidth, _name.CalcSize(new GUIContent(entry.Speaker)).x);
			}
			_nameWidth = Mathf.Clamp(_nameWidth, 60f * scale, 200f * scale);
		}
		int round = -1;
		foreach (DuelDialogueHistory.Entry entry in History.Entries)
		{
			if (round != entry.Round)
			{
				round = entry.Round;
				GUILayout.Space(8f * scale);
				Divider(TextConverter.ForDisplay(string.Format("第 {0} 回合", round)), scale);
			}
			GUILayout.Space(6f * scale);
			GUILayout.BeginHorizontal();
			GUILayout.Label(TextConverter.Verbatim + entry.Speaker, _name, GUILayout.Width(_nameWidth));
			GUILayout.Space(14f * scale);
			GUILayout.Label(TextConverter.Verbatim + entry.Text, entry.Speaker.Length == 0 ? _narration : _body);
			GUILayout.EndHorizontal();
		}
		GUILayout.Space(8f * scale);
		GUILayout.EndScrollView();
		if (Event.current.type == EventType.Repaint)
		{
			_scrollToEnd = false;
		}
		GUILayout.EndArea();
		GUI.depth = depth;
	}

	/// <summary>回合分隔：两边细线，中间回合数。</summary>
	private void Divider(string text, float scale)
	{
		Rect r = GUILayoutUtility.GetRect(1f, 24f * scale, GUILayout.ExpandWidth(true));
		float w = _round.CalcSize(new GUIContent(text)).x;
		float mid = r.y + r.height * 0.5f;
		float gap = 10f * scale;
		Line(new Rect(r.x, mid, (r.width - w) * 0.5f - gap, 1f), PanelTheme.InkLine);
		Line(new Rect(r.x + (r.width + w) * 0.5f + gap, mid, (r.width - w) * 0.5f - gap, 1f), PanelTheme.InkLine);
		GUI.Label(new Rect(r.x + (r.width - w) * 0.5f, r.y, w, r.height), text, _round);
	}

	private static void Line(Rect r, Color c)
	{
		if (Event.current.type != EventType.Repaint || r.width <= 0f)
		{
			return;
		}
		Color old = GUI.color;
		GUI.color = c;
		GUI.DrawTexture(r, Texture2D.whiteTexture);
		GUI.color = old;
	}

	private void Styles(float scale)
	{
		if (Mathf.Approximately(scale, _scale) && _dark == PanelTheme.Dark && _font == ConfigManager.PanelFont.Value)
		{
			return;
		}
		_scale = scale;
		_dark = PanelTheme.Dark;
		_font = ConfigManager.PanelFont.Value;
		_screenWidth = 0;
		_nameWidthCount = -1;
		_frame = PanelTheme.Window(scale);
		_title = PanelTheme.Label(scale, 22, PanelTheme.Ink);
		_title.fontStyle = FontStyle.Bold;
		_name = PanelTheme.Label(scale, 19, PanelTheme.Cinnabar, wrap: false);
		_name.alignment = TextAnchor.UpperRight;
		_body = PanelTheme.Label(scale, 19, PanelTheme.Ink);
		_narration = PanelTheme.Label(scale, 19, PanelTheme.InkMuted);
		_round = PanelTheme.Label(scale, 15, PanelTheme.InkMuted, wrap: false);
		_round.alignment = TextAnchor.MiddleCenter;
		_muted = PanelTheme.Label(scale, 17, PanelTheme.InkMuted);
		_button = PanelTheme.Button(scale, 18);
	}
}

internal static class DuelDialogueHistoryPatch
{
	[HarmonyPostfix]
	[HarmonyPriority(Priority.Last)]
	[HarmonyPatch(typeof(CombatDialogUI), nameof(CombatDialogUI.Say))]
	private static void Say_Postfix(string name, string text, Text ____nameText, Text ____text)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		try
		{
			// 读已经过游戏和翻译补丁处理的显示文字；旁白的空名字不使用本地化查找的回退键。
			DuelDialogueHistoryPanel.Record(string.IsNullOrEmpty(name) ? "" : ____nameText?.text ?? name, ____text?.text ?? text);
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[决斗回想] 记录对白失败（已忽略）：" + e.Message);
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(Mortal.Free.FreeManager), "Awake")]
	private static void FreeAwake_Postfix()
	{
		try
		{
			DuelBacklogButton.SaveIcons();
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[决斗回想] 存回想图标失败（决斗里改画简单按钮）：" + e.Message);
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(CombatUIManager), "Start")]
	private static void CombatUiStart_Postfix(CombatUIManager __instance)
	{
		try
		{
			DuelBacklogButton.Create(__instance);
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[决斗回想] 放回想按钮失败（仍可按 L）：" + e.Message);
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(CombatManager), "Awake")]
	private static void Awake_Postfix() => DuelDialogueHistoryPanel.Reset();

	[HarmonyPrefix]
	[HarmonyPatch(typeof(CombatManager), "OnDisable")]
	private static void OnDisable_Prefix() => DuelDialogueHistoryPanel.Reset();

	// NextInputAction 不检查 phase；连 canceled 也可能写入待推进标记，所以独立挡下整个回调。
	[HarmonyPrefix]
	[HarmonyPatch(typeof(CombatManager), "NextInputAction")]
	private static bool NextInputAction_Prefix() => !DuelDialogueHistoryPanel.BlocksInput;

	// 时间暂停不能拦住已经进入 WaitUntil 的对白等待，回想期间明确禁止满足它。
	[HarmonyPrefix]
	[HarmonyPatch(typeof(CombatManager), "GetNextInput")]
	private static bool GetNextInput_Prefix(ref bool __result)
	{
		if (!DuelDialogueHistoryPanel.BlocksInput)
		{
			return true;
		}
		__result = false;
		return false;
	}
}
