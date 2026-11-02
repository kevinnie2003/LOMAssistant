using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LOMAssistant.Features.Story;

// 剧情图：一个任务的剧情从开始剧本往下画成流程图。一路只有一条去处、又没有分岔的剧本合成一个方块；
// 选项、掷骰、条件各连一条线到下一段，线上写着选了什么；走出这个任务的（回地图、主线推进、结局）画成灰色小方块。
public partial class StoryReviewPanel
{
	private sealed class FlowBox
	{
		public string Id = "";
		public List<string> Chain = new List<string>();
		public StoryScript Last;
		public int Layer, Row;
		public Rect Rect;
		public string Title = "", Sub = "";
		public int? Read;
		public StoryNode Kept;
		/// <summary>走出本任务的去处（回地图、主线推进、结局、别的任务），没有剧本。</summary>
		public bool Terminal;
		public bool Ending;
		/// <summary>这一段最后一个剧本的每个去处，方块里一行一个：选项、掷骰、条件、接着；To 为 null 的是选了还留在本段、之后合回来的选项。</summary>
		public readonly List<FlowWay> Ways = new List<FlowWay>();
		/// <summary>方块里第一行去处的纵坐标（行高 RowHeight），排版时算。</summary>
		public float RowsTop;
		/// <summary>相邻连线的目的地色号；-1 保留原色，不参与剧情判定。</summary>
		public int LineTint = -1;
		public Color LineColor;
	}

	/// <summary>方块里的一行去处。</summary>
	private sealed class FlowWay
	{
		public FlowBox To;
		/// <summary>原始分岔路径及去处类型，用于合并同一路径，不受译文或截短影响。</summary>
		public string Key = "";
		/// <summary>行里显示的短说明；Full 是完整的条件和选项（悬停时看）。</summary>
		public string Short = "";
		public readonly List<string> Full = new List<string>();
		public string Effects = "";
		public readonly List<StoryExit> Exits = new List<StoryExit>();
		public bool Back;
		/// <summary>排版后的折线，只影响绘制，不参与剧情连接和判定。</summary>
		public Vector2[] Points;
	}

	private sealed class FlowLink
	{
		public FlowBox From, To;
		public List<string> Labels = new List<string>();
		public List<StoryExit> Exits = new List<StoryExit>();
		public bool Back;
		/// <summary>线上的标签（每项一行、完整显示）、它占的高度，以及离线多高（同一个方块进来几条线时往上叠）。</summary>
		public string Text = "";
		public float Height, Lift;
	}

	private sealed class FlowGraph
	{
		/// <summary>任务入口说明，只作显示，不计入剧情段或跳转路径。</summary>
		public StoryMission Entry;
		public FlowBox EntryBox;
		public readonly List<(StoryMission Mission, FlowBox To)> EntryWays = new List<(StoryMission, FlowBox)>();
		public readonly List<FlowBox> Boxes = new List<FlowBox>();
		public readonly List<FlowLink> Links = new List<FlowLink>();
		public readonly List<(FlowBox[] Sources, FlowBox To, bool Back, Vector2 A, Vector2 B)> Lines = new List<(FlowBox[], FlowBox, bool, Vector2, Vector2)>();
		public float Width, Height;
		public bool Cut;
	}

	private const int FlowLimit = 160;

	// 方块里去处那一行的文字：左对齐、超出截掉（悬停看完整的）
	private GUIStyle _flowRow, _flowRowMuted;
	private float _flowRowScale;
	private bool _flowRowDark;

	private StoryMission _flowMission;
	private FlowGraph _flow;
	private float _flowBuiltScale;
	private int _flowBuiltVersion;
	private int _flowBuiltAtlasVersion = -1;
	private FlowBox _flowPicked;
	private Vector2 _flowScroll, _flowListScroll, _flowDetailScroll;
	private string _flowSearch = "";

	/// <summary>打开某个任务的剧情图；pickScript 给了就选中含这个剧本的那一段并滚过去。</summary>
	private void OpenFlow(StoryMission m, string pickScript = null)
	{
		if (m == null) return;
		m = StoryAtlas.Find(m.Key) ?? m;
		_tab = 2;
		// 外部链接仍使用原任务，列表选中合并后的共同入口。
		_flowMission = FlowMissions(m).First();
		_flow = null;
		_flowPicked = null;
		_flowScroll = Vector2.zero;
		_flowPickScript = pickScript;
		_flowSearch = "";
		_flowListQuery = null;
	}

	private string _flowPickScript;
	/// <summary>此刻页点「在剧情图里看」：剧本读完后打开它所在任务的剧情图，选中这一段。</summary>
	private string _flowJumpScript;
	private string _flowListQuery;
	private HashSet<StoryMission> _flowDialogueHits = new HashSet<StoryMission>();

	/// <summary>任务的剧情图（路线图的小节也用它），按任务缓存。</summary>
	private readonly Dictionary<StoryMission, FlowGraph> _flowCache = new Dictionary<StoryMission, FlowGraph>();
	private int _flowCacheVersion = -1;

	private FlowGraph FlowOf(StoryMission m)
	{
		if (_flowCacheVersion != StoryAtlas.Version + StoryNodes.Version * 7919)
		{
			_flowCache.Clear();
			_flowCacheVersion = StoryAtlas.Version + StoryNodes.Version * 7919;
		}
		if (!_flowCache.TryGetValue(m, out FlowGraph graph))
		{
			_flowCache[m] = graph = BuildFlow(m);
		}
		return graph;
	}

	// 只在剧情图列表合并显示，路线图和游戏进度仍保留每个任务。
	private static StoryMission[] FlowMissions(StoryMission mission)
	{
		var members = mission.FlowGroup.Length == 0 ? new[] { mission }
			: StoryAtlas.Missions.Where(m => m.FlowGroup == mission.FlowGroup && m.StartScript.Length > 0).ToArray();
		return members.Length > 0 ? members : new[] { mission };
	}

	private static string FlowTitle(StoryMission mission)
	{
		var members = FlowMissions(mission);
		var first = members[0];
		// 同一开场兼有地点入口和自动入口时，标题取地点入口，触发方式留在各行。
		if (members.All(m => m.StartScript == first.StartScript)) first = members.FirstOrDefault(m => m.Position != Mortal.Core.PositionType.無) ?? first;
		return members.Length > 1 && !first.Main && first.SeriesTitle != first.Series ? first.SeriesTitle : first.NumberedTitle;
	}

	private static string FlowWhen(StoryMission mission)
	{
		var members = FlowMissions(mission);
		return members.Select(m => m.When).Distinct().Count() == 1 ? mission.When
			: StoryAtlas.TimeRange(members.Any(m => m.DisplayFrom == null) ? (int?)null : members.Min(m => m.DisplayFrom),
				members.Any(m => m.DisplayTo == null) ? (int?)null : members.Max(m => m.DisplayTo));
	}

	private static int? FlowDate(StoryMission mission) => FlowMissions(mission).Min(m => m.DisplayFrom ?? m.DisplayTo);

	/// <summary>主干：从开头到这个任务的每个出口都必经的段落；其余是岔路。</summary>
	private static HashSet<FlowBox> Trunk(FlowGraph graph)
	{
		var boxes = graph.Boxes.Where(b => !b.Terminal).ToList();
		var trunk = new HashSet<FlowBox>();
		if (boxes.Count == 0)
		{
			return trunk;
		}
		FlowBox start = boxes[0];
		var preds = boxes.ToDictionary(b => b, b => graph.Links.Where(l => !l.Back && l.To == b && !l.From.Terminal).Select(l => l.From).ToList());
		var dom = boxes.ToDictionary(b => b, b => b == start ? new HashSet<FlowBox> { start } : new HashSet<FlowBox>(boxes));
		for (bool changed = true; changed;)
		{
			changed = false;
			foreach (FlowBox b in boxes)
			{
				if (b == start || preds[b].Count == 0)
				{
					continue;
				}
				var next = new HashSet<FlowBox>(dom[preds[b][0]]);
				foreach (FlowBox p in preds[b].Skip(1))
				{
					next.IntersectWith(dom[p]);
				}
				next.Add(b);
				if (!next.SetEquals(dom[b]))
				{
					dom[b] = next;
					changed = true;
				}
			}
		}
		// 出口：连到「走出本任务」小方块的，或者没有往下走的段落
		var exits = boxes.Where(b => graph.Links.Any(l => l.From == b && l.To.Terminal) || !graph.Links.Any(l => l.From == b && !l.Back)).ToList();
		if (exits.Count == 0)
		{
			exits = boxes;
		}
		trunk.UnionWith(dom[exits[0]]);
		foreach (FlowBox e in exits.Skip(1))
		{
			trunk.IntersectWith(dom[e]);
		}
		return trunk;
	}

	private void DrawFlow(Rect r)
	{
		if (!EnsureAtlas(r))
		{
			return;
		}
		if (_flowBuiltAtlasVersion != StoryAtlas.Version)
		{
			_flowListQuery = null;
			if (_flowMission != null) _flowMission = StoryAtlas.Find(_flowMission.Key);
		}
		if (_flowJumpScript != null)
		{
			string script = _flowJumpScript;
			_flowJumpScript = null;
			if (StoryAtlas.Owner.TryGetValue(script, out StoryMission owner))
			{
				OpenFlow(owner, script);
			}
		}
		// 左边：任务列表
		float listWidth = Mathf.Min(S(330), r.width * 0.24f);
		string query = SearchBox(new Rect(r.x, r.y, listWidth, S(36)), ref _flowSearch);
		var listArea = new Rect(r.x, r.y + S(44), listWidth, r.height - S(44));
		DragScroll("flowList", listArea, ref _flowListScroll);
		GUILayout.BeginArea(listArea);
		_flowListScroll = GUILayout.BeginScrollView(_flowListScroll);
		// 搜索也看台词：台词里出现这几个字的任务一起列出来
		if (query != _flowListQuery)
		{
			_flowListQuery = query;
			_flowDialogueHits = query.Length >= 2
				? new HashSet<StoryMission>(StoryAtlas.SearchDialogue(query, 500).Select(h => StoryAtlas.Owner.TryGetValue(h.Script, out StoryMission o) ? o : null).Where(o => o != null))
				: new HashSet<StoryMission>();
		}
		// 按剧情时间从早到晚，主线和支线排在一起；同一旬里主线在前。按年份分段，「任意时间」的放最后
		string group = null;
		foreach (var members in StoryAtlas.Missions.Where(m => m.StartScript.Length > 0)
			.GroupBy(m => m.FlowGroup.Length > 0 ? "group:" + m.FlowGroup : "mission:" + m.Key)
			.Where(g => g.Any(m => Matches(query, m.Title, m.Key, m.SeriesTitle, m.Place, m.Summary) || _flowDialogueHits.Contains(m)))
			.OrderBy(g => FlowDate(g.First()) ?? int.MaxValue).ThenBy(g => g.First().Main ? 0 : 1).ThenBy(g => g.First().Key, StringComparer.Ordinal))
		{
			StoryMission m = members.First();
			bool selected = members.Contains(_flowMission);
			int? date = FlowDate(m);
			string g = date != null ? $"第{date.Value / 36 + 1}年" : "任意时间 / 每月";
			if (g != group)
			{
				group = g;
				GUILayout.Label(g, _muted);
			}
			string when = date != null ? $"{date.Value % 36 / 3 + 1}月{StoryAtlas.StageText(date.Value % 3 + 1)}" : FlowWhen(m);
			string kind = m.Main ? "主线" : Short(m.SeriesTitle, 10);
			var style = new GUIStyle(selected ? _primary : _button) { alignment = TextAnchor.MiddleLeft, wordWrap = true, richText = true };
			string whenColor = selected ? "#F3E6D6" : "#73644F";
			if (GUILayout.Button($"<size={Mathf.RoundToInt(13 * _scale)}><color={whenColor}>{when} · {kind}</color></size>\n{Short(FlowTitle(m), 16)}", style))
			{
				OpenFlow(m);
			}
		}
		GUILayout.EndScrollView();
		GUILayout.EndArea();

		var area = new Rect(r.x + listWidth + S(12), r.y, r.width - listWidth - S(12), r.height);
		if (_flowMission == null)
		{
			return;
		}
		if (_flow == null || !Mathf.Approximately(_flowBuiltScale, _scale) || _flowBuiltVersion != StoryNodes.Version || _flowBuiltAtlasVersion != StoryAtlas.Version)
		{
			_flow = BuildFlow(_flowMission, FlowMissions(_flowMission));
			_flowBuiltScale = _scale;
			_flowBuiltVersion = StoryNodes.Version;
			_flowBuiltAtlasVersion = StoryAtlas.Version;
			_flowPicked = _flowPicked == null ? null : _flow.Boxes.FirstOrDefault(b => b.Id == _flowPicked.Id);
		}
		if (_flowPickScript != null)
		{
			FlowBox target = _flow.Boxes.FirstOrDefault(b => b.Chain.Any(c => string.Equals(c, _flowPickScript, StringComparison.OrdinalIgnoreCase)));
			if (target != null)
			{
				_flowPicked = target;
				_flowScroll = new Vector2(Mathf.Max(0, target.Rect.x - S(80)), Mathf.Max(0, target.Rect.y - S(120)));
			}
			_flowPickScript = null;
		}
		string place = FlowMissions(_flowMission).All(m => m.Position == _flowMission.Position) ? _flowMission.Place : "";
		GUI.Label(new Rect(area.x, area.y, area.width, S(36)), $"<b>{FlowTitle(_flowMission)}</b>　<color=#73644F>{FlowWhen(_flowMission)}{(place.Length > 0 ? " · " + place : "")}{(_flow.Cut ? " · 太长，只画了前 " + FlowLimit + " 段" : "")}</color>", _label);
		float detailWidth = _flowPicked != null ? Mathf.Min(S(460), area.width * 0.36f) : 0;
		var canvas = new Rect(area.x, area.y + S(44), area.width - detailWidth - (detailWidth > 0 ? S(12) : 0), area.height - S(44));
		Fill(canvas, PaperLight);
		Frame(canvas, Edge, 1);
		DrawFlowCanvas(canvas);
		if (_flowPicked != null)
		{
			var detail = new Rect(canvas.xMax + S(12), canvas.y, detailWidth, canvas.height);
			Fill(detail, PaperDeep);
			Frame(detail, Edge, 1);
			DragScroll("flowDetail", detail, ref _flowDetailScroll);
			GUILayout.BeginArea(new Rect(detail.x + S(12), detail.y + S(10), detail.width - S(24), detail.height - S(20)));
			_flowDetailScroll = GUILayout.BeginScrollView(_flowDetailScroll);
			DrawBoxDetail(_flowPicked);
			GUILayout.EndScrollView();
			GUILayout.EndArea();
		}
	}

	/// <summary>从 name 开始，一路只有一条去处、没有分岔的剧本合在一起。</summary>
	private static List<string> Chain(string name, out StoryScript last, HashSet<string> boundaries = null)
	{
		var chain = new List<string>();
		StoryScript script = StoryScripts.Get(name);
		while (script != null && !script.Branches && script.Exits.Count == 1 && script.Exits[0].Path.Count == 0 && StoryScripts.FlowTarget(script.Exits[0]).Length > 0
			&& !script.Exits[0].ToMap && !script.Exits[0].ToTitle && script.Exits[0].MainMission.Length == 0 && !chain.Contains(script.Name) && chain.Count < 30
			&& !StoryPlaces.IsReturn(StoryScripts.FlowTarget(script.Exits[0])) && script.Exits[0].EventPool.Length == 0
			&& !(boundaries?.Contains(StoryScripts.FlowTarget(script.Exits[0])) ?? false))
		{
			chain.Add(script.Name);
			script = StoryScripts.Get(StoryScripts.FlowTarget(script.Exits[0]));
		}
		if (script != null && !chain.Contains(script.Name))
		{
			chain.Add(script.Name);
		}
		last = script;
		return chain;
	}

	/// <summary>线上的短标签：这条路最后一步分岔。</summary>
	/// <summary>线上的标签：这条路上的每一步分岔，用「→」连起来。</summary>
	private static string LinkLabel(StoryExit exit) => exit.Path.Count == 0 ? Lead(exit) : string.Join(" → ", exit.Path.Select(StepLabel).ToArray());

	private static string Lead(StoryExit exit) => StoryScripts.Travels(exit) ? "路上随机事件后" : exit.AfterScene ? "战斗后" : "";

	/// <summary>线上的短标签：选项写「选项文字」，掷骰写「掷出 &lt;33」，条件写「如果 小梅好感>=35」。</summary>
	private static string StepLabel(StoryStep step)
	{
		switch (step.Kind)
		{
			case StoryStep.Option:
				return "「" + StoryScripts.OptionText(step.Key) + "」";
			case StoryStep.Dice:
				return "掷出 " + (step.Range.Length > 0 ? step.Range : Short(StoryScripts.OptionText(step.Key), 6));
			default:
				if (step.Arm == 0)
				{
					return StoryScripts.Otherwise(step.Key, false);
				}
				// 「小梅好感 需>=35 → 当前40」只留「小梅好感>=35」
				string text = RichText.Strip(DataHelper.SwitchBranchCondition(step.Key, step.Arm)).Trim();
				if (text == "以上条件都不满足")
				{
					return StoryScripts.Otherwise(step.Key, false);
				}
				text = System.Text.RegularExpressions.Regex.Replace(text, @" → 当前[^ ]*", "").Replace(" 需", "").Trim();
				return text.Length > 0 ? "如果 " + text : "条件不明";
		}
	}

	/// <summary>这个去处触发的是哪个结局（汗青书），找不到返回空。</summary>
	private static string EndingId(StoryScript script, StoryExit exit)
	{
		foreach (var (path, death, id) in script.Endings)
		{
			if (!death && (StoryScripts.IsPrefix(path, exit.Path) || StoryScripts.IsPrefix(exit.Path, path)))
			{
				return id;
			}
		}
		return "";
	}

	private FlowGraph BuildFlow(StoryMission mission, StoryMission[] group = null)
	{
		var graph = new FlowGraph { Entry = mission };
		var members = group ?? new[] { mission };
		var otherStarts = new HashSet<string>(StoryAtlas.Missions.Where(m => !members.Contains(m) && m.StartScript.Length > 0).Select(m => m.StartScript), StringComparer.OrdinalIgnoreCase);
		// 地点入口沿用已有任务归属，不能把别的任务的中段重新展开。
		if (mission.PlaceEntry) otherStarts.UnionWith(StoryAtlas.Owner.Where(p => !members.Contains(p.Value)).Select(p => p.Key));
		var byScript = new Dictionary<string, FlowBox>(StringComparer.OrdinalIgnoreCase);
		var terminals = new Dictionary<string, FlowBox>(StringComparer.Ordinal);
		var queue = new Queue<FlowBox>();

		FlowBox Box(string head, int layer)
		{
			var boundaries = new HashSet<string>(otherStarts, StringComparer.OrdinalIgnoreCase);
			if (members.Length > 1)
			{
				if (byScript.TryGetValue(head, out FlowBox existing)) return existing;
				// 合并入口时保留每条支路，不能把汇合前的台词吞进已有方块。
				boundaries.UnionWith(members.Select(m => m.StartScript));
				boundaries.UnionWith(byScript.Keys);
			}
			List<string> chain = Chain(head, out StoryScript last, boundaries);
			if (mission.PlaceEntry) last = StoryPlaces.View(last);
			string key = last != null ? last.Name : head;
			if (byScript.TryGetValue(key, out FlowBox known))
			{
				return known;
			}
			if (graph.Boxes.Count >= FlowLimit)
			{
				graph.Cut = true;
				return null;
			}
			var scripts = chain.Select(n => mission.PlaceEntry ? StoryPlaces.View(StoryScripts.Get(n)) : StoryScripts.Get(n)).Where(s => s != null).ToList();
			var tags = new List<string>();
			if (last != null && last.Menu) tags.Add("选项");
			if (last != null && last.Dice) tags.Add("掷骰");
			if (last != null && last.Fight) tags.Add("战斗");
			var box = new FlowBox
			{
				Id = key,
				Chain = chain,
				Last = last,
				Layer = layer,
				Read = StoryScripts.ReadPercent(scripts.SelectMany(s => s.Lines)),
				Kept = StoryNodes.Latest(key),
				Title = scripts.SelectMany(s => s.Records).Select(StoryScripts.Legend).FirstOrDefault(t => t.Length > 0) ?? ""
			};
			// 没有剧情记录的段落用第一句台词，再没有才用剧本名
			if (box.Title.Length == 0)
			{
				box.Title = scripts.SelectMany(s => s.Lines).Take(6).Select(StoryAtlas.LineText).FirstOrDefault(t => t.Length > 0) ?? "";
			}
			if (box.Title.Length == 0)
			{
				box.Title = chain.Count > 1 ? chain[0] + " … " + key : key;
			}
			box.Sub = string.Join("・", tags.ToArray()) + (box.Read != null ? (tags.Count > 0 ? "　" : "") + "读过 " + box.Read + "%" : "");
			foreach (string name in chain)
			{
				byScript[name] = box;
			}
			graph.Boxes.Add(box);
			queue.Enqueue(box);
			return box;
		}

		FlowBox Terminal(FlowBox from, string sourceKey, string text, bool ending)
		{
			// 显示标题可能随语言变化或截短成相同文字，不能用来判断是否为同一去处。
			string key = from.Id + "|" + sourceKey;
			if (!terminals.TryGetValue(key, out FlowBox box))
			{
				box = new FlowBox { Id = key, Title = text, Terminal = true, Ending = ending, Layer = from.Layer + 1 };
				terminals[key] = box;
				graph.Boxes.Add(box);
			}
			return box;
		}

		foreach (StoryMission member in members)
		{
			if (members.Length > 1 && member.FlowEntries.Any(e => members.Contains(e.From))) continue;
			FlowBox first = Box(member.StartScript, 0);
			if (first != null) graph.EntryWays.Add((member, first));
		}
		// 先沿主线推进建立后续方块，避免把同图后续段误排为第一列和回头线。
		if (members.Length > 1)
		{
			Drain();
			foreach (StoryMission member in members.Where(m => graph.EntryWays.All(e => e.Mission != m)))
			{
				FlowBox first = Box(member.StartScript, 0);
				if (first != null) graph.EntryWays.Add((member, first));
			}
		}
		// 只从入口沿实际去处展开，随机事件选择之后的剧本由独立入口负责。
		Drain();
		return Layout();

		void Drain()
		{
			while (queue.Count > 0)
			{
				FlowBox from = queue.Dequeue();
				if (from.Last == null)
				{
					continue;
				}
				foreach (StoryExit exit in from.Last.Exits)
				{
					FlowBox to;
					string target = StoryScripts.FlowTarget(exit);
					if (exit.ToTitle)
					{
						string id = EndingId(from.Last, exit);
						string name = StoryAtlas.Endings.FirstOrDefault(e => !e.Death && e.Id == id)?.Title ?? id;
						to = Terminal(from, "ending:" + id, name.Length > 0 ? "结局：" + Short(name, 9) : "结局", true);
					}
					else if (exit.ToMap)
					{
						to = Terminal(from, "map", "回到地图", false);
					}
					else if (exit.EventPool.Length > 0 && !StoryScripts.Travels(exit))
					{
						to = Terminal(from, "pool:" + exit.EventPool, "随机事件", false);
					}
					else if (StoryPlaces.IsReturn(target))
					{
						to = Terminal(from, "return:" + target.ToLowerInvariant(), StoryPlaces.ReturnText(target, TextConverter.ForDisplay), false);
					}
					// 推进主线只是修改进度，有实际去处时仍沿原剧本走。
					else if (target.Length == 0 && exit.MainMission.Length > 0)
					{
						StoryMission next = StoryAtlas.Find(exit.MainMission);
						to = members.Length > 1 && next != null && members.Contains(next) ? Box(next.StartScript, from.Layer + 1)
							: Terminal(from, "main:" + exit.MainMission, "主线 → " + (next != null ? Short(next.Title, 10) : exit.MainMission), false);
						if (to == null) continue;
					}
					else if (target.Length == 0)
					{
						continue;
					}
					else if (otherStarts.Contains(target))
					{
						StoryMission other = StoryAtlas.Missions.FirstOrDefault(m => string.Equals(m.StartScript, target, StringComparison.OrdinalIgnoreCase));
						if (other == null) StoryAtlas.Owner.TryGetValue(target, out other);
						to = Terminal(from, "script:" + target, "接到 " + (other != null ? Short(other.Title, 10) : target), false);
					}
					else
					{
						to = Box(target, from.Layer + 1);
						if (to == null)
						{
							continue;
						}
					}
					FlowLink link = graph.Links.Find(l => l.From == from && l.To == to);
					if (link == null)
					{
						link = new FlowLink { From = from, To = to, Back = to.Layer <= from.Layer };
						graph.Links.Add(link);
					}
					link.Exits.Add(exit);
					string label = LinkLabel(exit);
					if (label.Length > 0 && !link.Labels.Contains(label))
					{
						link.Labels.Add(label);
					}
					AddWay(from, to, FlowPathKey(exit.Path) + (exit.AfterScene ? "/after-scene" : "/exit"), ShortLabel(exit), label, exit, link.Back);
				}
				// 选了还留在本段、之后合回来的选项：也列成一行，写上它改什么
				var leaving = new HashSet<string>(from.Last.Exits.SelectMany(x => x.Path).Where(st => st.Kind == StoryStep.Option).Select(st => st.Key));
				foreach (StoryStep option in from.Last.Menus.SelectMany(m => m).Where(st => st.Kind == StoryStep.Option && !leaving.Contains(st.Key)))
				{
					if (from.Ways.Any(w => w.To == null && w.Exits.Count == 0 && w.Full.Contains(option.Key)))
					{
						continue;
					}
					var stay = new FlowWay { Short = "「" + Short(StoryScripts.OptionText(option.Key), 12) + "」" };
					stay.Full.Add(option.Key);
					stay.Effects = string.Join("　", from.Last.Effects.Where(x => x.Path.Any(st => st.Key == option.Key) && !x.Text.Contains("暂存")).Select(x => x.Text).Distinct().ToArray());
					from.Ways.Add(stay);
				}
				// 输了会死的决斗
				foreach (var (path, level) in from.Last.Fights)
				{
					if (!ConditionBook.CombatDeaths.TryGetValue(level, out string deadId))
					{
						continue;
					}
					StoryEnding ending = StoryAtlas.Endings.FirstOrDefault(e => e.Death && e.Id == deadId);
					FlowBox to = Terminal(from, "death:" + deadId, "死亡：" + Short(ending != null ? ending.Title : deadId, 9), true);
					FlowLink link = graph.Links.Find(l => l.From == from && l.To == to);
					if (link == null)
					{
						link = new FlowLink { From = from, To = to };
						graph.Links.Add(link);
					}
					string label = (path.Count > 0 ? StepLabel(path[path.Count - 1]) + "，" : "") + "决斗输了";
					if (!link.Labels.Contains(label))
					{
						link.Labels.Add(label);
					}
					AddWay(from, to, FlowPathKey(path) + "/duel-loss", (path.Count > 0 ? StepShort(path[path.Count - 1]) + " · " : "") + "决斗输了", label, null, false);
				}
				// 死亡（生死簿）不是「回到标题」，单独画出来
				foreach (var (path, death, id) in from.Last.Endings.Where(e => e.Death))
				{
					StoryEnding ending = StoryAtlas.Endings.FirstOrDefault(e => e.Death && e.Id == id);
					FlowBox to = Terminal(from, "death:" + id, "死亡：" + Short(ending != null ? ending.Title : id, 9), true);
					FlowLink link = graph.Links.Find(l => l.From == from && l.To == to);
					if (link == null)
					{
						link = new FlowLink { From = from, To = to };
						graph.Links.Add(link);
					}
					string label = path.Count > 0 ? StepLabel(path[path.Count - 1]) : "";
					if (label.Length > 0 && !link.Labels.Contains(label))
					{
						link.Labels.Add(label);
					}
					AddWay(from, to, FlowPathKey(path) + "/death", path.Count > 0 ? StepShort(path[path.Count - 1]) : "接着", label, null, false);
				}
			}
		}

		// 同一个去处、同一条原始分岔路径合成一行，译文只负责显示。
		void AddWay(FlowBox from, FlowBox to, string key, string shortText, string full, StoryExit exit, bool back)
		{
			FlowWay way = from.Ways.Find(w => w.To == to && w.Key == key);
			if (way == null)
			{
				way = new FlowWay { To = to, Key = key, Short = shortText, Back = back };
				from.Ways.Add(way);
			}
			if (full.Length > 0 && !way.Full.Contains(full))
			{
				way.Full.Add(full);
			}
			if (exit != null)
			{
				way.Exits.Add(exit);
			}
		}

		FlowGraph Layout()
		{
			foreach (FlowLink link in graph.Links)
			{
				link.Text = string.Join("\n", link.Labels.ToArray());
			}
			SetFlowLayers(graph);
			float w = S(290), gapX = S(120), gapY = S(22);
			float entryWidth = graph.Entry != null ? w + gapX : 0;
			// 一列一列往右排：每个方块对着连进来的那几行去处的平均高度放，同一列里按这个高度排先后、互不重叠
			var incoming = new Dictionary<FlowBox, List<float>>();
			float bottom = 0;
			foreach (var layer in graph.Boxes.GroupBy(b => b.Layer).OrderBy(g => g.Key))
			{
				float Wanted(FlowBox b) => incoming.TryGetValue(b, out var ys) && ys.Count > 0 ? ys.Average() : 0f;
				var sorted = layer.OrderBy(Wanted).ThenBy(b => b.Terminal ? 1 : 0).ToList();
				float y = S(24);
				int row = 0;
				foreach (FlowBox b in sorted)
				{
					b.Row = row++;
					float bh = BoxHeight(b);
					float wanted = layer.Key == 0 ? y : Wanted(b) - InPort(b, bh);
					y = Mathf.Max(y, wanted);
					b.Rect = new Rect(S(24) + entryWidth + layer.Key * (w + gapX), y, b.Terminal ? w * 0.72f : w, bh);
					b.RowsTop = b.Rect.y + HeaderHeight;
					y += bh + gapY;
					for (int k = 0; k < b.Ways.Count; k++)
					{
						FlowWay way = b.Ways[k];
						if (way.To != null && !way.Back)
						{
							if (!incoming.TryGetValue(way.To, out var ys))
							{
								incoming[way.To] = ys = new List<float>();
							}
							ys.Add(b.RowsTop + (k + 0.5f) * RowHeight);
						}
					}
				}
				bottom = Mathf.Max(bottom, y);
			}
			graph.Width = S(48) + entryWidth + (graph.Boxes.Count == 0 ? 0 : graph.Boxes.Max(b => b.Layer) + 1) * (w + gapX);
			graph.Height = bottom + S(40);
			LayoutFlowLines(graph);
			return graph;
		}
	}

	// 只有接回当前路径上的祖先才是回边；首次发现时同列的合流不能误画成虚线。
	private static void SetFlowLayers(FlowGraph graph)
	{
		var outgoing = graph.Links.ToLookup(l => l.From);
		var visited = new HashSet<FlowBox>();
		var active = new HashSet<FlowBox>();
		void Visit(FlowBox box)
		{
			if (!visited.Add(box)) return;
			active.Add(box);
			foreach (FlowLink link in outgoing[box])
			{
				link.Back = active.Contains(link.To);
				if (!link.Back) Visit(link.To);
			}
			active.Remove(box);
		}
		foreach (FlowBox box in graph.Boxes) Visit(box);
		var backwards = new HashSet<(FlowBox From, FlowBox To)>(graph.Links.Where(l => l.Back).Select(l => (l.From, l.To)));
		foreach (FlowBox box in graph.Boxes)
		{
			box.Layer = 0;
			foreach (FlowWay way in box.Ways) way.Back = backwards.Contains((box, way.To));
		}
		// 分列：每一段放在「最长的一条路」走到它的那一列（往回接的线不算），岔开以后再合流的段落排在合流处，不会挤在前面
		for (int pass = 0; pass < graph.Boxes.Count; pass++)
		{
			bool moved = false;
			foreach (FlowLink link in graph.Links.Where(l => !l.Back))
			{
				if (link.To.Layer < link.From.Layer + 1)
				{
					link.To.Layer = link.From.Layer + 1;
					moved = true;
				}
			}
			if (!moved)
			{
				break;
			}
		}
	}

	private float HeaderHeight => S(60);
	private float RowHeight => S(28);

	private float BoxHeight(FlowBox b) => b.Terminal ? S(40) : HeaderHeight + Mathf.Max(1, b.Ways.Count) * RowHeight + S(8);

	// 连进来的线接在方块的哪个高度：剧情段接在标题那一行，走出本任务的小方块接在正中
	private float InPort(FlowBox b, float height) => b.Terminal ? height / 2f : S(22);

	/// <summary>行里的短说明：这条路上每一步分岔，选项写选项文字，掷骰写范围，条件只写个大概（完整的悬停看）。</summary>
	private static string ShortLabel(StoryExit exit) => exit.Path.Count == 0 ? (Lead(exit).Length > 0 ? Lead(exit) : "接着") : string.Join(" · ", exit.Path.Select(StepShort).ToArray());

	// 与 StoryScripts.IsPrefix 使用相同的分岔身份；转义分隔符，避免不同 key 拼成相同路径。
	private static string FlowPathKey(IEnumerable<StoryStep> path) => string.Join("|", path.Select(step =>
		Uri.EscapeDataString(step.Kind) + ":" + Uri.EscapeDataString(step.Key) + ":" + step.Arm).ToArray());

	private static string StepShort(StoryStep step)
	{
		switch (step.Kind)
		{
			case StoryStep.Option:
				return "「" + Short(StoryScripts.OptionText(step.Key), 12) + "」";
			case StoryStep.Dice:
				return "掷出 " + (step.Range.Length > 0 ? step.Range : Short(StoryScripts.OptionText(step.Key), 6));
			default:
				string full = StepLabel(step);
				return full.StartsWith("如果 ") ? "若 " + Short(full.Substring(3), 14) : Short(full, 12);
		}
	}


	// 入口只显示条件模板，不读取随机值，也不执行条件检查。
	private static string FlowEntryText(StoryMission mission, StoryEntry entry = null, bool missionEntry = false)
	{
		string origin = entry == null ? "" : (string.Format(TextConverter.ForDisplay("从「{0}」剧情接入"), TextConverter.ForDisplay(entry.From.Title)) + "\n" + FlowPathText(entry.Path)).TrimEnd();
		if (entry != null && !missionEntry) return origin;
		if (mission.Inactive) return TextConverter.ForDisplay("由其他剧情接入");
		string place = mission.PlaceEntry ? mission.Place : StoryPlaces.EntryPlace(mission.StartScript);
		if (place.Length == 0) place = mission.Place;
		bool automatic = entry == null && place.Length == 0 && mission.Position == Mortal.Core.PositionType.無;
		var parts = new List<string> { entry != null ? origin : automatic
			? TextConverter.ForDisplay("自动触发")
			: string.Format(TextConverter.ForDisplay(StoryPlaces.IsActivity(mission.StartScript) ? "选「{0}」" : "进入「{0}」"), TextConverter.ForDisplay(place)) };
		// 合并后各入口日期可能不同；只在这时逐行保留日期，不能只看图上方的总范围。
		if (FlowMissions(mission).Select(m => m.When).Distinct().Count() > 1) parts.Add(TextConverter.ForDisplay(mission.When));
		var conditions = mission.Conditions.Where(c => c != null).Select(DataHelper.ConditionTemplate).Where(t => t.Length > 0).Select(TextConverter.ForDisplay);
		// MissionCheckData.Check 要求所有条目都成立；条目内部的 AND/OR 由模板保留。
		parts.Add(string.Join("\n" + TextConverter.ForDisplay(" 且 ").TrimStart(), conditions.ToArray()));
		if (automatic && parts.Skip(1).Any(t => t.Length > 0)) parts[0] += ":";
		return string.Join("\n", parts.Where(t => t.Length > 0).ToArray());
	}

	// 和剧情出口用同一条原始路径；入口说明只读模板，不能为画图执行随机判定。
	private static string FlowPathText(List<StoryStep> path)
	{
		var parts = new List<string>();
		foreach (StoryStep step in path)
		{
			if (step.Kind == StoryStep.Option)
			{
				parts.Add(string.Format(TextConverter.ForDisplay("选「{0}」"), TextConverter.ForDisplay(StoryScripts.OptionText(step.Key))));
				if (step.Requirement.Length > 0)
				{
					var needs = DataHelper.CheckpointTemplates(step.Requirement, false);
					parts.Add(needs.Count > 0 ? TextConverter.ForDisplay(string.Join(" 或 ", needs.ToArray())) : TextConverter.ForDisplay("条件不明") + " (" + step.Requirement + ")");
				}
			}
			else if (step.Kind == StoryStep.Dice)
				parts.Add(string.Format(TextConverter.ForDisplay("掷骰「{0}」（{1}）"), TextConverter.ForDisplay(StoryScripts.OptionText(step.Key)), step.Range));
			else
			{
				var arms = DataHelper.CheckpointTemplates(step.Key, true);
				if (arms.Count == 0) parts.Add(TextConverter.ForDisplay("条件不明") + " (" + step.Key + ")");
				else
				{
					foreach (string earlier in arms.Take(step.Arm > 0 ? step.Arm - 1 : arms.Count))
						parts.Add(string.Format(TextConverter.ForDisplay("不满足：{0}"), "(" + TextConverter.ForDisplay(earlier) + ")"));
					if (step.Arm > 0 && step.Arm <= arms.Count) parts.Add(TextConverter.ForDisplay(arms[step.Arm - 1]));
				}
			}
		}
		return string.Join("\n", parts.ToArray());
	}

	// 入口只借用剧情方块的绘制和连接，不加入剧情图的数据及跳转路径。
	private FlowBox FlowEntryBox(FlowGraph graph)
	{
		if (graph.Entry == null || graph.EntryWays.Count == 0) return null;
		var box = new FlowBox { Title = "触发条件", Layer = -1 };
		foreach (var entry in graph.EntryWays)
		{
			// 不同入口各占一行，即使去处相同也保留各自条件；不在行间拼接“或”。
			var sources = new List<(StoryEntry Entry, bool Mission)>();
			if (entry.Mission.FlowEntries.Count == 0 && (!entry.Mission.Inactive || entry.Mission.Entries.Count == 0)) sources.Add((null, true));
			sources.AddRange(entry.Mission.FlowEntries.Select(e => (e, true)));
			sources.AddRange(entry.Mission.Entries.Select(e => (e, false)));
			foreach (var source in sources)
			{
				string text = Cached((entry.Mission, source, TextConverter.DisplayLanguage, "flow-entry"), () => FlowEntryText(entry.Mission, source.Entry, source.Mission));
				var way = new FlowWay { To = entry.To, Short = text.Replace("\r", "").Replace("\n", " ") };
				way.Full.Add(text);
				box.Ways.Add(way);
			}
		}
		box.Rect = new Rect(S(24), S(24), S(290), BoxHeight(box));
		box.RowsTop = box.Rect.y + HeaderHeight;
		return box;
	}

	// 同一起点的分支就近合流，不同起点到同一去处的线在终点附近汇合。
	private void LayoutFlowLines(FlowGraph graph)
	{
		graph.EntryBox = FlowEntryBox(graph);
		var boxes = (graph.EntryBox == null ? graph.Boxes : new[] { graph.EntryBox }.Concat(graph.Boxes)).ToArray();
		var exits = boxes.SelectMany(b => b.Ways.Select((w, i) => (From: b, Way: w, Y: b.RowsTop + (i + 0.5f) * RowHeight))).Where(e => e.Way.To != null).ToArray();
		var byColumn = exits.ToLookup(e => e.From.Layer);
		var shared = new HashSet<(FlowBox To, bool Back)>(exits.GroupBy(e => (e.Way.To, e.Way.Back))
			.Where(g => g.Select(e => e.From).Distinct().Count() > 1).Select(g => g.Key));
		var ports = new Dictionary<(FlowBox To, bool Back), float>();
		// 入点避开同一通道中通往别处的出线高度，防止横线盖在一起。
		foreach (var target in exits.Select(e => (e.Way.To, e.Way.Back)).Distinct())
		{
			float normal = target.To.Rect.y + InPort(target.To, target.To.Rect.height) + (target.Back ? S(10) : 0);
			var occupied = byColumn[target.To.Layer - 1].Where(e => e.Way.To != target.To || e.Way.Back != target.Back).Select(e => e.Y).ToList();
			if (ports.TryGetValue((target.To, !target.Back), out float other)) occupied.Add(other);
			ports[target] = Enumerable.Range(5, target.To.Terminal ? 11 : 21).Select(i => target.To.Rect.y + S(i * 2))
				.OrderBy(y => occupied.Count(v => Mathf.Abs(y - v) < S(8))).ThenBy(y => Mathf.Abs(y - normal)).First();
		}
		// 跨列和回接只绕过所经过的方块，优先使用附近空隙，不统一绕到整张图底部。
		var routes = new List<(FlowBox From, FlowWay Way, int Row, bool Detour, bool Bundle, float Rail)>();
		var rails = new List<(FlowBox From, FlowBox To, bool Back, int Left, int Right, float Y)>();
		foreach (FlowBox box in boxes)
			foreach (var group in box.Ways.Select((way, row) => (Way: way, Row: row)).Where(w => w.Way.To != null)
				.GroupBy(w => (w.Way.To, w.Way.Back)))
			{
				FlowBox target = group.Key.To;
				bool detour = target.Layer != box.Layer + 1, bundle = shared.Contains(group.Key);
				float rail = 0;
				if (detour || bundle)
				{
					int left = Math.Min(box.Layer, target.Layer - 1), right = Math.Max(box.Layer, target.Layer - 1);
					var obstacles = boxes.Where(b => b.Layer > left && b.Layer <= right).ToArray();
					float sourceY = group.Average(w => box.RowsTop + (w.Row + 0.5f) * RowHeight), targetY = ports[group.Key];
					var candidates = obstacles.SelectMany(b => new[] { b.Rect.y - S(16), b.Rect.yMax + S(16) })
						.Concat(new[] { sourceY, targetY });
					// 下方保留备用通道；同一去处同一线型可以共用。
					float bottom = boxes.Max(b => b.Rect.yMax);
					candidates = candidates.Concat(Enumerable.Range(0, rails.Count + 1).Select(i => bottom + S(16 + i * 16)));
					rail = candidates.Where(y => y >= S(8)
						&& obstacles.All(b => y <= b.Rect.y - S(12) || y >= b.Rect.yMax + S(12))
						&& rails.All(r => (r.From == box && r.To == target && r.Back == group.Key.Back) || r.Right < left || right < r.Left || Mathf.Abs(r.Y - y) >= S(8))
						&& exits.All(e => e.From.Layer < left || e.From.Layer > right || (e.Way.To == target && e.Way.Back == group.Key.Back) || Mathf.Abs(e.Y - y) >= S(8))
						&& ports.All(p => p.Key.To.Layer - 1 < left || p.Key.To.Layer - 1 > right || p.Key == group.Key || Mathf.Abs(p.Value - y) >= S(8)))
						.OrderBy(y => Mathf.Abs(y - sourceY) + Mathf.Abs(y - targetY)).First();
					rails.Add((box, target, group.Key.Back, left, right, rail));
				}
				foreach (var row in group) routes.Add((box, row.Way, row.Row, detour, bundle, rail));
			}
		// 同一去处的纵向范围先合并；互不相邻的范围复用线道，避免每增加一个目的地就加宽整列。
		var spans = new Dictionary<(int Column, FlowBox From, FlowBox To, bool Back), (float Low, float High)>();
		void Span(int column, FlowBox from, FlowWay way, float a, float b)
		{
			var key = (column, from, way.To, way.Back);
			float low = Mathf.Min(a, b), high = Mathf.Max(a, b);
			if (spans.TryGetValue(key, out var old)) { low = Mathf.Min(low, old.Low); high = Mathf.Max(high, old.High); }
			spans[key] = (low, high);
		}
		foreach (var route in routes)
		{
			float y = route.From.RowsTop + (route.Row + 0.5f) * RowHeight;
			if (route.Detour || route.Bundle) Span(route.From.Layer, route.From, route.Way, y, route.Rail);
			Span(route.Way.To.Layer - 1, null, route.Way, route.Detour || route.Bundle ? route.Rail : y, ports[(route.Way.To, route.Way.Back)]);
		}
		var lanes = new Dictionary<(int Column, FlowBox From, FlowBox To, bool Back), int>();
		var widths = new Dictionary<int, int>();
		foreach (var column in spans.GroupBy(s => s.Key.Column))
		{
			var slots = new List<List<(float Low, float High)>>();
			// 向下的长线靠近起点，向上的反过来，避免按选项顺序排成阶梯并反复交叉。
			foreach (var span in column.OrderBy(s => s.Key.From == null && shared.Contains((s.Key.To, s.Key.Back)) ? 1 : 0)
				.ThenBy(s => ports[(s.Key.To, s.Key.Back)] <= s.Value.Low ? 1 : 0)
				.ThenBy(s => ports[(s.Key.To, s.Key.Back)] <= s.Value.Low ? s.Value.Low : -s.Value.High))
			{
				// 与已排线道相交时放在其外侧；只复用无交叉的空位，不能为压缩宽度重新织成网格。
				int slot = slots.FindLastIndex(ranges => ranges.Any(r => span.Value.Low <= r.High + S(8) && r.Low <= span.Value.High + S(8))) + 1;
				if (slot == slots.Count) slots.Add(new List<(float, float)>());
				slots[slot].Add(span.Value);
				lanes[span.Key] = slot;
			}
			widths[column.Key] = slots.Count;
		}
		float shift = 0;
		int firstColumn = boxes.Length == 0 ? 0 : boxes.Min(b => b.Layer);
		foreach (var column in boxes.GroupBy(b => b.Layer).OrderBy(g => g.Key))
		{
			if (widths.TryGetValue(column.Key - 1, out int count)) shift += Mathf.Max(0, S(48 + count * 16) - (column.Key == firstColumn ? 0 : S(120)));
			foreach (FlowBox box in column) box.Rect.x += shift;
		}
		var columns = boxes.GroupBy(b => b.Layer).ToDictionary(g => g.Key, g => g.First().Rect.x);
		// 没有入口说明的图也可能回接第一列，需在左侧留出通道。
		if (widths.ContainsKey(firstColumn - 1)) columns[firstColumn - 1] = S(24 - 290);
		float Lane(int column, FlowBox from, FlowWay way)
		{
			int slot = lanes[(column, from, way.To, way.Back)];
			return from == null && shared.Contains((way.To, way.Back))
				? way.To.Rect.x - S(24 + (widths[column] - 1 - slot) * 16)
				: columns[column] + S(290 + 24 + slot * 16);
		}
		foreach (var route in routes)
		{
			var from = new Vector2(route.From.Rect.xMax, route.From.RowsTop + (route.Row + 0.5f) * RowHeight);
			FlowBox target = route.Way.To;
			var to = new Vector2(target.Rect.x, ports[(target, route.Way.Back)]);
			float input = Lane(target.Layer - 1, null, route.Way);
			route.Way.Points = !route.Detour && !route.Bundle
				? new[] { from, new Vector2(input, from.y), new Vector2(input, to.y), to }
				: new[] { from, new Vector2(Lane(route.From.Layer, route.From, route.Way), from.y), new Vector2(Lane(route.From.Layer, route.From, route.Way), route.Rail), new Vector2(input, route.Rail), new Vector2(input, to.y), to };
		}
		graph.Width += shift;
		foreach (var route in routes)
			foreach (Vector2 point in route.Way.Points)
			{
				graph.Width = Mathf.Max(graph.Width, point.x + S(24));
				graph.Height = Mathf.Max(graph.Height, point.y + S(24));
			}
		// 不同起点的共用线段也只画一次，防止虚线的间隙被重复绘制填满。
		foreach (var group in routes.GroupBy(r => (r.Way.To, r.Way.Back)))
		{
			var parts = group.SelectMany(r => r.Way.Points.Zip(r.Way.Points.Skip(1), (a, b) => (r.From, A: a, B: b)))
				.Where(p => p.A.x != p.B.x || p.A.y != p.B.y).GroupBy(p => (Vertical: p.A.x == p.B.x, Fixed: p.A.x == p.B.x ? p.A.x : p.A.y));
			foreach (var axis in parts)
			{
				var ranges = axis.Select(p => (p.From, Low: axis.Key.Vertical ? Mathf.Min(p.A.y, p.B.y) : Mathf.Min(p.A.x, p.B.x), High: axis.Key.Vertical ? Mathf.Max(p.A.y, p.B.y) : Mathf.Max(p.A.x, p.B.x)))
					.OrderBy(p => p.Low).ToArray();
				// 每个端点切开，选中某个起点时只高亮它真正经过的区间。
				float[] ends = ranges.SelectMany(r => new[] { r.Low, r.High }).Distinct().OrderBy(v => v).ToArray();
				for (int i = 1; i < ends.Length; i++)
				{
					float low = ends[i - 1], high = ends[i];
					FlowBox[] sources = ranges.Where(r => r.Low <= low && r.High >= high).Select(r => r.From).Distinct().ToArray();
					if (sources.Length == 0) continue;
					graph.Lines.Add((sources, group.Key.To, group.Key.Back,
						axis.Key.Vertical ? new Vector2(axis.Key.Fixed, low) : new Vector2(low, axis.Key.Fixed),
						axis.Key.Vertical ? new Vector2(axis.Key.Fixed, high) : new Vector2(high, axis.Key.Fixed)));
				}
			}
		}
		TintFlowLines(graph, boxes);
	}

	// 实线、虚线一并判断相邻或交叉；按目的地配色，整条线保持一致。
	private void TintFlowLines(FlowGraph graph, FlowBox[] boxes)
	{
		// 合流后再检查，避免为每个选项重复比较同一段；横向距离超过范围即停止。
		var segments = graph.Lines.Select(l => (l.To, Left: Mathf.Min(l.A.x, l.B.x), Right: Mathf.Max(l.A.x, l.B.x), Top: Mathf.Min(l.A.y, l.B.y), Bottom: Mathf.Max(l.A.y, l.B.y)))
			.Distinct().OrderBy(s => s.Left).ToArray();
		var neighbors = boxes.ToDictionary(b => b, b => new HashSet<FlowBox>());
		for (int i = 0; i < segments.Length; i++)
			for (int j = i + 1; j < segments.Length && segments[j].Left <= segments[i].Right + S(20); j++)
			{
				var a = segments[i]; var b = segments[j];
				if (a.To == b.To) continue;
				float dx = Mathf.Max(0, b.Left - a.Right), dy = Mathf.Max(0, Mathf.Max(a.Top - b.Bottom, b.Top - a.Bottom));
				if (dx * dx + dy * dy > S(20) * S(20)) continue;
				neighbors[a.To].Add(b.To); neighbors[b.To].Add(a.To);
			}
		foreach (FlowBox box in boxes.Where(b => neighbors[b].Count > 0).OrderByDescending(b => neighbors[b].Count))
		{
			box.LineTint = Enumerable.Range(0, neighbors[box].Count + 1).First(c => neighbors[box].All(b => b.LineTint != c));
			box.LineColor = Color.HSVToRGB((0.55f + box.LineTint * 0.381966f) % 1f, 0.35f, 0.78f);
		}
	}

	private void DrawFlowCanvas(Rect canvas)
	{
		FlowGraph graph = _flow;
		FlowBox entry = graph.EntryBox;
		var boxes = entry == null ? graph.Boxes : new[] { entry }.Concat(graph.Boxes);
		Event e = Event.current;
		if (e.type == EventType.ScrollWheel && canvas.Contains(e.mousePosition) && e.shift)
		{
			_flowScroll.x += e.delta.y * S(30);
			e.Use();
		}
		// 四周留出余量：图比窗口小时也能上下左右拖
		_flowScroll = GUI.BeginScrollView(canvas, _flowScroll, new Rect(0, 0, Mathf.Max(graph.Width, canvas.width) + canvas.width * 0.4f, Mathf.Max(Mathf.Max(graph.Height, (entry?.Rect.yMax ?? 0) + S(40)), canvas.height) + canvas.height * 0.4f));
		int pan = GUIUtility.GetControlID(FocusType.Passive);
		var view = new Rect(_flowScroll.x, _flowScroll.y, canvas.width, canvas.height);
		if (e.type == EventType.Repaint)
		{
			// 排版时统一避让；绘制仍复用原来的线段、选中高亮和入点。
			bool Hot(FlowBox[] sources, FlowBox to) => _flowPicked != null && (to == _flowPicked || sources.Contains(_flowPicked));
			foreach (var line in graph.Lines.OrderBy(l => Hot(l.Sources, l.To)))
			{
				bool hot = Hot(line.Sources, line.To);
				Color color = hot ? Cinnabar : InkLine;
				if (!hot && line.To.LineTint >= 0)
				{
					color = Color.Lerp(InkLine, line.To.LineColor, 0.3f);
					color.a = InkLine.a;
				}
				Segment(line.A, line.B, color, hot ? S(3) : S(2), line.Back, true);
				if (line.B.x == line.To.Rect.x) Fill(new Rect(line.B.x - S(7), line.B.y - S(4), S(7), S(8)), color);
			}
		}
		foreach (FlowBox box in boxes)
		{
			if (!box.Rect.Overlaps(view))
			{
				continue;
			}
			if (e.type == EventType.Repaint)
			{
				bool picked = box == _flowPicked;
				if (box.Terminal)
				{
					// 走出本任务的去处：虚线框；结局用朱砂
					Color c = box.Ending ? Cinnabar : InkMuted;
					Fill(box.Rect, Paper);
					Segment(new Vector2(box.Rect.x, box.Rect.y), new Vector2(box.Rect.xMax, box.Rect.y), c, 1, true);
					Segment(new Vector2(box.Rect.x, box.Rect.yMax), new Vector2(box.Rect.xMax, box.Rect.yMax), c, 1, true);
					Segment(new Vector2(box.Rect.x, box.Rect.y), new Vector2(box.Rect.x, box.Rect.yMax), c, 1, true);
					Segment(new Vector2(box.Rect.xMax, box.Rect.y), new Vector2(box.Rect.xMax, box.Rect.yMax), c, 1, true);
					var t = new GUIStyle(_boxSmall) { alignment = TextAnchor.MiddleCenter };
					t.normal.textColor = c;
					GUI.Label(box.Rect, box.Ending ? "<b>" + box.Title + "</b>" : box.Title, t);
					continue;
				}
				// 剧情段：宣纸卡片，墨边；标题下一条读过多少的进度，再往下一行一个去处
				Fill(box.Rect, picked ? Paper : PaperLight);
				Frame(box.Rect, picked ? Cinnabar : Ink, picked ? S(2) : 1);
				GUI.Label(new Rect(box.Rect.x + S(12), box.Rect.y + S(8), box.Rect.width - S(20), S(24)), Short(box.Title, 15), _boxTitle);
				GUI.Label(new Rect(box.Rect.x + S(12), box.Rect.y + S(32), box.Rect.width - S(20), S(20)), box.Sub, _boxSmall);
				ReadBar(new Rect(box.Rect.x + S(12), box.RowsTop - S(6), box.Rect.width - S(24), S(3)), box.Read);
				if (box.Ways.Count == 0)
				{
					GUI.Label(new Rect(box.Rect.x + S(12), box.RowsTop, box.Rect.width - S(24), RowHeight), "这一段之后没有直接接着的剧本", _boxSmall);
				}
				for (int k = 0; k < box.Ways.Count; k++)
				{
					FlowWay way = box.Ways[k];
					var row = new Rect(box.Rect.x + S(1), box.RowsTop + k * RowHeight, box.Rect.width - S(2), RowHeight);
					if (k > 0)
					{
						Fill(new Rect(row.x + S(10), row.y, row.width - S(20), 1), new Color(InkLine.r, InkLine.g, InkLine.b, InkLine.a * 0.35f));
					}
					bool stays = way.To == null;
					string text = stays ? way.Short + "　<color=#8A7E6E>留在本段</color>" : way.Short;
					if (_flowRow == null || !Mathf.Approximately(_flowRowScale, _scale) || _flowRowDark != PanelTheme.Dark)
					{
						_flowRowScale = _scale;
						_flowRowDark = PanelTheme.Dark;
						_flowRow = new GUIStyle(_tag) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
						_flowRow.normal.background = null;
						_flowRowMuted = new GUIStyle(_boxSmall) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip, richText = true };
					}
					if (box == entry) text = Fit(text, _flowRow, row.width - S(28));
					GUI.Label(new Rect(row.x + S(11), row.y, row.width - S(28), row.height), text, stays ? _flowRowMuted : _flowRow);
					if (!stays)
					{
						bool hot = _flowPicked != null && (box == _flowPicked || way.To == _flowPicked);
						Fill(new Rect(row.xMax - S(6), row.center.y - S(3), S(6), S(6)), hot ? Cinnabar : Ink);
					}
				}
				if (box.Kept != null)
				{
					var stamp = new Rect(box.Rect.xMax - S(26), box.Rect.y + S(6), S(20), S(20));
					Round(stamp, Cinnabar, S(3));
					GUI.Label(stamp, "存", new GUIStyle(_seal) { fontSize = Mathf.Max(8, Mathf.RoundToInt(_seal.fontSize * 0.65f)) });
				}
			}
			for (int k = 0; k < box.Ways.Count && !box.Terminal; k++)
			{
				FlowWay way = box.Ways[k];
				var row = new Rect(box.Rect.x, box.RowsTop + k * RowHeight, box.Rect.width, RowHeight);
				string full = way.Full.Count > 0 && way.To == null ? "「" + StoryScripts.OptionText(way.Full[0]) + "」" : string.Join("\n", way.Full.ToArray());
				string tip = (full.Length > 0 ? full : way.Short) + (way.To != null && !way.To.Terminal ? "\n→ " + way.To.Title : way.To != null ? "\n→ " + way.To.Title : "\n选了以后还在这一段里，之后合回来") + (way.Effects.Length > 0 ? "\n" + way.Effects : "");
				Hover(row, tip);
			}
			if (box != entry && !box.Terminal && GUI.Button(box.Rect, GUIContent.none, GUIStyle.none))
			{
				_flowPicked = box;
				_flowDetailScroll = Vector2.zero;
			}
		}
		switch (e.GetTypeForControl(pan))
		{
			// 右边、下边的滚动条留给滚动条自己拖
			case EventType.MouseDown when e.button == 0 && new Rect(view.x, view.y, view.width - S(22), view.height - S(22)).Contains(e.mousePosition):
				GUIUtility.hotControl = pan;
				e.Use();
				break;
			case EventType.MouseDrag when GUIUtility.hotControl == pan:
				_flowScroll -= e.delta;
				e.Use();
				break;
			case EventType.MouseUp when GUIUtility.hotControl == pan:
				GUIUtility.hotControl = 0;
				e.Use();
				break;
		}
		GUI.EndScrollView();
	}

	private void DrawBoxDetail(FlowBox box)
	{
		GUILayout.Label(string.Join(" → ", box.Chain.ToArray()), _muted);
		GUILayout.Label(box.Title, _heading);
		if (box.Sub.Length > 0)
		{
			GUILayout.Label(box.Sub, _label);
		}
		if (box.Kept != null)
		{
			ReplayButton(box.Kept, "从这里重玩", _primary, GUILayout.Height(S(38)));
		}
		if (!box.Terminal && box.Chain.Count > 0)
		{
			JumpButton(_flow.EntryWays.Count > 1 ? _flow.EntryWays.Select(w => w.Mission).FirstOrDefault(m => m.Scripts.Contains(box.Chain[0])) ?? _flowMission : _flowMission, box.Chain[0]);
		}
		if (box.Last == null)
		{
			return;
		}
		GUILayout.Space(S(10));
		GUILayout.Label("接下来", _heading);
		if (box.Last.Exits.Count == 0)
		{
			GUILayout.Label("这一段之后没有直接接着的剧本", _muted);
		}
		foreach (StoryExit exit in box.Last.Exits)
		{
			GUILayout.BeginVertical(_card);
			GUILayout.Label(Cached(exit, () => OnPaper(StoryScripts.Describe(exit, TextConverter.ForDisplay)) + "<color=#A3302A>" + ReadMark(exit) + "</color>"), _rich);
			string effects = Cached((exit, "效果", PanelTheme.Dark, LOMAssistant.Config.ConfigManager.ColorOptionDeltas.Value), () => PanelDeltas(string.Join("　", box.Last.EffectsOn(exit).ToArray())));
			if (effects.Length > 0)
			{
				GUILayout.Label(effects, _muted);
			}
			GUILayout.EndVertical();
		}
	}
}
