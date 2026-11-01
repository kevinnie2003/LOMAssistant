using System;
using System.Collections.Generic;
using HarmonyLib;
using System.Linq;
using LOMAssistant.Config;
using LOMAssistant.Features.Story;
using Mortal.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LOMAssistant.Features.Memo;

/// <summary>
/// 备忘录悬浮窗：F10 或设置页开关。条件、当前值、进度条都现读现算；
/// 窗口标题栏拖动、右下角缩放、底部滑条调不透明度，位置和大小跟着配置文件走。
/// </summary>
internal sealed class MemoPanel : MonoBehaviour
{
	private const int ControlId = 0x4D454F;
	private const float DefaultWidth = 360f;
	private const float DefaultHeight = 330f;
	private const float DefaultMargin = 8f;
	private const float ResizeHandleSize = 18f;
	private const float TitleBarHeight = 30f;
	private const float MinWidth = 260f;
	private const float MinHeight = 110f;

	private static readonly Color FrameFill = new Color(0.08f, 0.07f, 0.06f, 1f);
	private static readonly Color FrameEdge = new Color(0.55f, 0.45f, 0.33f, 0.9f);
	private static readonly Color Dim = new Color(0.66f, 0.61f, 0.54f, 1f);
	private static readonly string Green = "#A3B86C";
	private static readonly string Red = "#CD594A";
	private static readonly string Gold = "#BCA136";
	private static readonly string Grey = "#B5AA98";

	private static MemoPanel _instance;

	private bool _visible;
	private Rect _rect;
	private bool _placed;
	private bool _dragging;
	private bool _resizing;
	private Vector2 _pointerOffset;
	private int _screenWidth;
	private int _screenHeight;
	private bool _geometryDirty;
	private float _scale = -1f;
	private GUIStyle _frame;
	private GUIStyle _title;
	private GUIStyle _text;
	private GUIStyle _dim;
	private GUIStyle _button;
	private GUIStyle _buttonSelected;
	private GUIStyle _buttonLeft;
	private GUIStyle _field;
	private GUIStyle _primary;
	private Vector2 _scroll;
	private Vector2 _editScroll;
	private string _notice = "";
	private float _noticeUntil;
	private readonly Dictionary<string, bool> _metState = new Dictionary<string, bool>();
	private readonly Dictionary<string, int> _nearState = new Dictionary<string, int>();
	private MemoItem _editing;
	private bool _isNew;
	private string _editTitle = "";
	private string _editSearch = "";
	private string _editValue = "1";
	private string _editNote = "";
	private MemoTarget _editTarget;
	private MemoOp _editOp = MemoOp.AtLeast;
	private int _editYear = 1;
	private int _editMonth = 1;
	private int _editStage = 1;
	private bool _editDeadline;
	private MemoItem _confirmDelete;

	public static void Ensure(GameObject host)
	{
		if (_instance == null)
		{
			_instance = host.GetComponent<MemoPanel>() ?? host.AddComponent<MemoPanel>();
		}
	}

	public static void Toggle()
	{
		if (_instance == null || !ConfigManager.MemoFloat.Value)
		{
			return;
		}
		_instance._visible = !_instance._visible;
		if (_instance._visible)
		{
			MemoTargets.Invalidate();
		}
	}

	private void Update()
	{
		if ((_dragging || _resizing) && !Input.GetMouseButton(0))
		{
			StopPointerInteraction();
		}
		Tick();
	}

	private void Tick()
	{
		if (!ConfigManager.MemoFloat.Value || !ConfigManager.MemoNotify.Value || !StoryNodes.HasCurrentSave)
		{
			return;
		}
		int now = CurrentRounds();
		if (now < 0)
		{
			return;
		}
		foreach (MemoItem item in MemoStore.Items)
		{
			if (item.done || item.muted)
			{
				continue;
			}
			MemoTarget target = MemoTargets.Find(item.kind, item.key);
			bool met = target != null && Met(item, target);
			if (_metState.TryGetValue(item.id, out bool wasMet) && met != wasMet)
			{
				Notice(met ? "备忘达成：" : "备忘条件回落：", Title(item, target), met ? Green : Gold);
			}
			_metState[item.id] = met;

			if (item.deadline < 0)
			{
				continue;
			}
			int left = item.deadline - now;
			int before = _nearState.TryGetValue(item.id, out int state) ? state : int.MaxValue;
			if (before > 0 && left <= 0 && !met)
			{
				Notice("备忘过期：", Title(item, target) + "　" + StoryAtlas.DateText(item.deadline), Red);
			}
			else if (before > left && left > 0 && left <= ConfigManager.MemoAhead.Value)
			{
				Notice("备忘还剩 " + left + " 旬：", Title(item, target), Gold);
			}
			_nearState[item.id] = left;
		}
	}

	private void OnGUI()
	{
		if (!_visible || !ConfigManager.MemoFloat.Value || UiHider.Active || EventPicker.IsWaiting
			|| SceneManager.GetActiveScene().name == "Title" || StoryReviewPanel.OwnsEscape)
		{
			return;
		}
		float scale = PanelTheme.UiScale;
		if (scale <= 0f)
		{
			return;
		}
		EnsureStyles(scale);
		EnsureRect(scale);
		HandlePointerInput();

		Color previous = GUI.color;
		GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp(ConfigManager.MemoOpacity.Value, 0.2f, 1f));
		GUI.Box(_rect, GUIContent.none, _frame);
		GUI.color = previous;

		GUILayout.BeginArea(new Rect(_rect.x + 2f, _rect.y + 2f, _rect.width - 4f, _rect.height - 4f));
		DrawTitleBar();
		if (!ConfigManager.MemoCollapsed.Value)
		{
			if (_editing == null)
			{
				DrawList();
			}
			else
			{
				DrawEditor();
			}
		}
		GUILayout.EndArea();

		if (!ConfigManager.MemoCollapsed.Value)
		{
			DrawResizeHandle();
		}
		InputGuard.Report("Memo", _rect);
	}

	private void EnsureStyles(float scale)
	{
		if (Mathf.Approximately(scale, _scale))
		{
			return;
		}
		_scale = scale;
		_frame = PanelTheme.Toast(scale, 16);
		_frame.padding = new RectOffset(1, 1, 1, 1);
		_title = OneLine(PanelTheme.Label(scale, 17, PanelTheme.ToastText));
		_title.fontStyle = FontStyle.Bold;
		_text = PanelTheme.Label(scale, 14, PanelTheme.ToastText);
		_dim = PanelTheme.Label(scale, 12, Dim);
		_button = PanelTheme.ToastButton(scale, 13);
		_buttonSelected = new GUIStyle(_button);
		_buttonSelected.normal.background = _buttonSelected.hover.background;
		_buttonLeft = OneLine(PanelTheme.ToastButton(scale, 13));
		_buttonLeft.alignment = TextAnchor.MiddleLeft;
		_field = PanelTheme.Field(scale, 13);
		_field.normal.textColor = _field.focused.textColor = _field.hover.textColor = PanelTheme.ToastText;
		_primary = PanelTheme.ToastButton(scale, 14);
		_primary.normal.background = PanelTheme.Framed(new Color(0.36f, 0.15f, 0.12f, 0.95f), FrameEdge);
	}

	private static GUIStyle OneLine(GUIStyle style)
	{
		return new GUIStyle(style) { wordWrap = false, richText = true };
	}

	private void EnsureRect(float scale)
	{
		if (_placed && _screenWidth == Screen.width && _screenHeight == Screen.height)
		{
			return;
		}
		_screenWidth = Screen.width;
		_screenHeight = Screen.height;
		float width = ConfigManager.MemoWidth.Value > 0f ? ConfigManager.MemoWidth.Value * Screen.width : DefaultWidth * scale;
		float height = ConfigManager.MemoHeight.Value > 0f ? ConfigManager.MemoHeight.Value * Screen.height : DefaultHeight * scale;
		_rect = new Rect(
			ConfigManager.MemoX.Value >= 0f ? ConfigManager.MemoX.Value * Screen.width : Screen.width - width - DefaultMargin * scale,
			ConfigManager.MemoY.Value >= 0f ? ConfigManager.MemoY.Value * Screen.height : DefaultMargin * scale + 120f * scale,
			width,
			height);
		ClampRect(scale);
		_placed = true;
	}

	private void HandlePointerInput()
	{
		Event e = Event.current;
		if (e == null || e.button != 0)
		{
			return;
		}
		Rect resize = GetResizeHandleRect();
		Rect bar = TitleRect();
		bool canStart = ConfigManager.MemoCollapsed.Value && _rect.Contains(e.mousePosition)
			|| (!ConfigManager.MemoCollapsed.Value && bar.Contains(e.mousePosition)
				&& e.mousePosition.x < bar.xMax - ButtonsWidth(_scale));
		if (e.type == EventType.MouseDown && canStart)
		{
			_resizing = !ConfigManager.MemoCollapsed.Value && resize.Contains(e.mousePosition);
			_dragging = !_resizing;
			_pointerOffset = e.mousePosition - new Vector2(_rect.x, _rect.y);
			GUIUtility.hotControl = ControlId;
			e.Use();
			return;
		}
		if (e.type == EventType.MouseDrag && GUIUtility.hotControl == ControlId)
		{
			if (_resizing)
			{
				_rect.width = e.mousePosition.x - _rect.x;
				_rect.height = e.mousePosition.y - _rect.y;
			}
			else if (_dragging)
			{
				_rect.x = e.mousePosition.x - _pointerOffset.x;
				_rect.y = e.mousePosition.y - _pointerOffset.y;
			}
			ClampRect(_scale);
			_geometryDirty = true;
			e.Use();
			return;
		}
		if (e.rawType == EventType.MouseUp && GUIUtility.hotControl == ControlId)
		{
			StopPointerInteraction();
			e.Use();
		}
	}

	private Rect TitleRect()
	{
		return new Rect(_rect.x + 2f, _rect.y + 2f, _rect.width - 4f, TitleBarHeight * _scale);
	}

	private static float ButtonsWidth(float scale)
	{
		return 120f * scale;
	}

	private void StopPointerInteraction()
	{
		if (_geometryDirty)
		{
			SaveGeometry();
		}
		_dragging = false;
		_resizing = false;
		_geometryDirty = false;
		if (GUIUtility.hotControl == ControlId)
		{
			GUIUtility.hotControl = 0;
		}
	}

	private void SaveGeometry()
	{
		ConfigManager.MemoX.Value = _rect.x / Screen.width;
		ConfigManager.MemoY.Value = _rect.y / Screen.height;
		ConfigManager.MemoWidth.Value = _rect.width / Screen.width;
		ConfigManager.MemoHeight.Value = _rect.height / Screen.height;
		ConfigManager.SaveConfig();
	}

	private void ClampRect(float scale)
	{
		float margin = DefaultMargin * scale;
		_rect.width = Mathf.Clamp(_rect.width, MinWidth * scale, Mathf.Max(1f, Screen.width - margin * 2f));
		_rect.height = Mathf.Clamp(_rect.height, MinHeight * scale, Mathf.Max(1f, Screen.height - margin * 2f));
		_rect.x = Mathf.Clamp(_rect.x, margin, Screen.width - margin - _rect.width);
		_rect.y = Mathf.Clamp(_rect.y, margin, Screen.height - margin - _rect.height);
	}

	private Rect GetResizeHandleRect()
	{
		float size = ResizeHandleSize * _scale;
		return new Rect(_rect.xMax - size - 2f, _rect.yMax - size - 2f, size, size);
	}

	private void DrawResizeHandle()
	{
		Rect handle = GetResizeHandleRect();
		Color previous = GUI.color;
		GUI.color = new Color(1f, 1f, 1f, 0.55f);
		for (int i = 0; i < 3; i++)
		{
			float offset = (i + 1) * handle.width / 4f;
			GUI.DrawTexture(new Rect(handle.x + offset, handle.yMax - offset, handle.width - offset, 1f), Texture2D.whiteTexture);
			GUI.DrawTexture(new Rect(handle.xMax - offset, handle.y + offset, 1f, handle.height - offset), Texture2D.whiteTexture);
		}
		GUI.color = previous;
	}

	private void DrawTitleBar()
	{
		Rect bar = GUILayoutUtility.GetRect(_rect.width - 4f, TitleBarHeight * _scale);
		int unfinished = MemoStore.Items.Count(item => !item.done);
		GUI.Label(new Rect(bar.x + 4f * _scale, bar.y, bar.width - ButtonsWidth(_scale) - 8f * _scale, bar.height),
			TextConverter.ForDisplay("备忘 " + unfinished), _title);
		float w = 26f * _scale;
		float x = bar.xMax - w * 4f - 3f * _scale;
		Rect[] buttons =
		{
			new Rect(x, bar.y + 3f * _scale, w, bar.height - 6f * _scale),
			new Rect(x + w, bar.y + 3f * _scale, w, bar.height - 6f * _scale),
			new Rect(x + w * 2f, bar.y + 3f * _scale, w, bar.height - 6f * _scale),
			new Rect(x + w * 3f, bar.y + 3f * _scale, w, bar.height - 6f * _scale),
		};
		if (GUI.Button(buttons[0], "＋", _button))
		{
			StartNew();
		}
		if (GUI.Button(buttons[1], ConfigManager.MemoCollapsed.Value ? "□" : "—", _button))
		{
			ConfigManager.MemoCollapsed.Value = !ConfigManager.MemoCollapsed.Value;
		}
		if (GUI.Button(buttons[2], "存", _button))
		{
			MemoStore.Save();
			Notice("已保存：", "备忘录写进 memo.json", Green);
		}
		if (GUI.Button(buttons[3], "×", _button))
		{
			_visible = false;
		}
		if (!ConfigManager.MemoCollapsed.Value)
		{
			DrawLine(new Rect(bar.x, bar.yMax, bar.width, 1f));
		}
	}

	private void DrawList()
	{
		Rect area = GUILayoutUtility.GetRect(_rect.width - 4f, _rect.height - TitleBarHeight * _scale - 4f);
		Rect body = new Rect(area.x + 3f * _scale, area.y + 3f * _scale,
			area.width - 6f * _scale, area.height - 34f * _scale);
		if (Time.unscaledTime < _noticeUntil)
		{
			GUI.Label(new Rect(area.x + 3f * _scale, area.y, area.width - 6f * _scale, 30f * _scale), TextConverter.ForDisplay(_notice), _text);
		}
		_scroll = GUI.BeginScrollView(body, _scroll, new Rect(0f, 0f, body.width - 16f * _scale, Mathf.Max(body.height, ListHeight())));
		if (MemoStore.Items.Count == 0)
		{
			GUI.Label(new Rect(0f, 8f * _scale, body.width - 16f * _scale, 70f * _scale),
				TextConverter.ForDisplay("还没有备忘。点右上角「＋」新建一条。"), _dim);
		}
		else
		{
			Rect row = new Rect(0f, 0f, body.width - 16f * _scale, 0f);
			foreach (MemoItem item in MemoStore.Items)
			{
				row.height = RowHeight(item);
				DrawItem(row, item);
				row.y += row.height;
				DrawLine(new Rect(row.x, row.y, row.width, 1f));
				row.y += 6f * _scale;
			}
		}
		GUI.EndScrollView();

		Rect bottom = new Rect(area.x + 3f * _scale, area.yMax - 30f * _scale, area.width - 6f * _scale, 24f * _scale);
		GUI.Label(new Rect(bottom.x, bottom.y, 38f * _scale, bottom.height), TextConverter.ForDisplay("透明"), _dim);
		float value = GUI.HorizontalSlider(new Rect(bottom.x + 42f * _scale, bottom.y + 7f * _scale, bottom.width - 92f * _scale, 10f * _scale),
			Mathf.Clamp(ConfigManager.MemoOpacity.Value, 0.2f, 1f), 0.2f, 1f);
		if (!Mathf.Approximately(value, ConfigManager.MemoOpacity.Value))
		{
			ConfigManager.MemoOpacity.Value = value;
		}
		GUI.Label(new Rect(bottom.xMax - 48f * _scale, bottom.y, 48f * _scale, bottom.height), Mathf.RoundToInt(value * 100f) + "%", _dim);
	}

	private float ListHeight()
	{
		return MemoStore.Items.Sum(RowHeight) + Mathf.Max(0f, MemoStore.Items.Count - 1) * 6f * _scale + 20f * _scale;
	}

	private float RowHeight(MemoItem item)
	{
		MemoTarget target = MemoTargets.Find(item.kind, item.key);
		float height = 83f * _scale;
		if (target == null || target.Max() <= 0)
		{
			height -= 10f * _scale;
		}
		if (!string.IsNullOrEmpty(item.note))
		{
			height += 20f * _scale;
		}
		return height;
	}

	private void DrawItem(Rect row, MemoItem item)
	{
		MemoTarget target = MemoTargets.Find(item.kind, item.key);
		bool met = target != null && Met(item, target);
		int now = CurrentRounds();
		bool overdue = item.deadline >= 0 && now >= 0 && item.deadline - now < 0;
		bool near = item.deadline >= 0 && now >= 0 && item.deadline - now >= 0 && item.deadline - now <= ConfigManager.MemoAhead.Value;
		string status = item.done ? "<color=" + Grey + ">✓</color>"
			: met ? "<color=" + Green + ">●</color>"
			: overdue ? "<color=" + Red + ">●</color>"
			: near || target == null ? "<color=" + Gold + ">●</color>"
			: "○";
		float icon = 20f * _scale;
		GUI.Label(new Rect(row.x, row.y + 4f * _scale, icon, icon), status, _text);
		GUI.Label(new Rect(row.x + icon, row.y + 5f * _scale, row.width - icon - 116f * _scale, 20f * _scale),
			TextConverter.ForDisplay(Title(item, target)), OneLine(_text));

		float bw = 24f * _scale;
		float bx = row.xMax - bw * 4f;
		Rect[] buttons =
		{
			new Rect(bx, row.y + 5f * _scale, bw, 18f * _scale),
			new Rect(bx + bw, row.y + 5f * _scale, bw, 18f * _scale),
			new Rect(bx + bw * 2f, row.y + 5f * _scale, bw, 18f * _scale),
			new Rect(bx + bw * 3f, row.y + 5f * _scale, bw, 18f * _scale),
		};
		if (GUI.Button(buttons[0], item.done ? "○" : "✓", item.done ? _buttonSelected : _button))
		{
			item.done = !item.done;
			MemoStore.Save();
			ResetState(item);
		}
		if (GUI.Button(buttons[1], item.muted ? "静" : "响", item.muted ? _buttonSelected : _button))
		{
			item.muted = !item.muted;
			MemoStore.Save();
		}
		if (GUI.Button(buttons[2], "改", _button))
		{
			StartEdit(item);
		}
		if (GUI.Button(buttons[3], _confirmDelete == item ? "?" : "×", _confirmDelete == item ? _buttonSelected : _button))
		{
			if (_confirmDelete == item)
			{
				Delete(item);
			}
			else
			{
				_confirmDelete = item;
			}
		}

		string current = target == null ? "找不到数据" : "当前 <b>" + target.Now + "</b>" + (string.IsNullOrEmpty(target.Unit) ? "" : " " + target.Unit);
		GUI.Label(new Rect(row.x, row.y + 28f * _scale, row.width, 18f * _scale),
			TextConverter.ForDisplay(ConditionText(item) + "　·　" + current), _dim);
		float deadlineY = 48f * _scale;
		if (target != null && target.Max() > 0)
		{
			DrawBar(new Rect(row.x, row.y + 48f * _scale, row.width - 8f * _scale, 6f * _scale),
				Mathf.Clamp01((float)target.Now / target.Max()), met);
			deadlineY = 58f * _scale;
		}

		string deadline = item.deadline < 0 ? "不限时" : StoryAtlas.DateText(item.deadline);
		if (item.deadline >= 0 && now >= 0)
		{
			int left = item.deadline - now;
			deadline += left < 0 ? "　<color=" + Red + ">已过期 " + (-left) + " 旬</color>"
				: left <= ConfigManager.MemoAhead.Value ? "　<color=" + Gold + ">剩 " + left + " 旬</color>"
				: "　剩 " + left + " 旬";
		}
		GUI.Label(new Rect(row.x, row.y + deadlineY, row.width, 18f * _scale), TextConverter.ForDisplay(deadline), _dim);
		if (!string.IsNullOrEmpty(item.note))
		{
			GUI.Label(new Rect(row.x, row.y + deadlineY + 20f * _scale, row.width, 20f * _scale), TextConverter.ForDisplay(item.note), _dim);
		}
	}

	private void DrawBar(Rect rect, float ratio, bool met)
	{
		GUI.DrawTexture(rect, Solid(new Color(0.17f, 0.15f, 0.13f, 0.95f)));
		GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width * ratio, rect.height),
			Solid(met ? new Color(0.64f, 0.72f, 0.42f) : new Color(0.74f, 0.63f, 0.21f)));
	}

	private void DrawEditor()
	{
		Rect area = GUILayoutUtility.GetRect(_rect.width - 4f, _rect.height - TitleBarHeight * _scale - 4f);
		Rect view = new Rect(area.x + 4f * _scale, area.y + 4f * _scale, area.width - 8f * _scale, area.height - 8f * _scale);
		Rect content = new Rect(0f, 0f, view.width - 16f * _scale, EditorHeight());
		_editScroll = GUI.BeginScrollView(view, _editScroll, content);
		Rect r = new Rect(0f, 0f, content.width, 20f * _scale);

		Label(ref r, "标题", 48f * _scale);
		_editTitle = GUI.TextField(new Rect(r.x, r.y - 2f * _scale, r.width, 24f * _scale), _editTitle, 40, _field);
		r.x = 0f;
		r.y += 32f * _scale;
		Label(ref r, "条件搜索", 66f * _scale);
		_editSearch = GUI.TextField(new Rect(r.x, r.y - 2f * _scale, r.width, 24f * _scale), _editSearch, 60, _field);
		r.x = 0f;
		r.y += 34f * _scale;
		DrawSearchResults(ref r);

		Label(ref r, "比较", 42f * _scale);
		string[] ops = { "≥", "≤", "＝" };
		for (int i = 0; i < ops.Length; i++)
		{
			Rect b = new Rect(r.x + i * 46f * _scale, r.y, 40f * _scale, 22f * _scale);
			if (GUI.Button(b, ops[i], (int)_editOp == i ? _buttonSelected : _button))
			{
				_editOp = (MemoOp)i;
			}
		}
		r.x = 150f * _scale;
		Label(ref r, "目标值", 48f * _scale);
		_editValue = GUI.TextField(new Rect(r.x, r.y - 1f * _scale, 82f * _scale, 22f * _scale), _editValue, 9, _field);
		r.x = 0f;
		r.y += 34f * _scale;

		Label(ref r, "备注", 36f * _scale);
		_editNote = GUI.TextArea(new Rect(r.x, r.y - 2f * _scale, r.width, 46f * _scale), _editNote, 120, _dim);
		r.x = 0f;
		r.y += 54f * _scale;

		Label(ref r, "截止时间", 58f * _scale);
		if (GUI.Button(new Rect(r.x, r.y, 60f * _scale, 22f * _scale), _editDeadline ? "有限时" : "不限时",
			_editDeadline ? _buttonSelected : _button))
		{
			_editDeadline = !_editDeadline;
		}
		r.x = 0f;
		r.y += 32f * _scale;
		if (_editDeadline)
		{
			DrawDateSteppers(ref r);
		}
		else
		{
			GUI.Label(new Rect(r.x, r.y, r.width, 18f * _scale), TextConverter.ForDisplay("不限时，只盯条件。"), _dim);
			r.y += 24f * _scale;
		}

		Rect save = new Rect(r.x, r.y, 88f * _scale, 26f * _scale);
		if (GUI.Button(save, "保存", _primary) && SaveEdit())
		{
			GUIUtility.keyboardControl = 0;
		}
		Rect cancel = new Rect(r.x + 96f * _scale, r.y, 88f * _scale, 26f * _scale);
		if (GUI.Button(cancel, "取消", _button))
		{
			_editing = null;
			GUIUtility.keyboardControl = 0;
		}
		GUI.EndScrollView();
	}

	private float EditorHeight()
	{
		float baseHeight = 386f * _scale;
		if (_editDeadline)
		{
			baseHeight += 96f * _scale;
		}
		baseHeight += 26f * _scale * Mathf.Max(0, SearchResults().Count);
		return Mathf.Max(baseHeight, 220f * _scale);
	}

	private void Label(ref Rect r, string text, float width)
	{
		GUI.Label(new Rect(r.x, r.y, width, 20f * _scale), TextConverter.ForDisplay(text), _dim);
		r.x += width + 8f * _scale;
	}

	private List<MemoTarget> SearchResults()
	{
		string query = (_editSearch ?? "").Trim();
		return MemoTargets.Catalog()
			.Where(t => string.IsNullOrEmpty(query)
				|| t.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0
				|| t.Group.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0
				|| t.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
			.Take(60)
			.ToList();
	}

	private void DrawSearchResults(ref Rect r)
	{
		List<MemoTarget> results = SearchResults();
		if (results.Count == 0)
		{
			GUI.Label(new Rect(r.x, r.y, r.width, 20f * _scale),
				TextConverter.ForDisplay(StoryNodes.HasCurrentSave ? "没搜到，换个词试试" : "先进游戏读一个存档"), _dim);
			r.x = 0f;
			r.y += 26f * _scale;
			return;
		}
		string group = null;
		foreach (MemoTarget target in results)
		{
			if (target.Group != group)
			{
				group = target.Group;
				GUI.Label(new Rect(r.x, r.y, r.width, 18f * _scale), TextConverter.ForDisplay(group), _dim);
				r.x = 0f;
				r.y += 24f * _scale;
			}
			Rect row = new Rect(r.x, r.y, r.width, 20f * _scale);
			string text = target.Name + "　" + (target.Hint?.Invoke() ?? "");
			if (GUI.Button(row, TextConverter.ForDisplay(text), target == _editTarget ? _buttonSelected : _buttonLeft))
			{
				_editTarget = target;
				_editSearch = target.Name;
				if (target.Ceiling > 0 && (!int.TryParse(_editValue, out int value) || value > target.Ceiling))
				{
					_editValue = target.Ceiling.ToString();
				}
			}
			r.x = 0f;
			r.y += 24f * _scale;
		}
		r.x = 0f;
		r.y += 8f * _scale;
	}

	private void DrawDateSteppers(ref Rect r)
	{
		Stepper(ref r, "年", 16f * _scale, ref _editYear, 1, 99, 38f * _scale);
		Stepper(ref r, "月", 16f * _scale, ref _editMonth, 1, 12, 32f * _scale);
		Label(ref r, "旬", 16f * _scale);
		string[] stages = { "上", "中", "下" };
		for (int i = 0; i < stages.Length; i++)
		{
			Rect b = new Rect(r.x + i * 32f * _scale, r.y, 28f * _scale, 22f * _scale);
			if (GUI.Button(b, stages[i], _editStage == i + 1 ? _buttonSelected : _button))
			{
				_editStage = i + 1;
			}
		}
		r.x = 0f;
		r.y += 30f * _scale;
		int now = CurrentRounds();
		foreach (int months in new[] { 1, 3, 6 })
		{
			Rect b = new Rect(r.x, r.y, 92f * _scale, 22f * _scale);
			GUI.enabled = now >= 0;
			if (GUI.Button(b, "+" + months + "个月", _button))
			{
				SetDate(now + months * 3);
			}
			GUI.enabled = true;
			r.x += 100f * _scale;
		}
		r.x = 0f;
		r.y += 28f * _scale;
		int rounds = ToRounds(_editYear, _editMonth, _editStage);
		string left = now >= 0 ? "　剩 " + (rounds - now) + " 旬" : "";
		GUI.Label(new Rect(r.x, r.y, r.width, 18f * _scale), TextConverter.ForDisplay("截止：" + StoryAtlas.DateText(rounds) + left), _dim);
		r.x = 0f;
		r.y += 24f * _scale;
	}

	private void Stepper(ref Rect r, string label, float labelWidth, ref int value, int min, int max, float width)
	{
		Label(ref r, label, labelWidth);
		Rect valueRect = new Rect(r.x, r.y, width, 22f * _scale);
		if (GUI.Button(valueRect, value.ToString(), _button))
		{
			value = value >= max ? min : value + 1;
		}
		Rect down = new Rect(r.x + width + 2f * _scale, r.y, 22f * _scale, 22f * _scale);
		if (GUI.Button(down, "－", _button))
		{
			value = value <= min ? max : value - 1;
		}
		r.x += width + 34f * _scale;
	}

	private void StartNew()
	{
		int now = CurrentRounds();
		_editing = new MemoItem();
		_isNew = true;
		_editTitle = "";
		_editSearch = "";
		_editValue = "1";
		_editNote = "";
		_editTarget = null;
		_editOp = MemoOp.AtLeast;
		_editDeadline = now >= 0;
		FromRounds(now >= 0 ? now + 6 : 0);
		_confirmDelete = null;
		_editScroll = Vector2.zero;
	}

	private void StartEdit(MemoItem item)
	{
		if (item == null)
		{
			return;
		}
		_editing = item;
		_isNew = false;
		_editTitle = item.title ?? "";
		_editValue = item.value.ToString();
		_editNote = item.note ?? "";
		_editTarget = MemoTargets.Find(item.kind, item.key);
		_editSearch = _editTarget?.Name ?? "";
		_editOp = item.op;
		_editDeadline = item.deadline >= 0;
		int now = CurrentRounds();
		FromRounds(item.deadline >= 0 ? item.deadline : now >= 0 ? now + 6 : 0);
		_confirmDelete = null;
		_editScroll = Vector2.zero;
	}

	private bool SaveEdit()
	{
		if (_editing == null)
		{
			return false;
		}
		if (_editTarget != null)
		{
			_editing.kind = _editTarget.Kind;
			_editing.key = _editTarget.Key;
		}
		if (string.IsNullOrEmpty(_editing.key))
		{
			Notice("请先搜索并选择一个条件。", Red);
			return false;
		}
		if (!int.TryParse((_editValue ?? "").Trim(), out int value))
		{
			Notice("目标值要是一个整数。", Red);
			return false;
		}
		_editing.value = value;
		_editing.op = _editOp;
		_editing.title = TextConverter.ToSimplified((_editTitle ?? "").Trim());
		_editing.note = TextConverter.ToSimplified((_editNote ?? "").Trim());
		_editing.deadline = _editDeadline ? ToRounds(_editYear, _editMonth, _editStage) : -1;
		if (_isNew)
		{
			MemoStore.Items.Add(_editing);
		}
		MemoStore.Save();
		MemoTargets.Invalidate();
		ResetState(_editing);
		_confirmDelete = null;
		Notice("已保存：", Title(_editing, MemoTargets.Find(_editing.kind, _editing.key)), Green);
		_editing = null;
		return true;
	}

	private void Delete(MemoItem item)
	{
		if (item == null)
		{
			return;
		}
		string title = Title(item, MemoTargets.Find(item.kind, item.key));
		MemoStore.Items.Remove(item);
		MemoStore.Save();
		ResetState(item);
		_confirmDelete = null;
		if (_editing == item)
		{
			_editing = null;
		}
		Notice("已删除：", title, Grey);
	}

	private void ResetState(MemoItem item)
	{
		if (item == null || string.IsNullOrEmpty(item.id))
		{
			return;
		}
		_metState.Remove(item.id);
		_nearState.Remove(item.id);
	}

	private static bool Met(MemoItem item, MemoTarget target)
	{
		if (item == null || target == null)
		{
			return false;
		}
		switch (item.op)
		{
			case MemoOp.AtMost: return target.Now <= item.value;
			case MemoOp.Equal: return target.Now == item.value;
			default: return target.Now >= item.value;
		}
	}

	private static string ConditionText(MemoItem item)
	{
		MemoTarget target = MemoTargets.Find(item.kind, item.key);
		string name = target?.Name ?? "数据不在当前存档";
		string unit = target?.Unit ?? "";
		string op = item.op switch
		{
			MemoOp.AtMost => "≤",
			MemoOp.Equal => "＝",
			_ => "≥",
		};
		return name + " " + op + " " + item.value + unit;
	}

	private static string Title(MemoItem item, MemoTarget target)
	{
		if (!string.IsNullOrEmpty(item?.title))
		{
			return item.title;
		}
		return target?.Name ?? "备忘录";
	}

	private void Notice(string text, string color)
	{
		_notice = "<color=" + color + ">" + text + "</color>";
		_noticeUntil = Time.unscaledTime + 8f;
	}

	private void Notice(string prefix, string text, string color)
	{
		Notice(prefix + text, color);
	}

	private static int CurrentRounds()
	{
		GameTime time = PlayerStatManagerData.Instance?.GetFieldValue<GameTimeData>("_gameTimeData")?.GameTime;
		return StoryAtlas.Rounds(time) ?? -1;
	}

	private static int ToRounds(int year, int month, int stage)
	{
		return (Mathf.Max(1, year) - 1) * 36 + (Mathf.Clamp(month, 1, 12) - 1) * 3 + Mathf.Clamp(stage, 1, 3) - 1;
	}

	private void SetDate(int rounds)
	{
		FromRounds(rounds);
		_editDeadline = true;
	}

	private void FromRounds(int rounds)
	{
		rounds = Mathf.Max(0, rounds);
		_editYear = rounds / 36 + 1;
		_editMonth = rounds % 36 / 3 + 1;
		_editStage = rounds % 3 + 1;
	}

	private void DrawLine(Rect rect)
	{
		Color previous = GUI.color;
		GUI.color = FrameEdge;
		GUI.DrawTexture(rect, Texture2D.whiteTexture);
		GUI.color = previous;
	}

	private static readonly Dictionary<Color, Texture2D> SolidCache = new Dictionary<Color, Texture2D>();

	private static Texture2D Solid(Color color)
	{
		if (!SolidCache.TryGetValue(color, out Texture2D texture) || texture == null)
		{
			texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
			texture.SetPixel(0, 0, color);
			texture.Apply();
			SolidCache[color] = texture;
		}
		return texture;
	}
}

