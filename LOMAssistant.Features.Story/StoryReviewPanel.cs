using System;
using System.Collections.Generic;
using System.Linq;
using LOMAssistant.Config;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LOMAssistant.Features.Story;

/// <summary>
/// 剧情回顾面板（默认 Tab）：
/// 路线图——主线按剧本里写明的「下一个主线任务」连成线，岔开的路自动分到不同的车道，按游戏日历排开；支线挂在下面。
/// 剧情图——一个任务里的剧情段画成流程图：每段一个方块，选项、掷骰、条件连到下一段。
/// 节点存档、随时存档、收集进度是列表。
/// </summary>
public partial class StoryReviewPanel : MonoBehaviour
{
	private const int WindowId = 584135;
	private static readonly string[] Tabs = { "此刻", "路线图", "剧情图", "节点存档", "随时存档", "收集进度", "数值", "AI陪玩", "设置" };

	private bool _visible;
	private int _tab;
	private Rect _windowRect;
	private float _scale = -1f;
	private float _opacity = -1f;
	private int _fontMode = -1;
	private string _message = "";
	private float _messageUntil;
	private bool _messageRed; // 提示用朱砂色小字（手柄操作说明）
	private GUIStyle _redNotice;
	private float _redNoticeScale;
	private string _tooltip = "";

	// 配色：宣纸底、墨色字、朱砂点睛，和游戏的水墨画风搭；深色模式是夜里的墨色底、宣纸色字（见 PanelTheme）
	private static Color Paper => PanelTheme.Paper;
	private static Color PaperDeep => PanelTheme.PaperDeep;
	private static Color PaperLight => PanelTheme.PaperLight;
	private static Color Ink => PanelTheme.Ink;
	private static Color InkMuted => PanelTheme.InkMuted;
	private static Color InkLine => PanelTheme.InkLine;
	private static Color Edge => PanelTheme.Edge;
	private static Color Cinnabar => PanelTheme.Cinnabar;
	private static Color Jade => PanelTheme.Jade;
	private static Color OnCinnabar => PanelTheme.OnCinnabar;
	private bool _dark;

	private GUIStyle _window, _label, _rich, _small, _muted, _heading, _tabOff, _tabOn, _button, _primary, _field, _boxTitle, _boxSmall, _tag, _center, _card, _seal;

	internal static StoryReviewPanel Instance { get; private set; }

	private int _escFrame = -10;

	/// <summary>窗口开着（或刚被 Esc、右键关掉的这一两帧）：Esc 和右键归窗口用，不交给游戏。</summary>
	public static bool OwnsEscape
	{
		get
		{
			StoryReviewPanel panel = Instance;
			return panel != null && ((panel._visible && !UiHider.Active) || Time.frameCount - panel._escFrame <= 1);
		}
	}

	private string _prewarmScene = "";
	private float _prewarmSince;

	/// <summary>
	/// 进了养成地图、安顿下来几秒后，在后台把全部剧本读好：第一次打开路线图、剧情图、结局速查时不用再等。
	/// 只在养成地图上做（载入剧本文件那一下没法拆开，剧情对话里会觉得顿），随时存档恢复时不做。
	/// 读过一次（或失败过）就不再自动开始，失败了在面板上点「重新生成」。
	/// </summary>
	private void Prewarm()
	{
		if (StoryAtlas.Ready || StoryAtlas.Building || StoryAtlas.Status.Length > 0)
		{
			return;
		}
		string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
		if (scene != _prewarmScene)
		{
			_prewarmScene = scene;
			_prewarmSince = Time.realtimeSinceStartup;
			return;
		}
		Mortal.Core.SceneController controller = Mortal.Core.SceneController.Instance;
		if (scene == "Free" && Time.realtimeSinceStartup - _prewarmSince > 5f && !_visible
			&& !LOMAssistant.Features.AnytimeSave.AnytimeSave.Replaying && !LOMAssistant.Features.AnytimeSave.AnytimeSave.Restoring
			&& controller != null && !controller.IsLoading && !controller.IsPrepare && Mortal.Core.PlayerStatManagerData.Instance != null)
		{
			FileLogger.Log("[剧情回顾] 进游戏后在后台提前读取剧本");
			StoryAtlas.Start();
		}
	}

	public static void Ensure(GameObject host)
	{
		Instance = host.GetComponent<StoryReviewPanel>() ?? host.AddComponent<StoryReviewPanel>();
	}

	private void Update()
	{
		Prewarm();
		// 窗口开着时多半是在等剧本读完（读完后台词索引也一样），读快一点
		StoryAtlas.Tick(_visible && !UiHider.Active);
		// 等新热键时面板被关掉或切走了：不再等，免得热键一直失灵
		if (_capturing != null && (!_visible || _tab != SettingsTab || _showChangelog || UiHider.Active))
		{
			StopCapture();
		}
		CaptureMouse();
		// Esc 或鼠标右键（游戏里右键也是返回）：先关确认框，再关窗口（游戏那边由输入隔离挡下，不会同时打开暂停菜单）。
		// 正在等新热键时 Esc 是取消改键、右键是绑定右键，交给设置页
		Keyboard keyboard = Keyboard.current;
		Mouse mouse = Mouse.current;
		Gamepad pad = Gamepad.current;
		// 手柄：B 键和 Esc 一样是返回
		bool back = (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) || (mouse != null && mouse.rightButton.wasPressedThisFrame)
			|| (pad != null && pad.buttonEast.wasPressedThisFrame);
		if (_visible && !UiHider.Active && _capturing == null && back)
		{
			_escFrame = Time.frameCount;
			if (_confirming != null)
			{
				_confirming = null;
			}
			else
			{
				_visible = false;
				GUIUtility.keyboardControl = 0;
			}
			GamepadCursor.Release();
			return;
		}
		// 手柄开关窗口：默认按下左摇杆（L3，游戏没用到这个键），设置页可改
		bool padToggle = pad != null && GamepadCursor.TogglePressed(pad) && !UiHider.Active && _capturing == null;
		if ((GUIUtility.keyboardControl == 0 && InputGuard.HotkeyDown(ConfigManager.StoryReviewKey.Value)) || padToggle)
		{
			_visible = !_visible;
			if (padToggle && _visible)
			{
				Notice(GamepadCursor.Hint, red: true);
			}
			_confirming = null;
			if (_visible)
			{
				_reading.Clear();
				_texts.Clear();
				// 每次打开先看「此刻」，内容重新整理；数值页也重新整理（窗口关着时可能修炼、锻造升了级）
				_tab = 0;
				_showChangelog = false;
				_now = null;
				_statSections = null;
				_statInputs.Clear();
				StoryAtlas.ForgetConditions();
				StoryAtlas.RefreshProgress();
			}
		}
		if (_visible && !UiHider.Active)
		{
			// 手柄 LB / RB 切换各页
			int step = pad != null ? GamepadCursor.ShoulderStep(pad) : 0;
			if (step != 0 && _confirming == null && _capturing == null)
			{
				_tab = _showChangelog ? _tab : (_tab + step + Tabs.Length) % Tabs.Length;
				_showChangelog = false;
			}
			GamepadCursor.Update(_windowRect);
		}
		else
		{
			GamepadCursor.Release();
		}
	}

	private float S(float v) => v * _scale;

	/// <summary>窗口底色和卡片底色的不透明度（设置页可调）；文字、按钮、边框不变，调低也看得清。</summary>
	private static float PanelOpacity => Mathf.Clamp(ConfigManager.PanelOpacity.Value, 0.3f, 1f);

	private void BuildStyles(float scale)
	{
		bool rescale = !Mathf.Approximately(scale, _scale);
		_scale = scale;
		_fontMode = ModFont.Mode;
		_opacity = PanelOpacity;
		_dark = PanelTheme.Dark;
		Color Faded(Color c) => new Color(c.r, c.g, c.b, c.a * _opacity);
		_textures.Clear();
		_oneLine.Clear();
		_framed.Clear();
		GUIStyle Plain(int size, Color color, bool wrap = true)
		{
			var s = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(10, Mathf.RoundToInt(size * scale)), wordWrap = wrap, richText = true };
			ModFont.Apply(s);
			s.normal.textColor = color;
			s.padding = new RectOffset(0, 0, 0, 0);
			return s;
		}
		// 描边按钮：底色加一圈细边，悬停时底色加深
		GUIStyle Outlined(Color fill, Color hover, Color edge, Color text, int size = 18)
		{
			var s = Plain(size, text, false);
			s.alignment = TextAnchor.MiddleCenter;
			s.normal.background = Framed(fill, edge);
			s.hover.background = s.active.background = Framed(hover, edge);
			s.border = new RectOffset(2, 2, 2, 2);
			s.hover.textColor = s.active.textColor = text;
			s.padding = new RectOffset((int)S(10), (int)S(10), (int)S(4), (int)S(4));
			return s;
		}
		_window = new GUIStyle(GUI.skin.window);
		ModFont.Apply(_window);
		_window.normal.background = _window.onNormal.background = Framed(Faded(Paper), Ink);
		_window.border = new RectOffset(2, 2, 2, 2);
		_window.padding = new RectOffset((int)S(16), (int)S(16), (int)S(12), (int)S(12));
		_label = Plain(19, Ink);
		_rich = Plain(18, Ink);
		_small = Plain(16, Ink);
		_muted = Plain(16, InkMuted);
		_heading = Plain(22, Ink);
		_heading.fontStyle = FontStyle.Bold;
		_center = Plain(16, InkMuted, false);
		_center.alignment = TextAnchor.MiddleCenter;
		_tabOff = Plain(20, InkMuted, false);
		_tabOff.alignment = TextAnchor.MiddleCenter;
		_tabOff.hover.textColor = Ink;
		_tabOn = new GUIStyle(_tabOff) { fontStyle = FontStyle.Bold };
		_tabOn.normal.textColor = _tabOn.hover.textColor = Ink;
		_button = Outlined(new Color(0, 0, 0, 0), PaperDeep, Edge, Ink, 17);
		_primary = Outlined(Cinnabar, new Color(0.8f, 0.24f, 0.18f, 1f), Cinnabar, OnCinnabar, 17);
		_field = new GUIStyle(GUI.skin.textField) { fontSize = Mathf.RoundToInt(18 * scale), alignment = TextAnchor.MiddleLeft };
		ModFont.Apply(_field);
		_field.normal.background = _field.hover.background = Framed(PaperLight, Edge);
		// 点中时边框变朱砂红，看得出正在输入
		_field.focused.background = Framed(_dark ? new Color(0.21f, 0.19f, 0.17f, 1f) : new Color(1f, 0.99f, 0.96f, 1f), Cinnabar);
		_field.border = new RectOffset(2, 2, 2, 2);
		_field.normal.textColor = _field.focused.textColor = _field.hover.textColor = Ink;
		_field.padding = new RectOffset((int)S(8), (int)S(8), (int)S(4), (int)S(4));
		_boxTitle = Plain(16, Ink, false);
		_boxTitle.clipping = TextClipping.Clip;
		_boxSmall = Plain(13, InkMuted, false);
		_boxSmall.clipping = TextClipping.Clip;
		_tag = Plain(13, Ink, false);
		_tag.alignment = TextAnchor.MiddleCenter;
		_tag.normal.background = Texture(new Color(PaperLight.r, PaperLight.g, PaperLight.b, 0.92f));
		_tag.padding = new RectOffset((int)S(5), (int)S(5), (int)S(1), (int)S(1));
		_card = new GUIStyle(GUI.skin.box);
		ModFont.Apply(_card);
		_card.normal.background = Framed(Faded(PaperLight), new Color(Edge.r, Edge.g, Edge.b, 0.5f));
		_card.border = new RectOffset(2, 2, 2, 2);
		_card.padding = new RectOffset((int)S(10), (int)S(10), (int)S(8), (int)S(8));
		_card.margin = new RectOffset(0, 0, (int)S(4), (int)S(4));
		_seal = Plain(18, OnCinnabar, false);
		_seal.alignment = TextAnchor.MiddleCenter;
		_seal.fontStyle = FontStyle.Bold;
		// 只调透明度时不动窗口位置（可能被拖过）
		if (rescale)
		{
			_windowRect = new Rect(Mathf.Round(Screen.width * 0.04f), Mathf.Round(Screen.height * 0.05f), Mathf.Round(Screen.width * 0.92f), Mathf.Round(Screen.height * 0.9f));
		}
	}

	private void OnGUI()
	{
		AiTypingGuard();
		if (!_visible || UiHider.Active)
		{
			return;
		}
		float scale = LOMAssistant.PanelTheme.UiScale;
		if (scale <= 0f)
		{
			return;
		}
		if (ModFont.NeedsRebuild(scale, _scale, _fontMode) || !Mathf.Approximately(PanelOpacity, _opacity) || _dark != PanelTheme.Dark)
		{
			BuildStyles(scale);
		}
		// 焦点在搜索框里时，按开关键（默认 Tab）会被输入框拿去切换焦点，Update 里也不会关面板：在这里先接住，直接关掉
		Event e = Event.current;
		if (CaptureKey(e))
		{
			return;
		}
		KeyCode key = ConfigManager.StoryReviewKey.Value;
		if (e.type == EventType.KeyDown && key != KeyCode.None && e.keyCode == key && GUIUtility.keyboardControl != 0)
		{
			GUIUtility.keyboardControl = 0;
			_visible = false;
			_confirming = null;
			e.Use();
			return;
		}
		_windowRect = GUI.Window(WindowId, _windowRect, DrawWindow, "", _window);
		// 拖动时标题栏至少留 160 像素在屏幕里，拖不丢；位置不存，重启回到默认位置
		float keep = S(160);
		_windowRect.x = Mathf.Clamp(_windowRect.x, keep - _windowRect.width, Screen.width - keep);
		_windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Mathf.Max(0f, Screen.height - S(56)));
		InputGuard.Report("StoryReview", _windowRect);
	}

	/// <summary>按钮的宽度：按显示出来的文字（换成界面语言后）量，放得下就用原来的宽度，英语这种放不下的才加宽。</summary>
	private static float TextWidth(string text, GUIStyle style, float min)
	{
		return Mathf.Max(min, style.CalcSize(new GUIContent(TextConverter.ForDisplay(text))).x + 16f);
	}

	private void Notice(string text, bool red = false)
	{
		_message = text;
		_messageRed = red;
		_messageUntil = Time.realtimeSinceStartup + 6f;
	}

	// 画窗口期间，文字里的颜色标签按深浅色换（PanelTheme.MapColors）
	private void DrawWindow(int id)
	{
		PanelTheme.Painting = true;
		try
		{
			DrawWindowContent(id);
		}
		finally
		{
			PanelTheme.Painting = false;
		}
	}

	private string _displayLanguage;

	private void RefreshDisplayLanguage()
	{
		string language = TextConverter.DisplayLanguage;
		if (_displayLanguage == language)
		{
			return;
		}
		_displayLanguage = language;
		// 图里的文字和收集说明已在截短、拼接前翻译；切换语言后要重新生成。
		_flowCache.Clear();
		_flow = null;
		_route = null;
		_now = null;
		_texts.Clear();
	}

	private void DrawWindowContent(int id)
	{
		RefreshDisplayLanguage();
		_tooltip = "";
		float w = _windowRect.width, pad = S(18);
		// 确认框开着时，点击、滚轮、按键先给确认框，剩下的吞掉，下面的界面不响应
		EventType type = Event.current.type;
		bool input = type != EventType.Layout && type != EventType.Repaint;
		if (_confirming != null && input)
		{
			DrawConfirm();
			if (Event.current.type != EventType.Used && Event.current.type != EventType.Ignore)
			{
				Event.current.Use();
			}
		}
		// 第一次打开时的选预设弹窗，和确认框一样先接住输入
		bool chooser = _confirming == null && !ConfigManager.PresetChooserShown.Value;
		if (chooser && input)
		{
			DrawPresetChooser();
			if (Event.current.type != EventType.Used && Event.current.type != EventType.Ignore)
			{
				Event.current.Use();
			}
		}
		// 输入框的光标、选中颜色：皮肤默认是白色，宣纸底上看不见
		Color cursor = GUI.skin.settings.cursorColor, selection = GUI.skin.settings.selectionColor;
		GUI.skin.settings.cursorColor = Ink;
		GUI.skin.settings.selectionColor = new Color(Cinnabar.r, Cinnabar.g, Cinnabar.b, 0.3f);
		// 题头一行：标题、各页、关闭按钮都在同一条带子里竖直居中；每页按字数定宽，字与字之间的空隙一样大
		float top = S(10), bandHeight = S(36);
		GUIStyle title = Middle(_heading);
		float titleWidth = title.CalcSize(new GUIContent(TextConverter.ForDisplay("咖啡伴侣"))).x;
		GUI.Label(new Rect(pad, top, titleWidth, bandHeight), "咖啡伴侣", title);
		float x = pad + titleWidth + S(48);
		var close = new Rect(w - pad - S(36), top, S(36), bandHeight);
		float right = DrawUpdateHint(top, bandHeight, close.x - S(14));
		// 英文等页名长的语言放不下时，收窄每页两边的留白和页与页的间隔，不和右边的版本号叠在一起
		float[] widths = Tabs.Select(t => _tabOn.CalcSize(new GUIContent(TextConverter.ForDisplay(t))).x).ToArray();
		float room = right - S(16) - x, side = S(36), gap = S(8);
		float extra = room - widths.Sum() - (side + gap) * Tabs.Length;
		if (extra < 0f)
		{
			float shrink = Mathf.Min(-extra / Tabs.Length, side + gap - S(14));
			side -= shrink * side / (side + gap);
			gap -= shrink * gap / (side + gap + shrink);
			x = Mathf.Max(pad + titleWidth + S(20), x + Mathf.Min(0f, room - widths.Sum() - (side + gap) * Tabs.Length));
		}
		for (int i = 0; i < Tabs.Length; i++)
		{
			float textWidth = widths[i];
			var tab = new Rect(x, top, textWidth + side, bandHeight);
			bool on = i == _tab && !_showChangelog;
			if (GUI.Button(tab, Tabs[i], on ? _tabOn : _tabOff))
			{
				_tab = i;
				_showChangelog = false;
			}
			if (on)
			{
				Fill(new Rect(tab.center.x - textWidth / 2f - S(4), tab.yMax, textWidth + S(8), S(3)), Cinnabar);
			}
			x = tab.xMax + S(8);
		}
		if (GUI.Button(close, "✕", _tabOff))
		{
			_visible = false;
		}
		if (Time.realtimeSinceStartup < _messageUntil)
		{
			if (_redNotice == null || _redNoticeScale != _scale)
			{
				_redNoticeScale = _scale;
				_redNotice = new GUIStyle(Middle(_muted));
				_redNotice.normal.textColor = Cinnabar;
			}
			GUI.Label(new Rect(x + S(16), top, right - x - S(16), bandHeight), _message, _messageRed ? _redNotice : Middle(_label));
		}
		// 题头下一道墨线
		Fill(new Rect(pad, S(56), w - pad * 2, S(2)), InkLine);
		var body = new Rect(pad, S(68), w - pad * 2, _windowRect.height - S(68) - pad);
		switch (_showChangelog ? -1 : _tab)
		{
			case -1: DrawChangelog(body); break;
			case 0: DrawNow(body); break;
			case 1 when _jumpPlan != null: DrawJump(body); break;
			case 2 when _jumpPlan != null: DrawJump(body); break;
			case 1: DrawRoute(body); break;
			case 2: DrawFlow(body); break;
			case 3: DrawNodes(body); break;
			case AnytimeTab: DrawAnytime(body); break;
			case 5: DrawEndings(body); break;
			case StatsTab: DrawStats(body); break;
			case AiTab: DrawAi(body); break;
			default: DrawSettings(body); break;
		}
		if (_confirming != null && !input)
		{
			DrawConfirm();
		}
		else if (chooser && !input)
		{
			DrawPresetChooser();
		}
		else if (_tooltip.Length > 0)
		{
			DrawTooltip();
		}
		GUI.skin.settings.cursorColor = cursor;
		GUI.skin.settings.selectionColor = selection;
		GUI.DragWindow(new Rect(0, 0, w, S(56)));
	}

	private void Hover(Rect r, string text)
	{
		if (!string.IsNullOrEmpty(text) && r.Contains(Event.current.mousePosition))
		{
			_tooltip = text;
			// 记下悬停的那一项在屏幕上的位置（Hover 可能在滚动区里调用，坐标和窗口不同）
			_tooltipAnchor = new Rect(GUIUtility.GUIToScreenPoint(r.position), r.size);
		}
	}

	private Rect _tooltipAnchor;

	// 提示框固定在悬停那一项的下方、按整像素放，不跟着鼠标走：跟着鼠标的话位置有小数，文字每帧重新对齐像素，看着一闪一闪
	private void DrawTooltip()
	{
		var content = new GUIContent(_tooltip);
		float width = Mathf.Round(Mathf.Min(S(460), _small.CalcSize(content).x + S(20)));
		// 中文按字宽自己折行，句末标点（「）」「，」）不放到行首；韩、日、英文画的时候才逐行翻译，不能先拆行
		string language = TextConverter.DisplayLanguage;
		if (language == "ChineseSimplified" || language == "ChineseTraditional")
		{
			content = new GUIContent(Wrap(_tooltip, _small, width - Mathf.Round(S(10)) * 2 - 2f));
		}
		float height = Mathf.Round(_small.CalcHeight(content, width - S(20)) + S(14));
		Vector2 anchor = GUIUtility.ScreenToGUIPoint(_tooltipAnchor.position);
		float y = anchor.y + _tooltipAnchor.height + S(4);
		if (y + height > _windowRect.height - S(8))
		{
			y = anchor.y - height - S(4);
		}
		var r = new Rect(Mathf.Round(Mathf.Clamp(anchor.x, S(8), _windowRect.width - width - S(8))), Mathf.Round(Mathf.Max(S(8), y)), width, height);
		Fill(r, PaperLight);
		Frame(r, Edge, 1);
		float px = Mathf.Round(S(10)), py = Mathf.Round(S(7));
		GUI.Label(new Rect(r.x + px, r.y + py, r.width - px * 2, r.height - py * 2), content, _small);
	}

	private StoryNode _confirming;

	/// <summary>重玩按钮：点了先弹确认框，确认了才读档。</summary>
	private void ReplayButton(StoryNode node, string text, GUIStyle style, params GUILayoutOption[] options)
	{
		if (GUILayout.Button(text, style, options))
		{
			_confirming = node;
		}
	}

	/// <summary>读档确认框：盖在面板中间，底下压一层淡墨。</summary>
	private void DrawConfirm()
	{
		StoryNode node = _confirming;
		if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
		{
			_confirming = null;
			Event.current.Use();
			return;
		}
		if (Event.current.type == EventType.Repaint)
		{
			Fill(new Rect(0, 0, _windowRect.width, _windowRect.height), new Color(0.1f, 0.08f, 0.05f, 0.35f));
		}
		// 剧情段名长、字体放大或换语言时提示会折成好几行：按显示出来的文字量高度，框跟着长高
		string where = StoryScripts.SegmentName(node.Script) + "　" + $"第{node.Year}年{node.Month}月";
		string text = "读取「" + where + "」？\n当前没有存档的进度会丢失。";
		float width = S(460);
		float textHeight = Mathf.Max(S(70), _label.CalcHeight(new GUIContent(TextConverter.ForDisplay(text)), width - S(48)));
		float height = S(140) + textHeight;
		var box = new Rect((_windowRect.width - width) / 2, (_windowRect.height - height) / 2, width, height);
		if (Event.current.type == EventType.Repaint)
		{
			Fill(box, PaperLight);
			Frame(box, Cinnabar, 2);
		}
		GUI.Label(new Rect(box.x + S(24), box.y + S(18), width - S(48), S(36)), "确认读档", _heading);
		GUI.Label(new Rect(box.x + S(24), box.y + S(60), width - S(48), textHeight), text, _label);
		float bw = S(150), bh = S(40), by = box.yMax - bh - S(20);
		if (GUI.Button(new Rect(box.xMax - S(24) - bw * 2 - S(14), by, bw, bh), "取消", _button))
		{
			_confirming = null;
		}
		if (GUI.Button(new Rect(box.xMax - S(24) - bw, by, bw, bh), "读档", _primary))
		{
			_confirming = null;
			Replay(node);
			GUIUtility.ExitGUI();
		}
	}

	/// <summary>从节点重玩：成功就关掉面板。</summary>
	private void Replay(StoryNode node)
	{
		string error = StoryNodes.Replay(node);
		if (error.Length > 0)
		{
			Notice(error);
			return;
		}
		_visible = false;
	}

	/// <summary>搜索框；返回去掉首尾空格的内容。</summary>
	private string SearchBox(Rect r, ref string value)
	{
		string name = "storyreview.search." + (int)r.x + "." + (int)r.y;
		GUI.SetNextControlName(name);
		Color cursor = GUI.skin.settings.cursorColor;
		GUI.skin.settings.cursorColor = Ink;
		value = GUI.TextField(new Rect(r.x, r.y, r.width - S(44), r.height), value, _field);
		GUI.skin.settings.cursorColor = cursor;
		bool focused = GUI.GetNameOfFocusedControl() == name;
		if (string.IsNullOrEmpty(value) && !focused)
		{
			GUI.Label(new Rect(r.x + S(10), r.y, S(80), r.height), "搜索", _center);
		}
		else if (GUI.Button(new Rect(r.xMax - S(40), r.y, S(40), r.height), "×", _button))
		{
			value = "";
			GUI.FocusControl(null);
		}
		return value.Trim();
	}

	// 内部数据是简体：输入繁体时转成简体再搜一遍；界面是繁体、韩语、日语时，再按界面上显示的字样（翻译后）搜一遍；
	// 再按模糊找法搜一遍：中文拼音首字母、韩语初声、英语单词首字母（见 FuzzySearch）
	private static bool Matches(string query, params string[] texts)
	{
		if (query.Length == 0)
		{
			return true;
		}
		string simplified = TextConverter.ToSimplifiedUnlessJapanese(query);
		bool translated = TextConverter.DisplayLanguage != "ChineseSimplified";
		return texts.Any(t =>
		{
			if (t == null)
			{
				return false;
			}
			string plain = RichText.Strip(t);
			return plain.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
				|| (simplified != query && plain.IndexOf(simplified, StringComparison.OrdinalIgnoreCase) >= 0)
				|| (translated && RichText.Strip(TextConverter.ForDisplay(t)).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
				|| FuzzySearch.Matches(plain, query)
				|| (translated && FuzzySearch.Matches(RichText.Strip(TextConverter.ForDisplay(t)), query));
		});
	}

	// ---------------- 绘图小工具 ----------------

	private readonly Dictionary<Color, Texture2D> _textures = new Dictionary<Color, Texture2D>();

	private Texture2D Texture(Color color)
	{
		if (!_textures.TryGetValue(color, out Texture2D texture) || texture == null)
		{
			texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
			texture.SetPixel(0, 0, color);
			texture.Apply();
			_textures[color] = texture;
		}
		return texture;
	}

	private readonly Dictionary<(Color, Color), Texture2D> _framed = new Dictionary<(Color, Color), Texture2D>();

	/// <summary>带一圈细边的底图（配合 GUIStyle.border = 2 九宫格拉伸）。</summary>
	private Texture2D Framed(Color fill, Color edge)
	{
		if (!_framed.TryGetValue((fill, edge), out Texture2D texture) || texture == null)
		{
			texture = new Texture2D(5, 5) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
			for (int x = 0; x < 5; x++)
			{
				for (int y = 0; y < 5; y++)
				{
					texture.SetPixel(x, y, x == 0 || y == 0 || x == 4 || y == 4 ? edge : fill);
				}
			}
			texture.Apply();
			_framed[(fill, edge)] = texture;
		}
		return texture;
	}

	private void Fill(Rect r, Color color) => GUI.DrawTexture(r, Texture(color));

	private void Frame(Rect r, Color color, float w)
	{
		Fill(new Rect(r.x, r.y, r.width, w), color);
		Fill(new Rect(r.x, r.yMax - w, r.width, w), color);
		Fill(new Rect(r.x, r.y, w, r.height), color);
		Fill(new Rect(r.xMax - w, r.y, w, r.height), color);
	}

	private void Round(Rect r, Color color, float radius, float border = 0)
	{
		GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0, color, border, radius);
	}

	/// <summary>横平竖直的线；dashed 为虚线。</summary>
	private void Segment(Vector2 a, Vector2 b, Color color, float w, bool dashed, bool aligned = false)
	{
		var r = new Rect(Mathf.Min(a.x, b.x) - w / 2, Mathf.Min(a.y, b.y) - w / 2, Mathf.Abs(b.x - a.x) + w, Mathf.Abs(b.y - a.y) + w);
		if (!dashed)
		{
			Fill(r, color);
			return;
		}
		bool across = r.width >= r.height;
		float length = across ? r.width : r.height, dash = S(7), gap = S(5);
		// 合流线被拆分成高亮区间后，虚线仍按同一坐标对齐，不能从每一段重新起笔。
		float offset = aligned ? (across ? r.x : r.y) % (dash + gap) : 0;
		for (float t = -offset; t < length; t += dash + gap)
		{
			float start = Mathf.Max(0, t), end = Mathf.Min(length, t + dash);
			if (end <= start) continue;
			Fill(across ? new Rect(r.x + start, r.y, end - start, r.height) : new Rect(r.x, r.y + start, r.width, end - start), color);
		}
	}

	/// <summary>从 a 到 b 的折线：先横、再竖、再横。</summary>
	private void Elbow(Vector2 a, Vector2 b, Color color, float w, bool dashed, float bendX)
	{
		if (Mathf.Abs(a.y - b.y) < 0.5f)
		{
			Segment(a, b, color, w, dashed);
			return;
		}
		Segment(a, new Vector2(bendX, a.y), color, w, dashed);
		Segment(new Vector2(bendX, a.y), new Vector2(bendX, b.y), color, w, dashed);
		Segment(new Vector2(bendX, b.y), b, color, w, dashed);
	}

	/// <summary>读过多少：一条细进度条，墨色填到已读的比例，读完变青；没有对白不画。</summary>
	private void ReadBar(Rect r, int? read)
	{
		if (read == null)
		{
			return;
		}
		Fill(r, new Color(Edge.r, Edge.g, Edge.b, 0.25f));
		Fill(new Rect(r.x, r.y, r.width * read.Value / 100f, r.height), read.Value >= 100 ? Jade : Ink);
	}

	/// <summary>条件文字里的颜色是给深色底配的，换到宣纸底上调深一些。</summary>
	private static string OnPaper(string text)
	{
		return (text ?? "").Replace("#A3B86C", "#2F6B3A").Replace("#CD594A", "#A3302A").Replace("#BCA136", "#7A5A10");
	}

	/// <summary>界面用的短文字：先翻译再截短。返回值只供显示，不作为节点或分支的标识。</summary>
	private static string Short(string text, int length)
	{
		// 原文截短后可能对不上翻译句式，所以先翻译完整文字。
		text = TextConverter.ForDisplay(RichText.Strip(text ?? "")).Replace("\n", " ");
		return text.Length <= length ? text : text.Substring(0, length) + "…";
	}

	// ---------------- 按住拖动滚动 ----------------

	// 游戏的滚动列表只认滚轮和滚动条；这里让按住拖动也能滚。轻点照常是点按钮，拖出一小段才算拖动
	private string _dragId;
	private Vector2 _dragFrom;
	private bool _dragging;

	private readonly Dictionary<string, int> _padScrollFrame = new Dictionary<string, int>();

	private void DragScroll(string id, Rect area, ref Vector2 scroll)
	{
		Event e = Event.current;
		// 手柄右摇杆：指针在这块区域里时直接改滚动位置，每帧滚一点，连续不跳（一帧只加一次）
		if (e.type == EventType.Layout && area.Contains(e.mousePosition) && (!_padScrollFrame.TryGetValue(id, out int frame) || frame != Time.frameCount))
		{
			_padScrollFrame[id] = Time.frameCount;
			Vector2 delta = GamepadCursor.ScrollDelta();
			scroll.x = Mathf.Max(0, scroll.x + delta.x);
			scroll.y = Mathf.Max(0, scroll.y - delta.y);
		}
		// 最右边一条是滚动条，留给它自己拖
		if (e.type == EventType.MouseDown && e.button == 0 && new Rect(area.x, area.y, area.width - S(24), area.height).Contains(e.mousePosition))
		{
			_dragId = id;
			_dragFrom = e.mousePosition;
			_dragging = false;
		}
		else if (e.type == EventType.MouseDrag && _dragId == id)
		{
			if (!_dragging && (e.mousePosition - _dragFrom).magnitude > S(8))
			{
				_dragging = true;
				GUIUtility.hotControl = 0;
			}
			if (_dragging)
			{
				scroll.y = Mathf.Max(0, scroll.y - e.delta.y);
				e.Use();
			}
		}
		else if (e.rawType == EventType.MouseUp && _dragId == id)
		{
			_dragId = null;
			if (_dragging)
			{
				_dragging = false;
				e.Use();
			}
		}
	}

	// ---------------- 文字缓存 ----------------

	// 条件文字要查表、读当前数值，每帧重算会卡；算一次存着，打开面板或数据变了再重算
	private readonly Dictionary<object, string> _texts = new Dictionary<object, string>();
	private int _textsAtlas = -1, _textsNodes = -1;

	// 「嘴力 +1」「心相 -30」：加的玉绿、减的朱砂（跟着浅色、深色主题），条件里的「需>=40」「=1」不算
	private static readonly System.Text.RegularExpressions.Regex PanelDelta = new System.Text.RegularExpressions.Regex(@"(?<=^|[、；：:\s(（①-⑳])([^\s、；：:()（）<>=①-⑳]+?) ([+-]\d+)(?=$|[、；）)\s])");

	private static string PanelDeltas(string text)
	{
		if (string.IsNullOrEmpty(text) || !ConfigManager.ColorOptionDeltas.Value)
		{
			return text;
		}
		string up = "#" + ColorUtility.ToHtmlStringRGB(Jade), down = "#" + ColorUtility.ToHtmlStringRGB(Cinnabar);
		return PanelDelta.Replace(text, m => "<color=" + (m.Groups[2].Value.StartsWith("-") ? down : up) + ">" + m.Value + "</color>");
	}

	private string Cached(object key, Func<string> make)
	{
		if (_textsAtlas != StoryAtlas.Version || _textsNodes != StoryNodes.Version)
		{
			_texts.Clear();
			_textsAtlas = StoryAtlas.Version;
			_textsNodes = StoryNodes.Version;
		}
		if (!_texts.TryGetValue(key, out string text))
		{
			_texts[key] = text = make();
		}
		return text;
	}

	// ---------------- 已读标记 ----------------

	private readonly Dictionary<string, string> _reading = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	/// <summary>一个去处读过没有：看接下去那个剧本的对白（全部周目）。缓存原文，返回时按界面语言翻译。</summary>
	private string ReadMark(StoryExit exit)
	{
		if (exit.Target.Length == 0)
		{
			return "";
		}
		if (_reading.TryGetValue(exit.Target, out string mark))
		{
			return TextConverter.ForDisplay(mark);
		}
		StoryScript next = StoryScripts.Get(exit.Target);
		int? read = next != null ? StoryScripts.ReadPercent(next.Lines) : null;
		mark = read == null || read == 100 ? "" : read == 0 ? "〔未读〕" : "〔读过 " + read + "%〕";
		_reading[exit.Target] = mark;
		return TextConverter.ForDisplay(mark);
	}

	/// <summary>路线图、剧情图、结局都要先把全部剧本读一遍。</summary>
	private bool EnsureAtlas(Rect r)
	{
		if (StoryAtlas.Ready)
		{
			return true;
		}
		if (!StoryAtlas.Building && StoryAtlas.Status.Length == 0)
		{
			StoryAtlas.Start();
		}
		GUI.Label(new Rect(r.x, r.y + S(20), r.width, S(30)), StoryAtlas.Status, _label);
		if (!StoryAtlas.Building && GUI.Button(new Rect(r.x, r.y + S(60), S(140), S(36)), "重新生成", _button))
		{
			StoryAtlas.Start();
		}
		return false;
	}

	// ---------------- 节点存档 ----------------

	private Vector2 _nodesScroll;
	private string _nodesSearch = "";
	private bool _nodesAll;

	private void OnApplicationQuit() => StoryNodes.Flush();

	private void DrawNodes(Rect r)
	{
		string query = SearchBox(new Rect(r.x, r.y, Mathf.Min(S(520), r.width), S(36)), ref _nodesSearch);
		if (StoryNodes.HasCurrentSave)
		{
			var all = new Rect(r.x + Mathf.Min(S(520), r.width) + S(16), r.y, S(200), S(36));
			if (GUI.Button(all, _nodesAll ? "全部存档的节点" : "当前存档的节点", _button))
			{
				_nodesAll = !_nodesAll;
			}
		}
		var area = new Rect(r.x, r.y + S(46), r.width, r.height - S(46));
		DragScroll("nodes", area, ref _nodesScroll);
		GUILayout.BeginArea(area);
		_nodesScroll = GUILayout.BeginScrollView(_nodesScroll);
		var visible = new HashSet<StoryNode>(StoryNodes.Visible);
		var nodes = (_nodesAll ? StoryNodes.All : visible).OrderByDescending(n => n.Year).ThenByDescending(n => n.Month).ThenByDescending(n => n.Stage).ThenByDescending(n => n.Saved).ToList();
		if (nodes.Count == 0)
		{
			GUILayout.Label("还没有收录", _muted);
		}
		string month = "";
		foreach (StoryNode node in nodes)
		{
			StoryScript script = StoryScripts.Get(node.Script);
			string options = script == null ? "" : string.Join("　", script.Menus.SelectMany(menu => menu).Select(step =>
				"「" + StoryScripts.OptionText(step.Key) + "」" + string.Concat(script.Exits.Where(e => e.Path.Count > 0 && e.Path[0].Key == step.Key).Select(ReadMark).Distinct().ToArray())).ToArray());
			if (!Matches(query, node.Script, node.Mission, options))
			{
				continue;
			}
			string head = $"第{node.Year}年{node.Month}月";
			if (head != month)
			{
				month = head;
				GUILayout.Space(S(8));
				GUILayout.Label(head, _heading);
			}
			GUILayout.BeginHorizontal(_card);
			GUILayout.BeginVertical();
			GUILayout.Label($"{StoryAtlas.StageText(node.Stage)}　{(node.Mission.Length > 0 ? node.Mission : node.Script)}" + (_nodesAll && !visible.Contains(node) ? "　<color=#73644F>（其他存档）</color>" : ""), _label);
			GUILayout.Label(options.Length > 0 ? options : node.Script, _muted);
			GUILayout.EndVertical();
			ReplayButton(node, "从这里重玩", _primary, GUILayout.Width(S(170)), GUILayout.Height(S(40)));
			if (GUILayout.Button("删除", _button, GUILayout.Width(S(70)), GUILayout.Height(S(40))))
			{
				StoryNodes.Delete(node);
				GUIUtility.ExitGUI();
			}
			GUILayout.EndHorizontal();
		}
		GUILayout.EndScrollView();
		GUILayout.EndArea();
	}

	// ---------------- 收集进度（结局、死亡、成就、CG、剧情） ----------------

	/// <summary>一个结局的「怎么走到」，每处一行，最多四处。</summary>
	internal static string EndingWays(StoryEnding ending, bool onPaper = true)
	{
		string muted = onPaper ? "#73644F" : "#9A9286";
		if (ending.Ways.Count == 0)
		{
			return $"<color={muted}>在剧本和决斗数据里都没找到触发的地方（可能由战役、小游戏等直接触发）</color>";
		}
		var lines = new List<string>();
		foreach (StoryEndingWay way in ending.Ways.Take(4))
		{
			string head = way.Mission != null ? $"在{(way.Mission.Main ? "主线" : "支线")}「{way.Mission.Title}」（{way.Mission.When}）" : "在剧本 " + way.Script;
			var parts = way.Path.Select(step => StoryScripts.Describe(step, TextConverter.ForDisplay)).ToList();
			if (way.LostDuel)
			{
				parts.Add("决斗输了");
			}
			// 标题和每一步说明先各自翻译，再拼起来，免得完整句式对不上。
			string steps = parts.Count > 0 ? "：" + string.Join(" → ", parts.Select(TextConverter.ForDisplay).ToArray()) : TextConverter.ForDisplay("：走到这一段就会触发");
			string line = TextConverter.ForDisplay(head) + steps;
			lines.Add(onPaper ? OnPaper(line) : line);
		}
		if (ending.Ways.Count > 4)
		{
			lines.Add($"<color={muted}>还有 {ending.Ways.Count - 4} 处</color>");
		}
		return string.Join("\n", lines.ToArray());
	}

	private Vector2 _endingsScroll;
	private string _endingsSearch = "";
	private int _endingsSection = StorySection, _endingsFilter;
	// 收集进度：结局、死亡、成就、CG 来自游戏的图鉴，剧情按对白读过多少算，都是全部周目的记录，换存档也不变
	private static readonly string[] EndingSections = { "结局（汗青书）", "死亡（生死簿）", "成就（风云史）", "结局 CG", "剧情" };
	private const int AchievementSection = 2, CgSection = 3, StorySection = 4;
	// 按钮的排列顺序：剧情放最前面
	private static readonly int[] SectionOrder = { StorySection, 0, 1, AchievementSection, CgSection };
	private static readonly string[] EndingFilters = { "全部", "已达成", "未达成" };

	/// <summary>和游戏图鉴一样的编号前缀：汗青书「结局 N　」（按游戏语言），生死簿「N. 」；成就、CG 和图鉴里没有的不加。</summary>
	internal static string EndingNumber(StoryEnding ending)
	{
		if (ending.Number <= 0 || ending.Item == null || StoryAtlas.Achievements.Contains(ending) || StoryAtlas.Cgs.Contains(ending))
		{
			return "";
		}
		if (ending.Death)
		{
			return ending.Number + ". ";
		}
		string format = Mortal.Core.LocalizationManager.Instance?.LocaleResolver?.GetString("System/EndText");
		return (string.IsNullOrEmpty(format) || !format.Contains("{0}") ? TextConverter.ForDisplay("结局") + ending.Number : string.Format(format, ending.Number)) + "　";
	}

	private static bool _endingsByTime; // false：按游戏图鉴的编号；true：按剧情里最早能触发的时间

	private static IEnumerable<StoryEnding> EndingSection(int section)
	{
		if (section == AchievementSection)
		{
			return StoryAtlas.Achievements;
		}
		IEnumerable<StoryEnding> list = section == CgSection ? StoryAtlas.Cgs : StoryAtlas.Endings.Where(e => e.Death == (section == 1));
		// 图鉴里没有的（编号 0）排在最后
		return _endingsByTime
			? list.OrderBy(e => e.Ways.Select(w => w.Mission?.From ?? int.MaxValue).DefaultIfEmpty(int.MaxValue).Min()).ThenBy(e => e.Id, StringComparer.Ordinal)
			: list.OrderBy(e => e.Number > 0 ? e.Number : int.MaxValue).ThenBy(e => e.Id, StringComparer.Ordinal);
	}

	/// <summary>剧情：有对白的任务，读完（全部周目读过的对白占 100%）算达成；按触发时间排。</summary>
	private static IEnumerable<StoryMission> StoryMissions()
	{
		return StoryAtlas.Missions.Where(m => m.Read != null)
			.OrderBy(m => m.Main ? 0 : 1).ThenBy(m => m.From ?? int.MaxValue).ThenBy(m => m.Key, StringComparer.Ordinal);
	}

	private static bool StoryDone(StoryMission m) => m.Read == 100;

	private void DrawEndings(Rect r)
	{
		if (!EnsureAtlas(r))
		{
			return;
		}
		float searchWidth = Mathf.Min(S(420), r.width);
		string query = SearchBox(new Rect(r.x, r.y, searchWidth, S(36)), ref _endingsSearch);
		// 搜索框右边：全部 / 已达成 / 未达成
		float fx = r.x + searchWidth + S(16);
		for (int i = 0; i < EndingFilters.Length; i++)
		{
			if (GUI.Button(new Rect(fx, r.y + S(2), S(96), S(32)), EndingFilters[i], i == _endingsFilter ? _primary : _button) && _endingsFilter != i)
			{
				_endingsFilter = i;
				_endingsScroll = Vector2.zero;
			}
			fx += S(102);
		}
		// 再往右：排序方式（成就、剧情没有编号，不显示）
		if (_endingsSection != AchievementSection && _endingsSection != StorySection)
		{
			fx += S(14);
			string[] sorts = { "按编号", "按时间" };
			for (int i = 0; i < sorts.Length; i++)
			{
				bool on = _endingsByTime == (i == 1);
				if (GUI.Button(new Rect(fx, r.y + S(2), S(96), S(32)), sorts[i], on ? _primary : _button) && !on)
				{
					_endingsByTime = i == 1;
					_endingsScroll = Vector2.zero;
				}
				fx += S(102);
			}
		}
		// 下面一排是各类，点哪类看哪类；搜索时各类的结果都列出来
		float gx = r.x, gy = r.y + S(44);
		foreach (int i in SectionOrder)
		{
			string title = i == StorySection
				? $"{EndingSections[i]}  {StoryMissions().Count(StoryDone)}/{StoryMissions().Count()}"
				: $"{EndingSections[i]}  {EndingSection(i).Count(e => e.Achieved)}/{EndingSection(i).Count()}";
			float width = _tabOn.CalcSize(new GUIContent(title)).x * 0.85f + S(24);
			bool on = i == _endingsSection && query.Length == 0;
			if (GUI.Button(new Rect(gx, gy, width, S(32)), title, on ? _primary : _button) && (_endingsSection != i || query.Length > 0))
			{
				_endingsSection = i;
				_endingsSearch = "";
				_endingsScroll = Vector2.zero;
			}
			gx += width + S(6);
		}
		GUI.Label(new Rect(gx + S(10), gy, Mathf.Max(0f, r.xMax - gx - S(10)), S(32)), "全部周目的记录，换存档也不变", Middle(_muted));
		var endingsArea = new Rect(r.x, r.y + S(86), r.width, r.height - S(86));
		DragScroll("endings", endingsArea, ref _endingsScroll);
		GUILayout.BeginArea(endingsArea);
		_endingsScroll = GUILayout.BeginScrollView(_endingsScroll);
		int shownTotal = 0;
		foreach (int section in SectionOrder)
		{
			if (query.Length == 0 && section != _endingsSection)
			{
				continue;
			}
			if (section == StorySection)
			{
				shownTotal += DrawStorySection(query);
				continue;
			}
			var shown = EndingSection(section).Where(e => _endingsFilter == 0 || e.Achieved == (_endingsFilter == 1)).Where(e =>
			{
				string where = string.Join("、", e.Ways.Select(w => w.Mission).Where(m => m != null).Select(m => (m.Main ? "主线" : m.SeriesTitle) + "「" + m.Title + "」").Distinct().ToArray());
				return Matches(query, EndingNumber(e) + e.Title, e.Desc, e.Id, e.Award, where);
			}).ToList();
			if (shown.Count == 0)
			{
				continue;
			}
			shownTotal += shown.Count;
			if (query.Length > 0)
			{
				GUILayout.Space(S(8));
				GUILayout.Label(EndingSections[section], _heading);
			}
			if (section == CgSection)
			{
				DrawCreditsButton();
			}
			foreach (StoryEnding ending in shown)
			{
				GUILayout.BeginVertical(_card);
				GUILayout.BeginHorizontal();
				GUILayout.Label(ending.Achieved ? "<color=#2F6B3A><b>✓ 已达成</b></color>" : "<color=#A3302A>未达成</color>", _small, GUILayout.Width(S(80)));
				GUILayout.Label("<b>" + EndingNumber(ending) + ending.Title + "</b>", _label);
				GUILayout.EndHorizontal();
				if (section == AchievementSection)
				{
					// 成就的说明就是达成条件；奖励在新开存档时生效
					if (ending.Desc.Length > 0)
					{
						GUILayout.Label(OnPaper(ending.Desc), _rich);
					}
					if (ending.Award.Length > 0)
					{
						GUILayout.Label("<color=#73644F>奖励：" + ending.Award + "</color>", _rich);
					}
					if (ending.Ways.Count > 0)
					{
						GUILayout.Label(Cached(ending, () => EndingWays(ending)), _rich);
					}
				}
				else
				{
					GUILayout.Label(Cached(ending, () => EndingWays(ending)), _rich);
				}
				DrawWayJumps(ending);
				GUILayout.EndVertical();
			}
		}
		if (shownTotal == 0)
		{
			GUILayout.Space(S(12));
			GUILayout.Label(query.Length > 0 ? "没有搜到相关条目" : _endingsFilter == 1 ? "这一类还没有达成的条目" : "这一类已经全部达成", _muted);
		}
		GUILayout.EndScrollView();
		GUILayout.EndArea();
	}

	/// <summary>走到这一条的任务：每个一颗按钮，点了去剧情图（不用记任务编号再去搜）。</summary>
	private void DrawWayJumps(StoryEnding ending)
	{
		var missions = ending.Ways.Select(w => w.Mission).Where(m => m != null).Distinct().Take(4).ToList();
		if (missions.Count == 0)
		{
			return;
		}
		GUILayout.BeginHorizontal();
		foreach (StoryMission m in missions)
		{
			string text = (m.Main ? "主线" : "支线") + "「" + Short(m.Title, 12) + "」";
			if (GUILayout.Button(text, _button, GUILayout.Width(TextWidth(text, _button, S(120)))))
			{
				OpenFlow(m);
			}
			Hover(GUILayoutUtility.GetLastRect(), "看剧情图：" + TextConverter.ForDisplay(m.Title) + (m.When.Length > 0 ? "（" + TextConverter.ForDisplay(m.When) + "）" : ""));
		}
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
	}

	/// <summary>剧情：每个任务读过多少对白、什么时候在哪触发，可以去剧情图看；返回列出了几个。</summary>
	private int DrawStorySection(string query)
	{
		var shown = StoryMissions().Where(m => _endingsFilter == 0 || StoryDone(m) == (_endingsFilter == 1))
			.Where(m => Matches(query, m.Title, m.SeriesTitle, m.Place, m.When, m.Key)).ToList();
		if (shown.Count == 0)
		{
			return 0;
		}
		if (query.Length > 0)
		{
			GUILayout.Space(S(8));
			GUILayout.Label(EndingSections[StorySection], _heading);
		}
		foreach (StoryMission m in shown)
		{
			GUILayout.BeginVertical(_card);
			GUILayout.BeginHorizontal();
			GUILayout.Label(StoryDone(m) ? "<color=#2F6B3A><b>✓ 读完</b></color>" : "<color=#A3302A>" + string.Format("读过 {0}%", m.Read) + "</color>", _small, GUILayout.Width(S(80)));
			GUILayout.Label("<b>" + m.Title + "</b>", _label);
			GUILayout.FlexibleSpace();
			if (GUILayout.Button("看剧情图", _button, GUILayout.Width(TextWidth("看剧情图", _button, S(130)))))
			{
				OpenFlow(m);
			}
			GUILayout.EndHorizontal();
			string where = (m.Main ? "主线" : m.SeriesTitle) + (m.When.Length > 0 ? "　·　" + m.When : "") + (m.Place.Length > 0 ? "　·　" + m.Place : "");
			GUILayout.Label("<color=#73644F>" + where + "</color>", _rich);
			GUILayout.EndVertical();
		}
		return shown.Count;
	}
}
