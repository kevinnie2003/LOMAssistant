using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LOMAssistant.Config;
using LOMAssistant.LuaParse;
using Mortal.Core;
using Mortal.Story;
using UnityEngine;
using UnityEngine.UI;

namespace LOMAssistant;

/// <summary>
/// 条件求解：算出要改哪些数值才能让条件成立。只看属性、好感、旗标（以及它们相加）；
/// 含随机值、时间、物品、嵌套条件等改不了的部分时返回 null。思路参考赵活系统（Toufff，MIT）。
/// 不调用游戏的 Check/Execute（Execute 会累加计数，随机值会动随机数），自己按同样的规则算。
/// </summary>
internal static class ConditionSolver
{
	internal sealed class Write
	{
		public object Target;
		public int Value;
	}

	public static ConditionResultData Find(string name)
	{
		CheckPointManager cpm = CheckPointManager.Instance;
		ConditionResultConfig config = cpm != null ? cpm.GetFieldValue<ConditionResultConfig>("_condition") : null;
		return config != null && config.List != null ? config.List.Find(x => x != null && x.name == name) : null;
	}

	/// <summary>条件现在是否成立；含读不了的部分时返回 null。</summary>
	public static bool? Holds(ConditionResultData data)
	{
		bool? any = false;
		foreach (ConditionResultItem item in data.GetFieldValue<List<ConditionResultItem>>("_items"))
		{
			bool? h = Holds(item);
			if (h == true) return true;
			if (h == null) any = null;
		}
		return any;
	}

	private static bool? Holds(ConditionResultItem item)
	{
		var parts = new List<bool>();
		foreach (StatCompareItem c in item.GetFieldValue<List<StatCompareItem>>("_items"))
		{
			int? a = Eval(c.GetFieldValue<StatGroupVariable>("_value1"));
			int? b = Eval(c.GetFieldValue<StatGroupVariable>("_value2"));
			if (a == null || b == null) return null;
			parts.Add(GameStatUtils.StatCompare(a.Value, b.Value, c.GetFieldValue<StatCompareType>("_compareOp")));
		}
		return parts.Count > 0 && GameStatUtils.LogicOp(parts.ToArray(), item.GetFieldValue<LogicOperatorType>("_logicOp"));
	}

	/// <summary>让条件成立要写的值：满足时为空列表，做不到为 null。「或」取要改最少的一支。</summary>
	public static List<Write> Satisfy(ConditionResultData data)
	{
		List<Write> best = null;
		foreach (ConditionResultItem item in data.GetFieldValue<List<ConditionResultItem>>("_items"))
		{
			List<Write> w = Satisfy(item);
			if (w != null && (best == null || w.Count < best.Count)) best = w;
		}
		return best;
	}

	/// <summary>让一组条件（分支判断的一支）成立要写的值：满足时为空列表，做不到为 null。</summary>
	public static List<Write> Satisfy(ConditionResultItem item)
	{
		var parts = item.GetFieldValue<List<StatCompareItem>>("_items").Select(SatisfyCompare).ToList();
		if (parts.Count == 0) return null;
		if (item.GetFieldValue<LogicOperatorType>("_logicOp") == LogicOperatorType.OR)
		{
			return parts.Where(p => p != null).OrderBy(p => p.Count).FirstOrDefault();
		}
		if (parts.Any(p => p == null)) return null;
		var writes = new List<Write>();
		foreach (Write w in parts.SelectMany(p => p))
		{
			Write same = writes.Find(x => x.Target == w.Target);
			if (same == null) writes.Add(w);
			else if (same.Value != w.Value) return null; // 两处要把同一个值改成不同的数
		}
		return writes;
	}

	private static List<Write> SatisfyCompare(StatCompareItem c)
	{
		var g1 = c.GetFieldValue<StatGroupVariable>("_value1");
		var g2 = c.GetFieldValue<StatGroupVariable>("_value2");
		var op = c.GetFieldValue<StatCompareType>("_compareOp");
		int? a = Eval(g1), b = Eval(g2);
		if (a == null || b == null) return null;
		if (GameStatUtils.StatCompare(a.Value, b.Value, op)) return new List<Write>();
		if (IsConstant(g2)) return Toward(g1, a.Value, Target(b.Value, op));
		if (IsConstant(g1)) return Toward(g2, b.Value, Target(a.Value, Mirror(op)));
		return null;
	}

	/// <summary>
	/// 让一组条件不成立要写的值：「且」破一项就够（挑最好改的），「或」每一项都得破。做不到返回 null。
	/// 「走另一支」就是把它前面那些支按这个改到不满足——判定是从第一支起取第一个满足的。
	/// </summary>
	internal static List<Write> Break(ConditionResultItem item)
	{
		var parts = item.GetFieldValue<List<StatCompareItem>>("_items").Select(BreakCompare).ToList();
		if (parts.Count == 0) return null;
		if (item.GetFieldValue<LogicOperatorType>("_logicOp") == LogicOperatorType.OR)
		{
			// 「或」只要还有一项成立整组就成立，所以每一项都得破
			if (parts.Any(p => p == null)) return null;
			var writes = new List<Write>();
			foreach (Write w in parts.SelectMany(p => p))
			{
				Write same = writes.Find(x => x.Target == w.Target);
				if (same == null) writes.Add(w);
				else if (same.Value != w.Value) return null;
			}
			return writes;
		}
		// 「且」破一项就整组不成立：挑要写最少的那个
		return parts.Where(p => p != null).OrderBy(p => p.Count).FirstOrDefault();
	}

	private static List<Write> BreakCompare(StatCompareItem c)
	{
		var g1 = c.GetFieldValue<StatGroupVariable>("_value1");
		var g2 = c.GetFieldValue<StatGroupVariable>("_value2");
		var op = c.GetFieldValue<StatCompareType>("_compareOp");
		int? a = Eval(g1), b = Eval(g2);
		if (a == null || b == null) return null;
		if (!GameStatUtils.StatCompare(a.Value, b.Value, op)) return new List<Write>(); // 本来就不满足
		if (IsConstant(g2)) return Toward(g1, a.Value, BreakTarget(b.Value, op));
		if (IsConstant(g1)) return Toward(g2, b.Value, BreakTarget(a.Value, Mirror(op)));
		return null;
	}

	/// <summary>要把比较式改到不成立，左边那个数该取什么（刚好越界的一个）。</summary>
	private static int? BreakTarget(int bound, StatCompareType op)
	{
		switch (op)
		{
			case StatCompareType.Equal: return bound == int.MaxValue ? bound - 1 : bound + 1;
			case StatCompareType.NotEqual: return bound;
			case StatCompareType.Less: return bound;             // a ≥ b 就不成立
			case StatCompareType.LessEqual: return bound + 1;    // a > b
			case StatCompareType.Greater: return bound;          // a ≤ b
			case StatCompareType.GreaterEqual: return bound - 1; // a < b
			default: return null;
		}
	}

	/// <summary>把一组值（单个值或几个值相加）改到 target：相加时差额放在其中一个上，往上加优先放在还是 0 的那个，往下减放在最大的。</summary>
	private static List<Write> Toward(StatGroupVariable group, int current, int? target)
	{
		if (target == null) return null;
		var refs = group.GetFieldValue<List<StatValueReference>>("_value");
		if (refs == null || refs.Count == 0) return null;
		if (refs.Count > 1 && group.GetFieldValue<StatGroupType>("_groupType") != StatGroupType.Sum) return null;
		var settable = refs.Where(Settable).ToList();
		if (settable.Count == 0) return null;
		long gap = (long)target.Value - current;
		StatValueReference part = refs.Count == 1 ? settable[0]
			: gap > 0 ? settable.FirstOrDefault(r => Eval(r) == 0) ?? settable[0]
			: settable.OrderByDescending(r => Eval(r) ?? 0).First();
		long value = (Eval(part) ?? 0) + gap;
		if (value > int.MaxValue || value < int.MinValue) return null;
		return new List<Write> { new Write { Target = TargetOf(part), Value = (int)value } };
	}

	private static int? Target(int bound, StatCompareType op)
	{
		switch (op)
		{
			case StatCompareType.Equal: return bound;
			case StatCompareType.NotEqual: return bound == 0 ? 1 : 0;
			case StatCompareType.Less: return bound - 1;
			case StatCompareType.LessEqual: return bound;
			case StatCompareType.Greater: return bound + 1;
			case StatCompareType.GreaterEqual: return bound;
			default: return null;
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

	private static bool IsConstant(StatGroupVariable group)
	{
		var refs = group != null ? group.GetFieldValue<List<StatValueReference>>("_value") : null;
		return refs != null && refs.Count > 0 && refs.All(r => r.CheckType == StatCheckType.常數);
	}

	private static bool Settable(StatValueReference r)
	{
		if (r.GetFieldValue<bool>("_negative")) return false;
		return (r.CheckType == StatCheckType.角色數值 || r.CheckType == StatCheckType.好感度 || r.CheckType == StatCheckType.旗標) && TargetOf(r) != null;
	}

	/// <summary>
	/// 条件里用到的旗标，按旗标本身的名字（剧本 SetFlag 用的编号）。条件表里的变量另有名字，不一定和旗标一样
	/// （「TalkOption_03」指的是旗标 Talk_Option_03），要按变量实际指向的旗标取。
	/// </summary>
	public static HashSet<string> Flags(ConditionResultData data)
	{
		var flags = new HashSet<string>();
		foreach (ConditionResultItem item in data.GetFieldValue<List<ConditionResultItem>>("_items") ?? new List<ConditionResultItem>())
		{
			foreach (StatCompareItem c in item.GetFieldValue<List<StatCompareItem>>("_items") ?? new List<StatCompareItem>())
			{
				CollectFlags(c.GetFieldValue<StatGroupVariable>("_value1"), flags);
				CollectFlags(c.GetFieldValue<StatGroupVariable>("_value2"), flags);
			}
		}
		return flags;
	}

	private static void CollectFlags(StatGroupVariable group, HashSet<string> flags)
	{
		var refs = group != null ? group.GetFieldValue<List<StatValueReference>>("_value") : null;
		foreach (StatValueReference r in refs ?? new List<StatValueReference>())
		{
			if (r.CheckType == StatCheckType.旗標 && r.GetFieldValue<FlagData>("_flag") is FlagData flag && flag != null)
			{
				flags.Add(flag.name);
			}
			else if (r.CheckType == StatCheckType.數值群組)
			{
				CollectFlags(r.GetFieldValue<StatGroupVariable>("_statGroup"), flags);
			}
		}
	}

	private static object TargetOf(StatValueReference r)
	{
		switch (r.CheckType)
		{
			case StatCheckType.角色數值: return r.GetFieldValue<GameStat>("_stat");
			case StatCheckType.好感度: return r.GetFieldValue<RelationshipStat>("_relationship");
			case StatCheckType.旗標: return r.GetFieldValue<FlagData>("_flag");
			default: return null;
		}
	}

	private static int? Eval(StatGroupVariable group)
	{
		if (group == null) return null;
		var refs = group.GetFieldValue<List<StatValueReference>>("_value");
		if (refs == null || refs.Count == 0) return null;
		var values = new List<int>();
		foreach (StatValueReference r in refs)
		{
			int? v = Eval(r);
			if (v == null) return null;
			values.Add(v.Value);
		}
		switch (group.GetFieldValue<StatGroupType>("_groupType"))
		{
			case StatGroupType.Sum: return values.Sum();
			case StatGroupType.Max: return values.Max();
			case StatGroupType.Min: return values.Min();
			case StatGroupType.Multiply: return values.Aggregate(1, (x, y) => x * y);
			default: return null;
		}
	}

	private static int? Eval(StatValueReference r)
	{
		switch (r.CheckType)
		{
			// 随机值、嵌套条件读一次就有副作用，不算
			case StatCheckType.隨機值:
			case StatCheckType.條件設定:
				return null;
			case StatCheckType.數值群組:
				return Eval(r.GetFieldValue<StatGroupVariable>("_statGroup"));
			default:
				try { return r.GetValue(); }
				catch { return null; }
		}
	}

	/// <summary>写入一个值，写不进（超出范围）返回 false。属性按最终值算，扣掉加成写到基础值。</summary>
	public static bool Apply(Write w)
	{
		switch (w.Target)
		{
			case GameStat stat:
			{
				int baseValue = w.Value - stat.AdditionValue;
				if (baseValue < stat.Min || baseValue > stat.Max) return false;
				stat.SetValue(baseValue);
				return stat.FinalValue == w.Value;
			}
			case RelationshipStat relation:
			{
				int max = relation.GetFieldValue<int>("_max");
				if (w.Value < 0 || (max > 0 && w.Value > max)) return false;
				relation.SetValue(w.Value);
				return true;
			}
			case FlagData flag:
				flag.State = w.Value;
				return true;
			default:
				return false;
		}
	}
}

/// <summary>被锁住的选项旁加「一键设置」：把没满足的解锁条件改到满足，选项当场解锁。</summary>
internal static class OneClickUnlockPatch
{
	private const string ButtonName = "LOMAssistant_Unlock";

	[HarmonyPostfix]
	[HarmonyPatch(typeof(NormalOptionButton), "UpdateContent")]
	public static void UpdateContent_Postfix(NormalOptionButton __instance)
	{
		try
		{
			Text[] lines = __instance.GetFieldValue<Text[]>("_text");
			Text line = lines != null ? lines.FirstOrDefault(t => t != null && t.isActiveAndEnabled) ?? lines.FirstOrDefault(t => t != null) : null;
			if (line == null) return;
			Transform old = line.transform.Find(ButtonName);
			Button button = __instance.GetComponent<Button>();
			bool force = ConfigManager.ForceUnmetOptions.Value;
			if (!(ConfigManager.EnableOneClickUnlock.Value || force) || button == null || button.interactable)
			{
				if (old != null) old.gameObject.SetActive(false);
				return;
			}
			string key = __instance.GetFieldValue<Text>("_source").text.TrimStart('~');
			if (!LuaParser.Instance.LockedOptions.TryGetValue(key, out var locked) || string.IsNullOrEmpty(locked.Unlocked)) return;
			ConditionResultData data = ConditionSolver.Find(locked.Condition);
			List<ConditionSolver.Write> writes = data != null ? ConditionSolver.Satisfy(data) : null;
			// 条件已经满足（一项都不用改）但选项还锁着也要给按钮：读随时存档时游戏在快进回放中途决定锁不锁，
			// 那一刻的数值和停下时不同，会出现「当前已满足、选项却锁着」，点了直接解开。
			// 条件改不到（随机、时间、物品）时，设置里打开了「强行选条件没满足的选项」就给「强行解锁」：不改数值直接解开
			bool solvable = writes != null && ConfigManager.EnableOneClickUnlock.Value;
			if (!solvable && !force)
			{
				if (old != null) old.gameObject.SetActive(false);
				return;
			}
			Button unlock = old != null ? old.GetComponent<Button>() : Create(line);
			unlock.GetComponentInChildren<Text>().text = TextConverter.ForDisplay(solvable ? "一键设置" : "强行解锁");
			unlock.onClick.RemoveAllListeners();
			unlock.onClick.AddListener(() => Unlock(__instance, button, locked.Condition, locked.Unlocked, !solvable));
			Place(unlock, line);
			unlock.gameObject.SetActive(true);
		}
		catch (System.Exception ex)
		{
			FileLogger.LogWarning("[一键设置] 加按钮失败: " + ex.Message);
		}
	}

	private static void Unlock(NormalOptionButton row, Button button, string condition, string unlockedKey, bool force)
	{
		try
		{
			if (force)
			{
				FileLogger.Log($"[强行解锁] {condition}：不改数值，直接解开 {unlockedKey}");
				row.GetFieldValue<Text>("_source").text = unlockedKey;
				button.interactable = true;
				row.UpdateContent();
				return;
			}
			ConditionResultData data = ConditionSolver.Find(condition);
			List<ConditionSolver.Write> writes = data != null ? ConditionSolver.Satisfy(data) : null;
			if (writes == null)
			{
				FileLogger.LogWarning("[一键设置] " + condition + " 无法满足");
				return;
			}
			foreach (ConditionSolver.Write w in writes)
			{
				if (!ConditionSolver.Apply(w))
				{
					FileLogger.LogWarning($"[一键设置] {condition}：{w.Target} 改不到 {w.Value}");
					return;
				}
			}
			if (ConditionSolver.Holds(data) != true)
			{
				FileLogger.LogWarning("[一键设置] " + condition + " 改完仍不满足");
				return;
			}
			FileLogger.Log($"[一键设置] {condition}：" + string.Join("，", writes.Select(w => w.Target + "=" + w.Value).ToArray()));
			// 游戏给锁住的选项也挂了同样的回调，换成解锁后的文字、打开按钮，选它就走原本的分支
			row.GetFieldValue<Text>("_source").text = unlockedKey;
			button.interactable = true;
			row.UpdateContent();
		}
		catch (System.Exception ex)
		{
			FileLogger.LogWarning("[一键设置] 解锁失败: " + ex.Message);
		}
	}

	private static Button Create(Text line)
	{
		var root = new GameObject(ButtonName, typeof(RectTransform), typeof(Image), typeof(Button));
		root.transform.SetParent(line.transform, false);
		// 和浮在游戏画面上的提示框一样：墨色底、褐色细边、宣纸色字，悬停时底色亮一些
		var image = root.GetComponent<Image>();
		image.color = Color.white;
		image.type = Image.Type.Sliced;
		var button = root.GetComponent<Button>();
		button.targetGraphic = image;
		button.navigation = new Navigation { mode = Navigation.Mode.None };
		button.transition = Selectable.Transition.SpriteSwap;
		var labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
		labelObject.transform.SetParent(root.transform, false);
		Text label = labelObject.GetComponent<Text>();
		label.alignment = TextAnchor.MiddleCenter;
		label.color = PanelTheme.ToastText;
		label.raycastTarget = false;
		label.font = line.font;
		label.text = TextConverter.ForDisplay("一键设置");
		var rect = (RectTransform)labelObject.transform;
		rect.anchorMin = Vector2.zero;
		rect.anchorMax = Vector2.one;
		rect.offsetMin = rect.offsetMax = Vector2.zero;
		return button;
	}

	/// <summary>放在选项文字第一行的右边。</summary>
	private static void Place(Button button, Text line)
	{
		// 底图每次放的时候取：设置里改了提示框不透明度也跟着变
		((Image)button.targetGraphic).sprite = PanelTheme.ToastSprite(false);
		button.spriteState = new SpriteState { highlightedSprite = PanelTheme.ToastSprite(true), pressedSprite = PanelTheme.ToastSprite(true), selectedSprite = PanelTheme.ToastSprite(true) };
		Text label = button.GetComponentInChildren<Text>();
		label.fontSize = Mathf.Max(10, Mathf.RoundToInt(line.fontSize * 0.6f));
		Rect rect = line.rectTransform.rect;
		string firstLine = line.text.Split('\n')[0];
		float visible = line.cachedTextGeneratorForLayout.GetPreferredWidth(firstLine, line.GetGenerationSettings(Vector2.zero)) / line.pixelsPerUnit;
		float rowHeight = Mathf.Min(rect.height, line.fontSize * 1.3f);
		float right;
		switch (line.alignment)
		{
			case TextAnchor.UpperRight: case TextAnchor.MiddleRight: case TextAnchor.LowerRight: right = rect.xMax; break;
			case TextAnchor.UpperCenter: case TextAnchor.MiddleCenter: case TextAnchor.LowerCenter: right = rect.center.x + visible / 2f; break;
			default: right = rect.xMin + visible; break;
		}
		float middle;
		switch (line.alignment)
		{
			case TextAnchor.UpperLeft: case TextAnchor.UpperCenter: case TextAnchor.UpperRight: middle = rect.yMax - rowHeight / 2f; break;
			case TextAnchor.LowerLeft: case TextAnchor.LowerCenter: case TextAnchor.LowerRight: middle = rect.yMin + rowHeight / 2f; break;
			default: middle = rect.center.y; break;
		}
		var own = (RectTransform)button.transform;
		own.anchorMin = own.anchorMax = new Vector2(0f, 0.5f);
		own.pivot = new Vector2(0f, 0.5f);
		float height = line.fontSize * 0.6f * 1.6875f;
		own.sizeDelta = new Vector2(label.preferredWidth + height * 0.8f, height);
		own.anchoredPosition = new Vector2(right - rect.xMin + height * 0.4f, middle - rect.center.y);
	}
}
