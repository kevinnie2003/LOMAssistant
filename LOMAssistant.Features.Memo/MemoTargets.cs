using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LOMAssistant.Features.Story;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Memo;

/// <summary>
/// 备忘录能盯的一条数据：名字给人看、Key 给存档用、Current 现读现算。目录从当前存档的数据里来
/// （数值页那一套的取法），没读档进游戏时是空的，新建备忘录提示先读档。目录几秒重建一次，
/// 中间 Current 一直读的是游戏对象本身，修炼投点、剧情改好感时立刻就是新值。
/// </summary>
internal sealed class MemoTarget
{
	public MemoKind Kind;
	/// <summary>稳定编号（枚举值 / 资源名 / 数字），换语言、换界面文字都不变。</summary>
	public string Key = "";
	/// <summary>界面名（简体中文）。</summary>
	public string Name = "";
	/// <summary>目录里归哪一组：数值、好感、功法……</summary>
	public string Group = "";
	/// <summary>值的单位（层、点、级、本……），没有就空。</summary>
	public string Unit = "";
	/// <summary>现值。</summary>
	public Func<int> Current;
	/// <summary>上限，没法算（好感、旗标）就是 0。</summary>
	public Func<int> Max;
	/// <summary>做目标值输入框提示用的上限，0 表示随便填。</summary>
	public int Ceiling;
	/// <summary>搜出来一行上的小字（"当前 12" 这类）。</summary>
	public Func<string> Hint;

	public int Now => Current == null ? 0 : Current();
}

internal static class MemoTargets
{
	private static List<MemoTarget> _cache;
	private static PlayerStatManagerData _builtFor;
	private static bool _builtForSave;
	private static float _nextRebuild;

	/// <summary>所有能盯的，按组、名字排。没数据时空。</summary>
	public static List<MemoTarget> Catalog()
	{
		List<MemoTarget> list = _cache;
		if (list != null && !Stale())
		{
			return list;
		}
		list = Build();
		_cache = list;
		_builtFor = PlayerStatManagerData.Instance;
		_builtForSave = StoryNodes.HasCurrentSave;
		_nextRebuild = Time.unscaledTime + 5f;
		return list;
	}

	// 几秒一重建（物品、功法列表会变）；刚读档/回主菜单时立刻重建
	private static bool Stale() =>
		Time.unscaledTime >= _nextRebuild
		|| !ReferenceEquals(_builtFor, PlayerStatManagerData.Instance)
		|| _builtForSave != StoryNodes.HasCurrentSave;

	public static void Invalidate()
	{
		_nextRebuild = 0f;
	}

	/// <summary>某条备忘录盯的那条数据；找不到了（还没拿到这本书、换了存档）返回 null。</summary>
	public static MemoTarget Find(MemoKind kind, string key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return null;
		}
		return Catalog().FirstOrDefault(t => t.Kind == kind && t.Key == key);
	}

	private static List<MemoTarget> Build()
	{
		var list = new List<MemoTarget>();
		PlayerStatManagerData player = PlayerStatManagerData.Instance;
		if (player == null || player.Stats == null || SaveSystem.Instance == null || !StoryNodes.HasCurrentSave)
		{
			return list;
		}
		AddStats(list, player);
		AddFavor(list, player);
		AddTalents(list, player);
		AddMartial(list);
		AddUpgrades(list, player, "_weaponUpgradeItems", MemoKind.Forge, "锻造");
		AddUpgrades(list, player, "_poisonUpgradeItems", MemoKind.Alchemy, "炼丹");
		AddFacilities(list, player);
		AddItems(list);
		AddFlags(list);
		return list.OrderBy(t => Rank(t.Group)).ThenBy(t => t.Name, StringComparer.CurrentCulture).ToList();
	}

	// 目录里各组排的顺序，和名字排的先后一致
	private static readonly string[] GroupOrder =
	{
		"数值", "好感", "功法", "锻造", "炼丹", "天赋", "设施", "物品", "旗标",
	};

	private static int Rank(string group)
	{
		int i = Array.IndexOf(GroupOrder, group);
		return i < 0 ? GroupOrder.Length : i;
	}

	// 本地化名（跟着游戏语言，查不到用资源名）；数值页里同名方法在别的类里，这里是给备忘录自己的
	private static string LocalName(string key, string fallback)
	{
		try
		{
			string text = LocalizationManager.Instance.LocaleResolver.GetString(key);
			if (!string.IsNullOrEmpty(text) && text != key)
			{
				return TextConverter.ToSimplified(text);
			}
		}
		catch
		{
		}
		return TextConverter.ToSimplified(fallback ?? "");
	}

	private static void AddStats(List<MemoTarget> list, PlayerStatManagerData player)
	{
		foreach (GameStat stat in player.Stats?.List ?? new List<GameStat>())
		{
			if (stat == null)
			{
				continue;
			}
			GameStat s = stat;
			OwnStatNames.TryGetValue(s.StatType, out string own);
			string name = LocalName(s.GetLocaleKey(), own ?? TextConverter.ToSimplified(s.StatType.ToString()));
			if (string.IsNullOrEmpty(name))
			{
				continue;
			}
			// 本旬行动次数这类当目标没意义，跳过
			if (s.StatType == GameStatType.行動次數 || s.StatType == GameStatType.額外行動次數_1
				|| s.StatType == GameStatType.額外行動次數_2 || s.StatType == GameStatType.額外行動次數_3)
			{
				continue;
			}
			list.Add(new MemoTarget
			{
				Kind = MemoKind.Stat,
				Key = "stat:" + (int)s.StatType,
				Name = name,
				Group = "数值",
				Current = () => s.FinalValue,
				Ceiling = 0,
				Hint = () => "当前 " + s.FinalValue,
			});
		}
	}

	// 数值页给没起名的数值起的名，这里照抄一份（两处都改会忘，宁少勿错：只放备忘录里常当目标的）
	private static readonly Dictionary<GameStatType, string> OwnStatNames = new Dictionary<GameStatType, string>
	{
		{ GameStatType.廚藝, "厨艺" },
		{ GameStatType.防禦, "防御" },
		{ GameStatType.攻擊爆擊, "攻击暴击" },
		{ GameStatType.暗器爆擊, "暗器暴击" },
		{ GameStatType.戰役預設血量, "团战预设血量" },
		{ GameStatType.稱號, "称号" },
		{ GameStatType.全武學點數, "累计武学点" },
		{ GameStatType.全鍛造點數, "累计锻造点" },
		{ GameStatType.全毒藥點數, "累计炼丹点" },
		{ GameStatType.全貢獻度, "累计贡献度" },
		{ GameStatType.門派貢獻, "门派贡献门槛" },
	};

	private static void AddFavor(List<MemoTarget> list, PlayerStatManagerData player)
	{
		foreach (RelationshipStat stat in player.Relationships?.List ?? new List<RelationshipStat>())
		{
			if (stat == null)
			{
				continue;
			}
			RelationshipStat s = stat;
			string name = RelationshipName(s);
			if (string.IsNullOrEmpty(name))
			{
				continue;
			}
			list.Add(new MemoTarget
			{
				Kind = MemoKind.Favor,
				Key = "rel:" + s.name,
				Name = name,
				Group = "好感",
				Current = () => s.Value,
				Hint = () => "当前 " + s.Value,
			});
		}
	}

	// 数值页里的算法：类型名（简体）；类型没起名（同一人的几份好感）用资源名后半段
	private static string RelationshipName(RelationshipStat stat)
	{
		string name = stat.Type.ToString();
		if (name.Length == 0 || name.All(char.IsDigit))
		{
			string[] parts = (stat.name ?? "").Split('_');
			name = parts.Length > 2 ? string.Join("_", parts.Skip(2).ToArray()) : stat.name;
		}
		return TextConverter.ToSimplified(name ?? "");
	}

	private static void AddTalents(List<MemoTarget> list, PlayerStatManagerData player)
	{
		foreach (PlayerTalentData talent in player.Talents?.List ?? new List<PlayerTalentData>())
		{
			if (talent == null)
			{
				continue;
			}
			PlayerTalentData t = talent;
			int max = Mathf.Max(0, Traverse.Create(t).Field("_maxLevel").GetValue<int>());
			string name = LocalName(t.GetIdKey(), t.Id);
			if (string.IsNullOrEmpty(name))
			{
				continue;
			}
			list.Add(new MemoTarget
			{
				Kind = MemoKind.Talent,
				Key = "tal:" + t.Id,
				Name = name,
				Group = "天赋",
				Unit = "级",
				Current = () => t.Level,
				Max = () => max,
				Ceiling = max,
				Hint = () => "当前 " + t.Level + (max > 0 ? " / " + max : ""),
			});
		}
	}

	private static void AddMartial(List<MemoTarget> list)
	{
		foreach (Book book in ItemDatabase.Instance?.Books?.List?.OfType<Book>() ?? Enumerable.Empty<Book>())
		{
			if (book == null || !book.IsMartial || book.CurrentCount <= 0)
			{
				continue;
			}
			Book b = book;
			int max = b.MaxLevel;
			string name = LocalName(b.GetNameKey(), b.Id);
			if (string.IsNullOrEmpty(name))
			{
				continue;
			}
			list.Add(new MemoTarget
			{
				Kind = MemoKind.Martial,
				Key = "lvl:" + b.Id,
				Name = name + " 层数",
				Group = "功法",
				Unit = "层",
				Current = () => Mathf.Min(max, b.CurrentMartialLevel + 1),
				Max = () => max,
				Ceiling = max,
				Hint = () => "当前 " + Mathf.Min(max, b.CurrentMartialLevel + 1) + " / " + max + " 层",
			});
			bool open = b.CurrentMartialLevel >= 0 && b.CurrentMartialLevel < max && b.LevelData[b.CurrentMartialLevel] != null;
			int need = open ? b.LevelData[b.CurrentMartialLevel].Points : 0;
			list.Add(new MemoTarget
			{
				Kind = MemoKind.Martial,
				Key = "pt:" + b.Id,
				Name = name + " 本层点数",
				Group = "功法",
				Unit = "点",
				Current = () => b.CurrentLevelPoints,
				Max = () => need,
				Ceiling = need,
				Hint = () => open ? "本层 " + b.CurrentLevelPoints + " / " + need + " 点" : "已练满或暂无本层数据",
			});
		}
	}

	private static void AddUpgrades(List<MemoTarget> list, PlayerStatManagerData player, string field, MemoKind kind, string group)
	{
		var collection = Traverse.Create(player).Field(field).GetValue<UpgradeItemCollectionData>();
		foreach (UpgradeItemData item in collection?.List ?? new List<UpgradeItemData>())
		{
			if (item == null)
			{
				continue;
			}
			UpgradeItemData u = item;
			int max = u.LevelCounts;
			string name = LocalName(u.GetNameKey(), u.Key);
			if (string.IsNullOrEmpty(name))
			{
				continue;
			}
			list.Add(new MemoTarget
			{
				Kind = kind,
				Key = "lvl:" + u.Key,
				Name = name,
				Group = group,
				Unit = "级",
				Current = () => Mathf.Max(0, u.CurrentLevel),
				Max = () => max,
				Ceiling = max,
				Hint = () => (u.CurrentLevel < 0 ? "未开发" : "当前 " + Mathf.Max(0, u.CurrentLevel) + " / " + max + " 级"),
			});
			bool open = u.CurrentLevel >= 0 && u.CurrentLevel < max && u.UpgradeLevels[u.CurrentLevel] != null;
			int need = open ? u.UpgradeLevels[u.CurrentLevel].LevelPoints : 0;
			list.Add(new MemoTarget
			{
				Kind = kind,
				Key = "pt:" + u.Key,
				Name = name + " 本级点数",
				Group = group,
				Unit = "点",
				Current = () => u.CurrentPoints,
				Max = () => need,
				Ceiling = need,
				Hint = () => open ? "本级 " + u.CurrentPoints + " / " + need + " 点" : "未开发或已满级",
			});
		}
	}

	private static void AddFacilities(List<MemoTarget> list, PlayerStatManagerData player)
	{
		var config = Traverse.Create(player).Field("_facility").GetValue<FacilityConfigData>();
		foreach (FacilityItemData facility in config?.Data ?? new FacilityItemData[0])
		{
			if (facility == null)
			{
				continue;
			}
			var flag = Traverse.Create(facility).Field("_level").GetValue<FlagData>();
			int max = (Traverse.Create(facility).Field("_levelData").GetValue() as Array)?.Length ?? 0;
			if (flag == null)
			{
				continue;
			}
			FlagData f = flag;
			string name = LocalName(facility.GetNameKey(), facility.name);
			if (string.IsNullOrEmpty(name))
			{
				continue;
			}
			list.Add(new MemoTarget
			{
				Kind = MemoKind.Facility,
				Key = "fac:" + f.name,
				Name = name,
				Group = "设施",
				Unit = "级",
				Current = () => f.State,
				Max = () => max,
				Ceiling = max,
				Hint = () => "当前 " + f.State + (max > 0 ? " / " + max : "") + " 级",
			});
		}
	}

	private static void AddItems(List<MemoTarget> list)
	{
		ItemDatabase items = ItemDatabase.Instance;
		if (items == null)
		{
			return;
		}
		foreach (string field in new[] { "Books", "Miscs", "Special", "Consumables" })
		{
			ItemDataCollection collection = (ItemDataCollection)typeof(ItemDatabase).GetProperty(field)?.GetValue(items, null);
			foreach (ItemData item in collection?.List?.OfType<ItemData>() ?? Enumerable.Empty<ItemData>())
			{
				if (item == null)
				{
					continue;
				}
				ItemData it = item;
				string name = LocalName(it.GetNameKey(), it.Id);
				if (string.IsNullOrEmpty(name))
				{
					continue;
				}
				list.Add(new MemoTarget
				{
					Kind = MemoKind.Item,
					Key = "item:" + (int)it.ItemType + ":" + it.Id,
					Name = name,
					Group = "物品",
					Unit = "个",
					Current = () => it.CurrentCount,
					Hint = () => "当前 " + it.CurrentCount,
				});
			}
		}
	}

	private static void AddFlags(List<MemoTarget> list)
	{
		MissionManagerData missions = MissionManagerData.Instance;
		foreach (FlagData flag in missions?.FlagsCollection?.Where(c => c != null && c.List != null).SelectMany(c => c.List)
			.Where(f => f != null).Distinct().OrderBy(f => f.name, StringComparer.Ordinal) ?? Enumerable.Empty<FlagData>())
		{
			FlagData f = flag;
			string name = DataHelper.GetFlagName(f);
			if (string.IsNullOrEmpty(name))
			{
				name = f.name;
			}
			list.Add(new MemoTarget
			{
				Kind = MemoKind.Flag,
				Key = "flag:" + f.name,
				Name = name,
				Group = "旗标",
				Current = () => f.State,
				Hint = () => "当前 " + f.State + "　" + f.name,
			});
		}
	}
}
