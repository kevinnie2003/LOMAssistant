using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Fungus;
using HarmonyLib;
using MoonSharp.Interpreter;
using LOMAssistant.Config;
using LOMAssistant.LuaParse;

namespace LOMAssistant;

/// <summary>
/// 条件不满足就整项不出现的选项（剧本里写成「条件 and "选项" or "~x"」）：设置里打开「显示未满足条件的选项」时放回来。
/// - 对话选项（"O_…"）：换成锁住的写法（"~O_…"），和游戏本来就锁住的选项一样写出条件，旁边是「一键设置」或「强行解锁」。
/// - 议题、闲聊这类菜单（"类型+O_…+行动+贡献"）：游戏不显示锁住的项，就直接放出来能选，后果说明前面红字写「条件没满足」，条件在悬停提示里。
/// 选了会改掉自己条件里旗标的（问过就不再出现的话题）不放，免得菜单里堆一堆用过的选项。
/// 放不放要看当时的旗标，所以剧本里只改成「条件 and 选项 or (lomhidden(编号, 条件) and 放回的写法 or "~x")」，
/// 剧本改出来的样子固定；lomhidden 是随时存档会记下结果的查询（见 AnytimeSave.Queries），回退、读随时存档回放时用记下的结果，菜单和当时一样。
/// </summary>
internal static class HiddenOptions
{
	private static readonly Regex Gated = new Regex(@"\b(\w+)\s+and\s+""([^""]*)""\s+or\s+""~x""");
	private static readonly Regex ConditionVar = new Regex(@"local\s+(\w+)\s*=\s*checkpointmanager\.Condition\(\s*""([A-Za-z0-9_]+)""\s*\)");
	private static readonly Regex OptionKey = new Regex(@"(?<![\w])O_\w+");
	private static readonly Regex SetFlag = new Regex(@"statmodifymanager\.SetFlag\(\s*""([A-Za-z0-9_]+)""\s*,\s*(-?\d+)\s*\)");

	/// <summary>这一段剧本里直接放出来的议题类选项：选项编号 → 条件检查点名。每次载入带选项菜单的剧本时重建。</summary>
	private static readonly Dictionary<string, string> Forced = new Dictionary<string, string>();

	// 最近一次改过的剧本：改过的内容 → 原来的内容。随时存档核对剧本版本要用原来的（改法随开关、旗标变，不能算进版本）
	private static string _changedFrom, _changedTo;

	/// <summary>剧本被这里改过时返回改之前的内容，没改过原样返回。</summary>
	public static string Original(string code) => _changedTo != null && ReferenceEquals(code, _changedTo) ? _changedFrom : code;

	/// <summary>这一段剧本里有直接放出来的选项（悬停提示要看着它们）。</summary>
	public static bool Any => Forced.Count > 0;

	/// <summary>
	/// 直接放出来、现在条件确实没满足的选项。读剧本时只是把所有带条件的都改成能出现，条件成不成立要到这时候才看：
	/// 成立的就是本来就有的选项，不标。
	/// </summary>
	public static bool Unmet(string key) => key != null && Forced.TryGetValue(key, out string condition) && DataHelper.PeekCondition(condition, new string[0]) == false;

	/// <summary>选项上标的红字。</summary>
	public static string Mark => "<color=" + OptionInfoPatch.DeltaDownDark + ">条件没满足</color>";

	/// <summary>悬停提示里写的条件（带现在的值）；不是条件没满足的选项返回空串。</summary>
	public static string Note(string key)
	{
		if (!Unmet(key))
		{
			return "";
		}
		string condition = Forced[key];
		string text = RichText.Strip(DataHelper.ParseConditionCheckpoint(condition));
		return RichText.Red("条件没满足") + (string.IsNullOrWhiteSpace(text) ? "" : "：" + text);
	}

	// 在选项结果、锁住选项解析之后（要用解析出的选项结果判断是不是用过的话题），随机事件自选包装之前
	[HarmonyPrefix]
	[HarmonyPriority(Priority.Normal - 1)]
	[HarmonyPatch(typeof(LuaEnvironment), "LoadLuaFunction")]
	public static void LoadLuaFunction_Prefix(LuaEnvironment __instance, ref string luaString, string friendlyName)
	{
		try
		{
			// 剧本跑到菜单时问：这一项放不放回来（打开了设置、而且不是已经选过的）
			__instance.Interpreter.Globals["lomhidden"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(
				a.Count > 1 && ConfigManager.ForceUnmetOptions.Value && !UsedUp(a[0].String, a[1].String)));
			// 对话里零碎执行的小段 Lua 不含选项菜单，不能把当前剧本的记录清掉（同锁住选项）
			if (friendlyName == null || friendlyName.StartsWith("tmp.") || luaString == null || !luaString.Contains("setmenudialog"))
			{
				return;
			}
			Forced.Clear();
			// 开着随时存档时按这段执行记录开头定下的改写（读档回放按存档），不看现在的设置：改写多出来的查询要和执行记录一一对上
			var anytime = LOMAssistant.Features.AnytimeSave.AnytimeSave.Instance;
			bool on = anytime != null ? anytime.HiddenOptionsFor(__instance) : ConfigManager.ForceUnmetOptions.Value;
			if (!on || !luaString.Contains("\"~x\""))
			{
				return;
			}
			var conditions = new Dictionary<string, string>();
			foreach (Match m in ConditionVar.Matches(luaString))
			{
				conditions[m.Groups[1].Value] = m.Groups[2].Value;
			}
			var shown = new List<string>();
			var locked = new Dictionary<string, string>();
			string result = Gated.Replace(luaString, m =>
			{
				string variable = m.Groups[1].Value, value = m.Groups[2].Value;
				Match key = OptionKey.Match(value);
				if (!key.Success || !conditions.TryGetValue(variable, out string condition))
				{
					return m.Value;
				}
				shown.Add(key.Value);
				string ask = "lomhidden(\"" + key.Value + "\", \"" + condition + "\")";
				if (value.Contains("+"))
				{
					Forced[key.Value] = condition;
					return variable + " and \"" + value + "\" or (" + ask + " and \"" + value + "\" or \"~x\")";
				}
				locked[key.Value] = condition;
				return variable + " and \"" + value + "\" or (" + ask + " and \"~" + key.Value + "\" or \"~x\")";
			});
			if (shown.Count == 0)
			{
				return;
			}
			_changedFrom = luaString;
			_changedTo = result;
			luaString = result;
			// 换成锁住写法的对话选项要让「一键设置」「强行解锁」认得
			foreach (var pair in locked)
			{
				LuaParser.Instance.LockedOptions[pair.Key] = (pair.Value, pair.Key);
			}
			FileLogger.Log("[强行选项] " + friendlyName + " 有 " + shown.Count + " 个条件没满足就不出现的选项，到菜单时看要不要放回：" + string.Join("，", shown.ToArray()));
		}
		catch (System.Exception ex)
		{
			FileLogger.LogWarning("[强行选项] 放回条件没满足的选项失败，按原样: " + ex.Message);
		}
	}

	// 已经选过的：这个选项会设的旗标（SetFlag("编号", 值)）出现在它自己的条件里，而且现在已经是设过的那个值。
	// 问过一次就不再出现的话题、做过一次的事都是这样；还没选过、只是别的条件（好感、时间）不够的照样放回来
	private static bool UsedUp(string key, string condition)
	{
		if (!LuaParser.Instance.OptionResult.TryGetValue(key, out List<Action> actions) || actions == null)
		{
			return false;
		}
		// 按条件实际指向的旗标比（条件表里的变量名不一定和旗标一样）；条件表拿不到时退回按条件文字里的「(编号)」比
		Mortal.Core.ConditionResultData data = ConditionSolver.Find(condition);
		HashSet<string> flags = data != null ? ConditionSolver.Flags(data) : null;
		string text = flags == null ? DataHelper.ParseConditionCheckpoint(condition) ?? "" : "";
		foreach (Action action in actions)
		{
			Match set = SetFlag.Match(action.Content ?? "");
			if (!set.Success)
			{
				continue;
			}
			string id = set.Groups[1].Value;
			if (flags != null ? !flags.Contains(id) : !text.Contains("(" + id + ")"))
			{
				continue;
			}
			int? now = Mortal.Core.MissionManagerData.Instance?.GetFlag(id)?.State;
			if (now != null && now.Value == int.Parse(set.Groups[2].Value))
			{
				return true;
			}
		}
		return false;
	}
}
