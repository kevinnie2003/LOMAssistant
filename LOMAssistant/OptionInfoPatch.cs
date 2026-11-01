using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using Fungus;
using HarmonyLib;
using LOMAssistant.Config;
using LOMAssistant.LuaParse;
using Mortal.Core;
using Mortal.Free;
using Mortal.Story;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LOMAssistant;

/// <summary>
/// 记下现在演的剧本和弹出的选项菜单，并解析剧本里的选项结果、判定、锁住的选项。
/// 「此刻」页、AI 陪玩等要用，不管开没开「选项提示」都要打（只有在选项上显示结果的部分跟着开关）。
/// </summary>
internal static class OptionTracking
{
	/// <summary>取过的文字是哪个编号，加按钮时按文字查回编号（选项提示也用）。</summary>
	internal static readonly Dictionary<string, string> TextKeys = new Dictionary<string, string>();
	private static int _menuFrame = -1;

	/// <summary>掷骰转盘下面那几行：选项表每项是「选项编号|范围」，开始掷骰时记下这一组。</summary>
	[HarmonyPrefix]
	[HarmonyPatch(typeof(DiceMenuDialog), "ExecuteRoll")]
	public static void DiceMenuDialog_ExecuteRoll_Prefix(string[] __0)
	{
		if (__0 == null)
		{
			return;
		}
		OptionInfoPatch.MenuKeys.Clear();
		foreach (string entry in __0)
		{
			if (!string.IsNullOrEmpty(entry))
			{
				OptionInfoPatch.MenuKeys.Add(entry.Split('|')[0]);
			}
		}
		_menuFrame = Time.frameCount;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(CustomMenuDialog), "AddOption")]
	public static void CustomMenuDialog_AddOption_Prefix(string __0)
	{
		if (Time.frameCount != _menuFrame)
		{
			OptionInfoPatch.MenuKeys.Clear();
			_menuFrame = Time.frameCount;
		}
		if (__0 != null && TextKeys.TryGetValue(__0, out string key))
		{
			OptionInfoPatch.MenuKeys.Add(key);
			return;
		}
		// 行动菜单（休息、闲聊、事件……）、地点菜单传的是选项编号（「talk+O_xxx+0+0」这类），按钮自己取文字：编号就在里面
		string code = LOMAssistant.Features.Story.StoryScripts.OptionKey(__0, out string _);
		if (code.StartsWith("O_"))
		{
			OptionInfoPatch.MenuKeys.Add(code);
		}
	}

	/// <summary>记下每段取过的文字是哪个编号（在结果说明接好以后，和加按钮时传的文字一致）。</summary>
	[HarmonyPostfix]
	[HarmonyPriority(Priority.Last)]
	[HarmonyPatch(typeof(LeanLocalizationResolver), "GetStoryText")]
	public static void LeanLocalizationResolver_GetStoryText_Remember(string key, string __result)
	{
		if (OptionInfoPatch.Raw || __result == null)
		{
			return;
		}
		if (TextKeys.Count > 500)
		{
			TextKeys.Clear();
		}
		TextKeys[__result] = key;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(LuaEnvironment), "LoadLuaFunction")]
	public static void LuaEnvironment_LoadLuaFunction_Prefix(string luaString, string friendlyName)
	{
		var timer = System.Diagnostics.Stopwatch.StartNew();
		try
		{
			if (friendlyName == "tmp.LuaScript")
			{
				return;
			}
			// 剧本内容不会变：解析结果按内容缓存，同一个剧本再次载入（地点菜单等会反复载入）不用重新解析
			string key = friendlyName + "#" + (luaString ?? "").Length + "#" + (luaString ?? "").GetHashCode();
			long lap = 0;
			string laps = "";
			void Lap(string name)
			{
				long now = timer.ElapsedMilliseconds;
				laps += name + " " + (now - lap) + "，";
				lap = now;
			}
			// 选项结果、锁住选项：解析结果存在 LuaParser 里，显示选项文字时取用
			LuaParser.Instance.ExtractOptionActionsFromLua(key, luaString);
			LuaParser.Instance.ExtractSwitchCheckFromLua(key, luaString);
			LuaParser.Instance.ExtractPositionWorkFromLua(key, luaString);
			if (!friendlyName.StartsWith("tmp.") && !friendlyName.StartsWith("Flowchart.Say.ExecuteLua"))
			{
				OptionInfoPatch.LastScriptKey = key;
			}
			Lap("选项");
			LuaParser.Instance.ExtractLockedOptions(key, luaString);
			Lap("锁住选项");
			if (timer.ElapsedMilliseconds >= 50)
			{
				FileLogger.Log($"[性能] 解析剧本 {friendlyName}：{laps.TrimEnd('，')}（毫秒）");
			}
		}
		catch (Exception ex)
		{
			DicePlugin.LogError("LoadLuaFunction Prefix 解析失败，已跳过以保证对话加载: " + ex);
		}
		LuaLoadTiming.ParseMs += timer.ElapsedMilliseconds;
	}
}

internal static class OptionInfoPatch
{
	private static string _originalDialogKey = string.Empty;

	private static PositionController _positionCtrl = null;

	/// <summary>最近载入的剧本（解析缓存的键），剧情回顾「此刻」页用它取这一段的选项、判定和事件。</summary>
	public static string LastScriptKey;

	/// <summary>
	/// 游戏里最近一次弹出的选项菜单是哪几个选项（选项编号）。剧本的 choose 先取选项文字（GetStoryText）再加按钮（AddOption），
	/// 加按钮时传的就是取到的那段文字：取文字时记下「文字 → 编号」，加按钮时按文字查回编号（中间还会取别的文字，不能按顺序对）。
	/// 「此刻」页据此标出现在这一组。
	/// </summary>
	public static readonly List<string> MenuKeys = new List<string>();

	/// <summary>为真时取选项文字不经过下面的结果说明（本 Mod 自己取原文用）。</summary>
	public static bool Raw;

	[HarmonyPostfix]
	[HarmonyPatch(typeof(LeanLocalizationResolver), "GetStoryText")]
	public static void LeanLocalizationResolver_GetStoryText_Postfix(string key, ref string __result)
	{
		// 本 Mod 自己取选项原文（剧情回顾里显示）时不加结果说明，也不算游戏弹出的菜单；
		// 设成「鼠标悬停」「不显示」时也不接在选项后面（悬停时由 OptionHoverHint 另外显示）
		if (Raw)
		{
			return;
		}
		if (ConfigManager.OptionResultMode.Value != 0)
		{
			// 强行放出来的选项不看这个设置，总要标出条件没满足
			if (HiddenOptions.Unmet(key))
			{
				__result = __result + TextConverter.ForDisplay("\n<size=16>(" + HiddenOptions.Mark + ")</size>");
			}
			return;
		}
		// 强行放出来、条件没满足的选项：这一行只标红字，条件长、会把后果挤掉，放在悬停提示里
		string note = OptionNote(key);
		if (HiddenOptions.Unmet(key))
		{
			string rest = NoteOf(key);
			note = HiddenOptions.Mark + (rest.Length > 0 ? "；" + rest : "");
		}
		if (note.Length > 0)
		{
			__result = __result + TextConverter.ForDisplay("\n<size=16>(" + ColorDeltas(note) + ")</size>");
		}
	}

	// 「嘴力 +1」「南宫浅好感 -2」「银两 +500」：加的绿色、减的红色（条件里的「需>=40」「=1」不算）
	private static readonly Regex Delta = new Regex(@"(?<=^|[、；：:\s(（①-⑳])([^\s、；：:()（）<>=①-⑳]+?) ([+-]\d+)(?=$|[、；）)\s])");

	// 对话选项是浅色底，绿、红都要深一点才看得清；掷骰线路、悬停提示是深色底，换回浅色（见 DiceNoWrap、悬停提示）
	internal const string DeltaUpDark = "#1C560B";
	internal const string DeltaDownDark = "#8A1C12";

	/// <summary>后果说明里的加减上色：加的绿、减的红。选项按钮、掷骰线路、悬停提示都用它；light 为深色底上用的浅绿、浅红。</summary>
	internal static string ColorDeltas(string note, bool light = false) =>
		string.IsNullOrEmpty(note) || !ConfigManager.ColorOptionDeltas.Value ? note : Delta.Replace(note, m => m.Groups[2].Value.StartsWith("-")
			? light ? RichText.Red(m.Value) : "<color=" + DeltaDownDark + ">" + m.Value + "</color>"
			: light ? RichText.Green(m.Value) : "<color=" + DeltaUpDark + ">" + m.Value + "</color>");

	/// <summary>
	/// 一个选项的后果说明（不带括号、字号）：被锁的选项写真正的解锁条件和解锁后的效果；其余写选了会怎样，
	/// 满足某个分支条件就直接接一段剧情的写成「若 条件：触发剧情」。没有就返回空串。
	/// </summary>
	internal static string OptionNote(string key)
	{
		// 条件没满足、强行放出来的选项（见 HiddenOptions）：先写条件（悬停提示用）
		string forced = HiddenOptions.Note(key);
		string note = NoteOf(key);
		return forced.Length == 0 ? note : note.Length == 0 ? forced : forced + "；" + note;
	}

	private static string NoteOf(string key)
	{
		try
		{
			// 被锁的选项（「解锁条件：？？？」「银两不足」这类）：写出真正的解锁条件和当前值，以及解锁后的效果
			if (LuaParser.Instance.LockedOptions.TryGetValue(key, out var locked))
			{
				string condition = RichText.Strip(DataHelper.ParseConditionCheckpoint(locked.Condition));
				string effects = locked.Unlocked != null && LuaParser.Instance.OptionResult.TryGetValue(locked.Unlocked, out var unlockedActions)
					? RichText.Strip(TextConverter.ToSimplifiedUnlessJapanese(LuaParser.Description(unlockedActions)))
					: "";
				return condition.IsNullOrWhiteSpace() ? "" : condition + (effects.IsNullOrWhiteSpace() ? "" : "；解锁后：" + effects);
			}
			string jumps = "";
			if (LuaParser.Instance.OptionJumps.TryGetValue(key, out var jumpList))
			{
				foreach (var jump in jumpList)
				{
					string condition = RichText.Strip(DataHelper.SwitchBranchCondition(jump.Switch, jump.Branch));
					if (!condition.IsNullOrWhiteSpace())
					{
						jumps += (jumps.Length > 0 ? "；" : "") + "若 " + condition + "：触发剧情";
					}
				}
			}
			if (LuaParser.Instance.OptionResult.TryGetValue(key, out var value))
			{
				string text = TextConverter.ToSimplifiedUnlessJapanese(LuaParser.Description(value));
				if (jumps.Length > 0)
				{
					text = text.IsNullOrWhiteSpace() ? jumps : text + "；" + jumps;
				}
				return text.IsNullOrWhiteSpace() ? "" : RichText.Strip(text);
			}
		}
		catch (Exception ex)
		{
			DicePlugin.LogError("取选项后果说明失败，已跳过以保证对话显示: " + ex);
		}
		return "";
	}

	/// <summary>每个选项按钮现在是哪个选项（加选项时记下）：按钮上的文字会被游戏再加工，不能按文字查。</summary>
	internal static readonly Dictionary<Button, string> ButtonKeys = new Dictionary<Button, string>();

	[HarmonyPrefix]
	[HarmonyPatch(typeof(MenuDialog), nameof(MenuDialog.AddOption), new[] { typeof(string), typeof(bool), typeof(LuaEnvironment), typeof(MoonSharp.Interpreter.Closure) })]
	// 游戏按顺序取下一个按钮（nextOptionIndex），按钮本来可能就亮着，所以按加之前的序号找是哪个按钮
	public static void MenuDialog_AddOption_Prefix(MenuDialog __instance, out int __state)
	{
		__state = Traverse.Create(__instance).Field("nextOptionIndex").GetValue<int>();
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(MenuDialog), nameof(MenuDialog.AddOption), new[] { typeof(string), typeof(bool), typeof(LuaEnvironment), typeof(MoonSharp.Interpreter.Closure) })]
	public static void MenuDialog_AddOption_Postfix(MenuDialog __instance, string text, int __state)
	{
		Button[] buttons = __instance.CachedButtons;
		int now = Traverse.Create(__instance).Field("nextOptionIndex").GetValue<int>();
		if (buttons == null || now <= __state || __state < 0 || __state >= buttons.Length || buttons[__state] == null)
		{
			return;
		}
		string key = text != null && OptionTracking.TextKeys.TryGetValue(text, out string k) ? k : null;
		// 对话选项、地点菜单常把选项编号（「O_xxx」或「类型+O_xxx+…」）交给按钮，由按钮自己取文字：编号就在里面
		if (key == null && text != null)
		{
			string code = LOMAssistant.Features.Story.StoryScripts.OptionKey(text, out string _);
			key = code.StartsWith("O_") ? code : null;
		}
		if (key != null)
		{
			ButtonKeys[buttons[__state]] = key;
		}
		else
		{
			ButtonKeys.Remove(buttons[__state]);
		}
	}

	/// <summary>
	/// 骰子菜单的线路文字框很窄、行距很小：后面接的结果说明一长就折成两行，两行叠在一起看不清。
	/// 这里的文字框改成不折行，结果说明一行往右写完（右边是空的）。
	/// 线路文字本身（DiceOption 的 _normalText、_highText）是 TextMeshPro，不是 UI.Text，也要一起改，
	/// 不然结果说明一超过框宽（723）就折成几行，框高（55.2）放不下（日语的旗标名、变量名比中文长，更容易超过）。
	/// </summary>
	[HarmonyPostfix]
	[HarmonyPatch(typeof(DiceMenuDialog), "OnEnable")]
	public static void DiceMenuDialog_OnEnable_Postfix(DiceMenuDialog __instance)
	{
		foreach (UnityEngine.UI.Button button in __instance.CachedButtons ?? new UnityEngine.UI.Button[0])
		{
			if (button != null)
			{
				DiceNoWrap(button);
			}
		}
	}

	// 线路文字填进去以后，照画面宽度缩小说明。先再设一次不折行、不自动缩小，量出来的才是写成一行的宽度
	[HarmonyPostfix]
	[HarmonyPatch(typeof(DiceOption), "UpdateContent")]
	public static void DiceOption_UpdateContent_Postfix(DiceOption __instance)
	{
		DiceNoWrap(__instance);
		DiceFitToScreen(__instance);
	}

	// 选中时显示的是另一个文字框（_highText，粗体字型，字宽不同），换上来时再量一次
	[HarmonyPostfix]
	[HarmonyPatch(typeof(DiceOption), "Select")]
	public static void DiceOption_Select_Postfix(DiceOption __instance) => DiceFitToScreen(__instance);

	/// <summary>说明往右写到画面右边还写不完时，把说明的字缩小到刚好写到画面右边为止（最小 <see cref="DiceMinDescSize"/>）。</summary>
	private const float DiceMinDescSize = 11f;
	private const float DiceRightMargin = 24f;
	private static readonly Regex DiceDescSize = new Regex(@"\n<size=(\d+(?:\.\d+)?)>");
	private static readonly Vector3[] DiceCorners = new Vector3[4];

	private static void DiceFitToScreen(DiceOption option)
	{
		try
		{
			foreach (TMPro.TMP_Text text in option.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: false))
			{
				Match size = DiceDescSize.Match(text.text ?? string.Empty);
				if (!size.Success || text.canvas == null)
				{
					continue;
				}
				text.ForceMeshUpdate();
				TMPro.TMP_TextInfo info = text.textInfo;
				float left = float.MaxValue;
				float right = float.MinValue;
				for (int i = 1; i < info.lineCount; i++)
				{
					left = Mathf.Min(left, info.lineInfo[i].lineExtents.min.x);
					right = Mathf.Max(right, info.lineInfo[i].lineExtents.max.x);
				}
				((RectTransform)text.canvas.rootCanvas.transform).GetWorldCorners(DiceCorners);
				float screenRight = text.rectTransform.InverseTransformPoint(DiceCorners[2]).x - DiceRightMargin;
				if (right <= screenRight || right <= left)
				{
					continue;
				}
				float current = float.Parse(size.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
				float fitted = Mathf.Max(DiceMinDescSize, Mathf.Floor(current * (screenRight - left) / (right - left)));
				if (fitted < current)
				{
					text.text = DiceDescSize.Replace(text.text, "\n<size=" + fitted.ToString(System.Globalization.CultureInfo.InvariantCulture) + ">", 1);
				}
			}
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("骰子菜单结果说明缩小失败: " + ex.Message);
		}
	}

	private static void DiceNoWrap(UnityEngine.Component root)
	{
		try
		{
			foreach (UnityEngine.UI.Text text in root.GetComponentsInChildren<UnityEngine.UI.Text>(includeInactive: true))
			{
				text.horizontalOverflow = HorizontalWrapMode.Overflow;
			}
			// 自动缩小也关掉：不折行时它为了把整行塞进框里，会把标题缩到最小（15），说明是固定字号（<size=16>）却不跟着缩。
			// 标题照最大字号（28），说明往右写完，太长的由 DiceFitToScreen 缩小
			foreach (TMPro.TMP_Text text in root.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: true))
			{
				// 掷骰线路是深色底：后果说明里的加减换回浅绿、浅红
				if (text.text != null && (text.text.Contains(DeltaUpDark) || text.text.Contains(DeltaDownDark)))
				{
					text.text = text.text.Replace(DeltaUpDark, "#A3B86C").Replace(DeltaDownDark, "#CD594A");
				}
				text.enableWordWrapping = false;
				text.overflowMode = TMPro.TextOverflowModes.Overflow;
				if (text.enableAutoSizing)
				{
					text.enableAutoSizing = false;
					text.fontSize = text.fontSizeMax;
				}
			}
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("骰子菜单文字不折行设置失败: " + ex.Message);
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(PositionController), "OnPositionEnter")]
	public static void PositionController_OnPositionEnter_Prefix(PositionController __instance)
	{
		PositionType fieldValue = __instance.GetFieldValue<PositionType>("_position");
		if (_positionCtrl != null)
		{
			HandleDialogChange(_positionCtrl, addSuffix: false);
		}
		if (HasTriggerMission(fieldValue, out var missionKey))
		{
			HandleDialogChange(__instance, addSuffix: true, "可触发：" + missionKey);
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(PositionController), "OnPositionClick")]
	public static void PositionController_OnPositionClick_Prefix()
	{
		if (_positionCtrl != null)
		{
			HandleDialogChange(_positionCtrl, addSuffix: false);
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(ConditionResultData), "Execute")]
	public static void PrefixExecuteConditionResult(ConditionResultData __instance)
	{
		List<ConditionResultItem> fieldValue = __instance.GetFieldValue<List<ConditionResultItem>>("_items");
		LogCheckpoint("触发剧情检查点:", fieldValue);
	}

	// 只记真的满足、会触发的任务：没满足的每次行动都检查一遍，记下来会把回想（最多 300 条）占满
	[HarmonyPostfix]
	[HarmonyPatch(typeof(MissionCheckData), "Check")]
	public static void PostfixCheckMission(MissionCheckData __instance, bool __result)
	{
		if (!__result)
		{
			return;
		}
		ConditionResultItem[] fieldValue = __instance.GetFieldValue<ConditionResultItem[]>("_conditions");
		StringBuilder stringBuilder = new StringBuilder("触发任务检查点:");
		AppendConditions(fieldValue, stringBuilder);
		if (__instance.GetFieldValue<bool>("_dayCheck"))
		{
			DataHelper.GetMissionPeriod(__instance, out var periodText);
			if (!periodText.IsNullOrWhiteSpace())
			{
				stringBuilder.AppendLine("    时限：" + periodText);
			}
		}
		PrintToNarrativeLog(stringBuilder.ToString());
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(SwitchResultData), "Execute")]
	public static void PrefixExecuteSwitchResult(SwitchResultData __instance)
	{
		List<ConditionResultItem> fieldValue = __instance.GetFieldValue<List<ConditionResultItem>>("_items");
		LogCheckpoint("触发剧情分支检查点:", fieldValue);
	}

	// 地点对白后面接的「(可触发：…)」：按译表识别各语言，切换语言后也能清除旧提示。
	private static bool IsTriggerHint(string text)
	{
		if (text.Contains("可触发：") || text.Contains("可觸發："))
		{
			return true;
		}
		// 只查这条句式，不为识别提示初始化各语言词典或读取英文补丁文件。
		return Array.Exists(new[] { UiText.Korean, UiText.Japanese, UiText.English }, language =>
		{
			string format = UiText.TableTranslation("可触发：{0}", language);
			return !string.IsNullOrEmpty(format) && text.Contains(format.Replace("{0}", "").TrimEnd());
		});
	}

	// 任务标题本身也可能带括号，只移除包含触发提示的那层，保留原对白中的括号。
	private static string RemoveTriggerHint(string text)
	{
		if (!string.IsNullOrEmpty(text))
		{
			for (int start = text.LastIndexOf('('); start > 0; start = text.LastIndexOf('(', start - 1))
			{
				if (IsTriggerHint(text.Substring(start)))
				{
					return text.Substring(0, start);
				}
			}
		}
		return text;
	}

	private static void HandleDialogChange(PositionController instance, bool addSuffix, string suffix = "")
	{
		DicePlugin.LogInfo($"HandleDialogChange called, addSuffix: {addSuffix}, suffix: {suffix}");
		try
		{
			PositionDialogItem dialog = instance.GetFieldValue<FreePositionData>("_positionData").GetDialog();
			string text = dialog.GetFieldValue<string>("_dialog");
			if (addSuffix)
			{
				text = RemoveTriggerHint(text);
				_originalDialogKey = text;
				_positionCtrl = instance;
				string text2 = LocalizationManager.Instance.LocaleResolver.GetString("Position/Dialog/" + text);
				if (string.IsNullOrEmpty(text2))
				{
					text2 = text;
				}
				dialog.SetPrivateField("_dialog", text2 + "(" + TextConverter.ForDisplay(suffix) + ")");
				return;
			}
			_originalDialogKey = RemoveTriggerHint(_originalDialogKey);
			dialog.SetPrivateField("_dialog", _originalDialogKey);
			_positionCtrl = null;
			_originalDialogKey = string.Empty;
		}
		catch (Exception o)
		{
			DicePlugin.LogError(o);
		}
	}

	/// <summary>
	/// 点这个地点会不会触发任务，和游戏点地点时的判断一样：主线正在等（有地点）时，只有主线的地点能点，点了走主线；
	/// 否则是本旬生效的支线里，地点是这里、有开头剧本的那个。返回任务的名字，主线前面加「主线」。
	/// </summary>
	private static bool HasTriggerMission(PositionType position, out string missionKey)
	{
		missionKey = string.Empty;
		MissionManagerData missions = MissionManagerData.Instance;
		if (missions == null)
		{
			return false;
		}
		if (missions.MainMissionStart && missions.MainMissionPosition != PositionType.無)
		{
			MissionCheckData main = missions.MainCheckData;
			if (position != missions.MainMissionPosition || main == null || string.IsNullOrEmpty(main.StartScript))
			{
				return false;
			}
			missionKey = "主线「" + MissionName(main, missions.MainMissionKey) + "」";
			return true;
		}
		if (missions.ActiveSubMissions == null)
		{
			return false;
		}
		// 刚做完的支线在「本旬生效」的列表里还会留一阵；和「此刻」页一样，只算任务当前这一步（做完的已经是 clear，不在里面）
		Dictionary<MissionCheckData, (string, string)> current = DataHelper.GetMissions();
		foreach (KeyValuePair<string, MissionCheckData> active in missions.ActiveSubMissions)
		{
			MissionCheckData check = active.Value;
			if (check.Position != position || string.IsNullOrEmpty(check.StartScript) || !current.ContainsKey(check))
			{
				continue;
			}
			missionKey = MissionName(check, active.Key);
			return true;
		}
		return false;
	}

	/// <summary>任务的显示名：剧情回顾里整理过的标题，没有就用任务说明的第一行，再没有用编号。</summary>
	private static string MissionName(MissionCheckData check, string fallback)
	{
		LOMAssistant.Features.Story.StoryMission known = LOMAssistant.Features.Story.StoryAtlas.Find(check.Key);
		string desc = TextConverter.ToSimplified(UiQuestNames.Description(check.Key, check.GetFieldValue<string>("_desc")) ?? "").Split('\n')[0].Trim();
		return known != null && known.Title.Length > 0 ? known.Title : desc.Length > 0 ? desc : fallback;
	}

	private static void LogCheckpoint(string header, IEnumerable<ConditionResultItem> items)
	{
		StringBuilder stringBuilder = new StringBuilder(header);
		AppendConditions(items, stringBuilder);
		PrintToNarrativeLog(stringBuilder.ToString());
	}

	private static void AppendConditions(IEnumerable<ConditionResultItem> items, StringBuilder logBuilder)
	{
		foreach (ConditionResultItem item in items)
		{
			bool isInPeriod;
			string text = DataHelper.ExtractConditions(item, out isInPeriod);
			if (!text.IsNullOrWhiteSpace())
			{
				logBuilder.AppendLine("    " + text);
			}
		}
	}

	// 判定记录写进游戏的回想（1.0.6001 起是 StoryLogData；旧的 Fungus NarrativeLog 不再显示）。
	// 不写进存档：读档后清掉，Mod 卸掉后也不会在玩家存档里留东西。文字记录游戏会用单独的颜色显示
	// 本 Mod 写进回想的文字（认出自己的记录用，回想最多 300 条，这里留多一点）
	private static readonly HashSet<string> _ours = new HashSet<string>();
	private static readonly AccessTools.FieldRef<StoryLogEntryView, Text> LogContent = AccessTools.FieldRefAccess<StoryLogEntryView, Text>("_contentText");
	private static readonly AccessTools.FieldRef<StoryLogEntryView, int> LogChoiceSize = AccessTools.FieldRefAccess<StoryLogEntryView, int>("_choiceFontSize");

	// 回想里的判定记录用游戏选项那一行的小字号，不和对白一样大（游戏每次显示条目都先把字号重置，再按类型改）
	[HarmonyPostfix]
	[HarmonyPatch(typeof(StoryLogEntryView), nameof(StoryLogEntryView.Setup))]
	public static void LogEntrySetup_Postfix(StoryLogEntryView __instance, StoryLogEntry entry)
	{
		try
		{
			if (entry == null || entry.Type != StoryLogType.Text || entry.Text == null || !_ours.Contains(entry.Text))
			{
				return;
			}
			Text content = LogContent(__instance);
			int small = LogChoiceSize(__instance);
			if (content != null)
			{
				content.fontSize = small > 0 && small < content.fontSize ? small : Mathf.RoundToInt(content.fontSize * 0.72f);
			}
		}
		catch (Exception ex)
		{
			FileLogger.Log("[回想] 判定记录改字号失败（已忽略）：" + ex.Message);
		}
	}

	// 同一旬里内容一样的记录只写一次：养成地图每刷新一次地点事件、每做一次行动，游戏都会把同样的条件再查一遍
	private static readonly HashSet<string> _logged = new HashSet<string>();
	private static int _loggedAt;

	private static void PrintToNarrativeLog(string message)
	{
		// 只在剧情里记：养成地图上的多是地点、闲聊的内部开关，对玩家没用，还占回想的 300 条
		if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Story")
		{
			return;
		}
		string text = Regex.Replace(message, "^\\s*$\\n|\\r", string.Empty, RegexOptions.Multiline).Trim();
		GameTime now = PlayerStatManagerData.Instance?.GameTime;
		string key = (ReferenceEquals(now, null) ? "" : now.Year + "-" + now.Month + "-" + (int)now.Stage + "|") + text;
		// 读档时游戏清掉了不存档的记录（回想条数变少），去重表也跟着清
		if (_logged.Count > 2000 || StoryLogData.Entries.Count < _loggedAt)
		{
			_logged.Clear();
		}
		if (text.Length > 0 && _logged.Add(key))
		{
			// 不经过本 Mod 界面的翻译，这里先换成界面语言（繁体、韩语、日语）
			string shown = TextConverter.ForDisplay(text);
			if (_ours.Count > 1000)
			{
				_ours.Clear();
			}
			_ours.Add(shown);
			StoryLogData.AddText(shown, false);
		}
		_loggedAt = StoryLogData.Entries.Count;
	}
}

/// <summary>
/// 选项后果「鼠标悬停」显示：鼠标停在对话选项、掷骰线路上（或用手柄、方向键选中时），在选项旁边用提示框显示后果说明；
/// 不看就不剧透。普通选项按按钮上的文字查回选项编号，掷骰线路按线路记着的「编号|范围」查。
/// </summary>
internal sealed class OptionHoverHint : MonoBehaviour
{
	private static OptionHoverHint _instance;

	private MenuDialog[] _dialogs = new MenuDialog[0];
	private float _nextScan;
	private bool _useSelection;
	private Vector2 _lastMouse;
	private GUIStyle _style;
	private float _scale = -1f;
	private int _fontMode = -1;

	public static void Ensure(GameObject host)
	{
		if (_instance == null)
		{
			_instance = host.GetComponent<OptionHoverHint>() ?? host.AddComponent<OptionHoverHint>();
		}
	}

	private void Update()
	{
		// 选项后果设成悬停时都看；其余时候只为强行放出来的选项显示条件
		if (ConfigManager.OptionResultMode.Value != 1 && !HiddenOptions.Any)
		{
			return;
		}
		// 动了鼠标就按鼠标位置；按了方向键、手柄就按选中的选项（菜单打开时游戏会默认选中第一项，鼠标玩家不该一开就看到它的后果）
		var mouse = UnityEngine.InputSystem.Mouse.current;
		if (mouse != null)
		{
			Vector2 position = mouse.position.ReadValue();
			if ((position - _lastMouse).sqrMagnitude > 4f)
			{
				_useSelection = false;
			}
			_lastMouse = position;
		}
		var keyboard = UnityEngine.InputSystem.Keyboard.current;
		var pad = UnityEngine.InputSystem.Gamepad.current;
		if ((keyboard != null && (keyboard.upArrowKey.wasPressedThisFrame || keyboard.downArrowKey.wasPressedThisFrame || keyboard.leftArrowKey.wasPressedThisFrame || keyboard.rightArrowKey.wasPressedThisFrame))
			|| (pad != null && (pad.dpad.ReadValue().sqrMagnitude > 0.25f || pad.leftStick.ReadValue().sqrMagnitude > 0.25f)))
		{
			_useSelection = true;
		}
		if (Time.unscaledTime >= _nextScan)
		{
			_nextScan = Time.unscaledTime + 0.3f;
			_dialogs = FindObjectsOfType<MenuDialog>();
		}
	}


	private void OnGUI()
	{
		if ((ConfigManager.OptionResultMode.Value != 1 && !HiddenOptions.Any) || UiHider.Active || Event.current.type != EventType.Repaint)
		{
			return;
		}
		foreach (MenuDialog dialog in _dialogs)
		{
			if (dialog == null || !dialog.isActiveAndEnabled)
			{
				continue;
			}
			Button[] buttons = Traverse.Create(dialog).Field("cachedButtons").GetValue<Button[]>();
			if (buttons == null)
			{
				continue;
			}
			foreach (Button button in buttons)
			{
				if (button == null || !button.isActiveAndEnabled || !Pointed(button))
				{
					continue;
				}
				string note = Note(button);
				if (note.Length > 0)
				{
					Draw(button, note, buttons);
				}
				return;
			}
		}
	}

	private bool Pointed(Button button)
	{
		if (_useSelection)
		{
			return EventSystem.current != null && EventSystem.current.currentSelectedGameObject == button.gameObject;
		}
		Canvas canvas = button.GetComponentInParent<Canvas>();
		Camera cam = canvas == null || canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.rootCanvas.worldCamera;
		return RectTransformUtility.RectangleContainsScreenPoint((RectTransform)button.transform, _lastMouse, cam);
	}

	private static string Note(Button button)
	{
		DiceOption dice = button.GetComponent<DiceOption>();
		string key = null;
		if (dice != null)
		{
			Text source = Traverse.Create(dice).Field("_sourceText").GetValue<Text>();
			key = source != null && source.text != null ? source.text.Split('|')[0] : null;
		}
		else
		{
			OptionInfoPatch.ButtonKeys.TryGetValue(button, out key);
		}
		if (key == null)
		{
			return "";
		}
		return ConfigManager.OptionResultMode.Value == 1 ? OptionInfoPatch.OptionNote(key) : HiddenOptions.Note(key);
	}

	private void Draw(Button button, string note, Button[] group)
	{
		float scale = PanelTheme.UiScale;
		if (_style == null || ModFont.NeedsRebuild(scale, _scale, _fontMode))
		{
			_scale = scale;
			_fontMode = ModFont.Mode;
			_style = PanelTheme.Toast(scale, 18);
		}
		// 按钮在屏幕上的范围（IMGUI 坐标，原点左上）
		var corners = new Vector3[4];
		((RectTransform)button.transform).GetWorldCorners(corners);
		Canvas canvas = button.GetComponentInParent<Canvas>();
		Camera cam = canvas == null || canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.rootCanvas.worldCamera;
		Vector2 min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
		Vector2 max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
		// 整组选项的上下沿：提示框放在整组下面（放不下放上面），不压住别的选项
		float bottom = min.y, top = max.y;
		foreach (Button other in group)
		{
			if (other != null && other.isActiveAndEnabled)
			{
				((RectTransform)other.transform).GetWorldCorners(corners);
				bottom = Mathf.Min(bottom, RectTransformUtility.WorldToScreenPoint(cam, corners[0]).y);
				top = Mathf.Max(top, RectTransformUtility.WorldToScreenPoint(cam, corners[2]).y);
			}
		}
		var content = new GUIContent(TextConverter.ForDisplay(OptionInfoPatch.ColorDeltas(note, light: true)));
		float width = Mathf.Min(Mathf.Max(max.x - min.x, 360f * scale), Screen.width - 16f * scale);
		float height = _style.CalcHeight(content, width);
		float x = Mathf.Clamp(min.x, 8f * scale, Screen.width - width - 8f * scale);
		float y = Screen.height - bottom + 4f * scale;
		if (y + height > Screen.height - 8f * scale)
		{
			y = Screen.height - top - height - 4f * scale;
		}
		GUI.Box(new Rect(x, y, width, height), content, PanelTheme.Sync(_style));
	}
}

/// <summary>
/// 选项后果的加减默认用深绿、深红（浅色选项底上看得清）。同一句话也会出现在深色底上（暗场景的选项、养成地图的行程表），
/// 那里的字本身是浅色的：文字设成浅色字时，把深绿、深红换成浅绿、浅红。按字本身的颜色判断底色，不用逐个界面去认。
/// </summary>
[HarmonyPatch]
internal static class DeltaColorFit
{
	private const string UpLight = "#A3B86C", DownLight = "#CD594A";

	private static bool LightText(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b > 0.5f;

	private static string Fit(string value, Color color)
	{
		if (value == null || value.IndexOf("<color=#", StringComparison.Ordinal) < 0 || !LightText(color)
			|| (value.IndexOf(OptionInfoPatch.DeltaUpDark, StringComparison.Ordinal) < 0 && value.IndexOf(OptionInfoPatch.DeltaDownDark, StringComparison.Ordinal) < 0))
		{
			return value;
		}
		return value.Replace(OptionInfoPatch.DeltaUpDark, UpLight).Replace(OptionInfoPatch.DeltaDownDark, DownLight);
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(Text), nameof(Text.text), MethodType.Setter)]
	private static void Text_Prefix(Text __instance, ref string value)
	{
		value = Fit(value, __instance.color);
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(TMPro.TMP_Text), nameof(TMPro.TMP_Text.text), MethodType.Setter)]
	private static void Tmp_Prefix(TMPro.TMP_Text __instance, ref string value)
	{
		value = Fit(value, __instance.color);
	}
}
