using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Mortal.Combat;
using Mortal.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UObject = UnityEngine.Object;

namespace LOMAssistant.Features.Extras;

/// <summary>Runs the real Combat scene against detached native data, including the selected level's events.</summary>
[HarmonyPatch]
internal static class GalleryDuelPractice
{
	private static Session _session;
	private static int _allowSceneChange;
	internal static bool IsActive => _session != null;
	internal static string LastError { get; private set; }
	internal static event Action Finished;

	internal static IReadOnlyList<GalleryDuelPracticeSkill> AvailableSkills
	{
		get
		{
			var talents = PlayerStatManagerData.Instance?.Talents?.List;
			if (talents == null) return Array.Empty<GalleryDuelPracticeSkill>();
			return talents.Where(t => t != null && t.CombatSkill && !string.IsNullOrEmpty(t.Id))
				.OrderBy(t => t.Priority).ThenBy(t => t.Id, StringComparer.Ordinal).Select(t => new GalleryDuelPracticeSkill
				{
					Id = t.Id, Key = t.CombatSkillKey, DisplayLevel = t.DisplayLevel,
					MaxLevel = Math.Min(10, Math.Max(1, Traverse.Create(t).Field("_maxLevel").GetValue<int>())),
					Name = LocalizationManager.Instance.LocaleResolver.GetString(t.GetIdKey()),
					Description = LocalizationManager.Instance.LocaleResolver.GetString(t.GetDescKey()),
				}).ToArray();
		}
	}

	internal static bool TryStart(DuelEncounter encounter, GalleryDuelPracticeSettings settings, out string error)
	{
		error = null;
		if (IsActive) error = "决斗练习正在进行。";
		else if (LOMAssistantPlugin.Instance == null || SceneController.Instance == null) error = "游戏尚未准备好。";
		else if (SceneManager.GetActiveScene().name != "Title" || !Gallery.IsOpen || SceneController.Instance.IsLoading || SceneController.Instance.IsPrepare)
			error = "请从标题画面的鉴赏开始决斗练习。";
		else if (encounter == null || settings == null) error = "请选择对手并设置主角属性。";
		else if (!settings.TryValidate(out error)) { }
		else
		{
			var catalog = GalleryDuelCatalog.Instance;
			if (!catalog.Stats.Any(s => s.Id == encounter.StatId)
				|| (!string.IsNullOrEmpty(encounter.Id) && !catalog.Levels.Any(l => l.Id == encounter.Id && l.StatId == encounter.StatId)))
				error = "找不到所选的原版决斗配置。";
			else
			{
				var skills = AvailableSkills.ToDictionary(s => s.Id, StringComparer.Ordinal);
				foreach (var pair in settings.SkillLevels.Where(p => p.Value > 0))
					if (!skills.TryGetValue(pair.Key, out var skill) || pair.Value > skill.MaxLevel)
					{
						error = "技能编号或等级无效：" + pair.Key;
						break;
					}
			}
		}
		if (error != null) { LastError = error; return false; }

		var session = new Session(encounter, settings.Clone());
		_session = session; // The write guard covers setup, asynchronous loading AND restoration.
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
			// LoadCombat may already have scheduled a scene operation; let the watcher drain it.
			LOMAssistantPlugin.Instance.StartCoroutine(Watch(session));
			return false;
		}
	}

	internal static void Abort() => RequestReturn(_session);

	private static void RequestReturn(Session session)
	{
		if (session == null || _session != session) return;
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
		try { action(); }
		finally { _allowSceneChange--; }
	}

	private static IEnumerator Watch(Session session)
	{
		// Always leave the caller's OnGUI / native iterator before starting or completing a return.
		yield return null;
		float deadline = Time.realtimeSinceStartup + 90f;
		while (_session == session)
		{
			var scene = SceneManager.GetActiveScene();
			var combat = SceneManager.GetSceneByName("Combat");
			bool busy = SceneController.Instance != null && (SceneController.Instance.IsLoading || SceneController.Instance.IsPrepare);
			if (!session.Returning && Time.realtimeSinceStartup > deadline && !session.Entered)
				Fail(session, new TimeoutException("原版决斗场景未能完成初始化。"));
			if (!session.Returning && session.Entered && (!combat.IsValid() || !combat.isLoaded)) RequestReturn(session);
			if (session.Returning && !busy)
			{
				if (scene.name == "Title" && (!combat.IsValid() || !combat.isLoaded) && LoadingCleared())
				{
					// Title.Start and the final StartLoadScene frame must finish before restoring globals.
					yield return null;
					if (_session != session) yield break;
					if (SceneManager.GetActiveScene().name != "Title" || SceneManager.GetSceneByName("Combat").isLoaded || !LoadingCleared()) continue;
					Complete(session);
					yield break;
				}
				if (!session.TitleRequested)
				{
					session.TitleRequested = true;
					// Aborting before InitCombatRound leaves Combat's loading scene behind.
					// Drain it before Title creates its own loading scene with the same name.
					if (!LoadingCleared())
						yield return GuardEntry(session, ClearLoading(), false, true);
					try { AllowSceneChange(() => SceneController.Instance.LoadTitle()); }
					catch (Exception ex) { session.TitleRequested = false; Fail(session, ex); }
				}
			}
			yield return null;
		}
	}

	private static bool LoadingCleared() => !SceneManager.GetSceneByName("Loading1").isLoaded
		&& string.IsNullOrEmpty(Traverse.Create(SceneController.Instance).Field("_currentLoadingScene").GetValue<string>());

	private static IEnumerator ClearLoading()
	{
		string name = Traverse.Create(SceneController.Instance).Field("_currentLoadingScene").GetValue<string>();
		var loading = SceneManager.GetSceneByName(string.IsNullOrEmpty(name) ? "Loading1" : name);
		if (loading.IsValid() && loading.isLoaded)
		{
			if (string.IsNullOrEmpty(name)) yield return SceneManager.UnloadSceneAsync(loading);
			else yield return SceneController.Instance.UnloadLoading();
		}
		else
		{
			// A stopped native coroutine may have already unloaded it but not cleared this field yet.
			Traverse.Create(SceneController.Instance).Field("_currentLoadingScene").SetValue(string.Empty);
		}
	}

	private static void Complete(Session session)
	{
		try { session.Restore(); }
		catch (Exception ex)
		{
			LastError = "恢复决斗练习状态失败：" + ex.GetBaseException().Message;
			FileLogger.LogError("[决斗练习] " + ex);
		}
		finally { _session = null; }
		Gallery.RestoreAfterDuelPractice();
		FileLogger.Log("[决斗练习] return/restoration completed; active scene=" + SceneManager.GetActiveScene().name);
		try { Finished?.Invoke(); }
		catch (Exception ex) { FileLogger.LogWarning("[决斗练习] 返回回调失败：" + ex); }
	}

	// Before native Start's first ResetData, detach BOTH stat assets and every serialized event/round reference.
	[HarmonyPrefix, HarmonyPriority(Priority.First)]
	[HarmonyPatch(typeof(CombatStatController), "Awake")]
	private static void StatAwake(CombatStatController __instance)
	{
		// Unity does not guarantee Awake order. This controller clears mastery fields in Init().
		var session = _session;
		if (session == null) return;
		try { session.Graph.Rewire(__instance); }
		catch (Exception ex) { Fail(session, ex); }
	}

	[HarmonyPrefix, HarmonyPriority(Priority.First)]
	[HarmonyPatch(typeof(CombatManager), "Awake")]
	private static void CombatAwake(CombatManager __instance)
	{
		var session = _session;
		if (session == null || session.Returning) return;
		try { session.Attach(__instance); }
		catch (Exception ex) { Fail(session, ex); }
	}

	[HarmonyPrefix, HarmonyPriority(Priority.First)]
	[HarmonyPatch(typeof(CombatManager), "Start")]
	private static bool CombatStart(ref IEnumerator __result)
	{
		if (!IsActive) return true;
		if (!_session.Returning) { LevelTestMode.Begin(); return true; }
		__result = Empty();
		return false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(CombatManager), "SetPlayerStat")]
	private static bool SetPlayerStat(CombatManager __instance)
	{
		var session = _session;
		if (session == null || session.Manager != __instance) return true;
		session.AssignHero(__instance.PlayerAction.Stat.Data);
		__instance.PlayerAction.SetStat(__instance.PlayerAction.Stat.Data);
		return false;
	}

	// Other assistance patches may replace SetStat's argument; the selected native enemy wins last.
	[HarmonyPrefix, HarmonyPriority(Priority.Last)]
	[HarmonyPatch(typeof(CombatActionController), "SetStat")]
	private static void SetCombatStat(CombatActionController __instance, ref CombatStat data)
	{
		var session = _session;
		if (session?.Manager == null) return;
		if (__instance == session.Manager.EnemyAction) data = session.Level.EnemyStat;
		else if (__instance == session.Manager.PlayerAction) data = session.Graph.Copy(data);
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(CombatManager), "LoadCombatLevelAsset")]
	private static void LoadCombatLevel(ref IEnumerator __result)
	{
		if (IsActive) __result = GuardEntry(_session, __result);
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(CombatManager), "InitCombatRound")]
	private static void InitCombatRound(ref IEnumerator __result)
	{
		// Native LoadCombatLevelAsset starts this separately, so its events/skills need their own guard.
		if (IsActive) __result = GuardEntry(_session, __result, false);
	}

	private static IEnumerator GuardEntry(Session session, IEnumerator native, bool finishEntry = true, bool allowReturning = false)
	{
		// Flatten nested IEnumerators so startup errors reach the return path instead of stranding the scene.
		var stack = new Stack<IEnumerator>();
		stack.Push(native);
		while (stack.Count > 0 && _session == session && (allowReturning || !session.Returning))
		{
			object current = null;
			bool moved = false;
			Exception failure = null;
			try { moved = stack.Peek().MoveNext(); if (moved) current = stack.Peek().Current; }
			catch (Exception ex) { failure = ex; }
			if (failure != null) { Fail(session, failure); yield break; }
			if (!moved) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
			if (current is IEnumerator nested) stack.Push(nested);
			else yield return current;
		}
		if (!finishEntry || _session != session || session.Returning) yield break;
		session.Entered = true;
		var hero = session.Manager.PlayerAction.Stat.Data;
		FileLogger.Log("[决斗练习] entered; CombatLevel=" + session.Level.name + "; CombatStat=" + session.Level.EnemyStat.name
			+ "; hero Six=" + string.Join(",", new[] { hero.Strength, hero.Stamina, hero.Dexterity, hero.Fist, hero.Sword, hero.MartialWeapon })
			+ "; Four=" + string.Join(",", new[] { hero.Disposition, hero.Behaviour, hero.Karma, hero.Training })
			+ "; HP=" + hero.DefaultHealth + "/" + hero.MaxHealth + "; energy=" + hero.DefaultStamina + "/" + hero.MaxStamina
			+ "; talents=" + string.Join(",", hero.TalentItems.Select(t => t.Data.Id + ":" + t.Level)));
	}

	private static IEnumerator Empty() { yield break; }

	private static bool Exit(ref IEnumerator result)
	{
		if (!IsActive) return true;
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

	// This iterator factory is shared by LoadTitle, LoadScene, LoadNextScene and LoadNewScene.
	[HarmonyPrefix, HarmonyPriority(Priority.First)]
	[HarmonyPatch(typeof(SceneController), "StartLoadScene")]
	private static bool SceneChange(ref IEnumerator __result)
	{
		if (!IsActive || _allowSceneChange > 0) return true;
		return Exit(ref __result);
	}

	/// <summary>Registered separately, since this plugin's PatchAll is scoped to one type.</summary>
	[HarmonyPatch]
	internal static class SaveBarrier
	{
		[HarmonyTargetMethods]
		private static IEnumerable<MethodBase> Targets()
		{
			// Include OUTER auto-save APIs: the mod's history prefix runs before the low-level writer.
			// Loads and metadata setters retain serialized references to the original data, so block them too.
			foreach (var method in typeof(SaveSystem).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				if (method.ReturnType == typeof(void) && method.Name != "LogMessage" && method.Name != "DisplayAchivement") yield return method;
			foreach (string name in new[] { "SaveButtonClick", "ConfirmSaveButtonClick", "LoadButtonClick", "OnSavePanelOpen" })
				yield return AccessTools.Method(typeof(MenuPanel), name);
			// A native delete's existing postfix would otherwise edit this mod's note sidecar.
			yield return AccessTools.Method(typeof(SaveNotePatch), nameof(SaveNotePatch.SetNote));
		}

		[HarmonyPrefix, HarmonyPriority(Priority.First)]
		private static bool Allow() => !IsActive;
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

		internal Session(DuelEncounter encounter, GalleryDuelPracticeSettings settings)
		{
			LevelId = encounter.Id ?? ""; StatId = encounter.StatId; Settings = settings;
			_originalPlayer = PlayerStatManagerData.Instance;
			_originalMissions = MissionManagerData.Instance;
			_random = UnityEngine.Random.state; _timeScale = Time.timeScale; _testMode = LevelTestMode.IsActive;
		}

		internal void Prepare()
		{
			_player = Graph.Copy(_originalPlayer);
			var missions = Graph.Copy(_originalMissions);
			// ResetActionCount consults MissionManagerData.Instance: swap BOTH before resetting either.
			AccessTools.Field(typeof(PlayerStatManagerData), "_instance").SetValue(null, _player);
			AccessTools.Field(typeof(MissionManagerData), "_instance").SetValue(null, missions);
			_player.ResetData();
			missions.ResetData();
			// A fresh practice hero has no fatigue from a previous story round.
			missions.GetFlag("Z00001").State = 1;
			var sixTypes = new[] { GameStatType.體力, GameStatType.內力, GameStatType.輕功, GameStatType.武功拳掌, GameStatType.武功刀劍, GameStatType.武功暗器 };
			var fourTypes = new[] { GameStatType.性情, GameStatType.處世, GameStatType.道德, GameStatType.修養 };
			for (int i = 0; i < sixTypes.Length; i++) SetRaw(sixTypes[i], Settings.Six[i]);
			for (int i = 0; i < fourTypes.Length; i++) SetRaw(fourTypes[i], Settings.Four[i]);
			SetRaw(GameStatType.嘴力, Settings.Talking);
			SetRaw(GameStatType.佛學, Settings.Mastery[1]); SetRaw(GameStatType.道學, Settings.Mastery[2]); SetRaw(GameStatType.儒學, Settings.Mastery[3]);
			SetRaw(GameStatType.全武學點數, Settings.Six.Sum());
			foreach (var pair in Settings.SkillLevels) _player.Talents.Set(pair.Key, pair.Value);
			_player.SetSceneKey(string.IsNullOrEmpty(LevelId) ? "GalleryPractice" : LevelId.Substring(3));
			_player.SetNextScene("Title");
			LevelTestMode.Begin();
		}

		private void SetRaw(GameStatType type, int value)
		{
			var stat = _player.Stats.Get(type);
			stat.SetMax(Math.Max(stat.Max, value));
			Traverse.Create(stat).Field("_value").SetValue(value);
			Traverse.Create(stat).Field("_limitAddition").SetValue(0);
			Traverse.Create(stat).Field("_statAdditionData").SetValue(Array.Empty<StatConvertCollectionData>());
		}

		internal void Attach(CombatManager manager)
		{
			Manager = manager;
			var config = Traverse.Create(manager).Field("_levelConfig").GetValue<CombatLevelConfig>();
			var source = string.IsNullOrEmpty(LevelId) ? config.List.FirstOrDefault() : config.Get(LevelId);
			if (source == null) throw new InvalidOperationException("原版 CombatLevel 不存在：" + LevelId);
			Level = Graph.Copy(source);
			if (string.IsNullOrEmpty(LevelId))
			{
				var stat = Resources.FindObjectsOfTypeAll<CombatStat>().FirstOrDefault(s => s.name == StatId && !Graph.IsCopy(s));
				if (stat == null) throw new InvalidOperationException("原版 CombatStat 不存在：" + StatId);
				Level.name = "CL_GalleryPractice";
				Traverse.Create(Level).Field("_enemyStat").SetValue(Graph.Copy(stat));
				Level.Events = new List<CombatEventData>();
				Traverse.Create(Level).Field("_talkingEvent").SetValue(null);
			}
			else if (Level.EnemyStat == null || Level.EnemyStat.name != StatId)
				throw new InvalidOperationException("所选 CombatLevel 的敌人配置与鉴赏目录不一致。请更新目录。");
			// Story result flags and death endings are never used by practice.
			Traverse.Create(Level).Field("_deadEnd").SetValue(false);
			Traverse.Create(Level).Field("_winResult").SetValue(Array.Empty<CombatResultFlag>());
			Traverse.Create(Level).Field("_loseResult").SetValue(Array.Empty<CombatResultFlag>());
			Traverse.Create(Level).Field("_conditionResult").SetValue(Array.Empty<CombatConditionResultFlag>());
			var isolatedConfig = ScriptableObject.CreateInstance<CombatLevelConfig>();
			Graph.Own(isolatedConfig);
			Traverse.Create(isolatedConfig).Field("_list").SetValue(new List<CombatLevel> { Level });
			Traverse.Create(manager).Field("_levelConfig").SetValue(isolatedConfig);
			// Includes inactive roots and inherited fields (round UI, stat controllers, effect database).
			foreach (var root in manager.gameObject.scene.GetRootGameObjects())
				foreach (var component in root.GetComponentsInChildren<MonoBehaviour>(true))
					if (component != null && NativeGraph.IsNative(component.GetType())) Graph.Rewire(component);
		}

		internal void AssignHero(CombatStat hero)
		{
			hero.Strength = Settings.Six[0]; hero.Stamina = Settings.Six[1]; hero.Dexterity = Settings.Six[2];
			hero.Fist = Settings.Six[3]; hero.Sword = Settings.Six[4]; hero.MartialWeapon = Settings.Six[5];
			hero.Disposition = Settings.Four[0]; hero.Behaviour = Settings.Four[1]; hero.Karma = Settings.Four[2]; hero.Training = Settings.Four[3];
			hero.StrategyLevel = Settings.Mastery[0]; hero.Buddhism = Settings.Mastery[1]; hero.Taoism = Settings.Mastery[2]; hero.Confucianism = Settings.Mastery[3]; hero.Xingyi = Settings.Mastery[4];
			hero.MaxHealth = Settings.MaxHealth; hero.DefaultHealth = Settings.Health;
			hero.MaxStamina = Settings.MaxEnergy; hero.DefaultStamina = Settings.Energy; hero.Talking = Settings.Talking;
			hero.TotalMartial = Settings.Six.Sum();
			hero.TalentItems = Settings.SkillLevels.Where(p => p.Value > 0).Select(p => new CombatTalentItem { Data = _player.Talents.Get(p.Key), Level = p.Value }).ToList();
			hero.Reset();
		}

		internal void Restore()
		{
			AccessTools.Field(typeof(PlayerStatManagerData), "_instance").SetValue(null, _originalPlayer);
			AccessTools.Field(typeof(MissionManagerData), "_instance").SetValue(null, _originalMissions);
			UnityEngine.Random.state = _random;
			Time.timeScale = _timeScale;
			if (_testMode) LevelTestMode.Begin(); else LevelTestMode.End();
			Graph.Dispose();
		}
	}

	/// <summary>Copies Unity's serialized native data graph. Prefabs/media/input actions stay immutable shared assets.</summary>
	private sealed class NativeGraph : IDisposable
	{
		private readonly Dictionary<object, object> _copies = new Dictionary<object, object>(new ReferenceComparer());
		private readonly List<ScriptableObject> _owned = new List<ScriptableObject>();
		private static readonly MethodInfo Memberwise = AccessTools.Method(typeof(object), "MemberwiseClone");
		internal static bool IsNative(Type type) => type.Assembly.GetName().Name.StartsWith("Mortal.", StringComparison.Ordinal);
		internal T Copy<T>(T original) where T : ScriptableObject => (T)CopyValue(original);
		internal bool IsCopy(object value) => value != null && _copies.TryGetValue(value, out var copy) && ReferenceEquals(value, copy);
		internal void Own(ScriptableObject value) { value.hideFlags = HideFlags.HideAndDontSave; _owned.Add(value); _copies[value] = value; }

		private object CopyValue(object value)
		{
			if (value == null) return null;
			var type = value.GetType();
			if (type.IsPrimitive || type.IsEnum || value is string || value is Delegate) return value;
			if (_copies.TryGetValue(value, out var previous)) return previous;
			if (value is ScriptableObject asset && IsNative(type))
			{
				var copy = UObject.Instantiate(asset);
				copy.name = asset.name; // Native collections and conditions resolve by exact Unity asset name.
				Own(copy); _copies[value] = copy;
				CopyFields(value, copy);
				return copy;
			}
			if (value is UObject) return value;
			if (value is Array array)
			{
				var copy = (Array)array.Clone(); _copies[value] = copy;
				for (int i = 0; i < array.Length; i++) copy.SetValue(CopyValue(array.GetValue(i)), i);
				return copy;
			}
			if (value is IList list)
			{
				var copy = (IList)Activator.CreateInstance(type); _copies[value] = copy;
				foreach (object item in list) copy.Add(CopyValue(item));
				return copy;
			}
			if (!IsNative(type)) return value;
			var result = Memberwise.Invoke(value, null); _copies[value] = result;
			CopyFields(value, result);
			return result;
		}

		internal void Rewire(object target) => CopyFields(target, target);
		private void CopyFields(object source, object target)
		{
			for (var type = source.GetType(); type != null && IsNative(type); type = type.BaseType)
				foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
					if (!field.IsInitOnly && !field.IsNotSerialized && (field.IsPublic || field.IsDefined(typeof(SerializeField), true)))
						field.SetValue(target, CopyValue(field.GetValue(source)));
		}

		public void Dispose()
		{
			for (int i = _owned.Count - 1; i >= 0; i--) if (_owned[i] != null) UObject.Destroy(_owned[i]);
			_owned.Clear(); _copies.Clear();
		}
	}

	private sealed class ReferenceComparer : IEqualityComparer<object>
	{
		public new bool Equals(object x, object y) => ReferenceEquals(x, y);
		public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
	}
}
