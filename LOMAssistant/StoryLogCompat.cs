using System;
using System.Collections.Generic;
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

	/// <summary>决斗的已显示对白：普通字号，与判定提示分开；旧版也保留说话人。</summary>
	internal static void AddDialogue(string speaker, string text)
	{
		if (Available)
		{
			AddTextNow(string.IsNullOrEmpty(speaker) ? text : "<b>" + speaker + "</b>\n" + text);
			return;
		}
		NarrativeLog log = FungusManager.Instance?.NarrativeLog;
		if (log != null)
		{
			log.AddLine(new NarrativeLogEntry { name = speaker ?? "", text = text });
		}
	}

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
