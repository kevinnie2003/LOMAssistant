using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Core;
using Mortal.Story;
using Newtonsoft.Json;
using UnityEngine;

namespace LOMAssistant.Features.Story;

/// <summary>收录的一个剧情节点：某个有分岔的剧本开始时的进度。</summary>
public sealed class StoryNode
{
	public string Id = "";
	public string Script = "";
	public int Year, Month, Stage;
	/// <summary>收录时的主线任务说明（第一行）。</summary>
	public string Mission = "";
	public string Music = "", Env = "";
	public DateTime Saved;
	/// <summary>收录时所在的进度线；空 = 分线以前收录的，所有存档共用。</summary>
	public string Line = "";
	/// <summary>收录的先后顺序（全局递增），判断某个存档分出来时这个节点有没有收录过。</summary>
	public long Seq;
}

/// <summary>
/// 进度线：开新档或读一次档，就从读进来的那一点分出一条新线。线记着从哪条线的第几个节点分出来，
/// 分出来以前那条线上的节点新线也看得到，分出来以后各收录各的。
/// </summary>
public sealed class StoryLine
{
	public string Parent = "";
	public long Cut;
}

/// <summary>进度线的存盘内容：所有线，以及每份存档（按游戏写进存档里的写入时间）当时在哪条线的第几个节点。</summary>
public sealed class StoryLineage
{
	public long Seq;
	public Dictionary<string, StoryLine> Lines = new Dictionary<string, StoryLine>();
	public Dictionary<long, StoryMark> Marks = new Dictionary<long, StoryMark>();
}

public sealed class StoryMark
{
	public string Line = "";
	public long Seq;
}

/// <summary>
/// 剧情回顾的收录和存入。
/// 收录：游戏自动存档时（剧情的自动存档点、每旬开头的养成；战斗前的不收），取游戏此刻要写的存档，存进游戏存档文件夹（跟着 Steam 云同步）。
/// 收录按存档分开：每份存档自己一条进度线（见 StoryLine），每条线上每个剧本只留最新一份。
/// 游戏写存档时会把写入时间（TimeTick）一起存进去，复制到别的格子、自动存档、随时存档都原样带着；
/// 读档时按它找回这份进度在哪条线、走到第几个节点，就不用改存档文件本身。
/// 重玩：用游戏自己的存档写进专用存档位（写的内容换成节点的存档）再读进来，就从这个剧本开头重玩，读档后补放当时的背景音乐。
/// </summary>
public static class StoryNodes
{
	private static readonly MethodInfo CreateSave = AccessTools.Method(typeof(SaveSystem), "CreateSaveData");

	private static readonly Dictionary<string, StoryNode> Nodes = new Dictionary<string, StoryNode>(StringComparer.OrdinalIgnoreCase);
	/// <summary>节点写过的存档位 → 补放的音乐。</summary>
	private static readonly Dictionary<string, StoryNode> Exported = new Dictionary<string, StoryNode>(StringComparer.Ordinal);
	private static bool _loaded;
	private static GameSave _writing;
	private static StoryNode _writingNode;
	private static bool _justLoaded;
	private static StoryLineage _lineage = new StoryLineage();
	/// <summary>当前进度所在的线；还没读档、开新档（标题界面）时为 null。</summary>
	private static string _line;
	private const int MaxMarks = 5000;

	/// <summary>每次有改动加一，面板据此刷新列表。</summary>
	public static int Version { get; private set; }

	// 都放在游戏存档文件夹的最外层，跟着 Steam 云同步（Steam 只同步这一层，不同步子文件夹）；统一用 LOMStory_ 开头
	private static string Folder => AnytimeSave.AnytimeSave.SaveRoot;
	private static string IndexFile => Path.Combine(Folder, "LOMStory_nodes.json");
	private static string ExportFile => Path.Combine(Folder, "LOMStory_exports.json");
	private static string LineageFile => Path.Combine(Folder, "LOMStory_lineage.json");
	private static string SaveFileOf(string id) => Path.Combine(Folder, "LOMStory_" + System.Text.RegularExpressions.Regex.Replace(id, "[^A-Za-z0-9_]", "_") + ".gz");
	/// <summary>以前放在 Mod 文件夹里（不同步），第一次读的时候搬过来。</summary>
	private static string OldFolder => Path.Combine(Path.GetDirectoryName(typeof(StoryNodes).Assembly.Location) ?? ".", "StoryReview");

	public static IEnumerable<StoryNode> All
	{
		get
		{
			Load();
			return Nodes.Values.ToList();
		}
	}

	/// <summary>当前存档看得到的节点：自己这条线的，加上祖先线在分出来以前收录的，加上分线以前的老节点。还没读档时全部都算。</summary>
	private static List<StoryNode> _visible;
	private static int _visibleVersion = -1;
	private static string _visibleLine;

	public static List<StoryNode> Visible
	{
		get
		{
			Load();
			if (_visible != null && _visibleVersion == Version && _visibleLine == _line)
			{
				return _visible;
			}
			_visibleVersion = Version;
			_visibleLine = _line;
			return _visible = ComputeVisible();
		}
	}

	private static List<StoryNode> ComputeVisible()
	{
		{
			if (_line == null)
			{
				return Nodes.Values.ToList();
			}
			var limit = new Dictionary<string, long>(StringComparer.Ordinal);
			string line = _line;
			long cap = long.MaxValue;
			for (int depth = 0; line != null && depth < 500; depth++)
			{
				if (!limit.TryGetValue(line, out long known) || cap > known)
				{
					limit[line] = cap;
				}
				if (!_lineage.Lines.TryGetValue(line, out StoryLine info) || info.Parent.Length == 0)
				{
					break;
				}
				cap = Math.Min(cap, info.Cut);
				line = info.Parent;
			}
			return Nodes.Values.Where(n => n.Line.Length == 0 || (limit.TryGetValue(n.Line, out long lim) && n.Seq <= lim)).ToList();
		}
	}

	/// <summary>有没有读进来的存档（标题界面时没有）。</summary>
	public static bool HasCurrentSave => _line != null;

	/// <summary>当前存档看得到的、这个剧本最新的一份，没有返回 null。</summary>
	public static StoryNode Latest(string script)
	{
		return Visible.Where(n => string.Equals(n.Script, script ?? "", StringComparison.OrdinalIgnoreCase)).OrderByDescending(n => n.Seq).FirstOrDefault();
	}

	private static void Load()
	{
		// 存档系统还没起来就不知道存档文件夹在哪，等它好了再读
		if (_loaded || SaveSystem.Instance == null)
		{
			return;
		}
		_loaded = true;
		try
		{
			MoveOldFiles();
			if (File.Exists(IndexFile))
			{
				foreach (StoryNode node in JsonConvert.DeserializeObject<List<StoryNode>>(File.ReadAllText(IndexFile, Encoding.UTF8)) ?? new List<StoryNode>())
				{
					if (File.Exists(SaveFileOf(node.Id)))
					{
						Nodes[node.Id] = node;
					}
				}
			}
			if (File.Exists(LineageFile))
			{
				_lineage = JsonConvert.DeserializeObject<StoryLineage>(File.ReadAllText(LineageFile, Encoding.UTF8)) ?? new StoryLineage();
			}
			if (File.Exists(ExportFile))
			{
				foreach (var pair in JsonConvert.DeserializeObject<Dictionary<string, StoryNode>>(File.ReadAllText(ExportFile, Encoding.UTF8)) ?? new Dictionary<string, StoryNode>())
				{
					Exported[pair.Key] = pair.Value;
				}
			}
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[剧情回顾] 读收录列表失败: " + ex.Message);
		}
	}

	private static void MoveOldFiles()
	{
		if (!Directory.Exists(OldFolder))
		{
			return;
		}
		try
		{
			void Move(string from, string to)
			{
				if (File.Exists(from) && !File.Exists(to))
				{
					File.Move(from, to);
				}
			}
			Move(Path.Combine(OldFolder, "nodes.json"), IndexFile);
			Move(Path.Combine(OldFolder, "exports.json"), ExportFile);
			Move(Path.Combine(OldFolder, "lineage.json"), LineageFile);
			string saves = Path.Combine(OldFolder, "saves");
			if (Directory.Exists(saves) && File.Exists(IndexFile))
			{
				foreach (StoryNode node in JsonConvert.DeserializeObject<List<StoryNode>>(File.ReadAllText(IndexFile, Encoding.UTF8)) ?? new List<StoryNode>())
				{
					Move(Path.Combine(saves, node.Id.Replace('@', '_').Replace('/', '_') + ".json.gz"), SaveFileOf(node.Id));
				}
			}
			FileLogger.Log("[剧情回顾] 收录节点已搬到存档文件夹（跟着 Steam 云同步）");
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[剧情回顾] 搬旧节点失败: " + ex.Message);
		}
	}

	private static void SaveIndex()
	{
		Directory.CreateDirectory(Folder);
		File.WriteAllText(IndexFile, JsonConvert.SerializeObject(Nodes.Values.ToList(), Formatting.Indented), new UTF8Encoding(false));
		File.WriteAllText(ExportFile, JsonConvert.SerializeObject(Exported, Formatting.Indented), new UTF8Encoding(false));
		SaveLineage();
		Version++;
	}

	private static void SaveLineage()
	{
		try
		{
			Directory.CreateDirectory(Folder);
			if (_lineage.Marks.Count > MaxMarks)
			{
				foreach (long old in _lineage.Marks.Keys.OrderBy(k => k).Take(_lineage.Marks.Count - MaxMarks).ToList())
				{
					_lineage.Marks.Remove(old);
				}
			}
			File.WriteAllText(LineageFile, JsonConvert.SerializeObject(_lineage), new UTF8Encoding(false));
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[剧情回顾] 保存进度线失败: " + ex.Message);
		}
	}

	/// <summary>开一条新线；parent 为空表示新的一局（或认不出来历的存档）。</summary>
	private static void StartLine(string parent, long cut)
	{
		Load();
		string id = Guid.NewGuid().ToString("N").Substring(0, 12);
		_lineage.Lines[id] = new StoryLine { Parent = parent ?? "", Cut = cut };
		_line = id;
		Version++;
	}

	/// <summary>游戏读进一份存档：按它的写入时间找回来历，从那一点分出新线。</summary>
	internal static void SaveLoading(GameSave save)
	{
		if (save == null)
		{
			return;
		}
		Load();
		if (_lineage.Marks.TryGetValue(save.TimeTick, out StoryMark mark))
		{
			StartLine(mark.Line, mark.Seq);
		}
		else
		{
			StartLine("", 0);
		}
	}

	internal static void NewGame()
	{
		StartLine("", 0);
	}

	/// <summary>游戏造了一份存档（手动存、自动存、随时存档取快照都会走这里）：记下它此刻在哪条线的第几个节点。</summary>
	internal static void SaveCreated(GameSave save)
	{
		if (save == null || _line == null || _writing != null)
		{
			return;
		}
		_lineage.Marks[save.TimeTick] = new StoryMark { Line = _line, Seq = _lineage.Seq };
	}

	/// <summary>存档写到硬盘以后把对应关系也存下来。</summary>
	internal static void Flush()
	{
		if (_loaded)
		{
			SaveLineage();
		}
	}

	/// <summary>剧本加载时（还没执行）调用：刚读过档就补放音乐。</summary>
	public static void ScriptLoaded(string luaString, string friendlyName)
	{
		try
		{
			string name = friendlyName ?? "";
			if (!name.EndsWith(".LuaScript", StringComparison.Ordinal) || name.StartsWith("tmp", StringComparison.Ordinal))
			{
				return;
			}
			name = name.Substring(0, name.Length - ".LuaScript".Length);
			PlayerStatManagerData player = PlayerStatManagerData.Instance;
			if (player == null || SaveSystem.Instance == null || StoryManager.Instance == null
				|| !string.Equals(player.CurrentStoryScript, name, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}
			if (_justLoaded)
			{
				_justLoaded = false;
				RestoreMusic(name);
			}
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[剧情回顾] 收录失败: " + ex.Message);
		}
	}

	/// <summary>
	/// 游戏自动存档时收录一份：剧情里的自动存档点、每旬开头的养成（战斗开打前的自动存档不收）。
	/// 起始剧本就是游戏这次自动存档记下的那一个，重玩时和读自动存档一样从它开头开始。
	/// </summary>
	internal static void AutoSaved()
	{
		try
		{
			PlayerStatManagerData player = PlayerStatManagerData.Instance;
			if (player == null || SaveSystem.Instance == null || !ConfigManager.EnableStoryReview.Value || AnytimeSave.AnytimeSave.Replaying || AnytimeSave.AnytimeSave.Restoring)
			{
				return;
			}
			string script = player.GetFieldValue<string>("_startStoryScript");
			if (!string.IsNullOrEmpty(script))
			{
				Keep(script);
			}
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[剧情回顾] 收录失败: " + ex.Message);
		}
	}

	private static void Keep(string script)
	{
		Load();
		if (_line == null)
		{
			StartLine("", 0);
		}
		// 读档后重走：祖先线上这一段、同一个时间点已经收录过，就用那一份，不再另存
		PlayerStatManagerData player = PlayerStatManagerData.Instance;
		GameTime now = player != null ? player.GetFieldValue<GameTimeData>("_gameTimeData")?.GameTime : null;
		if (!ReferenceEquals(now, null) && Visible.Any(n => n.Line != _line && string.Equals(n.Script, script, StringComparison.OrdinalIgnoreCase)
			&& n.Year == now.Year && n.Month == now.Month && n.Stage == (int)now.Stage))
		{
			return;
		}
		// 1.0.6001 起多了可选参数 includeStoryLog（回想记录要不要写进存档），反射调用要把它传上
		object[] args = CreateSave.GetParameters().Length == 0 ? null : new object[] { true };
		if (!(CreateSave.Invoke(SaveSystem.Instance, args) is GameSave save))
		{
			return;
		}
		save.StartStoryScript = script;
		// 同一条线上这个剧本只留最新一份
		foreach (StoryNode old in Nodes.Values.Where(n => n.Line == _line && string.Equals(n.Script, script, StringComparison.OrdinalIgnoreCase)).ToList())
		{
			Nodes.Remove(old.Id);
			if (File.Exists(SaveFileOf(old.Id)))
			{
				File.Delete(SaveFileOf(old.Id));
			}
		}
		string id = _line + "_" + script;
		WriteSave(id, save);
		var main = MissionManagerData.Instance != null ? MissionManagerData.Instance.MainCheckData : null;
		SoundManager sound = SoundManager.Instance;
		Nodes[id] = new StoryNode
		{
			Id = id,
			Script = script,
			Line = _line,
			Seq = ++_lineage.Seq,
			Year = save.CurrentYear,
			Month = save.CurrentMonth,
			Stage = (int)save.CurrentStage,
			Mission = main != null ? FirstLine(TextConverter.ToSimplified(main.GetFieldValue<string>("_desc") ?? "")) : "",
			Music = sound != null ? sound.GetFieldValue<string>("_currentMusic") ?? "" : "",
			Env = sound != null ? sound.GetFieldValue<string>("_currentEnvSound") ?? "" : "",
			Saved = DateTime.Now
		};
		SaveIndex();
	}

	private static string FirstLine(string text)
	{
		string first = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
		return first.Length > 40 ? first.Substring(0, 40) + "…" : first;
	}

	private static void WriteSave(string id, GameSave save)
	{
		string path = SaveFileOf(id);
		Directory.CreateDirectory(Path.GetDirectoryName(path));
		byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(save));
		using (FileStream file = File.Create(path))
		using (var zip = new GZipStream(file, CompressionMode.Compress))
		{
			zip.Write(bytes, 0, bytes.Length);
		}
	}

	private static GameSave ReadSave(string id)
	{
		using (FileStream file = File.OpenRead(SaveFileOf(id)))
		using (var zip = new GZipStream(file, CompressionMode.Decompress))
		using (var reader = new StreamReader(zip, Encoding.UTF8))
		{
			return JsonUtility.FromJson<GameSave>(reader.ReadToEnd());
		}
	}

	public static void Delete(StoryNode node)
	{
		Load();
		Nodes.Remove(node.Id);
		if (File.Exists(SaveFileOf(node.Id)))
		{
			File.Delete(SaveFileOf(node.Id));
		}
		SaveIndex();
	}

	/// <summary>重玩用的存档位，不是数字，存档、读档页里不会出现。</summary>
	public const string ReplaySlot = "replay";

	/// <summary>
	/// 从这个节点重玩：把节点写进专用存档位，再照读档页的做法读进来，剧情从这个剧本开头重新开始。
	/// 返回给玩家看的结果；成功时为空。
	/// </summary>
	public static string Replay(StoryNode node)
	{
		if (SaveSystem.Instance == null || SceneController.Instance == null || MissionManagerData.Instance == null)
		{
			return "读档进入游戏后才能重玩";
		}
		if (SceneController.Instance.IsPrepare || SceneController.Instance.IsLoading)
		{
			return "正在切换场景，稍后再试";
		}
		if (AnytimeSave.AnytimeSave.Replaying || AnytimeSave.AnytimeSave.Restoring)
		{
			return "正在读取随时存档，稍后再试";
		}
		GameSave save;
		try
		{
			save = ReadSave(node.Id);
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[剧情回顾] 读节点失败: " + ex.Message);
			return "节点文件读不出来";
		}
		save.TimeTick = DateTime.Now.Ticks;
		// 读进来以后从这个节点所在的线、这个节点那一刻分出新线
		Load();
		_lineage.Marks[save.TimeTick] = new StoryMark { Line = node.Line, Seq = node.Line.Length == 0 ? 0 : node.Seq };
		_writing = save;
		_writingNode = node;
		try
		{
			SaveSystem.Instance.SaveGameData(ReplaySlot);
		}
		finally
		{
			_writing = null;
			_writingNode = null;
		}
		FileLogger.Log($"[剧情回顾] 从 {node.Id} 重玩");
		SaveSystem.Instance.SetSlot(ReplaySlot);
		SoundManager.Instance?.StopMusic();
		SaveSystem.Instance.LoadGameData();
		SaveSystem.Instance.SaveUniverseData();
		MissionManagerData.Instance.UpdateCheckMissions();
		SceneController.Instance.LoadCurrentScene();
		return "";
	}

	internal static void NameReplaySlot(string slot, UnityEngine.UI.Text text)
	{
		if (text != null && slot == ReplaySlot)
		{
			text.text = TextConverter.ForDisplay("剧情回顾");
		}
	}

	internal static bool CreateSaveData_Prefix(ref GameSave __result)
	{
		if (_writing == null)
		{
			return true;
		}
		__result = _writing;
		return false;
	}

	/// <summary>每次手动存档后：节点写的格子记下要补放的音乐，玩家自己存的格子去掉记录。</summary>
	internal static void SaveGameData_Postfix(string slot)
	{
		try
		{
			Load();
			SaveLineage();
			bool changed = Exported.Remove(slot);
			if (_writingNode != null && (_writingNode.Music.Length > 0 || _writingNode.Env.Length > 0))
			{
				Exported[slot] = _writingNode;
				changed = true;
			}
			if (changed)
			{
				SaveIndex();
			}
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[剧情回顾] 记录音乐失败: " + ex.Message);
		}
	}

	internal static void Loaded() => _justLoaded = true;

	private static void RestoreMusic(string script)
	{
		Load();
		string slot = SaveSystem.Instance.CurrentSlot;
		if (slot == null || !Exported.TryGetValue(slot, out StoryNode node) || !string.Equals(node.Script, script, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		SoundManager sound = SoundManager.Instance;
		if (sound == null)
		{
			return;
		}
		if (node.Music.Length > 0)
		{
			sound.PlayMusic(node.Music);
		}
		if (node.Env.Length > 0)
		{
			sound.PlayEnvSound(node.Env);
		}
		FileLogger.Log($"[剧情回顾] 第 {slot} 格读档，补放音乐 {node.Music} {node.Env}");
	}
}

internal static class StoryNodePatches
{
	[HarmonyPrefix]
	[HarmonyPatch(typeof(Fungus.LuaEnvironment), "LoadLuaFunction")]
	public static void LoadLuaFunction_Prefix(string luaString, string friendlyName) => StoryNodes.ScriptLoaded(luaString, friendlyName);

	[HarmonyPrefix]
	[HarmonyPriority(Priority.First)]
	[HarmonyPatch(typeof(SaveSystem), "CreateSaveData")]
	public static bool CreateSaveData_Prefix(ref GameSave __result) => StoryNodes.CreateSaveData_Prefix(ref __result);

	[HarmonyPostfix]
	[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.SaveGameData), new[] { typeof(string) })]
	public static void SaveGameData_Postfix(string slot) => StoryNodes.SaveGameData_Postfix(slot);

	[HarmonyPostfix]
	[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.LoadGameData))]
	public static void LoadGameData_Postfix() => StoryNodes.Loaded();

	[HarmonyPostfix]
	[HarmonyPatch(typeof(SaveSystem), "CreateSaveData")]
	public static void CreateSaveData_Postfix(GameSave __result) => StoryNodes.SaveCreated(__result);

	[HarmonyPostfix]
	[HarmonyPatch(typeof(SaveSystem), "AutoSaveData")]
	public static void AutoSaveData_Postfix(string slot)
	{
		// 战斗开打前的自动存档（auto_battle）不收录，只收剧情（auto）和养成（auto_free）
		if (slot != "auto_battle")
		{
			StoryNodes.AutoSaved();
		}
		StoryNodes.Flush();
	}

	// 读档页的「最近存档」和自动存档卡片：重玩用的存档位显示成「剧情回顾」，不显示「传奇replay」
	[HarmonyPostfix]
	[HarmonyPatch(typeof(RecentSaveSlotPanel), nameof(RecentSaveSlotPanel.Setup))]
	public static void RecentSlot_Postfix(RecentSaveSlotPanel __instance) => StoryNodes.NameReplaySlot(__instance.Slot, __instance.GetFieldValue<UnityEngine.UI.Text>("_slotText"));

	[HarmonyPostfix]
	[HarmonyPatch(typeof(AutoSaveSlotPanel), nameof(AutoSaveSlotPanel.Setup))]
	public static void AutoSlot_Postfix(AutoSaveSlotPanel __instance) => StoryNodes.NameReplaySlot(__instance.GetFieldValue<AutoGameSave>("_currentData")?.Slot, __instance.GetFieldValue<UnityEngine.UI.Text>("_slotText"));

	// 读档、随时存档恢复、回到上一次的进度，都走这里
	[HarmonyPrefix]
	[HarmonyPatch(typeof(SaveSystem), "ExecuteLoadGameData")]
	public static void ExecuteLoadGameData_Prefix(GameSave gameSave) => StoryNodes.SaveLoading(gameSave);

	[HarmonyPostfix]
	[HarmonyPatch(typeof(CheckPointManager), "Awake")]
	public static void CheckPointManagerAwake_Postfix(CheckPointManager __instance) => DataHelper.RememberCheckpoints(__instance);

	[HarmonyPostfix]
	[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.NewGameData))]
	public static void NewGameData_Postfix() => StoryNodes.NewGame();

	[HarmonyPostfix]
	[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.NewActGameData))]
	public static void NewActGameData_Postfix() => StoryNodes.NewGame();
}
