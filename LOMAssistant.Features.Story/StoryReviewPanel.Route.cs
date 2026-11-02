using System;
using System.Collections.Generic;
using System.Linq;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Story;

// 一条贯穿全程的主线（这一局走过的优先）画在同一条车道上，岔路分到上下的车道、线上写触发条件。横轴是游戏日历，支线按任务组挂在下面。
// 一个站点往后岔出几条路时，第一条留在原车道，其余分到上下的车道。横轴是游戏日历，支线按任务组挂在下面。
public partial class StoryReviewPanel
{
	private sealed class RouteLayout
	{
		public readonly Dictionary<string, Vector2> Points = new Dictionary<string, Vector2>(StringComparer.Ordinal);
		public readonly List<(string From, string To, bool ByDate)> Edges = new List<(string, string, bool)>();
		public readonly List<(float X, int Date)> Columns = new List<(float, int)>();
		public readonly List<SideStory> Side = new List<SideStory>();
		public float Width, Height, MainBottom, LabelWidth;
		/// <summary>岔开的线上写的触发条件（短），按 (从, 到)。</summary>
		public readonly Dictionary<(string, string), string> EdgeLabels = new Dictionary<(string, string), string>();
		/// <summary>一条贯穿全程的主线（这一局走过的优先），画在同一条车道上。</summary>
		public readonly HashSet<string> Spine = new HashSet<string>(StringComparer.Ordinal);
	}

	private sealed class SideStory
	{
		public string Series = "", Title = "";
		public List<StoryMission> Missions = new List<StoryMission>();
		/// <summary>走完会把主线设到这些站（襄阳行这类）。</summary>
		public List<StoryMission> Joins = new List<StoryMission>();
		public Rect Pill;
		public int? Read;
		public bool Visited, Dated;
	}

	/// <summary>
	/// 主线骨架：几条路线并行的地方（三条留学线、江陵、跟不跟大师兄、你的决策……）每站放哪条车道，手工排好；
	/// 按连线自动排容易把并行的路线交错在一起。车道参考赵活系统（Toufff，MIT）的路线图。0 是主线，负的在上面，正的在下面。
	/// 不在表里的站（游戏更新新加的）照旧自动排。
	/// </summary>
	private static readonly Dictionary<string, int> RouteLanes = new Dictionary<string, int>(StringComparer.Ordinal)
	{
		["M0001"] = 0, ["M0002"] = 0, ["M0003"] = 0,
		// 序章（第 3 年末，读回快照前）
		["M0000_01"] = -1, ["M0000_02"] = -1, ["M0000_03"] = -1, ["M0000_04"] = -1,
		["M2000_01"] = 0, ["M2003_02"] = -1, ["M2003_01"] = 1, ["M2004_01"] = 0, ["M2005_01"] = 0, ["M2005_02"] = 0,
		// 崆峒留学
		["M2006_03"] = -1, ["M2007_02"] = -1, ["M2008_01"] = -1,
		// 唐门留学
		["M2006_01"] = 1, ["M2007_01"] = 1, ["M2007_03"] = 1, ["M2007_04"] = 1, ["M2007_05"] = 1,
		// 破庙之后留在唐门
		["M2009_01"] = 2, ["M2010_02"] = 2, ["M2010_03"] = 2, ["M2010_04"] = 2,
		// 青城留学
		["M2011_01"] = 3, ["M2011_02"] = 3, ["M2011_03"] = 3, ["M2011_04"] = 3, ["M2011_05"] = 3, ["M2011_06"] = 3,
		["M2008_02"] = 0, ["M3001_01"] = 0, ["M3001_02"] = -1, ["M3001_03"] = 1, ["M3002_01"] = 0, ["M3003_01"] = 0, ["M3003_02"] = 0,
		// 不跟大师兄
		["M3004_01"] = 1, ["M3004_02"] = 1,
		["M3005_01"] = 0, ["M3005_02"] = 0, ["M4001_01"] = 0, ["M4002_01"] = 0, ["M4002_02"] = 0,
		["M5001_00"] = 0, ["M5001_01"] = 0, ["M5001_02"] = 0, ["M5001_03"] = 0, ["M5001_04"] = 0, ["M5001_05"] = 0,
		["M6001_01"] = 0, ["M6001_02"] = 0, ["M6001_03"] = 0,
		// 你的决策：退隐江湖在下，西武林盟在上，战他娘亲留在主线
		["M6002_01"] = 1, ["M6002_02"] = 0,
		["M6003_01"] = -1, ["M6003_02"] = -1, ["M6003_03"] = -1, ["M6003_04"] = -1,
		["M7001_01"] = 1, ["M7002_01"] = 0, ["M7003_01"] = 0,
	};

	/// <summary>下一站的金圈。</summary>
	private static Color Gold => PanelTheme.Dark ? new Color(0.85f, 0.68f, 0.3f, 1f) : new Color(0.6f, 0.44f, 0.1f, 1f);

	private RouteLayout _route;
	/// <summary>路线图上拆出来的小站（按编号），只在路线图里有，游戏的任务数据里没有。</summary>
	private readonly Dictionary<string, StoryMission> _routeParts = new Dictionary<string, StoryMission>(StringComparer.Ordinal);

	private StoryMission RouteFind(string key) => key != null && _routeParts.TryGetValue(key, out StoryMission part) ? part : StoryAtlas.Find(key);
	private int _routeBuiltFor = -1;
	/// <summary>上一次画路线图的帧：隔了几帧才又画，就是刚切到这一页（或重新打开窗口）。</summary>
	private int _routeShownFrame = -1;
	/// <summary>打开时自动选中的那一站：还停在它上面（没自己点别的）时，下次打开换到新的当前主线。</summary>
	private StoryMission _routeAutoPicked;
	private float _routeZoom = 1f, _routeBuiltZoom;
	private Vector2 _routeScroll, _routeDetailScroll;
	private string _routeSearch = "", _routeLastQuery = "";
	private StoryMission _routePicked;
	private SideStory _routeSide;
	// 结缘路线：选了一位，地图上和她有关的任务亮着，其余变淡，右边列出她的结缘进度
	private StoryRomance.Heroine _routeHeroine;
	private HashSet<StoryMission> _romanceMissions, _romanceSteps;
	private float _routeDetailInner; // 详情栏里能用的宽度（去掉边距和滚动条）

	private float Z(float v) => S(v) * _routeZoom;

	private void DrawRoute(Rect r)
	{
		if (!EnsureAtlas(r))
		{
			return;
		}
		if (_route == null || _routeBuiltFor != StoryAtlas.Version || !Mathf.Approximately(_routeBuiltZoom, _routeZoom * _scale))
		{
			_route = BuildRoute();
			// 重建后按编号重新关联选中的站点，搜索结果也要使用新的索引。
			_routePicked = _routePicked != null ? RouteFind(_routePicked.Key) : null;
			_routeAutoPicked = _routeAutoPicked != null ? RouteFind(_routeAutoPicked.Key) : null;
			_routeSide = _routeSide != null ? _route.Side.FirstOrDefault(s => s.Series == _routeSide.Series) : null;
			_searchedFor = _flagSearchedFor = null;
			_routeBuiltFor = StoryAtlas.Version;
			_routeBuiltZoom = _routeZoom * _scale;
		}
		// 顶上一行：搜索、缩放、跳到当前主线、重新生成
		string query = SearchBox(new Rect(r.x, r.y, S(360), S(36)), ref _routeSearch);
		// 输入新的搜索词时，右边改成显示搜索结果
		if (query != _routeLastQuery)
		{
			_routeLastQuery = query;
			if (query.Length >= 2)
			{
				_routePicked = null;
				_routeSide = null;
				_routeDetailScroll = Vector2.zero;
			}
		}
		float bx = r.x + S(376);
		if (GUI.Button(new Rect(bx, r.y, S(40), S(36)), "－", _button)) _routeZoom = Mathf.Max(0.6f, _routeZoom - 0.1f);
		GUI.Label(new Rect(bx + S(44), r.y, S(60), S(36)), Mathf.RoundToInt(_routeZoom * 100) + "%", _center);
		if (GUI.Button(new Rect(bx + S(108), r.y, S(40), S(36)), "＋", _button)) _routeZoom = Mathf.Min(1.6f, _routeZoom + 0.1f);
		// 回到现在：选中当前主线这一站（红色「今」字）、滚到它。刚打开路线图时，没选别的（或还停在上次自动选的那站）也自动这样做
		bool center = GUI.Button(new Rect(bx + S(160), r.y, TextWidth("回到现在", _button, S(130)), S(36)), "回到现在", _button);
		bool opened = Time.frameCount - _routeShownFrame > 2;
		_routeShownFrame = Time.frameCount;
		bool auto = !center && opened && (_routePicked == null || _routePicked == _routeAutoPicked) && _routeSide == null && _routeHeroine == null && query.Length < 2;
		if ((center || auto) && PickStation(StoryAtlas.Find(StoryAtlas.CurrentMain), r))
		{
			_routeAutoPicked = auto ? _routePicked : null;
		}
		if (GUI.Button(new Rect(r.xMax - S(130), r.y, S(130), S(36)), "重新生成", _button))
		{
			_reading.Clear();
			StoryAtlas.Start();
			return;
		}
		DrawRomanceBar(new Rect(r.x, r.y + S(44), r.width, S(34)));
		float detailWidth = Mathf.Min(S(460), r.width * 0.32f);
		var canvas = new Rect(r.x, r.y + S(86), r.width - detailWidth - S(12), r.height - S(86));
		var detail = new Rect(canvas.xMax + S(12), canvas.y, detailWidth, canvas.height);
		Fill(canvas, PaperLight);
		Frame(canvas, Edge, 1);
		DrawRouteCanvas(canvas, query);
		Fill(detail, PaperDeep);
		Frame(detail, Edge, 1);
		DragScroll("routeDetail", detail, ref _routeDetailScroll);
		GUILayout.BeginArea(new Rect(detail.x + S(12), detail.y + S(10), detail.width - S(24), detail.height - S(20)));
		_routeDetailInner = detail.width - S(24) - S(22);
		_routeDetailScroll = GUILayout.BeginScrollView(_routeDetailScroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
		if (query.Length >= 2 && _routeSide == null && _routePicked == null)
		{
			DrawSearchResults(query);
		}
		else if (_routeSide != null)
		{
			DrawSideDetail(_routeSide);
		}
		else if (_routePicked != null)
		{
			DrawMissionDetail(_routePicked);
		}
		else if (_routeHeroine != null)
		{
			DrawRomanceDetail(_routeHeroine);
		}
		GUILayout.EndScrollView();
		GUILayout.EndArea();
	}

	private RouteLayout BuildRoute()
	{
		var layout = new RouteLayout { LabelWidth = Z(132) };
		var mains = StoryAtlas.Missions.Where(m => m.Main && m.StartScript.Length > 0).ToDictionary(m => m.Key, StringComparer.Ordinal);
		// 连线：剧本里写明的
		var edges = new List<(string From, string To, bool ByDate)>();
		foreach (StoryMission m in mains.Values)
		{
			foreach (string next in m.Next)
			{
				if (mains.ContainsKey(next))
				{
					edges.Add((m.Key, next, false));
				}
			}
		}
		// 日期：使用包含触发条件的显示日期，没有日期的任务跟在它前一站后面。
		var date = mains.Values.ToDictionary(m => m.Key, m => m.DisplayFrom, StringComparer.Ordinal);
		for (int pass = 0; pass < 6; pass++)
		{
			foreach (var e in edges)
			{
				if (date[e.To] == null && date[e.From] != null)
				{
					date[e.To] = date[e.From];
				}
			}
		}
		var placed = mains.Values.Where(m => date[m.Key] != null).OrderBy(m => date[m.Key]).ThenBy(m => m.Key, StringComparer.Ordinal).ToList();
		// 没有任何剧本写明会走到的站点（按日期自动开始的），从日期最近的前一站接一条虚线
		var hasIncoming = new HashSet<string>(edges.Select(e => e.To), StringComparer.Ordinal);
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
			foreach (StoryMission p in placed.Take(i).Where(p => date[p.Key] == before).Take(2))
			{
				edges.Add((p.Key, m.Key, true));
			}
		}
		layout.Edges.AddRange(edges);

		// 横轴：每个出现过的日期一列，列距按相隔的旬数，有上下限
		float x = Z(40);
		int? last = null;
		var dates = placed.Select(m => date[m.Key].Value).ToList();
		var sideDates = StoryAtlas.Missions.Where(m => !m.Main && m.StartScript.Length > 0 && m.DisplayFrom != null).Select(m => m.DisplayFrom.Value).ToList();
		// 支线可能早于或晚于主线，补足横轴两端，区间内仍沿用插值。
		if (sideDates.Count > 0)
		{
			if (dates.Count == 0 || sideDates.Min() < dates.Min()) dates.Add(sideDates.Min());
			if (sideDates.Max() > dates.Max()) dates.Add(sideDates.Max());
		}
		foreach (int d in dates.Distinct().OrderBy(v => v))
		{
			if (last != null)
			{
				x += Mathf.Clamp((d - last.Value) * Z(14), layout.LabelWidth + Z(16), Z(260));
			}
			layout.Columns.Add((x, d));
			last = d;
		}

		// 一个主线任务演好几节（4-5 接着演 4-6 到 4-8 这类）：后面几节各成一站，排在它后面；先在它和下一列之间留出位置
		var parts = SplitSections(placed);
		float partGap = layout.LabelWidth + Z(24);
		foreach (var group in parts.GroupBy(kv => date[kv.Key].Value).OrderBy(g => g.Key))
		{
			int col = layout.Columns.FindIndex(c => c.Date == group.Key);
			if (col < 0 || col + 1 >= layout.Columns.Count)
			{
				continue;
			}
			float need = (group.Max(kv => kv.Value.Count) + 1) * partGap - (layout.Columns[col + 1].X - layout.Columns[col].X);
			if (need <= 0f)
			{
				continue;
			}
			for (int i = col + 1; i < layout.Columns.Count; i++)
			{
				layout.Columns[i] = (layout.Columns[i].X + need, layout.Columns[i].Date);
			}
		}

		// 车道：先挑一条贯穿全程的主线放在第 0 道（每到一个岔口，走这一局走过的那条；都没走过就走后面还长的那条），
		// 其余的站从主线上下两边就近找空车道，岔路的第一条接着沿用岔出去的那条车道，合流时回到主线
		var children = edges.GroupBy(e => e.From).ToDictionary(g => g.Key, g => g.Select(e => e.To).Distinct().OrderBy(k => date[k] ?? int.MaxValue).ThenBy(k => k, StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
		var depth = new Dictionary<string, int>(StringComparer.Ordinal);
		int Depth(string key, int guard)
		{
			if (depth.TryGetValue(key, out int d))
			{
				return d;
			}
			depth[key] = 0;
			int best = 0;
			if (guard < 200 && children.TryGetValue(key, out var next))
			{
				foreach (string n in next.Where(n => date[n] >= date[key]))
				{
					best = Mathf.Max(best, 1 + Depth(n, guard + 1));
				}
			}
			return depth[key] = best;
		}
		var placedKeys = new HashSet<string>(placed.Select(m => m.Key), StringComparer.Ordinal);
		string at = placed.Count > 0 ? placed[0].Key : null;
		while (at != null && layout.Spine.Add(at))
		{
			at = children.TryGetValue(at, out var next) ? next.Where(n => placedKeys.Contains(n) && date[n] >= date[at] && !layout.Spine.Contains(n))
				.OrderByDescending(n => mains[n].Visited ? 1 : 0).ThenByDescending(n => Depth(n, 0)).FirstOrDefault() : null;
		}
		// 名字按实际显示的宽度算，同一条车道上两站的名字不叠在一起
		var nameStyle = new GUIStyle(_boxTitle) { fontSize = Mathf.Max(9, Mathf.RoundToInt(_boxTitle.fontSize * _routeZoom)), wordWrap = false };
		var widths = placed.ToDictionary(m => m.Key, m => Mathf.Max(layout.LabelWidth * 0.6f, nameStyle.CalcSize(new GUIContent(TextConverter.ForDisplay("✓ " + Short(m.NumberedTitle, 9)))).x), StringComparer.Ordinal);
		var usedWidth = new Dictionary<int, List<(float X, float W)>>();
		bool Free(int l, float px, float pw) => !usedWidth.TryGetValue(l, out var xs) || xs.All(v => Mathf.Abs(v.X - px) >= (v.W + pw) / 2f + Z(14));
		var lane = new Dictionary<string, int>(StringComparer.Ordinal);
		void Take(string key, int l)
		{
			lane[key] = l;
			if (!usedWidth.TryGetValue(l, out var list))
			{
				usedWidth[l] = list = new List<(float, float)>();
			}
			list.Add((XOf(layout, date[key].Value), widths[key]));
		}
		// 主线上同一天的两站会落在同一个横坐标：第一站占中间这道，后面的挪到旁边空着的车道
		int Nearest(float px, float pw, bool allowSpine)
		{
			for (int step = 0; step < 24; step++)
			{
				int candidate = step == 0 ? 0 : step % 2 == 1 ? (step + 1) / 2 : -(step / 2);
				if ((allowSpine || candidate != 0) && Free(candidate, px, pw))
				{
					return candidate;
				}
			}
			return allowSpine ? 0 : 1;
		}
		// 手工排好车道的站先放；同一天落在同一道上的，往离主线远的方向挪一道（主线上的往两边找）
		foreach (StoryMission m in placed.Where(m => RouteLanes.ContainsKey(m.Key)))
		{
			int want = RouteLanes[m.Key], chosen = want;
			float px = XOf(layout, date[m.Key].Value);
			for (int step = 0; step < 12; step++)
			{
				int candidate = want != 0 ? want + Math.Sign(want) * step : step == 0 ? 0 : step % 2 == 1 ? (step + 1) / 2 : -(step / 2);
				if (Free(candidate, px, widths[m.Key]))
				{
					chosen = candidate;
					break;
				}
			}
			Take(m.Key, chosen);
		}
		foreach (StoryMission m in placed.Where(m => layout.Spine.Contains(m.Key) && !lane.ContainsKey(m.Key)))
		{
			Take(m.Key, Nearest(XOf(layout, date[m.Key].Value), widths[m.Key], true));
		}
		// 岔路按整条路线放：沿着剧本写明的连线一站接一站的算同一条路线（崆峒留学、青城留学、留守唐门……），
		// 从头到尾占同一条车道，别的路线不能插进它占着的这一段，几条并行的路线才不会交错在一起。
		// 一站往后岔出几条路时，第一条接着这条路线，其余各自另起一条
		var track = new Dictionary<string, int>(StringComparer.Ordinal);
		var tracks = new List<List<string>>();
		foreach (StoryMission m in placed.Where(m => !lane.ContainsKey(m.Key)))
		{
			int t = -1;
			foreach (var e in edges.Where(e => !e.ByDate && e.To == m.Key && track.ContainsKey(e.From)))
			{
				List<string> run = tracks[track[e.From]];
				float tail = XOf(layout, date[e.From].Value), here = XOf(layout, date[m.Key].Value);
				if (run[run.Count - 1] == e.From && here - tail >= (widths[e.From] + widths[m.Key]) / 2f + Z(14))
				{
					t = track[e.From];
					break;
				}
			}
			if (t < 0)
			{
				t = tracks.Count;
				tracks.Add(new List<string>());
			}
			tracks[t].Add(m.Key);
			track[m.Key] = t;
		}
		// 这一道上 [x0, x1] 这一段空着：别的站（连同名字的宽度）都不在里面
		bool FreeSpan(int l, float x0, float x1) => !usedWidth.TryGetValue(l, out var xs) || xs.All(v => v.X + v.W / 2f + Z(14) <= x0 || v.X - v.W / 2f - Z(14) >= x1);
		foreach (List<string> run in tracks)
		{
			float x0 = run.Min(k => XOf(layout, date[k].Value) - widths[k] / 2f), x1 = run.Max(k => XOf(layout, date[k].Value) + widths[k] / 2f);
			// 岔出来的线沿这条车道从出发那站走过来，合回去的线沿这条车道走到要合的那站前面：这两段也算这条路线占着的
			var inRun = new HashSet<string>(run, StringComparer.Ordinal);
			foreach (var e in edges.Where(e => !e.ByDate && inRun.Contains(e.To) && !inRun.Contains(e.From) && date[e.From] != null))
			{
				x0 = Mathf.Min(x0, XOf(layout, date[e.From].Value) + Z(20));
			}
			foreach (var e in edges.Where(e => !e.ByDate && inRun.Contains(e.From) && !inRun.Contains(e.To) && date[e.To] != null))
			{
				x1 = Mathf.Max(x1, XOf(layout, date[e.To].Value) - Z(20));
			}
			// 从岔路上再岔出来的挨着那条岔路找；从主线上岔出来的，从主线下面一道开始找
			string from = edges.Where(e => e.To == run[0] && lane.ContainsKey(e.From)).Select(e => e.From).OrderBy(k => layout.Spine.Contains(k) ? 1 : 0).FirstOrDefault();
			int want = from == null || lane[from] == 0 ? 1 : lane[from];
			int chosen = want;
			for (int step = 0; step < 40; step++)
			{
				int candidate = want + (step % 2 == 0 ? step / 2 : -(step / 2 + 1));
				if (candidate != 0 && FreeSpan(candidate, x0, x1))
				{
					chosen = candidate;
					break;
				}
			}
			foreach (string k in run)
			{
				Take(k, chosen);
			}
			usedWidth[chosen].Add(((x0 + x1) / 2f, x1 - x0 - Z(28)));
		}
		// 放完以后主线就是第 0 道上的站：走线、按日期接的虚线都按它
		layout.Spine.Clear();
		layout.Spine.UnionWith(lane.Where(kv => kv.Value == 0).Select(kv => kv.Key));
		// 没有剧本写明会走到的站（按日期自动开始的）只从主线上日期最近的前一站接虚线，不从别的岔路接过来，免得几条路线连在一起
		var dated = new List<(string From, string To, bool ByDate)>();
		foreach (string to in edges.Where(e => e.ByDate).Select(e => e.To).Distinct().Where(k => !RouteLanes.TryGetValue(k, out int fixedLane) || fixedLane == 0).ToList())
		{
			string src = placed.Where(p => layout.Spine.Contains(p.Key) && date[p.Key] < date[to]).OrderByDescending(p => date[p.Key]).Select(p => p.Key).FirstOrDefault();
			if (src != null)
			{
				dated.Add((src, to, true));
			}
		}
		edges.RemoveAll(e => e.ByDate);
		edges.AddRange(dated);
		layout.Edges.Clear();
		layout.Edges.AddRange(edges);
		// 岔开的线上写触发条件：去掉每条都有的时间、行动次数，留第一条，写短
		foreach (var (from, to, byDate) in edges)
		{
			if (!children.TryGetValue(from, out var outs) || outs.Count < 2 || !mains.TryGetValue(to, out StoryMission target))
			{
				continue;
			}
			string clause = DataHelper.ConditionSummary(target.Conditions, TextConverter.ForDisplay);
			if (clause.Length > 0)
			{
				layout.EdgeLabels[(from, to)] = Short(clause, 12);
			}
		}
		int minLane = lane.Values.DefaultIfEmpty(0).Min(), maxLane = lane.Values.DefaultIfEmpty(0).Max();
		float top = Z(70), laneHeight = Z(96);
		foreach (StoryMission m in placed)
		{
			layout.Points[m.Key] = new Vector2(XOf(layout, date[m.Key].Value), top + (lane[m.Key] - minLane) * laneHeight + Z(20));
		}
		layout.MainBottom = top + (maxLane - minLane + 1) * laneHeight;
		PlaceParts(layout, parts, partGap);

		// 支线：每个任务组一颗胶囊，放在最早能触发的日期上，一行放不下就换行
		float y = layout.MainBottom + Z(56);
		var rows = new List<float>();
		var groups = StoryAtlas.Missions.Where(m => !m.Main && m.StartScript.Length > 0).GroupBy(m => m.Series)
			.Select(g => new SideStory
			{
				Series = g.Key,
				Title = SideTitle(g.ToList()),
				Missions = g.ToList(),
				Joins = g.SelectMany(m => m.Next).Distinct().Where(mains.ContainsKey).Select(k => mains[k]).ToList(),
				Dated = g.Any(m => m.DisplayFrom != null),
				Read = StoryScripts.ReadPercent(g.SelectMany(m => m.Scripts).Distinct().Select(StoryScripts.Get).Where(s => s != null).SelectMany(s => s.Lines)),
				Visited = g.Any(m => m.Visited)
			}).OrderBy(s => s.Dated ? s.Missions.Where(m => m.DisplayFrom != null).Min(m => m.DisplayFrom.Value) : int.MaxValue).ToList();
		var pillStyle = new GUIStyle(_boxTitle) { fontSize = Mathf.Max(9, Mathf.RoundToInt(_boxTitle.fontSize * _routeZoom)) };
		// 签条：左边一道墨条，前面留「✓ 」的位置
		foreach (SideStory side in groups)
		{
			float sx = side.Dated ? XOf(layout, side.Missions.Where(m => m.DisplayFrom != null).Min(m => m.DisplayFrom.Value)) - Z(10) : Z(20);
			float w = pillStyle.CalcSize(new GUIContent(TextConverter.ForDisplay("✓ " + side.Title + (side.Joins.Count > 0 ? " → 主线" : "")))).x + Z(26);
			int row = rows.FindIndex(end => end <= sx);
			if (row < 0)
			{
				row = rows.Count;
				rows.Add(0);
			}
			rows[row] = sx + w + Z(10);
			side.Pill = new Rect(sx, y + row * Z(30), w, Z(24));
			layout.Side.Add(side);
		}
		layout.Width = Mathf.Max(Mathf.Max(layout.Columns.Count > 0 ? layout.Columns[layout.Columns.Count - 1].X : 0, layout.Points.Values.Select(v => v.x).DefaultIfEmpty(0).Max()), rows.DefaultIfEmpty(0).Max()) + Z(200);
		layout.Height = y + rows.Count * Z(30) + Z(30);
		return layout;
	}

	/// <summary>
	/// 支线签条的名字：任务组自己的名字；游戏里没起名（只有 S2321 这样的编号）的，
	/// 用最早那个任务的说明，再没有就用它的第一条剧情记录。
	/// </summary>
	private static string SideTitle(List<StoryMission> missions)
	{
		string title = missions[0].SeriesTitle;
		if (title.Any(c => c >= 128))
		{
			return title;
		}
		var first = missions.OrderBy(m => m.DisplayFrom ?? int.MaxValue).ThenBy(m => m.Key, StringComparer.Ordinal).ToList();
		string named = first.Select(m => m.Desc).FirstOrDefault(d => d.Any(c => c >= 128))
			?? first.Select(m => m.Summary).FirstOrDefault(d => d.Length > 0);
		return named != null ? Short(named, 12) : title;
	}

	// 剧本编号「ch4_6_…」：第 4 章第 6 节，和任务名「4-6 …」的编号对得上
	private static readonly System.Text.RegularExpressions.Regex SectionOf = new System.Text.RegularExpressions.Regex(@"^ch(\d+)_(\d+)(?:_|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

	/// <summary>
	/// 主线任务按章节拆站：游戏只给任务起了名字、定了日期，一个任务常常接着演好几节（4-5 跟着大师兄演到 4-8）。
	/// 和开始剧本同一章、编号在它后面的每一节各成一小站，按编号排；别的章节借用的剧本、开始那一节留在原来那站。
	/// </summary>
	private Dictionary<string, List<StoryMission>> SplitSections(List<StoryMission> placed)
	{
		_routeParts.Clear();
		var result = new Dictionary<string, List<StoryMission>>(StringComparer.Ordinal);
		var visited = new HashSet<string>(PlayerStatManagerData.Instance?.StoryKeyList ?? new List<string>(), StringComparer.Ordinal);
		foreach (StoryMission m in placed)
		{
			System.Text.RegularExpressions.Match start = SectionOf.Match(m.StartScript);
			if (!start.Success)
			{
				continue;
			}
			int chapter = int.Parse(start.Groups[1].Value), first = int.Parse(start.Groups[2].Value);
			var sections = m.Scripts
				.Select(name => (Name: name, Match: SectionOf.Match(name)))
				.Where(x => x.Match.Success && int.Parse(x.Match.Groups[1].Value) == chapter && int.Parse(x.Match.Groups[2].Value) > first)
				.GroupBy(x => int.Parse(x.Match.Groups[2].Value))
				.OrderBy(g => g.Key)
				.ToList();
			if (sections.Count == 0)
			{
				continue;
			}
			var list = new List<StoryMission>();
			foreach (var section in sections)
			{
				var names = section.Select(x => x.Name).OrderBy(ScriptOrder, StringComparer.Ordinal).ToList();
				var scripts = names.Select(StoryScripts.Get).Where(x => x != null).ToList();
				string legend = scripts.SelectMany(x => x.Records).Select(StoryScripts.Legend).FirstOrDefault(t => t.Length > 0) ?? "";
				var part = new StoryMission
				{
					Key = m.Key + "/" + chapter + "-" + section.Key,
					Series = m.Series,
					SeriesTitle = m.SeriesTitle,
					Main = true,
					Desc = StoryMission.ChapterOf("ch" + chapter + "_" + section.Key) + "　" + (legend.Length > 0 ? Short(legend, 12) : m.Title),
					Summary = legend,
					When = m.When,
					From = m.From,
					To = m.To,
					DisplayFrom = m.DisplayFrom,
					DisplayTo = m.DisplayTo,
					StartScript = names[0],
					Scripts = names,
					Read = StoryScripts.ReadPercent(scripts.SelectMany(x => x.Lines)),
					Visited = scripts.Any(x => x.Records.Any(visited.Contains)),
					Endings = StoryAtlas.Endings.Count(e => !e.Death && e.Ways.Any(w => names.Contains(w.Script, StringComparer.OrdinalIgnoreCase))),
					PartOf = m,
				};
				list.Add(part);
				_routeParts[part.Key] = part;
			}
			result[m.Key] = list;
		}
		return result;
	}

	// 剧本名里的数字按大小排：ch4_8_2 在 ch4_8_10 前面
	private static string ScriptOrder(string name) => System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), @"\d+", d => d.Value.PadLeft(4, '0'));

	/// <summary>小站排在所属那站后面同一道上，一站接一站；后面几节里写明的下一个主线任务，改从那一节连过去。</summary>
	private void PlaceParts(RouteLayout layout, Dictionary<string, List<StoryMission>> parts, float gap)
	{
		foreach (var pair in parts)
		{
			if (!layout.Points.TryGetValue(pair.Key, out Vector2 at))
			{
				continue;
			}
			List<StoryMission> list = pair.Value;
			for (int i = 0; i < list.Count; i++)
			{
				layout.Points[list[i].Key] = new Vector2(at.x + (i + 1) * gap, at.y);
				if (layout.Spine.Contains(pair.Key))
				{
					layout.Spine.Add(list[i].Key);
				}
			}
			// 每个下一站由哪一节写明：那一节的剧本里有接到它的去处
			string SourceOf(string target)
			{
				for (int i = list.Count - 1; i >= 0; i--)
				{
					if (list[i].Scripts.Any(name => StoryScripts.Get(name)?.Exits.Any(e => e.MainMission == target) == true))
					{
						return list[i].Key;
					}
				}
				return pair.Key;
			}
			var edges = layout.Edges.ToList();
			layout.Edges.Clear();
			foreach (var e in edges)
			{
				if (e.From != pair.Key)
				{
					layout.Edges.Add(e);
					continue;
				}
				// 按日期接的虚线从最后一节出去
				string from = e.ByDate ? list[list.Count - 1].Key : SourceOf(e.To);
				layout.Edges.Add((from, e.To, e.ByDate));
				if (from != e.From && layout.EdgeLabels.TryGetValue((e.From, e.To), out string label))
				{
					layout.EdgeLabels.Remove((e.From, e.To));
					layout.EdgeLabels[(from, e.To)] = label;
				}
			}
			layout.Edges.Add((pair.Key, list[0].Key, false));
			for (int i = 1; i < list.Count; i++)
			{
				layout.Edges.Add((list[i - 1].Key, list[i].Key, false));
			}
		}
	}

	/// <summary>日期 → 横坐标：落在两列之间的按旬数插进去。</summary>
	private static float XOf(RouteLayout layout, int date)
	{
		var cols = layout.Columns;
		if (cols.Count == 0)
		{
			return 0;
		}
		if (date <= cols[0].Date)
		{
			return cols[0].X;
		}
		for (int i = 1; i < cols.Count; i++)
		{
			if (date <= cols[i].Date)
			{
				float t = (float)(date - cols[i - 1].Date) / Mathf.Max(1, cols[i].Date - cols[i - 1].Date);
				return Mathf.Lerp(cols[i - 1].X, cols[i].X, t);
			}
		}
		return cols[cols.Count - 1].X;
	}

	private void DrawRouteCanvas(Rect canvas, string query)
	{
		RouteLayout layout = _route;
		Event e = Event.current;
		// Ctrl + 滚轮：以鼠标所在的位置为中心缩放（地图按新比例重排后，鼠标下面还是同一个地方）
		if (e.type == EventType.ScrollWheel && canvas.Contains(e.mousePosition) && (e.control || e.command))
		{
			float before = _routeZoom;
			float after = Mathf.Clamp(Mathf.Round((before + (e.delta.y < 0 ? 0.1f : -0.1f)) * 10f) / 10f, 0.6f, 1.6f);
			if (!Mathf.Approximately(before, after))
			{
				Vector2 mouse = e.mousePosition - canvas.position;
				_routeScroll = Vector2.Max(Vector2.zero, (_routeScroll + mouse) * (after / before) - mouse);
				_routeZoom = after;
			}
			e.Use();
		}
		else if (e.type == EventType.ScrollWheel && canvas.Contains(e.mousePosition) && e.shift)
		{
			_routeScroll.x += e.delta.y * Z(30);
			e.Use();
		}
		_routeScroll = GUI.BeginScrollView(canvas, _routeScroll, new Rect(0, 0, Mathf.Max(layout.Width, canvas.width) + canvas.width * 0.3f, Mathf.Max(layout.Height, canvas.height) + canvas.height * 0.4f));
		int pan = GUIUtility.GetControlID(FocusType.Passive);
		var view = new Rect(_routeScroll.x, _routeScroll.y, canvas.width, canvas.height);
		bool repaint = e.type == EventType.Repaint;
		var tiny = new GUIStyle(_boxSmall) { fontSize = Mathf.Max(8, Mathf.RoundToInt(_boxSmall.fontSize * _routeZoom)), alignment = TextAnchor.UpperCenter, wordWrap = true, clipping = TextClipping.Overflow };
		var name = new GUIStyle(_boxTitle) { fontSize = Mathf.Max(9, Mathf.RoundToInt(_boxTitle.fontSize * _routeZoom)), alignment = TextAnchor.UpperCenter, wordWrap = true, clipping = TextClipping.Overflow };
		if (repaint)
		{
			// 日历：每年一条淡墨底色，每列标上月份
			int year = -1;
			for (int i = 0; i < layout.Columns.Count; i++)
			{
				var (cx, d) = layout.Columns[i];
				if (d / 36 != year)
				{
					year = d / 36;
					float end = layout.Columns.Skip(i).Where(c => c.Date / 36 != year).Select(c => c.X - layout.LabelWidth / 2).DefaultIfEmpty(layout.Width).First();
					Fill(new Rect(cx - layout.LabelWidth / 2, 0, end - cx + layout.LabelWidth / 2, layout.Height), new Color(Ink.r, Ink.g, Ink.b, year % 2 == 0 ? 0.02f : 0.05f));
					Fill(new Rect(cx - layout.LabelWidth / 2, 0, Z(2), layout.Height), new Color(Ink.r, Ink.g, Ink.b, 0.18f));
					GUI.Label(new Rect(cx - layout.LabelWidth / 2 + Z(8), Z(6), Z(140), Z(26)), $"<b>第{year + 1}年</b>", name);
				}
				Fill(new Rect(cx, Z(56), 1, layout.MainBottom - Z(56)), new Color(Ink.r, Ink.g, Ink.b, 0.07f));
				GUI.Label(new Rect(cx - Z(40), Z(34), Z(80), Z(20)), $"{d % 36 / 3 + 1}月{StoryAtlas.StageText(d % 3 + 1).Substring(0, 1)}", tiny);
			}
			Fill(new Rect(0, layout.MainBottom + Z(16), layout.Width, Z(2)), InkLine);
			GUI.Label(new Rect(view.x + Z(12), layout.MainBottom + Z(24), Z(200), Z(26)), "<b>支线</b>", name);
			// 连线：这一局走过的两站之间用青色粗线
			foreach (var (from, to, byDate) in layout.Edges)
			{
				if (!layout.Points.TryGetValue(from, out Vector2 a) || !layout.Points.TryGetValue(to, out Vector2 b))
				{
					continue;
				}
				StoryMission ma = RouteFind(from), mb = RouteFind(to);
				bool walked = ma != null && mb != null && ma.Visited && mb.Visited;
				// 岔开的线在出发的那一站后面就拐，合流的线在到的那一站前面才拐，看得出从哪分、到哪合
				int outs = layout.Edges.Count(x => x.From == from), ins = layout.Edges.Count(x => x.To == to);
				bool fromSpine = layout.Spine.Contains(from), toSpine = layout.Spine.Contains(to);
				// 从主线岔到岔路：一离开就拐，沿岔路那道走；从岔路合回主线：沿岔路那道走到主线那站前面才拐。都不沿着主线走，不和主线叠在一起
				float bend = fromSpine && !toSpine ? a.x + Z(26) : !fromSpine && toSpine ? b.x - Z(26) : outs > 1 && ins <= 1 ? a.x + Z(26) : b.x - Z(26);
				Elbow(new Vector2(a.x + Z(10), a.y), new Vector2(b.x - Z(10), b.y), walked ? Jade : InkLine, walked ? Z(3) : Z(2), byDate, bend);
				// 岔开的线上写它的触发条件，写在拐过去以后那段横线上面
				if (layout.EdgeLabels.TryGetValue((from, to), out string why) && Mathf.Abs(a.y - b.y) > 1f)
				{
					var whyStyle = new GUIStyle(tiny) { alignment = TextAnchor.LowerLeft, wordWrap = false, clipping = TextClipping.Overflow };
					float whyWidth = whyStyle.CalcSize(new GUIContent(TextConverter.ForDisplay(why))).x + Z(4);
					GUI.Label(new Rect(bend + Z(6), b.y - Z(22), whyWidth, Z(18)), "<color=#A3302A>" + why + "</color>", whyStyle);
				}
			}
		}
		// 站点：墨圈，读得越多圈里墨越浓；走过的名字前打勾，当前主线盖一枚朱砂印，接在它后面的下一站套一圈金色
		var upcoming = new HashSet<string>(NextStations(layout, StoryAtlas.CurrentMain), StringComparer.Ordinal);
		foreach (var pair in layout.Points)
		{
			StoryMission m = RouteFind(pair.Key);
			Vector2 p = pair.Value;
			var hit = new Rect(p.x - layout.LabelWidth / 2, p.y - Z(14), layout.LabelWidth, Z(66));
			if (m == null || !hit.Overlaps(view))
			{
				continue;
			}
			bool dim = query.Length > 0 && !Matches(query, m.NumberedTitle, m.Key, m.Place, m.Summary) || _romanceMissions != null && !_romanceMissions.Contains(m.PartOf ?? m);
			if (repaint)
			{
				float rr = Z(9), alpha = dim ? 0.25f : 1f;
				var dot = new Rect(p.x - rr, p.y - rr, rr * 2, rr * 2);
				Round(dot, new Color(PaperLight.r, PaperLight.g, PaperLight.b, alpha), rr);
				if (m.Read != null)
				{
					Round(dot, new Color(Ink.r, Ink.g, Ink.b, alpha * (0.12f + 0.88f * m.Read.Value / 100f)), rr);
				}
				Round(dot, new Color(Ink.r, Ink.g, Ink.b, alpha), rr, Z(2));
				if (upcoming.Contains(m.Key))
				{
					Round(new Rect(dot.x - Z(2), dot.y - Z(2), dot.width + Z(4), dot.height + Z(4)), new Color(Gold.r, Gold.g, Gold.b, alpha), rr + Z(2), Z(3));
				}
				if (_routePicked == m)
				{
					Round(new Rect(dot.x - Z(4), dot.y - Z(4), dot.width + Z(8), dot.height + Z(8)), Cinnabar, rr + Z(4), Z(2));
				}
				if (StoryAtlas.CurrentMain == m.Key)
				{
					var stamp = new Rect(p.x + Z(9), p.y - Z(30), Z(22), Z(22));
					Round(stamp, Cinnabar, Z(3));
					GUI.Label(stamp, "今", new GUIStyle(_seal) { fontSize = Mathf.Max(8, Mathf.RoundToInt(_seal.fontSize * 0.7f * _routeZoom)) });
				}
				if (m.Endings > 0)
				{
					GUI.Label(new Rect(p.x - Z(34), p.y - Z(22), Z(24), Z(18)), "<color=#A3302A><b>终</b></color>", tiny);
				}
				var label = new Rect(p.x - layout.LabelWidth / 2 - Z(20), p.y + Z(13), layout.LabelWidth + Z(40), Z(24));
				// 结缘路线：设她结缘旗标的站，名字后面用朱砂色标出是哪一步（【认识】【告白·攻略】…）
				string title = (m.Visited ? "<color=#2F6B3A>✓</color> " : "") + Short(m.NumberedTitle, 9) + RomanceMark(m);
				GUI.Label(label, dim ? "<color=#B5AA98>" + RichText.Strip(title) + "</color>" : title, new GUIStyle(name) { wordWrap = false });
			}
			if (GUI.Button(hit, GUIContent.none, GUIStyle.none))
			{
				_routePicked = m;
				_routeSide = null;
				_routeDetailScroll = Vector2.zero;
			}
		}
		// 支线签条
		foreach (SideStory side in layout.Side)
		{
			if (!side.Pill.Overlaps(view))
			{
				continue;
			}
			bool dim = query.Length > 0 && !Matches(query, side.Title) && !side.Missions.Any(m => Matches(query, m.NumberedTitle, m.Key, m.Place, m.Summary))
				|| _romanceMissions != null && !side.Missions.Any(_romanceMissions.Contains);
			if (repaint)
			{
				float alpha = dim ? 0.3f : 1f;
				Fill(side.Pill, new Color(PaperLight.r, PaperLight.g, PaperLight.b, alpha));
				Frame(side.Pill, _routeSide == side ? Cinnabar : new Color(Edge.r, Edge.g, Edge.b, alpha), _routeSide == side ? Z(2) : 1);
				Fill(new Rect(side.Pill.x, side.Pill.y, Z(4), side.Pill.height), new Color(Ink.r, Ink.g, Ink.b, alpha));
				if (side.Read != null)
				{
					ReadBar(new Rect(side.Pill.x + Z(6), side.Pill.yMax - Z(4), side.Pill.width - Z(10), Z(2)), side.Read);
				}
				var tagText = new GUIStyle(name) { alignment = TextAnchor.MiddleCenter, wordWrap = false };
				// 走完会接到某一站主线的，后面标「→ 主线」
				string text = (side.Visited ? "<color=#2F6B3A>✓</color> " : "") + side.Title + (side.Joins.Count > 0 ? " <color=#A3302A>→ 主线</color>" : "");
				GUI.Label(new Rect(side.Pill.x + Z(4), side.Pill.y - Z(1), side.Pill.width - Z(4), side.Pill.height), dim ? "<color=#B5AA98>" + side.Title + "</color>" : text, tagText);
			}
			if (GUI.Button(side.Pill, GUIContent.none, GUIStyle.none))
			{
				_routeSide = side;
				_routePicked = null;
				_routeDetailScroll = Vector2.zero;
			}
		}
		// 按住空白处拖动地图
		switch (e.GetTypeForControl(pan))
		{
			case EventType.MouseDown when e.button == 0 && new Rect(view.x, view.y, view.width - S(22), view.height - S(22)).Contains(e.mousePosition):
				GUIUtility.hotControl = pan;
				e.Use();
				break;
			case EventType.MouseDrag when GUIUtility.hotControl == pan:
				_routeScroll -= e.delta;
				e.Use();
				break;
			case EventType.MouseUp when GUIUtility.hotControl == pan:
				GUIUtility.hotControl = 0;
				e.Use();
				break;
		}
		GUI.EndScrollView();
	}

	private void DrawMissionDetail(StoryMission m)
	{
		DrawRomanceOnMission(m);
		GUILayout.Label((m.Main ? "主线" : m.SeriesTitle) + "　" + (m.PartOf?.Key ?? m.Key), _muted);
		GUILayout.Label(m.NumberedTitle, _heading);
		if (m.PartOf != null && GUILayout.Button(string.Format("属于主线任务「{0}」", m.PartOf.NumberedTitle), _button))
		{
			_routePicked = m.PartOf;
			_routeDetailScroll = Vector2.zero;
		}
		if (m.Summary.Length > 0)
		{
			GUILayout.Label(m.Summary, _muted);
		}
		GUILayout.Space(S(6));
		GUILayout.Label(m.When + (m.Place.Length > 0 ? "　·　" + m.Place : ""), _label);
		GUILayout.Label((m.Visited ? "<color=#2F6B3A>✓ 这一局走过</color>" : "这一局没走过") + (m.Read != null ? $"　·　对白读过 {m.Read}%" : "") + (m.Endings > 0 ? $"　·　<color=#A3302A>{m.Endings} 个结局</color>" : ""), _label);
		if (StoryAtlas.CurrentMain == m.Key)
		{
			GUILayout.Label("<color=#A3302A><b>当前主线</b></color>", _label);
		}
		DrawNext(m);
		if (GUILayout.Button("看剧情图", _primary, GUILayout.Height(S(38))))
		{
			// 拆出来的小站打开整个任务的剧情图，选中这一节开头
			OpenFlow(m.PartOf ?? m, m.PartOf != null ? m.StartScript : null);
		}
		JumpButton(m.PartOf ?? m, m.StartScript);
		string conditions = StoryAtlas.ConditionText(m);
		if (conditions.Length > 0)
		{
			GUILayout.Space(S(10));
			GUILayout.Label("触发条件", _heading);
			GUILayout.Label(OnPaper(conditions), _rich);
		}
		DrawStationOptions(m);
		DrawSections(m);
		DrawEntries(m);
		var before = _route != null && _route.Points.ContainsKey(m.Key)
			? _route.Edges.Where(e => e.To == m.Key && !e.ByDate).Select(e => RouteFind(e.From)).Where(x => x != null).Distinct().ToList()
			: StoryAtlas.Missions.Where(x => x.Main && x.Next.Contains(m.Key)).ToList();
		if (before.Count > 0)
		{
			GUILayout.Space(S(10));
			GUILayout.Label("从哪来", _heading);
			foreach (StoryMission x in before)
			{
				if (GUILayout.Button("← " + x.NumberedTitle, _button))
				{
					_routePicked = x;
				}
			}
		}
		var kept = m.Scripts.Select(StoryNodes.Latest).Where(n => n != null).ToList();
		if (kept.Count > 0)
		{
			GUILayout.Space(S(10));
			GUILayout.Label("可以从这里重玩", _heading);
			foreach (StoryNode node in kept.Take(8))
			{
				ReplayButton(node, StoryScripts.SegmentName(node.Script) + "　" + $"第{node.Year}年{node.Month}月", _button);
			}
		}
	}

	/// <summary>
	/// 接下来：主线写接着的几站（剧本里明写的、按日期接上的都算，岔开的写触发条件）；支线写这条支线的下一步，最后一步写走完接回哪站主线。
	/// 每项写什么时候、在哪、这一局走过没有，点一下换到那一站。
	/// </summary>
	private void DrawNext(StoryMission m)
	{
		var next = new List<(StoryMission To, string Why, bool Join)>();
		if (m.Main)
		{
			foreach (string key in _route != null ? NextStations(_route, m.Key) : new List<string>())
			{
				StoryMission to = RouteFind(key);
				if (to != null && next.All(n => n.To != to))
				{
					next.Add((to, _route.EdgeLabels.TryGetValue((m.Key, key), out string label) ? label : "", false));
				}
			}
			foreach (StoryMission to in (m.PartOf != null || _routeParts.Values.Any(p => p.PartOf == m) ? new List<string>() : m.Next).Select(StoryAtlas.Find).Where(x => x != null && next.All(n => n.To != x)))
			{
				next.Add((to, "", false));
			}
		}
		else if (_route?.Side.FirstOrDefault(s => s.Missions.Contains(m)) is SideStory side)
		{
			var order = side.Missions.OrderBy(x => x.DisplayFrom ?? int.MaxValue).ToList();
			int at = order.IndexOf(m);
			if (at >= 0 && at + 1 < order.Count)
			{
				next.Add((order[at + 1], "", false));
			}
			else
			{
				next.AddRange(side.Joins.Select(j => (j, "", true)));
			}
		}
		if (next.Count == 0)
		{
			return;
		}
		GUILayout.Space(S(10));
		GUILayout.Label("接下来", _heading);
		foreach (var (to, why, join) in next.Take(4))
		{
			GUILayout.BeginVertical(_card);
			if (GUILayout.Button(join ? string.Format("走完接到主线「{0}」", to.NumberedTitle) : "→ " + to.NumberedTitle, new GUIStyle(_button) { alignment = TextAnchor.MiddleLeft, wordWrap = true }))
			{
				_routePicked = to;
				_routeSide = null;
				_routeDetailScroll = Vector2.zero;
			}
			GUILayout.Label(to.When + (to.Place.Length > 0 ? "　·　" + to.Place : "") + (to.Visited ? "　·　<color=#2F6B3A>✓ 这一局走过</color>" : ""), _rich);
			if (why.Length > 0)
			{
				GUILayout.Label(OnPaper(why), _small);
			}
			GUILayout.EndVertical();
		}
		GUILayout.Space(S(6));
	}

	private readonly HashSet<string> _routeOptionsOpen = new HashSet<string>(StringComparer.Ordinal);

	/// <summary>
	/// 这一站的选项：默认折起来（免得详情太长、剧透太多），展开后按剧情段列出每个选项要什么条件、选了数值好感旗标怎么变，
	/// 写法和「此刻」页、游戏里选项后面接的说明一样。
	/// </summary>
	private void DrawStationOptions(StoryMission m)
	{
		var options = m.Scripts.Select(StoryScripts.Get).Where(s => s != null)
			.Select(s => (Script: s, Options: s.Menus.SelectMany(menu => menu).Where(st => st.Kind == StoryStep.Option).GroupBy(st => st.Key).Select(g => g.First()).ToList()))
			.Where(x => x.Options.Count > 0).ToList();
		int count = options.Sum(x => x.Options.Count);
		if (count == 0)
		{
			return;
		}
		bool open = _routeOptionsOpen.Contains(m.Key);
		GUILayout.Space(S(10));
		if (GUILayout.Button((open ? "▼ " : "▶ ") + string.Format(TextConverter.ForDisplay("这一站的选项（{0}）"), count), new GUIStyle(_button) { alignment = TextAnchor.MiddleLeft }))
		{
			if (!_routeOptionsOpen.Remove(m.Key))
			{
				_routeOptionsOpen.Add(m.Key);
			}
		}
		if (!open)
		{
			return;
		}
		foreach (var (script, steps) in options)
		{
			if (options.Count > 1)
			{
				GUILayout.Label(StoryScripts.SegmentName(script.Name), _muted);
			}
			foreach (StoryStep step in steps.Take(40))
			{
				GUILayout.BeginVertical(_card);
				GUILayout.Label(Cached((step, "option"), () => OnPaper(StoryScripts.Describe(step, TextConverter.ForDisplay))), _rich);
				string effects = Cached((script, step.Key, "effects", PanelTheme.Dark), () => PanelDeltas(string.Join("　", script.Effects
					.Where(e => e.Path.Any(st => st.Kind == StoryStep.Option && st.Key == step.Key) && !e.Text.Contains("暂存"))
					.Select(e => e.Text).Distinct().ToArray())));
				if (effects.Length > 0)
				{
					GUILayout.Label(effects, _muted);
				}
				GUILayout.EndVertical();
			}
			if (steps.Count > 40)
			{
				GUILayout.Label($"……共 {steps.Count} 个", _muted);
			}
		}
	}

	/// <summary>
	/// 这一站接下来的站：先看连线；从这站出去没有连线的（剧本里没写下一站、靠日期接上的），按同一道上往后的第一站，
	/// 再没有就按主线上往后的第一站。
	/// </summary>
	private static List<string> NextStations(RouteLayout layout, string key)
	{
		var next = layout.Edges.Where(e => e.From == key).Select(e => e.To).Distinct().ToList();
		if (next.Count > 0 || key == null || !layout.Points.TryGetValue(key, out Vector2 at))
		{
			return next;
		}
		string after = layout.Points.Where(kv => Mathf.Abs(kv.Value.y - at.y) < 1f && kv.Value.x > at.x + 1f).OrderBy(kv => kv.Value.x).Select(kv => kv.Key).FirstOrDefault()
			?? layout.Points.Where(kv => layout.Spine.Contains(kv.Key) && kv.Value.x > at.x + 1f).OrderBy(kv => kv.Value.x).Select(kv => kv.Key).FirstOrDefault();
		if (after != null)
		{
			next.Add(after);
		}
		return next;
	}

	/// <summary>选中这一站并滚到它；图上没有这一站时返回 false。</summary>
	private bool PickStation(StoryMission m, Rect r)
	{
		if (m == null || _route == null || !_route.Points.TryGetValue(m.Key, out Vector2 p))
		{
			return false;
		}
		_routePicked = m;
		_routeSide = null;
		_routeDetailScroll = Vector2.zero;
		_routeScroll = new Vector2(Mathf.Max(0, p.x - r.width * 0.3f), Mathf.Max(0, p.y - r.height * 0.4f));
		return true;
	}

	/// <summary>地图上方一行：九位可以结缘的人，点一位看她的结缘路线，再点一次取消。</summary>
	private void DrawRomanceBar(Rect r)
	{
		GUIStyle line = Middle(_label);
		float x = r.x;
		GUI.Label(new Rect(x, r.y, S(90), r.height), "结缘路线", line);
		x += S(96);
		foreach (StoryRomance.Heroine h in StoryRomance.All)
		{
			string name = StoryRomance.Name(h);
			bool on = _routeHeroine == h;
			float w = _button.CalcSize(new GUIContent(name)).x + S(20);
			if (GUI.Button(new Rect(x, r.y, w, r.height), name, on ? _primary : _button))
			{
				_routeHeroine = on ? null : h;
				_routePicked = null;
				_routeSide = null;
				_routeDetailScroll = Vector2.zero;
			}
			x += w + S(6);
		}
		_romanceMissions = _romanceSteps = null;
		if (_routeHeroine != null && StoryRomance.StopsOf(_routeHeroine) is List<StoryRomance.Stop> stops)
		{
			_romanceMissions = new HashSet<StoryMission>(stops.Select(s => s.Mission));
			_romanceSteps = new HashSet<StoryMission>(stops.Where(s => s.Steps.Count > 0).Select(s => s.Mission));
		}
	}

	/// <summary>右边：她的好感、是不是心上人，结缘的每一步这一局做到没有、在哪拿到，以及加减她好感的剧情。</summary>
	private void DrawRomanceDetail(StoryRomance.Heroine h)
	{
		List<StoryRomance.Stop> stops = StoryRomance.StopsOf(h);
		if (stops == null)
		{
			GUILayout.Label("剧本还没整理好", _muted);
			return;
		}
		GUILayout.Label("结缘路线", _muted);
		GUILayout.Label(StoryRomance.Name(h), _heading);
		GUILayout.Label(string.Format("好感 {0}", StoryRomance.Affection(h)) + (StoryRomance.IsLover(h) ? "　·　<color=#A3302A><b>心上人</b></color>" : ""), WrapLabel());
		GUILayout.Space(S(10));
		GUILayout.Label("结缘进度（这一局）", _heading);
		foreach (string flag in h.Steps)
		{
			GUILayout.BeginVertical(_card, GUILayout.Width(_routeDetailInner));
			GUILayout.Label((StoryRomance.StepDone(flag) ? "<color=#2F6B3A><b>✓</b></color> " : "<color=#A3302A>✗</color> ") + StoryRomance.StepName(flag), WrapLabel());
			var where = stops.Where(s => s.Steps.Contains(flag)).ToList();
			if (where.Count == 0)
			{
				GUILayout.Label("剧本里没找到设这一步的地方", WrapSmall());
			}
			bool done = StoryRomance.StepDone(flag);
			foreach (StoryRomance.Stop s in where.Take(4))
			{
				RomanceStopButton(s, _routeDetailInner - S(24));
				string conditions = done ? "" : StoryAtlas.ConditionText(s.Mission);
				if (conditions.Length > 0)
				{
					GUILayout.Label(TextConverter.ForDisplay("触发条件（这一局的值）：") + OnPaper(conditions), WrapSmall(), GUILayout.Width(_routeDetailInner - S(24)));
				}
			}
			GUILayout.EndVertical();
		}
		GUILayout.Space(S(10));
		GUILayout.Label("加减好感的剧情", _heading);
		foreach (StoryRomance.Stop s in stops.Where(s => s.Affection != 0))
		{
			RomanceStopButton(s, _routeDetailInner);
		}
		int elsewhere = StoryRomance.ElsewhereOf(h);
		if (elsewhere > 0)
		{
			GUILayout.Label(string.Format("另有 {0} 处在养成随机事件等不属于任务的剧情里", elsewhere), WrapSmall());
		}
	}

	/// <summary>站名后面的结缘步骤：「【认识·告白】」；没选人或这站不设她的旗标时为空。</summary>
	private string RomanceMark(StoryMission m)
	{
		if (_routeHeroine == null || _romanceSteps == null || !_romanceSteps.Contains(m))
		{
			return "";
		}
		StoryRomance.Stop stop = StoryRomance.StopsOf(_routeHeroine)?.FirstOrDefault(s => s.Mission == m);
		return stop == null ? "" : " <color=#A3302A>" + StepMarks(stop) + "</color>";
	}

	// 步骤名拼在一串里，界面翻译整句对不上：先各自换成界面语言
	private static string StepMarks(StoryRomance.Stop s) => "【" + string.Join("·", s.Steps.Select(f => TextConverter.ForDisplay(StoryRomance.StepKind(f))).ToArray()) + "】";

	// 「　好感 +3　【认识】」
	private static string RomanceParts(StoryRomance.Stop s)
	{
		string text = s.Affection != 0 ? "　" + string.Format("好感 {0}", (s.Affection > 0 ? "+" : "") + s.Affection) : "";
		return text + (s.Steps.Count > 0 ? "　<color=#A3302A>" + StepMarks(s) + "</color>" : "");
	}

	/// <summary>选了人时，站点详情最上面：返回她的结缘路线，以及这一站和她有什么关系。</summary>
	private void DrawRomanceOnMission(StoryMission m)
	{
		if (_routeHeroine == null)
		{
			return;
		}
		string name = StoryRomance.Name(_routeHeroine);
		if (GUILayout.Button(string.Format("‹ 返回{0}的结缘路线", name), _button))
		{
			_routePicked = null;
			_routeSide = null;
			_routeDetailScroll = Vector2.zero;
			return;
		}
		StoryRomance.Stop stop = StoryRomance.StopsOf(_routeHeroine)?.FirstOrDefault(s => s.Mission == m);
		GUILayout.Label(stop == null ? string.Format("这一站不改{0}的好感，也不设她的结缘旗标", name) : TextConverter.ForDisplay(string.Format("和{0}有关：", name)) + RomanceParts(stop).TrimStart('　'), WrapSmall(), GUILayout.Width(_routeDetailInner));
		GUILayout.Space(S(6));
	}

	private GUIStyle _wrapLabel, _wrapSmall, _wrapButton;
	private float _wrapScale;

	// 详情栏里的字和按钮：宽度固定、长了换行，不把详情栏撑宽
	private void EnsureWrapStyles()
	{
		if (_wrapLabel == null || _wrapScale != _scale)
		{
			_wrapScale = _scale;
			_wrapLabel = new GUIStyle(_label) { wordWrap = true, richText = true };
			_wrapSmall = new GUIStyle(_small) { wordWrap = true, richText = true };
			_wrapButton = new GUIStyle(_button) { wordWrap = true, richText = true, alignment = TextAnchor.MiddleLeft };
		}
	}

	private GUIStyle WrapLabel() { EnsureWrapStyles(); return _wrapLabel; }
	private GUIStyle WrapSmall() { EnsureWrapStyles(); return _wrapSmall; }

	// 一个任务：名字一行，时间和好感加减一行；点了在地图上选中它
	private void RomanceStopButton(StoryRomance.Stop s, float width)
	{
		EnsureWrapStyles();
		StoryMission m = s.Mission;
		string detail = m.When + RomanceParts(s);
		string text = (m.Visited ? "✓ " : "") + m.NumberedTitle + "\n<size=" + _small.fontSize + ">" + detail + "</size>";
		if (GUILayout.Button(text, _wrapButton, GUILayout.Width(width)))
		{
			_routePicked = m;
			_routeSide = null;
			_routeDetailScroll = Vector2.zero;
			if (_route != null && _route.Points.TryGetValue(m.Key, out Vector2 p))
			{
				_routeScroll = new Vector2(Mathf.Max(0, p.x - S(400)), Mathf.Max(0, p.y - S(250)));
			}
		}
	}

	private void DrawSideDetail(SideStory side)
	{
		if (_routeHeroine != null && GUILayout.Button(string.Format("‹ 返回{0}的结缘路线", StoryRomance.Name(_routeHeroine)), _button))
		{
			_routeSide = null;
			_routePicked = null;
			_routeDetailScroll = Vector2.zero;
			return;
		}
		GUILayout.Label("支线", _muted);
		GUILayout.Label(side.Title, _heading);
		foreach (StoryMission m in side.Joins)
		{
			if (GUILayout.Button("走完接到主线「" + m.NumberedTitle + "」", _button))
			{
				_routePicked = m;
				_routeSide = null;
			}
		}
		GUILayout.Label((side.Visited ? "<color=#2F6B3A>✓ 这一局走过</color>" : "这一局没走过") + (side.Read != null ? $"　·　对白读过 {side.Read}%" : ""), _label);
		var order = side.Missions.OrderBy(m => m.DisplayFrom ?? int.MaxValue).ToList();
		// 这一局还没走过的第一步标成「下一步」
		StoryMission upcoming = order.FirstOrDefault(m => !m.Visited);
		foreach (StoryMission m in order)
		{
			GUILayout.Space(S(8));
			GUILayout.BeginVertical(_card);
			GUILayout.Label(m.NumberedTitle + (m.Visited ? "　<color=#2F6B3A>✓ 走过</color>" : m == upcoming ? "　<color=#A3302A>下一步</color>" : ""), _label);
			GUILayout.Label(m.When + (m.Place.Length > 0 ? "　·　" + m.Place : "") + (m.Read != null ? $"　·　读过 {m.Read}%" : ""), _muted);
			string conditions = StoryAtlas.ConditionText(m);
			if (conditions.Length > 0)
			{
				GUILayout.Label(OnPaper(conditions), _small);
			}
			if (m.Entries.Count > 0)
			{
				GUILayout.Label(Cached((m, "entries"), () => string.Join("\n", m.Entries.Select(EntryText).ToArray())), _small);
			}
			if (GUILayout.Button("看剧情图", _button))
			{
				OpenFlow(m);
			}
			GUILayout.EndVertical();
		}
	}

	/// <summary>从别的任务接进来的一处：「从「某任务」：选… → 如果…」。</summary>
	private static string EntryText(StoryEntry e)
	{
		string from = (e.From.Main ? "主线" : "支线") + "「" + TextConverter.ForDisplay(e.From.NumberedTitle) + "」";
		return OnPaper(TextConverter.ForDisplay("从" + from + "接进来") + (e.Path.Count > 0 ? "：" + string.Join(" → ", e.Path.Select(step => StoryScripts.Describe(step, TextConverter.ForDisplay)).ToArray()) : ""));
	}

	private void DrawEntries(StoryMission m)
	{
		if (m.Entries.Count == 0)
		{
			return;
		}
		GUILayout.Space(S(10));
		GUILayout.Label("从别处接进来", _heading);
		GUILayout.Label(Cached((m, "entries"), () => string.Join("\n", m.Entries.Select(EntryText).ToArray())), _rich);
	}

	/// <summary>站内小节：这个任务的剧情段，每条路都会经过的是主干，其余是岔路（写出怎么进去）。</summary>
	private void DrawSections(StoryMission m)
	{
		FlowGraph graph = FlowOf(m);
		var boxes = graph.Boxes.Where(b => !b.Terminal).OrderBy(b => b.Layer).ThenBy(b => b.Row).ToList();
		if (boxes.Count == 0)
		{
			return;
		}
		HashSet<FlowBox> trunk = Trunk(graph);
		GUILayout.Space(S(10));
		GUILayout.Label("小节", _heading);
		foreach (FlowBox box in boxes.Take(40))
		{
			bool main = trunk.Contains(box);
			// 岔路写怎么走进来：连进来的那几行去处的短说明（完整条件在剧情图里悬停看）
			string entry = main ? "" : Cached((box, "entry"), () => string.Join("；", graph.Boxes.SelectMany(b => b.Ways)
				.Where(w => w.To == box && (w.Exits.Count == 0 || w.Exits.Any(e => e.Path.Count > 0 || e.AfterScene)))
				.Select(w => w.Short).Distinct().Take(4).ToArray()));
			string head = (main ? "<b>主干</b>　" : "<color=#73644F>岔路</color>　") + Short(box.Title, 18) + (box.Read != null ? $"　<color=#73644F>读过 {box.Read}%</color>" : "");
			if (GUILayout.Button(head + (entry.Length > 0 ? "\n<size=" + Mathf.RoundToInt(14 * _scale) + "><color=#73644F>" + entry + "</color></size>" : ""),
				new GUIStyle(_button) { alignment = TextAnchor.MiddleLeft, wordWrap = true, richText = true }))
			{
				OpenFlow(m, box.Last != null ? box.Last.Name : null);
			}
		}
		if (boxes.Count > 40)
		{
			GUILayout.Label($"……共 {boxes.Count} 段", _muted);
		}
	}

	// ---------------- 旗标搜索 ----------------

	private const int FlagHitsShown = 5;
	private List<(string Name, string Note)> _allFlags;
	private string _flagSearchedFor;
	private List<(string Name, string Note, List<StoryUse> Uses)> _flagHits = new List<(string, string, List<StoryUse>)>();

	/// <summary>搜索词对得上旗标编号或说明时，列出这面旗标在哪些剧情里改动、判定（和数值页点旗标名字看到的一样），每处能跳到剧情图。</summary>
	private void DrawFlagSearch(string query)
	{
		if (_flagSearchedFor != query)
		{
			_flagSearchedFor = query;
			_allFlags ??= MissionManagerData.Instance?.FlagsCollection?.Where(c => c != null && c.List != null).SelectMany(c => c.List).Where(f => f != null)
				.Select(f => f.name).Distinct().OrderBy(n => n, StringComparer.Ordinal)
				.Select(n => (n, TextConverter.ToSimplified(MissionManagerData.Instance.GetFlag(n)?.DevNote ?? "").Replace("\n", " ").Trim())).ToList();
			_flagHits = new List<(string, string, List<StoryUse>)>();
			foreach (var (name, note) in _allFlags ?? new List<(string, string)>())
			{
				if (note.Contains("暂存") || !Matches(query, name, note))
				{
					continue;
				}
				List<StoryUse> uses = StoryUsage.Flag(name);
				if (uses == null)
				{
					// 反查还没整理好：下一帧再搜
					_flagSearchedFor = null;
					return;
				}
				if (uses.Count > 0)
				{
					_flagHits.Add((name, note, uses));
				}
			}
		}
		if (_flagHits.Count == 0)
		{
			return;
		}
		GUILayout.Label($"旗标里找到 {_flagHits.Count} 面" + (_flagHits.Count > FlagHitsShown ? $"　（只列前 {FlagHitsShown} 面，搜得更具体些能缩小范围）" : ""), _heading);
		foreach (var (name, note, uses) in _flagHits.Take(FlagHitsShown))
		{
			GUILayout.Space(S(6));
			GUILayout.Label($"<b>{name}</b>" + (note.Length > 0 ? "　" + Short(note, 40) : ""), _label);
			foreach (StoryUse use in uses.OrderBy(u => !u.Changes).ThenBy(u => u.Mission == null).ThenBy(u => u.Mission?.DisplayFrom ?? int.MaxValue).Take(20))
			{
				DrawUse(use);
			}
			if (uses.Count > 20)
			{
				GUILayout.Label($"还有 {uses.Count - 20} 处，到数值页点这面旗标的名字看全部", _muted);
			}
		}
		GUILayout.Space(S(12));
	}

	// ---------------- 台词搜索 ----------------

	private string _searchedFor;
	private List<(string Script, string Snippet)> _searchHits = new List<(string, string)>();

	private void DrawSearchResults(string query)
	{
		DrawFlagSearch(query);
		if (!StoryAtlas.DialogueReady)
		{
			GUILayout.Label("正在整理台词……", _muted);
			return;
		}
		if (_searchedFor != query)
		{
			_searchedFor = query;
			_searchHits = StoryAtlas.SearchDialogue(query, 60);
		}
		GUILayout.Label(_searchHits.Count == 0 ? "台词里没找到" : $"台词里找到 {_searchHits.Count} 处" + (_searchHits.Count >= 60 ? "　（只列前 60 处）" : ""), _heading);
		foreach (var (script, snippet) in _searchHits)
		{
			StoryAtlas.Owner.TryGetValue(script, out StoryMission m);
			string where = m != null ? (m.Main ? "主线" : m.SeriesTitle) + "「" + Short(m.NumberedTitle, 14) + "」" : script;
			if (GUILayout.Button($"<size={Mathf.RoundToInt(14 * _scale)}><color=#73644F>{where}</color></size>\n{snippet}", new GUIStyle(_button) { alignment = TextAnchor.MiddleLeft, wordWrap = true, richText = true }) && m != null)
			{
				OpenFlow(m, script);
			}
		}
	}
}
