using System.Collections;
using System.Collections.Generic;
using Fungus;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Story;
using UnityEngine;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 对白显示速度、自动播放间隔。
/// 显示速度：对白逐字出现（Fungus 的 Writer，默认每秒 60 字，标点后停一下），每句开写前按档位改它的速度和标点停顿；「立即显示」把速度设成 0，Writer 就不逐字等。
/// 自动播放：原版的「自动」是固定每 8 秒点一下，不管这句写完没有（没写完这一下只是补全，再过 8 秒才翻页）。
/// 选了秒数以后改成：这句写完、等着点击时，再等这么久翻页；中途换了句就重新计时。「原版」照游戏原来的做法。
/// </summary>
internal static class TextSpeed
{
	/// <summary>显示速度的档位（ConfigManager.TextSpeed 存序号）：倍率，0 是立即显示。</summary>
	internal static readonly float[] Speeds = { 0.5f, 1f, 2f, 4f, 0f };
	internal static readonly string[] SpeedNames = { "0.5 倍", "原速", "2 倍", "4 倍", "立即显示" };

	/// <summary>自动播放间隔的档位（ConfigManager.AutoPlayInterval 存序号）：秒，-1 是原版。0 秒排在最后，已有的序号不变。</summary>
	internal static readonly float[] Intervals = { -1f, 4f, 2f, 1f, 0f };
	internal static readonly string[] IntervalNames = { "原版", "4 秒", "2 秒", "1 秒", "0 秒" };

	private static readonly Dictionary<int, (float Speed, float Pause)> Original = new Dictionary<int, (float, float)>();

	private static float Factor
	{
		get
		{
			int i = ConfigManager.TextSpeed?.Value ?? 1;
			return i >= 0 && i < Speeds.Length ? Speeds[i] : 1f;
		}
	}

	private static float Interval
	{
		get
		{
			int i = ConfigManager.AutoPlayInterval?.Value ?? 0;
			return i >= 0 && i < Intervals.Length ? Intervals[i] : -1f;
		}
	}

	/// <summary>每句开写前：按档位设这个 Writer 的速度和标点停顿（按第一次见到时的原值算，档位改回原速就是原样）。</summary>
	[HarmonyPrefix]
	[HarmonyPatch(typeof(Writer), nameof(Writer.Write))]
	public static void Write_Prefix(Writer __instance)
	{
		var t = Traverse.Create(__instance);
		int id = __instance.GetInstanceID();
		if (!Original.TryGetValue(id, out var original))
		{
			original = (t.Field("writingSpeed").GetValue<float>(), t.Field("punctuationPause").GetValue<float>());
			Original[id] = original;
		}
		float factor = Factor;
		float speed = factor <= 0f ? 0f : original.Speed * factor;
		float pause = factor <= 0f ? 0f : original.Pause / factor;
		t.Field("writingSpeed").SetValue(speed);
		t.Field("punctuationPause").SetValue(pause);
	}

	/// <summary>换掉游戏的自动播放协程；「原版」档位下和游戏的一样。</summary>
	[HarmonyPrefix]
	[HarmonyPatch(typeof(StoryManager), "StartAutoDialogue")]
	public static bool StartAutoDialogue_Prefix(StoryManager __instance, ref IEnumerator __result)
	{
		__result = AutoDialogue(__instance);
		return false;
	}

	private static IEnumerator AutoDialogue(StoryManager manager)
	{
		var t = Traverse.Create(manager);
		float waited = 0f, original = 0f;
		while (true)
		{
			if (manager == null)
			{
				yield break;
			}
			bool skip = t.Field("_skipDialog").GetValue<bool>();
			bool auto = t.Field("_autoDialog").GetValue<bool>();
			if (!auto && !skip)
			{
				waited = original = 0f;
				yield return null;
				continue;
			}
			float interval = Interval;
			if (skip || interval < 0f)
			{
				// 原版：一进来先点一下，之后每 8 秒点一下；跳过时每帧点
				waited = 0f;
				if (skip || original <= 0f)
				{
					NextLine();
					original = 8f;
				}
				else
				{
					original -= Time.deltaTime;
					if (original <= 0f)
					{
						NextLine();
						original = 8f;
					}
				}
				yield return null;
				continue;
			}
			original = 0f;
			Writer writer = SayDialog.GetSayDialog()?.GetComponent<Writer>();
			if (writer == null || !writer.IsWaitingForInput)
			{
				// 还在写或者没有对白框：重新计时
				waited = 0f;
				yield return null;
				continue;
			}
			waited += Time.deltaTime;
			if (waited >= interval)
			{
				waited = 0f;
				NextLine();
			}
			yield return null;
		}
	}

	private static void NextLine()
	{
		SayDialog.GetSayDialog()?.GetComponent<DialogInput>()?.SetNextLineFlag();
	}
}
