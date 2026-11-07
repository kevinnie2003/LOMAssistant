using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Story;

/// <summary>一个任务（主线一节或支线的一步）。</summary>
/// <summary>从别的任务的剧本接进这个任务：在哪个任务、哪个剧本，走过哪些分岔。</summary>
public sealed class StoryEntry
{
	public StoryMission From;
	public string Script = "";
	public List<StoryStep> Path = new List<StoryStep>();
}

public sealed class StoryMission
{
	public string Key = "";
	/// <summary>所属任务组（MissionData），主线为 Main。</summary>
	public string Series = "";
	public string SeriesTitle = "";
	public bool Main;
	/// <summary>地点直入剧情的只读索引，不是游戏注册的任务。</summary>
	public bool PlaceEntry;
	/// <summary>游戏数据里没启用、不会在地图上自己触发，只能由别的剧本直接接进来（襄阳行这类）。</summary>
	public bool Inactive;
	public string Desc = "";
	public string Place = "";
	public PositionType Position;
	/// <summary>仅供剧情图合并相关入口；不改变游戏任务及其判定顺序。</summary>
	public string FlowGroup = "";
	/// <summary>同一旬由前段推进的主线入口，只供剧情图说明，不改变剧本归属。</summary>
	public readonly List<StoryEntry> FlowEntries = new List<StoryEntry>();
	/// <summary>触发时间，按旬计；任意时间、每月一次的为 null。</summary>
	public int? From, To;
	/// <summary>仅供列表、标题、路线布局和合并入口使用的日期范围；不改写跳剧情所用的原始时间。</summary>
	public int? DisplayFrom, DisplayTo;
	public bool Periodic, VariableTime;
	public string When = "";
	public string StartScript = "";
	public ConditionResultItem[] Conditions = new ConditionResultItem[0];
	/// <summary>从开始剧本一路接下去、到回地图或别的任务为止的剧本。</summary>
	public List<string> Scripts = new List<string>();
	public int? Read;
	public bool Visited;
	public int Endings;
	/// <summary>别的任务的剧本里接进来的地方。</summary>
	public List<StoryEntry> Entries = new List<StoryEntry>();
	/// <summary>主线：剧本里明写的下一个主线任务（Mission("Main", …)），按日期往后的才算。</summary>
	public List<string> Next = new List<string>();
	/// <summary>路线图把一个主线任务按章节拆开时，后面几节各成一站：这些小站所属的任务；游戏数据里的任务是 null。</summary>
	public StoryMission PartOf;
	/// <summary>这个任务的剧本里第一条剧情记录，用作标题旁的一句话简介。</summary>
	public string Summary = "";
	public string Opening = "";

	/// <summary>优先任务说明和支线名称，缺名时沿用剧情记录或开场台词的简短摘要。</summary>
	public string Title => Desc.Length > 0 ? Desc : !Main && SeriesTitle.Length > 0 && SeriesTitle != Series ? SeriesTitle
		: Summary.Length > 0 ? (Summary.Length > 16 ? Summary.Substring(0, 16) + "…" : Summary) : Main ? ChapterOf(StartScript) ?? Key
		: Opening.Length > 0 ? (Opening.Length > 16 ? Opening.Substring(0, 16) + "…" : Opening) : SeriesTitle;

	/// <summary>
	/// 路线图、剧情图的标题：游戏没给编号的主线任务（「西武林盟」「集思广益」这类），前面按开始剧本补上「第几章-第几节」；
	/// 和名字之间用全角空格隔开，翻译时编号和任务名分开查表。
	/// </summary>
	public string NumberedTitle
	{
		get
		{
			string title = Title, number = Main && PartOf == null ? ChapterOf(StartScript) : null;
			return number == null || title.StartsWith(number, StringComparison.Ordinal) || title.Length > 0 && char.IsDigit(title[0]) ? title : number + "　" + title;
		}
	}

	private static readonly System.Text.RegularExpressions.Regex Chapter = new System.Text.RegularExpressions.Regex(@"^ch(\d+)_(\d+)");

	/// <summary>剧本「ch7_5_…」的章节编号「7-5」；序章（ch0）写「序-5」。不是章节剧本返回 null。</summary>
	public static string ChapterOf(string script)
	{
		var m = Chapter.Match(script ?? "");
		return m.Success ? (m.Groups[1].Value == "0" ? "序" : m.Groups[1].Value) + "-" + m.Groups[2].Value : null;
	}
}

/// <summary>会回到标题的剧本（结局、死亡），以及怎么走到。</summary>
/// <summary>一个结局（汗青书）或死亡（生死簿）条目，以及剧本里哪些地方会走到它。</summary>
public sealed class StoryEnding
{
	public string Id = "";
	public bool Death;
	public string Title = "";
	public string Desc = "";
	/// <summary>成就给新存档的奖励，一行；结局、死亡没有。</summary>
	public string Award = "";
	public LibraryItemData Item;
	/// <summary>游戏图鉴里的编号（汗青书「结局 N」、生死簿「N.」，按图鉴里的顺序从 1 数）；图鉴里没有的为 0。</summary>
	public int Number;
	public List<StoryEndingWay> Ways = new List<StoryEndingWay>();

	/// <summary>达成过没有：游戏自己的记录（全部周目）。</summary>
	public bool Achieved => Item != null && Item.Count > 0;
}

public sealed class StoryEndingWay
{
	public string Script = "";
	public List<StoryStep> Path = new List<StoryStep>();
	public StoryMission Mission;
	/// <summary>输掉这一场决斗才会触发。</summary>
	public bool LostDuel;
}

/// <summary>
/// 把全部剧本和任务读一遍，整理成时间线和结局速查。进游戏后在后台提前开始（或第一次打开时），分很多帧做完，不卡游戏；
/// 读过、走过的标记每次打开窗口按当前进度重算，面板上也可以重新生成。
/// </summary>
public static class StoryAtlas
{
	// 每帧最多花多少毫秒：平时在后台读，少占一点；窗口开着、有人在等时多占一点，读得快
	private const double FrameBudgetMs = 5;
	private const double HurryBudgetMs = 25;

	public static readonly List<StoryMission> Missions = new List<StoryMission>();
	public static readonly List<StoryEnding> Endings = new List<StoryEnding>();
	/// <summary>成就（风云史）：游戏里的全部条目，剧本里直接解锁的附上位置；其余由游戏按条件解锁，看说明。</summary>
	public static readonly List<StoryEnding> Achievements = new List<StoryEnding>();
	/// <summary>结局 CG（讲经堂的 CG 图鉴），附上剧本里打开它的地方。</summary>
	public static readonly List<StoryEnding> Cgs = new List<StoryEnding>();
	/// <summary>剧本 → 它属于哪个任务（从任务开始剧本一路接下去的那些）。</summary>
	public static readonly Dictionary<string, StoryMission> Owner = new Dictionary<string, StoryMission>(StringComparer.OrdinalIgnoreCase);
	/// <summary>全部台词（剧本、文字），搜索用；读完剧本以后在后台慢慢整理。</summary>
	private static readonly List<(string Script, string Text)> Dialogue = new List<(string, string)>();
	public static bool DialogueReady { get; private set; }
	private static bool _dialoguePhase;
	public static bool Ready { get; private set; }
	public static bool Building => _steps != null;
	public static string Status { get; private set; } = "";
	/// <summary>主线现在到哪一步（路线图上的「今」）：每次都读游戏当前的值，不用读剧本时存下的（游戏推进、换存档都要跟着变）。</summary>
	public static string CurrentMain => MissionManagerData.Instance?.Main?.CurrentKey ?? "";
	/// <summary>每次生成完加一，面板据此重新排版。</summary>
	public static int Version { get; private set; }

	private static readonly Dictionary<StoryMission, string> Conditions = new Dictionary<StoryMission, string>();

	/// <summary>条件文字里带当前值（读数值、随机值都有代价），每次打开面板清一次。</summary>
	public static void ForgetConditions() => Conditions.Clear();

	/// <summary>
	/// 按现在的进度重算每个任务「这一局走过」「对白读过多少」（不重读剧本）。打开窗口时调用：
	/// 读剧本时算的是那一刻的进度，之后游戏推进了、换了存档都要跟着变。
	/// </summary>
	public static void RefreshProgress()
	{
		if (!Ready)
		{
			return;
		}
		var visited = new HashSet<string>(PlayerStatManagerData.Instance?.StoryKeyList ?? new List<string>(), StringComparer.Ordinal);
		var progress = SeriesProgress();
		foreach (StoryMission mission in Missions)
		{
			var scripts = mission.Scripts.Select(n => mission.PlaceEntry ? StoryPlaces.View(StoryScripts.Get(n)) : StoryScripts.Get(n)).Where(s => s != null).ToList();
			mission.Read = StoryScripts.ReadPercent(scripts.SelectMany(s => s.Lines));
			mission.Visited = IsVisited(mission, scripts, visited, progress);
		}
		Version++; // 路线图、剧情图按新进度重画
	}

	// 支线现在进行到哪一步（存档里记着）：剧本一条剧情记录都不写的支线（同门关怀这类），只能靠它判断这一局做过没有
	private static Dictionary<string, string> SeriesProgress()
	{
		var progress = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (MissionData series in MissionManagerData.Instance?.GetFieldValue<SubMissionsData>("_subMissions")?.List ?? new List<MissionData>())
		{
			if (series != null && !progress.ContainsKey(series.Name))
			{
				progress[series.Name] = series.CurrentKey ?? "";
			}
		}
		return progress;
	}

	/// <summary>这一局走过没有：剧本写下的剧情记录在本局的剧情记录里；支线的剧本一条都不写时，看存档里这条支线的进度。</summary>
	private static bool IsVisited(StoryMission mission, List<StoryScript> scripts, HashSet<string> visited, Dictionary<string, string> progress)
	{
		if (scripts.Any(s => s.Records.Any(visited.Contains)))
		{
			return true;
		}
		return !mission.Main && !mission.PlaceEntry && !scripts.Any(s => s.Records.Count > 0) && progress.TryGetValue(mission.Series, out string current) && PassedInSeries(mission, current);
	}

	// 这一局已经过了这一步：整组支线已 clear（做完最后一步，剧本里会把任务组设成 clear），或者进度已经走到组里排在它后面的那一步
	private static bool PassedInSeries(StoryMission mission, string current)
	{
		if (current == "clear")
		{
			return true;
		}
		var steps = Missions.Where(m => m.Series == mission.Series).Select(m => m.Key).ToList();
		int at = steps.IndexOf(current), self = steps.IndexOf(mission.Key);
		return at >= 0 && self >= 0 && at > self;
	}

	private static IEnumerator _steps;

	public static void Start()
	{
		if (_steps != null)
		{
			return;
		}
		Ready = false;
		DialogueReady = false;
		_dialoguePhase = false;
		Status = "正在读取剧本";
		_steps = Build();
	}

	/// <summary>每帧调用：在时间预算内往下做几步；hurry=窗口开着在等，预算放大。</summary>
	public static void Tick(bool hurry = false)
	{
		if (_steps == null)
		{
			return;
		}
		var clock = Stopwatch.StartNew();
		double budget = hurry ? HurryBudgetMs : FrameBudgetMs;
		try
		{
			while (clock.Elapsed.TotalMilliseconds < budget)
			{
				if (!_steps.MoveNext())
				{
					if (_dialoguePhase)
					{
						_steps = null;
						DialogueReady = true;
						break;
					}
					Ready = true;
					Version++;
					Conditions.Clear();
					Status = "";
					// 剧本读完就能用了；台词索引接着在后台整理
					_dialoguePhase = true;
					_steps = BuildDialogue();
					break;
				}
			}
		}
		catch (Exception ex)
		{
			_steps = null;
			Status = "生成失败：" + ex.Message;
			FileLogger.LogWarning("[剧情回顾] 生成时间线失败: " + ex);
		}
	}

	private static IEnumerator Build()
	{
		var timer = Stopwatch.StartNew();
		Missions.Clear();
		Endings.Clear();
		Achievements.Clear();
		Cgs.Clear();
		// 全部剧本：游戏和咖啡伴侣都没更新时直接读上次存的解析结果（后台线程读）；否则读一遍，记下大概，文字马上放掉，解析完存起来
		var names = new List<string>();
		Status = "正在读取剧本";
		var cached = StoryScriptCache.Load();
		while (!cached.IsCompleted)
		{
			yield return null;
		}
		if (cached.Result != null && cached.Result.Count > 0)
		{
			foreach (StoryScript script in cached.Result)
			{
				StoryScripts.Remember(script);
				names.Add(script.Name);
			}
			FileLogger.Log($"[剧情回顾] 从缓存读了 {names.Count} 个剧本，用时 {timer.ElapsedMilliseconds} 毫秒");
		}
		else
		{
			TextAsset[] assets = Resources.LoadAll<TextAsset>("story/chinesetraditional");
			// 这一步没法拆到多帧里，记下用时（后台提前读时会在这里顿一下）
			FileLogger.Log($"[剧情回顾] 载入 {assets.Length} 个剧本文件用时 {timer.ElapsedMilliseconds} 毫秒");
			var parsed = new List<StoryScript>();
			for (int i = 0; i < assets.Length; i++)
			{
				TextAsset asset = assets[i];
				if (asset == null)
				{
					continue;
				}
				names.Add(asset.name);
				StoryScript script = StoryScripts.Parse(asset.name, ModScripts.Text(asset.name) ?? asset.text);
				StoryScripts.Remember(script);
				parsed.Add(script);
				Resources.UnloadAsset(asset);
				if (i % 20 == 0)
				{
					Status = $"正在读取剧本 {i}/{assets.Length}";
					yield return null;
				}
			}
			assets = null;
			foreach (string name in ModScripts.Added(new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)))
			{
				string text = ModScripts.Text(name);
				if (text != null)
				{
					names.Add(name);
					StoryScript script = StoryScripts.Parse(name, text);
					StoryScripts.Remember(script);
					parsed.Add(script);
				}
			}
			StoryScriptCache.Save(parsed);
		}
		yield return null;

		// 任务
		Status = "正在整理任务";
		MissionManagerData data = MissionManagerData.Instance;
		if (data == null)
		{
			Status = "读档进入游戏后再生成";
			yield break;
		}
		AddSeries(data.Main, true);
		SubMissionsData subs = data.GetFieldValue<SubMissionsData>("_subMissions");
		if (subs != null)
		{
			foreach (MissionData series in subs.List)
			{
				AddSeries(series, false);
			}
		}
		yield return null;

		// 每个任务的剧本：从开始剧本沿着「接到」走，遇到别的任务的开始剧本、回地图就停
		var starts = new HashSet<string>(Missions.Select(m => m.StartScript).Where(s => s.Length > 0), StringComparer.OrdinalIgnoreCase);
		var owner = new Dictionary<string, StoryMission>(StringComparer.OrdinalIgnoreCase);
		// 标题画面还没读档时，这一局走过的剧情记录是空的
		var visited = new HashSet<string>(PlayerStatManagerData.Instance?.StoryKeyList ?? new List<string>(), StringComparer.Ordinal);
		var progress = SeriesProgress();
		int done = 0;
		foreach (StoryMission mission in Missions)
		{
			if (mission.StartScript.Length == 0)
			{
				continue;
			}
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var todo = new Queue<string>();
			todo.Enqueue(mission.StartScript);
			while (todo.Count > 0 && seen.Count < 400)
			{
				string name = todo.Dequeue();
				if (seen.Contains(name) || (seen.Count > 0 && (starts.Contains(name) || StoryPlaces.IsReturn(name) || StoryPlaces.Entries.Any(e => string.Equals(e.Script, name, StringComparison.OrdinalIgnoreCase)))))
				{
					continue;
				}
				StoryScript script = StoryScripts.Get(name);
				if (script == null)
				{
					continue;
				}
				seen.Add(name);
				foreach (StoryExit exit in script.Exits)
				{
					if (StoryScripts.FlowTarget(exit).Length > 0 && !exit.ToMap && !exit.ToTitle && (exit.EventPool.Length == 0 || StoryScripts.Travels(exit)))
					{
						todo.Enqueue(StoryScripts.FlowTarget(exit));
					}
				}
			}
			mission.Scripts = seen.ToList();
			foreach (string name in mission.Scripts)
			{
				StoryScript s = StoryScripts.Get(name);
				if (s == null)
				{
					continue;
				}
				foreach (StoryExit exit in s.Exits)
				{
					if (exit.MainMission.Length > 0 && !mission.Next.Contains(exit.MainMission))
					{
						mission.Next.Add(exit.MainMission);
					}
				}
				if (mission.Summary.Length == 0)
				{
					mission.Summary = s.Records.Select(StoryScripts.Legend).FirstOrDefault(t => t.Length > 0) ?? "";
				}
			}
			var scripts = mission.Scripts.Select(StoryScripts.Get).Where(s => s != null).ToList();
			mission.Read = StoryScripts.ReadPercent(scripts.SelectMany(s => s.Lines));
			mission.Visited = IsVisited(mission, scripts, visited, progress);
			foreach (string name in mission.Scripts)
			{
				if (!owner.ContainsKey(name))
				{
					owner[name] = mission;
				}
			}
			if (++done % 10 == 0)
			{
				Status = $"正在整理任务 {done}/{Missions.Count}";
				yield return null;
			}
		}

		// 支线演到一半回地图、后面几段由地图事件接着演的（襄阳行这类），顺着剧本走不到：
		// 同一任务组编号开头、又没归到别的任务的剧本，也算这条支线的（台词搜索、读过多少、剧情图、走完接到的主线都算上）
		foreach (StoryMission mission in Missions.Where(m => !m.Main && m.Series.Length > 0))
		{
			var extra = names.Where(n => n.StartsWith(mission.Series + "_", StringComparison.OrdinalIgnoreCase) && !owner.ContainsKey(n) && !StoryPlaces.IsReturn(n)).ToList();
			if (extra.Count == 0)
			{
				continue;
			}
			foreach (string name in extra)
			{
				owner[name] = mission;
				mission.Scripts.Add(name);
				StoryScript s = StoryScripts.Get(name);
				if (s == null)
				{
					continue;
				}
				foreach (StoryExit exit in s.Exits)
				{
					if (exit.MainMission.Length > 0 && !mission.Next.Contains(exit.MainMission))
					{
						mission.Next.Add(exit.MainMission);
					}
				}
			}
			var all = mission.Scripts.Select(StoryScripts.Get).Where(s => s != null).ToList();
			mission.Read = StoryScripts.ReadPercent(all.SelectMany(s => s.Lines));
			mission.Visited = IsVisited(mission, all, visited, progress);
		}

		StoryPlaces.AddStories(Missions, owner);

		// 剧本归属，以及别的任务接进来的地方（支线怎么进入）
		Owner.Clear();
		foreach (var pair in owner)
		{
			Owner[pair.Key] = pair.Value;
		}
		foreach (var pair in owner)
		{
			StoryScript s = pair.Value.PlaceEntry ? StoryPlaces.View(StoryScripts.Get(pair.Key)) : StoryScripts.Get(pair.Key);
			if (s == null)
			{
				continue;
			}
			foreach (StoryExit exit in s.Exits)
			{
				if (exit.Target.Length == 0 || !owner.TryGetValue(StoryScripts.FlowTarget(exit), out StoryMission into) || into == pair.Value || into.Entries.Count >= 8
					|| into.Entries.Any(e => e.From == pair.Value && e.Script == pair.Key))
				{
					continue;
				}
				into.Entries.Add(new StoryEntry { From = pair.Value, Script = pair.Key, Path = exit.Path });
			}
		}
		// 没启用、也没有剧本接进来的支线是用不到的，去掉
		var unused = new HashSet<StoryMission>(Missions.Where(m => m.Inactive && m.Entries.Count == 0));
		Missions.RemoveAll(unused.Contains);
		foreach (string name in Owner.Where(p => unused.Contains(p.Value)).Select(p => p.Key).ToList())
		{
			Owner.Remove(name);
		}
		foreach (StoryMission m in Missions)
		{
			m.Entries.RemoveAll(e => unused.Contains(e.From));
		}
		yield return null;

		// 主线之间的连线：只留往后走的、指向真有的主线任务（共用剧本里会混进别处的跳转）
		var mains = Missions.Where(m => m.Main).ToDictionary(m => m.Key, StringComparer.Ordinal);
		foreach (StoryMission m in mains.Values)
		{
			m.Next = m.Next.Where(k => k != m.Key && mains.TryGetValue(k, out StoryMission t) && (m.From == null || t.From == null || t.From >= m.From)).ToList();
		}
		SetFlowEntries();

		// 结局速查：游戏的汗青书（结局）和生死簿（死亡）条目，加上剧本里哪些地方会触发它们
		Status = "正在整理结局";
		var byId = new Dictionary<string, StoryEnding>(StringComparer.Ordinal);
		LibrarySystem library = LibrarySystem.Instance;
		void AddItems(LibraryItemCollection collection, bool death)
		{
			if (collection == null || collection.List == null)
			{
				return;
			}
			// 编号和游戏图鉴（LibraryPanel）一样：列表里的位置 + 1
			for (int i = 0; i < collection.List.Count; i++)
			{
				LibraryItemData item = collection.List[i];
				if (item == null)
				{
					continue;
				}
				var ending = new StoryEnding { Id = item.name, Death = death, Item = item, Number = i + 1, Title = LibraryText(item.GetTitleKey(), item.name), Desc = LibraryText(item.GetDescKey(), "") };
				byId[(death ? "d" : "e") + item.name] = ending;
				Endings.Add(ending);
			}
		}
		if (library != null)
		{
			AddItems(library.EndGame, false);
			AddItems(library.Dead, true);
		}
		var achievementById = new Dictionary<string, StoryEnding>(StringComparer.Ordinal);
		if (library?.Achievement?.List != null)
		{
			foreach (LibraryItemData item in library.Achievement.List)
			{
				if (item == null)
				{
					continue;
				}
				string award = string.Join("；", LibraryText(item.GetAwardKey(), "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray());
				var achievement = new StoryEnding { Id = item.name, Item = item, Title = LibraryText(item.GetTitleKey(), item.name), Desc = LibraryText(item.GetDescKey(), ""), Award = award };
				achievementById[item.name] = achievement;
				Achievements.Add(achievement);
			}
		}
		var cgById = new Dictionary<string, StoryEnding>(StringComparer.Ordinal);
		if (library?.EndCg?.List != null)
		{
			for (int i = 0; i < library.EndCg.List.Count; i++)
			{
				LibraryItemData item = library.EndCg.List[i];
				if (item == null)
				{
					continue;
				}
				var cg = new StoryEnding { Id = item.name, Item = item, Number = i + 1, Title = LibraryText(item.GetTitleKey(), "CG " + (i + 1)), Desc = "" };
				cgById[item.name] = cg;
				Cgs.Add(cg);
			}
		}
		foreach (string name in names)
		{
			StoryScript script = StoryScripts.Get(name);
			if (script == null)
			{
				continue;
			}
			foreach (var (path, id) in script.Cgs)
			{
				if (cgById.TryGetValue(id, out StoryEnding cg))
				{
					owner.TryGetValue(name, out StoryMission m);
					cg.Ways.Add(new StoryEndingWay { Script = script.Name, Path = path, Mission = m });
				}
			}
			foreach (var (path, death, id) in script.Endings)
			{
				owner.TryGetValue(name, out StoryMission m);
				if (!byId.TryGetValue((death ? "d" : "e") + id, out StoryEnding ending))
				{
					ending = new StoryEnding { Id = id, Death = death, Title = (death ? "死亡 " : "结局 ") + id };
					byId[(death ? "d" : "e") + id] = ending;
					Endings.Add(ending);
				}
				ending.Ways.Add(new StoryEndingWay { Script = script.Name, Path = path, Mission = m });
				if (m != null && !death)
				{
					m.Endings++;
				}
			}
			foreach (var (path, id) in script.Achievements)
			{
				if (achievementById.TryGetValue(id, out StoryEnding achievement))
				{
					owner.TryGetValue(name, out StoryMission m);
					achievement.Ways.Add(new StoryEndingWay { Script = script.Name, Path = path, Mission = m });
				}
			}
			foreach (var (path, level) in script.Fights)
			{
				if (!ConditionBook.CombatDeaths.TryGetValue(level, out string id) || !byId.TryGetValue("d" + id, out StoryEnding ending))
				{
					continue;
				}
				owner.TryGetValue(name, out StoryMission m);
				ending.Ways.Add(new StoryEndingWay { Script = script.Name, Path = path, Mission = m, LostDuel = true });
			}
		}
		FileLogger.Log($"[剧情回顾] 时间线生成完毕：剧本 {names.Count}，任务 {Missions.Count}，结局 {Endings.Count}，用时 {timer.ElapsedMilliseconds} 毫秒");
	}

	/// <summary>一句台词的文字（简体、去掉格式），读不到返回空。</summary>
	public static string LineText(string key)
	{
		try
		{
			string text = LocalizationManager.Instance.LocaleResolver.GetStoryText(key);
			// 去掉格式标记：<color=…> 这种，还有 {size=50}…{/size} 这种
			return string.IsNullOrWhiteSpace(text) || text == key ? "" : TextConverter.ToSimplified(System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>|\{/?[a-zA-Z]+(=[^}]*)?\}", "")).Replace("\n", " ").Trim();
		}
		catch
		{
			return "";
		}
	}

	private static IEnumerator BuildDialogue()
	{
		Dialogue.Clear();
		int n = 0;
		foreach (string name in Owner.Keys.ToList())
		{
			StoryScript script = StoryScripts.Get(name);
			if (script == null)
			{
				continue;
			}
			foreach (string key in script.Lines)
			{
				string text = LineText(key);
				if (text.Length > 0)
				{
					Dialogue.Add((name, text));
				}
				if (++n % 200 == 0)
				{
					yield return null;
				}
			}
		}
		FileLogger.Log($"[剧情回顾] 台词索引：{Dialogue.Count} 句");
	}

	/// <summary>按台词搜：返回（剧本、包含这段话的片段），最多 max 条。搜索词全是英文字母时也按拼音首字母找。</summary>
	public static List<(string Script, string Snippet)> SearchDialogue(string query, int max)
	{
		var found = new List<(string, string)>();
		if (!DialogueReady || string.IsNullOrEmpty(query))
		{
			return found;
		}
		// 台词索引是简体，输入繁体时转成简体再搜
		query = TextConverter.ToSimplifiedUnlessJapanese(query);
		foreach (var (script, text) in Dialogue)
		{
			int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
			if (at < 0)
			{
				at = Pinyin.Find(text, query, cache: false);
			}
			if (at < 0)
			{
				continue;
			}
			int from = Math.Max(0, at - 10);
			found.Add((script, (from > 0 ? "…" : "") + text.Substring(from, Math.Min(40, text.Length - from))));
			if (found.Count >= max)
			{
				break;
			}
		}
		return found;
	}

	private static void AddSeries(MissionData series, bool main)
	{
		if (series == null)
		{
			return;
		}
		string title = TextConverter.ToSimplified(series.Name ?? "");
		string desc = TextConverter.ToSimplified(series.GetFieldValue<string>("_desc") ?? "");
		if (title.Length == 0 || title == series.name)
		{
			title = desc.Length > 0 ? FirstLine(desc, 16) : series.name;
		}
		foreach (MissionCheckData check in series.GetFieldValue<MissionCheckData[]>("_missions") ?? new MissionCheckData[0])
		{
			// 没启用的支线先留着，等算完入口再看有没有剧本接进来
			bool active = check != null && check.GetFieldValue<bool>("_active");
			if (check == null || (main && !active))
			{
				continue;
			}
			// 缺名的任务使用已核对的多语言标题，原始编号仍用于任务识别。
			string missionTitle = title;
			if (title == series.name && UiQuestNames.Titles.TryGetValue(check.StartScript ?? "", out string[] known)) missionTitle = known[0];
			var mission = new StoryMission
			{
				Key = check.Key,
				Series = series.name,
				SeriesTitle = main ? "主线" : missionTitle,
				Main = main,
				Desc = FirstLine(TextConverter.ToSimplified(UiQuestNames.Description(check.Key, check.GetFieldValue<string>("_desc")) ?? ""), 30),
				Place = PlaceName(check.Position),
				Position = check.Position,
				StartScript = check.StartScript ?? "",
				Opening = StoryScripts.Get(check.StartScript ?? "")?.Lines.Take(6).Select(LineText).FirstOrDefault(s => s.Length > 0) ?? "",
				Conditions = check.GetFieldValue<ConditionResultItem[]>("_conditions") ?? new ConditionResultItem[0],
				Inactive = !active
			};
			SetTime(mission, check);
			// 使用任务组 ID、地点枚举和原始时间规则，不按译名合并。
			if (!main && active)
			{
				string time = !check.GetFieldValue<bool>("_dayCheck") ? "any" : mission.From != null
					? mission.From + ":" + mission.To : "monthly:" + (int)check.GetFieldValue<MonthStageType>("_periodStage");
				mission.FlowGroup = Uri.EscapeDataString(series.name) + "|" + (int)check.Position + "|" + time + "|" + check.BeforeMainMission;
			}
			Missions.Add(mission);
		}
		GroupFlowEntries(series.name);
	}


	private static void SetTime(StoryMission mission, MissionCheckData check)
	{
		if (!check.GetFieldValue<bool>("_dayCheck"))
		{
			mission.When = "任意时间";
			SetDisplayTime(mission);
			return;
		}
		switch (check.GetFieldValue<TimeCheckType>("_timeCheckType"))
		{
			case TimeCheckType.特定:
				GameTime at = check.GetFieldValue<GameTime>("_specificTime");
				mission.From = mission.To = Rounds(at);
				mission.When = DateText(at);
				break;
			case TimeCheckType.區間:
				GameTime min = check.GetFieldValue<GameTime>("_minTime"), max = check.GetFieldValue<GameTime>("_maxTime");
				mission.From = Rounds(min);
				mission.To = Rounds(max);
				mission.When = DateText(min) + " ～ " + DateText(max);
				break;
			default:
				mission.Periodic = true;
				mission.When = "每月" + StageText((int)check.GetFieldValue<MonthStageType>("_periodStage"));
				break;
		}
		SetDisplayTime(mission);
	}

	// 从原始时间类型读取边界，不解析译文，也不执行条件或读取随机值。
	private static void SetDisplayTime(StoryMission mission)
	{
		var clock = PlayerStatManagerData.Instance?.GetFieldValue<GameTimeData>("_gameTimeData");
		var savedTimes = PlayerStatManagerData.Instance?.GetFieldValue<GameTimeDataCollection>("_timeCollection")?.List;
		StatValueReference Single(StatGroupVariable group)
		{
			var values = group?.GetFieldValue<List<StatValueReference>>("_value");
			var type = group != null ? group.GetFieldValue<StatGroupType>("_groupType") : default;
			return values?.Count == 1 && (type == StatGroupType.Sum || type == StatGroupType.Max || type == StatGroupType.Min || type == StatGroupType.Multiply) ? values[0] : null;
		}
		bool Current(StatValueReference value) => clock != null && value?.CheckType == StatCheckType.遊戲時間 && value.GetFieldValue<GameTimeData>("_gameTime") == clock;
		int? Fixed(StatValueReference value)
		{
			if (value?.CheckType == StatCheckType.常數) return value.GetFieldValue<int>("_constant") * (value.GetFieldValue<bool>("_negative") ? -1 : 1) - 1;
			var time = value?.CheckType == StatCheckType.遊戲時間 ? value.GetFieldValue<GameTimeData>("_gameTime") : null;
			// 存档中的约定日期会改变，不能把资源里的默认日期当成固定日期。
			return time == null || time == clock || savedTimes == null || savedTimes.Contains(time) ? (int?)null : Rounds(time.GameTime);
		}
		int min = mission.From ?? int.MinValue, max = mission.To ?? int.MaxValue;
		foreach (var condition in mission.Conditions.Where(c => c != null))
		{
			bool any = condition.GetFieldValue<LogicOperatorType>("_logicOp") == LogicOperatorType.OR;
			var items = condition.GetFieldValue<List<StatCompareItem>>("_items");
			if (items == null || items.Count == 0) continue;
			int lower = any ? int.MaxValue : int.MinValue, upper = any ? int.MinValue : int.MaxValue;
			foreach (var item in items)
			{
				int lo = int.MinValue, hi = int.MaxValue;
				var left = item != null ? Single(item.GetFieldValue<StatGroupVariable>("_value1")) : null;
				var right = item != null ? Single(item.GetFieldValue<StatGroupVariable>("_value2")) : null;
				if (Current(left) || Current(right))
				{
					bool reverse = !Current(left);
					int? at = Fixed(reverse ? left : right);
					var op = item.GetFieldValue<StatCompareType>("_compareOp");
					if (at == null || op == StatCompareType.NotEqual) mission.VariableTime = true;
					else if (op == StatCompareType.Equal) lo = hi = at.Value;
					else if (op == StatCompareType.Greater || op == StatCompareType.GreaterEqual)
					{
						if (reverse) hi = at.Value - (op == StatCompareType.Greater ? 1 : 0);
						else lo = at.Value + (op == StatCompareType.Greater ? 1 : 0);
					}
					else if (op == StatCompareType.Less || op == StatCompareType.LessEqual)
					{
						if (reverse) lo = at.Value + (op == StatCompareType.Less ? 1 : 0);
						else hi = at.Value - (op == StatCompareType.Less ? 1 : 0);
					}
				}
				// OR 只取所有分支共同的外界；非时间分支不应变成时间限制。
				lower = any ? Math.Min(lower, lo) : Math.Max(lower, lo);
				upper = any ? Math.Max(upper, hi) : Math.Min(upper, hi);
			}
			min = Math.Max(min, lower);
			max = Math.Min(max, upper);
		}
		mission.DisplayFrom = min == int.MinValue ? (int?)null : Math.Max(0, min);
		mission.DisplayTo = max == int.MaxValue ? (int?)null : max;
		if (min > max) mission.VariableTime = true;
		if (mission.DisplayFrom != null || mission.DisplayTo != null)
		{
			string dates = TimeRange(mission.DisplayFrom, mission.DisplayTo);
			mission.When = mission.Periodic ? dates + " · " + mission.When : dates;
		}
		else if (mission.VariableTime && !mission.Periodic) mission.When = "时间不定";
	}

	internal static string TimeRange(int? from, int? to) => from == null && to == null ? "任意时间"
		: from == to ? DateText(from.Value) : (from != null ? DateText(from.Value) : "") + " ～ " + (to != null ? DateText(to.Value) : "");

	// 同任务组、同地点且固定日期重叠的入口共用一张图；编号分节的支线保留各自地点行。
	private static void GroupFlowEntries(string series)
	{
		string Family(string id) => System.Text.RegularExpressions.Regex.Replace(id, @"^(S[0-9]+)_[0-9]+$", "$1");
		string family = Family(series);
		bool divided = family != series;
		foreach (var place in Missions.Where(m => (divided ? Family(m.Series) == family : m.Series == series) && !m.Main && !m.Inactive && !m.Periodic && !m.VariableTime
			&& (m.DisplayFrom != null || m.DisplayTo != null)).GroupBy(m => divided ? PositionType.無 : m.Position))
		{
			string group = "";
			int end = int.MinValue;
			foreach (var mission in place.OrderBy(m => m.DisplayFrom ?? int.MinValue).ThenBy(m => m.Key, StringComparer.Ordinal))
			{
				if (group.Length == 0 || (mission.DisplayFrom ?? int.MinValue) > end)
				{
					group = "range|" + Uri.EscapeDataString(family) + "|" + (int)place.Key + "|" + Uri.EscapeDataString(mission.Key);
					end = int.MinValue;
				}
				mission.FlowGroup = group;
				end = Math.Max(end, mission.DisplayTo ?? int.MaxValue);
			}
		}
	}

	// 无地点不等于独立自动触发：同旬推进的主线还需要前段指定任务 ID。
	private static void SetFlowEntries()
	{
		var dated = Missions.Where(m => !m.Inactive && !m.Periodic && !m.VariableTime && m.From != null && m.From == m.To).ToArray();
		var mains = dated.Where(m => m.Main).ToArray();
		foreach (StoryMission mission in Missions) mission.FlowEntries.Clear();
		foreach (StoryMission from in mains)
			foreach (string name in from.Scripts)
				foreach (StoryExit exit in StoryScripts.Get(name)?.Exits ?? new List<StoryExit>())
				{
					StoryMission into = mains.FirstOrDefault(m => m.Key == exit.MainMission && m != from && m.Position == PositionType.無 && m.From == from.From);
					if (into == null || into.FlowEntries.Any(e => e.Script == name && e.Path.Count == exit.Path.Count && StoryScripts.IsPrefix(e.Path, exit.Path))) continue;
					into.FlowEntries.Add(new StoryEntry { From = from, Script = name, Path = exit.Path });
				}
		// 同旬共用开场的任务保留各入口行；主线同章、同旬的后续段也合入，不按译名判断。
		string Chapter(string script) => System.Text.RegularExpressions.Regex.Match(script, @"^ch[0-9]+_[0-9]+_").Value;
		foreach (var start in dated.Where(m => m.StartScript.Length > 0).GroupBy(m => (m.Main, m.StartScript, m.From)).Where(g => g.Count() > 1))
		{
			var members = start.ToList();
			string chapter = Chapter(members[0].StartScript);
			for (int i = 0; i < members.Count; i++)
				foreach (StoryMission next in mains.Where(m => members[0].Main && !members.Contains(m) && chapter.Length > 0 && Chapter(m.StartScript) == chapter
					&& m.FlowEntries.Any(e => e.From == members[i]))) members.Add(next);
			foreach (StoryMission member in members) member.FlowGroup = "opening|" + Uri.EscapeDataString(members[0].Key);
		}
	}

	// GameTime 重载了 ==，和 null 比会抛异常，只能用 ReferenceEquals
	internal static int? Rounds(GameTime t) => ReferenceEquals(t, null) || t.Year <= 0 ? (int?)null : (t.Year - 1) * 36 + (t.Month - 1) * 3 + ((int)t.Stage - 1);

	public static string DateText(GameTime t) => ReferenceEquals(t, null) || t.Year <= 0 ? "时间不定" : $"第{t.Year}年{t.Month}月{StageText((int)t.Stage)}";

	public static string DateText(int rounds) => $"第{rounds / 36 + 1}年{rounds % 36 / 3 + 1}月{StageText(rounds % 3 + 1)}";

	public static StoryMission Find(string key) => Missions.FirstOrDefault(m => m.Key == key);

	/// <summary>地点名：游戏地图上显示的名字（Position/Name/Kitchen 这类，跟着游戏语言）；取不到用地点类型的名字。</summary>
	private static string PlaceName(PositionType position)
	{
		string name = position.ToString();
		if (name == "無")
		{
			return "";
		}
		try
		{
			// 地点类型上标着 [StringValue("Kitchen")]，就是本地化用的键
			object attribute = typeof(PositionType).GetField(name)?.GetCustomAttributes(false).FirstOrDefault(a => a.GetType().Name == "StringValueAttribute");
			string key = attribute?.GetType().GetProperties().Select(p => p.GetValue(attribute, null) as string).FirstOrDefault(v => !string.IsNullOrEmpty(v));
			if (key != null)
			{
				string text = LibraryText("Position/Name/" + key, "");
				if (text.Length > 0)
				{
					return text;
				}
			}
		}
		catch (Exception e)
		{
			FileLogger.Log("地点名取不到 " + name + ": " + e.Message);
		}
		return TextConverter.ToSimplified(name);
	}

	internal static string LibraryText(string key, string fallback)
	{
		try
		{
			string text = LocalizationManager.Instance.LocaleResolver.GetString(key);
			if (!string.IsNullOrWhiteSpace(text) && text != key)
			{
				return TextConverter.ToSimplified(System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "").Trim());
			}
		}
		catch
		{
		}
		return fallback;
	}

	public static string StageText(int stage) => stage == 1 ? "上旬" : stage == 2 ? "中旬" : "下旬";

	private static string FirstLine(string text, int length)
	{
		string first = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
		return first.Length > length ? first.Substring(0, length) + "…" : first;
	}

	/// <summary>任务的触发条件（各组都要满足），带当前值，满足绿、不满足红。</summary>
	public static string ConditionText(StoryMission mission)
	{
		if (Conditions.TryGetValue(mission, out string cached))
		{
			return cached;
		}
		var parts = new List<string>();
		foreach (ConditionResultItem item in mission.Conditions)
		{
			if (item == null)
			{
				continue;
			}
			string text = DataHelper.ExtractConditions(item, out _);
			if (text.Length > 0)
			{
				parts.Add(text);
			}
		}
		return Conditions[mission] = string.Join("\n且 ", parts.ToArray());
	}
}
