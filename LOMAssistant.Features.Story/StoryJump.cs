using System;
using System.Collections.Generic;
using System.Linq;
using LOMAssistant.Config;
using LOMAssistant.Features.AnytimeSave;
using Mortal.Core;
using Mortal.Story;
using OBB.Framework.Utils;
using UnityEngine;

namespace LOMAssistant.Features.Story;

/// <summary>
/// 剧情跳转：从剧情图、路线图直接跳到某一段剧情的开头（包括这一局还没走到的），用来补剧情、收集分支和结局。
/// 中间的剧情没有真的演过：按剧本把一路上会设的旗标、剧情记录、任务进度、物品、好感、数值补上，日期改到那一段的时间，
/// 再从那一段开演。跳之前先存一份随时存档当「跳转前」，跳过去以后可以一键回去，也可以就这样玩下去。
/// 补的规则：
/// - 中间的主线任务（主线从现在这一步按剧本里写明的「下一个主线」一路接到目标）和目标所在的任务，都从任务开头顺着剧本走一遍：
///   每到一个岔口（几个选项、掷骰结果、条件各去一处，或选了还留在本段的一组选项）就是一个「岔路」，由玩家在确认页选走哪条，
///   没选的按默认（目标任务里走离目标最近的那条，中间的主线走接到下一个主线的那条）；走过的这条路上的变化都补。
/// - 看条件自动分的岔（剧本里的条件判断）也照样列出来让玩家选，补的就是选的那一支上的变化。
/// - 最后把主线（或目标所在的支线）的进度设到目标任务。
/// </summary>
public static class StoryJump
{
	/// <summary>一个岔口：在哪个任务、哪一段，有哪几条路，现在选的第几条。</summary>
	public sealed class Fork
	{
		public string Key = "";
		public StoryMission Mission;
		public string Segment = "";
		public readonly List<string> Options = new List<string>();
		public int Picked;
		/// <summary>选哪条会让流程不一样（去处、主线、旗标、任务进度不同）；只差数值、好感、对白的不列出来，按默认补。</summary>
		public bool Matters;

		// 条件要先翻译再截短；只重排显示文字，不重算跳转计划或玩家的选择。
		internal Func<List<string>> FormatOptions;
		private string _displayLanguage;

		internal void RefreshOptions()
		{
			string language = TextConverter.DisplayLanguage;
			if (FormatOptions == null || _displayLanguage == language)
			{
				return;
			}
			List<string> options = FormatOptions();
			Options.Clear();
			Options.AddRange(options);
			_displayLanguage = language;
		}
	}

	public sealed class Plan
	{
		public StoryMission Mission;
		public string Script = "";
		public string Title = "";
		/// <summary>中间的主线任务（按先后）。</summary>
		public readonly List<StoryMission> Between = new List<StoryMission>();
		/// <summary>主线从现在接不到目标：中间的主线任务没法补。</summary>
		public bool Unlinked;
		/// <summary>主线现在这一步（目标是主线、又不是现在这一步时才有），从它开始一站站走到目标。</summary>
		public StoryMission Current;
		/// <summary>从目标任务开头走不到目标这一段（要从别的任务接进来）。</summary>
		public bool Unreachable;
		public int? FromRounds, ToRounds;
		public string MissionFrom = "", MissionTo = "";
		/// <summary>顺便结缘的那一位（StoryRomance.All 里的）；null = 不结缘。</summary>
		public StoryRomance.Heroine Heroine;
		/// <summary>玩家在各个岔口选的（岔口的 Key → 第几条）；没选过的按默认。</summary>
		public readonly Dictionary<string, int> Choices = new Dictionary<string, int>();

		// 按选的路算出来的
		public readonly List<Fork> Forks = new List<Fork>();
		public readonly List<(string Method, string[] Args)> Commands = new List<(string, string[])>();
		public readonly List<string> Effects = new List<string>();
		public int Records;
		/// <summary>没列出来的岔口（只影响数值、好感或对白）。</summary>
		public int Minor;
		public readonly List<string> Notes = new List<string>();
		/// <summary>目标这一段里看条件分的岔：分支判断名 → 走第几支（0 = 以上都不满足）。没列的按游戏判断。</summary>
		public readonly Dictionary<string, int> Arms = new Dictionary<string, int>();
	}

	public static string ReturnSave { get; private set; }
	public static string ReturnTitle { get; private set; } = "";
	/// <summary>跳过去以后那张小提示还开着。</summary>
	public static bool NoteOpen;

	/// <summary>现在能不能跳：读档进入游戏、在养成地图或剧情里、没在战斗和切场景。</summary>
	public static bool CanJump(out string reason)
	{
		reason = null;
		string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
		if (PlayerStatManagerData.Instance == null || MissionManagerData.Instance == null || !StoryNodes.HasCurrentSave)
		{
			reason = "读档进入游戏后才能跳转";
		}
		else if (scene != "Free" && scene != "Story")
		{
			reason = "在养成地图或剧情里才能跳转";
		}
		else if (SceneController.Instance == null || SceneController.Instance.IsPrepare || SceneController.Instance.IsLoading)
		{
			reason = "正在切换场景，稍后再试";
		}
		else if (LOMAssistant.Features.AnytimeSave.AnytimeSave.Instance == null)
		{
			reason = "随时存档没有启用，存不了跳转前的进度";
		}
		return reason == null;
	}

	/// <summary>跳到任务 mission 里从 script 开始的那一段。</summary>
	public static Plan Build(StoryMission mission, string script)
	{
		var plan = new Plan { Mission = mission, Script = script, Title = StoryScripts.SegmentName(script) };
		PlayerStatManagerData player = PlayerStatManagerData.Instance;
		GameTime now = player?.GameTime;
		if (!ReferenceEquals(now, null))
		{
			plan.FromRounds = now.ConvertToRounds();
		}
		plan.ToRounds = mission.From;
		// 主线进度：现在这一步 → 目标
		MissionManagerData missions = MissionManagerData.Instance;
		StoryMission current = missions?.Main != null ? StoryAtlas.Find(missions.Main.CurrentKey) : null;
		plan.MissionFrom = current?.Title ?? "";
		plan.MissionTo = mission.Title;
		// 往回跳（目标比现在早）：中间的主线早就走过了，不补，只走目标任务自己
		bool back = mission.From != null && plan.FromRounds != null && mission.From < plan.FromRounds;
		if (mission.Main && current != null && current != mission && !back)
		{
			plan.Current = current;
		}
		Recompute(plan);
		return plan;
	}

	/// <summary>
	/// 跳到这里能不能和 h 结缘：她最后一步（攻略）的旗标在哪个任务里设，那个任务的时间不晚于目标才行。
	/// 不行时 when 写最早要到什么时候。剧本还没读完时都当作可以。
	/// </summary>
	public static bool CanBond(Plan plan, StoryRomance.Heroine h, out string when)
	{
		when = "";
		var stops = StoryRomance.StopsOf(h);
		if (stops == null || plan.ToRounds == null)
		{
			return true;
		}
		string last = h.Steps[h.Steps.Length - 1];
		var dates = stops.Where(st => st.Steps.Contains(last) && st.Mission.From != null).Select(st => st.Mission.From.Value).ToList();
		if (dates.Count == 0)
		{
			dates = FlagDates(last);
		}
		// 哪个剧本都不设、不判断她的结缘旗标（瑞杏的「攻略瑞杏」）：游戏里还没做她的结缘剧情，不能结缘；when 留空
		if (dates.Count == 0)
		{
			return false;
		}
		if (dates.Min() <= plan.ToRounds.Value)
		{
			return true;
		}
		when = StoryAtlas.DateText(RoundsTime(dates.Min()));
		return false;
	}

	/// <summary>
	/// 结缘旗标不在她的结缘任务里设时（设在不属于任务的剧本里，或游戏只在剧本里判断它），按设它、判断它的剧本找时间：
	/// 剧本属于哪个任务就用那个任务的日期，不属于任务就按剧本名的章节（ch5_…）用那一章最早的主线日期。
	/// </summary>
	private static List<int> FlagDates(string flag)
	{
		var mains = StoryAtlas.Missions.Where(m => m.Main && m.From != null && m.StartScript.Length > 0).ToList();
		var dates = new List<int>();
		foreach (StoryScript script in StoryScripts.All)
		{
			if (!script.Modifies.Any(m => m.Id == flag) && !script.Checks.Any(c => c.Checkpoint.StartsWith(flag, StringComparison.Ordinal)))
			{
				continue;
			}
			if (StoryAtlas.Owner.TryGetValue(script.Name, out StoryMission owner) && owner.From != null)
			{
				dates.Add(owner.From.Value);
				continue;
			}
			var chapter = System.Text.RegularExpressions.Regex.Match(script.Name, @"^ch(\d+)_");
			if (chapter.Success)
			{
				string prefix = "ch" + chapter.Groups[1].Value + "_";
				var first = mains.Where(m => m.StartScript.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(m => m.From.Value).DefaultIfEmpty(-1).Min();
				if (first >= 0)
				{
					dates.Add(first);
				}
			}
		}
		return dates;
	}

	/// <summary>在岔口 key 选第 index 条，重算。</summary>
	public static void Pick(Plan plan, string key, int index)
	{
		plan.Choices[key] = index;
		Recompute(plan);
	}

	/// <summary>按选的路重走一遍，重算岔口和要补的变化。</summary>
	public static void Recompute(Plan plan)
	{
		plan.Forks.Clear();
		plan.Commands.Clear();
		plan.Effects.Clear();
		plan.Notes.Clear();
		plan.Arms.Clear();
		plan.Records = 0;
		plan.Minor = 0;
		plan.Unreachable = false;
		plan.Unlinked = false;
		plan.Between.Clear();
		// 中间的主线：从现在这一站起一站站走，每站走完看选的路接到哪一站（剧本里写明的「下一个主线」），
		// 没写明的（到日期自动开始）按路线图的连法接到下一站；只走还能接到目标的站
		if (plan.Current != null)
		{
			HashSet<string> reach = Reaching(plan.Mission.Key);
			StoryMission m = plan.Current;
			if (!reach.Contains(m.Key))
			{
				plan.Unlinked = true;
			}
			while (!plan.Unlinked && m != null && m != plan.Mission && plan.Between.Count < 80 && !plan.Between.Contains(m))
			{
				plan.Between.Add(m);
				StoryExit last = Walk(plan, m, null, reach);
				StoryMission next = last != null && last.MainMission.Length > 0 && reach.Contains(last.MainMission) ? StoryAtlas.Find(last.MainMission) : null;
				if (next == null)
				{
					// 走完回地图：路线图上从这一站接出去、还能接到目标的站（到日期看条件自动开始的几个，比如去崆峒还是青城留学），
					// 不止一站时让玩家选，默认日期最早的那一站
					var candidates = MainGraph().Where(e => e.From == m.Key && reach.Contains(e.To)).Select(e => StoryAtlas.Find(e.To)).Where(x => x != null && x != m)
						.Distinct().OrderBy(x => x.Main ? 0 : 1).ThenBy(x => x.From ?? int.MaxValue).ThenBy(x => x.Key, StringComparer.Ordinal)
						.GroupBy(StationText).Select(g => g.First()).ToList();
					if (candidates.Count > 0)
					{
						next = candidates[ChooseNamed(plan, m, m.Key + "|下一站", "走完以后接到哪一站主线", candidates.Select(StationText).ToList(), 0, true)];
					}
				}
				// 这一站和下一站之间开的会（唐门会议这类，开完把主线推到别处，去崆峒留学就是在会上提的）：也列成下一站让玩家选，默认直接去下一站
				if (next != null)
				{
					StoryMission from = m, to = next;
					var meetings = MainGraph().Where(e => e.From == from.Key && reach.Contains(e.To)).Select(e => StoryAtlas.Find(e.To))
						.Where(x => x != null && !x.Main && x.From != null && (from.From == null || x.From >= from.From) && (to.From == null || x.From <= to.From) && !plan.Between.Contains(x))
						.Distinct().OrderBy(x => x.From).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
					if (meetings.Count > 0)
					{
						// 同一天排了好几场同名的会，只留一场
						var options = new List<StoryMission> { to };
						options.AddRange(meetings.GroupBy(StationText).Select(g => g.First()).Where(x => StationText(x) != StationText(to)));
						next = options[ChooseNamed(plan, m, m.Key + "|会议", "走完以后接到哪一站", options.Select(StationText).ToList(), 0, true)];
					}
				}
				if (next == null)
				{
					plan.Unlinked = true;
				}
				m = next;
			}
		}
		Walk(plan, plan.Mission, plan.Script, null);
		TargetBranches(plan);
		// 目标没有固定日期（「任意时间」的主线，比如围攻唐门）：用中间走过的最后一站有日期的主线的日期
		plan.ToRounds = plan.Mission.From ?? plan.Between.Where(x => x.From != null).Select(x => x.From).LastOrDefault();
		if (plan.Unreachable)
		{
			plan.Notes.Add("从任务开头找不到走到这一段的路（可能要从别的任务接进来），任务里的变化没有补全");
		}
		if (plan.Unlinked)
		{
			plan.Notes.Add("主线从现在这一步接不到目标任务（不在同一条路线上，或要靠别的支线接进来），中间的主线剧情没有补");
		}
		if (plan.FromRounds != null && plan.ToRounds != null && plan.ToRounds < plan.FromRounds)
		{
			plan.Notes.Add("目标在现在之前，日期会往回调；中间的主线不补，之后已经走过的剧情和旗标也不会撤销");
		}
	}

	/// <summary>
	/// 顺着一个任务的剧本走：target 给了就走到这一段为止（只走能走到它的去处），没给就走到出了这个任务为止
	/// （默认走接到 reach 里那些主线站的那条）。每个岔口记一个 Fork，按选的走，沿路补变化。返回最后走的那个去处。
	/// </summary>
	private static StoryExit Walk(Plan plan, StoryMission mission, string target, HashSet<string> reach)
	{
		if (target != null && Same(mission.StartScript, target))
		{
			return null;
		}
		StoryExit last = null;
		var inMission = new HashSet<string>(mission.Scripts, StringComparer.OrdinalIgnoreCase);
		Dictionary<string, int> distance = target != null ? Distances(mission, target) : null;
		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string at = mission.StartScript;
		while (at != null && visited.Add(at))
		{
			StoryScript s = StoryScripts.Get(at);
			if (s == null)
			{
				break;
			}
			// 能走的去处：不回标题（结局）；有目标时只走能走到目标的
			// 能走的去处：不回标题（结局）、不绕回这一段自己（休憩时的闲聊：聊完回到同一个菜单）；有目标时只走能走到目标的
			var ways = s.Exits.Where(e => !e.ToTitle && !Same(e.Target, s.Name) && (distance == null || Same(e.Target, target) || distance.ContainsKey(e.Target))).ToList();
			if (ways.Count == 0)
			{
				if (target != null)
				{
					plan.Unreachable = true;
				}
				break;
			}
			int fallback = 0;
			if (distance != null)
			{
				fallback = ways.Select((e, k) => (k, d: Same(e.Target, target) ? 0 : distance[e.Target], a: ArmScore(plan, e))).OrderBy(x => x.d).ThenByDescending(x => x.a).First().k;
			}
			else
			{
				// 默认：接到能走到目标的主线的那条；再看条件岔口（选了结缘对象的走她那一支，否则走「以上都不满足」）；再走留在本任务里的
				fallback = ways.Select((e, k) => (k, main: reach != null && e.MainMission.Length > 0 && reach.Contains(e.MainMission) ? 1 : 0, a: ArmScore(plan, e), stay: inMission.Contains(e.Target) ? 1 : 0))
					.OrderByDescending(x => x.main).ThenByDescending(x => x.a).ThenByDescending(x => x.stay).ThenBy(x => x.k).First().k;
			}
			// 两条路只是去的剧本不同、之后又合回来的（训练、闲聊这类）不算：比的是这条路上和接着那一段里设的旗标、任务进度，以及接到哪个主线
			var signatures = ways.Select(e => e.MainMission + "|" + e.ToMap + "|" + FlowSignature(s, path => StoryScripts.IsPrefix(path, e.Path))
				+ "|" + (StoryScripts.Get(e.Target) is StoryScript next && !Same(next.Name, target) ? FlowSignature(next, path => path.Count == 0) : "")).ToList();
			StoryExit exit = ways[Choose(plan, mission, s, "去处", ways.Select(WayText).ToList(), fallback, signatures.Distinct().Count() > 1)];
			// 选了还留在本段、之后合回来的选项：每组也算一个岔口
			var stays = new HashSet<string>();
			var leaving = new HashSet<string>(s.Exits.SelectMany(e => e.Path).Where(st => st.Kind == StoryStep.Option).Select(st => st.Key));
			for (int g = 0; g < s.Menus.Count; g++)
			{
				// 菜单所在的分岔不在走的这条路上（整组放在和某位结缘才进得去的分支里）：这一组不会出现
				if (g < s.MenuPaths.Count && !StoryScripts.IsPrefix(s.MenuPaths[g], exit.Path))
				{
					continue;
				}
				var options = s.Menus[g].Where(st => st.Kind == StoryStep.Option && !leaving.Contains(st.Key)).ToList();
				if (options.Count < 2)
				{
					continue;
				}
				bool matters = options.Select(o => FlowSignature(s, path => path.Any(st => st.Key == o.Key))).Distinct().Count() > 1;
				// 有的选项要满足条件才会出现（「夏侯兰是我的妻子」要先和她结缘）：写明条件，默认选不要条件的那个
				var texts = options.Select(st => (Option: StoryScripts.OptionText(st.Key), Condition: st.Requirement.Length > 0 ? RichText.Strip(DataHelper.ParseConditionCheckpoint(st.Requirement)) : "")).ToList();
				List<string> Labels() => texts.Select(t => "「" + t.Option + "」" + (t.Condition.Length > 0 ? "　（要 " + Short(TextConverter.ForDisplay(t.Condition), 40) + "）" : "")).ToList();
				int plain = options.FindIndex(st => st.Requirement.Length == 0);
				int pick = Choose(plan, mission, s, "选项" + g, Labels(), plain >= 0 ? plain : 0, matters, Labels);
				stays.Add(options[pick].Key);
			}
			Collect(plan, s, exit, stays);
			last = exit;
			if (exit.Path.Any(st => st.Kind == StoryStep.Dice))
			{
				plan.Notes.Add("「" + StoryScripts.SegmentName(s.Name) + "」这一步要靠掷骰，按选的这条路补");
			}
			if (s.Fight)
			{
				plan.Notes.Add("「" + StoryScripts.SegmentName(s.Name) + "」里有战斗，没有真的打，按选的这条路补");
			}
			if (target != null && Same(exit.Target, target))
			{
				break;
			}
			if (exit.Target.Length == 0 || exit.ToMap || exit.MainMission.Length > 0 || !inMission.Contains(exit.Target))
			{
				break;
			}
			at = exit.Target;
		}
		return last;
	}

	/// <summary>
	/// 目标这一段里看条件分的岔（分支判断）：没读过的对白在哪一支，默认走哪一支（玩家可以改，或选「按游戏判断」）。
	/// 跳过去以后这一段判断到它时，先把这一支的条件补上，补不上的（随机、物品、时间）直接走这一支，见 BranchOverride。
	/// </summary>
	private static void TargetBranches(Plan plan)
	{
		StoryScript s = StoryScripts.Get(plan.Script);
		if (s == null)
		{
			return;
		}
		ReadStorySystem read = ReadStorySystem.Instance;
		// 分支判断名 → 第几支 → 这一支里没读过的对白有几句
		var arms = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
		var order = new List<string>();
		foreach (var (path, _, key) in s.Talk)
		{
			bool unread = read != null && !read.ExistKey(key);
			foreach (StoryStep step in path.Where(st => st.Kind == StoryStep.Branch))
			{
				if (!arms.TryGetValue(step.Key, out Dictionary<int, int> count))
				{
					arms[step.Key] = count = new Dictionary<int, int>();
					order.Add(step.Key);
				}
				count.TryGetValue(step.Arm, out int n);
				count[step.Arm] = n + (unread ? 1 : 0);
			}
		}
		foreach (string name in order)
		{
			Dictionary<int, int> count = arms[name];
			// 老写法的剧本每一支都记成 0，分不出哪句属于哪一支，不猜
			if (!count.Values.Any(n => n > 0) || count.Keys.All(a => a == 0))
			{
				continue;
			}
			// 第 1、2… 支在前，「以上都不满足」放最后
			List<int> list = count.Keys.OrderBy(a => a == 0 ? int.MaxValue : a).ToList();
			var texts = list.Select(a => (Condition: RichText.Strip(DataHelper.SwitchBranchCondition(name, a)), Unread: count[a])).ToList();
			List<string> Labels()
			{
				var labels = new List<string> { "按游戏判断" };
				labels.AddRange(texts.Select(t => Short(TextConverter.ForDisplay(t.Condition), 40) + (t.Unread > 0 ? $"　（没读过 {t.Unread} 句）" : "")));
				return labels;
			}
			int most = list.OrderByDescending(a => count[a]).First();
			int picked = ChooseNamed(plan, plan.Mission, plan.Mission.Key + "|" + s.Name + "|条件" + name, StoryScripts.SegmentName(s.Name), Labels(), list.IndexOf(most) + 1, true, Labels);
			if (picked > 0)
			{
				plan.Arms[name] = list[picked - 1];
			}
		}
	}

	// 岔口：两条路以上才问；返回选的第几条
	private static int Choose(Plan plan, StoryMission mission, StoryScript script, string what, List<string> options, int fallback, bool matters, Func<List<string>> formatOptions = null)
		=> ChooseNamed(plan, mission, mission.Key + "|" + script.Name + "|" + what, StoryScripts.SegmentName(script.Name), options, fallback, matters, formatOptions);

	private static int ChooseNamed(Plan plan, StoryMission mission, string key, string segment, List<string> options, int fallback, bool matters, Func<List<string>> formatOptions = null)
	{
		if (options.Count < 2)
		{
			return 0;
		}
		if (!matters)
		{
			plan.Minor++;
		}
		int picked = plan.Choices.TryGetValue(key, out int chosen) && chosen >= 0 && chosen < options.Count ? chosen : fallback;
		var fork = new Fork { Key = key, Mission = mission, Segment = segment, Picked = picked, Matters = matters, FormatOptions = formatOptions };
		fork.Options.AddRange(options);
		plan.Forks.Add(fork);
		return picked;
	}

	// 下一站写成一句：主线写任务名；支线写任务组的名字（唐门会议这类，没起名的用任务名）和日期，好认出是哪一场
	private static string StationText(StoryMission m)
	{
		if (m.Main)
		{
			return "主线 → " + m.Title;
		}
		string group = m.SeriesTitle.Any(c => c >= 128) ? m.SeriesTitle : m.Title;
		return "支线 → " + group + (m.From != null ? "（" + StoryAtlas.DateText(RoundsTime(m.From.Value)) + "）" : "");
	}

	private static GameTime RoundsTime(int rounds)
	{
		int r = Math.Max(1, rounds) - 1;
		return new GameTime(r / 36 + 1, r % 36 / 3 + 1, (MonthStageType)(r % 3 + 1));
	}

	/// <summary>条件岔口的默认：这条路上的条件是结缘对象（选了的，或存档里已经结缘的）的结缘旗标的，2；是「以上都不满足」的，1；其他条件 0。</summary>
	private static int ArmScore(Plan plan, StoryExit exit)
	{
		int score = 0;
		foreach (StoryStep step in exit.Path.Where(st => st.Kind == StoryStep.Branch))
		{
			if (step.Arm == 0)
			{
				score = Math.Max(score, 1);
				continue;
			}
			string condition = DataHelper.SwitchBranchCondition(step.Key, step.Arm) ?? "";
			// 结缘对象：选了的那位；没选的按这个存档已经结缘的那位
			StoryRomance.Heroine heroine = plan.Heroine ?? StoryRomance.All.FirstOrDefault(x => StoryRomance.StepDone(x.Steps[x.Steps.Length - 1]));
			if (heroine != null && heroine.Steps.Any(f => condition.Contains(f)))
			{
				score = 2;
			}
			else if (condition.Contains("以上条件都不满足"))
			{
				score = Math.Max(score, 1);
			}
		}
		return score;
	}

	private static string Short(string text, int max) => text.Length > max ? text.Substring(0, max) + "…" : text;

	// 这条路上影响流程的变化：旗标（剧本内部记数用的暂存旗标 A00001 这类不算）、任务进度
	private static string FlowSignature(StoryScript script, Func<List<StoryStep>, bool> on)
	{
		return string.Join(";", script.Commands.Where(c => on(c.Path) && (c.Method == "SetFlag" || c.Method == "AddFlag" || c.Method == "Mission" || c.Method == "IsTriggerSubMission")
			&& !(c.Args.Length > 0 && System.Text.RegularExpressions.Regex.IsMatch(c.Args[0], @"^A\d+$")))
			.Select(c => c.Method + "(" + string.Join(",", c.Args) + ")").OrderBy(x => x, StringComparer.Ordinal).ToArray());
	}

	// 去处写成一句：这条路最后一步分岔（选项、掷骰、条件），再写去哪
	private static string WayText(StoryExit exit)
	{
		string how = exit.Path.Count > 0 ? string.Join(" → ", exit.Path.Select(StoryScripts.Describe).ToArray()) : exit.AfterScene ? "战斗后" : "接着";
		string where = exit.MainMission.Length > 0 ? "主线 → " + (StoryAtlas.Find(exit.MainMission)?.Title ?? exit.MainMission)
			: exit.ToMap ? "回到地图" : exit.Target.Length > 0 ? StoryScripts.SegmentName(exit.Target) : "";
		return where.Length > 0 ? how + "　→ " + where : how;
	}

	/// <summary>任务里每个剧本到 target 最少要走几段（走不到的不在里面）。</summary>
	private static Dictionary<string, int> Distances(StoryMission mission, string target)
	{
		var distance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [target] = 0 };
		var queue = new Queue<string>();
		queue.Enqueue(target);
		var names = mission.Scripts.Concat(new[] { mission.StartScript }).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		while (queue.Count > 0)
		{
			string to = queue.Dequeue();
			foreach (string from in names)
			{
				if (!distance.ContainsKey(from) && (StoryScripts.Get(from)?.Exits.Any(e => Same(e.Target, to)) ?? false))
				{
					distance[from] = distance[to] + 1;
					queue.Enqueue(from);
				}
			}
		}
		return distance;
	}

	private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

	// 一个剧本里走 exit 这条路要补的变化：路上的每一步都得是走的这条路上的（或选的那个留在本段的选项）
	private static void Collect(Plan plan, StoryScript script, StoryExit exit, HashSet<string> stays)
	{
		bool Taken(List<StoryStep> path) => path.All(st => exit.Path.Any(x => x.Kind == st.Kind && x.Key == st.Key && x.Arm == st.Arm) || (st.Kind == StoryStep.Option && stays.Contains(st.Key)));
		foreach (var (path, method, args) in script.Commands)
		{
			if (!Taken(path))
			{
				continue;
			}
			plan.Commands.Add((method, args));
			if (method == "AddStory")
			{
				plan.Records++;
			}
		}
		foreach (var (path, text) in script.Effects)
		{
			if (Taken(path) && !text.Contains("暂存"))
			{
				plan.Effects.Add(text);
			}
		}
	}

	private static List<(string From, string To)> _mainGraph;
	private static int _mainGraphVersion = -1;

	/// <summary>
	/// 主线站之间的连线，和路线图一样：剧本里写明的「下一个主线」；没有任何剧本接进来的站（到日期自动开始的），
	/// 从日期最近的前一站接过来。
	/// </summary>
	private static List<(string From, string To)> MainGraph()
	{
		if (_mainGraph != null && _mainGraphVersion == StoryAtlas.Version)
		{
			return _mainGraph;
		}
		var mains = StoryAtlas.Missions.Where(m => m.Main && m.StartScript.Length > 0).ToDictionary(m => m.Key, StringComparer.Ordinal);
		var edges = mains.Values.SelectMany(m => m.Next.Where(mains.ContainsKey).Select(n => (m.Key, n))).ToList();
		var date = mains.Values.ToDictionary(m => m.Key, m => m.From, StringComparer.Ordinal);
		for (int pass = 0; pass < 6; pass++)
		{
			foreach (var (from, to) in edges)
			{
				if (date[to] == null && date[from] != null)
				{
					date[to] = date[from];
				}
			}
		}
		var placed = mains.Values.Where(m => date[m.Key] != null).OrderBy(m => date[m.Key]).ThenBy(m => m.Key, StringComparer.Ordinal).ToList();
		var hasIncoming = new HashSet<string>(edges.Select(e => e.Item2), StringComparer.Ordinal);
		for (int i = 1; i < placed.Count; i++)
		{
			StoryMission m = placed[i];
			if (hasIncoming.Contains(m.Key))
			{
				continue;
			}
			int? before = placed.Take(i).Select(p => date[p.Key]).Where(d => d < date[m.Key]).DefaultIfEmpty(null).Max();
			if (before == null)
			{
				continue;
			}
			foreach (StoryMission p in placed.Take(i).Where(p => date[p.Key] == before))
			{
				edges.Add((p.Key, m.Key));
			}
		}
		// 开完会接回主线的支线（唐门会议这类：剧本里把主线推到某一站）：接在它触发日期前最近的那一站主线后面，再接回它推到的那一站。
		// 去崆峒还是青城留学就是在会议里选的
		foreach (StoryMission side in StoryAtlas.Missions.Where(m => !m.Main && m.StartScript.Length > 0 && m.From != null && m.Next.Any(mains.ContainsKey)))
		{
			int? before = placed.Select(p => date[p.Key]).Where(d => d <= side.From).DefaultIfEmpty(null).Max();
			if (before == null)
			{
				continue;
			}
			foreach (StoryMission p in placed.Where(p => date[p.Key] == before))
			{
				edges.Add((p.Key, side.Key));
			}
			// 每个月的唐门会议都是同一套剧本，会上能推到的主线站每一场都算上了；只接日期不早于这场会的，免得后面的会接回前面的站绕成环
			foreach (string n in side.Next.Where(n => mains.ContainsKey(n) && !(date[n] < side.From)))
			{
				edges.Add((side.Key, n));
			}
		}
		_mainGraphVersion = StoryAtlas.Version;
		return _mainGraph = edges;
	}

	/// <summary>能沿着主线连线走到 target 的站（含它自己）。日期比目标还晚的站不算：从那里绕回来的路不走。</summary>
	private static HashSet<string> Reaching(string target)
	{
		var graph = MainGraph();
		int? until = StoryAtlas.Find(target)?.From;
		var reach = new HashSet<string>(StringComparer.Ordinal) { target };
		for (bool grew = true; grew;)
		{
			grew = false;
			foreach (var (from, to) in graph)
			{
				if (reach.Contains(to) && !(StoryAtlas.Find(from)?.From > until) && reach.Add(from))
				{
					grew = true;
				}
			}
		}
		return reach;
	}

	/// <summary>跳：先存「跳转前」，再补变化、改日期和任务进度，从目标那一段开演。</summary>
	public static bool Execute(Plan plan, out string message)
	{
		if (plan?.Mission?.PlaceEntry == true)
		{
			message = "此地点剧情仅供查看";
			return false;
		}
		if (!CanJump(out message))
		{
			return false;
		}
		string back = LOMAssistant.Features.AnytimeSave.AnytimeSave.Instance.SaveSlot("跳转前（去「" + plan.Title + "」）");
		if (back == null)
		{
			message = "存不了跳转前的随时存档，没有跳";
			return false;
		}
		PlayerStatManagerData player = PlayerStatManagerData.Instance;
		MissionManagerData missions = MissionManagerData.Instance;
		int failed = 0;
		foreach (var (method, args) in plan.Commands)
		{
			try
			{
				Apply(method, args, player, missions);
			}
			catch (Exception ex)
			{
				failed++;
				FileLogger.LogWarning("[剧情跳转] " + method + "(" + string.Join(",", args) + ") 没补上: " + ex.Message);
			}
		}
		if (plan.Heroine != null && CanBond(plan, plan.Heroine, out _))
		{
			// 顺便结缘：她的结缘旗标都设上，好感拉到 100，心上人设成她
			foreach (string flag in plan.Heroine.Steps)
			{
				missions.SetFlag(flag, 1);
			}
			RelationshipStat stat = player.Relationships.Get(plan.Heroine.Relation);
			if (stat != null && stat.Value < 100)
			{
				player.Relationships.Set(plan.Heroine.Relation, 100);
			}
			player.Stats.Set(GameStatType.愛人, plan.Heroine.Lover);
		}
		if (plan.ToRounds != null)
		{
			int r = Math.Max(1, plan.ToRounds.Value) - 1;
			player.SetGameTime(r / 36 + 1, r % 36 / 3 + 1, (MonthStageType)(r % 3 + 1));
		}
		missions.SetKey(plan.Mission.Main ? "Main" : plan.Mission.Series, plan.Mission.Key);
		BranchOverride.Set(plan.Arms);
		ReturnSave = back;
		ReturnTitle = plan.Title;
		NoteOpen = true;
		FileLogger.Log($"[剧情跳转] 跳到 {plan.Script}（{plan.Mission.Key}），补了 {plan.Commands.Count - failed} 项变化" + (failed > 0 ? $"，{failed} 项没补上" : "") + "，跳转前存在随时存档 " + back);
		player.SetStoryScript(plan.Script);
		SceneController.Instance.LoadStory();
		message = null;
		return true;
	}

	private static void Apply(string method, string[] args, PlayerStatManagerData player, MissionManagerData missions)
	{
		string a0 = args.Length > 0 ? args[0] : "";
		int Int(int i) => args.Length > i && int.TryParse(args[i], out int v) ? v : 0;
		switch (method)
		{
			case "SetFlag": missions.SetFlag(a0, Int(1)); break;
			case "AddFlag": missions.AddFlag(a0, Int(1)); break;
			case "AddStory":
				if (!player.StoryKeyList.Contains(a0))
				{
					player.StoryKeyList.Add(a0);
				}
				break;
			case "Mission": missions.SetKey(a0, args.Length > 1 ? args[1] : ""); break;
			case "IsTriggerSubMission": missions.SetIsTriggerSubMission(a0, Int(1) == 1); break;
			case "AddBook": ItemDatabase.Instance.AddItem(GameItemType.書籍, a0, Int(1)); break;
			case "AddMisc": ItemDatabase.Instance.AddItem(GameItemType.雜物, a0, Int(1)); break;
			case "AddSpecial": ItemDatabase.Instance.AddItem(GameItemType.貴重品, a0, Int(1)); break;
			case "RemoveBook": ItemDatabase.Instance.RemoveItem(GameItemType.書籍, a0); break;
			case "RemoveMisc": ItemDatabase.Instance.RemoveItem(GameItemType.雜物, a0); break;
			case "RemoveSpecial": ItemDatabase.Instance.RemoveItem(GameItemType.貴重品, a0); break;
			case "Character":
				if (EnumUtils.TryParseByStringValue<RelationshipStatType>(a0, out var who)) player.Relationships.Get(who).AddValue(Int(1));
				break;
			case "SetCharacter":
				if (EnumUtils.TryParseByStringValue<RelationshipStatType>(a0, out var whom)) player.Relationships.Set(whom, Int(1));
				break;
			case "Player":
				if (EnumUtils.TryParseByStringValue<GameStatType>(a0, out var stat)) player.Stats.Add(stat, Int(1));
				break;
			case "SetPlayer":
				if (EnumUtils.TryParseByStringValue<GameStatType>(a0, out var set)) player.Stats.Set(set, Int(1));
				break;
		}
	}

	/// <summary>回到跳转前：读那份随时存档。</summary>
	public static void Return()
	{
		if (ReturnSave == null || LOMAssistant.Features.AnytimeSave.AnytimeSave.Instance == null)
		{
			return;
		}
		BranchOverride.Set(null);
		LOMAssistant.Features.AnytimeSave.AnytimeSave.Instance.LoadFile(ReturnSave);
		NoteOpen = false;
	}

	public static bool Enabled => ConfigManager.EnableStoryJump.Value;
}

/// <summary>
/// 跳过去以后屏幕上方的一张小卡片：和咖啡伴侣窗口一样的宣纸底、墨色字，左边一道朱砂。
/// 「回到跳转前」读回那份随时存档；「就这样玩」关掉卡片，当正常的进度玩下去（随时存档里那份「跳转前」还在）。
/// </summary>
public sealed class StoryJumpNote : MonoBehaviour
{
	private static StoryJumpNote _instance;
	private GUIStyle _card, _small, _title, _back, _keep;
	private float _scale = -1f;
	private int _fontMode = -1;
	private bool _dark;

	public static void Ensure(GameObject host)
	{
		if (_instance == null)
		{
			_instance = host.GetComponent<StoryJumpNote>() ?? host.AddComponent<StoryJumpNote>();
			// 回到标题画面（之后多半会读别的档）就收起
			UnityEngine.SceneManagement.SceneManager.activeSceneChanged += (a, b) =>
			{
				if (b.name == "Title")
				{
					StoryJump.NoteOpen = false;
				}
			};
		}
	}

	private void OnGUI()
	{
		if (!StoryJump.NoteOpen || StoryJump.ReturnSave == null || UiHider.Active || Event.current == null)
		{
			return;
		}
		float scale = PanelTheme.UiScale;
		if (scale <= 0f)
		{
			return;
		}
		if (_card == null || ModFont.NeedsRebuild(scale, _scale, _fontMode) || _dark != PanelTheme.Dark)
		{
			_scale = scale;
			_fontMode = ModFont.Mode;
			_dark = PanelTheme.Dark;
			_card = new GUIStyle { border = new RectOffset(2, 2, 2, 2) };
			_card.normal.background = PanelTheme.Framed(new Color(PanelTheme.Paper.r, PanelTheme.Paper.g, PanelTheme.Paper.b, PanelTheme.DialogOpacity), PanelTheme.Edge);
			_small = PanelTheme.Label(scale, 14, PanelTheme.InkMuted, false);
			_title = PanelTheme.Label(scale, 18, PanelTheme.Ink, false);
			_title.clipping = TextClipping.Clip;
			_back = PanelTheme.Primary(scale, 16);
			_keep = PanelTheme.Button(scale, 16);
		}
		float s = scale;
		string where = StoryJump.ReturnTitle;
		float back = Mathf.Max(132f * s, _back.CalcSize(new GUIContent("回到跳转前")).x + 16f * s);
		float keep = Mathf.Max(116f * s, _keep.CalcSize(new GUIContent("就这样玩")).x + 16f * s);
		float text = Mathf.Min(560f * s, _title.CalcSize(new GUIContent("已跳到「" + where + "」")).x);
		float width = Mathf.Min(Screen.width - 24f * s, text + back + keep + 70f * s), height = 64f * s;
		var rect = new Rect((Screen.width - width) / 2f, 14f * s, width, height);
		GUI.Box(rect, GUIContent.none, _card);
		// 左边一道朱砂
		Color old = GUI.color;
		GUI.color = PanelTheme.Cinnabar;
		GUI.DrawTexture(new Rect(rect.x + 2f * s, rect.y + 2f * s, 4f * s, rect.height - 4f * s), Texture2D.whiteTexture);
		GUI.color = old;
		float x = rect.x + 20f * s, textWidth = rect.width - back - keep - 56f * s;
		GUI.Label(new Rect(x, rect.y + 9f * s, textWidth, 20f * s), "剧情跳转", _small);
		GUI.Label(new Rect(x, rect.y + 29f * s, textWidth, 26f * s), "已跳到「" + where + "」", _title);
		float bh = 38f * s, by = rect.y + (height - bh) / 2f;
		if (GUI.Button(new Rect(rect.xMax - keep - back - 24f * s, by, back, bh), "回到跳转前", _back))
		{
			StoryJump.Return();
		}
		if (GUI.Button(new Rect(rect.xMax - keep - 14f * s, by, keep, bh), "就这样玩", _keep))
		{
			StoryJump.NoteOpen = false;
		}
	}
}
