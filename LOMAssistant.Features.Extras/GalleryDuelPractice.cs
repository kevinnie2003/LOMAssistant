using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using OBB.Framework.Utils;
using Mortal.Combat;
using Mortal.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UObject = UnityEngine.Object;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 用脱离存档的原版数据运行真正的战斗场景，包含所选关卡的事件。
/// </summary>
internal static class GalleryDuelPractice
{
	private static Session _session;
	private static int _allowSceneChange;
	internal static bool IsActive => _session != null;
	internal static string LastError { get; private set; }
	internal static event Action Finished;

	private const string BlockedText = "游戏版本或其他 Mod 和自定义决斗对不上，为了不碰存档，这次不能开始练习。";

	/// <summary>
	/// 不为 null 时不能开始练习。只有两种情况会挡：存档保护没装全，或者缺了练习根本跑不起来的部分。
	/// 其余对不上的地方只记日志，练习照常进行、跳过那一项。还没装补丁也算不能开始。
	/// </summary>
	internal static string Unavailable { get; private set; } = BlockedText;

	// 少了就跑不起来的补丁：接管关卡、换主角、拆开属性资源
	private static readonly HashSet<string> CriticalPatches = new HashSet<string> { nameof(StatAwake), nameof(CombatAwake), nameof(SetPlayerStat), nameof(SetCombatStat) };

	/// <summary>
	/// 按名字取的游戏私有字段，启动时取一次；游戏改了名字就是 null。
	/// 存档相关和接管关卡的几个缺了就不能开始，其余缺了就跳过那一步。
	/// </summary>
	private static class Native
	{
		internal static readonly FieldInfo PlayerInstance = AccessTools.Field(typeof(PlayerStatManagerData), "_instance");
		internal static readonly FieldInfo MissionInstance = AccessTools.Field(typeof(MissionManagerData), "_instance");
		internal static readonly FieldInfo LevelConfig = AccessTools.Field(typeof(CombatManager), "_levelConfig");
		internal static readonly FieldInfo ConfigList = AccessTools.Field(typeof(CombatLevelConfig), "_list");
		internal static readonly FieldInfo LoadingScene = AccessTools.Field(typeof(SceneController), "_currentLoadingScene");
		internal static readonly FieldInfo LevelEnemyStat = AccessTools.Field(typeof(CombatLevel), "_enemyStat");
		internal static readonly FieldInfo LevelTalkingEvent = AccessTools.Field(typeof(CombatLevel), "_talkingEvent");
		internal static readonly FieldInfo LevelDeadEnd = AccessTools.Field(typeof(CombatLevel), "_deadEnd");
		internal static readonly FieldInfo LevelWinResult = AccessTools.Field(typeof(CombatLevel), "_winResult");
		internal static readonly FieldInfo LevelLoseResult = AccessTools.Field(typeof(CombatLevel), "_loseResult");
		internal static readonly FieldInfo LevelConditionResult = AccessTools.Field(typeof(CombatLevel), "_conditionResult");
		internal static readonly FieldInfo StatValue = AccessTools.Field(typeof(GameStat), "_value");
		internal static readonly FieldInfo StatLimitAddition = AccessTools.Field(typeof(GameStat), "_limitAddition");
		internal static readonly FieldInfo StatAdditionData = AccessTools.Field(typeof(GameStat), "_statAdditionData");
		internal static readonly FieldInfo TalentMaxLevel = AccessTools.Field(typeof(PlayerTalentData), "_maxLevel");

		internal static bool IsCritical(string name) => name == nameof(PlayerInstance) || name == nameof(MissionInstance) || name == nameof(LevelConfig) || name == nameof(ConfigList);

		internal static IEnumerable<string> Missing()
		{
			foreach (var field in typeof(Native).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
			{
				if (field.FieldType == typeof(FieldInfo) && field.GetValue(null) == null)
				{
					yield return field.Name;
				}
			}
		}
	}

	/// <summary>
	/// 代替 PatchAll：每个补丁单独打，一个目标找不到或打不上，不连累其他补丁。
	/// 补丁目标直接从各补丁方法的 [HarmonyPatch] 读，加补丁时不用另外同步名单。
	/// 只有存档保护或必需部分缺了才挡住开始，其余照常练习，问题记进日志。
	/// </summary>
	internal static void Install(Harmony harmony)
	{
		var problems = new List<string>();
		var critical = new List<string>();
		foreach (string name in Native.Missing())
		{
			problems.Add("field " + name);
			if (Native.IsCritical(name))
			{
				critical.Add("field " + name);
			}
		}
		// 1.0.6001 以前的游戏没有测试模式：练习会存档、输了会进死亡结局，不能开始；
		// 必需部分缺了就一个补丁都不打，普通战斗完全不经过练习的代码
		if (!LevelTestModeCompat.Available)
		{
			critical.Add("type LevelTestMode");
		}
		if (critical.Count > 0)
		{
			Unavailable = BlockedText;
			FileLogger.LogWarning("[决斗练习] 必需部分缺失，不能开始练习，没有打补丁：" + string.Join(", ", critical));
			return;
		}
		foreach (var method in typeof(GalleryDuelPractice).GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
		{
			foreach (var attribute in method.GetCustomAttributes<HarmonyPatch>())
			{
				var info = attribute.info;
				if (info?.declaringType == null || string.IsNullOrEmpty(info.methodName))
				{
					continue;
				}
				string name = info.declaringType.Name + "." + info.methodName;
				string problem = TryPatch(harmony, () => AccessTools.Method(info.declaringType, info.methodName), method);
				if (problem != null)
				{
					problems.Add(name + " " + problem);
					if (CriticalPatches.Contains(method.Name))
					{
						critical.Add(name);
					}
				}
			}
		}
		MethodInfo allow = AccessTools.Method(typeof(SaveBarrier), nameof(SaveBarrier.Allow));
		foreach (var (name, target) in SaveBarrier.Required())
		{
			string problem = TryPatch(harmony, () => target, allow);
			if (problem != null)
			{
				problems.Add("save-guard " + name + " " + problem);
				// 真正写文件的都在 SaveSystem 里；菜单按钮最后也走 SaveSystem，缺了只是少一层
				if (name.StartsWith("SaveSystem.", StringComparison.Ordinal))
				{
					critical.Add("save-guard " + name);
				}
			}
		}
		Unavailable = critical.Count == 0 ? null : BlockedText;
		if (problems.Count == 0)
		{
			FileLogger.Log("[决斗练习] 补丁全部就绪");
		}
		else if (critical.Count == 0)
		{
			FileLogger.LogWarning("[决斗练习] 部分细节对不上，练习照常、跳过这些项：" + string.Join(", ", problems));
		}
		else
		{
			FileLogger.LogWarning("[决斗练习] 必需部分缺失，不能开始练习：" + string.Join(", ", critical) + "；全部问题：" + string.Join(", ", problems));
		}
	}

	/// <summary>打一个补丁；成功返回 null，失败返回原因（不抛出）。</summary>
	private static string TryPatch(Harmony harmony, Func<MethodBase> target, MethodInfo patch)
	{
		try
		{
			MethodBase method = target();
			if (method == null)
			{
				return "not found";
			}
			var harmonyMethod = new HarmonyMethod(patch);
			if (patch.IsDefined(typeof(HarmonyPostfix), false))
			{
				harmony.Patch(method, postfix: harmonyMethod);
			}
			else
			{
				harmony.Patch(method, prefix: harmonyMethod);
			}
			return null;
		}
		catch (Exception e)
		{
			return "failed: " + e.GetBaseException().Message;
		}
	}

	internal static IReadOnlyList<GalleryDuelPracticeSkill> AvailableSkills
	{
		get
		{
			var talents = PlayerStatManagerData.Instance?.Talents?.List;
			if (talents == null)
			{
				return Array.Empty<GalleryDuelPracticeSkill>();
			}
			return talents.Where(t => t != null && t.CombatSkill && !string.IsNullOrEmpty(t.Id))
				.OrderBy(t => t.Priority).ThenBy(t => t.Id, StringComparer.Ordinal).Select(t => new GalleryDuelPracticeSkill
				{
					Id = t.Id,
					Key = t.CombatSkillKey,
					DisplayLevel = t.DisplayLevel,
					MaxLevel = Math.Min(10, Math.Max(1, (Native.TalentMaxLevel == null ? 10 : (int)Native.TalentMaxLevel.GetValue(t)))),
					Name = LocalizationManager.Instance.LocaleResolver.GetString(t.GetIdKey()),
					Description = LocalizationManager.Instance.LocaleResolver.GetString(t.GetDescKey()),
				}).ToArray();
		}
	}

	internal static bool TryStart(DuelEncounter encounter, GalleryDuelPracticeSettings settings, out string error)
	{
		error = null;
		GameSave save = null;
		if (Unavailable != null)
		{
			error = Unavailable;
		}
		else if (IsActive)
		{
			error = "决斗练习正在进行。";
		}
		else if (LOMAssistantPlugin.Instance == null || SceneController.Instance == null)
		{
			error = "游戏尚未准备好。";
		}
		else if (SceneManager.GetActiveScene().name != "Title" || !Gallery.IsOpen || SceneController.Instance.IsLoading || SceneController.Instance.IsPrepare)
		{
			error = "请从标题画面的鉴赏开始决斗练习。";
		}
		else if (encounter == null || settings == null)
		{
			error = "请选择对手并设置主角属性。";
		}
		else if (settings.TryValidate(out error))
		{
			// 校验通过后，再核对所选的原版决斗配置与技能
			var catalog = GalleryDuelCatalog.Instance;
			if (!catalog.Stats.Any(s => s.Id == encounter.StatId)
				|| (!string.IsNullOrEmpty(encounter.Id) && !catalog.Levels.Any(l => l.Id == encounter.Id && l.StatId == encounter.StatId)))
			{
				error = "找不到所选的原版决斗配置。";
			}
			else if (settings.FromSave && (save = ReadSave(settings.SaveSlot)) == null)
			{
				error = "读不到这个存档，请重新选择。";
			}
			else
			{
				var skills = AvailableSkills.ToDictionary(s => s.Id, StringComparer.Ordinal);
				foreach (var pair in settings.SkillLevels.Where(p => p.Value > 0))
				{
					if (!skills.TryGetValue(pair.Key, out var skill) || pair.Value > skill.MaxLevel)
					{
						error = "技能编号或等级无效：" + pair.Key;
						break;
					}
				}
			}
		}
		if (error != null)
		{
			LastError = error;
			return false;
		}

		var session = new Session(encounter, settings.Clone(), save);
		_session = session; // 写入保护要覆盖准备、异步加载和还原三个阶段
		LastError = null;
		try
		{
			session.Prepare();
			Gallery.HideForDuelPractice();
			AllowSceneChange(() => SceneController.Instance.LoadCombat());
			LOMAssistantPlugin.Instance.StartCoroutine(Watch(session));
			return true;
		}
		catch (Exception ex)
		{
			error = "无法开始决斗练习：" + ex.GetBaseException().Message;
			Fail(session, ex);
			// LoadCombat 可能已经排上了场景操作，交给监视协程收尾
			LOMAssistantPlugin.Instance.StartCoroutine(Watch(session));
			return false;
		}
	}

	internal static void Abort() => RequestReturn(_session);

	private static readonly GameStatType[] SixTypes = { GameStatType.體力, GameStatType.內力, GameStatType.輕功, GameStatType.武功拳掌, GameStatType.武功刀劍, GameStatType.武功暗器 };
	private static readonly GameStatType[] FourTypes = { GameStatType.性情, GameStatType.處世, GameStatType.道德, GameStatType.修養 };

	/// <summary>
	/// 把存档载入一份临时副本，读出主角决斗时的属性和天赋等级，填成练习设置。
	/// 只读文件；全局数据只在这个方法里临时换成副本，结束前换回。
	/// </summary>
	internal static GalleryDuelPracticeSettings ReadSaveBase(string slot, out string error)
	{
		error = "读不到这个存档，请重新选择。";
		GameSave save = ReadSave(slot);
		if (save == null || IsActive || Native.PlayerInstance == null || Native.MissionInstance == null)
		{
			return null;
		}
		var originalPlayer = PlayerStatManagerData.Instance;
		var originalMissions = MissionManagerData.Instance;
		var graph = new NativeGraph();
		try
		{
			var player = graph.Copy(originalPlayer);
			var missions = graph.Copy(originalMissions);
			Native.PlayerInstance.SetValue(null, player);
			Native.MissionInstance.SetValue(null, missions);
			player.Load(save);
			missions.Load(save);
			var result = GalleryDuelPracticeSettings.CreateDefault();
			result.Six = SixTypes.Select(t => Mathf.Clamp(player.Stats.Get(t).FinalValue, 0, 1000)).ToArray();
			result.Four = FourTypes.Select(t => Mathf.Clamp(player.Stats.Get(t).FinalValue, 0, 100)).ToArray();
			result.Talking = Mathf.Clamp(player.Stats.Get(GameStatType.嘴力).FinalValue, 0, 1000);
			foreach (var skill in AvailableSkills)
			{
				int level = Mathf.Clamp(player.Talents.Get(skill.Id)?.Level ?? 0, 0, Math.Min(10, skill.MaxLevel));
				if (level > 0)
				{
					result.SkillLevels[skill.Id] = level;
				}
			}
			error = null;
			return result;
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[决斗练习] 读存档数值失败 " + slot + ": " + ex.GetBaseException().Message);
			return null;
		}
		finally
		{
			Native.PlayerInstance.SetValue(null, originalPlayer);
			Native.MissionInstance.SetValue(null, originalMissions);
			graph.Dispose();
		}
	}

	/// <summary>读一个手动存档；读不到返回 null。只读文件，不改存档。</summary>
	private static GameSave ReadSave(string slot)
	{
		try
		{
			return SaveSystem.Instance?.GetSaveData(slot);
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[决斗练习] 读存档失败 " + slot + ": " + ex.GetBaseException().Message);
			return null;
		}
	}

	/// <summary>列出全部手动存档（保存时间新的在前）。只读文件，不改存档。</summary>
	internal static List<GalleryDuelPracticeSave> ListSaves()
	{
		var result = new List<GalleryDuelPracticeSave>();
		string folder;
		try
		{
			folder = Path.GetDirectoryName(Traverse.Create(SaveSystem.Instance).Method("GetSaveDataFile", new[] { typeof(string) }).GetValue<string>("001"));
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[决斗练习] 找不到存档文件夹: " + ex.GetBaseException().Message);
			return result;
		}
		if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
		{
			return result;
		}
		using (SaveDataCache.Scope())
		{
			foreach (string file in Directory.GetFiles(folder, "Save_*.dat"))
			{
				string slot = Path.GetFileNameWithoutExtension(file).Substring("Save_".Length);
				if (slot.Length == 0 || !slot.All(char.IsDigit))
				{
					continue;
				}
				GameSave data = ReadSave(slot);
				if (data == null)
				{
					continue;
				}
				string note = SaveNotePatch.GetNote(slot);
				result.Add(new GalleryDuelPracticeSave
				{
					Slot = slot,
					Label = SaveBrowser.SlotLabel(slot),
					Detail = SaveBrowser.GameDate(data) + "　" + SaveBrowser.SavedAt(data) + (string.IsNullOrWhiteSpace(note) ? "" : "　" + note.Trim()),
					TimeTick = data.TimeTick,
				});
			}
		}
		return result.OrderByDescending(s => s.TimeTick).ToList();
	}

	private static void RequestReturn(Session session)
	{
		if (session == null || _session != session)
		{
			return;
		}
		session.Returning = true;
		Time.timeScale = 1f;
		if (session.Manager != null)
		{
			session.Manager.EnableHotKey = false;
			session.Manager.StopAllCoroutines();
		}
	}

	private static void Fail(Session session, Exception ex)
	{
		LastError = "决斗练习失败：" + ex.GetBaseException().Message;
		FileLogger.LogError("[决斗练习] " + ex);
		RequestReturn(session);
	}

	private static void AllowSceneChange(Action action)
	{
		_allowSceneChange++;
		try
		{
			action();
		}
		finally
		{
			_allowSceneChange--;
		}
	}

	private static IEnumerator Watch(Session session)
	{
		// 先退出调用方的 OnGUI / 原生迭代器，再开始或完成返回
		yield return null;
		float deadline = Time.realtimeSinceStartup + 90f;
		while (_session == session)
		{
			var scene = SceneManager.GetActiveScene();
			var combat = SceneManager.GetSceneByName("Combat");
			bool busy = SceneController.Instance != null && (SceneController.Instance.IsLoading || SceneController.Instance.IsPrepare);
			if (!session.Returning && Time.realtimeSinceStartup > deadline && !session.Entered)
			{
				Fail(session, new TimeoutException("原版决斗场景未能完成初始化。"));
			}
			if (!session.Returning && session.Entered && (!combat.IsValid() || !combat.isLoaded))
			{
				RequestReturn(session);
			}
			if (session.Returning && !busy)
			{
				if (scene.name == "Title" && (!combat.IsValid() || !combat.isLoaded) && LoadingCleared())
				{
					// 还原全局状态之前，要等 Title.Start 和最后一帧 StartLoadScene 跑完
					yield return null;
					if (_session != session)
					{
						yield break;
					}
					if (SceneManager.GetActiveScene().name != "Title" || SceneManager.GetSceneByName("Combat").isLoaded || !LoadingCleared())
					{
						continue;
					}
					Complete(session);
					yield break;
				}
				if (!session.TitleRequested)
				{
					session.TitleRequested = true;
					// 在 InitCombatRound 之前中止，会留下战斗的加载场景
					// 先把它清掉，免得与 Title 新建的同名加载场景冲突
					if (!LoadingCleared())
					{
						yield return GuardEntry(session, ClearLoading(), false, true);
					}
					try
					{
						AllowSceneChange(() => SceneController.Instance.LoadTitle());
					}
					catch (Exception ex)
					{
						session.TitleRequested = false;
						Fail(session, ex);
					}
				}
			}
			yield return null;
		}
	}

	private static bool LoadingCleared() => !SceneManager.GetSceneByName("Loading1").isLoaded
		&& string.IsNullOrEmpty(Native.LoadingScene?.GetValue(SceneController.Instance) as string);

	private static IEnumerator ClearLoading()
	{
		string name = Native.LoadingScene?.GetValue(SceneController.Instance) as string;
		var loading = SceneManager.GetSceneByName(string.IsNullOrEmpty(name) ? "Loading1" : name);
		if (loading.IsValid() && loading.isLoaded)
		{
			if (string.IsNullOrEmpty(name))
			{
				yield return SceneManager.UnloadSceneAsync(loading);
			}
			else
			{
				yield return SceneController.Instance.UnloadLoading();
			}
		}
		else
		{
			// 被停掉的原生协程可能已经卸载了它，但还没清空这个字段
			Native.LoadingScene?.SetValue(SceneController.Instance, string.Empty);
		}
	}

	private static void Complete(Session session)
	{
		try
		{
			session.Restore();
		}
		catch (Exception ex)
		{
			LastError = "恢复决斗练习状态失败：" + ex.GetBaseException().Message;
			FileLogger.LogError("[决斗练习] " + ex);
		}
		finally
		{
			_session = null;
		}
		Gallery.RestoreAfterDuelPractice();
		FileLogger.Log("[决斗练习] 已返回并恢复；当前场景=" + SceneManager.GetActiveScene().name);
		try
		{
			Finished?.Invoke();
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[决斗练习] 返回回调失败：" + ex);
		}
	}

	// 原生 Start 第一次 ResetData 之前，把两份属性资源和所有序列化的事件/回合引用全部拆开
	[HarmonyPrefix, HarmonyPriority(Priority.First)]
	[HarmonyPatch(typeof(CombatStatController), "Awake")]
	private static void StatAwake(CombatStatController __instance)
	{
		// Unity 不保证 Awake 的先后顺序，而这个控制器会在 Init() 里清空修为字段
		var session = _session;
		if (session == null)
		{
			return;
		}
		try
		{
			session.Graph.Rewire(__instance);
		}
		catch (Exception ex)
		{
			Fail(session, ex);
		}
	}

	[HarmonyPrefix, HarmonyPriority(Priority.First)]
	[HarmonyPatch(typeof(CombatManager), "Awake")]
	private static void CombatAwake(CombatManager __instance)
	{
		var session = _session;
		if (session == null || session.Returning)
		{
			return;
		}
		try
		{
			session.Attach(__instance);
		}
		catch (Exception ex)
		{
			Fail(session, ex);
		}
	}

	[HarmonyPrefix, HarmonyPriority(Priority.First)]
	[HarmonyPatch(typeof(CombatManager), "Start")]
	private static bool CombatStart(ref IEnumerator __result)
	{
		if (!IsActive)
		{
			return true;
		}
		if (!_session.Returning)
		{
			LevelTestModeCompat.Begin();
			return true;
		}
		__result = Empty();
		return false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(CombatManager), "SetPlayerStat")]
	private static bool SetPlayerStat(CombatManager __instance)
	{
		var session = _session;
		if (session == null || session.Manager != __instance)
		{
			return true;
		}
		// 以存档为底时走游戏自己的算法：数值从载入了存档的练习副本里算，紧接着的 SetStat 再套用自己设定的血量气力
		if (session.FromSave)
		{
			session.PendingVitals = true;
			return true;
		}
		session.AssignHero(__instance.PlayerAction.Stat.Data);
		__instance.PlayerAction.SetStat(__instance.PlayerAction.Stat.Data);
		return false;
	}

	// 其他辅助补丁可能替换 SetStat 的参数，最后以所选的原版敌人为准
	[HarmonyPrefix, HarmonyPriority(Priority.Last)]
	[HarmonyPatch(typeof(CombatActionController), "SetStat")]
	private static void SetCombatStat(CombatActionController __instance, ref CombatStat data)
	{
		var session = _session;
		if (session?.Manager == null)
		{
			return;
		}
		if (__instance == session.Manager.EnemyAction)
		{
			data = session.Level.EnemyStat;
		}
		else if (__instance == session.Manager.PlayerAction)
		{
			data = session.Graph.Copy(data);
			if (session.PendingVitals)
			{
				session.PendingVitals = false;
				session.ApplyVitals(data);
			}
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(CombatManager), "LoadCombatLevelAsset")]
	private static void LoadCombatLevel(ref IEnumerator __result)
	{
		if (IsActive)
		{
			__result = GuardEntry(_session, __result);
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(CombatManager), "InitCombatRound")]
	private static void InitCombatRound(ref IEnumerator __result)
	{
		// 原生的 LoadCombatLevelAsset 会单独启动它，所以它的事件和技能需要各自的保护
		if (IsActive)
		{
			__result = GuardEntry(_session, __result, false);
		}
	}

	private static IEnumerator GuardEntry(Session session, IEnumerator native, bool finishEntry = true, bool allowReturning = false)
	{
		// 把嵌套的 IEnumerator 展平，让启动期的错误走返回流程，而不是把场景卡住
		var stack = new Stack<IEnumerator>();
		stack.Push(native);
		while (stack.Count > 0 && _session == session && (allowReturning || !session.Returning))
		{
			object current = null;
			bool moved = false;
			Exception failure = null;
			try
			{
				moved = stack.Peek().MoveNext();
				if (moved)
				{
					current = stack.Peek().Current;
				}
			}
			catch (Exception ex)
			{
				failure = ex;
			}
			if (failure != null)
			{
				Fail(session, failure);
				yield break;
			}
			if (!moved)
			{
				(stack.Pop() as IDisposable)?.Dispose();
				continue;
			}
			if (current is IEnumerator nested)
			{
				stack.Push(nested);
			}
			else
			{
				yield return current;
			}
		}
		if (!finishEntry || _session != session || session.Returning)
		{
			yield break;
		}
		session.Entered = true;
		var hero = session.Manager.PlayerAction.Stat.Data;
		FileLogger.Log("[决斗练习] 已进入；CombatLevel=" + session.Level.name + "；CombatStat=" + session.Level.EnemyStat.name
			+ "; Six=" + string.Join(",", new[] { hero.Strength, hero.Stamina, hero.Dexterity, hero.Fist, hero.Sword, hero.MartialWeapon })
			+ "; Four=" + string.Join(",", new[] { hero.Disposition, hero.Behaviour, hero.Karma, hero.Training })
			+ "; HP=" + hero.DefaultHealth + "/" + hero.MaxHealth + "; energy=" + hero.DefaultStamina + "/" + hero.MaxStamina
			+ "; talents=" + string.Join(",", hero.TalentItems.Select(t => t.Data.Id + ":" + t.Level)));
	}

	private static IEnumerator Empty()
	{
		yield break;
	}

	private static bool Exit(ref IEnumerator result)
	{
		if (!IsActive)
		{
			return true;
		}
		RequestReturn(_session);
		result = Empty();
		return false;
	}

	[HarmonyPrefix, HarmonyPatch(typeof(CombatManager), "GameOver")]
	private static bool GameOver(ref IEnumerator __result) => Exit(ref __result);

	[HarmonyPrefix, HarmonyPatch(typeof(CombatManager), "LoadNextScene")]
	private static bool LoadNextScene(ref IEnumerator __result) => Exit(ref __result);

	[HarmonyPrefix, HarmonyPatch(typeof(CombatManager), "LoadGameOver")]
	private static bool LoadGameOver(ref IEnumerator __result) => Exit(ref __result);

	[HarmonyPrefix, HarmonyPatch(typeof(CombatManager), "LoadNewScene")]
	private static bool LoadNewScene(ref IEnumerator __result) => Exit(ref __result);

	// 这个迭代器工厂被 LoadTitle、LoadScene、LoadNextScene 和 LoadNewScene 共用
	[HarmonyPrefix, HarmonyPriority(Priority.First)]
	[HarmonyPatch(typeof(SceneController), "StartLoadScene")]
	private static bool SceneChange(ref IEnumerator __result)
	{
		if (!IsActive || _allowSceneChange > 0)
		{
			return true;
		}
		return Exit(ref __result);
	}

	/// <summary>练习期间挡住存读档；由 Install 逐个打补丁。</summary>
	internal static class SaveBarrier
	{
		/// <summary>要拦的方法；游戏更新后找不到的是 null，启动检查据此停用功能。</summary>
		internal static IEnumerable<(string Name, MethodBase Method)> Required()
		{
			// SaveSystem 里所有不返回值的方法都拦，游戏新加或改名的写入方法也自动包括在内。
			// 把外层的自动存档接口也包括进来：本 Mod 的历史记录前缀先于底层写入执行
			// 读档和元数据的 setter 会保留对原数据的序列化引用，所以也要拦住
			foreach (var method in typeof(SaveSystem).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
			{
				if (method.ReturnType == typeof(void) && method.Name != "LogMessage" && method.Name != "DisplayAchivement")
				{
					yield return ("SaveSystem." + method.Name, method);
				}
			}
			foreach (string name in new[] { "SaveButtonClick", "ConfirmSaveButtonClick", "LoadButtonClick", "OnSavePanelOpen" })
			{
				yield return ("MenuPanel." + name, AccessTools.Method(typeof(MenuPanel), name));
			}
			// 原生删除档的现有后缀补丁，否则会去改本 Mod 的存档备注旁路文件
			yield return ("SaveNotePatch.SetNote", AccessTools.Method(typeof(SaveNotePatch), nameof(SaveNotePatch.SetNote)));
		}

		[HarmonyPriority(Priority.First)]
		internal static bool Allow() => !IsActive;
	}

	private sealed class Session
	{
		internal readonly GalleryDuelPracticeSettings Settings;
		internal readonly string LevelId, StatId;
		internal readonly NativeGraph Graph = new NativeGraph();
		internal CombatManager Manager;
		internal CombatLevel Level;
		internal bool Returning, TitleRequested, Entered;
		private readonly PlayerStatManagerData _originalPlayer;
		private readonly MissionManagerData _originalMissions;
		private readonly UnityEngine.Random.State _random;
		private readonly float _timeScale;
		private readonly bool _testMode;
		private PlayerStatManagerData _player;
		private readonly GameSave _save;
		internal bool FromSave => _save != null;
		internal bool PendingVitals;

		internal Session(DuelEncounter encounter, GalleryDuelPracticeSettings settings, GameSave save)
		{
			_save = save;
			LevelId = encounter.Id ?? "";
			StatId = encounter.StatId;
			Settings = settings;
			_originalPlayer = PlayerStatManagerData.Instance;
			_originalMissions = MissionManagerData.Instance;
			_random = UnityEngine.Random.state;
			_timeScale = Time.timeScale;
			_testMode = LevelTestModeCompat.IsActive;
		}

		internal void Prepare()
		{
			_player = Graph.Copy(_originalPlayer);
			var missions = Graph.Copy(_originalMissions);
			// ResetActionCount 会读取 MissionManagerData.Instance：两个实例都要先换掉，再分别重置
			Native.PlayerInstance.SetValue(null, _player);
			Native.MissionInstance.SetValue(null, missions);
			if (_save != null)
			{
				// 存档只载入练习用的副本，和真正读档一样恢复属性、天赋、装备和剧情标记
				_player.Load(_save);
				missions.Load(_save);
				ApplySaveChanges();
				_player.SetSceneKey(string.IsNullOrEmpty(LevelId) ? "GalleryPractice" : LevelId.Substring(3));
				_player.SetNextScene("Title");
				LevelTestModeCompat.Begin();
				return;
			}
			_player.ResetData();
			missions.ResetData();
			// 全新的练习主角没有上一周目留下的疲劳
			missions.GetFlag("Z00001").State = 1;
			var sixTypes = new[] { GameStatType.體力, GameStatType.內力, GameStatType.輕功, GameStatType.武功拳掌, GameStatType.武功刀劍, GameStatType.武功暗器 };
			var fourTypes = new[] { GameStatType.性情, GameStatType.處世, GameStatType.道德, GameStatType.修養 };
			for (int i = 0; i < sixTypes.Length; i++)
			{
				SetRaw(sixTypes[i], Settings.Six[i]);
			}
			for (int i = 0; i < fourTypes.Length; i++)
			{
				SetRaw(fourTypes[i], Settings.Four[i]);
			}
			SetRaw(GameStatType.嘴力, Settings.Talking);
			SetRaw(GameStatType.佛學, Settings.Mastery[1]);
			SetRaw(GameStatType.道學, Settings.Mastery[2]);
			SetRaw(GameStatType.儒學, Settings.Mastery[3]);
			SetRaw(GameStatType.全武學點數, Settings.Six.Sum());
			foreach (var pair in Settings.SkillLevels)
			{
				_player.Talents.Set(pair.Key, pair.Value);
			}
			_player.SetSceneKey(string.IsNullOrEmpty(LevelId) ? "GalleryPractice" : LevelId.Substring(3));
			_player.SetNextScene("Title");
			LevelTestModeCompat.Begin();
		}

		/// <summary>以存档为底时，只把和存档不同的项写进练习副本。</summary>
		private void ApplySaveChanges()
		{
			var original = Settings.SaveBase;
			if (original == null)
			{
				return;
			}
			for (int i = 0; i < SixTypes.Length; i++)
			{
				if (Settings.Six[i] != original.Six[i])
				{
					SetRaw(SixTypes[i], Settings.Six[i]);
				}
			}
			for (int i = 0; i < FourTypes.Length; i++)
			{
				if (Settings.Four[i] != original.Four[i])
				{
					SetRaw(FourTypes[i], Settings.Four[i]);
				}
			}
			if (Settings.Talking != original.Talking)
			{
				SetRaw(GameStatType.嘴力, Settings.Talking);
			}
			foreach (string id in Settings.SkillLevels.Keys.Union(original.SkillLevels.Keys).ToList())
			{
				Settings.SkillLevels.TryGetValue(id, out int level);
				original.SkillLevels.TryGetValue(id, out int before);
				if (level != before)
				{
					_player.Talents.Set(id, level);
				}
			}
		}

		internal void ApplyVitals(CombatStat hero)
		{
			if (!Settings.CustomVitals)
			{
				return;
			}
			hero.MaxHealth = Settings.MaxHealth;
			hero.DefaultHealth = Settings.Health;
			hero.MaxStamina = Settings.MaxEnergy;
			hero.DefaultStamina = Settings.Energy;
		}

		private void SetRaw(GameStatType type, int value)
		{
			var stat = _player.Stats.Get(type);
			stat.SetMax(Math.Max(stat.Max, value));
			// 字段对不上就跳过：决斗用的数值另由 AssignHero 直接写进战斗属性
			Native.StatValue?.SetValue(stat, value);
			Native.StatLimitAddition?.SetValue(stat, 0);
			Native.StatAdditionData?.SetValue(stat, Array.Empty<StatConvertCollectionData>());
		}

		internal void Attach(CombatManager manager)
		{
			Manager = manager;
			var config = (CombatLevelConfig)Native.LevelConfig.GetValue(manager);
			var source = string.IsNullOrEmpty(LevelId) ? config.List.FirstOrDefault() : config.Get(LevelId);
			if (source == null)
			{
				throw new InvalidOperationException("原版 CombatLevel 不存在：" + LevelId);
			}
			Level = Graph.Copy(source);
			if (string.IsNullOrEmpty(LevelId))
			{
				var stat = Resources.FindObjectsOfTypeAll<CombatStat>().FirstOrDefault(s => s.name == StatId && !Graph.IsCopy(s));
				if (stat == null)
				{
					throw new InvalidOperationException("原版 CombatStat 不存在：" + StatId);
				}
				if (Native.LevelEnemyStat == null)
				{
					throw new InvalidOperationException("这个游戏版本不能单独练习独立配置的角色，请选一个剧情。");
				}
				Level.name = "CL_GalleryPractice";
				Native.LevelEnemyStat.SetValue(Level, Graph.Copy(stat));
				Level.Events = new List<CombatEventData>();
				Native.LevelTalkingEvent?.SetValue(Level, null);
			}
			else if (Level.EnemyStat == null || Level.EnemyStat.name != StatId)
			{
				throw new InvalidOperationException("所选 CombatLevel 的敌人配置与鉴赏目录不一致。请更新目录。");
			}
			// 练习不使用剧情的结果标记和死亡结局
			// 对不上的跳过：结果只写进练习用的副本，测试模式下也不会进死亡结局
			Native.LevelDeadEnd?.SetValue(Level, false);
			Native.LevelWinResult?.SetValue(Level, Array.Empty<CombatResultFlag>());
			Native.LevelLoseResult?.SetValue(Level, Array.Empty<CombatResultFlag>());
			Native.LevelConditionResult?.SetValue(Level, Array.Empty<CombatConditionResultFlag>());
			var isolatedConfig = ScriptableObject.CreateInstance<CombatLevelConfig>();
			Graph.Own(isolatedConfig);
			Native.ConfigList.SetValue(isolatedConfig, new List<CombatLevel> { Level });
			Native.LevelConfig.SetValue(manager, isolatedConfig);
			// 包括未激活的根物体和继承来的字段（回合界面、属性控制器、特效数据库）
			foreach (var root in manager.gameObject.scene.GetRootGameObjects())
			{
				foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
				{
					if (component != null && NativeGraph.IsNative(component.GetType()))
					{
						Graph.Rewire(component);
					}
				}
			}
		}

		internal void AssignHero(CombatStat hero)
		{
			hero.Strength = Settings.Six[0];
			hero.Stamina = Settings.Six[1];
			hero.Dexterity = Settings.Six[2];
			hero.Fist = Settings.Six[3];
			hero.Sword = Settings.Six[4];
			hero.MartialWeapon = Settings.Six[5];
			hero.Disposition = Settings.Four[0];
			hero.Behaviour = Settings.Four[1];
			hero.Karma = Settings.Four[2];
			hero.Training = Settings.Four[3];
			hero.StrategyLevel = Settings.Mastery[0];
			hero.Buddhism = Settings.Mastery[1];
			hero.Taoism = Settings.Mastery[2];
			hero.Confucianism = Settings.Mastery[3];
			hero.Xingyi = Settings.Mastery[4];
			hero.MaxHealth = Settings.MaxHealth;
			hero.DefaultHealth = Settings.Health;
			hero.MaxStamina = Settings.MaxEnergy;
			hero.DefaultStamina = Settings.Energy;
			hero.Talking = Settings.Talking;
			hero.TotalMartial = Settings.Six.Sum();
			hero.TalentItems = Settings.SkillLevels.Where(p => p.Value > 0).Select(p => new CombatTalentItem { Data = _player.Talents.Get(p.Key), Level = p.Value }).ToList();
			hero.Reset();
		}

		internal void Restore()
		{
			Native.PlayerInstance.SetValue(null, _originalPlayer);
			Native.MissionInstance.SetValue(null, _originalMissions);
			UnityEngine.Random.state = _random;
			Time.timeScale = _timeScale;
			if (_testMode)
			{
				LevelTestModeCompat.Begin();
			}
			else
			{
				LevelTestModeCompat.End();
			}
			Graph.Dispose();
		}
	}

	/// <summary>
	/// 复制 Unity 序列化的原生数据图。预制体、媒体和输入动作仍是不可变的共享资源。
	/// </summary>
	private sealed class NativeGraph : IDisposable
	{
		private readonly Dictionary<object, object> _copies = new Dictionary<object, object>(new ReferenceComparer());
		private readonly List<ScriptableObject> _owned = new List<ScriptableObject>();
		private static readonly MethodInfo Memberwise = AccessTools.Method(typeof(object), "MemberwiseClone");
		internal static bool IsNative(Type type) => type.Assembly.GetName().Name.StartsWith("Mortal.", StringComparison.Ordinal);
		internal T Copy<T>(T original) where T : ScriptableObject => (T)CopyValue(original);
		internal bool IsCopy(object value) => value != null && _copies.TryGetValue(value, out var copy) && ReferenceEquals(value, copy);

		internal void Own(ScriptableObject value)
		{
			value.hideFlags = HideFlags.HideAndDontSave;
			_owned.Add(value);
			_copies[value] = value;
		}

		private object CopyValue(object value)
		{
			if (value == null)
			{
				return null;
			}
			var type = value.GetType();
			if (type.IsPrimitive || type.IsEnum || value is string || value is Delegate)
			{
				return value;
			}
			if (_copies.TryGetValue(value, out var previous))
			{
				return previous;
			}
			if (value is ScriptableObject asset && IsNative(type))
			{
				var copy = UObject.Instantiate(asset);
				copy.name = asset.name; // 原生的集合和条件按 Unity 资源的确切名字查找
				Own(copy);
				_copies[value] = copy;
				CopyFields(value, copy);
				return copy;
			}
			if (value is UObject)
			{
				return value;
			}
			if (value is Array array)
			{
				var copy = (Array)array.Clone();
				_copies[value] = copy;
				for (int i = 0; i < array.Length; i++)
				{
					copy.SetValue(CopyValue(array.GetValue(i)), i);
				}
				return copy;
			}
			if (value is IList list)
			{
				var copy = (IList)Activator.CreateInstance(type);
				_copies[value] = copy;
				foreach (object item in list)
				{
					copy.Add(CopyValue(item));
				}
				return copy;
			}
			if (!IsNative(type))
			{
				return value;
			}
			var result = Memberwise.Invoke(value, null);
			_copies[value] = result;
			CopyFields(value, result);
			return result;
		}

		internal void Rewire(object target) => CopyFields(target, target);

		private void CopyFields(object source, object target)
		{
			for (var type = source.GetType(); type != null && IsNative(type); type = type.BaseType)
			{
				foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				{
					if (!field.IsInitOnly && !field.IsNotSerialized && (field.IsPublic || field.IsDefined(typeof(SerializeField), true)))
					{
						field.SetValue(target, CopyValue(field.GetValue(source)));
					}
				}
			}
		}

		public void Dispose()
		{
			for (int i = _owned.Count - 1; i >= 0; i--)
			{
				if (_owned[i] != null)
				{
					UObject.Destroy(_owned[i]);
				}
			}
			_owned.Clear();
			_copies.Clear();
		}
	}

	private sealed class ReferenceComparer : IEqualityComparer<object>
	{
		public new bool Equals(object x, object y) => ReferenceEquals(x, y);

		public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
	}
}
