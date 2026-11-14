using System;
using System.Collections.Generic;
using System.Linq;
using Mortal.Combat;

namespace LOMAssistant.Features.Extras;

internal sealed class DuelBuffOption
{
	public string Key;
	public DuelSkill Skill;
	public bool Triggered;
	public string OriginKey;
	public DuelBuff Condition;
	public DuelEvent StoryEvent;
}

/// <summary>只计算面板，不创建战斗角色、施放技能或修改游戏数据。</summary>
internal static class GalleryDuelBuffPreview
{
	// CombatStatType 的枚举顺序与鉴赏的六维顺序不同。
	private static readonly int[] StatTypes = { 1, 2, 3, 5, 6, 4 };
	internal static string SkillKey(DuelSkill skill) => string.IsNullOrEmpty(skill.Key) ? ""
		: skill.Key + (skill.DisplayLevel ? "_" + skill.Level : "");

	internal static List<DuelBuffOption> Options(DuelProfile profile, DuelEncounter encounter)
	{
		var catalog = GalleryDuelCatalog.Instance.Buffs;
		var result = new List<DuelBuffOption>();
		var seen = new HashSet<string>(StringComparer.Ordinal);
		void Visit(string key, DuelSkill skill, bool triggered, string originKey, DuelBuff condition, DuelEvent storyEvent)
		{
			if (string.IsNullOrEmpty(key) || !seen.Add(key) || !catalog.TryGetValue(key, out DuelBuff buff)) return;
			if (!string.IsNullOrEmpty(buff.Condition)) condition = buff;
			if (buff.Modifiers.Any(m => m.Targets.Length > 0 && (m.Rate > 0 || m.Kind == "Flat" && m.Value > 0)))
				result.Add(new DuelBuffOption { Key = key, Skill = skill, Triggered = triggered, OriginKey = originKey, Condition = condition, StoryEvent = storyEvent });
			foreach (string child in buff.Unlocks) Visit(child, skill, true, originKey, condition, storyEvent);
		}
		foreach (DuelSkill skill in profile.Skills.OrderBy(s => s.Priority))
			if (skill.Level > 0) Visit(SkillKey(skill), skill, false, SkillKey(skill), null, null);
		if (encounter != null)
			foreach (DuelEvent change in encounter.Events)
				if (change.Target == 2 && !change.Remove && change.Effect != null)
					Visit(change.Effect.Key, null, change.Phase != "Start", change.Effect.Key, null, change);
		return result;
	}

	internal static int[] Calculate(DuelProfile profile, IEnumerable<DuelBuffOption> options,
		ISet<string> selected, float healthPercent, bool hasSupport)
	{
		if (selected.Count == 0) return (int[])profile.Six.Clone();
		var stats = StatTypes.Select((type, i) => new { type, item = new CombatStatItem(profile.Six[i]) })
			.ToDictionary(x => x.type, x => x.item);
		var enabled = new HashSet<string>(options.Where(o => selected.Contains(o.Key)).Select(o => o.Key), StringComparer.Ordinal);
		var applied = new HashSet<string>(StringComparer.Ordinal);
		var later = new List<KeyValuePair<string, DuelBuffModifier>>();
		var mastery = (int[])profile.Mastery.Clone(); // 角色自身配置，随后按天赋 Priority 覆盖精通等级。
		var catalog = GalleryDuelCatalog.Instance.Buffs;
		void Modify(string key, DuelBuffModifier m)
		{
			if (m.Kind == "SetMastery") { mastery[m.Source] = m.Value; return; }
			if (m.Kind == "Support" && !hasSupport) return;
			// 与游戏 Mathf.RoundToInt 一样：以单精度计算，正好一半时取偶数。
			int Round(float value) => (int)Math.Round(value, MidpointRounding.ToEven);
			void Add(string modifierKey, int value)
			{
				foreach (int target in m.Targets) stats[target].AddModify(modifierKey, value);
			}
			if (m.Kind == "Stat" || m.Kind == "Stats")
			{
				for (int i = 0; i < m.Sources.Length; i++)
					Add(m.Kind == "Stats" ? key + "_" + i : key, Round(stats[m.Sources[i]].FinalValue * m.Rate));
				return;
			}
			foreach (int target in m.Targets)
			{
				float value = stats[target].FinalValue;
				switch (m.Kind)
				{
					case "Flat": value = m.Value; break;
					case "Schools": value = value * m.Rate * m.Sources.Sum(i => mastery[i]); break;
					case "Mastery": value *= mastery[m.Source] * m.Rate; break;
					case "Health": value *= (100f - Math.Max(0f, Math.Min(100f, healthPercent))) / 100f * m.Rate; break;
					default: value *= m.Rate; break;
				}
				// 原版 CombatStatItem 负责同一个效果键的去重和最终加算。
				stats[target].AddModify(key, Round(value));
			}
		}
		void Apply(string key, bool includeStats)
		{
			if (!catalog.TryGetValue(key, out DuelBuff buff) || !applied.Add(key)) return;
			foreach (DuelBuffModifier m in buff.Modifiers)
			{
				if (m.Kind != "SetMastery" && !includeStats) continue;
				if (m.BeforeAction) later.Add(new KeyValuePair<string, DuelBuffModifier>(key, m));
				else Modify(key, m);
			}
		}
		foreach (DuelSkill skill in profile.Skills.OrderBy(s => s.Priority))
			if (skill.Level > 0) Apply(SkillKey(skill), enabled.Contains(SkillKey(skill)));
		foreach (DuelBuffOption option in options)
			if (enabled.Contains(option.Key)) Apply(option.Key, true);
		// 底力、同舟等是在行动前计算，必须晚于初始化及触发状态的加成。
		foreach (var entry in later) Modify(entry.Key, entry.Value);
		return StatTypes.Select(type => stats[type].FinalValue).ToArray();
	}
}
