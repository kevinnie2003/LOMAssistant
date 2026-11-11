using System;
using System.Collections.Generic;
using System.Linq;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Extras;

public sealed partial class Gallery
{
	private bool _foeShowStats;
	private bool _foeBrave, _foeFriendly, _foePickEncounter;
	private GalleryDuelRadar _foeRadar;
	private string _foeEncounterSearch = "", _foePlayerTotal = "300", _foeInheritedTotal = "0", _foeCatalogError;
	private List<DuelEncounter> _foeEncounters = new List<DuelEncounter>();
	private DuelEncounter _foeEncounter;
	private Vector2 _foeEncounterScroll, _foeStatsScroll;
	private GUISkin _foeStatsSkin;
	private GUIStyle _foeWrapped, _foeStatsHeading;
	private float _foeStatsScale;
	private bool _foeStatsDark;
	private static readonly string[] SixNames = { "体力", "内力", "轻功", "拳掌", "刀剑", "暗器" };

	private void SelectFoeStats()
	{
		_foeEncounterScroll = _foeStatsScroll = Vector2.zero;
		_foePickEncounter = false;
		_foeEncounterSearch = "";
		_foeEncounters.Clear();
		_foeEncounter = null;
		_foeCatalogError = null;
		try
		{
			_foeEncounters = GalleryDuelCatalog.Instance.ForFolder(_foe.Folder);
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
		if (_foeStatsSkin != null) Destroy(_foeStatsSkin);
		_foeStatsSkin = Instantiate(GUI.skin);
		_foeStatsSkin.hideFlags = HideFlags.HideAndDontSave;
		var rail = new GUIStyle(GUI.skin.verticalScrollbar)
		{
			name = "verticalScrollbar", fixedWidth = S(10f), padding = new RectOffset(),
			border = new RectOffset(), margin = new RectOffset(Mathf.RoundToInt(S(8f)), 0, 0, 0),
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
	}

	private static string DuelText(string value) => TextConverter.ForDisplay(TextConverter.ToSimplified(value ?? ""));

	private static string DuelLocalized(string key, string fallback)
	{
		string value = LocalizationManager.Instance?.LocaleResolver?.GetString(key);
		if (string.IsNullOrEmpty(value) || value == key) value = fallback;
		value = System.Text.RegularExpressions.Regex.Replace(value ?? "", @"</?(?:b|i|size|color)(?:=[^>]*)?>", "");
		return TextConverter.ForDisplay(value.Replace("\\n", "\n"));
	}

	private static string EncounterTitle(DuelEncounter encounter)
	{
		if (string.IsNullOrWhiteSpace(encounter.Title))
			return TextConverter.ForDisplay(string.IsNullOrEmpty(encounter.Id) ? "未关联剧情的配置" : "未命名剧情");
		return DuelText(encounter.Title);
	}

	private void DrawFoeEncounters(Rect area)
	{
		FoeStatsStyles();
		GUI.SetNextControlName("LOMAssistant_FoeEncounterSearch");
		_foeEncounterSearch = GUI.TextField(new Rect(area.x, area.y, area.width, S(40f)), _foeEncounterSearch, _field);
		if (string.IsNullOrEmpty(_foeEncounterSearch) && GUI.GetNameOfFocusedControl() != "LOMAssistant_FoeEncounterSearch")
			GUI.Label(new Rect(area.x + S(10f), area.y, area.width - S(20f), S(40f)), TextConverter.ForDisplay("搜索剧情 / 配置"), _foeHint);
		string query = TextConverter.ToSimplified(_foeEncounterSearch.Trim());
		var shown = _foeEncounters.Where(e => query.Length == 0 || TextConverter.ToSimplified(
			EncounterTitle(e) + " " + e.Title + " " + e.Id + " " + e.StatId).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
		GUI.Label(new Rect(area.x, area.y + S(44f), area.width, S(28f)),
			string.Format(TextConverter.ForDisplay("{0} 个剧情 / 状态"), shown.Count), _foeSmall);
		var box = new Rect(area.x, area.y + S(78f), area.width, Mathf.Max(1f, area.height - S(78f)));
		float rowWidth = area.width - S(22f), y = 0f;
		var heights = shown.Select(e => Mathf.Max(S(86f), _foeWrapped.CalcHeight(new GUIContent(EncounterTitle(e)), rowWidth - S(24f)) + S(42f))).ToArray();
		_foeEncounterScroll = GUI.BeginScrollView(box, _foeEncounterScroll, new Rect(0, 0, rowWidth, Mathf.Max(box.height, heights.Sum() + shown.Count * S(6f))));
		for (int i = 0; i < shown.Count; i++)
		{
			DuelEncounter encounter = shown[i];
			Rect row = new Rect(0, y, rowWidth, heights[i]);
			bool on = encounter == _foeEncounter;
			if (GUI.Button(row, GUIContent.none, on ? _foeRowOn : _foeRow))
			{
				_foeEncounter = encounter;
				_foeStatsScroll = Vector2.zero;
				_foePickEncounter = false;
			}
			Color previous = _foeWrapped.normal.textColor;
			_foeWrapped.normal.textColor = on ? _foeNameOn.normal.textColor : PanelTheme.Ink;
			GUI.Label(new Rect(S(12f), y + S(7f), rowWidth - S(24f), heights[i] - S(35f)), EncounterTitle(encounter), _foeWrapped);
			_foeWrapped.normal.textColor = previous;
			GUI.Label(new Rect(S(12f), row.yMax - S(28f), rowWidth - S(24f), S(24f)),
				string.IsNullOrEmpty(encounter.Id) ? DuelText(encounter.StatId.Replace("Enemy_", "")) : encounter.Id, _foeSmall);
			y += heights[i] + S(6f);
		}
		GUI.EndScrollView();
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
		var selector = new Rect(area.x + S(14f), area.y + S(10f), area.width - S(28f), S(62f));
		if (_foeEncounter != null)
		{
			int current = _foeEncounters.IndexOf(_foeEncounter);
			if (GUI.Button(new Rect(selector.x, selector.y, S(36f), selector.height), "‹", _track))
				SelectFoeEncounter((current + _foeEncounters.Count - 1) % _foeEncounters.Count);
			var chooseStyle = new GUIStyle(_track) { wordWrap = true, fontSize = Mathf.RoundToInt(S(18f)) };
			if (GUI.Button(new Rect(selector.x + S(42f), selector.y, selector.width - S(84f), selector.height),
				EncounterTitle(_foeEncounter) + "  ▾", chooseStyle))
				_foePickEncounter = !_foePickEncounter;
			if (GUI.Button(new Rect(selector.xMax - S(36f), selector.y, S(36f), selector.height), "›", _track))
				SelectFoeEncounter((current + 1) % _foeEncounters.Count);
		}
		var content = new Rect(area.x + S(14f), selector.yMax + S(10f), area.width - S(28f), area.yMax - selector.yMax - S(20f));
		if (_foePickEncounter)
		{
			DrawFoeEncounters(content);
			return;
		}
		GUILayout.BeginArea(content);
		_foeStatsScroll = GUILayout.BeginScrollView(_foeStatsScroll);
		if (_foeEncounter == null)
		{
			GUILayout.Label(_foeCatalogError ?? TextConverter.ForDisplay(_foeLoading ? "正在加载…" : "此立绘没有关联的决斗配置"), _foeWrapped);
		}
		else
		{
			DuelProfile profile = GalleryDuelCatalog.Instance.Profile(_foeEncounter.StatId);
			GUILayout.Label(DuelLocalized("CombatCharacter/Name/" + profile.CharacterId, profile.Name)
				+ (string.IsNullOrWhiteSpace(profile.Note) ? "" : " · " + DuelText(profile.Note)), _foeStatsHeading);
			if (_foeEncounter.Difficulty > 0)
				GUILayout.Label(string.Format(TextConverter.ForDisplay("关卡难度 {0}"), _foeEncounter.Difficulty), _foeWrapped);
			GUILayout.Space(S(10f));
			GUILayout.BeginHorizontal();
			if (GUILayout.Button(TextConverter.ForDisplay("普通"), _foeBrave ? _track : _foeSelectedButton, GUILayout.Height(S(40f)))) _foeBrave = false;
			if (GUILayout.Button(TextConverter.ForDisplay("勇者模式"), _foeBrave ? _foeSelectedButton : _track, GUILayout.Height(S(40f)))) _foeBrave = true;
			if (GUILayout.Button(TextConverter.ForDisplay("友善效果") + (_foeFriendly ? " ✓" : ""), _foeFriendly ? _foeSelectedButton : _track, GUILayout.Height(S(40f)))) _foeFriendly = !_foeFriendly;
			GUILayout.EndHorizontal();
			if (_foeBrave)
			{
				DuelNumberField("主角战斗六维合计", ref _foePlayerTotal);
				DuelNumberField("继承六维合计", ref _foeInheritedTotal);
			}
			int.TryParse(_foePlayerTotal, out int player);
			int.TryParse(_foeInheritedTotal, out int inherited);
			int[] preview = GalleryDuelDifficulty.Preview(profile, _foeBrave, _foeFriendly, player, inherited);
			GUILayout.Space(S(10f));
			bool sideBySide = content.width >= S(620f);
			float chartWidth = sideBySide ? Mathf.Min(S(370f), content.width * 0.52f) : Mathf.Min(S(400f), content.width - S(24f));
			float chartHeight = chartWidth * 500f / 440f;
			if (sideBySide) GUILayout.BeginHorizontal();
			GUILayout.BeginVertical(GUILayout.Width(chartWidth));
			DrawFoeRadar(preview, chartWidth, chartHeight);
			GUILayout.EndVertical();
			GUILayout.BeginVertical();
			GUILayout.Label(TextConverter.ForDisplay("角色技能"), _foeStatsHeading);
			if (profile.Skills.Count == 0) GUILayout.Label(TextConverter.ForDisplay("无固有技能"), _foeWrapped);
			foreach (DuelSkill skill in profile.Skills)
			{
				string title = DuelLocalized("PlayerTalent/Name/" + skill.Id, skill.Name);
				if (skill.DisplayLevel) title += "  Lv." + skill.Level;
				if (skill.Level <= 0) title += " · " + TextConverter.ForDisplay("未启用");
				GUILayout.Label(title, _foeStatsHeading);
				GUILayout.Label(DuelLocalized("PlayerTalent/Desc/" + skill.Id, skill.Description), _foeWrapped);
				GUILayout.Space(S(8f));
			}
			GUILayout.EndVertical();
			if (sideBySide) GUILayout.EndHorizontal();
			GUILayout.Label(string.Format(TextConverter.ForDisplay("血量 {0}/{1} · 气力 {2}/{3} · 嘴力 {4}"),
				profile.Health, profile.MaxHealth, profile.Energy, profile.MaxEnergy, profile.Talking), _foeWrapped);
			GUILayout.Label(TextConverter.ForDisplay("基础面板；切换难度可预览修正。灰色为 0–100，蓝色为超过 100 的部分。"), _foeWrapped);
			GUILayout.Space(S(12f));
			GUILayout.Label(TextConverter.ForDisplay("修正规则"), _foeStatsHeading);
			{
				GUILayout.Label(TextConverter.ForDisplay("预览只计算难度修正；未计入角色技能、剧情事件及其他 Mod。血量、气力、嘴力显示基础值。"), _foeWrapped);
				if (_foeBrave)
				{
				GUILayout.Label(TextConverter.ForDisplay("勇者模式：先加继承数值，再按敌我六维差距补强，并额外获得 50% 伤害减免。实际补强在技能生效后计算。"), _foeWrapped);
				GUILayout.Label(TextConverter.ForDisplay("游戏当前的追赶加成不增加拳掌，暗器只加一次；这里按游戏实际规则预览。"), _foeWrapped);
				}
				if (_foeFriendly)
					GUILayout.Label(TextConverter.ForDisplay("友善效果：敌方六维减半；与勇者模式可同时生效。"), _foeWrapped);
			}
			GUILayout.Space(S(12f));
			GUILayout.Label(TextConverter.ForDisplay("剧情状态变化"), _foeStatsHeading);
			if (_foeEncounter.Events.Count == 0)
				GUILayout.Label(TextConverter.ForDisplay("未记录额外状态事件"), _foeWrapped);
			foreach (DuelEvent change in _foeEncounter.Events)
			{
				string phase = change.Phase == "Start" ? "开场" : change.Phase == "Round" ? "回合开始" : change.Phase == "Action" ? "行动开始" : "行动结束";
				GUILayout.Label(TextConverter.ForDisplay(phase) + (string.IsNullOrEmpty(change.Trigger) ? "" : " · " + DuelText(change.Trigger)), _foeStatsHeading);
				if (change.Effect != null)
				{
					string target = change.Target == 1 ? "我方" : "敌方";
					GUILayout.Label(TextConverter.ForDisplay(target) + " · " + TextConverter.ForDisplay(change.Remove ? "移除" : "获得") + " · "
						+ DuelLocalized("CombatSkill/Name/" + change.Effect.Key, change.Effect.Name), _foeWrapped);
					GUILayout.Label(DuelLocalized("CombatSkill/Desc/" + change.Effect.Key, change.Effect.Description), _foeWrapped);
				}
				else GUILayout.Label(DuelText(change.Action), _foeWrapped);
				GUILayout.Space(S(8f));
			}
		}
		GUILayout.EndScrollView();
		GUILayout.EndArea();
	}

	private void SelectFoeEncounter(int index)
	{
		_foeEncounter = _foeEncounters[index];
		_foeStatsScroll = Vector2.zero;
		_foePickEncounter = false;
	}

	private void DrawFoeRadar(int[] values, float width, float height)
	{
		Rect rect = GUILayoutUtility.GetRect(width, height);
		if (Event.current.type != EventType.Repaint) return;
		_foeRadar ??= new GalleryDuelRadar();
		Texture texture = _foeRadar.Render(values);
		if (texture != null) GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, true);
		else if (_foeRadar.Error != null)
			GUI.Label(rect, TextConverter.ForDisplay("游戏六维图资源已变化，请更新资料"), _foeWrapped);
		// 游戏 StatChart 下六个标签的位置，和游戏图表同方向。
		Vector2[] positions = { new Vector2(0, 155.7f), new Vector2(143.2f, 78.3f), new Vector2(-139.7f, 79.8f),
			new Vector2(140.4f, -81.7f), new Vector2(-141.7f, -82.2f), new Vector2(0, -163.1f) };
		float factor = width / 440f;
		var label = new GUIStyle(_foeWrapped) { alignment = TextAnchor.MiddleCenter, fontSize = Mathf.Max(10, Mathf.RoundToInt(23f * factor)) };
		label.normal.textColor = new Color(0.95f, 0.92f, 0.85f);
		for (int i = 0; i < 6; i++)
		{
			Vector2 center = rect.center + new Vector2(positions[i].x, -positions[i].y) * factor;
			string number = values[i].ToString();
			GUI.Label(new Rect(center.x - 60f * factor, center.y - 34f * factor, 120f * factor, 68f * factor),
				TextConverter.ForDisplay(SixNames[i]) + "\n" + number, label);
		}
	}

	private void DuelNumberField(string label, ref string text)
	{
		GUILayout.BeginHorizontal();
		GUILayout.Label(TextConverter.ForDisplay(label), _foeWrapped);
		string next = GUILayout.TextField(text, 6, _field, GUILayout.Width(S(130f)), GUILayout.Height(S(36f)));
		if (next.Length == 0 || (int.TryParse(next, out int value) && value >= 0)) text = next;
		GUILayout.EndHorizontal();
	}
}
