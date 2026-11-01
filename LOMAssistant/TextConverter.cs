using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LOMAssistant;

/// <summary>
/// 简繁转换。游戏数据里的开发备注、事件名等多为繁体，本 Mod 内部（比较、搜索、拼字）统一用简体（ToSimplified）；
/// 界面语言是繁体中文时，显示前再转回繁体（ForDisplay，韩语、日语见 UiText）。都用 Windows 自带的 LCMapStringEx 逐字转换；失败时原样返回。
/// </summary>
public static class TextConverter
{
	private const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;
	private const uint LCMAP_TRADITIONAL_CHINESE = 0x04000000;

	/// <summary>界面上的文字带数字（倒计时、数值），转过的会越积越多，超过这么多条就清掉重来。</summary>
	private const int CacheLimit = 20000;

	private static readonly Dictionary<string, string> _cache = new Dictionary<string, string>();
	private static readonly Dictionary<string, string> _traditionalCache = new Dictionary<string, string>();

	private static bool _unavailable;

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern int LCMapStringEx(string lpLocaleName, uint dwMapFlags, string lpSrcStr, int cchSrc,
		[Out] char[] lpDestStr, int cchDest, IntPtr lpVersionInformation, IntPtr lpReserved, IntPtr sortHandle);

	/// <summary>转成简体。带假名的是日语（日语补丁把游戏文字换成了日语），不转，不然「話」会变成「话」。</summary>
	public static string ToSimplified(string text) => HasKana(text) ? text : Cached(_cache, text, "zh-CN", LCMAP_SIMPLIFIED_CHINESE);

	/// <summary>
	/// 从游戏取来的名字（数值、人物、物品等）拼成的说明转简体；界面是日语时不转：日语补丁给的日语名字没有假名时
	/// （「銀両」「体力」）会被当成中文转成简体（「银两」）。旗标名在 DataHelper 里取的时候已经转好，不受影响。
	/// </summary>
	public static string ToSimplifiedUnlessJapanese(string text) => DisplayLanguage == UiText.Japanese ? text : ToSimplified(text);

	private static bool HasKana(string text)
	{
		if (text == null)
		{
			return false;
		}
		foreach (char c in text)
		{
			if (c >= 'ぁ' && c <= 'ヿ' && c != '・')
			{
				return true;
			}
		}
		return false;
	}

	private static readonly Dictionary<string, string> _hantCache = new Dictionary<string, string>();

	/// <summary>转成繁体：逐字转换以后，再按 UiTraditional 把转不对的字和大陆用语换成繁中习惯（設定、介面、視窗……）。</summary>
	public static string ToTraditional(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text;
		}
		if (_hantCache.TryGetValue(text, out string result))
		{
			return result;
		}
		if (_hantCache.Count > CacheLimit)
		{
			_hantCache.Clear();
		}
		return _hantCache[text] = UiTraditional.Fix(Cached(_traditionalCache, text, "zh-TW", LCMAP_TRADITIONAL_CHINESE));
	}

	/// <summary>
	/// 本 Mod 界面用的语言：ChineseSimplified / ChineseTraditional / Korean / Japanese。
	/// 设置里「界面语言」选了就用选的；「跟随游戏」时用游戏的语言，游戏是简体但装了日语补丁（它覆盖的就是简体）时算日语。
	/// </summary>
	public static string DisplayLanguage
	{
		get
		{
			switch (LOMAssistant.Config.ConfigManager.UiLanguage?.Value ?? 0)
			{
				case 1: return "ChineseSimplified";
				case 2: return "ChineseTraditional";
				case 3: return UiText.Korean;
				case 4: return UiText.Japanese;
				case 5: return UiText.English;
			}
			string game = GameLanguage;
			// 英文补丁、日语补丁覆盖的都是简体
			return game == "ChineseSimplified" && JapanesePatchInstalled ? UiText.Japanese : game == "ChineseSimplified" && EnglishPatchInstalled ? UiText.English : game;
		}
	}

	/// <summary>游戏现在的语言（Lean Localization 的语言名）；取不到时为 null。</summary>
	public static string GameLanguage
	{
		get
		{
			try
			{
				return Lean.Localization.LeanLocalization.CurrentLanguage;
			}
			catch
			{
				return null;
			}
		}
	}

	private static bool _englishPatch;
	private static int? _englishCheckedAt;

	/// <summary>
	/// 装了英文补丁：joshfreitas1984 的 LLM 英文补丁（FanslationStudio.LegendOfMortal…），或 velvetcakes 的英文补丁（XUnity.AutoTranslator 加 BepInEx\Translation\en\Text 里的翻译文件）。
	/// 和日语补丁一样，没找到时每隔几秒再看一次。
	/// </summary>
	public static bool EnglishPatchInstalled
	{
		get
		{
			if (_englishPatch || _englishCheckedAt.HasValue && Environment.TickCount - _englishCheckedAt.Value < 3000)
			{
				return _englishPatch;
			}
			_englishCheckedAt = Environment.TickCount;
			foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				string name = assembly.GetName().Name;
				// 只装了 XUnity（可能翻成别的语言、或者只是机翻）不算：要有英文补丁自带的翻译文件
				if (name.StartsWith("FanslationStudio.LegendOfMortal", StringComparison.OrdinalIgnoreCase)
					|| name.StartsWith("XUnity.AutoTranslator", StringComparison.OrdinalIgnoreCase) && XUnityTranslations.PatchFilesPresent)
				{
					FileLogger.Log("检测到英文补丁（" + name + "），界面跟随游戏时显示英语");
					return _englishPatch = true;
				}
			}
			return false;
		}
	}

	private static bool _japanesePatch;
	// 还没看过时为 null。以前用 int.MinValue 当「很久以前」，但 TickCount 是正数（开机不到 24.9 天）时
	// TickCount - int.MinValue 溢出成负数，永远「不到 3 秒」，一次都不看，装了日语补丁也显示中文
	private static int? _japaneseCheckedAt;

	/// <summary>
	/// 装了玩家做的日语补丁（活俠傳日本語制作会，BepInEx 插件都叫 LOM_JP_…）。
	/// 插件可能比本 Mod 晚载入：没找到时每隔几秒再看一次，找到就不再看。
	/// </summary>
	public static bool JapanesePatchInstalled
	{
		get
		{
			// 两个 TickCount 相减，TickCount 绕回负数时差值照样对
			if (_japanesePatch || _japaneseCheckedAt.HasValue && Environment.TickCount - _japaneseCheckedAt.Value < 3000)
			{
				return _japanesePatch;
			}
			_japaneseCheckedAt = Environment.TickCount;
			foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (assembly.GetName().Name.StartsWith("LOM_JP_", StringComparison.OrdinalIgnoreCase))
				{
					FileLogger.Log("检测到日语补丁（" + assembly.GetName().Name + "），界面跟随游戏时显示日语");
					return _japanesePatch = true;
				}
			}
			return false;
		}
	}

	/// <summary>文字开头加上这个（不显示的「词连接符」），画的时候就原样显示，不转繁体、不查表翻译。给 AI 的回答这类已经是界面语言的文字用。</summary>
	public const char Verbatim = '⁠';

	/// <summary>要显示给玩家的文字：界面语言是繁体时转成繁体，韩语、日语时查表（查不到原样），否则原样。</summary>
	public static string ForDisplay(string text)
	{
		text = UiConditionNames.Describe(text);
		string language = DisplayLanguage;
		if (language == "ChineseTraditional")
		{
			return ToTraditional(text);
		}
		return language is UiText.Korean or UiText.Japanese or UiText.English ? UiText.Translate(text, language) : text;
	}

	private static string Cached(Dictionary<string, string> cache, string text, string locale, uint flags)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text;
		}
		if (cache.TryGetValue(text, out var value))
		{
			return value;
		}
		if (cache.Count >= CacheLimit)
		{
			cache.Clear();
		}
		value = Convert(text, locale, flags);
		cache[text] = value;
		return value;
	}

	private static string Convert(string text, string locale, uint flags)
	{
		if (_unavailable)
		{
			return text;
		}
		try
		{
			int size = LCMapStringEx(locale, flags, text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
			if (size <= 0)
			{
				return text;
			}
			var buffer = new char[size];
			int written = LCMapStringEx(locale, flags, text, text.Length, buffer, size, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
			return written > 0 ? new string(buffer, 0, written) : text;
		}
		catch (Exception e)
		{
			// Not on Windows / API missing: stop trying and show the original text.
			_unavailable = true;
			FileLogger.LogError("简繁转换不可用: " + e.Message);
			return text;
		}
	}

	public static void ClearCache()
	{
		_cache.Clear();
		_traditionalCache.Clear();
	}
}
