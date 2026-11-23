using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Fungus;
using HarmonyLib;
using Mortal.Core;
using UnityEngine;
using UnityEngine.UI;

namespace LOMAssistant;

/// <summary>
/// 游戏的回想：1.0.6001 起是 StoryLogData（写进存档），更早的版本是 Fungus 的 NarrativeLog。
/// 旧版游戏里没有 StoryLogData 这些类型：别的类只要有一个字段、参数写了这些类型，整个类都加载不了（选项提示、窗口全跟着报错），
/// 所以只在这里、只在标了 NoInlining 的 *Now 方法里提到它们，先看 Available 再调。
/// </summary>
internal static class StoryLogCompat
{
	/// <summary>游戏有 1.0.6001 的回想。</summary>
	internal static readonly bool Available = AccessTools.TypeByName("Mortal.Core.StoryLogData") != null && AccessTools.TypeByName("Mortal.Core.StoryLogEntryView") != null;

	/// <summary>本 Mod 写进回想的文字（认出自己的记录用，回想最多 300 条，这里留多一点）。</summary>
	internal static readonly HashSet<string> Ours = new HashSet<string>();

	/// <summary>回想现在有几条；旧版游戏是 -1。</summary>
	internal static int Count => Available ? CountNow() : -1;

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static int CountNow() => StoryLogData.Entries.Count;

	/// <summary>写一行文字进回想（已经换成界面语言）。</summary>
	internal static void AddText(string shown)
	{
		if (Ours.Count > 1000)
		{
			Ours.Clear();
		}
		Ours.Add(shown);
		if (Available)
		{
			AddTextNow(shown);
			return;
		}
		// 旧版游戏：写进 Fungus 的对话记录，不写说话人（有名字就多一行金色大字，容易当成有人在说话）
		NarrativeLog log = FungusManager.Instance.NarrativeLog;
		if (log != null)
		{
			log.AddLine(new NarrativeLogEntry { name = "", text = "<size=18>" + shown + "</size>" });
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void AddTextNow(string shown) => StoryLogData.AddText(shown, false);

	/// <summary>随时存档读档时照游戏的做法另读回想；旧版游戏的回想不进存档，不用读。</summary>
	internal static void Load(GameSave game)
	{
		if (Available)
		{
			LoadNow(game);
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void LoadNow(GameSave game) => StoryLogData.Load(game);

	/// <summary>回想里最后一条，只当记号用（见 Dump）；没有条目、旧版游戏是 null。</summary>
	internal static object Tail() => Available ? TailNow() : null;

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static object TailNow()
	{
		IReadOnlyList<StoryLogEntry> entries = StoryLogData.Entries;
		return entries.Count == 0 ? null : entries[entries.Count - 1];
	}

	/// <summary>
	/// 把现在的回想整份记下来，连游戏不进存档的文字条目（判定记录）也记。cut 为 true 时只记到 last 这一条为止（last 是 null 就是一条不记）；
	/// last 已经被挤出 300 条、旧版游戏、读失败都返回 null。
	/// </summary>
	internal static StoryLogDump Dump(bool cut, object last)
	{
		if (!Available)
		{
			return null;
		}
		try
		{
			return DumpNow(cut, last);
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[回想] 记下回想失败（已忽略）：" + ex.Message);
			return null;
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static StoryLogDump DumpNow(bool cut, object last)
	{
		IReadOnlyList<StoryLogEntry> entries = StoryLogData.Entries;
		FieldInfo restart = AccessTools.Field(typeof(StoryLogData), "_restartCount");
		if (restart == null)
		{
			return null;
		}
		int count = entries.Count;
		if (cut)
		{
			count = 0;
			if (last != null)
			{
				count = -1;
				for (int i = entries.Count - 1; i >= 0; i--)
				{
					if (ReferenceEquals(entries[i], last))
					{
						count = i + 1;
						break;
					}
				}
				if (count < 0)
				{
					return null;
				}
			}
		}
		var dump = new StoryLogDump { restart = Math.Min((int)restart.GetValue(null), count) };
		for (int i = 0; i < count; i++)
		{
			StoryLogEntry e = entries[i];
			if (e == null)
			{
				continue;
			}
			var line = new StoryLogLine { type = (int)e.Type, key = e.Key, speaker = e.Speaker, text = e.Text, save = e.IsSave, ours = e.Text != null && Ours.Contains(e.Text) };
			if (e.Args != null)
			{
				line.args = new string[e.Args.Length];
				line.argIsKey = new bool[e.Args.Length];
				for (int k = 0; k < e.Args.Length; k++)
				{
					line.args[k] = e.Args[k]?.Value;
					line.argIsKey[k] = e.Args[k] != null && e.Args[k].IsKey;
				}
			}
			dump.lines.Add(line);
		}
		return dump;
	}

	/// <summary>把回想换成 Dump 记下的那一份，「剧情开头」的位置也照原样；失败时回想保持原样。</summary>
	internal static void Restore(StoryLogDump dump)
	{
		if (!Available || dump == null)
		{
			return;
		}
		try
		{
			RestoreNow(dump);
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[回想] 恢复回想失败（已忽略）：" + ex.Message);
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void RestoreNow(StoryLogDump dump)
	{
		var list = StoryLogData.Entries as List<StoryLogEntry>;
		FieldInfo restart = AccessTools.Field(typeof(StoryLogData), "_restartCount");
		if (list == null || restart == null)
		{
			FileLogger.LogWarning("[回想] 游戏的回想结构变了，读随时存档时不恢复回想");
			return;
		}
		var entries = new List<StoryLogEntry>(dump.lines.Count);
		foreach (StoryLogLine line in dump.lines)
		{
			if (line == null)
			{
				continue;
			}
			var e = new StoryLogEntry { Type = (StoryLogType)line.type, Key = line.key, Speaker = line.speaker ?? "", Text = line.text, IsSave = line.save, Args = new StoryLogArg[line.args?.Length ?? 0] };
			for (int k = 0; k < e.Args.Length; k++)
			{
				e.Args[k] = new StoryLogArg { Value = line.args[k], IsKey = line.argIsKey != null && k < line.argIsKey.Length && line.argIsKey[k] };
			}
			if (line.ours && line.text != null)
			{
				Ours.Add(line.text);
			}
			entries.Add(e);
		}
		if (entries.Count > StoryLogData.MaxCount)
		{
			entries.RemoveRange(0, entries.Count - StoryLogData.MaxCount);
		}
		StoryLogData.Reset();
		list.AddRange(entries);
		restart.SetValue(null, Math.Max(0, Math.Min(dump.restart, list.Count)));
	}

	/// <summary>回想里最后一条对白的说话人编号：没写说话人是 ""；没有对白、旧版游戏是 null。</summary>
	internal static string LastSpeaker() => Available ? LastSpeakerNow() : null;

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static string LastSpeakerNow()
	{
		IReadOnlyList<StoryLogEntry> entries = StoryLogData.Entries;
		for (int i = entries.Count - 1; i >= 0; i--)
		{
			if (entries[i].Type == StoryLogType.Story)
			{
				return entries[i].Speaker ?? "";
			}
		}
		return null;
	}

	/// <summary>
	/// 回想里 last 之后新写进去的条目（主动陪玩用）。last 是上次读到的最后一条，只当记号用；
	/// 找不到它（读档、清空、被挤出 300 条）就不算有新条目，只把记号对到末尾。旧版游戏什么都不返回。
	/// </summary>
	internal static void ReadNew(ref object last, ref bool wasEmpty, List<LogItem> into)
	{
		if (Available)
		{
			ReadNewNow(ref last, ref wasEmpty, into);
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void ReadNewNow(ref object last, ref bool wasEmpty, List<LogItem> into)
	{
		IReadOnlyList<StoryLogEntry> entries = StoryLogData.Entries;
		int count = entries.Count;
		if (count == 0)
		{
			last = null;
			wasEmpty = true;
			return;
		}
		object tail = entries[count - 1];
		if (ReferenceEquals(tail, last))
		{
			return;
		}
		int from = -1;
		if (last != null)
		{
			for (int i = count - 2; i >= 0; i--)
			{
				if (ReferenceEquals(entries[i], last))
				{
					from = i + 1;
					break;
				}
			}
		}
		else if (wasEmpty && count <= 3)
		{
			// 上一次看还是空的、现在只有头几条：新开的一局刚写下的，算新条目（读档一次会写回一大批）
			from = 0;
		}
		last = tail;
		wasEmpty = false;
		if (from < 0)
		{
			return;
		}
		for (int i = from; i < count; i++)
		{
			StoryLogEntry e = entries[i];
			if (e == null)
			{
				continue;
			}
			var item = new LogItem { Key = e.Key ?? "", Speaker = e.Speaker ?? "" };
			switch (e.Type)
			{
				case StoryLogType.Story: item.Kind = LogItem.Story; break;
				case StoryLogType.Choice: item.Kind = LogItem.Choice; break;
				case StoryLogType.DiceCheck: item.Kind = LogItem.Dice; break;
				case StoryLogType.StatChange: item.Kind = LogItem.Stat; break;
				case StoryLogType.RelationshipChange: item.Kind = LogItem.Relationship; break;
				case StoryLogType.TalentChange: item.Kind = LogItem.Talent; break;
				case StoryLogType.Message:
				case StoryLogType.StoryMessage: item.Kind = LogItem.Message; break;
				default: item.Kind = LogItem.Text; break;
			}
			if (e.Args != null)
			{
				item.Args = new string[e.Args.Length];
				for (int k = 0; k < e.Args.Length; k++)
				{
					item.Args[k] = e.Args[k]?.Value ?? "";
				}
			}
			if (item.Kind != LogItem.Text)
			{
				// 游戏自己的回想怎么显示这一条，就拿那句话；取选项文字时不要本 Mod 接在后面的后果说明
				OptionInfoPatch.Raw = true;
				try
				{
					if (StoryLogFormatter.TryGetContent(e, LocalizationManager.Instance.LocaleResolver, out string content))
					{
						item.Shown = content ?? "";
					}
				}
				catch (Exception ex)
				{
					FileLogger.Log("[回想] 读一条记录失败（已忽略）：" + ex.Message);
				}
				finally
				{
					OptionInfoPatch.Raw = false;
				}
			}
			into.Add(item);
		}
	}
}

/// <summary>随时存档里记下的完整回想（不依赖游戏类型，旧版游戏也能加载这个类）。restart 是「剧情开头」在第几条：游戏在剧情中途存档只写到这里，读档从这里重演。</summary>
public sealed class StoryLogDump
{
	public List<StoryLogLine> lines = new List<StoryLogLine>();
	public int restart;
}

public sealed class StoryLogLine
{
	public int type;
	public string key, speaker, text;
	/// <summary>游戏会把这一条写进存档（文字条目不写）。</summary>
	public bool save;
	/// <summary>本 Mod 写的判定记录（回想里用小字号）。</summary>
	public bool ours;
	public string[] args;
	public bool[] argIsKey;
}

/// <summary>回想里的一条，换成不依赖游戏类型的样子：旧版游戏没有回想的那些类型，别的类不能直接提到它们。</summary>
internal sealed class LogItem
{
	public const int Story = 0, Choice = 1, Dice = 2, Stat = 3, Relationship = 4, Talent = 5, Message = 6, Text = 7;

	public int Kind;
	/// <summary>对白、选项的编号；数值、好感变化时是数值、人物的编号。</summary>
	public string Key = "";
	/// <summary>对白的说话人编号，旁白是空的。</summary>
	public string Speaker = "";
	public string[] Args = new string[0];
	/// <summary>游戏回想里显示的那句话（游戏当前语言，可能带富文本标记）。</summary>
	public string Shown = "";
}

/// <summary>回想里的判定记录用游戏选项那一行的小字号，不和对白一样大（游戏每次显示条目都先把字号重置，再按类型改）。只在 1.0.6001 起的游戏上打。</summary>
internal static class StoryLogEntryPatch
{
	private static readonly AccessTools.FieldRef<StoryLogEntryView, Text> LogContent = AccessTools.FieldRefAccess<StoryLogEntryView, Text>("_contentText");
	private static readonly AccessTools.FieldRef<StoryLogEntryView, int> LogChoiceSize = AccessTools.FieldRefAccess<StoryLogEntryView, int>("_choiceFontSize");

	[HarmonyPostfix]
	[HarmonyPatch(typeof(StoryLogEntryView), nameof(StoryLogEntryView.Setup))]
	public static void LogEntrySetup_Postfix(StoryLogEntryView __instance, StoryLogEntry entry)
	{
		try
		{
			if (entry == null || entry.Type != StoryLogType.Text || entry.Text == null || !StoryLogCompat.Ours.Contains(entry.Text))
			{
				return;
			}
			Text content = LogContent(__instance);
			int small = LogChoiceSize(__instance);
			if (content != null)
			{
				content.fontSize = small > 0 && small < content.fontSize ? small : Mathf.RoundToInt(content.fontSize * 0.72f);
			}
		}
		catch (Exception ex)
		{
			FileLogger.Log("[回想] 判定记录改字号失败（已忽略）：" + ex.Message);
		}
	}
}
