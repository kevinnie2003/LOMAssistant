using System;
using System.Collections.Generic;
using System.Linq;

namespace LOMAssistant.Features.Extras;

/// <summary>A detached practice hero; never populated by loading or editing a save.</summary>
internal sealed class GalleryDuelPracticeSettings
{
	// Same order as DuelProfile: strength, stamina, dexterity, fist, sword, martial weapon.
	public int[] Six = { 50, 50, 50, 50, 50, 50 };
	// Disposition, behaviour, karma, training.
	public int[] Four = { 50, 50, 50, 50 };
	// Strategy, Buddhism, Taoism, Confucianism, Xingyi. Talents can override these in native InitSkill.
	public int[] Mastery = new int[5];
	public int Health = 1000, MaxHealth = 1000, Energy = 50, MaxEnergy = 100, Talking = 50;
	// PlayerTalentData.Id, not CombatSkillKey. Missing IDs / level zero mean disabled.
	public Dictionary<string, int> SkillLevels = new Dictionary<string, int>(StringComparer.Ordinal);

	public static GalleryDuelPracticeSettings CreateDefault() => new GalleryDuelPracticeSettings();

	public GalleryDuelPracticeSettings Clone() => new GalleryDuelPracticeSettings
	{
		Six = Six == null ? null : (int[])Six.Clone(),
		Four = Four == null ? null : (int[])Four.Clone(),
		Mastery = Mastery == null ? null : (int[])Mastery.Clone(),
		Health = Health, MaxHealth = MaxHealth, Energy = Energy, MaxEnergy = MaxEnergy, Talking = Talking,
		SkillLevels = SkillLevels == null ? null : new Dictionary<string, int>(SkillLevels, StringComparer.Ordinal),
	};

	public bool TryValidate(out string error)
	{
		error = null;
		if (!Valid(Six, 6, 1000)) error = "六维属性需要 6 个 0～1000 的整数。";
		else if (!Valid(Four, 4, 100)) error = "性情、处世、道德、修养需要 4 个 0～100 的整数。";
		else if (!Valid(Mastery, 5, 100)) error = "精通需要 5 个 0～100 的整数。";
		else if (MaxHealth < 1 || MaxHealth > 100000 || Health < 1 || Health > MaxHealth) error = "体力需要满足 1 ≤ 当前体力 ≤ 最大体力 ≤ 100000。";
		else if (MaxEnergy < 1 || MaxEnergy > 1000 || Energy < 0 || Energy > MaxEnergy) error = "气力需要满足 0 ≤ 当前气力 ≤ 最大气力 ≤ 1000。";
		else if (Talking < 0 || Talking > 1000) error = "嘴力需要为 0～1000 的整数。";
		else if (SkillLevels == null || SkillLevels.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value < 0 || p.Value > 10)) error = "技能需要有效的天赋编号和 0～10 的等级。";
		return error == null;
	}

	private static bool Valid(int[] values, int count, int max) => values != null && values.Length == count && values.All(v => v >= 0 && v <= max);
}

internal sealed class GalleryDuelPracticeSkill
{
	public string Id, Key, Name, Description;
	public int MaxLevel;
	public bool DisplayLevel;
}
