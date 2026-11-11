using System;
using System.Collections;
using System.Reflection;

namespace LOMAssistant.Tests;

public static class DuelDialogueHistoryTests
{
	private static readonly Type HistoryType = typeof(LOMAssistant.TextConverter).Assembly.GetType("LOMAssistant.Features.Combat.DuelDialogueHistory");
	private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
	private static object NewHistory() => Activator.CreateInstance(HistoryType, true);
	private static bool Add(object history, int round, string speaker, string text) => (bool)HistoryType.GetMethod("Add", Members).Invoke(history, new object[] { round, speaker, text });
	private static IList Entries(object history) => (IList)HistoryType.GetProperty("Entries", Members).GetValue(history);
	private static object Field(object entry, string name) => entry.GetType().GetField(name, Members).GetValue(entry);
	private static void Assert(bool condition, string message)
	{
		if (!condition) throw new Exception(message);
	}

	public static void TestDuelHistoryPreservesSpeakerOrderAndRepeatedLines()
	{
		object history = NewHistory();
		Add(history, 1, "对手", "<color=red>嘴攻！</color>\n第二行");
		Add(history, 1, "第三者", "插话");
		Add(history, 2, "对手", "<color=red>嘴攻！</color>\n第二行");
		Add(history, 2, "对手", "<color=red>嘴攻！</color>\n第二行");
		IList entries = Entries(history);
		Assert(entries.Count == 4, "实际重复说出的台词必须逐次保留");
		Assert((string)Field(entries[1], "Speaker") == "第三者", "第三者说话人或顺序丢失");
		Assert((int)Field(entries[2], "Round") == 2, "回合标签丢失");
		Assert((string)Field(entries[0], "Text") == "<color=red>嘴攻！</color>\n第二行", "已显示的富文本或换行被改写");
	}

	public static void TestDuelHistoryHandlesNarrationAndBlankLines()
	{
		object history = NewHistory();
		Assert(!Add(history, 1, "对手", null), "不应记录 null 台词");
		Assert(!Add(history, 1, "对手", " \n\t"), "不应记录空白台词");
		Assert(Add(history, -1, null, "旁白正文"), "没有说话人的旁白必须保留");
		IList entries = Entries(history);
		Assert(entries.Count == 1 && (string)Field(entries[0], "Speaker") == "", "旁白被伪造了名字");
		Assert((int)Field(entries[0], "Round") == 0, "初始回合应归零");
	}

	public static void TestDuelHistoryBoundsAndReset()
	{
		object history = NewHistory();
		int limit = (int)HistoryType.GetField("Limit", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue();
		for (int i = 0; i < limit + 17; i++) Add(history, i, "对手", i.ToString());
		IList entries = Entries(history);
		Assert(entries.Count == limit && (string)Field(entries[0], "Text") == "17", "超限时必须只淘汰最旧的记录");
		Assert((string)Field(entries[limit - 1], "Text") == (limit + 16).ToString(), "超限时丢失最新记录");
		HistoryType.GetMethod("Clear", Members).Invoke(history, null);
		Assert(entries.Count == 0, "下一场决斗不得沿用上一场记录");
		Add(history, 1, "新对手", "新的决斗");
		Assert(entries.Count == 1 && (int)Field(entries[0], "Round") == 1, "清空后无法正常记录");
	}
}
