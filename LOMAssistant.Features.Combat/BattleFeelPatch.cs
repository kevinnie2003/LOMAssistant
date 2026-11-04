using System.Linq;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Battle;
using Mortal.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LOMAssistant.Features.Combat;

/// <summary>战役里主角脚下的普攻范围圈：贴地的一圈细线，圈内的敌人普攻够得着。</summary>
internal sealed class BattleRangeRing : MonoBehaviour
{
	private const int Points = 72;
	private const float Lift = 0.1f;

	private LineRenderer _line;
	private PlayerCharacter _player;
	private CharacterController _controller;
	private Transform _ring;
	private float _radius;

	private void Start()
	{
		_player = GetComponent<PlayerCharacter>();
		_controller = GetComponent<CharacterController>();
		SpriteRenderer sprite = GetComponentInChildren<SpriteRenderer>(includeInactive: true);
		GameObject ring = new GameObject("LOMAssistant_RangeRing");
		_ring = ring.transform;
		_ring.SetParent(transform, worldPositionStays: false);
		_line = ring.AddComponent<LineRenderer>();
		_line.material = new Material(Shader.Find("Sprites/Default"));
		_line.startColor = _line.endColor = new Color(0.85f, 0.69f, 0.47f, 0.6f);
		_line.startWidth = _line.endWidth = 0.05f;
		_line.useWorldSpace = false;
		_line.loop = true;
		_line.alignment = LineAlignment.TransformZ;
		_line.positionCount = Points;
		_line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
		if (sprite != null)
		{
			ring.layer = sprite.gameObject.layer;
			_line.sortingLayerID = sprite.sortingLayerID;
		}
		_line.sortingOrder = short.MaxValue;
	}

	private void LateUpdate()
	{
		GameLevelManager level = GameLevelManager.Instance;
		bool show = ConfigManager.BattleFeel.Value && level != null && level.IsReady && !level.IsGameOver;
		_line.enabled = show;
		if (!show)
		{
			return;
		}
		Vector3 position = transform.position;
		position.y = _controller != null ? _controller.bounds.min.y + Lift : position.y + Lift;
		_ring.position = position;
		_ring.rotation = Quaternion.Euler(90f, 0f, 0f);
		float scale = Mathf.Max(Mathf.Abs(_ring.lossyScale.x), 0.01f);
		float radius = _player.AttackRadius / scale;
		if (Mathf.Approximately(radius, _radius))
		{
			return;
		}
		_radius = radius;
		for (int i = 0; i < Points; i++)
		{
			float angle = i * Mathf.PI * 2f / Points;
			_line.SetPosition(i, new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f));
		}
	}
}

/// <summary>
/// 战役手感优化（设置「战役手感优化」，辅助及以上打开，即时生效）。不改敌我数值和胜负规则：
/// 方向键每帧重读：游戏只在按键变化时读方向，硬直中按下的方向会被清掉，恢复后按住也不走；定身时松手则会一直走下去。
/// 普攻朝最近的敌人出招，判定半径放大、夹角放宽到正面半圆，不耗气；主角脚下画一圈普攻范围。
/// 主角绝招「奋力一击」范围加倍，同样朝最近的敌人出招。
/// 平射暗器飞向最近的敌人（原版只能朝左右），蓄力抛射不限前方，范围内有敌人就不落空。
/// 击杀缩短绝招冷却，连杀越高缩得越多。
/// 掉落物拾取范围放大。
/// 主角不受我方误伤：暗器、神奇球的绳网（定身 5 秒）、毒雾、火海、地雷都不再伤到主角，其他人照旧。
/// 战役结算不扣属性：反向积极度随时间涨，不挨打 30 秒就到三档，这一档固定扣性情和贡献，打得再好也躲不开。
/// </summary>
internal static class BattleFeelPatch
{
	private const float ReachScale = 1.5f;
	private const float AttackAngle = 90f;
	private const float FaceExtra = 0.5f; // 判定算碰撞体、转身算中心点：多留一点，圈边上的敌人也能转过去
	private const float SpecialScale = 2f;
	private const float PickupRadius = 2.5f;
	private const float KnifeAimRange = 12f;
	private const float KnifeFlight = 10f; // 和游戏的水平飞行距离一致
	private const float KillCooldownCut = 0.5f;
	private const float StreakCooldownCut = 0.1f;
	private const int StreakCap = 10;

	// 记在我方毒雾、地雷上的标记
	private sealed class OurSide : MonoBehaviour
	{
	}

	private static bool _ourThrow, _ourCast, _ourBlast;

	private static bool On => ConfigManager.BattleFeel.Value;

	[HarmonyPostfix]
	[HarmonyPatch(typeof(PlayerController), "Update")]
	public static void ReadMove(PlayerCharacter ____player, ref Vector3 ____moveDelta, InputAction ____moveAction)
	{
		GameLevelManager level = GameLevelManager.Instance;
		if (!On || level == null || !level.IsReady || level.IsPause || level.IsEventPlaying
			|| ____player.Character.LoseControl || ____player.Character.Movement.CurrentSpeed <= 0f)
		{
			return;
		}
		____moveDelta = ____moveAction.ReadValue<Vector2>();
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.AttackRadius), MethodType.Getter)]
	public static void Radius(ref float __result)
	{
		if (On)
		{
			__result *= ReachScale;
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.AttackRangeAngle), MethodType.Getter)]
	public static void Angle(ref float __result)
	{
		if (On)
		{
			__result = Mathf.Max(__result, AttackAngle);
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.CostAttack1), MethodType.Getter)]
	public static void AttackCost(ref int __result)
	{
		if (On)
		{
			__result = 0;
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.CanAttack1))]
	public static void CanAttack(ref bool __result)
	{
		if (On)
		{
			__result = true;
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(PlayerMeleeAttackState), nameof(PlayerMeleeAttackState.OnEnter))]
	public static void FaceAttackTarget(Character ____character, PlayerCharacter ____player)
	{
		if (On)
		{
			FaceNearest(____character, ____player.AttackRadius + FaceExtra);
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(PlayerRangerAttackState), nameof(PlayerRangerAttackState.OnEnter))]
	public static void FaceKnifeTarget(Character ____character)
	{
		if (On)
		{
			FaceNearest(____character, KnifeAimRange);
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(PlayerHunterAttackState), nameof(PlayerHunterAttackState.OnEnter))]
	public static void FaceBallTarget(Character ____character)
	{
		if (On)
		{
			FaceNearest(____character, Traverse.Create(____character.GetComponent<PlayerCharacter>()).Field("_hunterSearchRadius").GetValue<float>());
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(PlayerCharacter), "Attack2Handler")]
	public static bool AimKnife(PlayerCharacter __instance, RangerWeapon ____rangerWeapon)
	{
		if (On && Nearest(__instance.Character, KnifeAimRange, out Vector3 target))
		{
			____rangerWeapon.Attack(target, __instance.Character.AttackMultiplier.ModifierRate);
			return false;
		}
		return true;
	}

	// 游戏把平射的落点算成正左或正右；主角扔的改成沿着瞄准的方向
	[HarmonyPostfix]
	[HarmonyPatch(typeof(WeaponLauncher), "GetHorizontalTargetPosition")]
	public static void KnifeDirection(Character ____character, Vector3 launchPosition, Vector3 target, ref Vector3 __result)
	{
		if (On && ____character != null && ____character.Entity.CurrentType == EntityType.Player)
		{
			Vector3 direction = target - launchPosition;
			direction.y = 0f;
			__result = launchPosition + direction.normalized * KnifeFlight;
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(PlayerCharacter), "Attack3Handler")]
	public static bool AimBall(PlayerCharacter __instance, HunterWeapon ____hunterWeapon, float ____hunterSearchRadius)
	{
		if (On && Nearest(__instance.Character, ____hunterSearchRadius, out Vector3 target))
		{
			____hunterWeapon.Attack(target, __instance.Character.AttackMultiplier.ModifierRate);
			return false;
		}
		return true;
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(GameLevelManager), nameof(GameLevelManager.AddKillProgressLevel01))]
	public static void CutCooldowns(int ____currentMultiKills)
	{
		SkillManager skills = SkillManager.Instance;
		if (!On || skills == null)
		{
			return;
		}
		float cut = KillCooldownCut + StreakCooldownCut * Mathf.Min(____currentMultiKills, StreakCap);
		var tr = Traverse.Create(skills);
		foreach (string field in new[] { "_button_1", "_button_2", "_button_3", "_button_4" })
		{
			var time = Traverse.Create(tr.Field(field).GetValue<SkillButton>()).Field("_currentCooldownTime");
			time.SetValue(time.GetValue<float>() - cut);
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(PlayerSpecialAttack1_State), nameof(PlayerSpecialAttack1_State.OnEnter))]
	public static void FaceSpecialTarget(Character ____character, PlayerCharacter ____player)
	{
		if (On)
		{
			FaceNearest(____character, Traverse.Create(____player).Field("_specialAttackRadius").GetValue<float>() * SpecialScale + FaceExtra);
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(PlayerCharacter), "SpecialAttack1_Handler")]
	[HarmonyPatch(typeof(PlayerCharacter), "SpecialAttack2_Handler")]
	public static void BeginSpecial(ref float ____specialAttackRadius, out float __state)
	{
		__state = ____specialAttackRadius;
		if (On)
		{
			____specialAttackRadius *= SpecialScale;
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(PlayerCharacter), "SpecialAttack1_Handler")]
	[HarmonyPatch(typeof(PlayerCharacter), "SpecialAttack2_Handler")]
	public static void EndSpecial(ref float ____specialAttackRadius, float __state)
	{
		____specialAttackRadius = __state;
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(PlayerCharacter), "Start")]
	public static void AddRing(PlayerCharacter __instance)
	{
		__instance.gameObject.AddComponent<BattleRangeRing>();
	}

	private static void FaceNearest(Character character, float reach)
	{
		if (Nearest(character, reach, out Vector3 target))
		{
			character.Animation.FlipX(target.x - character.transform.position.x);
		}
	}

	private static bool Nearest(Character character, float reach, out Vector3 target)
	{
		Vector3 from = character.transform.position;
		float nearest = reach * reach;
		target = from;
		bool found = false;
		foreach (Entity entity in EntityManager.Instance.AllEntities)
		{
			if (entity == null || !entity.CanTargeted || entity.CurrentType != EntityType.Enemy)
			{
				continue;
			}
			Vector3 offset = entity.transform.position - from;
			offset.y = 0f;
			if (offset.sqrMagnitude >= nearest)
			{
				continue;
			}
			Character candidate = entity.GetComponent<Character>();
			if (candidate != null && !candidate.IsDead)
			{
				nearest = offset.sqrMagnitude;
				target = entity.transform.position;
				found = true;
			}
		}
		return found;
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(DropItem), nameof(DropItem.Setup))]
	public static void PickupRange(DropItem __instance, Collider ____triggerCollider)
	{
		if (!On || ____triggerCollider == null)
		{
			return;
		}
		Vector3 scale = __instance.transform.lossyScale;
		float local = PickupRadius / Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z), 0.01f);
		switch (____triggerCollider)
		{
		case SphereCollider sphere:
			sphere.radius = Mathf.Max(sphere.radius, local);
			break;
		case BoxCollider box:
			box.size = new Vector3(Mathf.Max(box.size.x, local * 2f), box.size.y, Mathf.Max(box.size.z, local * 2f));
			break;
		}
	}

	// 游戏判定目标时写死「主角一律能打中」，所以瞄敌人的我方暗器、绳网、火海都会伤到主角
	[HarmonyPostfix]
	[HarmonyPatch(typeof(WeaponUtils), nameof(WeaponUtils.CanAttackEntity))]
	public static void OwnWeapons(WeaponTarget weaponTarget, EntityType entityType, ref bool __result)
	{
		if (On && entityType == EntityType.Player && weaponTarget == WeaponTarget.Enemy)
		{
			__result = false;
		}
	}

	// 二师兄的毒雾不分敌我（同门照样中毒），只把主角摘出去。毒雾在 OnAttack 里当场生成，这里记下是不是我方扔的
	[HarmonyPrefix]
	[HarmonyPatch(typeof(BallWeapon), "OnAttack")]
	public static void NoteThrow(BallWeapon __instance)
	{
		_ourThrow = __instance.WeaponTarget == WeaponTarget.Enemy;
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(BallWeapon), "OnAttack")]
	public static void EndThrow()
	{
		_ourThrow = false;
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(AreaEffectState), nameof(AreaEffectState.Setup))]
	public static void MarkArea(AreaEffectState __instance)
	{
		if (_ourThrow)
		{
			__instance.gameObject.AddComponent<OurSide>();
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(AreaEffectState), "GetAttackableEntity")]
	public static void SkipPlayer(AreaEffectState __instance, ref Entity[] __result)
	{
		if (On && __instance.GetComponent<OurSide>() != null)
		{
			__result = __result.Where(entity => entity.CurrentType != EntityType.Player).ToArray();
		}
	}

	// 同门倒下时留下的地雷
	[HarmonyPrefix]
	[HarmonyPatch(typeof(WarriorNpc), "CharacterDieHandler")]
	[HarmonyPatch(typeof(WarriorNpc_01), "CharacterDieHandler")]
	[HarmonyPatch(typeof(WarriorNpc_02), "CharacterDieHandler")]
	[HarmonyPatch(typeof(WarriorNpc_03), "CharacterDieHandler")]
	public static void NoteCaster(Component __instance)
	{
		_ourCast = __instance.GetComponent<Entity>().CurrentType == EntityType.Friend;
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(WarriorNpc), "CharacterDieHandler")]
	[HarmonyPatch(typeof(WarriorNpc_01), "CharacterDieHandler")]
	[HarmonyPatch(typeof(WarriorNpc_02), "CharacterDieHandler")]
	[HarmonyPatch(typeof(WarriorNpc_03), "CharacterDieHandler")]
	public static void EndCast()
	{
		_ourCast = false;
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(TrapWeapon), "Awake")]
	public static void MarkTrap(TrapWeapon __instance)
	{
		if (_ourCast)
		{
			__instance.gameObject.AddComponent<OurSide>();
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(TrapWeapon), "TriggerAttack")]
	public static void BeginBlast(TrapWeapon __instance)
	{
		_ourBlast = On && __instance.GetComponent<OurSide>() != null;
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(TrapWeapon), "TriggerAttack")]
	public static void EndBlast()
	{
		_ourBlast = false;
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(Character), nameof(Character.TakeDamage))]
	public static bool BlastSkipsPlayer(Character __instance, ref bool __result)
	{
		if (_ourBlast && __instance.Entity.CurrentType == EntityType.Player)
		{
			__result = true;
			return false;
		}
		return true;
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(ProgressLevelData), nameof(ProgressLevelData.GetCurrentModifyStat))]
	public static void NoPenalty(ref MartialModifyStat[] __result)
	{
		if (On && __result != null)
		{
			__result = __result.Where(stat => stat.Value >= 0).ToArray();
		}
	}
}
