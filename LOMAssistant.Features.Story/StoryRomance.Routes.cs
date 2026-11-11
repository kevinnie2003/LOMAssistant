using System;
using System.Collections.Generic;
using System.Linq;
using Mortal.Core;
using Mortal.Story;

namespace LOMAssistant.Features.Story;

/// <summary>
/// 女主线（收集进度页）：每位的对白、结局归属和完成度。
/// 整个剧本算她的：她的戏（剧本里她说的话占四分之一以上，或所在个人事件里占五分之一以上），
/// 或只能从她这一支走进去。别的剧本里，走到之前要过她这一关的对白也算：分支判断的这一支、选项的解锁条件里
/// 要求她的好感（≥ 某值）、她的结缘旗标（认识、告白、攻略……）打开，或者心上人就是她。
/// </summary>
public static partial class StoryRomance
{
	/// <summary>游戏数据里写到她的汗青书条目和结局 CG（LibraryItemData 的名字），和剧本里算出来的合在一起。瑞杏、魏菊没有。</summary>
	private static readonly Dictionary<int, string[]> EndingIds = new Dictionary<int, string[]>
	{
		[0] = new[] { "20036", "40000" },
		[2] = new[] { "40003" },
		[3] = new[] { "20039", "40004" },
		[4] = new[] { "20038" },
		[5] = new[] { "20033", "20042", "40002" },
		[6] = new[] { "20040", "20054", "40005" },
		[8] = new[] { "20016", "40001" },
	};

	/// <summary>她这条线在一个任务里的对白。</summary>
	public sealed class RouteMission
	{
		public StoryMission Mission;
		public HashSet<string> Lines = new HashSet<string>(StringComparer.Ordinal);
		public int Read;
	}

	public sealed class Progress
	{
		/// <summary>有她的对白的任务，按触发时间；不属于任何任务的剧本（当门闲聊等）合在 Mission 为 null 的一项里，排最后。</summary>
		public List<RouteMission> Missions = new List<RouteMission>();
		public int Read, Lines;
		public List<StoryEnding> Endings = new List<StoryEnding>();
		public int EndingsDone => Endings.Count(e => e.Achieved);
		/// <summary>对白读过的比例（全部周目），0～100，向下取整；没有对白为 0。</summary>
		public int Percent => Lines == 0 ? 0 : (int)Math.Floor(100.0 * Read / Lines);
		public bool Done => Lines > 0 && Read == Lines && EndingsDone == Endings.Count;
	}

	private sealed class Route
	{
		public HashSet<string> Lines = new HashSet<string>(StringComparer.Ordinal);
		public List<RouteMission> Missions = new List<RouteMission>();
		public List<StoryEnding> Endings = new List<StoryEnding>();
	}

	private static readonly Dictionary<int, Route> Routes = new Dictionary<int, Route>();
	private static readonly Dictionary<int, Progress> ProgressCache = new Dictionary<int, Progress>();
	private static (int Atlas, int Gates) _routesVersion = (-1, -1);
	private static float _progressAt = -10f;

	/// <summary>
	/// 分支判断的每一支（"B|判断名|第几支"）、选项的解锁条件（"O|条件名"）以哪几位为前提，按 Lover 编号的位。
	/// 判断表只在剧情场景里加载：进剧情时算一遍存进 StoryReview/heroine-gates.json，在标题画面、地图上用存下的。
	/// </summary>
	private static Dictionary<string, int> _gates;
	private static int _gatesVersion;
	private static bool _gatesLoaded, _gatesFromGame;
	private static string GatesFile => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(StoryRomance).Assembly.Location) ?? ".", "StoryReview", "heroine-gates.json");

	private sealed class GateBook
	{
		public string Version = "";
		public Dictionary<string, int> Gates = new Dictionary<string, int>();
	}

	/// <summary>还没进过剧情场景、分支判断表一次都没拿到过：女主线没法统计。</summary>
	public static bool GatesMissing => LoadGates() == null;

	private static Dictionary<string, int> LoadGates()
	{
		if (!_gatesLoaded)
		{
			_gatesLoaded = true;
			try
			{
				if (System.IO.File.Exists(GatesFile))
				{
					GateBook book = Newtonsoft.Json.JsonConvert.DeserializeObject<GateBook>(System.IO.File.ReadAllText(GatesFile, System.Text.Encoding.UTF8));
					if (book?.Gates != null && book.Version == UnityEngine.Application.version)
					{
						_gates = book.Gates;
						_gatesVersion++;
					}
				}
			}
			catch (Exception e)
			{
				FileLogger.LogWarning("[女主线] 读取分支归属失败: " + e.Message);
			}
		}
		return _gates;
	}

	/// <summary>剧情场景建好时（判断表在手）算一遍每个判断、每个条件以谁为前提，存下来。每次启动游戏只算一次。</summary>
	internal static void RememberGates(SwitchResultConfig[] switches, ConditionResultConfig conditions)
	{
		if (_gatesFromGame || switches == null || conditions?.List == null)
		{
			return;
		}
		_gatesFromGame = true;
		try
		{
			var gates = new Dictionary<string, int>(StringComparer.Ordinal);
			foreach (SwitchResultConfig config in switches)
			{
				foreach (SwitchResultData data in config?.GetFieldValue<List<SwitchResultData>>("_list") ?? new List<SwitchResultData>())
				{
					// 按执行次数分支的没有条件
					if (data == null || data.GetFieldValue<bool>("_useCount"))
					{
						continue;
					}
					List<ConditionResultItem> items = data.GetFieldValue<List<ConditionResultItem>>("_items") ?? new List<ConditionResultItem>();
					for (int i = 0; i < items.Count; i++)
					{
						int mask = Mask(new[] { items[i] });
						if (mask != 0)
						{
							gates["B|" + data.name + "|" + (i + 1)] = mask;
						}
					}
				}
			}
			foreach (ConditionResultData data in conditions.List)
			{
				int mask = data == null ? 0 : Mask(data.GetFieldValue<List<ConditionResultItem>>("_items") ?? new List<ConditionResultItem>());
				if (mask != 0)
				{
					gates["O|" + data.name] = mask;
				}
			}
			_gates = gates;
			_gatesLoaded = true;
			_gatesVersion++;
			string file = GatesFile;
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
			System.IO.File.WriteAllText(file, Newtonsoft.Json.JsonConvert.SerializeObject(new GateBook { Version = UnityEngine.Application.version, Gates = gates }), System.Text.Encoding.UTF8);
			FileLogger.Log($"[女主线] 分支归属已记下：{gates.Count} 处");
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[女主线] 记分支归属失败: " + e);
		}
	}

	private static int Mask(IEnumerable<ConditionResultItem> items)
	{
		var list = items.ToList();
		int mask = 0;
		foreach (Heroine h in All)
		{
			if (list.Any(item => About(h, item)))
			{
				mask |= 1 << h.Lover;
			}
		}
		return mask;
	}

	/// <summary>她这条线的完成度（全部周目读过的对白、她的结局）；剧本还没读完、还没进过剧情场景返回 null。</summary>
	public static Progress ProgressOf(Heroine h)
	{
		if (!StoryAtlas.Ready || LoadGates() == null)
		{
			return null;
		}
		if (_routesVersion != (StoryAtlas.Version, _gatesVersion))
		{
			_routesVersion = (StoryAtlas.Version, _gatesVersion);
			Routes.Clear();
			ProgressCache.Clear();
			try
			{
				BuildRoutes();
			}
			catch (Exception e)
			{
				FileLogger.LogWarning("[女主线] 整理归属失败: " + e);
			}
		}
		// 读过几句每秒最多重算一次：对白很多，界面每帧要画好几遍
		if (UnityEngine.Time.unscaledTime - _progressAt > 1f)
		{
			_progressAt = UnityEngine.Time.unscaledTime;
			ProgressCache.Clear();
		}
		if (ProgressCache.TryGetValue(h.Lover, out Progress cached))
		{
			return cached;
		}
		Routes.TryGetValue(h.Lover, out Route route);
		route ??= new Route();
		ReadStorySystem read = ReadStorySystem.Instance;
		var progress = new Progress { Lines = route.Lines.Count, Endings = route.Endings };
		if (read != null)
		{
			progress.Read = route.Lines.Count(read.ExistKey);
			foreach (RouteMission m in route.Missions)
			{
				m.Read = m.Lines.Count(read.ExistKey);
			}
		}
		progress.Missions = route.Missions;
		ProgressCache[h.Lover] = progress;
		return progress;
	}

	private static void BuildRoutes()
	{
		var scripts = StoryScripts.All.ToList();
		var byName = new Dictionary<string, StoryScript>(StringComparer.OrdinalIgnoreCase);
		foreach (StoryScript s in scripts)
		{
			byName[s.Name] = s;
		}
		// 从别的剧本接进来的路：目标剧本 → （来源剧本，走到这个去处的分岔）
		var incoming = new Dictionary<string, List<(string From, List<StoryStep> Path)>>(StringComparer.OrdinalIgnoreCase);
		foreach (StoryScript s in scripts)
		{
			foreach (StoryExit exit in s.Exits)
			{
				if (exit.Target.Length == 0 || !byName.ContainsKey(exit.Target))
				{
					continue;
				}
				if (!incoming.TryGetValue(exit.Target, out var list))
				{
					incoming[exit.Target] = list = new List<(string, List<StoryStep>)>();
				}
				list.Add((s.Name, exit.Path));
			}
		}
		var starts = new HashSet<string>(StoryAtlas.Missions.Select(m => m.StartScript).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
		// 每个剧本、每个个人事件（S0014 这样的编号）里各人说了几句；主角和旁白不算
		var speakers = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
		var eventSpeakers = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
		void Count(Dictionary<string, Dictionary<string, int>> table, string key, string who)
		{
			if (!table.TryGetValue(key, out var counts))
			{
				table[key] = counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			}
			counts[who] = (counts.TryGetValue(who, out int n) ? n : 0) + 1;
		}
		foreach (StoryScript s in scripts)
		{
			string ev = EventOf(s.Name);
			foreach (var (_, who, _) in s.Talk)
			{
				if (who.Length == 0 || who.Equals("player", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				Count(speakers, s.Name, who);
				if (ev != null)
				{
					Count(eventSpeakers, ev, who);
				}
			}
		}
		double Share(Dictionary<string, Dictionary<string, int>> table, string key, HashSet<string> who)
		{
			if (key == null || !table.TryGetValue(key, out var counts))
			{
				return 0;
			}
			int all = counts.Values.Sum();
			return all == 0 ? 0 : (double)counts.Where(p => who.Contains(p.Key)).Sum(p => p.Value) / all;
		}
		var allSpeakers = speakers.Values.SelectMany(c => c.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

		foreach (Heroine h in All)
		{
			bool Gated(List<StoryStep> path) => path.Any(st => (StepMask(st) & (1 << h.Lover)) != 0);
			HashSet<string> codes = CodesOf(h, allSpeakers);

			// 她的戏：剧本里她说的话占（主角、旁白以外）四分之一以上，或这个个人事件里她的话占五分之一以上
			var whole = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (StoryScript s in scripts)
			{
				if (codes.Count > 0 && (Share(speakers, s.Name, codes) >= 0.25 || Share(eventSpeakers, EventOf(s.Name), codes) >= 0.2))
				{
					whole.Add(s.Name);
				}
			}
			// 只能从她这一支（她的好感、结缘旗标、心上人是她）走进去的剧本也整个算
			var gatedOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			bool changed = true;
			while (changed)
			{
				changed = false;
				foreach (var pair in incoming)
				{
					// 地图上能自己触发的任务开头不算：不经过她也走得到
					if (gatedOnly.Contains(pair.Key) || starts.Contains(pair.Key))
					{
						continue;
					}
					if (pair.Value.All(e => gatedOnly.Contains(e.From) || Gated(e.Path)))
					{
						gatedOnly.Add(pair.Key);
						changed = true;
					}
				}
			}
			whole.UnionWith(gatedOnly);

			var route = new Route();
			var perMission = new Dictionary<StoryMission, RouteMission>();
			var loose = new RouteMission();
			var endingIds = new HashSet<string>(EndingIds.TryGetValue(h.Lover, out string[] fixedIds) ? fixedIds : new string[0], StringComparer.Ordinal);
			foreach (StoryScript s in scripts)
			{
				bool all = whole.Contains(s.Name);
				IEnumerable<string> lines = all ? s.Lines : s.Talk.Where(t => Gated(t.Path)).Select(t => t.Key);
				var list = lines.Where(k => !string.IsNullOrEmpty(k)).ToList();
				if (list.Count > 0)
				{
					route.Lines.UnionWith(list);
					StoryAtlas.Owner.TryGetValue(s.Name, out StoryMission mission);
					RouteMission rm = loose;
					if (mission != null && !perMission.TryGetValue(mission, out rm))
					{
						perMission[mission] = rm = new RouteMission { Mission = mission };
					}
					rm.Lines.UnionWith(list);
				}
				foreach (var (path, death, id) in s.Endings)
				{
					if (!death && (all || Gated(path)))
					{
						endingIds.Add(id);
					}
				}
				foreach (var (path, id) in s.Cgs)
				{
					if (all || Gated(path))
					{
						endingIds.Add(id);
					}
				}
			}
			route.Missions = perMission.Values
				.OrderBy(x => x.Mission.From ?? int.MaxValue).ThenBy(x => x.Mission.Key, StringComparer.Ordinal).ToList();
			if (loose.Lines.Count > 0)
			{
				route.Missions.Add(loose);
			}
			foreach (string id in endingIds)
			{
				StoryEnding ending = StoryAtlas.Endings.FirstOrDefault(e => !e.Death && e.Id == id) ?? StoryAtlas.Cgs.FirstOrDefault(e => e.Id == id);
				if (ending != null)
				{
					route.Endings.Add(ending);
				}
			}
			route.Endings = route.Endings.OrderBy(e => StoryAtlas.Cgs.Contains(e) ? 1 : 0).ThenBy(e => e.Number).ToList();
			Routes[h.Lover] = route;
			FileLogger.Log($"[女主线] {h.Relation}：整个剧本 {whole.Count} 个，对白 {route.Lines.Count} 句，结局 {route.Endings.Count} 个");
		}
	}

	private static readonly System.Text.RegularExpressions.Regex EventName = new System.Text.RegularExpressions.Regex(@"^(S\d{4})_", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

	/// <summary>
	/// 剧本里她用的角色代号：好感类型上的代号（girl7），加上各时期的造型（girl7_3）——代号以它开头、游戏人名表里显示的名字和她一样的。
	/// 同一前缀也有别人（girl2_4 是叶云啾、girl6_3 是夏侯梅），所以要对名字。
	/// </summary>
	private static HashSet<string> CodesOf(Heroine h, IEnumerable<string> speakers)
	{
		var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string code = KeysOf(h.Relation).FirstOrDefault();
		if (string.IsNullOrEmpty(code))
		{
			return codes;
		}
		codes.Add(code);
		string CharacterName(string c)
		{
			try
			{
				string name = LocalizationManager.Instance?.LocaleResolver?.GetString("Character/" + c);
				return string.IsNullOrEmpty(name) || name.StartsWith("Character/", StringComparison.Ordinal) ? null : name.Trim();
			}
			catch
			{
				return null;
			}
		}
		string own = CharacterName(code);
		if (own == null)
		{
			return codes;
		}
		foreach (string c in speakers)
		{
			if (c.StartsWith(code + "_", StringComparison.OrdinalIgnoreCase) && CharacterName(c) == own)
			{
				codes.Add(c);
			}
		}
		return codes;
	}

	/// <summary>个人事件的编号（S0014_01_05 → S0014）；主线、闲聊等返回 null。</summary>
	private static string EventOf(string script)
	{
		var m = EventName.Match(script);
		return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
	}

	/// <summary>这一步（分支判断的一支、带解锁条件的选项）以哪几位为前提，按 Lover 编号的位。</summary>
	private static int StepMask(StoryStep st)
	{
		string key = st.Kind == StoryStep.Branch && st.Arm > 0 ? "B|" + st.Key + "|" + st.Arm
			: st.Kind == StoryStep.Option && st.Requirement.Length > 0 ? "O|" + st.Requirement
			: null;
		return key != null && _gates != null && _gates.TryGetValue(key, out int mask) ? mask : 0;
	}

	/// <summary>这组条件里有没有要求她的：好感 ≥ 某值、她的结缘旗标打开、心上人是她。</summary>
	private static bool About(Heroine h, ConditionResultItem item)
	{
		var compares = item?.GetFieldValue<List<StatCompareItem>>("_items");
		return compares != null && compares.Any(c => About(h, c));
	}

	private static bool About(Heroine h, StatCompareItem c)
	{
		StatValueReference a = Single(c.GetFieldValue<StatGroupVariable>("_value1"));
		StatValueReference b = Single(c.GetFieldValue<StatGroupVariable>("_value2"));
		StatCompareType op = c.GetFieldValue<StatCompareType>("_compareOp");
		if (a == null || b == null)
		{
			return false;
		}
		if (a.CheckType == StatCheckType.常數 && b.CheckType != StatCheckType.常數)
		{
			(a, b) = (b, a);
			op = Mirror(op);
		}
		if (b.CheckType != StatCheckType.常數 || a.GetFieldValue<bool>("_negative"))
		{
			return false;
		}
		int k;
		try
		{
			k = b.GetValue();
		}
		catch
		{
			return false;
		}
		switch (a.CheckType)
		{
			case StatCheckType.好感度:
				return a.GetFieldValue<RelationshipStat>("_relationship")?.Type == h.Relation && AtLeastOne(op, k);
			case StatCheckType.旗標:
				FlagData flag = a.GetFieldValue<FlagData>("_flag");
				return flag != null && h.Steps.Contains(flag.name) && AtLeastOne(op, k);
			case StatCheckType.角色數值:
				return a.GetFieldValue<GameStat>("_stat")?.StatType == GameStatType.愛人 && op == StatCompareType.Equal && k == h.Lover;
			default:
				return false;
		}
	}

	// 「值 op k」成立时值至少是 1（好感有了、旗标打开）
	private static bool AtLeastOne(StatCompareType op, int k)
	{
		switch (op)
		{
			case StatCompareType.Equal:
			case StatCompareType.GreaterEqual:
				return k >= 1;
			case StatCompareType.Greater:
				return k >= 0;
			default:
				return false;
		}
	}

	private static StatCompareType Mirror(StatCompareType op)
	{
		switch (op)
		{
			case StatCompareType.Less: return StatCompareType.Greater;
			case StatCompareType.LessEqual: return StatCompareType.GreaterEqual;
			case StatCompareType.Greater: return StatCompareType.Less;
			case StatCompareType.GreaterEqual: return StatCompareType.LessEqual;
			default: return op;
		}
	}

	private static StatValueReference Single(StatGroupVariable group)
	{
		var refs = group?.GetFieldValue<List<StatValueReference>>("_value");
		return refs != null && refs.Count == 1 ? refs[0] : null;
	}
}
