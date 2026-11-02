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
