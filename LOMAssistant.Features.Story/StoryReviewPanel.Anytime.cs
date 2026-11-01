using System.Collections.Generic;
using System.Linq;
using System.Text;
using LOMAssistant.Config;
using LOMAssistant.Features.AnytimeSave;
using UnityEngine;

namespace LOMAssistant.Features.Story;

/// <summary>
/// 「随时存档」页（F6）：存新档、读最近的、回退到判定前；下面按时间列出全部随时存档，每个可以读取、写备注、删除，
/// 鼠标停上去看存档时的上下文（游戏日期、所在任务、之前几句对白、做过的选择）。
/// </summary>
public partial class StoryReviewPanel
{
	private const int AnytimeTab = 4;
	private Vector2 _anytimeScroll;
	private string _noteFile, _noteText = "";
	private string _deleteFile;
	private float _deleteUntil;
	// 列表筛选（0 全部、1 剧情、2 养成、3 有备注）、折起来的月份、整组删除要点两下
	private int _anytimeFilter;
	private readonly HashSet<string> _anytimeFolded = new HashSet<string>();
	private string _deleteGroup;
	private float _deleteGroupUntil;
	private static readonly string[] AnytimeFilters = { "全部", "剧情", "养成", "有备注" };
	// 列表排法（ConfigManager.AnytimeSaveSort）：按存档栏位分组（不同周目、不同存档各在一组）、按存的时间、按游戏日期
	private static readonly string[] AnytimeSorts = { "按存档栏位", "按存的时间", "按游戏日期" };

	/// <summary>咖啡伴侣窗口开着吗（随时存档的提示开着窗口时不另外画）。</summary>
	public static bool IsOpen => Instance != null && Instance._visible;

	public static void Hide()
	{
		if (Instance != null)
		{
			Instance._visible = false;
		}
	}

	/// <summary>打开窗口并切到随时存档页。</summary>
	public static void ShowAnytime()
	{
		StoryReviewPanel panel = Instance;
		if (panel == null)
		{
			return;
		}
		panel._visible = true;
		StoryAtlas.RefreshProgress();
		panel._tab = AnytimeTab;
		panel._showChangelog = false;
	}

	/// <summary>F6：打开随时存档页；已经在这一页就关掉。</summary>
	public static void ToggleAnytime()
	{
		StoryReviewPanel panel = Instance;
		if (panel != null && panel._visible && panel._tab == AnytimeTab && !panel._showChangelog)
		{
			panel._visible = false;
			return;
		}
		ShowAnytime();
	}

	private void DrawAnytime(Rect r)
	{
		AnytimeSave.AnytimeSave save = AnytimeSave.AnytimeSave.Instance;
		if (save == null)
		{
			GUI.Label(new Rect(r.x, r.y + S(10), r.width, S(80)), AnytimeSave.AnytimeSave.Unavailable ?? "随时存档没有启用：在设置页「存档」里打开，重启游戏后生效。", _rich);
			return;
		}
		List<SaveSummary> saves;
		try
		{
			saves = save.Saves();
		}
		catch (System.Exception ex)
		{
			// 存档系统还没初始化（刚开游戏）时取不到存档目录
			GUI.Label(new Rect(r.x, r.y + S(10), r.width, S(40)), "暂时读不到随时存档：" + ex.Message, _rich);
			return;
		}
		// 上面一排：存、读最近的、回退；右边是状态
		float x = r.x, h = S(36);
		GUI.enabled = save.CanSaveNow;
		if (GUI.Button(new Rect(x, r.y, S(250), h), "存到新的随时存档（" + InputGuard.KeyName(ConfigManager.AnytimeSaveKey.Value) + "）", _primary))
		{
			save.SaveSlot();
		}
		x += S(260);
		GUI.enabled = !save.Busy && saves.Any(s => s.error == null);
		if (GUI.Button(new Rect(x, r.y, S(220), h), "读最近的（" + InputGuard.KeyName(ConfigManager.AnytimeLoadKey.Value) + "）", _button))
		{
			save.LoadLatest();
		}
		x += S(230);
		GUI.enabled = save.CanRollback;
		if (GUI.Button(new Rect(x, r.y, S(240), h), "回退到判定前（" + InputGuard.KeyName(ConfigManager.AnytimeRollbackKey.Value) + "）", _button))
		{
			save.Rollback();
		}
		x += S(250);
		GUI.enabled = true;
		if (AnytimeSave.AnytimeSave.Faulted)
		{
			if (GUI.Button(new Rect(x, r.y, S(130), h), "返回标题", _primary))
			{
				save.BackToTitle();
				_visible = false;
			}
			x += S(140);
		}
		GUI.Label(new Rect(x + S(10), r.y, r.xMax - x - S(10), h), save.Status(), Middle(_muted));
		GUI.Label(new Rect(r.x, r.y + S(42), r.width, S(28)),
			ConfigManager.AnytimeSaveLimit.Value > 0
				? $"共 {saves.Count} 个，最多保留 {ConfigManager.AnytimeSaveLimit.Value} 个，超过自动删最旧的，有备注的不删（设置页可改）。鼠标停在存档上看当时的剧情和对白。"
				: $"共 {saves.Count} 个，不自动删（设置页可设上限）。鼠标停在存档上看当时的剧情和对白。", _muted);

		// 筛选：全部 / 剧情 / 养成 / 有备注
		float fx = r.x;
		for (int i = 0; i < AnytimeFilters.Length; i++)
		{
			float w = S(96);
			if (GUI.Button(new Rect(fx, r.y + S(74), w, S(32)), AnytimeFilters[i], _anytimeFilter == i ? _primary : _button))
			{
				_anytimeFilter = i;
			}
			fx += w + S(8);
		}
		fx += S(24);
		int sort = Mathf.Clamp(ConfigManager.AnytimeSaveSort.Value, 0, AnytimeSorts.Length - 1);
		for (int i = 0; i < AnytimeSorts.Length; i++)
		{
			float w = S(130);
			if (GUI.Button(new Rect(fx, r.y + S(74), w, S(32)), AnytimeSorts[i], sort == i ? _primary : _button))
			{
				ConfigManager.AnytimeSaveSort.Value = sort = i;
			}
			fx += w + S(8);
		}
		List<SaveSummary> shown = saves.Where(s => _anytimeFilter == 0 || (_anytimeFilter == 1 && s.scene == "Story")
			|| (_anytimeFilter == 2 && s.scene == "Free") || (_anytimeFilter == 3 && !string.IsNullOrEmpty(s.note))).ToList();

		var area = new Rect(r.x, r.y + S(114), r.width, r.height - S(114));
		DragScroll("anytime", area, ref _anytimeScroll);
		GUILayout.BeginArea(area);
		_anytimeScroll = GUILayout.BeginScrollView(_anytimeScroll);
		if (saves.Count == 0)
		{
			GUILayout.Label("还没有随时存档。在剧情的台词、选项、掷骰时或养成界面按 " + InputGuard.KeyName(ConfigManager.AnytimeSaveKey.Value) + " 存一个。", _muted);
		}
		else if (shown.Count == 0)
		{
			GUILayout.Label("没有符合筛选的随时存档。", _muted);
		}
		// 分组（存档列表本来按存的时间从新到旧），每组可以折起来、整组删除
		foreach (var (key, label, items) in AnytimeGroups(shown, sort))
		{
			DrawAnytimeGroup(save, key, label, items);
			if (!_anytimeFolded.Contains(key))
			{
				foreach (SaveSummary s in items)
				{
					DrawAnytimeRow(save, s);
				}
			}
		}
		GUILayout.EndScrollView();
		GUILayout.EndArea();
	}

	/// <summary>存档时的游戏月份（「第2年　3月」，中间全角空格让两半各自翻译），分组用；没记下日期的归到一组。</summary>
	private static string AnytimeMonth(SaveSummary s)
	{
		var m = System.Text.RegularExpressions.Regex.Match(s.gameTime ?? "", @"^第(\d+)年(\d+)月");
		return m.Success ? "第" + m.Groups[1].Value + "年　" + m.Groups[2].Value + "月" : "日期不明";
	}

	/// <summary>
	/// 按排法分组，返回（折叠用的键、组名、组里的存档）。
	/// 按存档栏位：同一栏位的在一组，最近存过的组在上，组里按游戏日期从后往前；按存的时间：每天一组；按游戏日期：每个游戏月一组，从后往前。
	/// </summary>
	private static List<(string Key, string Label, List<SaveSummary> Items)> AnytimeGroups(List<SaveSummary> shown, int sort)
	{
		switch (sort)
		{
			case 1:
				return shown.GroupBy(s => s.savedAt.Length >= 10 ? s.savedAt.Substring(0, 10) : "日期不明")
					.Select(g => ("day:" + g.Key, g.Key, g.ToList())).ToList();
			case 2:
				return shown.OrderByDescending(AnytimeGameOrder).ThenByDescending(s => s.savedAt, System.StringComparer.Ordinal)
					.GroupBy(AnytimeMonth).Select(g => ("month:" + g.Key, g.Key, g.ToList())).ToList();
			default:
				return shown.GroupBy(s => s.slot ?? "")
					.Select(g => ("slot:" + g.Key, AnytimeSlotLabel(g.Key),
						g.OrderByDescending(AnytimeGameOrder).ThenByDescending(s => s.savedAt, System.StringComparer.Ordinal).ToList())).ToList();
		}
	}

	/// <summary>存档时的游戏日期排成一个数（年、月、上中下旬），比先后用；没记下日期的排最后。</summary>
	private static int AnytimeGameOrder(SaveSummary s)
	{
		var m = System.Text.RegularExpressions.Regex.Match(s.gameTime ?? "", @"^第(\d+)年(\d+)月(上|中|下)?");
		return m.Success ? int.Parse(m.Groups[1].Value) * 1000 + int.Parse(m.Groups[2].Value) * 10 + "上中下".IndexOf(m.Groups[3].Value.Length > 0 ? m.Groups[3].Value[0] : '上') : -1;
	}

	/// <summary>存档栏位的名字照游戏读档页的写法（「传奇018」）；从节点存档重玩的、以前没记栏位的另写。</summary>
	private static string AnytimeSlotLabel(string slot) =>
		slot == StoryNodes.ReplaySlot ? "从节点存档重玩" : string.IsNullOrEmpty(slot) ? "栏位不明" : LOMAssistant.Features.Extras.SaveBrowser.SlotLabel(slot);

	private void DrawAnytimeGroup(AnytimeSave.AnytimeSave save, string month, string title, List<SaveSummary> items)
	{
		bool folded = _anytimeFolded.Contains(month);
		GUILayout.BeginHorizontal();
		if (GUILayout.Button((folded ? "▶ " : "▼ ") + "<b>" + title + "</b>　<color=#73644F>" + items.Count + " 个存档</color>", _rich, Ctl()))
		{
			if (!_anytimeFolded.Remove(month))
			{
				_anytimeFolded.Add(month);
			}
		}
		GUILayout.FlexibleSpace();
		// 整组删除要点两下；有备注的留着
		int removable = items.Count(s => string.IsNullOrEmpty(s.note));
		GUI.enabled = removable > 0 && !save.Busy;
		bool confirm = _deleteGroup == month && Time.unscaledTime < _deleteGroupUntil;
		string deleteText = confirm ? "确定删除这 " + removable + " 个？" : "删这一组（有备注的不删）";
		GUIStyle deleteStyle = confirm ? _primary : _button;
		if (GUILayout.Button(deleteText, deleteStyle, GUILayout.Width(TextWidth(deleteText, deleteStyle, S(confirm ? 200 : 240))), Ctl()))
		{
			if (confirm)
			{
				_deleteGroup = null;
				foreach (SaveSummary s in items.Where(s => string.IsNullOrEmpty(s.note)).ToList())
				{
					save.Delete(s.file);
				}
			}
			else
			{
				_deleteGroup = month;
				_deleteGroupUntil = Time.unscaledTime + 3f;
			}
		}
		GUI.enabled = true;
		GUILayout.EndHorizontal();
	}

	private void DrawAnytimeRow(AnytimeSave.AnytimeSave save, SaveSummary s)
	{
		GUILayout.BeginHorizontal(_card);
		GUILayout.BeginVertical();
		string when = s.savedAt.Length >= 16 ? s.savedAt.Substring(5, 11) : s.savedAt;
		var head = new List<string> { s.scene == "Free" ? "养成" : s.scene == "Story" ? "剧情" : "" };
		if (s.gameTime.Length > 0)
		{
			head.Add(s.gameTime);
		}
		string mission = MissionName(s);
		if (mission.Length > 0)
		{
			head.Add(mission);
		}
		GUILayout.Label($"<b>{AnytimeSave.AnytimeSave.DisplayName(s)}</b>　<color=#73644F>{when}</color>　" + string.Join(" · ", head.Where(t => t.Length > 0).ToArray()), _rich);
		// 存档经过的剧本被游戏更新改过：重演会对不上，不让读
		bool changed = s.error == null && AnytimeSave.AnytimeSave.ChangedScript(s.scripts) != null;
		if (s.error != null)
		{
			GUILayout.Label("<color=#A3302A>读不了：" + s.error + "</color>", _small);
		}
		else if (changed)
		{
			GUILayout.Label("<color=#A3302A>游戏更新改过这个存档经过的剧本，读不了</color>", _small);
		}
		else if (s.preview.Length > 0)
		{
			GUILayout.Label(Fit(s.preview, OneLine(_muted), S(900)), OneLine(_muted));
		}
		if (_noteFile == s.file)
		{
			GUILayout.BeginHorizontal();
			GUI.SetNextControlName("anytimeNote");
			_noteText = GUILayout.TextField(_noteText, 60, _field, GUILayout.Width(S(520)), Ctl());
			if (GUILayout.Button("保存备注", _primary, GUILayout.Width(S(110)), Ctl()))
			{
				save.SetNote(s.file, _noteText);
				_noteFile = null;
				GUIUtility.keyboardControl = 0;
			}
			if (GUILayout.Button("取消", _button, GUILayout.Width(S(70)), Ctl()))
			{
				_noteFile = null;
				GUIUtility.keyboardControl = 0;
			}
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
		}
		else if (!string.IsNullOrEmpty(s.note))
		{
			GUILayout.Label("<color=#A3302A>备注：</color>" + s.note, _rich);
		}
		GUILayout.EndVertical();
		GUILayout.FlexibleSpace();
		if (_noteFile != s.file && GUILayout.Button(string.IsNullOrEmpty(s.note) ? "备注" : "改备注", _button, GUILayout.Width(S(90)), Ctl()))
		{
			_noteFile = s.file;
			_noteText = s.note ?? "";
			GUI.FocusControl("anytimeNote");
		}
		GUI.enabled = s.error == null && !changed && !save.Busy;
		if (GUILayout.Button("读取", _primary, GUILayout.Width(S(80)), Ctl()))
		{
			save.LoadFile(s.file);
		}
		GUI.enabled = true;
		// 删除要点两下
		bool confirm = _deleteFile == s.file && Time.unscaledTime < _deleteUntil;
		if (GUILayout.Button(confirm ? "确定删除？" : "删除", confirm ? _primary : _button, GUILayout.Width(S(110)), Ctl()))
		{
			if (confirm)
			{
				_deleteFile = null;
				save.Delete(s.file);
			}
			else
			{
				_deleteFile = s.file;
				_deleteUntil = Time.unscaledTime + 3f;
			}
		}
		GUILayout.EndHorizontal();
		Hover(GUILayoutUtility.GetLastRect(), Cached(("anytime", s.file, s.note), () => AnytimeContext(s)));
	}

	/// <summary>存档时在哪个任务里（剧本读完以后才知道，没读完先空着）。</summary>
	private static string MissionName(SaveSummary s)
	{
		if (string.IsNullOrEmpty(s.script) || !StoryAtlas.Ready || !StoryAtlas.Owner.TryGetValue(s.script, out StoryMission m))
		{
			return "";
		}
		return (m.Main ? "主线「" : "支线「") + Short(m.Title, 14) + "」";
	}

	/// <summary>悬停提示：存档时的上下文。</summary>
	private static string AnytimeContext(SaveSummary s)
	{
		var text = new StringBuilder();
		text.Append(AnytimeSave.AnytimeSave.DisplayName(s)).Append("　存于 ").Append(s.savedAt);
		if (s.gameTime.Length > 0)
		{
			text.Append("\n游戏时间：").Append(s.gameTime);
		}
		string mission = MissionName(s);
		text.Append("\n").Append(s.scene == "Free" ? "养成界面" : "剧情").Append(mission.Length > 0 ? " · " + mission : "");
		if (s.preview.Length > 0)
		{
			text.Append("\n停在：").Append(s.preview);
		}
		if (s.lines.Count > 0)
		{
			text.Append("\n\n之前的对白：");
			foreach (string line in s.lines)
			{
				text.Append("\n　").Append(line.Length > 40 ? line.Substring(0, 40) + "…" : line);
			}
		}
		if (s.choices.Count > 0)
		{
			text.Append("\n\n这一段做过的选择：");
			foreach (string choice in s.choices)
			{
				text.Append("\n　「").Append(choice).Append("」");
			}
		}
		if (!string.IsNullOrEmpty(s.note))
		{
			text.Append("\n\n备注：").Append(s.note);
		}
		return text.ToString();
	}
}
