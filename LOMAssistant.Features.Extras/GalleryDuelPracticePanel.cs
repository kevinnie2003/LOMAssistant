using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
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
	// 「从存档读取」的列表：打开时才读存档文件
	private bool _practiceSavePicker;
	private List<GalleryDuelPracticeSave> _practiceSaves;
	private string _practiceLayoutMessage;
	private readonly Dictionary<string, string> _practiceEdits = new Dictionary<string, string>(StringComparer.Ordinal);
	private readonly HashSet<string> _practiceInvalid = new HashSet<string>(StringComparer.Ordinal);
	private GUIStyle _practiceText, _practiceMuted, _practiceHeading, _practiceErrorStyle, _practiceCard, _practiceCardOn;
	private float _practiceStyleScale = -1f;
	private bool _practiceStyleDark;
	private static readonly string[] PracticePages = { "主角属性", "天赋与技能" };
	private static readonly string[] PracticeFourNames = { "性情", "处世", "道德", "修养" };
	private static readonly string[] PracticeMasteryNames = { "战术", "释学", "道学", "儒学", "形意" };

	private void PracticeStyles()
	{
		FoeStatsStyles();
		if (_practiceText != null && Mathf.Approximately(_practiceStyleScale, _scale) && _practiceStyleDark == PanelTheme.Dark)
		{
			return;
		}
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
		// 技能卡片：启用的描朱砂边
		int pad = Mathf.RoundToInt(12f * _scale);
		_practiceCard = new GUIStyle { normal = { background = PanelTheme.Framed(PanelTheme.Paper, PanelTheme.Edge) }, border = new RectOffset(2, 2, 2, 2), padding = new RectOffset(pad, pad, pad, pad) };
		_practiceCardOn = new GUIStyle(_practiceCard) { normal = { background = PanelTheme.Framed(PanelTheme.Paper, PanelTheme.Cinnabar) } };
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
		if (PanelTheme.UiScale <= 0f)
		{
			return;
		}
		Styles();
		int previousDepth = GUI.depth;
		bool enabled = GUI.enabled;
		try
		{
			GUI.depth = -10;
			string text = TextConverter.ForDisplay(_practiceReturning ? "正在返回…" : "结束练习，返回鉴赏");
			float width = Mathf.Min(Screen.width - S(24f), Mathf.Max(S(200f), _track.CalcSize(new GUIContent(text)).x + S(32f)));
			// 放左上角：右上角是敌人的血条
			var button = new Rect(S(12f), S(12f), width, S(40f));
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
			_practiceLayoutMessage = _practiceError ?? (_foeEncounter == null ? _foeCatalogError : PracticeValidation());
		}
		GUISkin previous = GUI.skin;
		GUI.skin = _foeStatsSkin;
		try
		{
			Fill(area, PanelTheme.PaperLight);
			float pad = S(16f);
			float gap = S(8f);
			float width = Mathf.Max(1f, area.width - pad * 2f);
			float x = area.x + pad;
			float y = area.y + pad;
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
			if (!_foePickEncounter && _foeEncounter != null)
			{
				string events = PracticeEventSummary(_foeEncounter);
				float eventsHeight = _practiceMuted.CalcHeight(new GUIContent(events), width);
				GUI.Label(new Rect(x, y, width, eventsHeight), events, _practiceMuted);
				y += eventsHeight + gap;
			}
			if (_foePickEncounter)
			{
				DrawFoeEncounters(new Rect(x, y, width, Mathf.Max(1f, area.yMax - pad - y)));
				return;
			}
			float tabWidth = (width - gap * (PracticePages.Length - 1)) / PracticePages.Length;
			for (int i = 0; i < PracticePages.Length; i++)
			{
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
				if (_practicePage == 0)
				{
					DrawPracticeAttributes(width - FoeGutter);
				}
				else
				{
					DrawPracticeSkills(width - FoeGutter);
				}
			}
			finally
			{
				GUILayout.EndScrollView();
				GUILayout.EndArea();
			}
		}
		finally
		{
			GUI.skin = previous;
		}
	}

	private string PracticeValidation()
	{
		// 存档保护或必需部分没装上：开始按钮点不了，其余对不上的只记日志、照常练习
		if (GalleryDuelPractice.Unavailable != null)
		{
			return TextConverter.ForDisplay(GalleryDuelPractice.Unavailable);
		}
		if (_foeEncounter == null)
		{
			return _foeCatalogError ?? TextConverter.ForDisplay("请选择有关联决斗配置的角色。");
		}
		if (_practiceInvalid.Count > 0)
		{
			return TextConverter.ForDisplay("请先修正标红的数值，再开始练习。");
		}
		if (!_practiceSettings.TryValidate(out string error))
		{
			return TextConverter.ForDisplay(error);
		}
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
			{
				_practiceError = TextConverter.ForDisplay(error ?? "练习启动失败，请重试。");
			}
		}
		catch (Exception e)
		{
			_practiceError = TextConverter.ForDisplay("练习启动失败，请重试。");
			FileLogger.LogWarning("[鉴赏] 决斗练习启动失败: " + e);
		}
		finally
		{
			_practiceStarting = false;
		}
	}

	private void DrawPracticeAttributes(float width)
	{
		GalleryDuelPracticeSettings settings = _practiceSettings;
		bool fromSave = settings.FromSave && settings.SaveBase != null;
		PracticeSection("主角来源");
		GUILayout.Label(fromSave ? string.Format(TextConverter.ForDisplay("以存档「{0}」为底：没改的按存档算，改过的项（红框）替换存档里的值。"), settings.SaveLabel)
			: TextConverter.ForDisplay("自己填数值，或者先从存档读取主角，再在这个基础上改。"), fromSave ? _practiceText : _practiceMuted);
		GUILayout.Space(S(8f));
		GUILayout.BeginHorizontal();
		if (GUILayout.Button(TextConverter.ForDisplay(_practiceSavePicker ? "收起" : "从存档读取"), _practiceSavePicker ? _trackOn : _track, GUILayout.Width(S(160f)), GUILayout.Height(S(36f))))
		{
			_practicePendingChange = () =>
			{
				_practiceSavePicker = !_practiceSavePicker;
				if (_practiceSavePicker)
				{
					LoadPracticeSaves();
				}
			};
		}
		if (fromSave && GUILayout.Button(TextConverter.ForDisplay("不用存档"), _track, GUILayout.Width(S(160f)), GUILayout.Height(S(36f))))
		{
			_practicePendingChange = () =>
			{
				// 数值留着当自定义起点
				_practiceSettings.SaveSlot = null;
				_practiceSettings.SaveLabel = null;
				_practiceSettings.SaveBase = null;
				_practiceSettings.CustomVitals = false;
				_practiceError = null;
			};
		}
		if (GUILayout.Button(TextConverter.ForDisplay("重置全部"), _track, GUILayout.Width(S(160f)), GUILayout.Height(S(36f))))
		{
			_practicePendingChange = ResetPracticeSettings;
		}
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
		if (_practiceSavePicker)
		{
			GUILayout.Space(S(10f));
			DrawPracticeSaves();
		}
		GUILayout.Space(S(18f));

		PracticeSection("生命与气力");
		if (fromSave)
		{
			GUILayout.BeginHorizontal();
			if (GUILayout.Button(TextConverter.ForDisplay("按存档计算"), settings.CustomVitals ? _track : _trackOn, GUILayout.Width(S(160f)), GUILayout.Height(S(36f))))
			{
				_practicePendingChange = () => SetPracticeVitals(false);
			}
			if (GUILayout.Button(TextConverter.ForDisplay("自己设定"), settings.CustomVitals ? _trackOn : _track, GUILayout.Width(S(160f)), GUILayout.Height(S(36f))))
			{
				_practicePendingChange = () => SetPracticeVitals(true);
			}
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			GUILayout.Space(S(8f));
		}
		var vitals = new List<PracticeField>();
		if (!fromSave || settings.CustomVitals)
		{
			vitals.Add(new PracticeField("MaxHealth", "血量上限", 1, 100000, x => x.MaxHealth, (x, v) => x.MaxHealth = v, false));
			vitals.Add(new PracticeField("Health", "当前血量", 1, Math.Max(1, settings.MaxHealth), x => x.Health, (x, v) => x.Health = v, false));
			vitals.Add(new PracticeField("MaxEnergy", "气力上限", 1, 1000, x => x.MaxEnergy, (x, v) => x.MaxEnergy = v, false));
			vitals.Add(new PracticeField("Energy", "当前气力", 0, Math.Max(1, settings.MaxEnergy), x => x.Energy, (x, v) => x.Energy = v, false));
		}
		else
		{
			GUILayout.Label(TextConverter.ForDisplay("血量和气力在进入决斗时按存档计算。"), _practiceMuted);
			GUILayout.Space(S(8f));
		}
		vitals.Add(new PracticeField("Talking", "嘴力", 0, 1000, x => x.Talking, (x, v) => x.Talking = v, true));
		DrawPracticeFields(width, vitals);
		GUILayout.Space(S(14f));

		PracticeSection("六维属性");
		DrawPracticeFields(width, Enumerable.Range(0, 6).Select(i => new PracticeField("Six" + i, SixNames[i], 0, 1000, x => x.Six[i], (x, v) => x.Six[i] = v, true)).ToList());
		GUILayout.Space(S(14f));
		PracticeSection("角色四维");
		DrawPracticeFields(width, Enumerable.Range(0, 4).Select(i => new PracticeField("Four" + i, PracticeFourNames[i], 0, 100, x => x.Four[i], (x, v) => x.Four[i] = v, true)).ToList());
		GUILayout.Space(S(14f));
		PracticeSection("学识与战术");
		if (fromSave)
		{
			// 存档主角的学识和战术由对应天赋决定，在天赋页调
			GUILayout.Label(TextConverter.ForDisplay("学识和战术由存档里的天赋决定，可以在「天赋与技能」里调整。"), _practiceMuted);
			return;
		}
		DrawPracticeFields(width, Enumerable.Range(0, 5).Select(i => new PracticeField("Mastery" + i, PracticeMasteryNames[i], 0, 100, x => x.Mastery[i], (x, v) => x.Mastery[i] = v, true)).ToList());
	}

	private sealed class PracticeField
	{
		internal readonly string Key, Name;
		internal readonly int Min, Max;
		internal readonly Func<GalleryDuelPracticeSettings, int> Get;
		internal readonly Action<GalleryDuelPracticeSettings, int> Set;
		// 以存档为底时，和存档不同就描红框
		internal readonly bool Compare;

		internal PracticeField(string key, string name, int min, int max, Func<GalleryDuelPracticeSettings, int> get, Action<GalleryDuelPracticeSettings, int> set, bool compare)
		{
			Key = key;
			Name = name;
			Min = min;
			Max = max;
			Get = get;
			Set = set;
			Compare = compare;
		}
	}

	/// <summary>数值格子：多栏排列，每格是名称、范围和加减。</summary>
	private void DrawPracticeFields(float width, List<PracticeField> fields)
	{
		int columns = width >= S(1100f) ? 4 : width >= S(820f) ? 3 : width >= S(520f) ? 2 : 1;
		float gap = S(10f);
		float cell = (width - gap * (columns - 1)) / columns;
		float inner = cell - _practiceCard.padding.horizontal;
		float height = _practiceCard.padding.vertical
			+ Mathf.Max(S(40f), _practiceText.CalcHeight(new GUIContent("Ag"), inner) + _practiceMuted.CalcHeight(new GUIContent("Ag"), inner) + S(6f));
		GalleryDuelPracticeSettings original = _practiceSettings.FromSave ? _practiceSettings.SaveBase : null;
		for (int i = 0; i < fields.Count; i += columns)
		{
			GUILayout.BeginHorizontal();
			for (int j = i; j < Math.Min(i + columns, fields.Count); j++)
			{
				if (j > i)
				{
					GUILayout.Space(gap);
				}
				PracticeField field = fields[j];
				int value = field.Get(_practiceSettings);
				bool changed = original != null && field.Compare && field.Get(original) != value;
				GUILayout.BeginVertical(changed ? _practiceCardOn : _practiceCard, GUILayout.Width(cell), GUILayout.Height(height));
				GUILayout.BeginHorizontal();
				GUILayout.BeginVertical();
				GUILayout.Label(TextConverter.ForDisplay(field.Name), _practiceInvalid.Contains(field.Key) ? _practiceErrorStyle : _practiceText);
				GUILayout.Label(field.Min + "–" + field.Max, _practiceMuted);
				GUILayout.EndVertical();
				GUILayout.FlexibleSpace();
				PracticeStepper(field.Key, ref value, field.Min, field.Max, GUILayout.Width(S(72f)));
				GUILayout.EndHorizontal();
				GUILayout.EndVertical();
				field.Set(_practiceSettings, value);
			}
			GUILayout.EndHorizontal();
			GUILayout.Space(gap);
		}
	}

	private void SetPracticeVitals(bool custom)
	{
		_practiceSettings.CustomVitals = custom;
		foreach (string key in new[] { "MaxHealth", "Health", "MaxEnergy", "Energy" })
		{
			_practiceEdits.Remove(key);
			_practiceInvalid.Remove(key);
		}
		_practiceError = null;
		GUI.FocusControl(null);
	}

	private void LoadPracticeSaves()
	{
		try
		{
			_practiceSaves = GalleryDuelPractice.ListSaves();
		}
		catch (Exception e)
		{
			_practiceSaves = new List<GalleryDuelPracticeSave>();
			FileLogger.LogWarning("[鉴赏] 决斗练习读取存档列表失败: " + e);
		}
	}

	private void DrawPracticeSaves()
	{
		GUILayout.Label(TextConverter.ForDisplay("只读取存档文件，不会改动存档。选一个存档，主角的数值和天赋会填成存档里的值。"), _practiceMuted);
		GUILayout.Space(S(8f));
		if (_practiceSaves == null || _practiceSaves.Count == 0)
		{
			GUILayout.Label(TextConverter.ForDisplay("没有找到手动存档。"), _practiceMuted);
			return;
		}
		foreach (GalleryDuelPracticeSave save in _practiceSaves)
		{
			bool chosen = save.Slot == _practiceSettings.SaveSlot;
			if (GUILayout.Button(save.Label + "\n" + save.Detail, chosen ? _trackOn : _track, GUILayout.Height(S(58f))))
			{
				GalleryDuelPracticeSave picked = save;
				_practicePendingChange = () => ApplyPracticeSave(picked);
			}
			GUILayout.Space(S(4f));
		}
	}

	private void ApplyPracticeSave(GalleryDuelPracticeSave save)
	{
		GalleryDuelPracticeSettings original = GalleryDuelPractice.ReadSaveBase(save.Slot, out string error);
		if (original == null)
		{
			_practiceError = TextConverter.ForDisplay(error);
			return;
		}
		_practiceSettings = original.Clone();
		_practiceSettings.SaveSlot = save.Slot;
		_practiceSettings.SaveLabel = save.Label;
		_practiceSettings.SaveBase = original;
		_practiceSavePicker = false;
		_practiceEdits.Clear();
		_practiceInvalid.Clear();
		_practiceError = null;
		GUI.FocusControl(null);
	}

	private void ResetPracticeSettings()
	{
		GalleryDuelPracticeSettings current = _practiceSettings;
		if (current.FromSave && current.SaveBase != null)
		{
			// 以存档为底时回到存档里的值
			_practiceSettings = current.SaveBase.Clone();
			_practiceSettings.SaveSlot = current.SaveSlot;
			_practiceSettings.SaveLabel = current.SaveLabel;
			_practiceSettings.SaveBase = current.SaveBase;
		}
		else
		{
			_practiceSettings = GalleryDuelPracticeSettings.CreateDefault();
		}
		_practiceEdits.Clear();
		_practiceInvalid.Clear();
		_practiceError = null;
		GUI.FocusControl(null);
	}

	/// <summary>− 输入框 +，放在调用方的横排里；返回输入是否有效。</summary>
	private bool PracticeStepper(string key, ref int value, int min, int max, GUILayoutOption fieldWidth)
	{
		bool enabled = GUI.enabled;
		GUI.enabled = enabled && value > min;
		bool less = GUILayout.Button("−", _track, GUILayout.Width(S(34f)), GUILayout.Height(S(34f)));
		GUI.enabled = enabled;
		if (!_practiceEdits.TryGetValue(key, out string draft))
		{
			draft = value.ToString(CultureInfo.InvariantCulture);
		}
		GUI.SetNextControlName("LOMAssistant_Practice_" + key);
		string next = GUILayout.TextField(draft, 8, _field, fieldWidth, GUILayout.Height(S(34f)));
		GUI.enabled = enabled && value < max;
		bool more = GUILayout.Button("+", _track, GUILayout.Width(S(34f)), GUILayout.Height(S(34f)));
		GUI.enabled = enabled;
		bool valid = int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= min && parsed <= max;
		if (valid)
		{
			value = parsed;
		}
		if (less || more)
		{
			value = Mathf.Clamp(value + (more ? 1 : -1), min, max);
			next = value.ToString(CultureInfo.InvariantCulture);
			valid = true;
			GUI.FocusControl(null);
		}
		if (next != draft || less || more)
		{
			_practiceError = null;
		}
		_practiceEdits[key] = next;
		if (valid)
		{
			_practiceInvalid.Remove(key);
		}
		else
		{
			_practiceInvalid.Add(key);
		}
		return valid;
	}

	private void DrawPracticeSkills(float width)
	{
		PracticeSection("天赋与技能");
		if (_practiceSettings.FromSave && _practiceSettings.SaveBase != null)
		{
			GUILayout.Label(string.Format(TextConverter.ForDisplay("已按存档「{0}」填好天赋等级，改过的会替换存档里的。"), _practiceSettings.SaveLabel), _practiceText);
		}
		GUILayout.Label(TextConverter.ForDisplay("等级 0 为不启用；上限按游戏原生天赋定义。高阶技能可能覆盖同系低阶技能。"), _practiceMuted);
		GUILayout.Space(S(10f));
		GUI.SetNextControlName("LOMAssistant_PracticeSkillSearch");
		_practiceSkillSearch = GUILayout.TextField(_practiceSkillSearch ?? "", _field, GUILayout.Height(S(38f)));
		GUILayout.Label(TextConverter.ForDisplay("搜索名称、说明或编号"), _practiceMuted);
		GUILayout.Space(S(8f));
		if (Event.current.type == EventType.Layout)
		{
			IReadOnlyList<GalleryDuelPracticeSkill> skills = GalleryDuelPractice.AvailableSkills;
			string query = TextConverter.ToSimplified((_practiceSkillSearch ?? "").Trim());
			_practiceShownSkills = skills.Where(s => (!_practiceOnlyEnabled || PracticeSkillLevel(s.Id) > 0 || _practiceInvalid.Contains("Skill:" + s.Id))
				&& (query.Length == 0 || TextConverter.ToSimplified(s.Name + " " + s.Description + " " + s.Id + " " + s.Key)
					.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
			_practiceSkillCount = skills.Count;
		}
		// 两个并排的筛选按钮，选中的那个是朱砂色；清空放在最右
		GUILayout.BeginHorizontal();
		int enabledCount = _practiceSettings.SkillLevels.Count(p => p.Value > 0);
		if (GUILayout.Button(string.Format(TextConverter.ForDisplay("全部 {0}"), _practiceSkillCount), _practiceOnlyEnabled ? _track : _trackOn, GUILayout.Width(S(150f)), GUILayout.Height(S(36f))))
		{
			_practicePendingChange = () => _practiceOnlyEnabled = false;
		}
		if (GUILayout.Button(string.Format(TextConverter.ForDisplay("已启用 {0}"), enabledCount), _practiceOnlyEnabled ? _trackOn : _track, GUILayout.Width(S(150f)), GUILayout.Height(S(36f))))
		{
			_practicePendingChange = () => _practiceOnlyEnabled = true;
		}
		GUILayout.FlexibleSpace();
		if (GUILayout.Button(TextConverter.ForDisplay("清空技能"), _track, GUILayout.Width(S(150f)), GUILayout.Height(S(36f))))
		{
			_practicePendingChange = () =>
			{
				_practiceSettings.SkillLevels.Clear();
				foreach (string key in _practiceEdits.Keys.Where(k => k.StartsWith("Skill:", StringComparison.Ordinal)).ToList())
				{
					_practiceEdits.Remove(key);
				}
				_practiceInvalid.RemoveWhere(k => k.StartsWith("Skill:", StringComparison.Ordinal));
				_practiceError = null;
				GUI.FocusControl(null);
			};
		}
		GUILayout.EndHorizontal();
		GUILayout.Space(S(12f));
		if (_practiceSkillCount == 0)
		{
			GUILayout.Label(TextConverter.ForDisplay("天赋资料尚未就绪，请稍后重试。"), _practiceMuted);
			return;
		}
		if (_practiceShownSkills.Count == 0)
		{
			GUILayout.Label(TextConverter.ForDisplay(_practiceOnlyEnabled ? "还没有启用的天赋。" : "没有符合条件的天赋。"), _practiceMuted);
			return;
		}
		int columns = width >= S(1050f) ? 3 : width >= S(680f) ? 2 : 1;
		float gap = S(10f);
		float cardWidth = (width - gap * (columns - 1)) / columns;
		float inner = cardWidth - _practiceCard.padding.horizontal;
		for (int i = 0; i < _practiceShownSkills.Count; i += columns)
		{
			// 同一行的卡片一样高：按这一行最长的说明算
			int end = Math.Min(i + columns, _practiceShownSkills.Count);
			float textHeight = 0f;
			for (int j = i; j < end; j++)
			{
				textHeight = Mathf.Max(textHeight, _practiceMuted.CalcHeight(new GUIContent(PracticeSkillDescription(_practiceShownSkills[j])), inner));
			}
			float header = Mathf.Max(S(42f), _practiceText.CalcHeight(new GUIContent("Ag"), inner) + _practiceMuted.CalcHeight(new GUIContent("Ag"), inner) + S(6f));
			float height = _practiceCard.padding.vertical + header + S(4f) + textHeight;
			GUILayout.BeginHorizontal();
			for (int j = i; j < end; j++)
			{
				if (j > i)
				{
					GUILayout.Space(gap);
				}
				DrawPracticeSkillCard(_practiceShownSkills[j], cardWidth, height);
			}
			GUILayout.EndHorizontal();
			GUILayout.Space(gap);
		}
	}

	private void DrawPracticeSkillCard(GalleryDuelPracticeSkill skill, float width, float height)
	{
		int level = PracticeSkillLevel(skill.Id);
		int max = Math.Min(10, skill.MaxLevel);
		string key = "Skill:" + skill.Id;
		GUILayout.BeginVertical(level > 0 ? _practiceCardOn : _practiceCard, GUILayout.Width(width), GUILayout.Height(height));
		GUILayout.BeginHorizontal();
		GUILayout.BeginVertical();
		GUILayout.Label(string.IsNullOrEmpty(skill.Name) ? skill.Id : skill.Name, _practiceInvalid.Contains(key) ? _practiceErrorStyle : _practiceText);
		GUILayout.Label(string.Format(TextConverter.ForDisplay("上限 {0}"), max), _practiceMuted);
		GUILayout.EndVertical();
		GUILayout.FlexibleSpace();
		PracticeStepper(key, ref level, 0, max, GUILayout.Width(S(52f)));
		GUILayout.EndHorizontal();
		GUILayout.Space(S(4f));
		GUILayout.Label(PracticeSkillDescription(skill), _practiceMuted);
		GUILayout.EndVertical();
		if (level > 0)
		{
			_practiceSettings.SkillLevels[skill.Id] = level;
		}
		else
		{
			_practiceSettings.SkillLevels.Remove(skill.Id);
		}
	}

	// 这里列的都是决斗天赋，说明开头的「[决斗]」标签行每张卡都一样，去掉
	private static readonly Regex TagLine = new Regex(@"^\[[^\]\n]{1,8}\]\s*\n");

	private static string PracticeSkillDescription(GalleryDuelPracticeSkill skill)
	{
		return TagLine.Replace((skill.Description ?? "").Trim(), "").Trim();
	}

	private int PracticeSkillLevel(string id) => _practiceSettings.SkillLevels.TryGetValue(id, out int level) ? level : 0;

	private void PracticeSection(string text)
	{
		GUILayout.Label(TextConverter.ForDisplay(text), _practiceHeading);
		GUILayout.Space(S(8f));
	}

	/// <summary>标题区下面的一行：这段剧情的决斗里会触发哪些事件。</summary>
	private string PracticeEventSummary(DuelEncounter encounter)
	{
		if (string.IsNullOrEmpty(encounter.Id))
		{
			return TextConverter.ForDisplay("独立配置没有剧情事件。");
		}
		var parts = new List<string>();
		foreach (DuelEvent change in encounter.Events ?? new List<DuelEvent>())
		{
			string phase = change.Phase == "Start" ? TextConverter.ForDisplay("开场") : DuelCondition(change.Trigger);
			if (string.IsNullOrEmpty(phase))
			{
				phase = TextConverter.ForDisplay(change.Phase == "Round" ? "回合开始" : change.Phase == "Action" ? "行动开始" : "行动结束");
			}
			string effect = change.Effect == null ? DuelAction(change.Action)
				: TextConverter.ForDisplay(change.Target == 1 ? "我方" : "敌方") + TextConverter.ForDisplay(change.Remove ? "失去" : "获得")
					+ DuelLocalized("CombatSkill/Name/" + change.Effect.Key, change.Effect.Name);
			string part = phase + " " + effect;
			if (!parts.Contains(part))
			{
				parts.Add(part);
			}
		}
		return parts.Count == 0 ? TextConverter.ForDisplay("这段剧情的决斗没有额外事件。")
			: TextConverter.ForDisplay("剧情事件照常触发：") + string.Join("；", parts);
	}

	/// <summary>由练习后端在接受启动后调用，沿用鉴赏的资源释放与音乐清理。</summary>
	internal static void HideForDuelPractice()
	{
		if (_instance == null)
		{
			return;
		}
		_instance._practiceReturnFolder = _instance._foe?.Folder;
		_instance._practiceReturnEncounter = _instance._foeEncounter?.Id;
		_instance._practiceReturnStat = _instance._foeEncounter?.StatId;
		GUIUtility.keyboardControl = 0;
		_instance.Close();
	}

	/// <summary>后端恢复临时状态、重新载入标题后调用；保留主角编辑值及对手剧情选择。</summary>
	internal static void RestoreAfterDuelPractice()
	{
		if (_instance == null)
		{
			return;
		}
		_instance._practiceStarting = false;
		_instance._practiceVisible = true;
		_instance._tab = FoesTab;
		_instance._practiceError = string.IsNullOrEmpty(GalleryDuelPractice.LastError) ? null : TextConverter.ForDisplay(GalleryDuelPractice.LastError);
		Open();
	}

	private void RestoreDuelPracticeSelection()
	{
		if (string.IsNullOrEmpty(_practiceReturnFolder) || _foes == null)
		{
			return;
		}
		Foe foe = _foes.FirstOrDefault(f => f.Folder == _practiceReturnFolder);
		if (foe != null)
		{
			SelectFoe(foe);
			_foeEncounter = _foeEncounters.FirstOrDefault(e => e.Id == _practiceReturnEncounter && e.StatId == _practiceReturnStat) ?? _foeEncounter;
		}
		_practiceReturnStat = null;
		_practiceReturnEncounter = null;
		_practiceReturnFolder = null;
	}
}
