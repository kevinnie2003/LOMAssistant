using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LOMAssistant.Features.Story;

/// <summary>养成菜单和地点入口。用剧本编号划定边界，名称只用于显示。</summary>
internal static class StoryPlaces
{
	private static readonly Regex SectionStart = new Regex(@"^section_\d+_free_start_\d+$", RegexOptions.IgnoreCase);
	private static readonly Dictionary<string, (string Key, string Name)> Returns = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
	{
		["section_01_free_start_01"] = ("MiddleMenu/SectionFree01", "崆峒日常"),
		["section_01_free_start_02"] = ("MiddleMenu/SectionFree01", "崆峒日常"),
		["section_02_free_start_01"] = ("MiddleMenu/SectionFree02", "青城日常"),
		["section_02_free_start_02"] = ("MiddleMenu/SectionFree02", "青城日常"),
		["ch2_4_3_break_01"] = ("", "离家出走"),
		["ch2_4_3_break_start_01"] = ("", "离家出走"),
		["ch3_6_1_break_01"] = ("MapPosition/Name/009", "江陵"),
		["ch3_6_1_break_start_01"] = ("MapPosition/Name/009", "江陵"),
		["ch6_4_3_break_01"] = ("MapPosition/Name/013", "锦香宫"),
		["ch6_4_3_break_start_01"] = ("MapPosition/Name/013", "锦香宫"),
		["S2503_break_01"] = ("MapPosition/Name/016", "雪山"),
		["S2503_break_start_01"] = ("MapPosition/Name/016", "雪山"),
		["section_01_free_02_start_001"] = ("Position/Name/Section_01_02", "夺魄森林")
	};

	// 只收地点入口，随机事件候选由现有事件选择窗负责，不在这里逐项注册。
	internal static readonly (string Script, string Key, string Name)[] Entries =
	{
		("section_01_free_01", "Position/Name/Section_01_01", "飞天殿"),
		("section_01_free_02", "Position/Name/Section_01_02", "夺魄森林"),
		("section_01_free_03", "Position/Name/Section_01_03", "铁拳巷"),
		("section_01_free_04", "Position/Name/Section_01_04", "玄功洞"),
		("section_01_free_05", "Position/Name/Section_01_05", "夺魄峰"),
		("section_01_free_06", "Position/Name/Section_01_06", "无色广场"),
		("section_01_free_08", "Story/O_Section_01_Free_Start_02_008", "鹤崖"),
		("section_02_free_01", "Position/Name/Section_02_01", "上风观"),
		("section_02_free_02", "Position/Name/Section_02_02", "上清宫"),
		("section_02_free_03", "Position/Name/Section_02_03", "青城山镇"),
		("ch2_4_3_break_start_01", "", "离家出走"),
		("ch3_6_1_break_start_01", "MapPosition/Name/009", "江陵"),
		("ch3_6_2", "Story/O_3_6_1_Break_Start_01_003", "出城冲锋"),
		("ch3_6_3", "Story/O_3_6_1_Break_Start_01_005", "庭院演武"),
		("ch3_6_4", "Story/O_3_6_1_Break_Start_01_006", "乘凉"),
		("ch6_4_3_break_start_01", "MapPosition/Name/013", "锦香宫"),
		("ch6_4_3_1", "Story/O_6_4_3_Break_01_002", "参观锦香宫"),
		("ch6_4_3_2", "Story/O_6_4_3_Break_01_003", "花音阁"),
		("ch6_4_3_3", "Story/O_6_4_3_Break_01_004", "舞影殿"),
		("ch6_4_3_4", "Story/O_6_4_3_Break_01_005", "挑战花中仙"),
		("S2503_break_start_01", "MapPosition/Name/016", "雪山"),
		("S2503_01", "Story/O_S2503_Break_01_003", "盖房子")
	};

	internal static bool IsReturn(string script) => !string.IsNullOrEmpty(script) && (Returns.ContainsKey(script) || SectionStart.IsMatch(script));

	internal static string Name(string key, string fallback) => key.Length == 0 ? fallback : StoryAtlas.LibraryText(key, fallback);

	internal static string ReturnText(string script, Func<string, string> format)
	{
		return Returns.TryGetValue(script ?? "", out var place)
			? string.Format(format("回到「{0}」"), format(Name(place.Key, place.Name))) : format("回到地图");
	}

	internal static string EntryPlace(string script)
	{
		var entry = Entries.FirstOrDefault(e => string.Equals(e.Script, script, StringComparison.OrdinalIgnoreCase));
		return entry.Script == null ? "" : Name(entry.Key, entry.Name);
	}

	// Story 选项键表示活动，不把“逛一逛”这样的完整选项当作地点名。
	internal static bool IsActivity(string script) => Entries.Any(e => string.Equals(e.Script, script, StringComparison.OrdinalIgnoreCase) && e.Key.StartsWith("Story/", StringComparison.Ordinal));

	private static readonly Dictionary<string, StoryScript> Views = new Dictionary<string, StoryScript>(StringComparer.OrdinalIgnoreCase);

	// 地点图在事件选择窗处收尾；裁剪显示副本，不改原始剧本和其他功能使用的索引。
	internal static StoryScript View(StoryScript script)
	{
		if (script == null) return null;
		if (Views.TryGetValue(script.Name, out var cached)) return cached;
		var random = new HashSet<string>(script.Checks.Where(c => c.Switch && SwitchRandom.IsRandom(c.Checkpoint)).Select(c => c.Checkpoint));
		if (random.Count == 0) return Views[script.Name] = script;
		bool Before(List<StoryStep> path) => !path.Any(p => p.Kind == StoryStep.Branch && random.Contains(p.Key));
		var view = new StoryScript
		{
			Name = script.Name, Talk = script.Talk.Where(t => Before(t.Path)).ToList(),
			Fights = script.Fights.Where(f => Before(f.Path)).ToList(), Endings = script.Endings.Where(e => Before(e.Path)).ToList(),
			Effects = script.Effects.Where(e => Before(e.Path)).ToList(), Records = script.Records
		};
		for (int i = 0; i < script.Menus.Count; i++)
		{
			var path = i < script.MenuPaths.Count ? script.MenuPaths[i] : new List<StoryStep>();
			if (!Before(path)) continue;
			view.Menus.Add(script.Menus[i]); view.MenuPaths.Add(path);
		}
		view.Exits.AddRange(script.Exits.Where(e => Before(e.Path)));
		foreach (var check in script.Checks.Where(c => c.Switch && random.Contains(c.Checkpoint) && Before(c.Path)))
			view.Exits.Add(new StoryExit { Path = check.Path, EventPool = "Switch:" + check.Checkpoint });
		view.Lines = view.Talk.Select(t => t.Key).Concat(view.Menus.SelectMany(m => m).Select(p => p.Key)).Distinct().ToList();
		view.Menu = view.Menus.Count > 0;
		view.Dice = view.Exits.Any(e => e.Path.Any(p => p.Kind == StoryStep.Dice));
		view.Fight = view.Fights.Count > 0;
		return Views[script.Name] = view;
	}

	/// <summary>补足地点直入剧情；沿用任务索引供当前情况、数值反查和流程图定位，只读，不创建游戏任务。</summary>
	internal static void AddStories(List<StoryMission> missions, Dictionary<string, StoryMission> owner)
	{
		Views.Clear();
		var starts = new HashSet<string>(missions.Select(m => m.StartScript).Concat(Entries.Select(e => e.Script)), StringComparer.OrdinalIgnoreCase);
		foreach (var entry in Entries)
		{
			if (owner.ContainsKey(entry.Script)) continue;
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var todo = new Queue<string>();
			todo.Enqueue(entry.Script);
			while (todo.Count > 0)
			{
				string name = todo.Dequeue();
				if (seen.Contains(name) || owner.ContainsKey(name) || (name != entry.Script && (starts.Contains(name) || IsReturn(name)))) continue;
				StoryScript script = View(StoryScripts.Get(name));
				if (script == null) continue;
				seen.Add(name);
				foreach (StoryExit exit in script.Exits.Where(e => !e.ToMap && !e.ToTitle && e.EventPool.Length == 0 && e.Target.Length > 0)) todo.Enqueue(StoryScripts.FlowTarget(exit));
			}
			var scripts = seen.Select(n => View(StoryScripts.Get(n))).ToList();
			if (!scripts.Any(s => s.Talk.Count > 0)) continue;
			var mission = new StoryMission
			{
				Key = "place|" + entry.Script, Series = "place|" + entry.Script, StartScript = entry.Script, PlaceEntry = true,
				Place = Name(entry.Key, entry.Name), SeriesTitle = Name(entry.Key, entry.Name),
				VariableTime = true, When = "时间不定", Scripts = seen.ToList(),
				Read = StoryScripts.ReadPercent(scripts.SelectMany(s => s.Lines))
			};
			missions.Add(mission);
			foreach (string name in seen) owner[name] = mission;
		}
	}
}
