using System;
using System.Collections.Generic;
using System.Linq;
using LOMAssistant.Features.Extras;

namespace LOMAssistant.Tests;

public static class GalleryDuelBuffTests
{
	private static DuelSkill Skill(string key, int level = 1, int priority = 0, bool displayLevel = false)
		=> new DuelSkill { Id = key, Key = key, Level = level, Priority = priority, DisplayLevel = displayLevel };
	private static DuelProfile Profile(int[] six, params DuelSkill[] skills)
		=> new DuelProfile { Six = six, Skills = skills.ToList(), MaxHealth = 100, Health = 100 };
	private static int[] Preview(DuelProfile profile, params string[] keys)
		=> GalleryDuelBuffPreview.Calculate(profile, GalleryDuelBuffPreview.Options(profile, null), new HashSet<string>(keys), 100, false);
	private static void Assert(bool condition, string message)
	{
		if (!condition) throw new Exception(message);
	}

	public static void TestGalleryBuffSourceStatsOrderAndRounding()
	{
		var p = Profile(new[] { 75, 65, 55, 45, 85, 35 },
			Skill("B300", 9, 3, true), Skill("B114", 9, 2, true), Skill("B0013", 9, 1, true));
		var expected = new[] { 75, 65, 66, 60, 97, 35 };
		Assert(Preview(p, "B300_9", "B114_9", "B0013_9").SequenceEqual(expected), "Source stat bonuses or native priority order differ");
		Assert(Preview(p, "B0013_9", "B114_9", "B300_9").SequenceEqual(expected), "Click order changed the result");
		var ties = Profile(new[] { 0, 0, 0, 25, 35, 0 }, Skill("B114", 5, 0, true), Skill("B0013", 5, 1, true));
		Assert(Preview(ties, "B114_5", "B0013_5").SequenceEqual(new[] { 0, 0, 2, 29, 35, 0 }), "Half-even rounding differs from Mathf.RoundToInt");
	}

	public static void TestGalleryBuffFlatBonusesOverflowAndDuplicateKeys()
	{
		var p = Profile(Enumerable.Repeat(100, 6).ToArray(), Skill("S127"), Skill("S127"), Skill("S8112"));
		Assert(Preview(p, "S127", "S8112_1").SequenceEqual(new[] { 210, 150, 150, 150, 150, 150 }), "Flat bonus order, overflow, or effect-key deduplication differs from game");
		Assert(Preview(p).SequenceEqual(p.Six), "Clearing buffs must restore the base panel");
	}

	public static void TestGalleryBuffMasteryFromCharacterTalents()
	{
		var p = Profile(Enumerable.Repeat(100, 6).ToArray(), Skill("Z003", 9, 0, true), Skill("A0011", 9, 1, true));
		Assert(Preview(p, "A0011_9")[1] == 262, "Taoism must come from this profile's own talent");
		p.Skills[0].Level = 0;
		Assert(Preview(p, "A0011_9")[1] == 100, "Unlearned mastery must not affect buffs");
		p.Mastery[2] = 5;
		Assert(Preview(p, "A0011_9")[1] == 190, "Base mastery from the actual character config must be preserved");
		p = Profile(Enumerable.Repeat(100, 6).ToArray(), Skill("Z005", 5, 0, true), Skill("B0014", 1, 1));
		Assert(Preview(p, "B0014").All(v => v == 110), "Xingyi mastery multiplier differs from native rule");
	}

	public static void TestGalleryBuffTriggeredStatesAndStoryChoices()
	{
		var c = GalleryDuelCatalog.Instance;
		var ruisheng = c.Profile("Enemy_瑞笙_004");
		var options = GalleryDuelBuffPreview.Options(ruisheng, null);
		Assert(options.Any(o => o.Key == "S8112_1" && o.Triggered && o.Skill.Key == "S8112"), "Taibai triggered state missing");
		Assert(Preview(ruisheng, "S8112_1").SequenceEqual(ruisheng.Six.Select(v => v + (int)Math.Round(v * 0.5f))), "Taibai's actual 50% buff was lost");
		var lan = c.Profile("Enemy_小蘭_001");
		Assert(GalleryDuelBuffPreview.Options(lan, null).Any(o => o.Key == "S2105_1_1" && o.Triggered), "Ruyi must expose its resulting buff");
		Assert(Preview(lan, "S2105_1_1")[3] > lan.Six[3], "Ruyi must affect fist, not sword or hidden weapon");
		var encounter = new DuelEncounter { Events = new List<DuelEvent> {
			new DuelEvent { Target = 2, Phase = "Start", Effect = new DuelEffect { Key = "S127" } },
			new DuelEvent { Target = 1, Phase = "Start", Effect = new DuelEffect { Key = "S137" } },
			new DuelEvent { Target = 2, Remove = true, Effect = new DuelEffect { Key = "S162" } } } };
		var story = GalleryDuelBuffPreview.Options(lan, encounter);
		Assert(story.Any(o => o.Key == "S127" && o.Skill == null) && !story.Any(o => o.Key == "S137" || o.Key == "S162"), "Wrong side or removed story buffs leaked into preview");
		Assert(!Preview(lan, "S8112_1").Where((v,i) => v != lan.Six[i]).Any(), "A different character's buff must not be accepted");
	}

	public static void TestGalleryBuffHealthSupportAndPhaseOrdering()
	{
		var p = Profile(Enumerable.Repeat(100, 6).ToArray(), Skill("S139"), Skill("S8112"), Skill("S8125"));
		var options = GalleryDuelBuffPreview.Options(p, null);
		int[] Calc(float hp, bool support, params string[] keys) => GalleryDuelBuffPreview.Calculate(p, options, new HashSet<string>(keys), hp, support);
		Assert(Calc(50, false, "S139", "S8112_1").All(v => v == 188), "Health bonus must apply after initialization buffs");
		Assert(Calc(100, false, "S139").All(v => v == 100), "Full health should not grant low-health bonus");
		Assert(Calc(0, false, "S139").All(v => v == 150), "Zero-health preview should grant the native maximum bonus");
		Assert(Calc(-50, false, "S139").All(v => v == 150), "Preview HP must be clamped");
		Assert(Calc(100, false, "S8125").All(v => v == 100), "Support bonus needs a companion");
		Assert(Calc(100, true, "S8125").All(v => v == 130), "Support bonus missing");
	}

	public static void TestGalleryBuffDiancangSwordRealmAndEncounterScope()
	{
		var c = GalleryDuelCatalog.Instance;
		var encounter = c.Levels.Single(e => e.Id == "CL_1809_01");
		var p = c.Profile(encounter.StatId);
		var options = GalleryDuelBuffPreview.Options(p, encounter);
		var sword = options.Single(o => o.Key == "H006_1");
		Assert(sword.Triggered && sword.Skill == null && sword.OriginKey == "C554", "Sword Realm must retain its Diancang story source");
		Assert(sword.Condition?.Condition == "ComboRange" && sword.Condition.MinCombo == 8 && sword.Condition.MaxCombo == 12,
			"Diancang's native combo thresholds were lost");
		Assert(sword.StoryEvent?.Trigger.Contains("A10006") == true, "Conditional story support must retain its encounter flag");
		int[] Calc(params string[] keys) => GalleryDuelBuffPreview.Calculate(p, options, new HashSet<string>(keys), 100, false);
		Assert(Calc("H006_1")[4] == 117, "Sword Realm must add 30% to sword");
		Assert(Calc("H006_1", "S8112_1")[4] == 175, "Sword Realm must apply after Taibai and use native half-even rounding");
		Assert(Calc("H006_1").Where((v,i) => i != 4).SequenceEqual(p.Six.Where((v,i) => i != 4)), "Sword Realm must not raise unrelated stats");
		Assert(!GalleryDuelBuffPreview.Options(p, null).Any(o => o.Key == "H006_1"), "Story support leaked into innate skills");
		var supported = c.Levels.Where(e => GalleryDuelBuffPreview.Options(c.Profile(e.StatId), e)
			.Any(o => o.Key == "H006_1")).Select(e => e.Id).OrderBy(id => id);
		Assert(supported.SequenceEqual(new[] { "CL_1809_01", "CL_S0208_05_04", "CL_S0209_01_04" }), "Sword Realm leaked into an unsupported encounter");
	}

	public static void TestGalleryBuffSpecialConditionalEffects()
	{
		var c = GalleryDuelCatalog.Instance;
		var encounter = c.Levels.Single(e => e.Id == "CL_1218_01");
		var p = c.Profile(encounter.StatId);
		var options = GalleryDuelBuffPreview.Options(p, encounter);
		var north = options.Single(o => o.Key == "S105");
		Assert(north.Triggered && north.OriginKey == "C241" && north.Condition?.Condition == "NoDebuff",
			"Quanzhen must expose the North Dipper state with its no-debuff condition");
		var result = GalleryDuelBuffPreview.Calculate(p, options, new HashSet<string> { "S105" }, 100, false);
		Assert(result.SequenceEqual(p.Six.Select(v => v + (int)Math.Round(v * 0.1f))), "North Dipper's native bonus differs");
		p = c.Profile("Enemy_金烏上人_001");
		Assert(GalleryDuelBuffPreview.Options(p, null).Single(o => o.Key == "H3100").Condition?.Condition == "HigherEnergy",
			"Bully the Weak must make its positive energy condition explicit");
		Assert(Preview(p, "H3100").SequenceEqual(new[] { 84, 60, 36, 72, 72, 36 }), "Bully the Weak's 20% bonus differs");
		p = Profile(Enumerable.Repeat(100, 6).ToArray(), Skill("H005", 3, 0, true));
		Assert(Preview(p, "H005_3").SequenceEqual(new[] { 100, 100, 130, 100, 100, 100 }), "Transcendence must use the native keyed dexterity modifier");
	}

	public static void TestGalleryBuffCatalogAndPreviewIsolation()
	{
		var c = GalleryDuelCatalog.Instance;
		string[] kinds = { "SetMastery", "Stat", "Stats", "Percent", "Flat", "Schools", "Mastery", "Support", "Health" };
		Assert(c.Buffs.Count > 100 && c.Buffs.All(b => b.Value.Effect.Key == b.Key), "Missing game buff rules");
		Assert(c.Buffs.Values.All(b => b.Unlocks.All(c.Buffs.ContainsKey) && b.Modifiers.All(m => kinds.Contains(m.Kind)
			&& m.Targets.All(t => t >= 1 && t <= 6))), "Invalid stat rule or trigger reference");
		foreach (DuelProfile p in c.Stats)
		{
			var saved = (int[])p.Six.Clone();
			foreach (DuelEncounter e in c.Levels.Where(e => e.StatId == p.Id).DefaultIfEmpty())
			{
				var options = GalleryDuelBuffPreview.Options(p, e);
				Assert(options.Select(o => o.Key).Distinct().Count() == options.Count, "Duplicate toggle");
				var keys = new HashSet<string>(options.Select(o => o.Key));
				var one = GalleryDuelBuffPreview.Calculate(p, options, keys, 25, true);
				Assert(one.Length == 6 && one.SequenceEqual(GalleryDuelBuffPreview.Calculate(p, options, keys, 25, true)), "Preview must be repeatable");
				Assert(GalleryDuelBuffPreview.Calculate(p, options, new HashSet<string>(), 25, true).SequenceEqual(saved), "Reset must restore the base stats");
				Assert(p.Six.SequenceEqual(saved), "Preview modified the shared profile");
			}
		}
	}
}
