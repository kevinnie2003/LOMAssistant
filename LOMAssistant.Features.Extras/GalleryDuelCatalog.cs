using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;

namespace LOMAssistant.Features.Extras;

/// <summary>只读的游戏配置快照。标题画面不加载决斗场景，不实例化或修改 CombatStat。</summary>
internal sealed class GalleryDuelCatalog
{
	public int Schema;
	public Dictionary<string, string> Sources;
	public List<DuelProfile> Stats;
	public List<DuelEncounter> Levels;
	public DuelRadarResource Radar;
	private Dictionary<string, DuelProfile> _byId;
	private static GalleryDuelCatalog _instance;
	internal static GalleryDuelCatalog Instance => _instance ??= Load();

	private static GalleryDuelCatalog Load()
	{
		using Stream resource = typeof(GalleryDuelCatalog).Assembly.GetManifestResourceStream("LOMAssistant.gallery-duels.json.gz");
		if (resource == null) throw new InvalidDataException("Missing duel catalog");
		using var gzip = new GZipStream(resource, CompressionMode.Decompress);
		using var reader = new StreamReader(gzip);
		var catalog = JsonConvert.DeserializeObject<GalleryDuelCatalog>(reader.ReadToEnd());
		if (catalog?.Schema != 1 || catalog.Stats == null || catalog.Levels == null)
			throw new InvalidDataException("Invalid duel catalog");
		catalog._byId = catalog.Stats.ToDictionary(s => s.Id, StringComparer.Ordinal);
		if (catalog.Stats.Any(s => s.Six == null || s.Six.Length != 6 || s.Folders == null || s.Skills == null)
			|| catalog.Levels.Any(l => !catalog._byId.ContainsKey(l.StatId)))
			throw new InvalidDataException("Invalid duel profile reference");
		return catalog;
	}

	internal DuelProfile Profile(string id) => _byId[id];

	internal List<DuelEncounter> ForFolder(string folder)
	{
		// 不用台词编号联结：山贼共用 0000，其他角色也有换装、假扮和共用立绘。
		var ids = new HashSet<string>(Stats.Where(s => s.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)).Select(s => s.Id));
		var result = Levels.Where(l => ids.Contains(l.StatId)).ToList();
		foreach (string id in ids.OrderBy(s => s, StringComparer.Ordinal))
			if (!result.Any(l => l.StatId == id))
				result.Add(new DuelEncounter { Id = "", StatId = id, Title = "", Events = new List<DuelEvent>() });
		return result;
	}
}

internal sealed class DuelRadarResource
{
	public string Name, File, Sha256;
	public long Offset;
	public int Size, Width, Height, Format;
}

internal sealed class DuelProfile
{
	public string Id, CharacterId, Name, Note;
	public string[] Folders;
	// 体力、内力、轻功、拳掌、刀剑、暗器；不得截断 100 以上的数值。
	public int[] Six;
	public int Health, MaxHealth, Energy, MaxEnergy, Talking;
	public List<DuelSkill> Skills;
	public DuelBalance Balance;
}

internal sealed class DuelBalance
{
	public float Threshold, K, Maximum, Inheritance;
}

internal sealed class DuelEncounter
{
	public string Id, StatId, Title;
	public int Difficulty;
	public List<DuelEvent> Events;
}

internal sealed class DuelSkill
{
	public string Id, Key, Name, Description;
	public int Level, Priority;
	public bool DisplayLevel;
}

internal sealed class DuelEffect
{
	public string Key, Name, Description;
	public int Rounds;
}

internal sealed class DuelEvent
{
	public string Trigger, Phase, Action;
	public int Target;
	public bool Remove;
	public DuelEffect Effect;
}

/// <summary>
/// 难度单独作用于基础面板的预览，不冒充技能/事件生效后的实战值。
/// 顺序来自 CombatManager.InitCombatRound：继承加成 → 友善削弱 → 技能 → 追赶加成。
/// 这里跳过技能，界面明确说明；勇者和友善是独立开关，可以同时生效。
/// </summary>
internal static class GalleryDuelDifficulty
{
	internal static int Round(float value) => (int)Math.Round(value, MidpointRounding.ToEven);

	internal static int[] Preview(DuelProfile profile, bool brave, bool friendly, int playerTotal, int inheritedTotal)
	{
		var result = (int[])profile.Six.Clone();
		DuelBalance b = profile.Balance;
		if (brave && b != null)
		{
			int add = Round(Math.Max(0, inheritedTotal) / 6f * b.Inheritance);
			for (int i = 0; i < result.Length; i++) result[i] += add;
		}
		if (friendly)
			for (int i = 0; i < result.Length; i++) result[i] += Round(result[i] * -0.5f);
		if (brave && b != null)
		{
			float gap = Math.Max(0f, Math.Max(0, playerTotal) / 100f - result.Sum() / 100f - b.Threshold);
			float ratio = gap > 0f ? b.Maximum * (gap / (gap + b.K)) : 0f;
			// 游戏 S_053 漏了拳掌，暗器写两次；AddModify 同 key 去重，暗器只加一次。
			for (int i = 0; i < result.Length; i++)
				if (i != 3) result[i] += Round(result[i] * ratio);
		}
		return result;
	}
}
