using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Battle;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Combat;

/// <summary>
/// 战役敌军士气（设置「战役敌军士气」，辅助及以上打开）：原版限时比人数、否则杀光，主角只是总人数里的一个。
/// 这里给普通战役加敌军士气：敌兵阵亡、主角亲手击杀、每十连杀、击败敌方首领都扣士气，归零时敌军溃散，走游戏原来的胜利流程。
/// 敌方首领是出兵配置里的特殊 NPC。带事件、不比全灭、场地自带暗器、我方人太少的战役不套用，强制胜败的剧情不受影响。
/// </summary>
internal sealed class BattleMorale : MonoBehaviour
{
	private const int MinFriends = 10;
	private const int MinEnemies = 20;
	private const float DeathLoss = 75f; // 全军覆没累计扣这么多：只靠同门硬拼打不崩，主角得出力
	private const float PlayerKillLoss = 40f;
	private const float EliteLoss = 15f;
	private const float StreakLoss = 5f;
	private const int StreakStep = 10;
	private const float RoutSeconds = 3f;

	private static BattleMorale _active;

	private readonly HashSet<string> _elites = new HashSet<string>();
	private float _morale = 100f;
	private int _enemies;
	private bool _routed;
	private GUIStyle _style;
	private float _scale = -1f;

	[HarmonyPostfix]
	[HarmonyPatch(typeof(NpcManager), nameof(NpcManager.Setup))]
	public static void Begin(NpcSpawner enemy)
	{
		_active = null;
		try
		{
			GameLevelManager manager = GameLevelManager.Instance;
			BattleLevel level = manager == null ? null : Traverse.Create(manager).Field("_levelData").GetValue<BattleLevel>();
			if (!ConfigManager.BattleMorale.Value || level == null || !Eligible(level))
			{
				return;
			}
			BattleMorale morale = manager.GetComponent<BattleMorale>() ?? manager.gameObject.AddComponent<BattleMorale>();
			morale.Setup(level, enemy);
			_active = morale;
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[战役] 士气初始化失败（已忽略）：" + e.Message);
		}
	}

	private static bool Eligible(BattleLevel level)
	{
		BattleEventData[] events = Traverse.Create(level).Field("_events").GetValue<BattleEventData[]>();
		return (events == null || events.Length == 0)
			&& level.VitoryDefeat
			&& (level.WeaponSpawnerPrefab == null || level.WeaponSpawnerPrefab.Length == 0)
			&& level.GetFriendPeople() >= MinFriends
			&& level.GetEnemyPeople() >= MinEnemies;
	}

	private void Setup(BattleLevel level, NpcSpawner enemy)
	{
		_morale = 100f;
		_routed = false;
		_enemies = level.GetEnemyPeople();
		_elites.Clear();
		NpcSpawnPreset[] specials = Traverse.Create(enemy).Field("_specialNPCs").GetValue<NpcSpawnPreset[]>();
		foreach (NpcSpawnPreset preset in specials ?? new NpcSpawnPreset[0])
		{
			if (preset?.Prefab != null)
			{
				_elites.Add(preset.Prefab.name);
			}
		}
		FileLogger.Log($"[战役] 士气开始：敌军 {_enemies} 人，敌将 {_elites.Count} 种");
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(NpcDeathState), nameof(NpcDeathState.OnEnter))]
	public static void EnemyDied(NpcCharacter ____character)
	{
		if (_active == null || ____character.Character.Entity.CurrentType != EntityType.Enemy)
		{
			return;
		}
		float loss = DeathLoss / _active._enemies;
		if (_active._elites.Contains(____character.gameObject.name.Replace("(Clone)", "")))
		{
			loss += EliteLoss;
		}
		_active.Lose(loss);
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(GameLevelManager), nameof(GameLevelManager.AddKillProgressLevel01))]
	public static void PlayerKilled(int ____currentMultiKills)
	{
		if (_active != null)
		{
			_active.Lose(PlayerKillLoss / _active._enemies + (____currentMultiKills % StreakStep == 0 ? StreakLoss : 0f));
		}
	}

	private void Lose(float amount)
	{
		_morale = Mathf.Max(0f, _morale - amount);
		if (_morale <= 0f && !_routed)
		{
			_routed = true;
			FileLogger.Log("[战役] 敌军溃散");
			StartCoroutine(Rout());
		}
	}

	private IEnumerator Rout()
	{
		foreach (Entity entity in EntityManager.Instance.AllEntities.Where(e => e != null && e.CurrentType == EntityType.Enemy).ToList())
		{
			Flee(entity);
		}
		yield return new WaitForSeconds(RoutSeconds);
		GameLevelManager manager = GameLevelManager.Instance;
		if (manager != null && !manager.IsGameOver)
		{
			manager.ShowGameOver(GameOverType.FriendWin, finish: true);
		}
	}

	private static void Flee(Entity entity)
	{
		try
		{
			NpcCharacter npc = entity.GetComponent<NpcCharacter>();
			CharacterStateMachine machine = entity.GetComponent<CharacterStateMachine>();
			if (npc != null && machine != null && !npc.Character.IsDead)
			{
				Traverse.Create(machine).Field("_stateMachine").GetValue<StateMachine>().SetState(new FleeState(npc));
			}
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[战役] 敌兵逃跑失败（已忽略）：" + e.Message);
		}
	}

	private void OnGUI()
	{
		GameLevelManager manager = GameLevelManager.Instance;
		if (_active != this || manager == null || !manager.IsReady || manager.IsGameOver || manager.IsPause
			|| !manager.DisplayUI || manager.IsEventPlaying || UiHider.Active)
		{
			return;
		}
		float scale = PanelTheme.UiScale;
		if (scale <= 0f)
		{
			return;
		}
		if (!Mathf.Approximately(scale, _scale))
		{
			_scale = scale;
			_style = PanelTheme.Toast(scale, 18);
			_style.alignment = TextAnchor.MiddleCenter;
		}
		string text = _routed ? TextConverter.ForDisplay("敌军溃散") : string.Format(TextConverter.ForDisplay("敌军士气 {0}"), Mathf.CeilToInt(_morale));
		float width = 200f * scale;
		float height = 40f * scale;
		float x = (Screen.width - width) / 2f;
		float y = 110f * scale;
		GUI.Label(new Rect(x, y, width, height), text, _style);
		GUI.DrawTexture(new Rect(x, y + height, width * _morale / 100f, 4f * scale), Texture2D.whiteTexture, ScaleMode.StretchToFill, true, 0f, new Color(0.85f, 0.35f, 0.25f, 0.9f), 0f, 0f);
	}
}
