using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LOMAssistant.LuaParse;

public class LuaParser
{
	private static readonly Lazy<LuaParser> instance = new Lazy<LuaParser>(() => new LuaParser());

	private static readonly Regex indexMarkerRegex = new Regex("^\\s*--index:(O(_\\w+)+)", RegexOptions.Multiline);

	private static readonly Regex endBlockRegex = new Regex("\\b(elseif|end)");

	private static readonly Regex switchRegex = new Regex("checkpointmanager\\.Switch\\(([^)]*)\\)");

	// 「local switch_001_result = checkpointmanager.Switch("S_Mental_GE_65")」和「if switch_001_result == 1 then」
	private static readonly Regex switchLocalRegex = new Regex("^local (\\w+) = checkpointmanager\\.Switch\\(\"([^\"]+)\"\\)");

	private static readonly Regex switchArmRegex = new Regex("^(?:if|elseif) (\\w+) == (\\d+) then");

	private static readonly Regex positionRegex = new Regex("checkpointmanager\\.Position\\(([^)]*)\\)");

	private static readonly Regex modifyRegex = new Regex("\\s*statmodifymanager\\.(?!GetDisplayTime)\\w+\\(.*?\\)");

	private static readonly Regex battleScenesRegex = new Regex("luamanager\\.ChangeScene\\(\"(Combat|Battle)\"");

	private static readonly Regex scriptActionsRegex = new Regex("luamanager\\.SetNextScript");

	public Dictionary<string, List<Action>> OptionResult;

	// 被锁的选项：脚本里写成「条件 and "正常选项" or "~锁住时显示的文字"」
	private static readonly Regex lockedOptionRegex = new Regex("\\b(\\w+)\\s+and\\s+\"([^\"]*)\"\\s+or\\s+\"~(O_\\w+)\"");

	private static readonly Regex optionKeyRegex = new Regex("O_\\w+");

	/// <summary>锁住时显示的文字的 key → (解锁条件名, 解锁后的选项 key)。每次载入剧本时重建。</summary>
	public readonly Dictionary<string, (string Condition, string Unlocked)> LockedOptions = new Dictionary<string, (string, string)>();

	/// <summary>选项 key → 这个选项里「满足某个分支判断才跳到另一段剧情」的位置（分支判断名, 第几支，0 表示 else）。</summary>
	public readonly Dictionary<string, List<(string Switch, int Branch)>> OptionJumps = new Dictionary<string, List<(string, int)>>();

	private static readonly Regex switchAssignRegex = new Regex("^local\\s+(\\w+)\\s*=\\s*checkpointmanager\\.Switch\\(\"([^\"]+)\"\\)");

	private static readonly Regex branchRegex = new Regex("^(if|elseif)\\s+(\\w+)\\s*==\\s*(\\d+)\\s+then");

	// 逐行跟踪 if/elseif/else/end，找出写在某个分支判断里的「跳到下一段剧本」
	private static List<(string, int)> FindConditionalJumps(string block)
	{
		var result = new List<(string, int)>();
		var switches = new Dictionary<string, string>();
		var stack = new List<(string Var, int Branch)>(); // Var 为 null 表示不是分支判断的块（普通 if / for 等）
		foreach (string raw in block.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None))
		{
			string line = raw.Trim();
			if (line.StartsWith("--"))
			{
				continue;
			}
			Match assign = switchAssignRegex.Match(line);
			if (assign.Success)
			{
				switches[assign.Groups[1].Value] = assign.Groups[2].Value;
				continue;
			}
			Match branch = branchRegex.Match(line);
			if (branch.Success && switches.ContainsKey(branch.Groups[2].Value))
			{
				int n = int.Parse(branch.Groups[3].Value);
				if (branch.Groups[1].Value == "if")
				{
					stack.Add((branch.Groups[2].Value, n));
				}
				else if (stack.Count > 0)
				{
					stack[stack.Count - 1] = (stack[stack.Count - 1].Var, n);
				}
				continue;
			}
			if (line.StartsWith("if ") || line.StartsWith("for ") || line.StartsWith("while ") || line.Contains("function("))
			{
				stack.Add((null, 0));
			}
			else if (line == "else" && stack.Count > 0 && stack[stack.Count - 1].Var != null)
			{
				stack[stack.Count - 1] = (stack[stack.Count - 1].Var, 0);
			}
			else if (line == "end" || line.StartsWith("end)") || line.StartsWith("end "))
			{
				if (stack.Count > 0)
				{
					stack.RemoveAt(stack.Count - 1);
				}
			}
			else if (line.Contains("luamanager.SetNextScript"))
			{
				for (int i = stack.Count - 1; i >= 0; i--)
				{
					if (stack[i].Var != null)
					{
						var hit = (switches[stack[i].Var], stack[i].Branch);
						if (!result.Contains(hit))
						{
							result.Add(hit);
						}
						break;
					}
				}
			}
		}
		return result;
	}

	private readonly Dictionary<string, Dictionary<string, (string Condition, string Unlocked)>> lockedByScript = new Dictionary<string, Dictionary<string, (string, string)>>();

	/// <param name="key">剧本内容的缓存键（同一份内容只解析一次）。</param>
	public void ExtractLockedOptions(string key, string luaScript)
	{
		// 对话里也会零碎执行一些小段 Lua，那些不含选项菜单，不能把当前剧本的记录清掉
		if (luaScript == null || !luaScript.Contains("setmenudialog"))
		{
			return;
		}
		if (!lockedByScript.TryGetValue(key, out var locked))
		{
			locked = new Dictionary<string, (string, string)>();
			// 没有「~O_」锁住选项就不用跑正则
			if (luaScript.Contains("\"~O_"))
			{
				foreach (Match m in lockedOptionRegex.Matches(luaScript))
				{
					Match unlocked = optionKeyRegex.Match(m.Groups[2].Value);
					locked[m.Groups[3].Value] = (m.Groups[1].Value, unlocked.Success ? unlocked.Value : null);
				}
			}
			lockedByScript[key] = locked;
		}
		LockedOptions.Clear();
		foreach (var pair in locked)
		{
			LockedOptions[pair.Key] = pair.Value;
		}
	}

	private Dictionary<string, List<Action>> luaModification;

	private Dictionary<string, List<string>> luaOptions;

	private Dictionary<string, List<string>> luaSwitchCheck;

	private Dictionary<string, List<string>> luaPositionWork;

	public static LuaParser Instance => instance.Value;

	private LuaParser()
	{
		OptionResult = new Dictionary<string, List<Action>>();
		luaModification = new Dictionary<string, List<Action>>();
		luaOptions = new Dictionary<string, List<string>>();
		luaSwitchCheck = new Dictionary<string, List<string>>();
		luaPositionWork = new Dictionary<string, List<string>>();
	}

	public void ClearCache()
	{
		OptionResult.Clear();
		OptionJumps.Clear();
		luaOptions.Clear();
		luaModification.Clear();
		luaSwitchCheck.Clear();
		luaPositionWork.Clear();
	}

	private readonly Dictionary<string, (Dictionary<string, List<Action>> Results, Dictionary<string, List<(string, int)>> Jumps)> optionsByScript
		= new Dictionary<string, (Dictionary<string, List<Action>>, Dictionary<string, List<(string, int)>>)>();

	/// <summary>
	/// 剧本里每个选项（--index:O_xxx 标记）之后的数值变化和条件跳转。按剧本内容缓存；每次载入都把这个剧本的结果写回
	/// OptionResult / OptionJumps，同一个选项 key 出现在别的剧本里时，以当前剧本为准。
	/// </summary>
	/// <param name="key">剧本内容的缓存键。</param>
	public Dictionary<string, List<Action>> ExtractOptionActionsFromLua(string key, string luaScript = "")
	{
		if (!optionsByScript.TryGetValue(key, out var parsed))
		{
			var results = new Dictionary<string, List<Action>>();
			var jumps = new Dictionary<string, List<(string, int)>>();
			MatchCollection matchCollection = indexMarkerRegex.Matches(luaScript);
			// 整个剧本只拆一次行，记下每行的起点，按选项标记的位置找行号（章节剧本几千行、上百个选项，每个选项都重拆会卡）
			string[] scriptLines = luaScript.Split('\n');
			var lineStarts = new int[scriptLines.Length];
			for (int n = 1; n < scriptLines.Length; n++)
			{
				lineStarts[n] = lineStarts[n - 1] + scriptLines[n - 1].Length + 1;
			}
			for (int i = 0; i < matchCollection.Count; i++)
			{
				string option = matchCollection[i].Groups[1].Value;
				// 同一个剧本里同一个选项出现多次时，以第一次为准（和原来一样）
				if (results.ContainsKey(option))
				{
					continue;
				}
				int markerLine = Array.BinarySearch(lineStarts, matchCollection[i].Index);
				string blockContent = BranchBlock(scriptLines, markerLine < 0 ? ~markerLine - 1 : markerLine);
				if (blockContent == null)
				{
					int start = matchCollection[i].Index + matchCollection[i].Length;
					int end = luaScript.Length;
					if (i + 1 < matchCollection.Count)
					{
						end = Math.Min(end, matchCollection[i + 1].Index);
					}
					else
					{
						Match match = endBlockRegex.Match(luaScript, start);
						if (match.Success)
						{
							end = match.Index;
						}
					}
					blockContent = luaScript.Substring(start, end - start);
				}
				List<Action> list = new List<Action>();
				AddActionsToList(blockContent, modifyRegex, ActionType.ModifyAction, list);
				AddActionsToList(blockContent, battleScenesRegex, ActionType.BattleScene, list);
				results[option] = list;
				jumps[option] = FindConditionalJumps(blockContent);
			}
			parsed = (results, jumps);
			optionsByScript[key] = parsed;
		}
		foreach (var pair in parsed.Results)
		{
			OptionResult[pair.Key] = pair.Value;
		}
		foreach (var pair in parsed.Jumps)
		{
			OptionJumps[pair.Key] = pair.Value;
		}
		return new Dictionary<string, List<Action>>(parsed.Results);
	}

	private static readonly Regex choiceBranchRegex = new Regex("^(if|elseif)\\s+choice_\\w+\\s*==\\s*\\d+\\s+then$");

	/// <summary>
	/// 选项标记（--index:O_xxx）所在的那一支：从它前面的「if/elseif choice_… == n then」起，到同一层的下一个 elseif、else 或 end，
	/// 或这一支里的下一个菜单（setmenudialog，之后的变化要看那个菜单怎么选）为止。
	/// 以前按「到下一个选项标记为止」截，一组里最后一个选项会越过这一支的结尾，把菜单结束后人人都有的变化也算进去。
	/// 前面不是选项的 if 时返回 null，照原来的截法。
	/// </summary>
	private static string BranchBlock(string[] lines, int markerLine)
	{
		int head = markerLine - 1;
		while (head >= 0 && (lines[head].Trim().Length == 0 || lines[head].Trim().StartsWith("--")))
		{
			head--;
		}
		if (head < 0 || !choiceBranchRegex.IsMatch(lines[head].Trim()))
		{
			return null;
		}
		var body = new List<string>();
		int depth = 0;
		for (int i = head + 1; i < lines.Length; i++)
		{
			string line = lines[i].Trim();
			bool opens = (line.StartsWith("if ") && line.EndsWith("then")) || line.StartsWith("for ") || line.StartsWith("while ") || line.Contains("function(");
			bool sibling = line.StartsWith("elseif ") || line == "else";
			bool closes = line == "end" || line.StartsWith("end ") || line.StartsWith("end)");
			if ((depth == 0 && (sibling || closes)) || line.Contains("setmenudialog("))
			{
				break;
			}
			if (opens)
			{
				depth++;
			}
			else if (closes)
			{
				depth--;
			}
			body.Add(lines[i]);
		}
		return string.Join("\n", body.ToArray());
	}

	private void AddActionsToList(string blockContent, Regex regex, ActionType actionType, List<Action> actionList)
	{
		string[] array = blockContent.Split(new string[3] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
		var scope = new BranchScope();
		foreach (string text in array)
		{
			scope.Step(text.Trim());
			foreach (Match item in regex.Matches(text))
			{
				actionList.Add(new Action(actionType, item.Value.Trim(), scope.Conditional, scope.Branch) { Checkpoint = scope.Checkpoint, Arm = scope.Arm });
			}
		}
	}

	/// <summary>
	/// 逐行走 Lua 的 if/elseif/else/end，记下当前在哪个判断的哪一支。判断可以嵌套：内层 end 之后回到外层那一支，
	/// 外层后面的效果仍只属于这一支。从分支中间截出来的片段可能先碰到 elseif/else，这时当成新开的一支。
	/// </summary>
	private sealed class BranchScope
	{
		private sealed class Frame
		{
			public int Branch;
			public string IfCheckpoint = "", Checkpoint = "";
			public int Arm = -1;
		}

		private readonly Stack<Frame> frames = new Stack<Frame>();

		// 记下每个分支判断存进哪个变量，分支里的变化就知道是哪个检查点的第几支，说明里能写出条件
		private readonly Dictionary<string, string> switches = new Dictionary<string, string>();

		private int count;

		public bool Conditional => frames.Count > 0;

		public int Branch => frames.Count > 0 ? frames.Peek().Branch : 0;

		public string Checkpoint => frames.Count > 0 ? frames.Peek().Checkpoint : "";

		public int Arm => frames.Count > 0 ? frames.Peek().Arm : -1;

		public void Step(string line)
		{
			Match local = switchLocalRegex.Match(line);
			if (local.Success)
			{
				switches[local.Groups[1].Value] = local.Groups[2].Value;
			}
			if (line.StartsWith("if ") || line.StartsWith("elseif "))
			{
				if (line.StartsWith("if ") || frames.Count == 0)
				{
					frames.Push(new Frame());
				}
				Frame top = frames.Peek();
				top.Branch = ++count;
				Match m = switchArmRegex.Match(line);
				top.Checkpoint = m.Success && switches.TryGetValue(m.Groups[1].Value, out string name) ? name : "";
				top.Arm = top.Checkpoint.Length > 0 ? int.Parse(m.Groups[2].Value) : -1;
				if (line.StartsWith("if "))
				{
					top.IfCheckpoint = top.Checkpoint;
				}
			}
			else if (line == "else")
			{
				if (frames.Count == 0)
				{
					frames.Push(new Frame());
				}
				Frame top = frames.Peek();
				top.Branch = ++count;
				top.Checkpoint = top.IfCheckpoint;
				top.Arm = top.Checkpoint.Length > 0 ? 0 : -1;
			}
			else if ((line == "end" || line.StartsWith("end ")) && frames.Count > 0)
			{
				frames.Pop();
			}
		}
	}

	public List<string> ExtractSwitchCheckFromLua(string friendlyName, string luaScript = "")
	{
		if (luaSwitchCheck.ContainsKey(friendlyName))
		{
			return luaSwitchCheck[friendlyName];
		}
		List<string> list = ExtractMatches(luaScript, switchRegex);
		luaSwitchCheck[friendlyName] = list;
		return list;
	}

	public List<string> ExtractPositionWorkFromLua(string friendlyName, string luaScript = "")
	{
		if (luaPositionWork.ContainsKey(friendlyName))
		{
			return luaPositionWork[friendlyName];
		}
		List<string> list = ExtractMatches(luaScript, positionRegex);
		luaPositionWork[friendlyName] = list;
		return list;
	}

	private List<string> ExtractMatches(string luaScript, Regex regex)
	{
		List<string> list = new List<string>();
		foreach (Match item2 in regex.Matches(luaScript))
		{
			string item = item2.Groups[1].Value.Trim().Trim('"');
			if (!list.Contains(item))
			{
				list.Add(item);
			}
		}
		return list;
	}

	public List<Action> ExtractActionsFromLua(string luaScriptName)
	{
		if (luaModification.ContainsKey(luaScriptName))
		{
			return luaModification[luaScriptName];
		}
		string[] array = DataHelper.LoadLuaScript(luaScriptName).Split(new string[3] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
		List<Action> list = new List<Action>();
		var scope = new BranchScope();
		string[] array2 = array;
		for (int i = 0; i < array2.Length; i++)
		{
			string text = array2[i].Trim();
			scope.Step(text);
			foreach (Match item in modifyRegex.Matches(text))
			{
				list.Add(new Action(ActionType.ModifyAction, item.Value, scope.Conditional, scope.Branch) { Checkpoint = scope.Checkpoint, Arm = scope.Arm });
			}
			foreach (Match item2 in battleScenesRegex.Matches(text))
			{
				list.Add(new Action(ActionType.BattleScene, item2.Value, scope.Conditional, scope.Branch) { Checkpoint = scope.Checkpoint, Arm = scope.Arm });
			}
		}
		luaModification[luaScriptName] = list;
		return list;
	}

	/// <summary>
	/// 条件分支里的变化，一支一行：「若 条件：变化」。条件取自分支判断（Switch）检查点的那一支，带当前值；
	/// else 那支写「否则（不满足：…）」；认不出条件的（不是分支判断的 if）写「某些条件下」。
	/// </summary>
	public static List<string> ConditionalLines(List<Action> actions)
	{
		var lines = new List<string>();
		foreach (var branch in Effects(actions).Where(r => r.Branch != 0).GroupBy(r => r.Branch))
		{
			Action first = branch.First().First;
			string effects = string.Join("、", branch.Select(r => r.Text).ToArray());
			string when = first.Checkpoint.Length == 0 ? "" : RichText.Strip(DataHelper.SwitchBranchCondition(first.Checkpoint, first.Arm)).Trim();
			// else，或者比条件条数多一的那个结果值（条件都不满足时 Switch 返回它）：写成「否则（不满足：…）」
			bool otherwise = first.Arm == 0 || when == "以上条件都不满足";
			if (first.Checkpoint.Length > 0 && otherwise)
			{
				lines.Add(LOMAssistant.Features.Story.StoryScripts.Otherwise(first.Checkpoint, true) + "：" + effects);
			}
			else if (when.Length == 0)
			{
				lines.Add("某些条件下：" + effects);
			}
			else
			{
				lines.Add("若 " + TextConverter.ToSimplifiedUnlessJapanese(when) + "：" + effects);
			}
		}
		return lines;
	}

	/// <summary>
	/// 每一支（0 是不分支的部分）要显示的效果，按出现顺序。同一支里重复的加减都会执行，合成一条写总数；
	/// 依赖当前数值的按实际增量写，拿不到数值时写 Action.Description。其余相同的说明只留一条。
	/// </summary>
	private static List<(int Branch, Action First, string Text)> Effects(List<Action> actions)
	{
		var rows = new List<(int Branch, Action First, string Text)>();
		var totals = new Dictionary<(int, string), int>();
		var rowOf = new Dictionary<(int, string), int>();
		int Total(int branch, string key) => totals.TryGetValue((branch, key), out int total) ? total : 0;
		foreach (Action action in actions.Where(a => !string.IsNullOrEmpty(a.Description)))
		{
			int branch = action.IsConditional ? action.BranchIndex : 0;
			int? actual = null;
			if (action.Key != null)
			{
				try
				{
					actual = action.Actual(key => Total(0, key) + (branch != 0 ? Total(branch, key) : 0));
				}
				catch (Exception e)
				{
					FileLogger.LogWarning("效果预览读取当前数值失败: " + e.Message);
				}
			}
			if (actual == null)
			{
				if (!rows.Any(r => r.Branch == branch && r.Text == action.Description))
				{
					rows.Add((branch, action, action.Description));
				}
				continue;
			}
			var slot = (branch, action.Key);
			int sum = Total(branch, action.Key) + actual.Value;
			totals[slot] = sum;
			if (rowOf.TryGetValue(slot, out int row))
			{
				rows[row] = (branch, rows[row].First, action.Shown(sum));
			}
			else
			{
				rowOf[slot] = rows.Count;
				rows.Add((branch, action, action.Shown(sum)));
			}
		}
		return rows;
	}

	public static string Description(List<Action> actions) => FormatDescription(actions, text => text);

	internal static string DisplayDescription(List<Action> actions) =>
		FormatDescription(actions, text => TextConverter.ForDisplay(RichText.Strip(TextConverter.ToSimplifiedUnlessJapanese(text))));

	private static string FormatDescription(List<Action> actions, Func<string, string> format)
	{
		var rows = Effects(actions);
		List<string> list = rows.Where(r => r.Branch == 0).Select(r => format(r.Text)).ToList();
		List<string> list2 = (from r in rows
			where r.Branch != 0
			group r.Text by r.Branch into g
			select string.Join("、", g.Select(format))).ToList();
		List<string> list3 = new List<string>();
		if (list.Count > 0)
		{
			list3.Add(string.Join("、", list));
		}
		if (list2.Count == 1)
		{
			list3.Add(format("后续: ") + list2[0]);
		}
		else if (list2.Count > 1)
		{
			const string circled = "①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳";
			List<string> numbered = new List<string>();
			for (int i = 0; i < list2.Count; i++)
			{
				numbered.Add((i < circled.Length ? circled[i].ToString() : (i + 1) + ".") + list2[i]);
			}
			list3.Add(format("后续: ") + string.Join(" ", numbered));
		}
		return string.Join("；", list3);
	}
}
