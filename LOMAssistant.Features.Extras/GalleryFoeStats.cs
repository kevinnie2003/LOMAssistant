using System;
using System.Collections.Generic;
using System.Linq;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Extras;

public sealed partial class Gallery
{
	private bool _foeShowStats;
	private bool _foePickEncounter;
	private GalleryDuelRadar _foeRadar;
	private string _foeEncounterSearch = "", _foeCatalogError;
	private List<DuelEncounter> _foeEncounters = new List<DuelEncounter>();
	private DuelEncounter _foeEncounter;
	private Vector2 _foeEncounterScroll, _foeStatsScroll;
	private GUISkin _foeStatsSkin;
	private GUIStyle _foeWrapped, _foeStatsHeading, _foeStatsSubheading, _foeSubtitle, _foeDetail, _foeCondition;
	private float _foeStatsScale;
	private bool _foeStatsDark;
	private static readonly string[] SixNames = { "体力", "内力", "轻功", "拳掌", "刀剑", "暗器" };

	private void SelectFoeStats()
	{
		_foeEncounterScroll = _foeStatsScroll = Vector2.zero;
		_foeBuffEncounter = null;
		_foeEnabledBuffs.Clear();
		_foePickEncounter = false;
		_foeEncounterSearch = "";
		_foeEncounters.Clear();
		_foeEncounter = null;
		_foeCatalogError = null;
		try
		{
			var all = GalleryDuelCatalog.Instance.ForFolder(_foe.Folder);
			// 「测试」关卡（CL_0000_*）是开发用的，玩家碰不到；只有测试关卡的角色才留着
			var real = all.Where(e => !e.Id.StartsWith("CL_0000", StringComparison.Ordinal)).ToList();
			_foeEncounters = real.Count > 0 ? real : all;
			_foeEncounter = _foeEncounters.FirstOrDefault();
		}
		catch (Exception e)
		{
			_foeCatalogError = TextConverter.ForDisplay("决斗资料读取失败");
			FileLogger.LogWarning("[鉴赏] 决斗资料: " + e);
		}
	}

	private void FoeStatsStyles()
	{
		if (_foeWrapped != null && _foeStatsSkin != null && Mathf.Approximately(_foeStatsScale, _scale) && _foeStatsDark == PanelTheme.Dark) return;
		_foeStatsScale = _scale;
		_foeStatsDark = PanelTheme.Dark;
		_foeWrapped = PanelTheme.Label(_scale, 18, PanelTheme.Ink, wrap: true);
		_foeWrapped.richText = false; // 原游戏说明有富文本，走纯文本以免固定色在深色主题下不可读。
		_foeStatsHeading = PanelTheme.Label(_scale, 22, PanelTheme.Ink, wrap: true);
		_foeStatsHeading.fontStyle = FontStyle.Bold;
		// 去掉左右外边距：GUILayout 排的正文和 GUI.Label 画的标题左边对齐
		foreach (GUIStyle style in new[] { _foeWrapped, _foeStatsHeading })
			style.margin = new RectOffset(0, 0, style.margin.top, style.margin.bottom);
		// 技能名、事件时机：比栏目标题小一号
		_foeStatsSubheading = PanelTheme.Label(_scale, 18, PanelTheme.Ink, wrap: true);
		_foeStatsSubheading.fontStyle = FontStyle.Bold;
		_foeStatsSubheading.margin = new RectOffset(0, 0, _foeStatsSubheading.margin.top, _foeStatsSubheading.margin.bottom);
		// 技能、效果的说明：颜色比名字淡，一眼分开名字和说明
		_foeDetail = PanelTheme.Label(_scale, 17, PanelTheme.InkMuted, wrap: true);
		_foeDetail.richText = false;
		_foeDetail.margin = new RectOffset(0, 0, _foeDetail.margin.top, _foeDetail.margin.bottom);
		// 增益的触发条件、剧情条件：小一号、换个颜色，和说明分开
		_foeCondition = PanelTheme.Label(_scale, 16, PanelTheme.Jade, wrap: true);
		_foeCondition.richText = false;
		_foeCondition.margin = new RectOffset(0, 0, _foeCondition.margin.top, _foeCondition.margin.bottom);
		_foeSubtitle = PanelTheme.Label(_scale, 16, PanelTheme.InkMuted, wrap: false);
		_foeSubtitle.clipping = TextClipping.Clip;
		_foeSubtitle.margin = new RectOffset();
		if (_foeStatsSkin != null) Destroy(_foeStatsSkin);
		_foeStatsSkin = Instantiate(GUI.skin);
		_foeStatsSkin.hideFlags = HideFlags.HideAndDontSave;
		var rail = new GUIStyle(GUI.skin.verticalScrollbar)
		{
			name = "verticalScrollbar", fixedWidth = S(10f), padding = new RectOffset(),
			border = new RectOffset(), margin = new RectOffset(Mathf.RoundToInt(S(4f)), 0, 0, 0),
		};
		var thumb = new GUIStyle(GUI.skin.verticalScrollbarThumb)
		{
			name = "verticalScrollbarThumb", fixedWidth = S(8f), fixedHeight = 0f,
			border = new RectOffset(), padding = new RectOffset(), margin = new RectOffset(),
		};
		Color railColor = Color.Lerp(PanelTheme.PaperLight, PanelTheme.PaperDeep, 0.35f);
		Color thumbColor = Color.Lerp(PanelTheme.PaperDeep, PanelTheme.InkMuted, 0.3f);
		foreach (GUIStyleState state in new[] { rail.normal, rail.hover, rail.active, rail.focused })
			state.background = PanelTheme.Framed(railColor, railColor);
		foreach (GUIStyleState state in new[] { thumb.normal, thumb.hover, thumb.active, thumb.focused })
			state.background = PanelTheme.Framed(thumbColor, thumbColor);
		_foeStatsSkin.verticalScrollbar = rail;
		_foeStatsSkin.verticalScrollbarThumb = thumb;
		_foeStatsSkin.verticalScrollbarUpButton = new GUIStyle { fixedWidth = S(10f), fixedHeight = 0f };
		_foeStatsSkin.verticalScrollbarDownButton = new GUIStyle { fixedWidth = S(10f), fixedHeight = 0f };
		FoeBuffStyles();
	}

	private static string DuelText(string value) => TextConverter.ForDisplay(TextConverter.ToSimplified(value ?? ""));

	private static string DuelLocalized(string key, string fallback)
	{
		string value = LocalizationManager.Instance?.LocaleResolver?.GetString(key);
		if (string.IsNullOrEmpty(value) || value == key) value = fallback;
		value = System.Text.RegularExpressions.Regex.Replace(value ?? "", @"</?(?:b|i|size|color)(?:=[^>]*)?>", "");
		// 技能说明开头的「[決鬥]」这类场合标签：这里全是决斗，去掉
		value = System.Text.RegularExpressions.Regex.Replace(value, @"^\s*\[[^\]\n]{1,6}\]\s*(\\n|\n)?", "");
		return TextConverter.ForDisplay(value.Replace("\\n", "\n"));
	}

	private static string EncounterTitle(DuelEncounter encounter)
	{
		if (string.IsNullOrWhiteSpace(encounter.Title))
			return TextConverter.ForDisplay(string.IsNullOrEmpty(encounter.Id) ? "未关联剧情的配置" : "未命名剧情");
		return DuelText(encounter.Title);
	}

	// 面板四周的边距；滚动条（10 宽 + 4 间隔）放在右边距里，内容的右边缘和标题栏按钮对齐
	private float FoePad => S(16f);
	private float FoeGutter => S(14f);

	private void DrawFoeEncounters(Rect area)
	{
		FoeStatsStyles();
		float width = area.width - FoeGutter;
		GUI.SetNextControlName("LOMAssistant_FoeEncounterSearch");
		_foeEncounterSearch = GUI.TextField(new Rect(area.x, area.y, width, S(40f)), _foeEncounterSearch, _field);
		if (string.IsNullOrEmpty(_foeEncounterSearch) && GUI.GetNameOfFocusedControl() != "LOMAssistant_FoeEncounterSearch")
			GUI.Label(new Rect(area.x + S(10f), area.y, width - S(20f), S(40f)), TextConverter.ForDisplay("搜索剧情 / 配置"), _foeHint);
		string query = TextConverter.ToSimplified(_foeEncounterSearch.Trim());
		var shown = _foeEncounters.Where(e => query.Length == 0 || TextConverter.ToSimplified(
			EncounterTitle(e) + " " + e.Title + " " + e.Id + " " + e.StatId).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
		GUI.Label(new Rect(area.x, area.y + S(46f), width, S(26f)),
			string.Format(TextConverter.ForDisplay("{0} 个剧情 / 状态"), shown.Count), _foeSmall);
		// 每个剧情一行：名字靠左，编号小字靠右
		float rowHeight = S(48f), step = rowHeight + S(6f);
		var box = new Rect(area.x, area.y + S(78f), area.width, Mathf.Max(1f, area.height - S(78f)));
		DragScroll(new Rect(box.x, box.y, width, box.height), ref _foeEncounterScroll);
		_foeEncounterScroll = GUI.BeginScrollView(box, _foeEncounterScroll, new Rect(0, 0, width, Mathf.Max(box.height, shown.Count * step)));
		var name = new GUIStyle(_foeName) { alignment = TextAnchor.MiddleLeft };
		var id = new GUIStyle(_foeSmall) { alignment = TextAnchor.MiddleRight };
		for (int i = 0; i < shown.Count; i++)
		{
			DuelEncounter encounter = shown[i];
			var row = new Rect(0, i * step, width, rowHeight);
			bool on = encounter == _foeEncounter;
			if (GUI.Button(row, GUIContent.none, on ? _foeRowOn : _foeRow))
			{
				_foeEncounter = encounter;
				_foeStatsScroll = Vector2.zero;
				_foePickEncounter = false;
			}
			string code = string.IsNullOrEmpty(encounter.Id) ? DuelText(encounter.StatId.Replace("Enemy_", "")) : encounter.Id;
			float codeWidth = id.CalcSize(new GUIContent(code)).x;
			name.normal.textColor = on ? _foeNameOn.normal.textColor : _foeName.normal.textColor;
			id.normal.textColor = on ? new Color(_foeNameOn.normal.textColor.r, _foeNameOn.normal.textColor.g, _foeNameOn.normal.textColor.b, 0.75f) : PanelTheme.InkMuted;
			GUI.Label(new Rect(row.x + S(14f), row.y, row.width - codeWidth - S(40f), row.height), EncounterTitle(encounter), name);
			GUI.Label(new Rect(row.xMax - codeWidth - S(14f), row.y, codeWidth, row.height), code, id);
		}
		GUI.EndScrollView();
	}

	private bool _foeScrollDrag;
	private Vector2 _foeScrollDragLast;
	private float _foeScrollDragMoved;

	/// <summary>
	/// 按住鼠标拖动来滚动（不只是滚轮）。区域不含右边的滚动条，拖滚动条照旧。
	/// 拖过一小段再松手，这一下不算点击（不会误选列表里的剧情）。在画滚动区之前调用，坐标和 r 一致。
	/// </summary>
	private void DragScroll(Rect r, ref Vector2 scroll)
	{
		Event e = Event.current;
		switch (e.type)
		{
			case EventType.MouseDown:
				if (e.button == 0 && r.Contains(e.mousePosition))
				{
					_foeScrollDrag = true;
					_foeScrollDragLast = e.mousePosition;
					_foeScrollDragMoved = 0f;
				}
				break;
			case EventType.MouseDrag:
				if (_foeScrollDrag)
				{
					float dy = e.mousePosition.y - _foeScrollDragLast.y;
					_foeScrollDragLast = e.mousePosition;
					_foeScrollDragMoved += Mathf.Abs(dy);
					scroll.y = Mathf.Max(0f, scroll.y - dy);
					e.Use();
				}
				break;
			case EventType.MouseUp:
				if (_foeScrollDrag)
				{
					_foeScrollDrag = false;
					if (_foeScrollDragMoved > S(6f))
					{
						GUIUtility.hotControl = 0;
						e.Use();
					}
				}
				break;
		}
	}

	private void DrawFoeStats(Rect area)
	{
		FoeStatsStyles();
		GUISkin previous = GUI.skin;
		try
		{
			GUI.skin = _foeStatsSkin;
			DrawFoeStatsContent(area);
		}
		finally { GUI.skin = previous; }
	}

	private void DrawFoeStatsContent(Rect area)
	{
		Fill(area, PanelTheme.PaperLight);
		// 第一行：角色名（加备注），右边「选择剧情」（只有一个剧情时不显示）；第二行灰字：剧情名、编号、关卡难度。
		// 「选择剧情」和点开后的「收起」是同一个按钮，宽度按两者里长的算，切换时大小不变
		float pad = FoePad;
		var header = new Rect(area.x + pad, area.y + S(12f), area.width - pad * 2f, S(40f));
		float top = header.y;
		if (_foeEncounter != null)
		{
			DuelProfile current = GalleryDuelCatalog.Instance.Profile(_foeEncounter.StatId);
			bool many = _foeEncounters.Count > 1;
			string open = TextConverter.ForDisplay("选择剧情"), close = TextConverter.ForDisplay("收起");
			float buttonWidth = many ? Mathf.Min(Mathf.Max(_track.CalcSize(new GUIContent(open)).x, _track.CalcSize(new GUIContent(close)).x) + S(32f), header.width * 0.4f) : 0f;
			var title = new GUIStyle(_foeStatsHeading) { wordWrap = false, clipping = TextClipping.Clip, alignment = TextAnchor.MiddleLeft, padding = new RectOffset() };
			GUI.Label(new Rect(header.x, header.y, header.width - buttonWidth - S(12f), header.height),
				DuelLocalized("CombatCharacter/Name/" + current.CharacterId, current.Name)
				+ (string.IsNullOrWhiteSpace(current.Note) ? "" : " · " + DuelText(current.Note)), title);
			if (many && GUI.Button(new Rect(header.xMax - buttonWidth, header.y, buttonWidth, header.height), _foePickEncounter ? close : open, _foePickEncounter ? _trackOn : _track))
				_foePickEncounter = !_foePickEncounter;
			string sub = EncounterTitle(_foeEncounter)
				+ (string.IsNullOrEmpty(_foeEncounter.Id) ? "" : " · " + _foeEncounter.Id)
				+ (_foeEncounter.Difficulty > 0 ? " · " + string.Format(TextConverter.ForDisplay("关卡难度 {0}"), _foeEncounter.Difficulty) : "");
			GUI.Label(new Rect(header.x, header.yMax + S(2f), header.width, S(24f)), sub, _foeSubtitle);
			top = header.yMax + S(34f);
		}
		// 滚动区伸进右边距，滚动条在边距里，内容右边缘仍是 area.xMax - pad
		var content = new Rect(area.x + pad, top, area.width - pad * 2f + FoeGutter, area.yMax - top - pad);
		if (_foePickEncounter)
		{
			DrawFoeEncounters(content);
			return;
		}
		DragScroll(new Rect(content.x, content.y, content.width - FoeGutter, content.height), ref _foeStatsScroll);
		GUILayout.BeginArea(content);
		_foeStatsScroll = GUILayout.BeginScrollView(_foeStatsScroll);
		if (_foeEncounter == null)
		{
			GUILayout.Label(_foeCatalogError ?? TextConverter.ForDisplay(_foeLoading ? "正在加载…" : "此立绘没有关联的决斗配置"), _foeWrapped);
		}
		else
		{
			DuelProfile profile = GalleryDuelCatalog.Instance.Profile(_foeEncounter.StatId);
			SelectFoeBuffs(profile);
			bool sideBySide = content.width >= S(620f);
			float columnGap = S(24f);
			float chartWidth = sideBySide ? Mathf.Min(S(370f), (content.width - columnGap) * 0.52f) : Mathf.Min(S(400f), content.width - S(24f));
			float chartHeight = chartWidth * 500f / 440f;
			if (sideBySide) GUILayout.BeginHorizontal();
			GUILayout.BeginVertical(GUILayout.Width(chartWidth));
			GUILayout.Label(F(_foeEnabledBuffs.Count > 0 ? "增益后面板" : "基础面板"), _foeStatsSubheading);
			DrawFoeRadar(GalleryDuelBuffPreview.Calculate(profile, _foeBuffOptions, _foeEnabledBuffs, _foeBuffHealth, _foeBuffSupport), chartWidth, chartHeight);
			DrawFoePersonality(profile, chartWidth);
			GUILayout.EndVertical();
			GUILayout.Space(sideBySide ? columnGap : S(16f));
			GUILayout.BeginVertical();
			GUILayout.Label(TextConverter.ForDisplay("角色技能"), _foeStatsHeading);
			DrawFoeBuffControls(profile);
			if (profile.Skills.Count == 0) GUILayout.Label(TextConverter.ForDisplay("无固有技能"), _foeWrapped);
			foreach (DuelSkill skill in profile.Skills)
			{
				string title = DuelLocalized("PlayerTalent/Name/" + skill.Id, skill.Name);
				if (skill.DisplayLevel) title += "  Lv." + skill.Level;
				if (skill.Level <= 0) title += " · " + TextConverter.ForDisplay("未启用");
				GUILayout.Label(title, _foeStatsSubheading);
				GUILayout.Label(DuelLocalized("PlayerTalent/Desc/" + skill.Id, skill.Description), _foeDetail);
				DrawFoeBuffOptions(skill);
				GUILayout.Space(S(10f));
			}
			if (_foeBuffOptions.Any(o => o.Skill == null))
			{
				GUILayout.Label(F("剧情增益"), _foeStatsSubheading);
				DrawFoeBuffOptions(null);
			}
			GUILayout.EndVertical();
			if (sideBySide) GUILayout.EndHorizontal();
			GUILayout.Label(string.Format(TextConverter.ForDisplay("血量 {0}/{1} · 气力 {2}/{3} · 嘴力 {4}"),
				profile.Health, profile.MaxHealth, profile.Energy, profile.MaxEnergy, profile.Talking), _foeWrapped);
			GUILayout.Space(S(12f));
			GUILayout.Label(TextConverter.ForDisplay("剧情状态变化"), _foeStatsHeading);
			if (_foeEncounter.Events.Count == 0)
				GUILayout.Label(TextConverter.ForDisplay("未记录额外状态事件"), _foeWrapped);
			// 同一个时机（开场、某个条件满足时）只写一次小标题，下面每条效果一行；完全相同的条目只列一次
			string currentHead = null;
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (DuelEvent change in _foeEncounter.Events)
			{
				string condition = change.Phase == "Start" ? "" : DuelCondition(change.Trigger);
				string head = change.Phase == "Start" ? F("开场")
					: condition.Length > 0 ? condition
					: F(change.Phase == "Round" ? "回合开始" : change.Phase == "Action" ? "行动开始" : "行动结束");
				string text;
				if (change.Effect != null)
				{
					string who = F(change.Target == 1 ? "我方" : "敌方");
					string name = DuelLocalized("CombatSkill/Name/" + change.Effect.Key, change.Effect.Name);
					string desc = DuelLocalized("CombatSkill/Desc/" + change.Effect.Key, change.Effect.Description).Replace("\n", " ").Trim();
					// 说明一般以名字开头（「封印：无法使用绝招。」），获得时只写说明
					text = change.Remove ? Join(who, F("失去")) + "「" + name + "」"
						: Join(who, F("获得")) + (Join("a", "b").Length == 2 ? "：" : ": ") + (desc.Length > 0 ? desc : name);
				}
				else
				{
					text = DuelAction(change.Action);
				}
				if (!seen.Add(head + "\n" + text))
				{
					continue;
				}
				if (head != currentHead)
				{
					currentHead = head;
					GUILayout.Space(S(6f));
					GUILayout.Label(head, _foeStatsSubheading);
				}
				GUILayout.Label(text, _foeDetail);
			}
		}
		GUILayout.EndScrollView();
		GUILayout.EndArea();
	}

	private void DrawFoeRadar(int[] values, float width, float height)
	{
		Rect rect = GUILayoutUtility.GetRect(width, height);
		if (Event.current.type != EventType.Repaint) return;
		_foeRadar ??= new GalleryDuelRadar();
		Texture texture = _foeRadar.Render(values);
		if (texture != null) GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, true);
		// 游戏 StatChart 下六个标签的位置，和游戏图表同方向。
		Vector2[] positions = { new Vector2(0, 155.7f), new Vector2(143.2f, 78.3f), new Vector2(-139.7f, 79.8f),
			new Vector2(140.4f, -81.7f), new Vector2(-141.7f, -82.2f), new Vector2(0, -163.1f) };
		float factor = width / 440f;
		var label = new GUIStyle(_foeWrapped) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.Max(10, Mathf.RoundToInt(23f * factor)) };
		// 游戏底图是深色，字用浅色；底图读不到时画在纸色上，字用墨色
		label.normal.textColor = _foeRadar.HasBackground ? new Color(0.95f, 0.92f, 0.85f) : PanelTheme.Ink;
		for (int i = 0; i < 6; i++)
		{
			Vector2 center = rect.center + new Vector2(positions[i].x, -positions[i].y) * factor;
			string number = values[i].ToString();
			GUI.Label(new Rect(center.x - 60f * factor, center.y - 34f * factor, 120f * factor, 68f * factor),
				TextConverter.ForDisplay(SixNames[i]) + "\n" + number, label);
		}
	}

	// ---------------- 决斗事件的条件、动作：游戏数据里是开发者起的名字（「我方_血量_LE_PCT_30」），按命名规则写成人话 ----------------
	// 规则和 tools/make-conditions.py 的 cond_text / action_text 一样；认不出的把下划线换成空格原样显示

	private static readonly Dictionary<string, string> DuelOps = new Dictionary<string, string>
	{
		["EQ"] = "=", ["NE"] = "≠", ["GE"] = "≥", ["LE"] = "≤", ["GT"] = ">", ["LT"] = "<",
	};
	private const string DuelOp = "(EQ|NE|GE|LE|GT|LT)";

	private static string F(string text) => TextConverter.ForDisplay(text);

	// 拼句子：不用「{0}{1} {2}」这种没有固定文字的格式串（翻译表把带占位符的行当整句模板，会套到别的句子上），
	// 而是一个词一个词翻好再拼；中文词之间不空格，其他语言空一格
	private static string Join(params string[] words)
	{
		string gap = TextConverter.DisplayLanguage == UiText.Korean || TextConverter.DisplayLanguage == UiText.Japanese
			|| TextConverter.DisplayLanguage == UiText.English ? " " : "";
		return string.Join(gap, words.Where(w => !string.IsNullOrEmpty(w)).ToArray());
	}

	private static string DuelCondition(string trigger)
	{
		if (string.IsNullOrEmpty(trigger))
		{
			return "";
		}
		string[] parts = System.Text.RegularExpressions.Regex.Split(TextConverter.ToSimplified(trigger), " (AND|OR) ");
		var text = new System.Text.StringBuilder();
		for (int i = 0; i < parts.Length; i += 2)
		{
			string part = DuelConditionPart(parts[i]);
			if (part == null)
			{
				continue;
			}
			if (text.Length > 0)
			{
				text.Append(" " + F(parts[i - 1] == "AND" ? "且" : "或") + " ");
			}
			text.Append(part);
		}
		return text.ToString();
	}

	/// <summary>一个条件的人话；游戏内部的通用计数（TC 开头的决斗泛用旗标、A 开头的暂存旗标）玩家看不懂，返回 null 不显示。</summary>
	private static string DuelConditionPart(string c)
	{
		if (System.Text.RegularExpressions.Regex.IsMatch(c, @"^旗标_(TC|A)\d+_"))
		{
			return null;
		}
		// 「决斗泛用事件旗标」「旗标暂存」这类通用旗标的备注不说明什么，去掉
		c = System.Text.RegularExpressions.Regex.Replace(c, "（[^）]*(泛用|暂存)[^）]*）", "");
		System.Text.RegularExpressions.Match m;
		if ((m = System.Text.RegularExpressions.Regex.Match(c, "^回合数_" + DuelOp + @"_(\d+)$")).Success)
		{
			return m.Groups[1].Value == "EQ" ? string.Format(F("第 {0} 回合"), m.Groups[2].Value)
				: F("回合数") + " " + DuelOps[m.Groups[1].Value] + " " + m.Groups[2].Value;
		}
		if ((m = System.Text.RegularExpressions.Regex.Match(c, @"^(我方|敌方|敌人)_(\w+?)_" + DuelOp + @"_(PCT_)?(-?\d+)$")).Success)
		{
			return Join(F(m.Groups[1].Value), F(m.Groups[2].Value)) + " " + DuelOps[m.Groups[3].Value] + " "
				+ m.Groups[5].Value + (m.Groups[4].Success ? "%" : "");
		}
		if ((m = System.Text.RegularExpressions.Regex.Match(c, @"^数值_(\w+?)_" + DuelOp + @"_(-?\d+)$")).Success)
		{
			return Join(F("主角"), F(m.Groups[1].Value)) + " " + DuelOps[m.Groups[2].Value] + " " + m.Groups[3].Value;
		}
		if ((m = System.Text.RegularExpressions.Regex.Match(c, @"^旗标_(\w+?)_" + DuelOp + @"_(-?\d+)(（.*）)?$")).Success)
		{
			return F("计数") + " " + m.Groups[1].Value + DuelText(m.Groups[4].Value) + " " + DuelOps[m.Groups[2].Value] + " " + m.Groups[3].Value;
		}
		if (c.StartsWith("旗标回合_", StringComparison.Ordinal))
		{
			return F("到了约定的回合");
		}
		return DuelText(c.Replace('_', ' '));
	}

	private static string DuelAction(string action)
	{
		string a = TextConverter.ToSimplified(action ?? "").Replace("格档", "格挡");
		var m = System.Text.RegularExpressions.Regex.Match(a, @"^(我方|敌方|敌人)_(\w+?)_(加|减)_(\d+)(_PCT)?(?:_(\w+))?$");
		if (!m.Success)
		{
			return DuelText(a.Replace('_', ' '));
		}
		// 「我方血量 −20%」：加减号不用翻译
		string text = Join(F(m.Groups[1].Value), F(m.Groups[2].Value)) + " " + (m.Groups[3].Value == "加" ? "+" : "−")
			+ int.Parse(m.Groups[4].Value) + (m.Groups[5].Success ? "%" : "");
		return m.Groups[6].Success ? text + "（" + F(m.Groups[6].Value) + "）" : text;
	}
}
