using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Combat;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Combat;

/// <summary>
/// 决斗玩法重做（设置「决斗玩法重做」）的追击与压制：同时选招和克制关系都不变，只改赢了以后发生什么。
/// 捅人赢嘴攻时追击一下；暗器赢捅人或嘴攻时压制对方，下回合不能用刚才的指令；嘴攻赢备揍时破防，下回合不能备揍；
/// 嘴攻赢绝招时绝招落空；备揍挡住捅人或暗器时反击，回气，下回合追击和压制都翻倍。
/// 绝招赢捅人或暗器时对方震撼，绝招打备揍时对方失衡；同招相遇时比对应数值，高的一方追击，备揍相遇改成回气。敌我规则相同。
/// 同一个指令不会连着被压制两次，免得一路翻不了身；压制也不会让人无招可用。
/// </summary>
internal static class DuelPressure
{
	private sealed class Side
	{
		public CombatActionType Locked;
		public int Rounds, RecentRounds;
		public CombatActionType Recent;
		public bool Boost, BoostNext;

		public void Tick()
		{
			if (Rounds > 0)
			{
				if (--Rounds == 0)
				{
					Recent = Locked;
					RecentRounds = 1;
				}
			}
			else if (RecentRounds > 0 && --RecentRounds == 0)
			{
				Recent = CombatActionType.無動作;
			}
		}
	}

	private static readonly MethodInfo Attack = AccessTools.Method(typeof(CombatManager), "AttackActionHandle");

	private const float PursuitRate = 0.5f;
	private const int CounterStamina = 10;
	private const string Shaken = "D012", Staggered = "D017"; // 游戏自带的震撼、失衡
	private const float Pause = 0.5f;

	private static Side _player = new Side(), _enemy = new Side();
	private static float _damageRate = 1f;

	internal static bool On => ConfigManager.DuelRework.Value;
	internal static string PlayerLocked => _player.Rounds > 0 ? Name(_player.Locked) : null;
	internal static string EnemyLocked => _enemy.Rounds > 0 ? Name(_enemy.Locked) : null;

	internal static string CommandName(CombatActionType action) => Name(action);

	private static string Name(CombatActionType action) => LocalizationManager.Instance.LocaleResolver.GetString("CombatAction/" + (action switch
	{
		CombatActionType.嘴砲 => "curse",
		CombatActionType.捅人 => "attack",
		CombatActionType.暗器 => "weapon",
		CombatActionType.備揍 => "block",
		_ => "ultimate",
	}));

	private static CombatActionType Slot(CombatActionType action) =>
		action == CombatActionType.絕招2 || action == CombatActionType.絕招3 ? CombatActionType.絕招1 : action;

	[HarmonyPrefix]
	[HarmonyPatch(typeof(CombatManager), "InitCombatRound")]
	public static void Reset()
	{
		_player = new Side();
		_enemy = new Side();
		_damageRate = 1f;
		DuelPressurePanel.Close();
	}

	private static Side SideOf(CombatActionController controller)
	{
		CombatManager manager = CombatManager.Instance;
		return !On || manager == null ? null : controller == manager.PlayerAction ? _player : controller == manager.EnemyAction ? _enemy : null;
	}

	private static void Check(CombatActionController controller, CombatActionType action, ref bool result)
	{
		Side side = SideOf(controller);
		if (side != null && side.Rounds > 0 && side.Locked == action)
		{
			result = false;
		}
	}

	// 压制不能让人无招可用：只剩被封的指令能用时解除
	[HarmonyPostfix]
	[HarmonyPatch(typeof(CombatActionController), nameof(CombatActionController.CanUseAnyAction))]
	public static void NeverStuck(CombatActionController __instance, ref bool __result)
	{
		Side side = SideOf(__instance);
		if (!__result && side != null && side.Rounds > 0 && __instance.Stat.CanAction)
		{
			side.Rounds = 0;
			__result = __instance.CanUseAnyAction();
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(CombatActionController), nameof(CombatActionController.CanUseTalkAction))]
	public static void LockTalk(CombatActionController __instance, ref bool __result) => Check(__instance, CombatActionType.嘴砲, ref __result);

	[HarmonyPostfix]
	[HarmonyPatch(typeof(CombatActionController), nameof(CombatActionController.CanUseAttackAction))]
	public static void LockAttack(CombatActionController __instance, ref bool __result) => Check(__instance, CombatActionType.捅人, ref __result);

	[HarmonyPostfix]
	[HarmonyPatch(typeof(CombatActionController), nameof(CombatActionController.CanUseBlockAction))]
	public static void LockBlock(CombatActionController __instance, ref bool __result) => Check(__instance, CombatActionType.備揍, ref __result);

	// 嘴攻赢绝招：绝招落空
	[HarmonyPostfix]
	[HarmonyPatch(typeof(CombatActionController), nameof(CombatActionController.GetTalkUltimateHitRate))]
	public static void BreakUltimate(CombatActionController __instance, ref float __result)
	{
		if (On && !__instance.TargetAction.Stat.IgnoreTalkDebuff)
		{
			__result = 0f;
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(CombatManager), "GetTotalDamage")]
	public static void ScaleDamage(ref int initDamage)
	{
		if (On && !Mathf.Approximately(_damageRate, 1f))
		{
			initDamage = Mathf.RoundToInt(initDamage * _damageRate);
		}
	}

	// 原版结算完再追加效果
	[HarmonyPostfix]
	[HarmonyPatch(typeof(CombatManager), "ActionRoundHandle")]
	public static void AfterRound(CombatManager __instance, ref IEnumerator __result)
	{
		if (On && __result != null)
		{
			__result = Wrap(__result, __instance);
		}
	}

	private static IEnumerator Wrap(IEnumerator round, CombatManager manager)
	{
		yield return round;
		_player.Tick();
		_enemy.Tick();
		FileLogger.Log($"[决斗] 本回合：主角 {manager.PlayerAction.CurrentActionResult.Action}，敌人 {manager.EnemyAction.CurrentActionResult.Action}");
		yield return Win(manager, manager.PlayerAction, manager.EnemyAction, _player, _enemy);
		yield return Win(manager, manager.EnemyAction, manager.PlayerAction, _enemy, _player);
		yield return Mirror(manager);
		_player.Boost = _player.BoostNext;
		_enemy.Boost = _enemy.BoostNext;
		_player.BoostNext = _enemy.BoostNext = false;
	}

	private static IEnumerator Say(CombatManager manager, string text, CombatActionController side)
	{
		FileLogger.Log("[决斗] 克制效果：" + text);
		manager.DisplayMessage(text, side);
		yield return new WaitForSeconds(Pause);
	}

	private static IEnumerator Pursue(CombatManager manager, CombatActionController self, CombatActionController target, Side mine)
	{
		yield return Say(manager, TextConverter.ForDisplay("追击"), self);
		self.SetCurrentDamageResult(CombatActionType.捅人);
		_damageRate = mine.Boost ? 1f : PursuitRate;
		yield return (IEnumerator)Attack.Invoke(manager, new object[] { self, target, target.CurrentActionResult.Action, true });
		_damageRate = 1f;
	}

	private static float Rank(CombatStatController stat, CombatActionType action) => action switch
	{
		CombatActionType.暗器 => stat.MartialWeapon.FinalValue,
		CombatActionType.嘴砲 => stat.Talking.FinalValue,
		CombatActionType.絕招1 => stat.Stamina.FinalValue,
		_ => stat.Dexterity.FinalValue,
	};

	// 同招相遇：比对应数值，高的一方抢攻
	private static IEnumerator Mirror(CombatManager manager)
	{
		CombatActionController player = manager.PlayerAction, enemy = manager.EnemyAction;
		CombatActionResult a = player.CurrentActionResult, b = enemy.CurrentActionResult;
		CombatActionType action = Slot(a.Action);
		if (action == CombatActionType.無動作 || action != Slot(b.Action) || player.IsDead || enemy.IsDead)
		{
			yield break;
		}
		float mine = Rank(player.Stat, action), theirs = Rank(enemy.Stat, action);
		if (Mathf.Approximately(mine, theirs))
		{
			yield break;
		}
		CombatActionController winner = mine > theirs ? player : enemy, loser = mine > theirs ? enemy : player;
		yield return Say(manager, TextConverter.ForDisplay("抢攻"), winner);
		if (action == CombatActionType.備揍)
		{
			yield return manager.DisplayStamina(CounterStamina, winner);
			yield return winner.ModifyStamina(CounterStamina);
		}
		else
		{
			yield return Pursue(manager, winner, loser, winner == player ? _player : _enemy);
		}
	}

	private static IEnumerator Win(CombatManager manager, CombatActionController self, CombatActionController target, Side mine, Side theirs)
	{
		if (self.IsDead || target.IsDead)
		{
			yield break;
		}
		CombatActionResult result = self.CurrentActionResult, other = target.CurrentActionResult;
		CombatActionType action = Slot(result.Action), against = Slot(other.Action);
		if (action == CombatActionType.備揍 && (against == CombatActionType.捅人 || against == CombatActionType.暗器) && other.IsSuccess && !other.IsHit)
		{
			mine.BoostNext = true;
			yield return Say(manager, TextConverter.ForDisplay("反击"), self);
			yield return manager.DisplayStamina(CounterStamina, self);
			yield return self.ModifyStamina(CounterStamina);
			yield break;
		}
		if (!result.IsHit)
		{
			yield break;
		}
		bool talkWorks = !target.Stat.IgnoreTalkDebuff;
		if (action == CombatActionType.捅人 && against == CombatActionType.嘴砲)
		{
			yield return Pursue(manager, self, target, mine);
		}
		else if (action == CombatActionType.暗器 && (against == CombatActionType.捅人 || against == CombatActionType.嘴砲))
		{
			yield return Lock(manager, target, theirs, against, mine.Boost);
		}
		else if (action == CombatActionType.嘴砲 && against == CombatActionType.備揍 && talkWorks)
		{
			yield return Lock(manager, target, theirs, against, mine.Boost);
		}
		else if (action == CombatActionType.嘴砲 && against == CombatActionType.絕招1 && talkWorks)
		{
			yield return Say(manager, TextConverter.ForDisplay("破招"), target);
		}
		else if (action == CombatActionType.絕招1 && (against == CombatActionType.捅人 || against == CombatActionType.暗器))
		{
			yield return Say(manager, TextConverter.ForDisplay("重创"), target);
			yield return target.AddStateEffect(Shaken, 0);
		}
		else if (action == CombatActionType.絕招1 && against == CombatActionType.備揍)
		{
			yield return Say(manager, TextConverter.ForDisplay("破势"), target);
			yield return target.AddStateEffect(Staggered, 0);
		}
	}

	private static IEnumerator Lock(CombatManager manager, CombatActionController target, Side side, CombatActionType action, bool boost)
	{
		// 被强制只能出某招的一方（比如愤怒）不压制
		if (target.Stat.CrazyMode != CombatCrazyMode.None || side.Recent == action || (side.Rounds > 0 && side.Locked == action))
		{
			yield break;
		}
		side.Locked = action;
		side.Rounds = boost ? 2 : 1;
		yield return Say(manager, string.Format(TextConverter.ForDisplay("下回合不能用{0}"), Name(action)), target);
	}
}

/// <summary>显示这回合哪方的哪个指令被压制，放在屏幕上方中间，避开左边的技能表；第一次进入决斗时先弹出规则说明。</summary>
internal sealed class DuelPressurePanel : MonoBehaviour
{
	private static readonly (CombatActionType Win, CombatActionType[] Lose, string Title, string Text)[] Guide =
	{
		(CombatActionType.捅人, new[] { CombatActionType.嘴砲 }, "追击", "再打一下，伤害减半"),
		(CombatActionType.暗器, new[] { CombatActionType.捅人, CombatActionType.嘴砲 }, "压制", "对方下回合不能用被打中的指令"),
		(CombatActionType.嘴砲, new[] { CombatActionType.備揍 }, "破防", "对方下回合不能备揍"),
		(CombatActionType.嘴砲, new[] { CombatActionType.絕招1 }, "破招", "绝招落空，气照扣"),
		(CombatActionType.絕招1, new[] { CombatActionType.捅人, CombatActionType.暗器 }, "重创", "对方震撼一回合，受到伤害增加五成"),
		(CombatActionType.絕招1, new[] { CombatActionType.備揍 }, "破势", "对方失衡两回合，轻功减半"),
		(CombatActionType.備揍, new[] { CombatActionType.捅人, CombatActionType.暗器 }, "反击", "回气，下回合追击和压制翻倍"),
		(CombatActionType.無動作, new CombatActionType[0], "抢攻", "比对应数值，高的一方追击，备揍相遇则回气"),
	};

	private static readonly string[] GuideNotes =
	{
		"这些规则敌我相同",
		"同一个指令不会连着被压制两次",
		"敌人会看双方的气、血量和你连续出的招",
	};

	private static DuelPressurePanel _instance;
	private static bool _guideOpen;

	private GUIStyle _style, _title, _body, _button, _effect, _note, _muted, _arrow, _chip, _tag;
	private float _scale = -1f;
	private string _text;

	public static void Ensure(GameObject host)
	{
		if (_instance == null)
		{
			_instance = host.GetComponent<DuelPressurePanel>() ?? host.AddComponent<DuelPressurePanel>();
		}
	}

	internal static void Close()
	{
		if (_instance != null)
		{
			_instance._text = null;
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(CombatUIManager), nameof(CombatUIManager.ShowAction))]
	public static void Show(ref IEnumerator __result)
	{
		if (_instance == null || !DuelPressure.On)
		{
			return;
		}
		if (!ConfigManager.DuelReworkGuideShown.Value && __result != null)
		{
			_guideOpen = true;
			__result = AfterGuide(__result);
		}
		string enemy = DuelPressure.EnemyLocked, player = DuelPressure.PlayerLocked;
		string[] lines =
		{
			enemy == null ? null : string.Format(TextConverter.ForDisplay("敌方本回合不能用：{0}"), enemy),
			player == null ? null : string.Format(TextConverter.ForDisplay("我方本回合不能用：{0}"), player),
		};
		_instance._text = string.Join("\n", lines.Where(line => line != null));
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(CombatUIManager), nameof(CombatUIManager.HideAction))]
	public static void Hide() => Close();

	// 看完说明才显示指令按钮
	private static IEnumerator AfterGuide(IEnumerator show)
	{
		yield return new WaitUntil(() => !_guideOpen);
		yield return show;
	}

	private static Color ChipColor(CombatActionType action) => action switch
	{
		CombatActionType.捅人 => new Color(0.58f, 0.27f, 0.22f),
		CombatActionType.暗器 => new Color(0.27f, 0.42f, 0.36f),
		CombatActionType.嘴砲 => new Color(0.55f, 0.43f, 0.22f),
		CombatActionType.備揍 => new Color(0.29f, 0.37f, 0.48f),
		_ => new Color(0.42f, 0.31f, 0.47f),
	};

	// 纯色块用白贴图染色：Framed 贴图直接拉伸时边框也会跟着放大
	private static void Solid(Rect rect, Color color)
	{
		Color old = GUI.color;
		GUI.color = color;
		GUI.DrawTexture(rect, Texture2D.whiteTexture);
		GUI.color = old;
	}

	private static void Outline(Rect rect, Color color, float width)
	{
		Solid(new Rect(rect.x, rect.y, rect.width, width), color);
		Solid(new Rect(rect.x, rect.yMax - width, rect.width, width), color);
		Solid(new Rect(rect.x, rect.y, width, rect.height), color);
		Solid(new Rect(rect.xMax - width, rect.y, width, rect.height), color);
	}

	private void Chip(Rect rect, CombatActionType action)
	{
		Solid(rect, ChipColor(action));
		GUI.Label(rect, DuelPressure.CommandName(action), _chip);
	}

	private void DrawGuide(float scale)
	{
		Solid(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.6f));
		float width = 760f * scale, pad = 32f * scale, inner = width - pad * 2f;
		float rowHeight = 44f * scale, chipHeight = 32f * scale, chipGap = 6f * scale, arrowWidth = 30f * scale;
		float line = Mathf.Max(1f, scale);
		CombatActionType[] commands = Guide.Select(row => row.Win).Concat(Guide.SelectMany(row => row.Lose)).Where(action => action != CombatActionType.無動作).Distinct().ToArray();
		float chipWidth = commands.Max(action => _chip.CalcSize(new GUIContent(DuelPressure.CommandName(action))).x) + 20f * scale;
		float titleWidth = Guide.Max(row => _effect.CalcSize(new GUIContent(TextConverter.ForDisplay(row.Title))).x) + 16f * scale;

		string title = TextConverter.ForDisplay("决斗玩法已重做");
		string tag = TextConverter.ForDisplay("测试中");
		string warning = TextConverter.ForDisplay("数值可能不平衡，这个玩法以后可能重做或删除，欢迎提意见");
		string subtitle = TextConverter.ForDisplay("克制关系不变，赢了以后多一个效果");
		string notes = string.Join("\n", GuideNotes.Select(note => "·  " + TextConverter.ForDisplay(note)));
		float titleHeight = _title.CalcHeight(new GUIContent(title), inner);
		float warningHeight = _muted.CalcHeight(new GUIContent(warning), inner);
		float subHeight = _body.CalcHeight(new GUIContent(subtitle), inner);
		float notesHeight = _muted.CalcHeight(new GUIContent(notes), inner);
		float buttonHeight = 44f * scale;
		float height = pad * 2f + titleHeight + 6f * scale + warningHeight + 22f * scale + subHeight + 8f * scale
			+ rowHeight * Guide.Length + 16f * scale + notesHeight + 22f * scale + buttonHeight;
		Rect box = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);
		Color paper = PanelTheme.Paper;
		paper.a = 1f;
		Solid(box, paper);
		Outline(box, PanelTheme.Edge, line);
		InputGuard.Report("DuelGuide", box);

		float x = box.x + pad, y = box.y + pad;
		float titleTextWidth = _title.CalcSize(new GUIContent(title)).x;
		GUI.Label(new Rect(x, y, inner, titleHeight), title, _title);
		float tagWidth = _tag.CalcSize(new GUIContent(tag)).x + 20f * scale, tagHeight = 26f * scale;
		Rect tagRect = new Rect(x + titleTextWidth + 14f * scale, y + (titleHeight - tagHeight) / 2f, tagWidth, tagHeight);
		Solid(tagRect, PanelTheme.Cinnabar);
		GUI.Label(tagRect, tag, _tag);
		y += titleHeight + 6f * scale;
		GUI.Label(new Rect(x, y, inner, warningHeight), warning, _muted);
		y += warningHeight + 22f * scale;
		GUI.Label(new Rect(x, y, inner, subHeight), subtitle, _body);
		y += subHeight + 8f * scale;

		float loseX = x + chipWidth + arrowWidth;
		float effectX = loseX + chipWidth * 2f + chipGap + 24f * scale;
		float textX = effectX + titleWidth;
		Solid(new Rect(x, y, inner, line), PanelTheme.InkLine);
		foreach (var row in Guide)
		{
			float chipY = y + (rowHeight - chipHeight) / 2f;
			if (row.Win == CombatActionType.無動作)
			{
				GUI.Label(new Rect(x, y, effectX - x, rowHeight), TextConverter.ForDisplay("同招相遇"), _note);
			}
			else
			{
				Chip(new Rect(x, chipY, chipWidth, chipHeight), row.Win);
				GUI.Label(new Rect(x + chipWidth, chipY, arrowWidth, chipHeight), "→", _arrow);
			}
			for (int i = 0; i < row.Lose.Length; i++)
			{
				Chip(new Rect(loseX + i * (chipWidth + chipGap), chipY, chipWidth, chipHeight), row.Lose[i]);
			}
			GUI.Label(new Rect(effectX, y, titleWidth, rowHeight), TextConverter.ForDisplay(row.Title), _effect);
			GUI.Label(new Rect(textX, y, x + inner - textX, rowHeight), TextConverter.ForDisplay(row.Text), _note);
			y += rowHeight;
			Solid(new Rect(x, y - line, inner, line), PanelTheme.InkLine);
		}
		y += 16f * scale;
		GUI.Label(new Rect(x, y, inner, notesHeight), notes, _muted);
		y += notesHeight + 22f * scale;
		float buttonWidth = 160f * scale;
		if (GUI.Button(new Rect(x + inner - buttonWidth, y, buttonWidth, buttonHeight), TextConverter.ForDisplay("知道了"), _button))
		{
			_guideOpen = false;
			ConfigManager.DuelReworkGuideShown.Value = true;
		}
	}

	private void OnGUI()
	{
		if (CombatManager.Instance == null || (!_guideOpen && (string.IsNullOrEmpty(_text) || !DuelPressure.On || UiHider.Active)))
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
			_style.alignment = TextAnchor.UpperCenter;
			_style.wordWrap = false;
			_title = PanelTheme.Label(scale, 28, PanelTheme.Ink, false);
			_body = PanelTheme.Label(scale, 19, PanelTheme.Ink);
			_muted = PanelTheme.Label(scale, 17, PanelTheme.InkMuted);
			_effect = PanelTheme.Label(scale, 19, PanelTheme.Cinnabar, false);
			_effect.alignment = TextAnchor.MiddleLeft;
			_note = PanelTheme.Label(scale, 17, PanelTheme.Ink);
			_note.alignment = TextAnchor.MiddleLeft;
			_arrow = PanelTheme.Label(scale, 18, PanelTheme.InkMuted, false);
			_arrow.alignment = TextAnchor.MiddleCenter;
			_button = PanelTheme.Primary(scale, 19);
			_chip = PanelTheme.Label(scale, 17, PanelTheme.OnCinnabar, false);
			_chip.alignment = TextAnchor.MiddleCenter;
			_tag = PanelTheme.Label(scale, 15, PanelTheme.OnCinnabar, false);
			_tag.alignment = TextAnchor.MiddleCenter;
		}
		if (_guideOpen)
		{
			DrawGuide(scale);
			return;
		}
		float width = 420f * scale;
		float height = _style.CalcHeight(new GUIContent(_text), width);
		Rect box = new Rect((Screen.width - width) / 2f, Screen.height * 0.06f, width, height);
		GUI.Box(box, _text, PanelTheme.Sync(_style));
		InputGuard.Report("DuelPressure", box);
	}
}
