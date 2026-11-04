using System.Collections.Generic;

// 不装游戏时替代 Mod 里碰游戏的那几个类型，让界面文字表能单独编译。只给 tests/run-language-tests.sh 用。

namespace LOMAssistant
{
	internal static class FileLogger
	{
		public static void Log(string message)
		{
		}

		public static void LogWarning(string message)
		{
		}

		public static void LogError(string message)
		{
		}
	}

	internal static class XUnityTranslations
	{
		public static bool PatchFilesPresent => false;

		public static Dictionary<string, string> Load() => new Dictionary<string, string>();
	}

	internal static class JapanesePatchCompat
	{
		public const string NoticeText = "测试用提示";
	}
}

namespace LOMAssistant.Config
{
	public class ConfigElement<T>
	{
		public T m_value;

		public T Value => m_value;
	}

	public static class ConfigManager
	{
		public static ConfigElement<int> UiLanguage;
	}
}

namespace Lean.Localization
{
	public static class LeanLocalization
	{
		public static string CurrentLanguage => "ChineseSimplified";
	}
}
