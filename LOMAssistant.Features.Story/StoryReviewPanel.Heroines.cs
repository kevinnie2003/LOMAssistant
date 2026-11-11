using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LOMAssistant.Features.Story;

/// <summary>
/// 收集进度「女主线」：每位可结缘的角色，她那条线的对白读过多少（全部周目）、她的结局解锁了几个，最上面一行是合计。
/// 哪些对白算她的见 StoryRomance.Routes。
/// </summary>
public partial class StoryReviewPanel
{
	private readonly HashSet<int> _heroinesOpen = new HashSet<int>();

	/// <summary>读完全部对白、解锁全部结局的有几位；剧本还没读完返回 null。</summary>
	private static (int Done, int All)? HeroinesDone()
	{
		var list = StoryRomance.All.Select(StoryRomance.ProgressOf).ToList();
		return list.Any(p => p == null) ? ((int, int)?)null : (list.Count(p => p.Done), list.Count);
	}

	private int DrawHeroineSection(string query)
	{
		if (StoryRomance.GatesMissing)
		{
			if (query.Length > 0)
			{
				return 0;
			}
			GUILayout.Label("<color=#73644F>" + "进入一次剧情后才能统计" + "</color>", _rich);
			return 1;
		}
		var rows = StoryRomance.All.Select(h => (Heroine: h, Name: StoryRomance.Name(h), Progress: StoryRomance.ProgressOf(h)))
			.Where(x => x.Progress != null).ToList();
		var shown = rows.Where(x => _endingsFilter == 0 || x.Progress.Done == (_endingsFilter == 1))
			.Where(x => Matches(query, new[] { x.Name }.Concat(x.Progress.Missions.Where(m => m.Mission != null).Select(m => m.Mission.Title)).ToArray())).ToList();
		if (shown.Count == 0)
		{
			return 0;
		}
		if (query.Length > 0)
		{
			GUILayout.Space(S(8));
			GUILayout.Label(EndingSections[HeroineSection], _heading);
		}
		else
		{
			// 合计：几位的对白有重叠（同一段里两人都在），合计按去重前相加
			int read = rows.Sum(x => x.Progress.Read), lines = rows.Sum(x => x.Progress.Lines);
			int endingsDone = rows.Sum(x => x.Progress.EndingsDone), endings = rows.Sum(x => x.Progress.Endings.Count);
			GUILayout.BeginVertical(_card);
			GUILayout.Label("<b>" + string.Format("合计　{0}%", lines == 0 ? 0 : (int)System.Math.Floor(100.0 * read / lines)) + "</b>", _label);
			GUILayout.Label("<color=#73644F>" + string.Format("对白 已读 {0} / 共 {1}", read, lines) + "　·　" + string.Format("结局 {0}/{1}", endingsDone, endings) + "</color>", _rich);
			GUILayout.EndVertical();
		}
		foreach (var (h, name, p) in shown)
		{
			GUILayout.BeginVertical(_card);
			GUILayout.BeginHorizontal();
			GUILayout.Label(p.Done ? "<color=#2F6B3A><b>✓ 完成</b></color>" : "<color=#A3302A>" + p.Percent + "%</color>", _small, GUILayout.Width(S(80)));
			GUILayout.Label("<b>" + name + "</b>", _label);
			GUILayout.FlexibleSpace();
			bool open = _heroinesOpen.Contains(h.Lover) || query.Length > 0;
			if (query.Length == 0 && GUILayout.Button(open ? "收起" : "展开", _button, GUILayout.Width(TextWidth("展开", _button, S(90)))))
			{
				if (!_heroinesOpen.Remove(h.Lover))
				{
					_heroinesOpen.Add(h.Lover);
				}
			}
			GUILayout.EndHorizontal();
			string summary = string.Format("对白 已读 {0} / 共 {1}", p.Read, p.Lines);
			if (p.Endings.Count > 0)
			{
				summary += "　·　" + string.Format("结局 {0}/{1}", p.EndingsDone, p.Endings.Count);
			}
			GUILayout.Label("<color=#73644F>" + summary + "</color>", _rich);
			if (open)
			{
				foreach (StoryEnding ending in p.Endings)
				{
					string title = ending.Title.Length > 0 ? EndingNumber(ending) + ending.Title : "结局 CG";
					GUILayout.Label((ending.Achieved ? "<color=#2F6B3A>✓</color>　" : "<color=#A3302A>✗</color>　") + title, _rich);
				}
				foreach (StoryRomance.RouteMission rm in p.Missions)
				{
					StoryMission m = rm.Mission;
					if (m != null && !Matches(query, name, m.Title))
					{
						continue;
					}
					GUILayout.BeginHorizontal();
					string state = rm.Read == rm.Lines.Count ? "<color=#2F6B3A>✓ 读完</color>" : "<color=#A3302A>" + string.Format("{0}/{1} 句", rm.Read, rm.Lines.Count) + "</color>";
					GUILayout.Label(state, _small, GUILayout.Width(S(110)));
					string where = m == null ? "" : (m.Main ? "主线" : m.SeriesTitle) + (m.When.Length > 0 ? "　·　" + m.When : "");
					GUILayout.Label((m != null ? m.Title : "闲聊和其他") + (where.Length > 0 ? "　<color=#73644F>" + where + "</color>" : ""), _rich);
					GUILayout.FlexibleSpace();
					if (m != null && GUILayout.Button("看剧情图", _button, GUILayout.Width(TextWidth("看剧情图", _button, S(130)))))
					{
						OpenFlow(m);
					}
					GUILayout.EndHorizontal();
				}
			}
			GUILayout.EndVertical();
		}
		return shown.Count;
	}
}
