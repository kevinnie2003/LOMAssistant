using System.Collections.Generic;
using System.Linq;
using LOMAssistant.Features.Story;
using Mortal.Battle;
using Mortal.Combat;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Combat;

/// <summary>
/// 这场战斗会怎样：打赢、打输接到哪里，决斗里拖到第几回合有什么事。屏幕上方的回合提示和「此刻」页共用。
/// 回合事件读关卡的 Events，只留结束战斗和改旗标的，对白、数值变化不算。到回合结束战斗时游戏不写胜负结果，
/// 胜负旗标留在开打时的值，所以除非事件自己写了胜利结果，都算未获胜。
/// </summary>
internal static class FightForecast
{
	internal sealed class RoundEvent
	{
		public int Round;
		public StatCompareType Op;
		public bool OtherConditions, Ends, Won;
		public readonly List<string> Flags = new List<string>();

		public bool Passed(int round) => Op == StatCompareType.Equal || Op == StatCompareType.Less || Op == StatCompareType.LessEqual ? round > Round : false;

		public string Text
		{
			get
			{
				var parts = new List<string>();
				if (Ends)
				{
					parts.Add(TextConverter.ForDisplay(Won ? "战斗结束（算获胜）" : "战斗结束（算未获胜）"));
				}
				parts.AddRange(Flags.Select(TextConverter.ForDisplay));
				string text = string.Join(TextConverter.ForDisplay("、"), parts.ToArray()) + (OtherConditions ? TextConverter.ForDisplay("（另有条件）") : "");
				switch (Op)
				{
					case StatCompareType.Equal:
						return string.Format(TextConverter.ForDisplay("第 {0} 回合：{1}"), Round, text);
					case StatCompareType.Less:
						return string.Format(TextConverter.ForDisplay("第 {0} 回合前：{1}"), Round, text);
					case StatCompareType.LessEqual:
						return string.Format(TextConverter.ForDisplay("第 {0} 回合前：{1}"), Round + 1, text);
					case StatCompareType.Greater:
						return string.Format(TextConverter.ForDisplay("第 {0} 回合起：{1}"), Round + 1, text);
					default:
						return string.Format(TextConverter.ForDisplay("第 {0} 回合起：{1}"), Round, text);
				}
			}
		}
	}

	/// <summary>现在这场战斗，没在打时是 null。</summary>
	internal static Object Current => CombatManager.Instance != null ? CombatManager.Instance : GameLevelManager.Instance;

	internal static List<RoundEvent> Rounds(CombatLevel level)
	{
		var list = new List<RoundEvent>();
		if (level == null || level.Events == null)
		{
			return list;
		}
		var results = (level.WinResult ?? new CombatResultFlag[0]).Concat(level.LoseResult ?? new CombatResultFlag[0]).ToList();
		foreach (CombatEventData data in level.Events.Where(d => d != null))
		{
			foreach (CombatEventConditionAction item in data.GetFieldValue<List<CombatEventConditionAction>>("_conditionAction") ?? new List<CombatEventConditionAction>())
			{
				List<CombatEventCondition> conditions = item.GetFieldValue<List<CombatEventCondition>>("_conditions");
				CombatEventConditionRound round = conditions?.OfType<CombatEventConditionRound>().FirstOrDefault();
				if (round == null)
				{
					continue;
				}
				var entry = new RoundEvent
				{
					Round = round.GetFieldValue<int>("_round"),
					Op = round.GetFieldValue<StatCompareType>("_op"),
					OtherConditions = conditions.Count > 1,
				};
				if (entry.Op == StatCompareType.NotEqual)
				{
					continue;
				}
				var actions = new[] { "_actions", "_afterActions", "_afterResult" }.SelectMany(name => item.GetFieldValue<List<CombatEventAction>>(name) ?? new List<CombatEventAction>());
				foreach (CombatEventAction action in actions)
				{
					if (action is CombatEventActionLoadScene)
					{
						entry.Ends = true;
					}
					else if (action is CombatEventActionModifyFlag modify)
					{
						FlagData flag = modify.GetFieldValue<FlagData>("_flag");
						int state = modify.GetFieldValue<int>("_state");
						if (flag == null)
						{
							continue;
						}
						if ((level.WinResult ?? new CombatResultFlag[0]).Any(r => r.GetFieldValue<FlagData>("_flag") == flag && r.GetFieldValue<int>("_state") == state))
						{
							entry.Won = true;
							continue;
						}
						string name = DataHelper.GetFlagName(flag);
						if (!results.Any(r => r.GetFieldValue<FlagData>("_flag") == flag) && !string.IsNullOrEmpty(name) && !name.Contains("暂存") && !name.Contains("泛用"))
						{
							entry.Flags.Add("设置" + name + "=" + state);
						}
					}
				}
				if (entry.Ends || entry.Flags.Count > 0)
				{
					list.Add(entry);
				}
			}
		}
		return list.OrderBy(e => e.Round).ToList();
	}

	/// <summary>战役按秒数、人数门槛触发的事件。只列结束战斗、增援和支援，镜头演出不算。</summary>
	private static List<string> BattleEvents(BattleLevel level)
	{
		var lines = new List<string>();
		foreach (BattleEventData data in level.GetFieldValue<BattleEventData[]>("_events") ?? new BattleEventData[0])
		{
			if (data == null)
			{
				continue;
			}
			var parts = new List<string>();
			foreach (BattleEventActionData action in data.GetFieldValue<BattleEventActionData[]>("_actions") ?? new BattleEventActionData[0])
			{
				if (action is BattleEventActionFlashGameOver over)
				{
					parts.Add(TextConverter.ForDisplay(over.GetFieldValue<BattleForceResult>("_result") == BattleForceResult.FriendWin ? "战斗结束（算获胜）" : "战斗结束（算失败）"));
				}
				else if (action is BattleEventActionSpawnReinforcement spawn)
				{
					bool friend = spawn.GetFieldValue<EntityType>("_team") != EntityType.Enemy;
					parts.Add(string.Format(TextConverter.ForDisplay(friend ? "我方增援 {0} 人" : "敌方增援 {0} 人"), spawn.GetFieldValue<int>("_count")));
				}
				else if (action is BattleEventActionCallSupport)
				{
					parts.Add(TextConverter.ForDisplay("支援出手"));
				}
			}
			if (parts.Count == 0)
			{
				continue;
			}
			string text = string.Join(TextConverter.ForDisplay("、"), parts.Distinct().ToArray()) + (data.GetFieldValue<bool>("_checkCondition") ? TextConverter.ForDisplay("（另有条件）") : "");
			if (data.GetFieldValue<bool>("_checkTriggerSecond"))
			{
				lines.Add(string.Format(TextConverter.ForDisplay("第 {0} 秒：{1}"), data.GetFieldValue<int>("_triggerSecond"), text));
			}
			if (data.GetFieldValue<bool>("_checkCountThreshold"))
			{
				bool friend = data.GetFieldValue<EntityType>("_thresholdTeam") != EntityType.Enemy;
				lines.Add(string.Format(TextConverter.ForDisplay(friend ? "我方剩 {0} 人以下：{1}" : "敌方剩 {0} 人以下：{1}"), data.GetFieldValue<int>("_countThreshold"), text));
			}
		}
		return lines.Distinct().ToList();
	}

	/// <summary>打赢或打输之后：接着的剧本里走哪几条路，各有什么变化。</summary>
	private static List<string> Outcome(bool duel, bool win)
	{
		var lines = new List<string>();
		PlayerStatManagerData stat = PlayerStatManagerData.Instance;
		StoryScript next = stat.CurrentNextScene == "Story" ? StoryScripts.Get(stat.CurrentStoryScript) : null;
		if (next == null)
		{
			return lines;
		}
		string kind = duel ? "Combat_Result_" : "Battle_Result_";
		foreach (StoryExit exit in next.Exits)
		{
			List<StoryStep> results = exit.Path.Where(s => s.Kind == StoryStep.Branch && s.Key.StartsWith(kind)).ToList();
			if (results.Count == 0 || !results.All(s => FightDeathHint.IsLoss(s) != win))
			{
				continue;
			}
			string way = StoryScripts.Describe(exit, TextConverter.ForDisplay);
			string effects = string.Join("　", next.EffectsOn(exit).Select(TextConverter.ForDisplay).ToArray());
			lines.Add(effects.Length > 0 ? way + "\n" + effects : way);
		}
		return lines.Distinct().Take(8).ToList();
	}

	/// <summary>「此刻」页用：每项是小标题和内容。</summary>
	internal static List<(string Title, string Text)> Report()
	{
		var report = new List<(string, string)>();
		CombatManager duel = CombatManager.Instance;
		GameLevelManager battle = duel == null ? GameLevelManager.Instance : null;
		if (duel == null && battle == null)
		{
			return report;
		}
		CombatLevel level = duel != null ? duel.CombatLevel : null;
		if (duel != null && level == null)
		{
			return report;
		}
		string battleEvents = null;
		if (battle != null)
		{
			BattleLevel data = battle.GetFieldValue<BattleLevel>("_levelData");
			if (data != null && data.VictoryTimeout)
			{
				report.Add(("胜利条件", string.Format(TextConverter.ForDisplay("坚持 {0} 秒"), data.Time)));
			}
			List<string> events = data != null ? BattleEvents(data) : new List<string>();
			if (events.Count > 0)
			{
				battleEvents = string.Join("\n", events.ToArray());
			}
		}
		List<string> win = Outcome(duel != null, true), lose = Outcome(duel != null, false);
		string death = FightDeathHint.Describe(level);
		var loseText = new List<string>();
		if (death != null)
		{
			loseText.Add(death);
		}
		loseText.AddRange(lose);
		// 两边都没有差别才写「输赢一样」：决斗输了直接死的关卡，打赢那边也可能没有分支
		if (win.Count == 0 && loseText.Count == 0)
		{
			report.Add(("输赢", TextConverter.ForDisplay("输赢都接同一段剧情")));
		}
		else
		{
			report.Add(("打赢", win.Count > 0 ? string.Join("\n", win.ToArray()) : TextConverter.ForDisplay("继续后面的剧情")));
			report.Add(("打输", loseText.Count > 0 ? string.Join("\n", loseText.ToArray()) : TextConverter.ForDisplay("继续后面的剧情")));
		}
		if (battleEvents != null)
		{
			report.Add(("战役事件", battleEvents));
		}
		List<RoundEvent> rounds = Rounds(level);
		if (rounds.Count > 0)
		{
			report.Add(("回合事件", string.Join("\n", rounds.Select(e => e.Text).Distinct().ToArray())));
		}
		return report;
	}
}
