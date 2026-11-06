using LOMAssistant.Config;
using LOMAssistant.Features.Story;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LOMAssistant.Features.AnytimeSave;

/// <summary>
/// 随时存档的快捷键（默认 F5 存新档、F7 读最近的、F6 打开咖啡伴侣窗口的随时存档页）和左下角的提示。
/// 存档列表、备注、删除都在咖啡伴侣窗口的「随时存档」页（StoryReviewPanel.Anytime）。
/// </summary>
public partial class AnytimeSave
{
	private float _scale = -1f;
	private int _fontMode = -1;
	private GUIStyle _toast;

	private void UpdateHotkeys()
	{
		if (TextInputFocused()) return;
		if (InputGuard.HotkeyDown(ConfigManager.AnytimePanelKey.Value)) StoryReviewPanel.ToggleAnytime();
		else if (!installed) return;
		else if (InputGuard.HotkeyDown(ConfigManager.AnytimeSaveKey.Value)) SaveSlot();
		else if (InputGuard.HotkeyDown(ConfigManager.AnytimeLoadKey.Value)) LoadLatest();
		else if (InputGuard.HotkeyDown(ConfigManager.AnytimeRollbackKey.Value)) Rollback();
	}

	private static bool TextInputFocused()
	{
		if (GUIUtility.keyboardControl != 0) return true;
		var selected = EventSystem.current == null ? null : EventSystem.current.currentSelectedGameObject;
		if (selected == null) return false;
		var input = selected.GetComponent<UnityEngine.UI.InputField>();
		return (input != null && input.isFocused) || selected.GetComponent("TMP_InputField") != null;
	}

	/// <summary>页面上要显示的状态：正在提示的消息，或者现在为什么不能存。</summary>
	public string Status()
	{
		if (Time.realtimeSinceStartup < messageUntil && !string.IsNullOrEmpty(message)) return message;
		return CanSave(out string reason) ? "" : "暂不能保存：" + reason;
	}

	public bool CanSaveNow => CanSave(out _);
	public bool Busy => Replaying || Restoring || pendingLoad;
	public bool CanRollback => !Busy && RollbackCount > 0;

	/// <summary>恢复失败后回标题。</summary>
	public void BackToTitle()
	{
		Faulted = false; SetPanel(false); Time.timeScale = 1;
		Mortal.Core.SceneController.Instance.LoadTitle();
	}

	private GameObject _mask;
	private RectTransform _maskBar;
	private Text _maskCount;

	private const float MaskBarWidth = 480f;

	/// <summary>
	/// 恢复期间盖住快进的画面，中间写进度。用最高层的 Canvas，比游戏所有界面和转场都靠上。
	/// Fail 会清掉这两个状态，遮罩跟着消失，错误提示看得到。
	/// </summary>
	private void UpdateRestoreMask()
	{
		bool on = Restoring || Replaying;
		if (!on && _mask == null)
		{
			return;
		}
		if (_mask == null)
		{
			BuildRestoreMask();
		}
		if (_mask.activeSelf != on)
		{
			_mask.SetActive(on);
		}
		if (on)
		{
			_maskBar.sizeDelta = new Vector2(MaskBarWidth * (Replaying ? Mathf.Clamp01(cursor / (float)(target + 1)) : 0f), 6f);
			_maskCount.text = Replaying ? cursor + " / " + (target + 1) : "";
		}
	}

	private void BuildRestoreMask()
	{
		_mask = new GameObject("LOMAssistant.RestoreMask", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
		DontDestroyOnLoad(_mask);
		var canvas = _mask.GetComponent<Canvas>();
		canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		canvas.sortingOrder = 32767;
		var scaler = _mask.GetComponent<CanvasScaler>();
		scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
		scaler.referenceResolution = new Vector2(1920f, 1080f);
		var back = MaskImage("Back", Color.black, Vector2.zero, Vector2.zero);
		back.anchorMin = Vector2.zero;
		back.anchorMax = Vector2.one;
		MaskImage("Track", new Color(1f, 1f, 1f, 0.2f), new Vector2(0f, 0f), new Vector2(MaskBarWidth, 6f));
		_maskBar = MaskImage("Bar", PanelTheme.ToastText, new Vector2(0f, 0f), new Vector2(0f, 6f));
		_maskBar.pivot = new Vector2(0f, 0.5f);
		_maskBar.anchoredPosition = new Vector2(-MaskBarWidth / 2f, 0f);
		MaskText("Label", 44f).text = TextConverter.ForDisplay("正在读取随时存档");
		_maskCount = MaskText("Count", -36f);
		_mask.SetActive(false);
	}

	private RectTransform MaskImage(string name, Color color, Vector2 position, Vector2 size)
	{
		var go = new GameObject(name, typeof(RectTransform), typeof(Image));
		go.transform.SetParent(_mask.transform, false);
		var image = go.GetComponent<Image>();
		image.color = color;
		image.raycastTarget = false;
		var rect = (RectTransform)go.transform;
		rect.anchoredPosition = position;
		rect.sizeDelta = size;
		return rect;
	}

	private Text MaskText(string name, float y)
	{
		var go = new GameObject(name, typeof(RectTransform), typeof(Text));
		go.transform.SetParent(_mask.transform, false);
		var text = go.GetComponent<Text>();
		var style = new GUIStyle();
		ModFont.Apply(style);
		text.font = style.font != null ? style.font : Font.CreateDynamicFontFromOSFont("Microsoft YaHei", 26);
		text.fontSize = 26;
		text.alignment = TextAnchor.MiddleCenter;
		text.color = PanelTheme.ToastText;
		text.raycastTarget = false;
		var rect = (RectTransform)go.transform;
		rect.anchoredPosition = new Vector2(0f, y);
		rect.sizeDelta = new Vector2(1200f, 44f);
		return text;
	}

	private void OnGUI()
	{
		// 隐藏界面看 CG 时本 Mod 的提示也不画；咖啡伴侣窗口开着时提示在窗口里
		if (UiHider.Active || StoryReviewPanel.IsOpen)
		{
			return;
		}
		float scale = LOMAssistant.PanelTheme.UiScale;
		if (scale <= 0) return;
		if (ModFont.NeedsRebuild(scale, _scale, _fontMode))
		{
			_scale = scale;
			_fontMode = ModFont.Mode;
			_toast = PanelTheme.Toast(scale, 22);
			_toast.alignment = TextAnchor.MiddleLeft;
			_toast.padding = new RectOffset((int)(12 * scale), (int)(12 * scale), (int)(8 * scale), (int)(8 * scale));
		}
		if (!Replaying && Time.realtimeSinceStartup < messageUntil)
		{
			var content = new GUIContent(message);
			float width = Mathf.Min(Screen.width - 40f * scale, Mathf.Max(200f * scale, _toast.CalcSize(content).x));
			float height = _toast.CalcHeight(content, width);
			GUI.Box(new Rect(20f * scale, Screen.height - height - 20f * scale, width, height), content, PanelTheme.Sync(_toast));
		}
	}
}
