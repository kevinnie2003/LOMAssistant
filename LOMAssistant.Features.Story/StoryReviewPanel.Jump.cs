using System.Linq;
using UnityEngine;

namespace LOMAssistant.Features.Story;

/// <summary>
/// 剧情跳转的确认页：选中一段剧情点「跳到这里」后整页换成它，写明会改什么、补不全什么，有几条路能走到时让玩家选一条。
/// 确认后先存「跳转前」的随时存档，再跳。
/// </summary>
public partial class StoryReviewPanel
{
	private StoryJump.Plan _jumpPlan;
	private Vector2 _jumpScroll;
	private string _jumpError;

	/// <summary>剧情图、路线图详情里的「跳到这里」。</summary>
	private void JumpButton(StoryMission mission, string script)
	{
		if (!StoryJump.Enabled || mission == null || mission.PlaceEntry || string.IsNullOrEmpty(script))
		{
			return;
		}
		if (GUILayout.Button("跳到这里", _button, GUILayout.Height(S(38))))
		{
			_jumpPlan = StoryJump.Build(mission, script);
			_jumpScroll = Vector2.zero;
			_jumpError = null;
		}
	}

	private void DrawJump(Rect r)
	{
		StoryJump.Plan plan = _jumpPlan;
		if (GUI.Button(new Rect(r.x, r.y, S(110), S(36)), "返回", _button))
		{
			_jumpPlan = null;
			return;
		}
		GUI.Label(new Rect(r.x + S(126), r.y, r.width - S(126), S(36)), "跳到「" + plan.Title + "」", Middle(_heading));
		var area = new Rect(r.x, r.y + S(46), r.width, r.height - S(46) - S(56));
		DragScroll("jump", area, ref _jumpScroll);
		GUILayout.BeginArea(area);
		_jumpScroll = GUILayout.BeginScrollView(_jumpScroll);
		GUILayout.Label("<color=#A3302A><b>正常游玩不建议这样做。</b></color>中间的剧情没有真的演过，只按剧本把一路上会设的旗标、剧情记录、任务进度、物品、好感和数值补上，"
			+ "有些状态补不全，之后可能出现对话对不上、分支判断出错，甚至卡住。跳之前会先存一份随时存档，跳过去以后可以回到跳转前。", _rich);
		GUILayout.Space(S(10));

		GUILayout.Label("会改的", _heading);
		GUILayout.Label("任务：" + plan.Mission.Title + (plan.Mission.Main ? "（主线）" : "（" + plan.Mission.SeriesTitle + "）"), _label);
		if (plan.Mission.Main && plan.MissionFrom.Length > 0 && plan.MissionFrom != plan.MissionTo)
		{
			GUILayout.Label("主线进度：" + plan.MissionFrom + " → " + plan.MissionTo, _label);
		}
		if (plan.ToRounds != null)
		{
			GUILayout.Label("日期：" + (plan.FromRounds != null ? RoundsDate(plan.FromRounds.Value) + " → " : "") + RoundsDate(plan.ToRounds.Value), _label);
		}
		if (plan.Between.Count > 0)
		{
			GUILayout.Label("补上中间的主线：" + string.Join("、", plan.Between.Select(m => m.Title).ToArray()), _label);
		}
		GUILayout.Label(string.Format("补 {0} 项变化（其中剧情记录 {1} 条）", plan.Commands.Count, plan.Records), _label);
		if (plan.Effects.Count > 0)
		{
			string effects = Cached(("jump", plan.Commands.Count, string.Join(",", plan.Forks.Select(f => f.Picked.ToString()).ToArray()), plan.Effects.Count, PanelTheme.Dark, LOMAssistant.Config.ConfigManager.ColorOptionDeltas.Value),
				() => PanelDeltas(string.Join("　", plan.Effects.Distinct().ToArray())));
			GUILayout.Label(effects, _muted);
		}

		// 顺便结缘：结缘旗标大多在支线里设，跳转只走主线补不上，在这里直接选一位
		GUILayout.Space(S(10));
		GUILayout.Label("顺便结缘", _heading);
		GUILayout.Label("结缘要的旗标大多在支线剧情里设，跳转补不上。选一位，跳过去时把她的结缘旗标都设上、好感拉到 100、心上人设成她，一路上看结缘的岔口也默认走她那一支。", _muted);
		GUILayout.BeginHorizontal();
		// 已经结缘的存档：默认什么都不改，写出现在是和谁
		StoryRomance.Heroine bonded = StoryRomance.All.FirstOrDefault(x => StoryRomance.StepDone(x.Steps[x.Steps.Length - 1]));
		string keep = bonded != null ? string.Format("保持现在（已结缘：{0}）", StoryRomance.Name(bonded)) : "保持现在（不结缘）";
		if (GUILayout.Button(keep, plan.Heroine == null ? _primary : _button, GUILayout.Height(S(34))))
		{
			plan.Heroine = null;
			StoryJump.Recompute(plan);
		}
		int perRow = 0;
		foreach (StoryRomance.Heroine h in StoryRomance.All)
		{
			if (++perRow == 6)
			{
				GUILayout.FlexibleSpace();
				GUILayout.EndHorizontal();
				GUILayout.BeginHorizontal();
				perRow = 1;
			}
			// 目标比她结缘那一段还早的不能选，悬停写最早要到什么时候
			bool bondable = StoryJump.CanBond(plan, h, out string when);
			bool enabledBefore = GUI.enabled;
			GUI.enabled = enabledBefore && bondable;
			if (GUILayout.Button(StoryRomance.Name(h), plan.Heroine == h ? _primary : _button, GUILayout.Height(S(34))))
			{
				plan.Heroine = h;
				StoryJump.Recompute(plan);
			}
			GUI.enabled = enabledBefore;
			if (!bondable)
			{
				Hover(GUILayoutUtility.GetLastRect(), when.Length > 0 ? string.Format("最早 {0} 才能和她结缘", when) : "游戏里还没有和她结缘的剧情");
			}
		}
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
		if (plan.Heroine != null && !StoryJump.CanBond(plan, plan.Heroine, out _))
		{
			plan.Heroine = null;
			StoryJump.Recompute(plan);
		}
		if (plan.Heroine != null && bonded != null && bonded != plan.Heroine)
		{
			GUILayout.Label("<color=#A3302A>" + string.Format("已经和{0}结缘：她的结缘旗标不会清掉，等于两位都结缘了，之后的剧情可能对不上", StoryRomance.Name(bonded)) + "</color>", _rich);
		}

		if (plan.Forks.Any(f => f.Matters) || plan.Minor > 0)
		{
			GUILayout.Space(S(10));
			GUILayout.Label("一路上的岔路", _heading);
			GUILayout.Label("下面是会让流程不一样的岔口（去处、主线、旗标、任务进度不同）。每个岔口选一条，就补那条路上的变化；不选按默认（目标任务里走最近的那条，中间的主线走接到下一站的那条）。", _muted);
			StoryMission shown = null;
			if (plan.Minor > 0)
			{
				GUILayout.Label(string.Format("另有 {0} 处只影响数值、好感或对白的岔路，按默认补", plan.Minor), _muted);
			}
			foreach (StoryJump.Fork fork in plan.Forks.Where(f => f.Matters).ToList())
			{
				if (fork.Mission != shown)
				{
					shown = fork.Mission;
					GUILayout.Space(S(6));
					GUILayout.Label("<b>" + shown.Title + "</b>" + (shown == plan.Mission ? "" : shown.Main ? "　<color=#73644F>中间的主线</color>" : "　<color=#73644F>中间的支线</color>"), _rich);
				}
				GUILayout.BeginVertical(_card);
				GUILayout.Label("「" + fork.Segment + "」", _muted);
				fork.RefreshOptions();
				for (int i = 0; i < fork.Options.Count; i++)
				{
					bool picked = i == fork.Picked;
					if (GUILayout.Button((picked ? "● " : "○ ") + fork.Options[i], new GUIStyle(picked ? _primary : _button) { alignment = TextAnchor.MiddleLeft, wordWrap = true }))
					{
						StoryJump.Pick(plan, fork.Key, i);
					}
				}
				GUILayout.EndVertical();
			}
		}

		if (plan.Notes.Count > 0)
		{
			GUILayout.Space(S(10));
			GUILayout.Label("补不全的", _heading);
			foreach (string note in plan.Notes)
			{
				GUILayout.Label("· " + note, _label);
			}
		}
		GUILayout.EndScrollView();
		GUILayout.EndArea();

		// 底下一行：跳
		var bar = new Rect(r.x, r.yMax - S(46), r.width, S(46));
		bool can = StoryJump.CanJump(out string reason);
		string hint = _jumpError ?? (can ? "跳之前会先存一份随时存档，备注「跳转前」" : reason);
		GUI.Label(new Rect(bar.x, bar.y, bar.width - S(200), bar.height), hint, _jumpError != null ? _redNotice : Middle(_muted));
		bool wasEnabled = GUI.enabled;
		GUI.enabled = wasEnabled && can;
		if (GUI.Button(new Rect(bar.xMax - S(190), bar.y + S(4), S(190), S(40)), "仍然跳过去", _primary))
		{
			if (StoryJump.Execute(plan, out string message))
			{
				_jumpPlan = null;
				_visible = false;
			}
			else
			{
				_jumpError = message;
			}
		}
		GUI.enabled = wasEnabled;
	}

	private static string RoundsDate(int rounds)
	{
		int r = Mathf.Max(1, rounds) - 1;
		return $"第{r / 36 + 1}年{r % 36 / 3 + 1}月{StoryAtlas.StageText(r % 3 + 1)}";
	}
}
