using System;
using System.Collections.Generic;
using System.Linq;
using Mortal.Core;
using OBB.Framework.Extensions;

namespace LOMAssistant.Features.Story;

/// <summary>
/// 结缘路线：可以结缘的九位，每位的结缘旗标（认识 → 告白、前置 → 攻略）和「心上人」的值（PlayerInfo/Lover/N）。
/// 旗标名、说明都取自游戏数据（FlagData 的说明）；这里只记编号。
/// 剧本读完后整理每位在哪些任务里加减好感、设结缘旗标，路线图按这个高亮。
/// </summary>
public static partial class StoryRomance
{
	public sealed class Heroine
	{
		public int Lover;
		public RelationshipStatType Relation;
		/// <summary>结缘旗标，按先后：最后一面是「攻略」。</summary>
		public string[] Steps;
	}

	/// <summary>一位在一个任务里：加减了多少好感、设了哪几面结缘旗标。</summary>
	public sealed class Stop
	{
		public StoryMission Mission;
		public int Affection;
		public List<string> Steps = new List<string>();
	}

	public static readonly Heroine[] All =
	{
		new Heroine { Lover = 0, Relation = RelationshipStatType.小師妹, Steps = new[] { "G11000" } },
		new Heroine { Lover = 1, Relation = RelationshipStatType.瑞杏, Steps = new[] { "G11008" } },
		new Heroine { Lover = 2, Relation = RelationshipStatType.葉雲裳, Steps = new[] { "G10002", "M50024", "G11002" } },
		new Heroine { Lover = 3, Relation = RelationshipStatType.虞小梅, Steps = new[] { "G10004", "G00005", "G11004" } },
		new Heroine { Lover = 4, Relation = RelationshipStatType.上官螢, Steps = new[] { "G10001", "G11001" } },
		new Heroine { Lover = 5, Relation = RelationshipStatType.夏侯蘭, Steps = new[] { "G10005", "S0014_02", "G11005" } },
		new Heroine { Lover = 6, Relation = RelationshipStatType.郁竹, Steps = new[] { "G10006", "G00006", "G11006" } },
		new Heroine { Lover = 7, Relation = RelationshipStatType.魏菊, Steps = new[] { "G10007", "G11007" } },
		new Heroine { Lover = 8, Relation = RelationshipStatType.龍湘, Steps = new[] { "G10003", "G00004", "G11003" } },
	};

	private static readonly Dictionary<int, List<Stop>> Stops = new Dictionary<int, List<Stop>>();
	private static readonly Dictionary<int, int> Elsewhere = new Dictionary<int, int>();
	private static int _builtVersion = -1;

	/// <summary>心上人的名字（游戏自己的文字）。</summary>
	public static string Name(Heroine h)
	{
		try
		{
			string name = LocalizationManager.Instance.LocaleResolver.GetString("PlayerInfo/Lover/" + h.Lover);
			return string.IsNullOrEmpty(name) || name.StartsWith("PlayerInfo/") ? h.Relation.ToString() : TextConverter.ToSimplified(name);
		}
		catch
		{
			return TextConverter.ToSimplified(h.Relation.ToString());
		}
	}

	/// <summary>旗标的说明（游戏数据里的，转简体）。</summary>
	public static string StepName(string flag)
	{
		string note = MissionManagerData.Instance?.GetFlag(flag)?.DevNote ?? "";
		return note.Length > 0 ? TextConverter.ToSimplified(note).Replace("\n", " ").Trim() : flag;
	}

	public static bool StepDone(string flag) => (MissionManagerData.Instance?.GetFlag(flag)?.State ?? 0) >= 1;

	/// <summary>一步的简称（站名后面标用）：认识、告白、攻略、拜师、前置。</summary>
	public static string StepKind(string flag)
	{
		if (flag.StartsWith("G10"))
		{
			return "认识";
		}
		if (flag.StartsWith("G00"))
		{
			return "告白";
		}
		if (flag.StartsWith("G11"))
		{
			return "攻略";
		}
		return flag == "S0014_02" ? "拜师" : "前置";
	}

	public static int Affection(Heroine h) => PlayerStatManagerData.Instance?.Relationships?.Get(h.Relation)?.Value ?? 0;

	public static bool IsLover(Heroine h)
	{
		var lover = PlayerStatManagerData.Instance?.Stats?.Get(GameStatType.愛人);
		return lover != null && lover.FinalValue == h.Lover;
	}

	/// <summary>加减她好感、设她结缘旗标的任务，按触发时间；剧本还没读完返回 null。</summary>
	public static List<Stop> StopsOf(Heroine h) => Ensure() ? Stops.TryGetValue(h.Lover, out var list) ? list : new List<Stop>() : null;

	/// <summary>不属于任务的剧本（养成随机事件等）里加减她好感的地方有几处。</summary>
	public static int ElsewhereOf(Heroine h) => Ensure() && Elsewhere.TryGetValue(h.Lover, out int n) ? n : 0;

	private static bool Ensure()
	{
		if (!StoryAtlas.Ready)
		{
			return false;
		}
		if (_builtVersion != StoryAtlas.Version)
		{
			_builtVersion = StoryAtlas.Version;
			Build();
		}
		return true;
	}

	private static void Build()
	{
		Stops.Clear();
		Elsewhere.Clear();
		// 剧本里写的是角色的英文代号（Character("girl4", 3, 1)），换成好感的类型
		var byKey = new Dictionary<string, Heroine>(StringComparer.OrdinalIgnoreCase);
		foreach (Heroine h in All)
		{
			foreach (string key in KeysOf(h.Relation))
			{
				byKey[key] = h;
			}
		}
		var stops = new Dictionary<(int, StoryMission), Stop>();
		Stop Get(Heroine h, StoryMission m)
		{
			if (!stops.TryGetValue((h.Lover, m), out Stop s))
			{
				stops[(h.Lover, m)] = s = new Stop { Mission = m };
			}
			return s;
		}
		foreach (StoryScript script in StoryScripts.All)
		{
			StoryAtlas.Owner.TryGetValue(script.Name, out StoryMission mission);
			foreach (var (_, method, id, value) in script.Modifies)
			{
				int.TryParse(value, out int amount);
				if (method == "Character" && byKey.TryGetValue(id, out Heroine h) && amount != 0)
				{
					if (mission == null)
					{
						Elsewhere[h.Lover] = (Elsewhere.TryGetValue(h.Lover, out int n) ? n : 0) + 1;
					}
					else
					{
						Get(h, mission).Affection += amount;
					}
				}
				else if (method == "SetFlag" && amount >= 1 && mission != null)
				{
					foreach (Heroine x in All.Where(x => x.Steps.Contains(id)))
					{
						Stop s = Get(x, mission);
						if (!s.Steps.Contains(id))
						{
							s.Steps.Add(id);
						}
					}
				}
			}
		}
		foreach (var group in stops.GroupBy(p => p.Key.Item1))
		{
			Stops[group.Key] = group.Select(p => p.Value).OrderBy(s => s.Mission.From ?? int.MaxValue).ThenBy(s => s.Mission.Key, StringComparer.Ordinal).ToList();
		}
	}

	// 枚举上标的代号（girl4 这样，剧本里写的就是它）
	private static IEnumerable<string> KeysOf(RelationshipStatType type)
	{
		string key = type.GetStringValue();
		if (!string.IsNullOrEmpty(key))
		{
			yield return key;
		}
	}
}
