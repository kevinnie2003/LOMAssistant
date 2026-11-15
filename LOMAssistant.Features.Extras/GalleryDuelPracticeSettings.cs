using System;
using System.Collections.Generic;
using System.Linq;

namespace LOMAssistant.Features.Extras;

/// <summary>练习用的主角，和存档无关：读档、改档都不会填进来。</summary>
internal sealed class GalleryDuelPracticeSettings
{
	// 顺序和 DuelProfile 一样：体力、内力、轻功、拳掌、刀剑、暗器
	public int[] Six = { 50, 50, 50, 50, 50, 50 };
	// 性情、处世、道德、修养
	public int[] Four = { 50, 50, 50, 50 };
	// 战术、佛学、道学、儒学、形意；游戏 InitSkill 里天赋可能改掉这些值
	public int[] Mastery = new int[5];
	public int Health = 1000;
	public int MaxHealth = 1000;
	public int Energy = 50;
	public int MaxEnergy = 100;
	public int Talking = 50;
	// 键是 PlayerTalentData.Id（不是 CombatSkillKey）；没有这一项或等级为 0 就是不带
	public Dictionary<string, int> SkillLevels = new Dictionary<string, int>(StringComparer.Ordinal);
	// 不为空时以这个手动存档里的主角为底（只读文件）：和 SaveBase 不同的项才替换存档里的值
	public string SaveSlot;
	public string SaveLabel;
	public GalleryDuelPracticeSettings SaveBase;
	// 以存档为底时，血量和气力默认按存档在进战斗时算；为 true 才用上面填的
	public bool CustomVitals;

	public bool FromSave => !string.IsNullOrEmpty(SaveSlot);

	public static GalleryDuelPracticeSettings CreateDefault() => new GalleryDuelPracticeSettings();

	public GalleryDuelPracticeSettings Clone() => new GalleryDuelPracticeSettings
	{
		Six = Six == null ? null : (int[])Six.Clone(),
		Four = Four == null ? null : (int[])Four.Clone(),
		Mastery = Mastery == null ? null : (int[])Mastery.Clone(),
		Health = Health,
		MaxHealth = MaxHealth,
		Energy = Energy,
		MaxEnergy = MaxEnergy,
		Talking = Talking,
		SkillLevels = SkillLevels == null ? null : new Dictionary<string, int>(SkillLevels, StringComparer.Ordinal),
		SaveSlot = SaveSlot,
		SaveLabel = SaveLabel,
		SaveBase = SaveBase?.Clone(),
		CustomVitals = CustomVitals,
	};

	public bool TryValidate(out string error)
	{
		error = null;
		bool vitals = !FromSave || CustomVitals;
		if (!Valid(Six, 6, 1000))
		{
			error = "六维属性需要 6 个 0～1000 的整数。";
		}
		else if (!Valid(Four, 4, 100))
		{
			error = "性情、处世、道德、修养需要 4 个 0～100 的整数。";
		}
		else if (!Valid(Mastery, 5, 100))
		{
			error = "精通需要 5 个 0～100 的整数。";
		}
		else if (vitals && (MaxHealth < 1 || MaxHealth > 100000 || Health < 1 || Health > MaxHealth))
		{
			error = "血量需要满足 1 ≤ 当前血量 ≤ 血量上限 ≤ 100000。";
		}
		else if (vitals && (MaxEnergy < 1 || MaxEnergy > 1000 || Energy < 0 || Energy > MaxEnergy))
		{
			error = "气力需要满足 0 ≤ 当前气力 ≤ 气力上限 ≤ 1000。";
		}
		else if (Talking < 0 || Talking > 1000)
		{
			error = "嘴力需要为 0～1000 的整数。";
		}
		else if (SkillLevels == null || SkillLevels.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value < 0 || p.Value > 10))
		{
			error = "技能需要有效的天赋编号和 0～10 的等级。";
		}
		return error == null;
	}

	private static bool Valid(int[] values, int count, int max) => values != null && values.Length == count && values.All(v => v >= 0 && v <= max);
}

internal sealed class GalleryDuelPracticeSkill
{
	public string Id;
	public string Key;
	public string Name;
	public string Description;
	public int MaxLevel;
	public bool DisplayLevel;
}

/// <summary>可以拿来练习的手动存档，只用于列表显示。</summary>
internal sealed class GalleryDuelPracticeSave
{
	public string Slot;
	public string Label;
	public string Detail;
	public long TimeTick;
}
