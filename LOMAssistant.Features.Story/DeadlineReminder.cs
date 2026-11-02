using System.Collections.Generic;
using System.Linq;
using LOMAssistant.Config;
using Mortal.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LOMAssistant.Features.Story;

/// <summary>
/// 限时剧情提醒（设置「限时剧情提醒」，辅助及以上的预设打开）：条件已经满足、还没触发的任务，离触发时间结束只剩 3 旬以内时，
/// 在地图左上角列出来，写还剩几旬、到哪天为止、去哪里触发。只列条件已满足的，不提前透露还没到的剧情。
/// 「此刻」页的任务也用同一套算法标出还剩几旬。
/// </summary>
public class DeadlineReminder : MonoBehaviour
{
	/// <summary>离结束还剩几旬以内开始提醒。</summary>
	public const int Ahead = 3;

	internal sealed class Item
	{
		public string Title = "", Place = "", Until = "";
		public int Left;
		public bool Main;
	}

	private static DeadlineReminder _instance;
	private string _text = "", _language;
	private int _builtFor = -1;
	private float _nextCheck;
	private float _scale = -1f;
	private int _fontMode = -1;
	private GUIStyle _box;

	public static void Ensure(GameObject host)
	{
		if (_instance == null)
		{
			_instance = host.GetComponent<DeadlineReminder>() ?? host.AddComponent<DeadlineReminder>();
		}
	}

	/// <summary>
	/// 这一步离触发时间结束还剩几旬（0 是这一旬最后机会）；不限时间、还没开始或已经过了的返回 null。
	/// 时限有两种写法：任务本身的触发时间段；或者没设时间段、写在条件里（开局的「早操」「报复飞石帮」是「目前时间 ≤ 第一年五月下旬」）。两个都有取早的。
	/// </summary>
	internal static int? Left(MissionCheckData check)
	{
		if (check == null)
		{
			return null;
		}
		int? left = null;
		if (check.GetFieldValue<bool>("_dayCheck") && Window(check, out GameTime start, out GameTime end))
		{
			int? now = StoryAtlas.Rounds(PlayerStatManagerData.Instance?.GameTime), from = StoryAtlas.Rounds(start), to = StoryAtlas.Rounds(end);
			if (now == null || to == null || now > to || (from != null && now < from))
			{
				return null;
			}
			left = to - now;
		}
		int? byCondition = ConditionLeft(check);
		if (byCondition != null && byCondition >= 0)
		{
			left = left == null ? byCondition : System.Math.Min(left.Value, byCondition.Value);
		}
		return left;
	}

	// 条件里「目前时间 ≤ 某天」「某天 ≥ 目前时间」（还有 <、>、=）给的截止：还剩几旬；没有这种条件返回 null。
	// 用「或」连起来的一组条件单看一条定不了截止，跳过
	private static int? ConditionLeft(MissionCheckData check)
	{
		GameTimeData current = PlayerStatManagerData.Instance?.GetFieldValue<GameTimeData>("_gameTimeData");
		if (current == null)
		{
			return null;
		}
		int? left = null;
		foreach (ConditionResultItem item in check.GetFieldValue<ConditionResultItem[]>("_conditions") ?? new ConditionResultItem[0])
		{
			List<StatCompareItem> compares = item?.GetFieldValue<List<StatCompareItem>>("_items");
			if (compares == null || (compares.Count > 1 && item.GetFieldValue<LogicOperatorType>("_logicOp") != LogicOperatorType.AND))
			{
				continue;
			}
			foreach (StatCompareItem compare in compares)
			{
				StatGroupVariable a = compare.GetFieldValue<StatGroupVariable>("_value1"), b = compare.GetFieldValue<StatGroupVariable>("_value2");
				StatCompareType op = compare.GetFieldValue<StatCompareType>("_compareOp");
				int? end = null, now = null;
				if (IsTime(a, current, true) && IsTime(b, current, false))
				{
					now = a.GetResult();
					end = op == StatCompareType.LessEqual || op == StatCompareType.Equal ? b.GetResult() : op == StatCompareType.Less ? b.GetResult() - 1 : (int?)null;
				}
				else if (IsTime(b, current, true) && IsTime(a, current, false))
				{
					now = b.GetResult();
					end = op == StatCompareType.GreaterEqual || op == StatCompareType.Equal ? a.GetResult() : op == StatCompareType.Greater ? a.GetResult() - 1 : (int?)null;
				}
				if (end != null)
				{
					left = left == null ? end - now : System.Math.Min(left.Value, end.Value - now.Value);
				}
			}
		}
		return left;
	}

	// 只由一个游戏时间组成的数值：current 为 true 时要的是玩家现在的时间，false 时要的是某个固定的日子
	private static bool IsTime(StatGroupVariable variable, GameTimeData current, bool wantCurrent)
	{
		List<StatValueReference> refs = variable?.GetFieldValue<List<StatValueReference>>("_value");
		if (refs == null || refs.Count != 1 || refs[0] == null || refs[0].CheckType != StatCheckType.遊戲時間)
		{
			return false;
		}
		GameTimeData time = refs[0].GetFieldValue<GameTimeData>("_gameTime");
		return time != null && ReferenceEquals(time, current) == wantCurrent;
	}

	// 某一旬触发的、一段时间里触发的有截止时间；每月一次的没有
	private static bool Window(MissionCheckData check, out GameTime start, out GameTime end)
	{
		switch (check.GetFieldValue<TimeCheckType>("_timeCheckType"))
		{
			case TimeCheckType.特定:
				start = end = check.GetFieldValue<GameTime>("_specificTime");
				return true;
			case TimeCheckType.區間:
				start = check.GetFieldValue<GameTime>("_minTime");
				end = check.GetFieldValue<GameTime>("_maxTime");
				return true;
			default:
				start = end = null;
				return false;
		}
	}

	/// <summary>条件已满足、快过期的任务，剩得少的在前。</summary>
	internal static List<Item> Expiring()
	{
		var list = new List<Item>();
		MissionManagerData missions = MissionManagerData.Instance;
		if (missions == null)
		{
			return list;
		}
		if (DataHelper.GetMainMission(out MissionCheckData main, out string mainTitle, out _) && missions.MainMissionStart && missions.MainCheckData == main)
		{
			Add(list, main, mainTitle, true);
		}
		var active = missions.ActiveSubMissions;
		// 留学（崆峒、青城）时唐门各处的支线触发不了，和「此刻」页一样只算不限地点的
		bool away = (missions.GetFlag("E11000")?.State ?? 0) == 1;
		foreach (var pair in DataHelper.GetMissions())
		{
			if (active == null || !active.Values.Contains(pair.Key) || (away && pair.Key.Position != PositionType.無))
			{
				continue;
			}
			Add(list, pair.Key, pair.Value.Item1, false);
		}
		return list.OrderBy(i => i.Left).ThenByDescending(i => i.Main).ToList();
	}

	private static void Add(List<Item> list, MissionCheckData check, string title, bool main)
	{
		int? left = Left(check);
		int? now = StoryAtlas.Rounds(PlayerStatManagerData.Instance?.GameTime);
		if (left == null || left > Ahead || now == null || string.IsNullOrEmpty(check.StartScript))
		{
			return;
		}
		string place = TextConverter.ToSimplified(check.Position.ToString());
		// 任务这一步没写说明时游戏数据里只有编号（「抢劫山贼 · S9904_01」），编号不显示
		string name = string.Join(" · ", title.Replace("\n", " ").Split(new[] { " · " }, System.StringSplitOptions.None)
			.Where(part => !System.Text.RegularExpressions.Regex.IsMatch(part.Trim(), @"^[A-Za-z0-9_]+$")).ToArray());
		list.Add(new Item
		{
			Title = TextConverter.ToSimplified(name.Length > 0 ? name : title),
			Place = place == "無" ? "" : place,
			Until = StoryAtlas.DateText(now.Value + left.Value),
			Left = left.Value,
			Main = main,
		});
	}

	/// <summary>一条提醒：「还剩 2 旬　到第3年5月下旬为止　去「地点」触发」，各段用全角空格隔开，翻译时分段对照。</summary>
	internal static string When(Item item) =>
		(item.Left == 0 ? "这一旬是最后机会" : "还剩 " + item.Left + " 旬") + "　到" + item.Until + "为止" + (item.Place.Length > 0 ? "　去「" + item.Place + "」触发" : "");

	private void Update()
	{
		// 只在地图上提醒；日期变了、或隔几秒（任务条件可能刚满足）重新算一次
		if (!ConfigManager.DeadlineReminder.Value || SceneManager.GetActiveScene().name != "Free")
		{
			_text = "";
			_builtFor = -1;
			return;
		}
		int now = StoryAtlas.Rounds(PlayerStatManagerData.Instance?.GameTime) ?? -1;
		if (now == _builtFor && _language == TextConverter.DisplayLanguage && Time.unscaledTime < _nextCheck)
		{
			return;
		}
		_builtFor = now;
		_language = TextConverter.DisplayLanguage;
		_nextCheck = Time.unscaledTime + 3f;
		try
		{
			List<Item> items = Expiring();
			_text = items.Count == 0 ? "" : TextConverter.ForDisplay("<b>限时剧情</b>") + "\n" + string.Join("\n", items.Select(i =>
				TextConverter.ForDisplay((i.Main ? "主线　" : "") + i.Title) + "\n  " + TextConverter.ForDisplay(When(i))).ToArray());
		}
		catch (System.Exception ex)
		{
			_text = "";
			FileLogger.LogWarning("[限时剧情] 整理失败: " + ex.Message);
		}
	}

	private void OnGUI()
	{
		if (_text.Length == 0 || UiHider.Active || InputGuard.ModWindowOpen || EventPicker.IsWaiting || Event.current.type != EventType.Repaint)
		{
			return;
		}
		float scale = PanelTheme.UiScale;
		if (scale <= 0f)
		{
			return;
		}
		if (_box == null || ModFont.NeedsRebuild(scale, _scale, _fontMode))
		{
			_scale = scale;
			_fontMode = ModFont.Mode;
			// 和地图事件提示同一种样子
			_box = PanelTheme.Toast(scale, 17);
			_box.alignment = TextAnchor.UpperLeft;
			_box.richText = true;
			_box.wordWrap = false;
			_box.padding = new RectOffset((int)(12f * scale), (int)(12f * scale), (int)(8f * scale), (int)(8f * scale));
		}
		var content = new GUIContent(_text);
		Vector2 size = _box.CalcSize(content);
		size.x = Mathf.Min(size.x, Screen.width * 0.45f);
		// 左上角日期下面
		GUI.Label(new Rect(20f * scale, 170f * scale, size.x, _box.CalcHeight(content, size.x)), content, _box);
	}
}
