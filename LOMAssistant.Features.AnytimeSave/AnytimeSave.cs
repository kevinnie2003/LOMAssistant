using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Fungus;
using HarmonyLib;
using LOMAssistant.Features.Dice;
using MoonSharp.Interpreter;
using Mortal.Core;
using Mortal.Story;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using UObject = UnityEngine.Object;

namespace LOMAssistant.Features.AnytimeSave;

[Serializable] public class Entry { public string kind, label, type, value; }
[Serializable] public class ScriptStamp { public string name, hash; }
[Serializable] public class DiceAddition { public string name, status; public int value, sign; }
[Serializable] public class DiceValue { public int random, result; public string header; public List<DiceAddition> additions = new List<DiceAddition>(); }
[Serializable] public class DiceState { public bool valid, resultReady, useFate; public int random, selection, count, min, max, rangeIndex, defaultMax; }
[Serializable] public class FreeRng { public int next, nextp; public int[] seed; }
[Serializable]
public class SaveFile
{
	public int format = 2;
	public string compatibilityId;
	public string version, coreHash, storyHash, savedAt, scene, rootScript, currentScript, nativeSlot, gameJson, randomJson, preview;
	// 存档时正在放的音乐、环境音（存档本身不记，读档快进时剧本里不一定会再放一次）
	public string music, envSound;
	// 存档时音乐、环境音的音量：剧本常用淡出到 0 关掉环境音，名字还留着；读档重放时 PlayEnvSound 会把音量拉回 100，要再调回去。以前的存档没有，是 -1
	public float musicVolume = -1, envVolume = -1;
	// 存档时台上看得见的角色（读档快进时清场特效可能没跑，读档后把不在名单里的隐藏）
	public List<string> visibleCharacters;
	public int target;
	/// <summary>存档时的游戏日期（第几年几月几旬），列表里显示用；以前的存档没有。</summary>
	public string gameTime;
	public List<Entry> trace = new List<Entry>();
	public List<ScriptStamp> scripts = new List<ScriptStamp>();
	public DiceState dice;
	public FreeRng freeRng;
	/// <summary>这段记录跑的时候剧本里带条件的选项改写过（见 HiddenOptions），回放时要照样改写，执行记录才对得上。以前的存档没有，是 false。</summary>
	public bool hiddenOptions;
}
[Serializable] public class Envelope { public string sha256, payload; }

/// <summary>
/// 随时存档：在剧情台词、选项、掷骰和自由行动界面存到独立槽位（存档目录下的 AnytimeSave\Slot_XX.json），不占原版存档栏。
/// 移植自 mahosalieri85-a11y 的 MortalAnytimeSave（issue #2）。原理：
/// 存档 = 原版存档数据快照 + 本段剧情的执行记录（每句台词、选项、查询结果）。
/// 读档时经标题界面清掉旧剧情，从记录的剧情入口重新执行，按记录跳过已完成的台词和选项、跳过数据修改，
/// 走到存档时那一句再套一次快照，交还给玩家。不序列化 Lua 协程。
/// 游戏更新改动了存读档相关的方法时（见 contract.tsv.gz）自动停用，不会冒险继续。
/// </summary>
public partial class AnytimeSave : MonoBehaviour
{
	public static AnytimeSave Instance;
	public static bool Replaying, Faulted, Restoring;

	public static string SaveRoot
	{
		get
		{
			if (SaveSystem.Instance == null) throw new InvalidOperationException("存档系统尚未初始化");
			var file = (string)AccessTools.Method(typeof(SaveSystem), "GetSaveDataFile").Invoke(SaveSystem.Instance, new object[] { "universe" });
			return Path.GetDirectoryName(Path.GetFullPath(file));
		}
	}
	internal static string StoreRoot => Path.Combine(SaveRoot, "AnytimeSave");

	/// <summary>正在读的随时存档存的时候开着「显示未满足条件的选项」。</summary>
	internal bool LoadingHiddenOptions => loading != null && loading.hiddenOptions;

	/// <summary>
	/// 这段剧情的执行记录是不是按改写过的剧本跑的。一段记录开头定一次（新开一段按设置，读随时存档按存档），
	/// 中途改设置到下一段才生效：同一段记录里有的剧本改写、有的没改写，读档回放时就对不上。存档、回退点都记这个。
	/// </summary>
	private bool runHidden;

	/// <summary>这个剧本加载时要不要改写带条件的选项（HiddenOptions 在本类包装剧本之前问）。</summary>
	internal bool HiddenOptionsFor(LuaEnvironment env)
	{
		if (Restoring || Replaying) return LoadingHiddenOptions;
		return env.Interpreter != currentInterpreter ? LOMAssistant.Config.ConfigManager.ForceUnmetOptions.Value : runHidden;
	}

	private static readonly MethodInfo ApplyData = AccessTools.Method(typeof(SaveSystem), "ExecuteLoadGameData");
	private static readonly HashSet<string> Queries = new HashSet<string> {
		"checkpointmanager.Switch", "checkpointmanager.Condition", "checkpointmanager.Position", "checkpointmanager.Dice",
		"luamanager.GetStatData", "luamanager.GetFlagData", "luamanager.GetTempScript", "luamanager.GetCurrentTravelScript", "luamanager.GetStoryText", "math.random", "lomhidden" };
	private static readonly HashSet<string> Mutators = new HashSet<string> {
		"luamanager.SetGameTime", "luamanager.SetGameDayEnvironment", "luamanager.NextMonth", "luamanager.NextRound",
		"luamanager.SetCurrentTravelScript", "luamanager.SetTempScript", "luamanager.SetMissionTime", "luamanager.SetTitlePartner",
		"luamanager.SetPlayerBattleSkill", "luamanager.ResetBattleSkill", "luamanager.SetBattleSkillActive", "luamanager.PrologueSave",
		"luamanager.AutoSave", "luamanager.AutoFreeSave", "shoppanel.NewShop" };
	private static readonly Regex Token = new Regex(@"(?<str>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*')|(?<comment>--[^\r\n]*)|(?<name>\b[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)", RegexOptions.Compiled);

	private List<Entry> trace = new List<Entry>();
	private List<ScriptStamp> stamps = new List<ScriptStamp>();
	private string rootScript, scriptPrefix, coreHash, storyHash, message = "";
	private int active = -1, cursor, target, replyIndex = -1;
	private bool panel, busy, installed, pendingLoad;
	private string compatibilityFailure;
	private float messageUntil, replayStarted;
	private float panelTimeScale = 1;
	private readonly List<InputAction> pausedActions = new List<InputAction>();
	private SaveFile loading;
	private Script currentInterpreter;
	private DiceState pendingDice;

	/// <summary>挂到插件对象上。返回 false 表示游戏版本未核验，功能停用（面板仍可打开看原因）。</summary>
	public static bool Install(GameObject host)
	{
		var self = host.GetComponent<AnytimeSave>() ?? host.AddComponent<AnytimeSave>();
		return self.installed;
	}

	private void Awake()
	{
		compatibilityFailure = CheckCompatibility();
		if (compatibilityFailure != null)
		{
			Unavailable = compatibilityFailure;
			LOMAssistantPlugin.LogError(compatibilityFailure);
			FileLogger.LogError("[随时存档] " + compatibilityFailure);
			return;
		}
		Instance = this;
		// 游戏裁剪过的 Mono 没有 Reflection.Emit，让 Json.NET 改用反射
		typeof(JsonConvert).Assembly.GetType("Newtonsoft.Json.Serialization.JsonTypeReflector")
			.GetField("_dynamicCodeGeneration", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, (bool?)false);
		scriptPrefix = ReadResource("Replay.lua");
		SceneManager.activeSceneChanged += SceneChanged;
		Application.logMessageReceived += ScriptCrashed;
		installed = true;
		FileLogger.Log("[随时存档] 已启用。");
	}

	internal static string ReadResource(string name)
	{
		using (var stream = typeof(AnytimeSave).Assembly.GetManifestResourceStream("LOMAssistant.AnytimeSave." + name))
		{
			if (stream == null) throw new FileNotFoundException("缺少内置资源 " + name);
			Stream source = name.EndsWith(".gz") ? new GZipStream(stream, CompressionMode.Decompress) : stream;
			using (var reader = new StreamReader(source, Encoding.UTF8)) return reader.ReadToEnd().Replace("\r\n", "\n");
		}
	}

	private static string Hash(string s) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", ""); }
	private static string HashFile(string p) { using (var sha = SHA256.Create()) using (var f = File.OpenRead(p)) return BitConverter.ToString(sha.ComputeHash(f)).Replace("-", ""); }

	private void SceneChanged(Scene previous, Scene next)
	{
		active = -1; busy = false;
		if (next.name != "Story" && !Restoring) { trace.Clear(); stamps.Clear(); rootScript = null; ClearRollback(); }
	}

	// 恢复途中剧本的 Lua 协程报错就停了，剧情不会再往下走；不用等 180 秒超时，直接停下并说明
	private void ScriptCrashed(string condition, string stackTrace, LogType type)
	{
		if (Replaying && type == LogType.Exception && stackTrace != null && stackTrace.Contains("RunLuaCoroutine"))
		{
			Fail("剧本报错（" + (condition ?? "").Split('\n')[0] + "）");
		}
	}

	internal void Notice(string s, float seconds = 8)
	{
		message = s; messageUntil = Time.realtimeSinceStartup + seconds;
		FileLogger.Log("[随时存档] " + s);
	}

	internal void Fail(string s)
	{
		Faulted = true; Replaying = false; Restoring = false; pendingLoad = false; active = -1;
		Time.timeScale = 0; Notice("恢复已停止：" + s + "。马上回到标题画面，请重新读档。", 12);
		LOMAssistantPlugin.LogError("随时存档：" + s);
		// 停在半路的剧情没法继续，画面是黑的：几秒后自动回标题（按真实时间等，游戏这时是暂停的）
		StartCoroutine(BackToTitleLater());
	}

	private IEnumerator BackToTitleLater()
	{
		yield return new WaitForSecondsRealtime(4f);
		if (Faulted)
		{
			BackToTitle();
		}
	}

	private void Apply(SaveFile data, bool atTarget)
	{
		var game = JsonUtility.FromJson<GameSave>(data.gameJson);
		ApplyData.Invoke(SaveSystem.Instance, new object[] { game });
		// 回想（1.0.6001 起写进存档）：游戏读档时在 ExecuteLoadGameData 外面另读，这里照做，不然回到的这一刻之后的记录还留着、重演时再记一遍
		StoryLogCompat.Load(game);
		if (!string.IsNullOrEmpty(data.nativeSlot)) SaveSystem.Instance.SetSlot(data.nativeSlot);
		PlayerStatManagerData.Instance.SetStoryScript(atTarget || data.scene == "Free" ? data.currentScript : data.rootScript);
		if (atTarget) PlayerStatManagerData.Instance.SetStartScript(game.StartStoryScript);
		UnityEngine.Random.state = JsonUtility.FromJson<UnityEngine.Random.State>(data.randomJson);
	}

	private readonly Dictionary<string, string> wrapped = new Dictionary<string, string>(StringComparer.Ordinal);

	internal string PrepareScript(LuaEnvironment env, string code, string name)
	{
		if (!name.EndsWith(".LuaScript", StringComparison.Ordinal) || name == "tmp.LuaScript") return code;
		// 版本核对按原剧本：「显示未满足条件的选项」改过的剧本（见 HiddenOptions）也按改之前的算
		string original = LOMAssistant.HiddenOptions.Original(code);
		string hash = Hash(original);
		string shortName = name.Substring(0, name.Length - 10);
		if (env.Interpreter != currentInterpreter)
		{
			currentInterpreter = env.Interpreter;
			if (!Restoring) { trace.Clear(); stamps.Clear(); rootScript = null; active = -1; Faulted = false; ClearRollback(); }
			runHidden = Restoring ? LoadingHiddenOptions : LOMAssistant.Config.ConfigManager.ForceUnmetOptions.Value;
		}
		if (string.IsNullOrEmpty(rootScript)) rootScript = shortName;
		var stamp = stamps.Find(x => x.name == name);
		if (Replaying)
		{
			var expected = loading.scripts.Find(x => x.name == name);
			if (expected == null || expected.hash != hash) { Fail("剧情脚本版本不匹配：" + name); throw new InvalidDataException(message); }
		}
		if (stamp == null) stamps.Add(new ScriptStamp { name = name, hash = hash });
		Bind(env.Interpreter);
		// 版本核对用的是原剧本的哈希；事件自选的等待先包上，再套记录/回放包装。包装结果只取决于剧本内容，按（改过的）内容的哈希缓存
		string contentHash = ReferenceEquals(original, code) ? hash : Hash(code);
		if (!wrapped.TryGetValue(contentHash, out string result))
		{
			if (wrapped.Count >= 40)
			{
				wrapped.Clear();
			}
			result = wrapped[contentHash] =scriptPrefix + "\n" + Transform(EventPicker.Instrument(code, name.EndsWith(".LuaScript") ? name.Substring(0, name.Length - ".LuaScript".Length) : name));
		}
		return result;
	}

	/// <summary>给剧情脚本套上记录/回放包装：台词、选项、掷骰、查询改成 ams_* 调用，数据修改在回放时跳过。</summary>
	public static string Transform(string code)
	{
		var output = new StringBuilder();
		foreach (string line in code.Replace("\r\n", "\n").Split('\n'))
		{
			string trimmed = line.TrimStart();
			bool mutate = trimmed.StartsWith("statmodifymanager.") && !trimmed.StartsWith("statmodifymanager.Get");
			mutate |= trimmed.StartsWith("runwait(statmodifymanager.");
			mutate |= Mutators.Any(n => trimmed.StartsWith(n + "("));
			bool panelCall = Regex.IsMatch(trimmed, @"^runwait\((?:\w*panel|intropanel)\.Open\(");
			string transformed = Token.Replace(line, delegate (Match m)
			{
				if (!m.Groups["name"].Success) return m.Value;
				string n = m.Value;
				int end = m.Index + m.Length, k = end;
				while (k < line.Length && char.IsWhiteSpace(line[k])) k++;
				if (n == "dicemenudialog.ResultSelection") return "ams_query('dice.ResultSelection', function() return dicemenudialog.ResultSelection end)";
				if (k >= line.Length || line[k] != '(') return n;
				if (Queries.Contains(n)) return "__Q__" + n;
				if (n == "say") return "ams_say";
				if (n == "choose") return "ams_choose";
				if (n == "choosetimer") return "ams_choosetimer";
				if (n == "wait") return "ams_wait";
				if (n == "runwait" && trimmed.StartsWith("runwait(dicemenudialog.ExecuteRoll(")) return "ams_roll";
				if (n == "runwait" && panelCall) return "ams_panel";
				return n;
			});
			transformed = Regex.Replace(transformed, @"__Q__([\w.]+)\s*\(\s*", delegate (Match m)
			{
				string n = m.Groups[1].Value;
				bool empty = m.Index + m.Length < transformed.Length && transformed[m.Index + m.Length] == ')';
				return "ams_query('" + n + "', " + n + (empty ? "" : ", ");
			});
			if (mutate) transformed = "if not __ams_replaying() then __ams_mutate() " + transformed + " end";
			output.AppendLine(transformed);
		}
		return output.ToString();
	}

	private void Bind(Script script)
	{
		script.Globals["__ams_replaying"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(Replaying));
		script.Globals["__ams_mutate"] = DynValue.NewCallback((c, a) => { OnMutate(); return DynValue.Nil; });
		script.Globals["__ams_busy"] = DynValue.NewCallback((c, a) => { busy = a[0].Boolean; return DynValue.Nil; });
		script.Globals["__ams_begin"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(Begin(a[0].String, a[1].String)));
		script.Globals["__ams_end"] = DynValue.NewCallback((c, a) => { End(a[0]); return DynValue.Nil; });
		script.Globals["__ams_reply"] = DynValue.NewCallback((c, a) => Decode(trace[replyIndex]));
		script.Globals["__ams_query"] = DynValue.NewCallback((c, a) =>
		{
			string name = a[0].String;
			if (Faulted) throw new ScriptRuntimeException("Anytime Save restore stopped");
			var args = new DynValue[Math.Max(0, a.Count - 2)];
			for (int i = 0; i < args.Length; i++) args[i] = a[i + 2];
			if (Replaying)
			{
				Entry recorded = Consume("query", name);
				// 台词照样过一遍游戏的 GetStoryText：登记已读、按已读设文字颜色（恢复到的那句才不会是白字），内容用记录里的
				if (name == "luamanager.GetStoryText") script.Call(a[1], args);
				return Decode(recorded);
			}
			OnJudgment(name);
			DynValue v = script.Call(a[1], args);
			Entry entry = Encode(v); entry.kind = "query"; entry.label = name; trace.Add(entry);
			return v;
		});
	}

	private Entry Consume(string kind, string label)
	{
		if (cursor >= trace.Count || trace[cursor].kind != kind || trace[cursor].label != label)
		{ Fail("执行记录不一致，位置 " + cursor + " (" + kind + ")"); throw new ScriptRuntimeException(message); }
		return trace[cursor++];
	}

	private bool Begin(string kind, string label)
	{
		if (Faulted) throw new ScriptRuntimeException("Anytime Save restore stopped");
		if (Replaying)
		{
			int index = cursor;
			Consume(kind, label); replyIndex = index;
			if (index != target) return true;
			Apply(loading, true);
			pendingDice = loading.dice;
			RestoreSound(loading);
			StartCoroutine(TidyPortraits(loading.visibleCharacters));
			Replaying = false; Restoring = false; Time.timeScale = 1;
			active = index;
			// 恢复到的这句就是新的「最近一句」，之后的判定以它为回退点
			OnInteraction(index, kind);
			Notice(restoreLabel);
			return false;
		}
		active = trace.Count;
		OnInteraction(active, kind);
		trace.Add(new Entry { kind = kind, label = label, type = "nil" });
		return false;
	}

	private void End(DynValue value)
	{
		if (Replaying) return;
		if (active >= 0 && active < trace.Count)
		{
			var e = Encode(value); trace[active].type = e.type; trace[active].value = e.value;
		}
		active = -1;
	}

	private static Entry Encode(DynValue v)
	{
		var e = new Entry();
		if (v == null || v.IsNil() || v.Type == DataType.Void) e.type = "nil";
		else if (v.Type == DataType.Number) { e.type = "number"; e.value = v.Number.ToString("R", System.Globalization.CultureInfo.InvariantCulture); }
		else if (v.Type == DataType.Boolean) { e.type = "bool"; e.value = v.Boolean ? "1" : "0"; }
		else if (v.Type == DataType.String) { e.type = "string"; e.value = v.String; }
		else if (v.Type == DataType.UserData && v.UserData.Object is DiceCheckResult d)
		{
			var dto = new DiceValue { random = d.Random, result = d.Result, header = d.Header };
			foreach (var a in d.Additions) dto.additions.Add(new DiceAddition { name = a.Name, status = a.Status, value = a.Value, sign = a.Sign });
			e.type = "dice"; e.value = JsonConvert.SerializeObject(dto);
		}
		else throw new InvalidDataException("Unsupported query value: " + v.Type);
		return e;
	}

	private static DynValue Decode(Entry e)
	{
		switch (e.type)
		{
			case "number": return DynValue.NewNumber(double.Parse(e.value, System.Globalization.CultureInfo.InvariantCulture));
			case "bool": return DynValue.NewBoolean(e.value == "1");
			case "string": return DynValue.NewString(e.value);
			case "dice":
				var dto = JsonConvert.DeserializeObject<DiceValue>(e.value);
				var d = new DiceCheckResult(dto.random) { Result = dto.result, Header = dto.header };
				foreach (var a in dto.additions) d.Additions.Add(new DiceCheckAddition { Name = a.name, Status = a.status, Value = a.value, Sign = a.sign });
				return UserData.Create(d);
			default: return DynValue.Nil;
		}
	}

	private static string SceneName => SceneManager.GetActiveScene().name;

	private bool CanSave(out string reason)
	{
		reason = null;
		if (!installed) reason = compatibilityFailure ?? "随时存档尚未初始化";
		else if (Faulted || Replaying || Restoring || pendingLoad) reason = "正在恢复或已发生错误";
		else if (SceneController.Instance == null || SceneController.Instance.IsPrepare || SceneController.Instance.IsLoading) reason = "场景正在切换";
		else if (SceneName == "Story")
		{
			if (active < 0 || busy) reason = "请在台词、选项或掷骰时保存";
			else if (trace[active].kind == "roll")
			{
				var dice = UObject.FindObjectOfType<DiceMenuDialog>();
				if (dice == null || (!ActiveField(dice, "_continueButton") && !ActiveField(dice, "_startButton"))) reason = "骰子正在转动，请等结果显示";
				else if (!ActiveField(dice, "_continueButton") && ((bool)Field(dice, "_pressStartButton") || DiceManualSelectionPatch.IsClicked)) reason = "掷骰正在处理，请等结果显示";
			}
		}
		else if (SceneName == "Free")
		{
			var f = Mortal.Free.FreeManager.Instance;
			if (f == null || f.IsProcessing || !f.EnableHotKey) reason = "自由行动正在处理，请回到行动界面";
		}
		else reason = "只能在剧情和养成界面保存";
		return reason == null;
	}

	/// <summary>存一个新的随时存档，返回文件名（存不了返回 null）。note 给了就一并写上备注（剧情跳转存「跳转前」用）。</summary>
	public string SaveSlot(string note = null)
	{
		if (!CanSave(out string reason)) { Notice("暂不能保存：" + reason); return null; }
		try
		{
			var data = new SaveFile
			{
				version = Application.version, coreHash = coreHash, storyHash = storyHash, compatibilityId = compatibilityId,
				savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), scene = SceneName,
				rootScript = rootScript, currentScript = PlayerStatManagerData.Instance.CurrentStoryScript,
				nativeSlot = SaveSystem.Instance.CurrentSlot,
				gameJson = SaveSystem.Instance.GetCurrentSaveData(), randomJson = JsonUtility.ToJson(UnityEngine.Random.state),
				music = CurrentSound("_currentMusic"), envSound = CurrentSound("_currentEnvSound"), musicVolume = CurrentVolume("BackgroundMusic"), envVolume = CurrentVolume("EnvSound"),
				visibleCharacters = VisibleCharacters(),
				target = active, preview = Preview(), gameTime = GameTimeText(),
				dice = CaptureDice(),
				hiddenOptions = runHidden,
				freeRng = SceneName == "Free" ? CaptureFreeRng() : null,
				trace = SceneName == "Story" ? trace.Take(active + 1).ToList() : new List<Entry>(),
				scripts = new List<ScriptStamp>(stamps)
			};
			string payload = JsonConvert.SerializeObject(data);
			string text = JsonConvert.SerializeObject(new Envelope { payload = payload, sha256 = Hash(payload) });
			Directory.CreateDirectory(StoreRoot);
			// 每次都存成一个新文件，先写临时文件、读回来校验过再改名
			string name = NextFile(out int number);
			string path = Path.Combine(StoreRoot, name), tmp = path + ".tmp";
			using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
			{ byte[] bytes = Encoding.UTF8.GetBytes(text); f.Write(bytes, 0, bytes.Length); f.Flush(true); }
			ReadSave(tmp);
			File.Move(tmp, path);
			ReadSave(path);
			Remember(data, name);
			if (!string.IsNullOrEmpty(note))
			{
				SetNote(name, note);
			}
			Notice("已保存到随时存档 #" + number);
			return name;
		}
		catch (Exception ex) { Notice("保存失败：" + ex.Message); LOMAssistantPlugin.LogError("随时存档保存失败: " + ex); return null; }
	}

	private string Preview()
	{
		if (SceneName != "Story" || active < 0) return "养成";
		var e = trace[active];
		if (e.kind == "roll") { var d = CaptureDice(); return d.resultReady ? "掷骰结果：" + d.count + "（待确认）" : "掷骰前"; }
		if (e.kind == "choose" || e.kind == "timer") return ChoicePreview(e.label);
		return Regex.Replace(e.label ?? "", @"<[^>]+>|\{[^}]+\}", "");
	}

	private static string ChoicePreview(string label)
	{
		// 选项文字在 Story/ 下；只读文本，不用 LuaManager.GetStoryText（它会改已读记录）
		var texts = Regex.Matches(label ?? "", @"O_[A-Za-z0-9_]+").Cast<Match>().Take(3)
			.Select(m => LOMAssistant.Features.Story.StoryScripts.OptionText(m.Value));
		return "选项：" + string.Join(" / ", texts.ToArray());
	}

	private SaveFile ReadSave(string path)
	{
		var env = JsonConvert.DeserializeObject<Envelope>(File.ReadAllText(path, Encoding.UTF8));
		if (env == null || env.payload == null || Hash(env.payload) != env.sha256) throw new InvalidDataException("存档校验失败");
		var d = JsonConvert.DeserializeObject<SaveFile>(env.payload);
		ValidateSaveCompatibility(d);
		if (d.scene != "Story" && d.scene != "Free") throw new InvalidDataException("不支持的存档场景");
		if (d.scene == "Story" && (d.target < 0 || d.target >= d.trace.Count || string.IsNullOrEmpty(d.rootScript))) throw new InvalidDataException("剧情恢复信息不完整");
		return d;
	}

	/// <summary>读一个随时存档（文件名，在存档目录的 AnytimeSave 下）。</summary>
	public void LoadFile(string name)
	{
		if (!installed) { Notice(compatibilityFailure ?? "随时存档尚未初始化"); return; }
		if (Replaying || Restoring || pendingLoad) { Notice("正在恢复，请稍候"); return; }
		if (SceneController.Instance == null || SceneController.Instance.IsPrepare || SceneController.Instance.IsLoading) { Notice("场景切换中，暂不能读取"); return; }
		if (SceneName != "Story" && SceneName != "Free" && SceneName != "Title") { Notice("请在标题、剧情或养成界面读取"); return; }
		SaveFile data;
		try { data = ReadSave(Path.Combine(StoreRoot, name)); }
		catch (Exception ex) { Notice("无法读取：" + ex.Message); return; }
		ClearRollback();
		SaveSummary summary = Saves().FirstOrDefault(x => x.file == name);
		restoreLabel = "已读取随时存档 " + (summary != null ? DisplayName(summary) : name);
		LoadFrom(data);
	}

	// 剧本名 → 现在游戏里这个剧本的哈希（没有这个剧本是空字符串）；游戏运行中剧本不会变
	private static readonly Dictionary<string, string> currentHashes = new Dictionary<string, string>(StringComparer.Ordinal);

	/// <summary>
	/// 存档经过的剧本里，被游戏更新改过（或删掉）的第一个；都没改是 null。
	/// 只看存档记下的剧本：游戏更新大多不动已有剧情，没改到的随时存档照样能读。
	/// </summary>
	public static string ChangedScript(List<ScriptStamp> scripts)
	{
		if (scripts == null) return null;
		foreach (ScriptStamp stamp in scripts)
		{
			if (stamp?.name == null || !stamp.name.EndsWith(".LuaScript", StringComparison.Ordinal)) continue;
			if (!currentHashes.TryGetValue(stamp.name, out string now))
			{
				// 和游戏读剧本是同一个文件（Story/ChineseTraditional/剧本名），哈希算法和存档时一样
				string text = DataHelper.LoadLuaScript(stamp.name.Substring(0, stamp.name.Length - 10));
				now = text.Length > 0 ? Hash(text) : "";
				currentHashes[stamp.name] = now;
			}
			if (now != stamp.hash) return stamp.name.Substring(0, stamp.name.Length - 10);
		}
		return null;
	}

	private void LoadFrom(SaveFile data)
	{
		// 游戏更新改了存档经过的剧本时，重演到那里就对不上、只能停在半路：读之前先拦下，不进黑屏
		string changed = ChangedScript(data.scripts);
		if (changed != null)
		{
			Notice("这个随时存档经过的剧本 " + changed + " 被游戏更新改过了，读不了。请读普通存档，再重新存随时存档。", 12);
			return;
		}
		// 以前按存档那一刻的设置记「改写过」，读了没改写的旧档接着玩再存，就记错成改写过。记录里一次都没问过要不要放回选项时，
		// 改写和不改写回放出来的查询一模一样，按没改写读
		if (data.hiddenOptions && !(data.trace?.Any(e => e.kind == "query" && e.label == "lomhidden") ?? false)) data.hiddenOptions = false;
		loading = data;
		SetPanel(false); pendingLoad = true;
		LOMAssistant.Features.Story.StoryReviewPanel.Hide();
		StartCoroutine(LoadRoutine());
	}

	private IEnumerator LoadRoutine()
	{
		// 先停掉旧剧情再动数据；经标题界面换一个全新的 Lua 环境
		Faulted = false; Restoring = true; Replaying = false; Time.timeScale = 1;
		DiceManualSelectionPatch.IsClicked = false;
		foreach (var env in UObject.FindObjectsOfType<LuaEnvironment>()) env.StopAllCoroutines();
		// 已读记录（对话变黄）只在内存里，换场景时游戏会从磁盘重新读一遍；原版读档前会先存一次，这里也先存，
		// 不然这一段刚读过的对话（比如存档后选了一个选项看完）读回来又都成了没读过
		SaveSystem.Instance.SaveUniverseData();
		SceneController.Instance.LoadTitle();
		float start = Time.realtimeSinceStartup;
		while (SceneName != "Title" || SceneController.Instance.IsPrepare || SceneController.Instance.IsLoading)
		{ if (Time.realtimeSinceStartup - start > 30) { Fail("标题场景加载超时"); yield break; } yield return null; }
		yield return null;
		Apply(loading, false);
		trace = new List<Entry>(loading.trace); stamps = new List<ScriptStamp>(loading.scripts);
		rootScript = loading.rootScript; target = loading.target; cursor = 0; active = -1;
		Replaying = loading.scene == "Story";
		replayStarted = Time.realtimeSinceStartup;
		// 原版读档界面进场景前会先停掉所有音乐，剧本里的 PlayMusic 才能接上；不停的话标题音乐会一直留着
		SoundManager.Instance.StopMusic();
		SceneController.Instance.LoadNewScene(loading.scene);
		pendingLoad = false;
		if (loading.scene == "Free")
		{
			while (SceneName != "Free" || SceneController.Instance.IsLoading || SceneController.Instance.IsPrepare) yield return null;
			start = Time.realtimeSinceStartup;
			while (Mortal.Free.FreeManager.Instance == null || Mortal.Free.FreeManager.Instance.IsProcessing || !Mortal.Free.FreeManager.Instance.EnableHotKey)
			{ if (Time.realtimeSinceStartup - start > 30) { Fail("养成界面初始化超时"); yield break; } yield return null; }
			// 养成界面初始化会消耗随机数，原生读档还会留下上一段剧情的入口；等地图交还操作后再恢复
			var savedGame = JsonUtility.FromJson<GameSave>(loading.gameJson);
			PlayerStatManagerData.Instance.SetStartScript(savedGame.StartStoryScript);
			RestoreFreeRng(loading.freeRng);
			UnityEngine.Random.state = JsonUtility.FromJson<UnityEngine.Random.State>(loading.randomJson);
			RestoreSound(loading);
			Restoring = false; Notice(restoreLabel);
		}
	}

	private bool replayMuted;

	/// <summary>
	/// 重演时静音。动的是总音量 RTPC：剧本的淡入淡出用 BackgroundMusic、EnvSound，互不影响，RestoreSound 照常工作。
	/// 只看 Replaying，正常结束、失败、超时都走这里；设置里的音量不改，半路退游戏也不会留下静音。
	/// </summary>
	private void UpdateReplayMute()
	{
		if (Replaying != replayMuted)
		{
			replayMuted = Replaying;
			AkSoundEngine.SetRTPCValue("MasterVolume", replayMuted ? 0 : SystemSettings.MasterVolume, null, replayMuted ? 0 : 300);
		}
	}

	private void Update()
	{
		UpdateReplayMute();
		if (Replaying)
		{
			Time.timeScale = 30;
			if (Time.realtimeSinceStartup - replayStarted > 180) Fail("恢复超时");
		}
		UpdateHotkeys();
	}

	private void SetPanel(bool value)
	{
		if (panel == value) return;
		panel = value;
		if (value)
		{
			panelTimeScale = Time.timeScale;
			pausedActions.Clear();
			foreach (var asset in Resources.FindObjectsOfTypeAll<InputActionAsset>())
				foreach (var map in asset.actionMaps)
					foreach (var action in map.actions) if (action.enabled) pausedActions.Add(action);
			foreach (var action in pausedActions) action.Disable();
			Time.timeScale = 0;
		}
		else
		{
			foreach (var action in pausedActions) action.Enable();
			pausedActions.Clear(); Time.timeScale = panelTimeScale;
		}
	}

	/// <summary>
	/// 读档快进时同一角色短时间内连续换表情，几个淡入淡出叠在一起，旧表情可能没被隐藏、留在台上。
	/// 恢复到位后等淡入淡出结束，每个角色只留当前那张立绘，已下台的角色整组隐藏。
	/// </summary>
	private IEnumerator TidyPortraits(List<string> keep)
	{
		yield return new WaitForSecondsRealtime(0.6f);
		try
		{
			var current = new HashSet<Image>();
			var sprites = new HashSet<Sprite>();
			int hidden = 0;
			foreach (var character in UObject.FindObjectsOfType<Character>())
			{
				var state = character.State;
				if (state == null || state.allPortraits == null) continue;
				foreach (var image in state.allPortraits)
				{
					if (image == null) continue;
					if (image.sprite != null) sprites.Add(image.sprite);
					if (state.onScreen && image == state.portraitImage) { current.Add(image); continue; }
					if (!image.gameObject.activeSelf) continue;
					LeanTween.cancel(image.gameObject);
					image.gameObject.SetActive(false);
					hidden++;
				}
			}
			// 特效（如受惊抖动）会复制一份立绘，快进时可能没删掉：画布上用的是角色立绘图、却不是任何角色当前那张的，也隐藏
			var visible = new List<string>();
			foreach (var stage in Stage.ActiveStages)
			{
				if (stage == null || stage.PortraitCanvas == null) continue;
				foreach (var image in stage.PortraitCanvas.GetComponentsInChildren<Image>())
				{
					if (current.Contains(image) || image.sprite == null || !sprites.Contains(image.sprite))
					{
						if (image.sprite != null && sprites.Contains(image.sprite)) visible.Add(image.sprite.name + "@" + image.transform.parent?.name);
						continue;
					}
					LeanTween.cancel(image.gameObject);
					image.gameObject.SetActive(false);
					hidden++;
				}
			}
			if (keep != null)
			{
				var keepSet = new HashSet<string>(keep);
				// 存档时不在台上的角色整个撤下。不能只看现在看不看得见：快进时退场的淡出没走完、留在台上但透明的角色，
				// 之后每句对白调亮、调暗台上角色（Stage 的 DimPortraits）都会把颜色设回不透明，人就莫名冒出来了
				foreach (var character in UObject.FindObjectsOfType<Character>())
				{
					var state = character.State;
					if (keepSet.Contains(character.name) || state == null || !(state.onScreen || IsVisible(character))) continue;
					foreach (var image in state.allPortraits ?? new List<Image>())
					{
						if (image == null) continue;
						LeanTween.cancel(image.gameObject);
						if (!image.gameObject.activeSelf) continue;
						image.gameObject.SetActive(false);
						hidden++;
					}
					state.onScreen = false;
					foreach (var stage in Stage.ActiveStages)
					{
						if (stage != null) stage.CharactersOnStage.Remove(character);
					}
					FileLogger.Log("[随时存档] 存档时不在台上，撤下：" + character.name);
				}
			}
			FileLogger.Log("[随时存档] 读档后台上立绘：" + string.Join("，", visible.ToArray()) + (hidden > 0 ? "；清理了 " + hidden + " 张残留" : ""));
		}
		catch (Exception e) { FileLogger.LogWarning("[随时存档] 清理立绘失败: " + e.Message); }
	}

	/// <summary>台上实际看得见：在台上、当前立绘激活且不透明。很多角色退场只是移到画面外或淡到透明，不会改 onScreen。</summary>
	private static bool IsVisible(Character character)
	{
		var state = character.State;
		if (state == null || !state.onScreen || state.portraitImage == null || state.holder == null) return false;
		if (!state.portraitImage.gameObject.activeInHierarchy || state.portraitImage.color.a < 0.05f) return false;
		var canvas = state.holder.GetComponentInParent<Canvas>();
		if (canvas == null) return true;
		var corners = new Vector3[4];
		state.portraitImage.rectTransform.GetWorldCorners(corners);
		var area = (canvas.transform as RectTransform);
		var areaCorners = new Vector3[4];
		area.GetWorldCorners(areaCorners);
		// 和画布没有交集就是在画面外
		return corners[2].x > areaCorners[0].x && corners[0].x < areaCorners[2].x && corners[2].y > areaCorners[0].y && corners[0].y < areaCorners[2].y;
	}

	internal static List<string> VisibleCharacters()
	{
		var list = new List<string>();
		try
		{
			foreach (var character in UObject.FindObjectsOfType<Character>())
				if (IsVisible(character)) list.Add(character.name);
		}
		catch (Exception e) { FileLogger.LogWarning("[随时存档] 记录台上角色失败: " + e.Message); }
		return list;
	}

	internal static string CurrentSound(string field)
	{
		try { return SoundManager.Instance == null ? null : Traverse.Create(SoundManager.Instance).Field(field).GetValue<string>(); }
		catch { return null; }
	}

	/// <summary>Wwise 里音乐（BackgroundMusic）、环境音（EnvSound）的全局音量，0~100；读不到是 -1。</summary>
	internal static float CurrentVolume(string rtpc)
	{
		try
		{
			int type = 1; // 全局值
			return AkSoundEngine.GetRTPCValue(rtpc, null, 0u, out float value, ref type) == AKRESULT.AK_Success ? value : -1;
		}
		catch { return -1; }
	}

	private static void RestoreSound(SaveFile data)
	{
		try
		{
			if (!string.IsNullOrEmpty(data.music)) SoundManager.Instance.PlayMusic(data.music);
			if (!string.IsNullOrEmpty(data.envSound)) SoundManager.Instance.PlayEnvSound(data.envSound);
			if (data.musicVolume >= 0) AkSoundEngine.SetRTPCValue("BackgroundMusic", data.musicVolume);
			if (data.envVolume >= 0) AkSoundEngine.SetRTPCValue("EnvSound", data.envVolume);
		}
		catch (Exception e) { FileLogger.LogWarning("[随时存档] 恢复音乐失败: " + e.Message); }
	}

	private static object Field(object o, string n) => AccessTools.Field(o.GetType(), n).GetValue(o);
	private static void SetField(object o, string n, object v) => AccessTools.Field(o.GetType(), n).SetValue(o, v);
	private static bool ActiveField(object o, string n)
	{
		var v = Field(o, n);
		return v is Component c ? c.gameObject.activeInHierarchy : v is GameObject g && g.activeInHierarchy;
	}

	private DiceState CaptureDice()
	{
		var d = UObject.FindObjectOfType<DiceMenuDialog>();
		if (d == null) return new DiceState();
		return new DiceState
		{
			valid = true, resultReady = ActiveField(d, "_continueButton"), useFate = (bool)Field(d, "_useFatePoint"),
			random = (int)Field(d, "_currentRandomValue"), selection = (int)Field(d, "_resultSelection"), count = (int)Field(d, "_resultCount"),
			min = (int)Field(d, "_diceMin"), max = (int)Field(d, "_diceMax"), defaultMax = (int)Field(d, "_defaultDiceMax"), rangeIndex = (int)Field(d, "_currentDiceRangeIndex")
		};
	}

	private FreeRng CaptureFreeRng()
	{
		var r = (System.Random)Field(Mortal.Free.FreeManager.Instance, "_rand");
		return new FreeRng { next = (int)Field(r, "inext"), nextp = (int)Field(r, "inextp"), seed = (int[])((int[])Field(r, "SeedArray")).Clone() };
	}

	private void RestoreFreeRng(FreeRng data)
	{
		if (data == null) return;
		if (data.seed == null || data.seed.Length != 56) throw new InvalidDataException("养成随机状态无效");
		var r = new System.Random(0);
		SetField(r, "inext", data.next); SetField(r, "inextp", data.nextp); SetField(r, "SeedArray", (int[])data.seed.Clone());
		SetField(Mortal.Free.FreeManager.Instance, "_rand", r);
	}

	/// <summary>读档回到「骰子结果已出、等待确认」时，替换 Roll 协程直接摆出保存的结果，不重新掷。</summary>
	internal static bool RestoreRoll(DiceMenuDialog d, ref IEnumerator result)
	{
		var p = Instance;
		if (p == null || p.pendingDice == null || !p.pendingDice.valid || !p.pendingDice.resultReady) return true;
		var snap = p.pendingDice; p.pendingDice = null;
		result = p.RestoredRoll(d, snap); return false;
	}

	internal static bool AutoStartRestoredDice(ref bool result)
	{
		if (Instance != null && Instance.pendingDice != null && Instance.pendingDice.valid && Instance.pendingDice.resultReady)
		{ result = true; return false; }
		return true;
	}

	private IEnumerator RestoredRoll(DiceMenuDialog d, DiceState s)
	{
		SetField(d, "_currentRandomValue", s.random); SetField(d, "_resultSelection", s.selection); SetField(d, "_resultCount", s.count);
		SetField(d, "_diceMin", s.min); SetField(d, "_diceMax", s.max); SetField(d, "_defaultDiceMax", s.defaultMax); SetField(d, "_currentDiceRangeIndex", s.rangeIndex);
		SetField(d, "_useFatePoint", s.useFate);
		AccessTools.Method(typeof(DiceMenuDialog), "UpdateDiceText").Invoke(d, new object[] { s.min, s.max });
		AccessTools.Method(typeof(DiceMenuDialog), "UpdateFatePointItem").Invoke(d, null);
		yield return (IEnumerator)AccessTools.Method(typeof(DiceMenuDialog), "StartSpin").Invoke(d, new object[] { 0f });
		((GameObject)Field(d, "_continuePanel")).SetActive(true);
		((Component)Field(d, "_continueButton")).gameObject.SetActive(true);
	}
}

/// <summary>随时存档用到的游戏补丁。只在功能启用且版本核验通过时应用。</summary>
public static class AnytimeSavePatches
{
	// 回放可能走到一个选项菜单，但菜单从没激活过、按钮缓存还是空的；原版这里会空引用，导致状态页其它分页关不掉
	[HarmonyPrefix]
	[HarmonyPatch(typeof(CustomMenuDialog), "InteractableCacheButton")]
	public static bool InteractableCacheButton_Prefix(CustomMenuDialog __instance, bool interactable)
	{
		var buttons = __instance.CachedButtons;
		if (buttons == null) return false;
		foreach (var button in buttons)
			if (button != null) button.enabled = interactable;
		return false;
	}

	// 回放和恢复中不写原版存档、不删档、不解成就
	[HarmonyPrefix]
	[HarmonyPatch(typeof(SaveSystem), "ExecuteSaveData")]
	public static bool ExecuteSaveData_Prefix() => AllowWrite();

	[HarmonyPrefix]
	[HarmonyPatch(typeof(SaveSystem), "ExecuteSaveJsonData")]
	public static bool ExecuteSaveJsonData_Prefix() => AllowWrite();

	[HarmonyPrefix]
	[HarmonyPatch(typeof(SaveSystem), "SetAchivement")]
	public static bool SetAchivement_Prefix() => AllowWrite();

	[HarmonyPrefix]
	[HarmonyPatch(typeof(SaveSystem), "DeleteSaveData")]
	public static bool DeleteSaveData_Prefix() => AllowWrite();

	private static bool AllowWrite() => !AnytimeSave.Replaying && !AnytimeSave.Restoring && !AnytimeSave.Faulted;

	// 排在本 Mod 其它 LoadLuaFunction 补丁之后，让信息窗口、选项提示解析的还是原始剧本
	[HarmonyPrefix]
	[HarmonyPriority(Priority.Last)]
	[HarmonyPatch(typeof(LuaEnvironment), "LoadLuaFunction")]
	public static void LoadLuaFunction_Prefix(LuaEnvironment __instance, ref string luaString, string friendlyName)
	{
		if (AnytimeSave.Instance == null) return;
		var timer = System.Diagnostics.Stopwatch.StartNew();
		luaString = AnytimeSave.Instance.PrepareScript(__instance, luaString, friendlyName);
		LuaLoadTiming.AnytimeMs += timer.ElapsedMilliseconds;
	}

	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	[HarmonyPatch(typeof(DiceMenuDialog), "Roll")]
	public static bool Roll_Prefix(DiceMenuDialog __instance, ref IEnumerator __result) => AnytimeSave.RestoreRoll(__instance, ref __result);

	[HarmonyPrefix]
	[HarmonyPatch(typeof(DiceMenuDialog), "GetPressInput")]
	public static bool GetPressInput_Prefix(ref bool __result) => AnytimeSave.AutoStartRestoredDice(ref __result);
}
