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
	public DuelRadarResource PersonalityBar, PersonalityPointer;
	public Dictionary<string, string> PersonalityLabels;
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
		if (catalog.Stats.Any(s => s.Six == null || s.Six.Length != 6 || s.Four == null || s.Four.Length != 4 || s.Folders == null || s.Skills == null)
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
	// 性情、处世、道德、修养：每份敌方配置独立取值，与主角存档无关。
	public int[] Four;
	public int Health, MaxHealth, Energy, MaxEnergy, Talking;
	public List<DuelSkill> Skills;
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
