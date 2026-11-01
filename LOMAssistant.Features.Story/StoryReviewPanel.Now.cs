using System;
using System.Collections.Generic;
using System.Linq;
using LOMAssistant.LuaParse;
using Mortal.Core;
using Mortal.Story;
using UnityEngine;

namespace LOMAssistant.Features.Story;

/// <summary>
/// 「此刻」页：只讲当下。这一段剧情的选项（完整结果）和分支判定、当前地点的随机事件（概率、条件、结果，
/// 满足条件的可以选定下次触发）、现在能触发和差一点的任务。打开面板时整理一次，换了剧本再整理。
/// </summary>
public partial class StoryReviewPanel
{
	private sealed class NowEvent
	{
		public PositionEventData Data;
		public string Name = "", Desc = "";
		public int Rate, Percent;
		/// <summary>这个事件的剧情一句都没读过（和随机事件小窗的「没看过」一样）。</summary>
		public bool Unread;
	}

	private sealed class NowMission
	{
		public string Title = "", Desc = "", Place = "";
		public bool Ready, Main;
		/// <summary>离触发时间结束还剩几旬（限时剧情提醒用）；不限时间的为 null。</summary>
		public int? Left;
	}

	private sealed class NowSnapshot
	{
		public string Script = "";
		/// <summary>正在演的剧本（PlayerStatManagerData.CurrentStoryScript），跳剧情图、标未读用。</summary>
		public string Story = "";
		public StoryScript StoryScript;
		public readonly List<(string Key, string Option, string Result)> Options = new List<(string, string, string)>();
		/// <summary>这一段剧情的去处：走过哪些选项、掷骰、条件，接到哪里（和剧情图里「接下来」一样）。</summary>
		public readonly List<(string Way, string Effects)> Ways = new List<(string, string)>();
		public readonly List<NowEvent> Events = new List<NowEvent>();
		public readonly List<NowMission> Missions = new List<NowMission>();
	}

	private NowSnapshot _now;
	private Vector2 _nowScroll;

	private void DrawNow(Rect r)
	{
		// 标题画面：和数值页一样，只在左上角写一句
		if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Title")
		{
			GUI.Label(new Rect(r.x, r.y, r.width, S(40)), "读档进入游戏后才有内容", _muted);
			return;
		}
		if (_now == null || _now.Script != (OptionInfoPatch.LastScriptKey ?? ""))
		{
			_now = BuildNow();
		}
		if (GUI.Button(new Rect(r.xMax - S(130), r.y, S(130), S(36)), "刷新", _button))
		{
			_now = BuildNow();
		}
		// 跳到这一段所在任务的剧情图（剧本还没读完时先切过去，读完再定位）
		if (_now.Story.Length > 0)
		{
			bool owned = !StoryAtlas.Ready || StoryAtlas.Owner.ContainsKey(_now.Story);
			GUI.enabled = owned;
			string flowText = owned ? "在剧情图里看" : "这一段不属于任务";
			GUIStyle flowStyle = owned ? _primary : _button;
			float flowWidth = TextWidth(flowText, flowStyle, S(220));
			if (GUI.Button(new Rect(r.xMax - S(130) - S(10) - flowWidth, r.y, flowWidth, S(36)), flowText, flowStyle))
			{
				_flowJumpScript = _now.Story;
				_tab = 2;
			}
			GUI.enabled = true;
		}
		var area = new Rect(r.x, r.y + S(46), r.width, r.height - S(46));
		DragScroll("now", area, ref _nowScroll);
		GUILayout.BeginArea(area);
		_nowScroll = GUILayout.BeginScrollView(_nowScroll);
		bool any = false;
		if (_now.Options.Count > 0)
		{
			any = true;
			// 游戏里正在显示的那一组放最上面；其余按剧本里第几次选择分组（一段剧情里常有好几轮选择）
			var showing = new HashSet<string>(OptionInfoPatch.MenuKeys);
			var current = _now.Options.Where(o => showing.Contains(o.Key)).ToList();
			if (current.Count > 0)
			{
				GUILayout.Label("现在可以选的", _heading);
				foreach (var option in current)
				{
					DrawNowOption(option.Key, option.Option, option.Result);
				}
				GUILayout.Space(S(10));
			}
			var rest = _now.Options.Where(o => !showing.Contains(o.Key)).ToList();
			if (rest.Count > 0)
			{
				GUILayout.Label(current.Count > 0 ? "这一段的其他选项" : "这一段的选项", _heading);
				var groups = rest.GroupBy(o => MenuOf(o.Key)).OrderBy(g => g.Key < 0 ? int.MaxValue + g.Key : g.Key).ToList();
				foreach (var group in groups)
				{
					if (groups.Count > 1)
					{
						GUILayout.Label(group.Key == -2 ? "掷骰" : group.Key < 0 ? "其他" : "第 " + (group.Key + 1) + " 次选择", _muted);
					}
					foreach (var option in group)
					{
						DrawNowOption(option.Key, option.Option, option.Result);
					}
				}
				GUILayout.Space(S(10));
			}
		}
		if (_now.Ways.Count > 0)
		{
			any = true;
			GUILayout.Label("这一段接下来会怎么走", _heading);
			foreach (var (way, effects) in _now.Ways)
			{
				GUILayout.BeginVertical(_card);
				GUILayout.Label(way, _rich);
				if (effects.Length > 0)
				{
					GUILayout.Label(Cached((way, effects, PanelTheme.Dark), () => PanelDeltas(effects)), _muted);
				}
				GUILayout.EndVertical();
			}
			GUILayout.Space(S(10));
		}
		if (_now.Events.Count > 0)
		{
			any = true;
			GUILayout.Label("这里的随机事件", _heading);
			foreach (NowEvent ev in _now.Events)
			{
				GUILayout.BeginVertical(_card);
				GUILayout.BeginHorizontal();
				GUILayout.Label(ev.Name + "　" + (ev.Rate > 0 ? $"<color=#2F6B3A>{ev.Percent}%</color>" : "<color=#A3302A>条件不满足</color>") + (ev.Unread ? "　<color=#A3302A>没看过</color>" : ""), _rich);
				GUILayout.FlexibleSpace();
				if (ev.Rate > 0)
				{
					bool chosen = EventPicker.Pending == ev.Data;
					if (GUILayout.Button(chosen ? "已选定" : "下次触发这个", chosen ? _primary : _button, GUILayout.Width(S(150))))
					{
						EventPicker.Pending = chosen ? null : ev.Data;
					}
				}
				GUILayout.EndHorizontal();
				if (ev.Desc.Length > 0)
				{
					GUILayout.Label(ev.Desc, _rich);
				}
				GUILayout.EndVertical();
			}
			GUILayout.Space(S(10));
		}
		if (_now.Missions.Count > 0)
		{
			any = true;
			GUILayout.Label("现在的任务", _heading);
			foreach (NowMission m in _now.Missions)
			{
				GUILayout.BeginVertical(_card);
				// 限时剧情提醒开着时，条件已满足、快过期的标出还剩几旬
				string left = m.Ready && m.Left <= DeadlineReminder.Ahead && LOMAssistant.Config.ConfigManager.DeadlineReminder.Value
					? "　<color=#A3302A>" + (m.Left == 0 ? "这一旬是最后机会" : "还剩 " + m.Left + " 旬") + "</color>" : "";
				GUILayout.Label((m.Main ? "<color=#A3302A><b>主线</b></color>　" : "") + m.Title + "　" + (m.Ready ? "<color=#2F6B3A>条件已满足" + (m.Place.Length > 0 ? "，去「" + m.Place + "」就会触发" : "") + "</color>" : "<color=#73644F>还差条件</color>") + left, _rich);
				if (m.Desc.Length > 0)
				{
					GUILayout.Label(m.Desc, _rich);
				}
				GUILayout.EndVertical();
			}
		}
		if (!any)
		{
			GUILayout.Label("读档进入游戏后才有内容", _muted);
		}
		GUILayout.EndScrollView();
		GUILayout.EndArea();
	}

	/// <summary>这个选项是剧本里第几次选择（从 0 数）；掷骰的结果是 -2，找不到是 -1。</summary>
	private int MenuOf(string key)
	{
		StoryScript script = _now.StoryScript;
		if (script == null)
		{
			return -1;
		}
		int menu = script.Menus.FindIndex(m => m.Any(step => step.Key == key));
		if (menu >= 0)
		{
			return menu;
		}
		return script.Exits.Any(e => e.Path.Any(step => step.Kind == StoryStep.Dice && step.Key == key)) ? -2 : -1;
	}

	private void DrawNowOption(string key, string option, string result)
	{
		GUILayout.BeginVertical(_card);
		// 选了以后接下去的剧情读过多少（和剧情图里一样）
		string mark = _now.StoryScript == null ? "" : string.Concat(_now.StoryScript.Exits.Where(e => e.Path.Count > 0 && e.Path[0].Key == key).Select(ReadMark).Distinct().ToArray());
		GUILayout.Label(option + (mark.Length > 0 ? "　<color=#A3302A>" + mark + "</color>" : ""), _rich);
		if (result.Length > 0)
		{
			GUILayout.Label(Cached((key, "result", LOMAssistant.Config.ConfigManager.ColorOptionDeltas.Value), () => ColorDeltas(result)), _muted);
		}
		GUILayout.EndVertical();
	}

	private static readonly System.Text.RegularExpressions.Regex Delta = new System.Text.RegularExpressions.Regex(@"(?<![\w.])([+-])(\d+(?:\.\d+)?)");

	/// <summary>数值变化上色：加的绿、减的红。</summary>
	private static string ColorDeltas(string text) => !LOMAssistant.Config.ConfigManager.ColorOptionDeltas.Value ? text :
		Delta.Replace(text, m => (m.Groups[1].Value == "+" ? "<color=#2F6B3A>" : "<color=#A3302A>") + m.Value + "</color>");

	private static NowSnapshot BuildNow()
	{
		var now = new NowSnapshot { Script = OptionInfoPatch.LastScriptKey ?? "" };
		// 标题画面上游戏还留着默认（或上一局）的任务数据，不算「此刻」
		if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Title")
		{
			return now;
		}
		try
		{
			if (now.Script.Length > 0 && StoryManager.Instance != null)
			{
				foreach (KeyValuePair<string, List<LuaParse.Action>> option in LuaParser.Instance.ExtractOptionActionsFromLua(now.Script))
				{
					string text = RichText.Strip(StoryScripts.OptionText(option.Key)).Trim();
					// 和游戏里选项后面接的说明一样（被锁的写解锁条件、满足条件就接剧情的写「若……：触发剧情」）；取不到再只写选了会怎样
					string result = RichText.Strip(OptionInfoPatch.OptionNote(option.Key)).Trim();
					if (result.Length == 0)
					{
						result = RichText.Strip(TextConverter.ToSimplifiedUnlessJapanese(LuaParser.Description(option.Value))).Trim();
					}
					// 选项文字里已经被选项提示接上了「(结果)」，去掉，结果单独写在下面
					if (result.Length > 0 && text.EndsWith(result + ")"))
					{
						int cut = text.LastIndexOf('(', text.Length - result.Length - 1);
						if (cut > 0)
						{
							text = text.Substring(0, cut).Trim();
						}
					}
					now.Options.Add((option.Key, text.Length > 0 ? text : option.Key, result));
				}
				now.Story = PlayerStatManagerData.Instance?.CurrentStoryScript ?? "";
				StoryScript script = StoryScripts.Get(now.Story);
				now.StoryScript = script;
				if (script != null && script.Exits.Count > 1)
				{
					foreach (StoryExit exit in script.Exits)
					{
						now.Ways.Add((OnPaper(StoryScripts.Describe(exit, TextConverter.ForDisplay)), string.Join("　", script.EffectsOn(exit).ToArray())));
					}
				}
				foreach (string pool in LuaParser.Instance.ExtractPositionWorkFromLua(now.Script))
				{
					var events = PositionEventCache.Instance.GetPositionEventsSortedByRate(pool);
					int total = Math.Max(1, events.Sum(e => Math.Max(0, e.GetRate())));
					foreach (PositionEventData data in events)
					{
						int rate = data.GetRate();
						now.Events.Add(new NowEvent
						{
							Data = data,
							Name = TextConverter.ForDisplay(TextConverter.ToSimplified(PositionEventCache.PlaceName(pool))) + "　" + TextConverter.ForDisplay("事件：") + "　" + DataHelper.GetEventName(data),
							Desc = OnPaper(TextConverter.ToSimplified(PositionEventCache.Instance.GetPositionEventDesc(data)).Trim()),
							Rate = rate,
							Percent = rate > 0 ? Mathf.RoundToInt(rate * 100f / total) : 0,
							Unread = MapEventHints.Unread(PositionEventCache.Instance.ResultScript(data))
						});
					}
				}
			}
			// 主线当前这一步：游戏判定条件满足时 MainMissionStart 为真，去 MainMissionPosition 就会触发
			MissionManagerData missions = MissionManagerData.Instance;
			if (DataHelper.GetMainMission(out MissionCheckData main, out string mainTitle, out string mainDesc))
			{
				string place = TextConverter.ToSimplified(main.Position.ToString());
				now.Missions.Add(new NowMission
				{
					Main = true,
					Title = TextConverter.ToSimplified(mainTitle.Replace("\n", " ")),
					Desc = OnPaper(TextConverter.ToSimplified(mainDesc).Trim()),
					Place = place == "無" ? "" : place,
					Ready = missions != null && missions.MainMissionStart && missions.MainCheckData == main && !string.IsNullOrEmpty(main.StartScript),
					Left = DeadlineReminder.Left(main)
				});
			}
			var active = missions?.ActiveSubMissions;
			// 留学（崆峒、青城）等主角不在唐门的时候，唐门各处的支线去不了、触发不了，只列不限地点的
			bool away = (missions?.GetFlag("E11000")?.State ?? 0) == 1;
			foreach (var pair in DataHelper.GetMissions())
			{
				MissionCheckData check = pair.Key;
				if (away && check.Position != PositionType.無)
				{
					continue;
				}
				string place = TextConverter.ToSimplified(check.Position.ToString());
				now.Missions.Add(new NowMission
				{
					Title = TextConverter.ToSimplified(pair.Value.Item1.Replace("\n", " ")),
					Desc = OnPaper(TextConverter.ToSimplified(pair.Value.Item2).Trim()),
					Place = place == "無" ? "" : place,
					Ready = active != null && active.Values.Contains(check) && !string.IsNullOrEmpty(check.StartScript),
					Left = DeadlineReminder.Left(check)
				});
			}
			// 主线在最前，其余条件满足的在前；OrderBy 是稳定排序，同组保持游戏里的顺序
			var sorted = now.Missions.OrderByDescending(m => m.Main).ThenByDescending(m => m.Ready).ToList();
			now.Missions.Clear();
			now.Missions.AddRange(sorted);
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[剧情回顾] 整理「此刻」失败: " + ex.Message);
		}
		return now;
	}
}
