using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace LOMAssistant;

/// <summary>
/// 简繁转换。游戏数据里的开发备注、事件名等多为繁体，本 Mod 内部（比较、搜索、拼字）统一用简体（ToSimplified）；
/// 界面语言是繁体中文时，显示前再转回繁体（ForDisplay，韩语、日语见 UiText）。
/// 转简体时记下游戏的繁体原文，转回繁体时游戏的字用原文；只有 Mod 自己的简体字才用 Windows 自带的 LCMapStringEx 逐字转换，失败时原样返回。
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

	/// <summary>
	/// 转成简体。带假名的是日语（日语补丁把游戏文字换成了日语），不转，不然「話」会变成「话」。
	/// 转的是游戏的繁体原文时，记下原文（RememberOriginal），繁中界面显示时用原文，不再逐字转回去。
	/// </summary>
	public static string ToSimplified(string text)
	{
		if (string.IsNullOrEmpty(text) || HasKana(text))
		{
			return text;
		}
		if (_cache.TryGetValue(text, out string cached))
		{
			return cached;
		}
		string result = Cached(_cache, text, "zh-CN", LCMAP_SIMPLIFIED_CHINESE);
		RememberOriginal(text, result);
		return result;
	}

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

	/// <summary>游戏原文：按连续的汉字段记「简体 → 繁体原文」。简繁一字对多字（干/幹/乾、余/餘、斗/鬥……），逐字转回去会错，原文不会。</summary>
	private static readonly Dictionary<string, string> _originals = new Dictionary<string, string>();

	/// <summary>同上，按段首字分组、长的在前，找「被截断的原文」（剧情图里截短加「…」的对白）用。</summary>
	private static readonly Dictionary<char, List<string>> _originalsByFirst = new Dictionary<char, List<string>>();

	/// <summary>记的段数上限：一局下来对白、旗标名、任务名加起来也就几万段；到了就不再记。</summary>
	private const int OriginalLimit = 150000;

	/// <summary>截断的对白至少剩这么多字，才按原文开头还原（太短的容易撞上 Mod 自己的词）。</summary>
	private const int TruncatedMin = 4;

	/// <summary>游戏原文里的错字（任务名「夏侯蘭鍛鏈唐門弟子」），显示时改正。</summary>
	private static readonly (string Typo, string Correct)[] GameTypos = { ("鍛鏈", "鍛鍊") };

	private static bool IsHan(char c) => c >= '㐀' && c <= '鿿' || c >= '豈' && c <= '﫿';

	/// <summary>
	/// 记下游戏原文。逐字转换前后长度一样，按位置对得上：把每段连续汉字（至少两个字）记成「简体段 → 原文段」。
	/// 整句转换前后一样的不记：不知道是游戏的繁体原文，还是简体游戏给的简体字（记了繁中界面就不转了）。
	/// 整句变了（确定是繁体原文）时，里面简繁同形的段（「世人皆云」）逐字转回去会改错的，也记下来。
	/// </summary>
	private static void RememberOriginal(string original, string simplified)
	{
		if (original == simplified || original.Length != simplified.Length || _originals.Count >= OriginalLimit)
		{
			return;
		}
		int i = 0;
		while (i < original.Length)
		{
			if (!IsHan(original[i]))
			{
				i++;
				continue;
			}
			int start = i;
			while (i < original.Length && IsHan(original[i]))
			{
				i++;
			}
			int length = i - start;
			if (length < 2)
			{
				continue;
			}
			string key = simplified.Substring(start, length);
			string value = original.Substring(start, length);
			if (_originals.ContainsKey(key) || key == value && ConvertOwn(key) == value)
			{
				continue;
			}
			foreach (var (typo, correct) in GameTypos)
			{
				value = value.Replace(typo, correct);
			}
			_originals[key] = value;
			if (!_originalsByFirst.TryGetValue(key[0], out List<string> list))
			{
				_originalsByFirst[key[0]] = list = new List<string>();
			}
			int at = list.FindIndex(k => k.Length < key.Length);
			list.Insert(at < 0 ? list.Count : at, key);
			_hantCache.Clear();
		}
	}

	/// <summary>
	/// 转成繁体。逐段看连续的汉字：
	/// 游戏原文记过的段（含截短加「…」的对白开头）直接用原文；本来就是繁体的段（游戏直接给的繁体）原样留着；
	/// 剩下的是 Mod 自己的简体字，逐字转换以后再按 UiTraditional 换成繁中习惯（設定、介面、視窗……）。
	/// 用语表只管 Mod 自己的字：游戏原文里的「支持」「回復」「保存」不会被换掉。
	/// </summary>
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
		var output = new System.Text.StringBuilder(text.Length);
		var own = new System.Text.StringBuilder();
		int i = 0;
		while (i < text.Length)
		{
			if (!IsHan(text[i]))
			{
				own.Append(text[i++]);
				continue;
			}
			int start = i;
			while (i < text.Length && IsHan(text[i]))
			{
				i++;
			}
			string run = text.Substring(start, i - start);
			string original = OriginalOf(run, i == text.Length || text[i] == '…' || text[i] == '.');
			if (original == null && run != Cached(_cache, run, "zh-CN", LCMAP_SIMPLIFIED_CHINESE))
			{
				original = run; // 已经是繁体（游戏直接给的），不再转
			}
			if (original == null)
			{
				own.Append(run);
				continue;
			}
			output.Append(ConvertOwn(own.ToString()));
			own.Clear();
			output.Append(original);
		}
		output.Append(ConvertOwn(own.ToString()));
		return _hantCache[text] = output.ToString();
	}

	/// <summary>这段汉字的游戏原文：整段记过的；或者这段在截断处（后面是「…」或到头了），是记过的某段的开头。都不是时为 null。</summary>
	private static string OriginalOf(string run, bool truncated)
	{
		if (run.Length < 2)
		{
			return null;
		}
		if (_originals.TryGetValue(run, out string original))
		{
			return original;
		}
		if (!truncated || run.Length < TruncatedMin || !_originalsByFirst.TryGetValue(run[0], out List<string> list))
		{
			return null;
		}
		foreach (string key in list)
		{
			if (key.Length <= run.Length)
			{
				break;
			}
			if (key.StartsWith(run, StringComparison.Ordinal))
			{
				return _originals[key].Substring(0, run.Length);
			}
		}
		return null;
	}

	/// <summary>Mod 自己的简体字：逐字转繁体，再换成繁中习惯的说法。</summary>
	private static string ConvertOwn(string text) =>
		text.Length == 0 ? text : UiTraditional.Fix(Cached(_traditionalCache, text, "zh-TW", LCMAP_TRADITIONAL_CHINESE));

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
