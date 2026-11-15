using System;
using LOMAssistant.Features.Extras;

namespace LOMAssistant.Tests;

public static class GalleryDuelPracticeTests
{
	public static void TestPracticeHeroIsDetachedFromEditor()
	{
		var settings = GalleryDuelPracticeSettings.CreateDefault();
		settings.SkillLevels["test-talent"] = 3;
		var battle = settings.Clone();
		battle.Six[0] = 999;
		battle.Four[0] = 0;
		battle.Mastery[0] = 10;
		battle.SkillLevels["test-talent"] = 7;
		battle.SkillLevels["other-talent"] = 1;
		Check(settings.Six[0] == 50 && settings.Four[0] == 50 && settings.Mastery[0] == 0,
			"Battle preparation mutated the editable hero's arrays");
		Check(settings.SkillLevels["test-talent"] == 3 && !settings.SkillLevels.ContainsKey("other-talent"),
			"Battle preparation mutated the editable hero's talents");
	}

	public static void TestPracticeHeroRejectsInvalidCombatResources()
	{
		var settings = GalleryDuelPracticeSettings.CreateDefault();
		Check(settings.TryValidate(out _), "Default hero is invalid");
		settings.Health = settings.MaxHealth + 1;
		Check(!settings.TryValidate(out _), "Health above the maximum was accepted");
		settings.Health = 0;
		Check(!settings.TryValidate(out _), "A dead hero was accepted");
		settings.Health = 1;
		settings.Energy = settings.MaxEnergy + 1;
		Check(!settings.TryValidate(out _), "Energy above the maximum was accepted");
		settings.Energy = 0;
		settings.Six[0] = 250;
		Check(settings.TryValidate(out _), "A valid native stat above 100 was truncated or rejected");
		settings.Four[0] = 101;
		Check(!settings.TryValidate(out _), "Personality outside the native range was accepted");
	}

	public static void TestPracticeHeroRejectsMalformedBuilds()
	{
		var settings = GalleryDuelPracticeSettings.CreateDefault();
		settings.Six = new int[5];
		Check(!settings.TryValidate(out _), "Incomplete six-dimensional stats were accepted");
		settings = GalleryDuelPracticeSettings.CreateDefault();
		settings.SkillLevels["unknown"] = -1;
		Check(!settings.TryValidate(out _), "A negative talent level was accepted");
		settings.SkillLevels.Clear();
		settings.SkillLevels[""] = 1;
		Check(!settings.TryValidate(out _), "An empty talent ID was accepted");
	}

	private static void Check(bool condition, string message)
	{
		if (!condition) throw new Exception(message);
	}
}
