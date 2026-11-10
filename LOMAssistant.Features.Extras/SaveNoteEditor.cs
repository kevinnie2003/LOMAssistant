using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 存档备注输入框（IMGUI，屏幕居中），从 F8 面板打开。
/// </summary>
public class SaveNoteEditor : MonoBehaviour
{
	private const int WindowId = 0x4E4F54; // "NOT"
	internal const int MaxLength = 30;
	private const string FieldName = "LOMAssistant_SaveNote";

	private static SaveNoteEditor _instance;

	private string _slot;
	private string _text = "";
	private Action _onSaved;
	private bool _focusPending;
	private IMECompositionMode _prevIme;
	// The game's uGUI EventSystem is paused while the box is open so Enter/clicks don't also hit the save slots behind it.
	private EventSystem _pausedEventSystem;

	private float _scale = -1f;
	private int _fontMode = -1;
	private GUIStyle _label;
	private GUIStyle _button;
	private GUIStyle _field;
	private GUIStyle _window;
	private bool _dark;
	private float _opacity;

	public static bool IsOpen => _instance != null && _instance._slot != null;

	public static void Open(string slot, Action onSaved)
	{
		if (_instance == null)
		{
			_instance = LOMAssistantPlugin.Instance.gameObject.AddComponent<SaveNoteEditor>();
		}
		if (_instance._slot == null)
		{
			_instance._prevIme = Input.imeCompositionMode;
		}
		_instance._slot = slot;
		_instance._text = SaveNotePatch.GetNote(slot);
		_instance._onSaved = onSaved;
		_instance._focusPending = true;
		Input.imeCompositionMode = IMECompositionMode.On;
		if (_instance._pausedEventSystem == null && EventSystem.current != null && EventSystem.current.enabled)
		{
			_instance._pausedEventSystem = EventSystem.current;
			_instance._pausedEventSystem.enabled = false;
		}
	}

	private void Close(bool save)
	{
		if (save)
		{
			SaveNotePatch.SetNote(_slot, _text);
			try
			{
				_onSaved?.Invoke();
			}
			catch (Exception e)
			{
				FileLogger.LogError("存档备注：刷新栏位失败: " + e.Message);
			}
		}
		_slot = null;
		ClosedAt = Time.unscaledTime;
		_onSaved = null;
		Input.imeCompositionMode = _prevIme;
		if (_pausedEventSystem != null)
		{
			StartCoroutine(ResumeEventSystem(_pausedEventSystem));
			_pausedEventSystem = null;
		}
	}

	// Re-enable the game UI only after the click/Enter that closed the box is released,
	// otherwise the same click lands on the save slot behind it (e.g. reopening "overwrite?").
	private System.Collections.IEnumerator ResumeEventSystem(EventSystem eventSystem)
	{
		yield return null;
		while (StillHeld())
		{
			yield return null;
		}
		yield return null;
		if (eventSystem == null)
		{
			yield break;
		}
		if (IsOpen)
		{
			// Reopened meanwhile: keep it paused and let the next Close restore it.
			_pausedEventSystem = eventSystem;
			yield break;
		}
		eventSystem.enabled = true;
	}

	// 游戏用的是新输入系统，旧的 Input.GetMouseButton 一直读到「没按」，等不到松手；两套都看
	private static bool StillHeld()
	{
		var mouse = UnityEngine.InputSystem.Mouse.current;
		var keyboard = UnityEngine.InputSystem.Keyboard.current;
		if ((mouse != null && (mouse.leftButton.isPressed || mouse.rightButton.isPressed))
			|| (keyboard != null && (keyboard.enterKey.isPressed || keyboard.numpadEnterKey.isPressed)))
		{
			return true;
		}
		try
		{
			return Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetKey(KeyCode.Return) || Input.GetKey(KeyCode.KeypadEnter);
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}

	/// <summary>备注框上次关掉的时间（不受游戏倍速影响）：刚关掉的那一下不算点到后面的存档上。</summary>
	public static float ClosedAt { get; private set; } = -10f;

	private Rect _lastRect;

	private void OnGUI()
	{
		// 隐藏界面看 CG 时本 Mod 的窗口也不画
		if (UiHider.Active)
		{
			return;
		}
		if (_slot == null)
		{
			// 刚关掉的一小会儿仍然挡住这块：点「保存」「不写」松开鼠标时，不能落到下面的存档栏上读了档
			if (Time.unscaledTime - ClosedAt < 0.5f)
			{
				InputGuard.Report("SaveNote", _lastRect);
			}
			return;
		}
		float scale = LOMAssistant.PanelTheme.UiScale;
		if (ModFont.NeedsRebuild(scale, _scale, _fontMode) || _dark != PanelTheme.Dark || _opacity != PanelTheme.DialogOpacity)
		{
			BuildStyles(scale);
		}
		float w = 560f * _scale;
		float h = 220f * _scale;
		Rect rect = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
		_lastRect = rect;
		InputGuard.Report("SaveNote", rect);
		GUI.ModalWindow(WindowId, rect, DrawWindow, "", _window);
	}

	private void DrawWindow(int id)
	{
		Event e = Event.current;
		// 中文输入法还在候选（拼音没上屏）时，回车是给输入法确认用的，不能当成「保存」把输入框关掉：
		// 输入框在输入法组字途中消失，有玩家反馈会闪退。
		if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
			&& string.IsNullOrEmpty(Input.compositionString))
		{
			e.Use();
			Close(save: true);
			return;
		}

		// 标题单独一行（用窗口自带的标题栏时，字一大就压到输入框上）
		GUILayout.Label($"存档 {_slot} 备注", _label);
		GUILayout.Space(8f * _scale);
		GUI.SetNextControlName(FieldName);
		_text = GUILayout.TextField(_text, MaxLength, _field);
		if (_focusPending && e.type == EventType.Repaint)
		{
			GUI.FocusControl(FieldName);
			_focusPending = false;
		}
		GUILayout.Space(10f * _scale);
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("保存", _button))
		{
			Close(save: true);
		}
		if (GUILayout.Button("不写", _button))
		{
			Close(save: false);
		}
		GUILayout.EndHorizontal();
	}

	private void BuildStyles(float scale)
	{
		_scale = scale;
		_fontMode = ModFont.Mode;
		// 和咖啡伴侣主窗口同一套配色
		_dark = PanelTheme.Dark;
		_opacity = PanelTheme.DialogOpacity;
		_label = PanelTheme.Label(scale, 22, PanelTheme.Ink, false);
		_label.alignment = TextAnchor.MiddleCenter;
		_label.fontStyle = FontStyle.Bold;
		_button = PanelTheme.Button(scale, 20);
		_button.fixedHeight = 44f * scale;
		_field = PanelTheme.Field(scale, 22);
		_field.fixedHeight = 44f * scale;
		_window = PanelTheme.Window(scale);
		_window.fontSize = Mathf.Max(12, (int)(22f * scale));
		_window.normal.textColor = _window.onNormal.textColor = PanelTheme.Ink;

	}
}
