using System;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using LOMAssistant.Features.Extras;

namespace LOMAssistant.Tests;

public static class GalleryDuelTests
{
	public static void TestGalleryNativeRadarAxisOrderAndOverflow()
	{
		var six = new[] { 100, 80, 60, 40, 20, 0 };
		Assert(GalleryDuelRadar.Distances(six, 0).SequenceEqual(new[] { 1f, 0.6f, 0.2f, 0.02f, 0.4f, 0.8f, 1f }), "Native radar axes or minimum visible radius differ from game");
		Assert(GalleryDuelRadar.Distances(six, 100).All(v => v == 0f), "Overflow layer must be invisible for ordinary stats");
		Assert(GalleryDuelRadar.Distances(new[] { 250, 0, 0, 0, 0, 150 }, 100).SequenceEqual(new[] { 1f, 0.02f, 0.02f, 0.5f, 0.02f, 0.02f, 1f }), "Native overflow shape is incorrect");
		Assert(GalleryDuelRadar.Distances(new int[6], 0).All(v => v == 0f), "Zero stats must not draw a nonzero shape");
	}

	public static void TestGalleryNativeRadarResourceMatchesInstalledGame()
	{
		var r = GalleryDuelCatalog.Instance.Radar;
		Assert(r != null && r.Width == 440 && r.Height == 500 && r.Format == 12, "Native background metadata missing");
		using var file = new BinaryReader(File.OpenRead(Path.Combine(Program.Game, "Mortal_Data", r.File)));
		file.BaseStream.Position = r.Offset;
		using var hash = SHA256.Create();
		string actual = BitConverter.ToString(hash.ComputeHash(file.ReadBytes(r.Size))).Replace("-", "").ToLowerInvariant();
		Assert(actual == r.Sha256, "Native radar background no longer matches installed game");
	}

	private static void Assert(bool condition, string message)
	{
		if (!condition) throw new Exception(message);
	}

	public static void TestGalleryDuelCatalogIntegrity()
	{
		var catalog = GalleryDuelCatalog.Instance;
		Assert(catalog.Stats.Count == 248 && catalog.Levels.Count == 397, "Snapshot extraction lost profiles or encounters");
		Assert(catalog.Levels.Select(e => e.Id).Distinct().Count() == catalog.Levels.Count, "Encounter identity collision");
		Assert(catalog.Stats.All(s => s.Six.Length == 6 && s.Folders.Length > 0), "Incomplete profile");
		Assert(catalog.Stats.All(s => s.Skills.All(k => !string.IsNullOrEmpty(k.Name) && !string.IsNullOrEmpty(k.Description))), "Missing skill text");
		foreach (var level in catalog.Levels)
		{
			DuelProfile profile = catalog.Profile(level.StatId);
			Assert(profile.Folders.All(folder => catalog.ForFolder(folder).Contains(level)), "Encounter not accessible from its portrait: " + level.Id);
		}
		foreach (var profile in catalog.Stats)
			Assert(profile.Folders.All(folder => catalog.ForFolder(folder).Any(e => e.StatId == profile.Id)), "Orphan profile hidden: " + profile.Id);
	}

	public static void TestGalleryDuelStoryVariants()
	{
		var c = GalleryDuelCatalog.Instance;
		DuelProfile normal = c.Profile("Enemy_小蘭_001"), weakened = c.Profile("Enemy_小蘭_002");
		// 游戏给 002 写了「内力弱化」备注，但基础内力与 001 相同；不能根据备注编造数值。
		Assert(normal.Id != weakened.Id && normal.Six.SequenceEqual(weakened.Six), "Equal base panels must remain distinct story configurations");
		Assert(c.Profile("Enemy_小蘭_003").Six[1] < normal.Six[1], "Different Xiahou Lan stat variants lost");
		Assert(c.ForFolder(normal.Folders[0]).Any(e => e.StatId == weakened.Id), "Weakened state inaccessible");
		var arena = c.Levels.Where(e => e.Id.StartsWith("CL_1203_", StringComparison.Ordinal)).ToList();
		Assert(arena.Count == 9 && arena.Select(e => e.Difficulty).Distinct().OrderBy(x => x).SequenceEqual(new[] { 1, 2, 3 }), "Arena difficulties lost");
		Assert(c.Levels.Any(e => e.Events.Any(v => v.Phase == "Start" && v.Effect != null)), "Missing initial skills from story events");
		Assert(c.Levels.Any(e => e.Events.Any(v => v.Remove && v.Trigger.Length > 0)), "Missing conditional skill removals");
		Assert(c.Stats.GroupBy(s => s.CharacterId).Any(g => g.SelectMany(s => s.Folders).Distinct().Count() > 1), "Fixture must include shared dialogue identifiers");
		Assert(c.ForFolder("bandit_山賊_01").Count > 0, "Bandit portrait should have profiles");
		Assert(c.ForFolder("bandit_山賊_01").All(e => c.Profile(e.StatId).Folders.Contains("bandit_山賊_01")), "Shared dialogue ID mixed portraits");
	}
}
