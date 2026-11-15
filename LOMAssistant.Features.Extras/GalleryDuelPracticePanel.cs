using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace LOMAssistant.Features.Extras;

/// <summary>鉴赏决斗练习的编辑器。所有输入只写临时设置，由后端建立隔离战斗。</summary>
public sealed partial class Gallery
{
	private bool _practiceVisible, _practiceStarting, _practiceOnlyEnabled, _practiceReturning;
	private int _practicePage;
	private string _practiceSkillSearch = "", _practiceError;
	private string _practiceReturnFolder, _practiceReturnEncounter, _practiceReturnStat;
	private Vector2 _practiceScroll;
	private GalleryDuelPracticeSettings _practiceSettings;
	private Action _practicePendingChange;
	private List<GalleryDuelPracticeSkill> _practiceShownSkills = new List<GalleryDuelPracticeSkill>();
	private int _practiceSkillCount;
	private DuelEncounter _practiceStoryEncounter;
	private string _practiceLayoutMessage;
	private readonly Dictionary<string, string> _practiceEdits = new Dictionary<string, string>(StringComparer.Ordinal);
	private readonly HashSet<string> _practiceInvalid = new HashSet<string>(StringComparer.Ordinal);
	private GUIStyle _practiceText, _practiceMuted, _practiceHeading, _practiceErrorStyle;
	private float _practiceStyleScale = -1f;
	private bool _practiceStyleDark;
	private static readonly string[] PracticePages = { "主角属性", "天赋与技能", "剧情说明" };
	private static readonly string[] PracticeFourNames = { "性情", "处世", "道德", "修养" };
	private static readonly string[] PracticeMasteryNames = { "战术", "释学", "道学", "儒学", "形意" };

	private void PracticeStyles()
	{
		FoeStatsStyles();
		if (_practiceText != null && Mathf.Approximately(_practiceStyleScale, _scale) && _practiceStyleDark == PanelTheme.Dark) return;
		_practiceStyleScale = _scale;
		_practiceStyleDark = PanelTheme.Dark;
		_practiceText = PanelTheme.Label(_scale, 18, PanelTheme.Ink);
		_practiceMuted = PanelTheme.Label(_scale, 16, PanelTheme.InkMuted);
		_practiceHeading = PanelTheme.Label(_scale, 22, PanelTheme.Ink);
		_practiceHeading.fontStyle = FontStyle.Bold;
		_practiceErrorStyle = PanelTheme.Label(_scale, 16, PanelTheme.Cinnabar);
		foreach (GUIStyle style in new[] { _practiceText, _practiceMuted, _practiceHeading, _practiceErrorStyle })
		{
			style.richText = false;
			style.margin = new RectOffset();
		}
	}

	private void ToggleDuelPractice()
	{
		_practiceVisible = !_practiceVisible;
		_practiceSettings ??= GalleryDuelPracticeSettings.CreateDefault();
		_foePickEncounter = false;
		_foeDragging = false;
		EndFoeResize();
		GUI.FocusControl(null);
	}

	private void DrawDuelPracticeReturn()
	{
		if (!GalleryDuelPractice.IsActive)
		{
			_practiceReturning = false;
			return;
		}
		if (PanelTheme.UiScale <= 0f) return;
		Styles();
		int previousDepth = GUI.depth;
		bool enabled = GUI.enabled;
		try
		{
			GUI.depth = -10;
			string text = TextConverter.ForDisplay(_practiceReturning ? "正在返回…" : "结束练习，返回鉴赏");
			float width = Mathf.Min(Screen.width - S(24f), Mathf.Max(S(200f), _track.CalcSize(new GUIContent(text)).x + S(32f)));
			var button = new Rect(Screen.width - width - S(12f), S(12f), width, S(40f));
			InputGuard.Report("GalleryDuelPracticeReturn", button);
			// 不报告整个屏幕，以便原生战斗继续接收输入。
			Fill(button, PanelTheme.Paper);
			GUI.enabled = enabled && !_practiceReturning;
			if (GUI.Button(button, text, _track))
			{
				_practiceReturning = true;
				StartCoroutine(AbortDuelPracticeNextFrame());
			}
		}
		finally
		{
			GUI.enabled = enabled;
			GUI.depth = previousDepth;
		}
	}

	private IEnumerator AbortDuelPracticeNextFrame()
	{
		yield return null;
		GalleryDuelPractice.Abort();
	}

	private void DrawDuelPractice(Rect area)
	{
		PracticeStyles();
		_practiceSettings ??= GalleryDuelPracticeSettings.CreateDefault();
		// 结构变化只在 Layout 生效，鼠标/键盘事件与 Repaint 沿用同一组 GUILayout 控件。
		if (Event.current.type == EventType.Layout)
		{
			Action pending = _practicePendingChange;
			_practicePendingChange = null;
			pending?.Invoke();
			_practiceStoryEncounter = _foeEncounter;
			_practiceLayoutMessage = _practiceError ?? (_foeEncounter == null ? _foeCatalogError : PracticeValidation());
		}
		GUISkin previous = GUI.skin;
		GUI.skin = _foeStatsSkin;
		try
		{
			Fill(area, PanelTheme.PaperLight);
			float pad = S(16f), gap = S(8f), width = Mathf.Max(1f, area.width - pad * 2f);
			float x = area.x + pad, y = area.y + pad;
			// 窄屏时标题、剧情选择和开始按钮分行，开始按钮始终在滚动区外。
			float startWidth = Mathf.Max(S(160f), _trackOn.CalcSize(new GUIContent(TextConverter.ForDisplay("开始练习"))).x + S(32f));
			bool wide = width >= S(620f);
			GUI.Label(new Rect(x, y, wide ? width - startWidth - gap : width, S(32f)), TextConverter.ForDisplay("自定义决斗"), _practiceHeading);
			var launch = new Rect(wide ? x + width - startWidth : x, wide ? y : y + S(42f), wide ? startWidth : width, S(42f));
			string validation = PracticeValidation();
			bool enabled = GUI.enabled;
			GUI.enabled = enabled && validation == null && !_practiceStarting && !GalleryDuelPractice.IsActive;
			if (GUI.Button(launch, TextConverter.ForDisplay(_practiceStarting ? "正在准备…" : "开始练习"), _trackOn))
			{
				_practiceStarting = true;
				_practiceError = null;
				// 不在 OnGUI 的 BeginArea / BeginScrollView 内关闭鉴赏。
				StartCoroutine(StartDuelPracticeNextFrame(_foeEncounter, _practiceSettings.Clone()));
			}
			GUI.enabled = enabled;
			y = launch.yMax + S(10f);
			string encounter = _foeEncounter == null ? TextConverter.ForDisplay("先从左侧选择角色")
				: FoeDisplay(_foe) + " · " + EncounterTitle(_foeEncounter);
			float encounterHeight = Mathf.Max(S(28f), _practiceText.CalcHeight(new GUIContent(encounter), width));
			GUI.Label(new Rect(x, y, width, encounterHeight), encounter, _practiceText);
			y += encounterHeight + gap;
			string choose = TextConverter.ForDisplay(_foePickEncounter ? "收起" : "选择剧情");
			float chooseWidth = Mathf.Min(width, Mathf.Max(S(130f), _track.CalcSize(new GUIContent(choose)).x + S(32f)));
			GUI.enabled = enabled && _foeEncounters.Count > 0 && !_practiceStarting;
			if (GUI.Button(new Rect(x, y, chooseWidth, S(36f)), choose, _foePickEncounter ? _trackOn : _track))
			{
				_foePickEncounter = !_foePickEncounter;
				GUI.FocusControl(null);
			}
			GUI.enabled = enabled;
			string code = _foeEncounter == null ? "" : string.IsNullOrEmpty(_foeEncounter.Id)
				? TextConverter.ForDisplay("独立配置") + " · " + _foeEncounter.StatId : _foeEncounter.Id;
			GUI.Label(new Rect(x + chooseWidth + gap, y, Mathf.Max(0f, width - chooseWidth - gap), S(36f)), code, _foeSubtitle);
			y += S(46f);
			if (_foePickEncounter)
			{
				DrawFoeEncounters(new Rect(x, y, width, Mathf.Max(1f, area.yMax - pad - y)));
				return;
			}
			float tabWidth = (width - gap * 2f) / 3f;
			for (int i = 0; i < PracticePages.Length; i++)
				if (GUI.Button(new Rect(x + i * (tabWidth + gap), y, tabWidth, S(40f)), TextConverter.ForDisplay(PracticePages[i]), _practicePage == i ? _trackOn : _track))
				{
					int page = i;
					_practicePendingChange = () =>
					{
						_practicePage = page;
						_practiceScroll = Vector2.zero;
						GUI.FocusControl(null);
					};
				}
			y += S(50f);
			var viewport = new Rect(x, y, width + FoeGutter, Mathf.Max(1f, area.yMax - pad - y));
			GUILayout.BeginArea(viewport);
			_practiceScroll = GUILayout.BeginScrollView(_practiceScroll, false, false);
			try
			{
				string message = _practiceLayoutMessage;
				if (!string.IsNullOrEmpty(message))
				{
					GUILayout.Label(message, _practiceErrorStyle);
					GUILayout.Space(gap);
				}
				GUILayout.Label(TextConverter.ForDisplay("练习不保存进度；结束或退出后返回鉴赏。"), _practiceMuted);
				GUILayout.Space(S(14f));
				if (_practicePage == 0) DrawPracticeAttributes(width - FoeGutter);
				else if (_practicePage == 1) DrawPracticeSkills(width - FoeGutter);
				else DrawPracticeStory();
			}
			finally
			{
				GUILayout.EndScrollView();
				GUILayout.EndArea();
			}
		}
		finally { GUI.skin = previous; }
	}

	private string PracticeValidation()
	{
		if (_foeEncounter == null) return _foeCatalogError ?? TextConverter.ForDisplay("请选择有关联决斗配置的角色。");
		if (_practiceInvalid.Count > 0) return TextConverter.ForDisplay("请先修正标红的数值，再开始练习。");
		if (!_practiceSettings.TryValidate(out string error)) return TextConverter.ForDisplay(error);
		return null;
	}

	private IEnumerator StartDuelPracticeNextFrame(DuelEncounter encounter, GalleryDuelPracticeSettings settings)
	{
		yield return null;
		// 用户在下一帧前关闭鉴赏或切换页面时取消排队的启动。
		if (!_open || _tab != FoesTab || !_practiceVisible)
		{
			_practiceStarting = false;
			yield break;
		}
		try
		{
			if (!GalleryDuelPractice.TryStart(encounter, settings, out string error))
				_practiceError = TextConverter.ForDisplay(error ?? "练习启动失败，请重试。");
		}
		catch (Exception e)
		{
			_practiceError = TextConverter.ForDisplay("练习启动失败，请重试。");
			FileLogger.LogWarning("[鉴赏] 决斗练习启动失败: " + e);
		}
		finally { _practiceStarting = false; }
	}

	private void DrawPracticeAttributes(float width)
	{
		PracticeSection("主角预设");
		DrawPracticePresets(width);
		GUILayout.Label(TextConverter.ForDisplay("预设会替换属性与技能；数值仅用于本次练习。"), _practiceMuted);
		GUILayout.Space(S(16f));
		PracticeSection("生命与气力");
		int health = _practiceSettings.Health, maxHealth = _practiceSettings.MaxHealth;
		int energy = _practiceSettings.Energy, maxEnergy = _practiceSettings.MaxEnergy;
		DrawPracticePair("MaxHealth", "血量上限", ref maxHealth, 1, 100000, "Health", "当前血量", ref health, 1, Math.Max(1, maxHealth), width);
		DrawPracticePair("MaxEnergy", "气力上限", ref maxEnergy, 1, 1000, "Energy", "当前气力", ref energy, 0, Math.Max(1, maxEnergy), width);
		_practiceSettings.Health = health;
		_practiceSettings.MaxHealth = maxHealth;
		_practiceSettings.Energy = energy;
		_practiceSettings.MaxEnergy = maxEnergy;
		PracticeNumber("Talking", "嘴力", ref _practiceSettings.Talking, 0, 1000);
		GUILayout.Space(S(14f));
		PracticeSection("六维属性");
		DrawPracticeArray("Six", SixNames, _practiceSettings.Six, 0, 1000, width);
		GUILayout.Space(S(14f));
		PracticeSection("角色四维");
		DrawPracticeArray("Four", PracticeFourNames, _practiceSettings.Four, 0, 100, width);
		GUILayout.Space(S(14f));
		PracticeSection("学识与战术");
		DrawPracticeArray("Mastery", PracticeMasteryNames, _practiceSettings.Mastery, 0, 100, width);
	}

	private void DrawPracticePresets(float width)
	{
		string[] names = { "均衡", "拳掌", "刀剑", "暗器", "重置全部" };
		int columns = width >= S(650f) ? 5 : width >= S(400f) ? 3 : 2;
		for (int i = 0; i < names.Length; i += columns)
		{
			GUILayout.BeginHorizontal();
			for (int j = i; j < Math.Min(i + columns, names.Length); j++)
				if (GUILayout.Button(TextConverter.ForDisplay(names[j]), _track, GUILayout.Height(S(36f))))
				{
					int preset = j;
					_practicePendingChange = () => ApplyPracticePreset(preset);
				}
			GUILayout.EndHorizontal();
			GUILayout.Space(S(6f));
		}
	}

	private void ApplyPracticePreset(int preset)
	{
		_practiceSettings = GalleryDuelPracticeSettings.CreateDefault();
		if (preset >= 1 && preset <= 3)
		{
			_practiceSettings.Six[3] = preset == 1 ? 100 : 30;
			_practiceSettings.Six[4] = preset == 2 ? 100 : 30;
			_practiceSettings.Six[5] = preset == 3 ? 100 : 30;
		}
		_practiceEdits.Clear();
		_practiceInvalid.Clear();
		_practiceError = null;
		GUI.FocusControl(null);
	}

	private void DrawPracticeArray(string prefix, string[] names, int[] values, int min, int max, float width)
	{
		for (int i = 0; i < values.Length; i += 2)
		{
			if (i + 1 < values.Length)
				DrawPracticePair(prefix + i, names[i], ref values[i], min, max, prefix + (i + 1), names[i + 1], ref values[i + 1], min, max, width);
			else PracticeNumber(prefix + i, names[i], ref values[i], min, max);
		}
	}

	private void DrawPracticePair(string key, string name, ref int value, int min, int max,
		string otherKey, string otherName, ref int other, int otherMin, int otherMax, float width)
	{
		bool columns = width >= S(650f);
		if (columns) GUILayout.BeginHorizontal();
		GUILayout.BeginVertical();
		PracticeNumber(key, name, ref value, min, max);
		GUILayout.EndVertical();
		if (columns) GUILayout.Space(S(18f));
		GUILayout.BeginVertical();
		// 上限刚被调小时，保留当前血量/气力并标错，避免静默改写用户输入。
		if (key == "MaxHealth" || key == "MaxEnergy") otherMax = Math.Max(1, value);
		PracticeNumber(otherKey, otherName, ref other, otherMin, otherMax);
		GUILayout.EndVertical();
		if (columns) GUILayout.EndHorizontal();
	}

	private void PracticeNumber(string key, string name, ref int value, int min, int max)
	{
		GUILayout.Label(TextConverter.ForDisplay(name), _practiceText);
		GUILayout.Space(S(4f));
		GUILayout.BeginHorizontal();
		bool enabled = GUI.enabled;
		GUI.enabled = enabled && value > min;
		bool less = GUILayout.Button("−", _track, GUILayout.Width(S(34f)), GUILayout.Height(S(34f)));
		GUI.enabled = enabled;
		if (!_practiceEdits.TryGetValue(key, out string draft)) draft = value.ToString(CultureInfo.InvariantCulture);
		GUI.SetNextControlName("LOMAssistant_Practice_" + key);
		string next = GUILayout.TextField(draft, 8, _field, GUILayout.MinWidth(S(50f)), GUILayout.Height(S(34f)));
		GUI.enabled = enabled && value < max;
		bool more = GUILayout.Button("+", _track, GUILayout.Width(S(34f)), GUILayout.Height(S(34f)));
		GUI.enabled = enabled;
		GUILayout.EndHorizontal();
		bool valid = int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= min && parsed <= max;
		if (valid) value = parsed;
		if (less || more)
		{
			value = Mathf.Clamp(value + (more ? 1 : -1), min, max);
			next = value.ToString(CultureInfo.InvariantCulture);
			valid = true;
			GUI.FocusControl(null);
		}
		if (next != draft || less || more) _practiceError = null;
		_practiceEdits[key] = next;
		if (valid) _practiceInvalid.Remove(key); else _practiceInvalid.Add(key);
		GUILayout.Label(string.Format(TextConverter.ForDisplay("范围 {0}–{1}"), min, max), valid ? _practiceMuted : _practiceErrorStyle);
		GUILayout.Space(S(10f));
	}

	private void DrawPracticeSkills(float width)
	{
		PracticeSection("天赋与技能");
		GUILayout.Label(TextConverter.ForDisplay("等级 0 为不启用；上限按游戏原生天赋定义。高阶技能可能覆盖同系低阶技能。"), _practiceMuted);
		GUILayout.Space(S(10f));
		GUI.SetNextControlName("LOMAssistant_PracticeSkillSearch");
		_practiceSkillSearch = GUILayout.TextField(_practiceSkillSearch ?? "", _field, GUILayout.Height(S(38f)));
		GUILayout.Label(TextConverter.ForDisplay("搜索名称、说明或编号"), _practiceMuted);
		GUILayout.Space(S(8f));
		bool narrow = width < S(430f);
		if (!narrow) GUILayout.BeginHorizontal();
		if (GUILayout.Button(TextConverter.ForDisplay(_practiceOnlyEnabled ? "仅看已启用" : "查看全部"), _practiceOnlyEnabled ? _trackOn : _track, GUILayout.Height(S(36f))))
			_practicePendingChange = () => _practiceOnlyEnabled = !_practiceOnlyEnabled;
		if (GUILayout.Button(TextConverter.ForDisplay("清空技能"), _track, GUILayout.Height(S(36f))))
		{
			_practicePendingChange = () =>
			{
				_practiceSettings.SkillLevels.Clear();
				foreach (string key in _practiceEdits.Keys.Where(k => k.StartsWith("Skill:", StringComparison.Ordinal)).ToList()) _practiceEdits.Remove(key);
				_practiceInvalid.RemoveWhere(k => k.StartsWith("Skill:", StringComparison.Ordinal));
				_practiceError = null;
				GUI.FocusControl(null);
			};
		}
		if (!narrow) GUILayout.EndHorizontal();
		GUILayout.Space(S(12f));
		if (Event.current.type == EventType.Layout)
		{
			IReadOnlyList<GalleryDuelPracticeSkill> skills = GalleryDuelPractice.AvailableSkills;
			string query = TextConverter.ToSimplified((_practiceSkillSearch ?? "").Trim());
			_practiceShownSkills = skills.Where(s => (!_practiceOnlyEnabled || PracticeSkillLevel(s.Id) > 0 || _practiceInvalid.Contains("Skill:" + s.Id))
				&& (query.Length == 0 || TextConverter.ToSimplified(s.Name + " " + s.Description + " " + s.Id + " " + s.Key)
					.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
			_practiceSkillCount = skills.Count;
		}
		GUILayout.Label(string.Format(TextConverter.ForDisplay("已启用 {0} 项 · 显示 {1} / {2}"), _practiceSettings.SkillLevels.Count(p => p.Value > 0), _practiceShownSkills.Count, _practiceSkillCount), _practiceMuted);
		GUILayout.Space(S(12f));
		if (_practiceSkillCount == 0) GUILayout.Label(TextConverter.ForDisplay("天赋资料尚未就绪，请稍后重试。"), _practiceMuted);
		else if (_practiceShownSkills.Count == 0) GUILayout.Label(TextConverter.ForDisplay("没有符合条件的天赋。"), _practiceMuted);
		foreach (GalleryDuelPracticeSkill skill in _practiceShownSkills)
		{
			int level = PracticeSkillLevel(skill.Id);
			PracticeNumber("Skill:" + skill.Id, string.IsNullOrEmpty(skill.Name) ? skill.Id : skill.Name, ref level, 0, Math.Min(10, skill.MaxLevel));
			if (level > 0) _practiceSettings.SkillLevels[skill.Id] = level;
			else _practiceSettings.SkillLevels.Remove(skill.Id);
			GUILayout.Label(skill.Id, _practiceMuted);
			if (!string.IsNullOrWhiteSpace(skill.Description)) GUILayout.Label(skill.Description, _practiceMuted);
			GUILayout.Space(S(10f));
		}
	}

	private int PracticeSkillLevel(string id) => _practiceSettings.SkillLevels.TryGetValue(id, out int level) ? level : 0;

	private void PracticeSection(string text)
	{
		GUILayout.Label(TextConverter.ForDisplay(text), _practiceHeading);
		GUILayout.Space(S(8f));
	}

	private void DrawPracticeStory()
	{
		PracticeSection("原生决斗与剧情事件");
		GUILayout.Label(TextConverter.ForDisplay("练习使用所选剧情的原生决斗关卡，保留开场、回合与行动中的剧情事件。事件可能改变双方属性、技能或状态，也可能强制结束战斗。"), _practiceText);
		GUILayout.Space(S(12f));
		GUILayout.Label(TextConverter.ForDisplay("自定义数值是主角的初始设置，剧情事件与天赋仍可能继续调整。选择不同剧情，即使对手相同，战斗规则也可能不同。"), _practiceMuted);
		GUILayout.Space(S(12f));
		GUILayout.Label(TextConverter.ForDisplay("独立配置没有关联剧情事件，使用该角色配置直接练习。"), _practiceMuted);
		GUILayout.Space(S(18f));
		PracticeSection("本次练习的剧情事件");
		if (_practiceStoryEncounter == null) return;
		GUILayout.Label(EncounterTitle(_practiceStoryEncounter), _practiceText);
		GUILayout.Label(_practiceStoryEncounter.Id ?? "", _practiceMuted);
		if (_practiceStoryEncounter.Events == null || _practiceStoryEncounter.Events.Count == 0)
			GUILayout.Label(TextConverter.ForDisplay("未记录额外状态事件"), _practiceMuted);
		else
		{
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (DuelEvent change in _practiceStoryEncounter.Events)
			{
				string phase = change.Phase == "Start" ? TextConverter.ForDisplay("开场") : DuelCondition(change.Trigger);
				if (string.IsNullOrEmpty(phase)) phase = TextConverter.ForDisplay(change.Phase == "Round" ? "回合开始" : change.Phase == "Action" ? "行动开始" : "行动结束");
				string effect = change.Effect == null ? DuelAction(change.Action)
					: TextConverter.ForDisplay(change.Target == 1 ? "我方" : "敌方") + " · "
						+ TextConverter.ForDisplay(change.Remove ? "失去" : "获得") + " · "
						+ DuelLocalized("CombatSkill/Name/" + change.Effect.Key, change.Effect.Name);
				if (!seen.Add(phase + "\n" + effect)) continue;
				GUILayout.Space(S(10f));
				GUILayout.Label(phase, _practiceText);
				GUILayout.Label(effect, _practiceMuted);
			}
		}
		GUILayout.Space(S(18f));
		PracticeSection("练习结束");
		GUILayout.Label(TextConverter.ForDisplay("胜负与退出都不会写入存档、推进剧情或保留奖励。返回鉴赏后可继续调整设置，再次挑战。"), _practiceText);
	}

	/// <summary>由练习后端在接受启动后调用，沿用鉴赏的资源释放与音乐清理。</summary>
	internal static void HideForDuelPractice()
	{
		if (_instance == null) return;
		_instance._practiceReturnFolder = _instance._foe?.Folder;
		_instance._practiceReturnEncounter = _instance._foeEncounter?.Id;
		_instance._practiceReturnStat = _instance._foeEncounter?.StatId;
		GUIUtility.keyboardControl = 0;
		_instance.Close();
	}

	/// <summary>后端恢复临时状态、重新载入标题后调用；保留主角编辑值及对手剧情选择。</summary>
	internal static void RestoreAfterDuelPractice()
	{
		if (_instance == null) return;
		_instance._practiceStarting = false;
		_instance._practiceVisible = true;
		_instance._tab = FoesTab;
		_instance._practiceError = string.IsNullOrEmpty(GalleryDuelPractice.LastError) ? null : TextConverter.ForDisplay(GalleryDuelPractice.LastError);
		Open();
	}

	private void RestoreDuelPracticeSelection()
	{
		if (string.IsNullOrEmpty(_practiceReturnFolder) || _foes == null) return;
		Foe foe = _foes.FirstOrDefault(f => f.Folder == _practiceReturnFolder);
		if (foe != null)
		{
			SelectFoe(foe);
			_foeEncounter = _foeEncounters.FirstOrDefault(e => e.Id == _practiceReturnEncounter && e.StatId == _practiceReturnStat) ?? _foeEncounter;
		}
		_practiceReturnFolder = _practiceReturnEncounter = _practiceReturnStat = null;
	}
}
