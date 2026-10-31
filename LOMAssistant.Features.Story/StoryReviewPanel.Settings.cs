using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using LOMAssistant.Config;
using LOMAssistant.Features.Extras;
using Mortal.Pinball;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LOMAssistant.Features.Story;

/// <summary>
/// 「设置」页：配置文件里的选项都在这里改，分几组排开。开关点一下就切换；数字改了点「设定」；热键点一下再按新键。
/// 改了马上写回配置文件。要重启游戏才生效的标出来（按配置说明里有没有写「改后重启生效」判断）。
/// </summary>
public partial class StoryReviewPanel
{
	private sealed class SettingRow
	{
		public IConfigElement Element;
		public string Name;
		public float Min, Max; // 数字的范围
		public string[] Choices; // 几选一的数字（0、1、2…）
		public bool Dropdown; // 选项多时做成下拉框：平时只显示当前选的，点开再列出全部
		public Action<int> Choose; // 选了第几个（不设就直接写配置）
		public float[] Speeds; // 倍速档位

		public bool Restart => Element.Description.Contains("重启生效");
	}

	private const int SettingsTab = 8;

	/// <summary>F8：打开面板并切到「设置」页；已经在这一页就关掉。</summary>
	public static void ToggleSettings()
	{
		StoryReviewPanel panel = Instance;
		if (panel == null)
		{
			return;
		}
		if (panel._visible && panel._tab == SettingsTab && !panel._showChangelog)
		{
			panel._visible = false;
			return;
		}
		OpenSettings();
	}

	/// <summary>打开面板并切到「设置」页（检测到冲突 Mod 时自动调用）。窗口还没建好时返回 false。</summary>
	public static bool OpenSettings()
	{
		StoryReviewPanel panel = Instance;
		if (panel == null)
		{
			return false;
		}
		panel._visible = true;
		StoryAtlas.RefreshProgress();
		panel._tab = SettingsTab;
		panel._showChangelog = false;
		return true;
	}

	// 配置说明里写给改配置文件的人看的部分（true=开，false=关、改后重启生效）在面板里用不着
	private static string Describe(string description)
	{
		string text = Regex.Replace(description, "[。，]?\\s*(true=开[^。]*|改后重启(游戏)?生效)", "");
		text = Regex.Replace(text, "（[，。\\s]*）", "");
		return text.TrimEnd('，', '。', ' ') + "。";
	}

	private List<(string Title, List<SettingRow> Rows)> _settingSections;
	private Vector2 _settingsScroll;
	private string _settingsSearch = "";
	private readonly Dictionary<string, string> _settingInputs = new Dictionary<string, string>();
	private SettingRow _capturing;

	// 现在展开着的下拉框
	private SettingRow _dropdown;

	private static SettingRow Row(IConfigElement element, string name, float min = 0f, float max = 0f)
	{
		return new SettingRow { Element = element, Name = name, Min = min, Max = max };
	}

	private static List<(string, List<SettingRow>)> BuildSettingSections()
	{
		// 每组里先放以前 F8 小面板里就有的，以前只能改配置文件的放后面
		return new List<(string, List<SettingRow>)>
		{
			("剧情", new List<SettingRow>
			{
				Row(ConfigManager.ShowBranchChecks, "显示分支判断"),
				Row(ConfigManager.ShowFlagChanges, "显示旗标变化"),
				Row(ConfigManager.EnableEventPicker, "随机事件自选"),
				Row(ConfigManager.ForceUnmetEvents, "强行触发条件没满足的事件"),
				Row(ConfigManager.EnableBranchAhead, "判定前选分支"),
				Row(ConfigManager.BranchAheadAutoSnapshot, "判定前自动存随时存档"),
				Row(ConfigManager.BranchAheadSnapshots, "保留几张「判定前」存档", 1f, 10f),
				Row(ConfigManager.BranchAheadHint, "没可看的就只说一句"),
				Row(ConfigManager.BranchAheadStopSkip, "Ctrl 快进遇到窗口就停"),
				Row(ConfigManager.EnableOneClickUnlock, "锁住的选项一键设置"),
				Row(ConfigManager.ForceUnmetOptions, "强行选条件没满足的选项"),
				Row(ConfigManager.EnableOptionInfo, "选项提示、判定记录"),
				Row(ConfigManager.ShowItemUses, "物品说明里写用途"),
				new SettingRow { Element = ConfigManager.OptionResultMode, Name = "选项后果", Choices = new[] { "总是显示", "鼠标悬停", "不显示" } },
				Row(ConfigManager.ColorOptionDeltas, "选项后果加减上色"),
				Row(ConfigManager.EnableManualDiceSelection, "骰子手动选择"),
				Row(ConfigManager.EnableStoryJump, "剧情跳转"),
				Row(ConfigManager.FontScale, "对白文字大小（%）", 50f, 200f),
				Row(ConfigManager.FlagNoticeSeconds, "旗标提示停留（秒）", 0f, 30f),
				Row(ConfigManager.BranchPanelAutoHide, "分支小窗自动收起（秒）", 0f, 600f),
			}),
			("养成", new List<SettingRow>
			{
				new SettingRow { Element = ConfigManager.ActionMode, Name = "行动点", Choices = new[] { "正常", "不消耗", "每旬N点" }, Choose = ActionModePatch.Set },
				Row(ConfigManager.ActionsPerRound, "每旬N点的 N", 1f, 5f),
				Row(ConfigManager.BreakStatLimit, "突破属性上限"),
				Row(ConfigManager.CheatUnlimitedTraining, "无限修炼"),
				Row(ConfigManager.CheatUnlimitedUpgrade, "无限锻造、炼丹"),
				Row(ConfigManager.CheatFreeUpgradeMoney, "锻造、炼丹不花银两"),
				Row(ConfigManager.CheatDevelopIgnoreStory, "开发装备不看剧情条件"),
				Row(ConfigManager.PinballFixedScore, "弹珠固定总分（-1=关）", -1f, 9999999f),
				Row(ConfigManager.EnableSkipAction, "养成「空过」按钮"),
				Row(ConfigManager.EnableMapHints, "地图建筑事件提示"),
				Row(ConfigManager.MapHintsNowOnly, "地图提示只列能触发的"),
			}),
			("战斗", new List<SettingRow>
			{
				Row(ConfigManager.CheatDuelInvincible, "决斗我方无敌"),
				Row(ConfigManager.CheatDuelNoDamage, "决斗敌方不受伤"),
				Row(ConfigManager.CheatBattleInvincible, "团战我方不受伤"),
				Row(ConfigManager.CheatBattleNoCooldown, "团战绝招无冷却"),
				Row(ConfigManager.EnableCombatAssist, "决斗直接获胜按钮"),
				Row(ConfigManager.EnableBattleWin, "团战直接获胜按钮"),
				Row(ConfigManager.EnableDuelActionForecast, "决斗敌方行动预测"),
				Row(ConfigManager.EnableSkillColumns, "技能表全显示"),
			}),
			("存档", new List<SettingRow>
			{
				Row(ConfigManager.EnableAnytimeSave, "随时存档"),
				Row(ConfigManager.EnableStoryReview, "节点存档"),
				Row(ConfigManager.AnytimeSaveLimit, "随时存档上限（0=不限）", 0f, 999f),
				Row(ConfigManager.SaveSlotCount, "手动存档栏位", 20f, 999f),
				Row(ConfigManager.LegendSlotPages, "传奇栏位页数", 1f, 50f),
				Row(ConfigManager.AutoSaveHistory, "自动存档多留旧档", 0f, 50f),
				Row(ConfigManager.EnableSaveBrowser, "新版存档、读档界面"),
			}),
			("其他", new List<SettingRow>
			{
				new SettingRow { Element = ConfigManager.GameSpeed, Name = "游戏倍速", Speeds = GameSpeed.Options },
				Row(ConfigManager.GameFontScale, "游戏文字大小（%）", 50f, 200f),
				Row(ConfigManager.EnableBackgroundRun, "后台继续运行"),
				Row(ConfigManager.EnableFastStartup, "开游戏更快"),
				Row(ConfigManager.EnableFlowSpeedup, "流程提速"),
				Row(ConfigManager.EnableLibraryDisplay, "讲经堂解锁显示"),
				Row(ConfigManager.EnableShopAllItems, "四师兄卖全部物品"),
				new SettingRow { Element = ConfigManager.TitlePartnerChoice, Name = "标题画面人物", Choices = LOMAssistant.Features.Extras.TitlePartnerPatch.Names, Dropdown = true },
			}),
			// Mod 自己的设置：窗口的样子、日志、更新检查（界面语言在搜索框右边）
			("咖啡伴侣", new List<SettingRow>
			{
				new SettingRow { Element = ConfigManager.PanelTheme, Name = "窗口配色", Choices = new[] { "浅色", "深色", "跟随系统" } },
				new SettingRow { Element = ConfigManager.PanelFont, Name = "窗口字体", Choices = new[] { "思源宋体", "微软雅黑" } },
				Row(ConfigManager.PanelScale, "窗口文字大小（%）", 50f, 200f),
				// 所有窗口的不透明度放在一起
				Row(ConfigManager.PanelOpacity, "咖啡伴侣窗口不透明度", 0.3f, 1f),
				Row(ConfigManager.DialogOpacity, "小窗不透明度", 0.3f, 1f),
				Row(ConfigManager.PopupOpacity, "提示框不透明度", 0.3f, 1f),
				Row(ConfigManager.EnableUpdateCheck, "检查新版本"),
				Row(ConfigManager.LogToFile, "写独立日志文件"),
			}),
			("热键", new List<SettingRow>
			{
				Row(ConfigManager.StoryReviewKey, "本窗口"),
				new SettingRow { Element = ConfigManager.GamepadPanelButton, Name = "手柄开关本窗口", Choices = new[] { "L3", "R3", "LB+RB" } },
				Row(ConfigManager.DebugPanelKey, "数值页"),
				Row(ConfigManager.PanelKey, "设置页"),
				Row(ConfigManager.SaveNoteKey, "存档备注"),
				Row(ConfigManager.HideUIKey, "隐藏界面看 CG"),
				Row(ConfigManager.SkipReadKey, "一键快进已读对话"),
				Row(ConfigManager.SkipActionKey, "空过一次行动"),
				Row(ConfigManager.AiPanelKey, "AI陪玩页"),
				Row(ConfigManager.AnytimePanelKey, "随时存档页"),
				Row(ConfigManager.AnytimeSaveKey, "随时存档：存新档"),
				Row(ConfigManager.AnytimeLoadKey, "随时存档：读最近的"),
				Row(ConfigManager.AnytimeRollbackKey, "回退到判定前"),
			}),
		};
	}

	/// <summary>等新热键时，OnGUI 一开始先接住按键（比面板开关键先），返回 true 表示这次按键用掉了。</summary>
	private bool CaptureKey(Event e)
	{
		if (_capturing == null)
		{
			return false;
		}
		if (!_visible || _tab != SettingsTab)
		{
			StopCapture();
			return false;
		}
		// 鼠标右键、中键、侧键也能绑（左键要用来点界面，不算）
		KeyCode key = e.type == EventType.KeyDown ? e.keyCode
			: e.type == EventType.MouseDown && e.button >= 1 && e.button <= 4 ? KeyCode.Mouse0 + e.button
			: KeyCode.None;
		if (key == KeyCode.None)
		{
			return false;
		}
		if (key != KeyCode.Escape)
		{
			AssignKey(_capturing, key);
		}
		StopCapture();
		e.Use();
		return true;
	}

	// 等新热键时按了鼠标右键、中键、侧键：直接读鼠标（这几个键不一定会作为界面事件送到窗口里）
	private void CaptureMouse()
	{
		Mouse mouse = Mouse.current;
		if (_capturing == null || mouse == null)
		{
			return;
		}
		KeyCode key = mouse.rightButton.wasPressedThisFrame ? KeyCode.Mouse1
			: mouse.middleButton.wasPressedThisFrame ? KeyCode.Mouse2
			: mouse.backButton.wasPressedThisFrame ? KeyCode.Mouse3
			: mouse.forwardButton.wasPressedThisFrame ? KeyCode.Mouse4
			: KeyCode.None;
		if (key != KeyCode.None)
		{
			AssignKey(_capturing, key);
			StopCapture();
		}
	}

	// 设置页热键和存档备注默认同一个键，在不同界面起作用，不算冲突
	private static bool SharedOnPurpose(IConfigElement a, IConfigElement b)
	{
		return (a == ConfigManager.PanelKey && b == ConfigManager.SaveNoteKey) || (a == ConfigManager.SaveNoteKey && b == ConfigManager.PanelKey);
	}

	// 绑新键；这个键已经被别的热键用了，就把两个对调（那个热键换成这一个原来的键），不会出现两个功能抢一个键
	private void AssignKey(SettingRow row, KeyCode key)
	{
		var old = (KeyCode)row.Element.BoxedValue;
		if (key == old)
		{
			return;
		}
		if (key != KeyCode.None)
		{
			foreach (var (_, rows) in _settingSections ?? BuildSettingSections())
			{
				foreach (SettingRow other in rows)
				{
					if (other != row && other.Element.BoxedValue is KeyCode used && used == key && !SharedOnPurpose(row.Element, other.Element))
					{
						other.Element.BoxedValue = old;
						Notice($"{InputGuard.KeyName(key)} 原是「{other.Name}」的键，两个已对调");
					}
				}
			}
		}
		row.Element.BoxedValue = key;
	}

	private void StopCapture()
	{
		_capturing = null;
		InputGuard.CapturingKey = false;
	}

	private void DrawSettings(Rect r)
	{
		_settingSections ??= BuildSettingSections();
		string query = SearchBox(new Rect(r.x, r.y, S(420), S(36)), ref _settingsSearch);
		DrawLanguage(new Rect(r.x + S(450), r.y, r.width - S(450), S(36)));		var area = new Rect(r.x, r.y + S(46), r.width, r.height - S(46));
		DragScroll("settings", area, ref _settingsScroll);
		GUILayout.BeginArea(area);
		_settingsScroll = GUILayout.BeginScrollView(_settingsScroll);
		float rowWidth = S(560);
		int columns = Mathf.Max(1, (int)((area.width - S(30)) / rowWidth));
		foreach (string warning in ConflictDetector.Warnings)
		{
			GUILayout.Label(Red("【注意】" + warning), _rich);
		}
		if (string.IsNullOrEmpty(query))
		{
			DrawPresets();
		}
		DrawPinballScore(rowWidth);
		foreach (var (title, rows) in _settingSections)
		{
			var shown = rows.Where(row => Matches(query, row.Name, TextConverter.ForDisplay(row.Name), row.Element.Name, row.Element.Description)).ToList();
			if (shown.Count == 0)
			{
				continue;
			}
			GUILayout.Space(S(8));
			GUILayout.Label(title, _heading);
			for (int i = 0; i < shown.Count; i += columns)
			{
				GUILayout.BeginHorizontal();
				for (int c = 0; c < columns && i + c < shown.Count; c++)
				{
					DrawSettingRow(shown[i + c], rowWidth);
				}
				GUILayout.FlexibleSpace();
				GUILayout.EndHorizontal();
			}
			if (shown.Any(row => row.Element == ConfigManager.ActionMode) && ActionModePatch.Mode != ActionModePatch.Normal)
			{
				GUILayout.Label(Red((ActionModePatch.Mode == ActionModePatch.Unlimited ? "行动点剩 1 点后不再消耗，用 ESC「空过」结束本旬。" : "")
					+ "行动点不是「正常」时可能影响剧情触发（部分剧情、支线不触发或提前触发），请谨慎使用，切换前先存档。"), _rich);
			}
		}
		GUILayout.EndScrollView();
		GUILayout.EndArea();
	}

	// 各语言用自己的写法，不跟着界面语言翻译
	private static readonly string[] Languages = { "跟随游戏", "简体中文", "繁體中文", "한국어", "日本語", "English" };

	/// <summary>界面语言：搜索框右边一排按钮。</summary>
	private void DrawLanguage(Rect r)
	{
		GUIStyle line = Middle(_label);
		string label = "界面语言";
		float x = r.x, labelWidth = line.CalcSize(new GUIContent(label)).x;
		GUI.Label(new Rect(x, r.y, labelWidth, r.height), label, line);
		x += labelWidth + S(12);
		int current = ConfigManager.UiLanguage.Value;
		for (int i = 0; i < Languages.Length; i++)
		{
			GUIStyle style = i == current ? _primary : _button;
			float w = Mathf.Max(S(96), style.CalcSize(new GUIContent(Languages[i])).x + S(20));
			if (GUI.Button(new Rect(x, r.y, w, r.height), Languages[i], style))
			{
				ConfigManager.UiLanguage.Value = i;
			}
			x += w + S(6);
		}
	}

	private readonly Dictionary<(GUIStyle, int), GUIStyle> _shrunk = new Dictionary<(GUIStyle, int), GUIStyle>();

	/// <summary>文字比宽度长时，返回字号小一些的同款样式（最小原来的八成）；放得下就是原样式。</summary>
	private GUIStyle Shrunk(GUIStyle style, string text, float width)
	{
		float full = style.CalcSize(new GUIContent(text)).x;
		if (full <= width || style.fontSize <= 0)
		{
			return style;
		}
		int size = Mathf.Max(Mathf.CeilToInt(style.fontSize * 0.8f), Mathf.FloorToInt(style.fontSize * width / full));
		if (!_shrunk.TryGetValue((style, size), out GUIStyle small))
		{
			small = new GUIStyle(style) { fontSize = size };
			_shrunk[(style, size)] = small;
		}
		return small;
	}

	private void DrawSettingRow(SettingRow row, float width)
	{
		IConfigElement element = row.Element;
		GUILayout.BeginHorizontal(_card, GUILayout.Width(width - S(10)));
		GUIStyle line = Middle(_label);
		float nameWidth = S(232);
		// 译文比中文长时先把字缩小一点（最小到八成）放进去，还放不下才截短
		string shownName = TextConverter.ForDisplay(row.Name);
		GUIStyle nameStyle = Shrunk(line, shownName, nameWidth);
		GUILayout.Label(Fit(shownName, nameStyle, nameWidth), nameStyle, GUILayout.Width(nameWidth), Ctl());
		Rect nameRect = GUILayoutUtility.GetLastRect();
		if (row.Restart)
		{
			GUILayout.Label("重启生效", Middle(_muted), GUILayout.Width(S(76)), Ctl());
		}
		Hover(nameRect, UiDescriptions.Get(element.Name, TextConverter.DisplayLanguage) ?? Describe(element.Description));
		GUILayout.FlexibleSpace();
		object value = element.BoxedValue;
		if (value is bool on)
		{
			if (GUILayout.Button(on ? "开" : "关", on ? _primary : _button, GUILayout.Width(S(80)), Ctl()))
			{
				element.BoxedValue = !on;
			}
		}
		else if (value is KeyCode key)
		{
			bool waiting = _capturing == row;
			// 只认左键点：右键点这个框会被当成「按了新键」绑上，不能再顺手开始下一次改键
			if (GUILayout.Button(waiting ? "按新键…" : InputGuard.KeyName(key), waiting ? _primary : _button, GUILayout.Width(S(130)), Ctl()) && Event.current.button == 0)
			{
				if (waiting)
				{
					StopCapture();
				}
				else
				{
					_capturing = row;
					InputGuard.CapturingKey = true;
					GUIUtility.keyboardControl = 0;
				}
			}
			// 改成什么都能一键回到默认键（比如换走了鼠标右键想换回来）
			GUI.enabled = !waiting && !Equals(key, element.DefaultValue);
			if (GUILayout.Button("默认", _button, GUILayout.Width(S(60)), Ctl()))
			{
				AssignKey(row, (KeyCode)element.DefaultValue);
			}
			// 不想要这个热键（比如和别的 Mod 抢键）就改成不绑定；开本窗口的键不行，解了就再也打不开窗口改回来
			GUI.enabled = !waiting && key != KeyCode.None && element != ConfigManager.StoryReviewKey;
			if (GUILayout.Button("不绑定", _button, GUILayout.Width(S(76)), Ctl()))
			{
				AssignKey(row, KeyCode.None);
			}
			GUI.enabled = true;
		}
		else if (row.Choices != null && row.Dropdown && value is int picked)
		{
			bool open = _dropdown == row;
			int current = Mathf.Clamp(picked, 0, row.Choices.Length - 1);
			// 只在展开时套一层竖排：竖排里按钮的上下边距会多算一次，收起时直接放，和别的行一样高
			if (open)
			{
				GUILayout.BeginVertical(GUILayout.Width(S(170)));
			}
			if (GUILayout.Button(row.Choices[current] + (open ? "  ▲" : "  ▼"), open ? _primary : _button, GUILayout.Width(S(170)), Ctl()))
			{
				_dropdown = open ? null : row;
			}
			if (open)
			{
				for (int i = 0; i < row.Choices.Length; i++)
				{
					if (i != current && GUILayout.Button(row.Choices[i], _button, GUILayout.Width(S(170)), Ctl()))
					{
						if (row.Choose != null)
						{
							row.Choose(i);
						}
						else
						{
							element.BoxedValue = i;
						}
						_dropdown = null;
					}
				}
				GUILayout.EndVertical();
			}
		}
		else if (row.Choices != null && value is int choice)
		{
			// 手柄键名按接着的手柄型号写（Xbox / PS / Switch）
			string[] choices = element == ConfigManager.GamepadPanelButton ? GamepadCursor.ToggleNames : row.Choices;
			for (int i = 0; i < choices.Length; i++)
			{
				if (GUILayout.Button(choices[i], i == choice ? _primary : _button, GUILayout.Width(S(88)), Ctl()))
				{
					if (row.Choose != null)
					{
						row.Choose(i);
					}
					else
					{
						element.BoxedValue = i;
					}
				}
			}
		}
		else if (row.Speeds != null && value is float speed)
		{
			foreach (float option in row.Speeds)
			{
				if (GUILayout.Button(option + "x", Mathf.Approximately(speed, option) ? _primary : _button, GUILayout.Width(S(60)), Ctl()))
				{
					GameSpeed.Set(option);
				}
			}
		}
		else if (value is int || value is float)
		{
			DrawNumber(row, value);
		}
		GUILayout.EndHorizontal();
	}

	// 设置页每行的按钮、输入框、文字一样高，行高才一致
	private GUILayoutOption Ctl() => GUILayout.Height(S(34));

	private readonly Dictionary<GUIStyle, GUIStyle> _middle = new Dictionary<GUIStyle, GUIStyle>();
	private float _middleScale;

	// 单行、竖直居中的文字
	private GUIStyle Middle(GUIStyle style)
	{
		if (_middleScale != _scale)
		{
			_middle.Clear();
			_middleScale = _scale;
		}
		if (!_middle.TryGetValue(style, out GUIStyle middle))
		{
			_middle[style] = middle = new GUIStyle(OneLine(style)) { alignment = TextAnchor.MiddleLeft };
		}
		return middle;
	}

	private static string Red(string text) => "<color=#B32B21>" + text + "</color>";

	private string _pinballInput;

	// 在玩弹珠小游戏时，最上面多一行：直接改当前总分
	private void DrawPinballScore(float rowWidth)
	{
		PinballManager pinball = PinballManager.Instance;
		if (pinball == null || !pinball.isActiveAndEnabled)
		{
			_pinballInput = null;
			return;
		}
		var tr = Traverse.Create(pinball);
		int total = tr.Field("_totalScore").GetValue<int>();
		GUILayout.Space(S(8));
		GUILayout.Label("弹珠小游戏", _heading);
		GUILayout.BeginHorizontal(_card, GUILayout.Width(rowWidth - S(10)));
		GUIStyle line = Middle(_label);
		GUILayout.Label("当前总分 " + total, line, GUILayout.Width(S(232)), Ctl());
		GUILayout.FlexibleSpace();
		_pinballInput ??= total.ToString();
		_pinballInput = GUILayout.TextField(_pinballInput, 9, _field, GUILayout.Width(S(120)), Ctl());
		bool valid = int.TryParse(_pinballInput.Trim(), out int value);
		GUI.enabled = valid && value != total;
		if (GUILayout.Button("设定", GUI.enabled ? _primary : _button, GUILayout.Width(S(60)), Ctl()))
		{
			tr.Field("_totalScore").SetValue(value);
			tr.Method("UpdateTotalText").GetValue();
			FileLogger.Log($"弹珠分数：总分 {total} -> {value}（设置页修改）");
			GUIUtility.keyboardControl = 0;
		}
		GUI.enabled = true;
		GUILayout.EndHorizontal();
	}

	private void DrawNumber(SettingRow row, object value)
	{
		string id = row.Element.Name;
		string current = value is float f ? f.ToString("0.##", CultureInfo.InvariantCulture) : value.ToString();
		if (!_settingInputs.TryGetValue(id, out string input))
		{
			input = current;
		}
		string typed = GUILayout.TextField(input, _field, GUILayout.Width(S(90)), Ctl());
		if (typed != current)
		{
			_settingInputs[id] = typed;
		}
		else
		{
			_settingInputs.Remove(id);
		}
		bool valid = float.TryParse(typed, NumberStyles.Float, CultureInfo.InvariantCulture, out float number);
		bool changed = _settingInputs.ContainsKey(id) && valid;
		GUI.enabled = changed;
		if (GUILayout.Button("设定", changed ? _primary : _button, GUILayout.Width(S(60)), Ctl()))
		{
			number = Mathf.Clamp(number, row.Min, row.Max);
			row.Element.BoxedValue = value is int ? (object)Mathf.RoundToInt(number) : number;
			_settingInputs.Remove(id);
			GUIUtility.keyboardControl = 0;
		}
		GUI.enabled = true;
	}
}
