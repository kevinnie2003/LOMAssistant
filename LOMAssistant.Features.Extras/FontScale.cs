using System.Collections.Generic;
using HarmonyLib;
using LOMAssistant.Config;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 文字放大，两个设置：
/// 「游戏文字大小」放大游戏里的全部文字（菜单、按钮、数值、对白……，Steam Deck 这类小屏幕用）；
/// 「对白文字大小」只放大剧情对白框（普通对白、内心独白、旁白，都叫 StoryText，在 SayDialogs 下面），乘在前一个上面。
/// 普通文字（UnityEngine.UI.Text）在排版时先取一份排版参数，在这里把字号乘上比例，不改组件本身记的字号，100% 就完全是原样。
/// TextMeshPro 的文字没有这一步，启用时记下原字号再改它的字号，比例改回 100% 时换回原字号。
/// 本 Mod 自己的窗口是 IMGUI 画的，不受影响。放大太多界面可能放不下，字会挤出框。
/// 放大以后格子正好装下的数字（六维、锻造等级等）会被折成两行，原版设成超出就截掉，结果整个数字不见了：
/// 放大时不截，超出格子照样画出来；没有空格的短文字（数字、一个词）不折行。
/// 原版在 100% 也有这个毛病：分辨率低时字形宽度取整变宽，决斗状态页的六维这类正好装满格子的数字会挤到第二行，被截掉看不见。
/// 所以纯数字（「85」「-3」「12/30」「50%」）不管放没放大都不折行、不截掉；别的文字在 100% 保持原样。
/// </summary>
public static class FontScale
{
	private static int _appliedDialogue = 100, _appliedGame = 100;
	private static readonly Dictionary<int, bool> Dialogue = new Dictionary<int, bool>();
	private static readonly Dictionary<int, bool> Fixed = new Dictionary<int, bool>();
	private static readonly Dictionary<int, (float Size, float Min, float Max, TextOverflowModes Overflow)> TmpBase = new Dictionary<int, (float, float, float, TextOverflowModes)>();

	private static readonly System.Text.RegularExpressions.Regex RichTags = new System.Text.RegularExpressions.Regex(@"</?(?:b|i|size|color|material|quad)(?:=[^<>]*)?>");

	private static float DialogueFactor => Mathf.Clamp(ConfigManager.FontScale.Value, 50, 200) / 100f;
	private static float GameFactor => Mathf.Clamp(ConfigManager.GameFontScale.Value, 50, 200) / 100f;

	/// <summary>每帧调用：比例改了就让文字重新排版。</summary>
	public static void Tick()
	{
		int dialogue = ConfigManager.FontScale.Value, game = ConfigManager.GameFontScale.Value;
		if (dialogue == _appliedDialogue && game == _appliedGame)
		{
			return;
		}
		bool all = game != _appliedGame;
		_appliedDialogue = dialogue;
		_appliedGame = game;
		foreach (Text text in Resources.FindObjectsOfTypeAll<Text>())
		{
			if (text != null && text.gameObject.scene.IsValid() && (all || IsDialogue(text)))
			{
				text.SetAllDirty();
			}
		}
		if (all)
		{
			foreach (TMP_Text text in Resources.FindObjectsOfTypeAll<TMP_Text>())
			{
				if (text != null && text.gameObject.scene.IsValid())
				{
					ApplyTmp(text);
				}
			}
		}
	}

	private static bool IsDialogue(Text text)
	{
		int id = text.GetInstanceID();
		if (!Dialogue.TryGetValue(id, out bool yes))
		{
			yes = false;
			// 屏幕正中的大字旁白（CenterNarrativeSayDialog，StoryText 直接挂在 Panel 下）本来就大，不动
			if (text.name == "StoryText" && text.transform.parent != null && text.transform.parent.name == "Background")
			{
				for (Transform t = text.transform.parent; t != null; t = t.parent)
				{
					if (t.name == "SayDialogs")
					{
						yes = true;
						break;
					}
				}
			}
			Dialogue[id] = yes;
		}
		return yes;
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(Text), nameof(Text.GetGenerationSettings))]
	public static void GetGenerationSettings_Postfix(Text __instance, ref TextGenerationSettings __result)
	{
		float factor = IsFixed(__instance) ? 1f : GameFactor * (IsDialogue(__instance) ? DialogueFactor : 1f);
		if (IsNumber(__instance.text))
		{
			__result.horizontalOverflow = HorizontalWrapMode.Overflow;
			__result.verticalOverflow = VerticalWrapMode.Overflow;
		}
		if (factor == 1f)
		{
			return;
		}
		__result.fontSize = Mathf.Max(1, Mathf.RoundToInt(__result.fontSize * factor));
		__result.resizeTextMinSize = Mathf.Max(1, Mathf.RoundToInt(__result.resizeTextMinSize * factor));
		__result.resizeTextMaxSize = Mathf.Max(1, Mathf.RoundToInt(__result.resizeTextMaxSize * factor));
		if (factor > 1f && __result.resizeTextForBestFit)
		{
			// 「自动缩到框内」的文字（商店物品名这类）：原版已经用到框能放下的最大字号，光放大上限没用。
			// 放大时关掉自动缩放，字号取原上限乘比例；一行字时再按框宽封顶，长名字不挤到旁边的价格上
			__result.resizeTextForBestFit = false;
			int size = __result.resizeTextMaxSize;
			// 数字数前去掉富文本标签（「看过」这类标记是用 <color> 拼进去的），标签不占宽度
			string shown = __instance.supportRichText ? RichTags.Replace(__instance.text ?? "", "") : __instance.text ?? "";
			if (shown.Length > 0 && shown.IndexOf('\n') < 0)
			{
				float width = 0f;
				foreach (char c in shown)
				{
					width += c < 0x2E80 ? 0.55f : 1f;
				}
				// 下限用原版的最小字号（没放大的），长名字最多缩回原版的大小
				size = Mathf.Min(size, Mathf.Max(__instance.resizeTextMinSize, Mathf.FloorToInt(__instance.rectTransform.rect.width / width)));
			}
			__result.fontSize = size;
		}
		if (factor > 1f)
		{
			// 放大后装不下时不截掉（截掉的是整行，数字就没了）；数字、单个词不折行
			__result.verticalOverflow = VerticalWrapMode.Overflow;
			if (IsShort(__instance.text))
			{
				__result.horizontalOverflow = HorizontalWrapMode.Overflow;
			}
		}
	}

	/// <summary>
	/// 不放大的文字：决斗下方的招式卡（招名、消耗都是竖排，一个字一行，本来就大；放大后一行装不下一个字，就横过来了）。
	/// </summary>
	private static bool IsFixed(Text text)
	{
		int id = text.GetInstanceID();
		if (!Fixed.TryGetValue(id, out bool yes))
		{
			Fixed[id] = yes = text.GetComponentInParent<Mortal.Combat.CombatActionButton>(true) != null;
		}
		return yes;
	}

	// 纯数字：数字和 + - / % . , 这几个符号，至少一个数字
	private static bool IsNumber(string text)
	{
		if (string.IsNullOrEmpty(text) || text.Length > 12)
		{
			return false;
		}
		bool digit = false;
		foreach (char c in text)
		{
			if (c >= '0' && c <= '9')
			{
				digit = true;
			}
			else if (c != '+' && c != '-' && c != '/' && c != '%' && c != '.' && c != ',')
			{
				return false;
			}
		}
		return digit;
	}

	// 没有空白的短文字：数字、「Lv.3」、一个词
	private static bool IsShort(string text)
	{
		if (string.IsNullOrEmpty(text) || text.Length > 12)
		{
			return false;
		}
		foreach (char c in text)
		{
			if (char.IsWhiteSpace(c))
			{
				return false;
			}
		}
		return true;
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(TextMeshProUGUI), "OnEnable")]
	public static void TmpOnEnable_Postfix(TextMeshProUGUI __instance)
	{
		ApplyTmp(__instance);
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(TextMeshPro), "OnEnable")]
	public static void TmpWorldOnEnable_Postfix(TextMeshPro __instance)
	{
		ApplyTmp(__instance);
	}

	// 按记下的原字号乘比例；还没记过、比例又是 100% 的不碰
	private static void ApplyTmp(TMP_Text text)
	{
		int id = text.GetInstanceID();
		float factor = GameFactor;
		if (!TmpBase.TryGetValue(id, out var original))
		{
			if (factor == 1f)
			{
				return;
			}
			original = (text.fontSize, text.fontSizeMin, text.fontSizeMax, text.overflowMode);
			TmpBase[id] = original;
		}
		// 放大时超出框的不截掉、不变省略号；回到 100% 换回原来的
		bool clips = original.Overflow == TextOverflowModes.Truncate || original.Overflow == TextOverflowModes.Ellipsis;
		TextOverflowModes overflow = factor > 1f && clips ? TextOverflowModes.Overflow : original.Overflow;
		if (text.overflowMode != overflow)
		{
			text.overflowMode = overflow;
		}
		float size = original.Size * factor;
		if (Mathf.Approximately(text.fontSize, size))
		{
			return;
		}
		text.fontSizeMin = original.Min * factor;
		text.fontSizeMax = original.Max * factor;
		text.fontSize = size;
	}
}
