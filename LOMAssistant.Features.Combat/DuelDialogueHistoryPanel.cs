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

/// <summary>决斗回想：记录显示入口，不预读随机台词；打开时暂停并隔离输入。</summary>
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
	private GUIStyle _frame, _title, _body, _muted, _button, _launcher;
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
		int round = CombatManager.Instance.CurrentRounds;
		if (History.Add(round, speaker, text))
		{
			// 决斗结束后，也能在游戏自己的回想中找到这些台词（和普通文字记录一样，不写入存档）。
			try
			{
				StoryLogCompat.AddDialogue(speaker, text);
			}
			catch (Exception e)
			{
				FileLogger.LogWarning("[决斗回想] 写入游戏回想失败，决斗窗口仍可查看：" + e.Message);
			}
		}
	}

	private void Update()
	{
		if (!Enabled || !InDuel || UiHider.Active || StoryReviewPanel.OwnsEscape || EventPicker.IsWaiting)
		{
			Close();
			return;
		}
		bool key = GUIUtility.keyboardControl == 0 && InputGuard.HotkeyDown(ConfigManager.DuelDialogueHistoryKey.Value);
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
			string key = ConfigManager.DuelDialogueHistoryKey.Value == KeyCode.None ? ""
				: " [" + TextConverter.ForDisplay(InputGuard.KeyName(ConfigManager.DuelDialogueHistoryKey.Value)) + "]";
			string label = TextConverter.ForDisplay("决斗回想") + key;
			float width = Mathf.Max(140f * scale, _launcher.CalcSize(new GUIContent(label)).x + 16f * scale);
			Rect button = new Rect((Screen.width - width) * 0.5f, Screen.height - 44f * scale, width, 32f * scale);
			InputGuard.Report("DuelDialogueHistoryButton", button);
			if (GUI.Button(button, label, _launcher))
			{
				Open();
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
			float width = Mathf.Min(1000f * scale, Screen.width * 0.92f);
			float height = Mathf.Min(760f * scale, Screen.height * 0.85f);
			_window = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
		}
		GUI.Box(_window, GUIContent.none, _frame);
		GUILayout.BeginArea(new Rect(_window.x + 20f * scale, _window.y + 16f * scale, _window.width - 40f * scale, _window.height - 32f * scale));
		GUILayout.BeginHorizontal();
		GUILayout.Label(TextConverter.ForDisplay("决斗回想"), _title);
		GUILayout.FlexibleSpace();
		GUILayout.Label(TextConverter.ForDisplay(string.Format("最近 {0} 条", History.Entries.Count)), _muted);
		if (GUILayout.Button("×", _button, GUILayout.Width(40f * scale)))
		{
			Close();
		}
		GUILayout.EndHorizontal();
		GUILayout.Space(8f * scale);
		if (_scrollToEnd)
		{
			_scroll.y = float.MaxValue;
		}
		_scroll = GUILayout.BeginScrollView(_scroll);
		if (History.Entries.Count == 0)
		{
			GUILayout.Label(TextConverter.ForDisplay("本场决斗还没有对白记录。"), _muted);
		}
		int round = -1;
		foreach (DuelDialogueHistory.Entry entry in History.Entries)
		{
			if (round != entry.Round)
			{
				round = entry.Round;
				GUILayout.Space(10f * scale);
				GUILayout.Label(TextConverter.ForDisplay(string.Format("第 {0} 回合", round)), _muted);
			}
			GUILayout.Space(8f * scale);
			GUILayout.Label(string.IsNullOrEmpty(entry.Speaker) ? TextConverter.ForDisplay("旁白") : TextConverter.Verbatim + entry.Speaker, _title);
			GUILayout.Label(TextConverter.Verbatim + entry.Text, _body);
		}
		GUILayout.EndScrollView();
		if (Event.current.type == EventType.Repaint)
		{
			_scrollToEnd = false;
		}
		GUILayout.Space(10f * scale);
		GUILayout.BeginHorizontal();
		GUILayout.Label(TextConverter.ForDisplay("回看时暂停决斗，关闭后继续。"), _muted);
		GUILayout.FlexibleSpace();
		if (GUILayout.Button(TextConverter.ForDisplay("回到最新"), _button))
		{
			_scrollToEnd = true;
		}
		GUILayout.EndHorizontal();
		GUILayout.EndArea();
		GUI.depth = depth;
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
		_frame = PanelTheme.Window(scale);
		_title = PanelTheme.Label(scale, 20, PanelTheme.Ink);
		_title.fontStyle = FontStyle.Bold;
		_body = PanelTheme.Label(scale, 22, PanelTheme.Ink);
		_muted = PanelTheme.Label(scale, 16, PanelTheme.InkMuted);
		_button = PanelTheme.Button(scale, 18);
		_launcher = PanelTheme.ToastButton(scale, 17);
		_launcher.normal.background = PanelTheme.ToastBackground;
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
