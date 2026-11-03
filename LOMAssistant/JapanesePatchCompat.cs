using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace LOMAssistant;

/// <summary>
/// 和日语补丁（活俠傳日本語制作会）的注音插件（LOM JP Ruby Prototype，lom.jp.rubyproto）一起装时的兼容处理。
/// 它按 F7 切换注音（振假名）显示（全部 / 只显示第一次 / 不显示），Ctrl+F7 清空「显示过」的记录，键写死在它的代码里、不能改。
/// 本 Mod 的「随时存档：读取最新存档」默认也是 F7，按下就直接读档，玩家切换注音时会把进度读回去。
/// 第一次检测到它时，本 Mod 用 F7 的热键改成不绑定，键让给它，只改一次；之后玩家自己再绑 F7 就不管了。
/// </summary>
public static class JapanesePatchCompat
{
	public const string RubyPluginId = "lom.jp.rubyproto";

	/// <summary>日语补丁切换注音显示的键。</summary>
	public const KeyCode RubyKey = KeyCode.F7;

	/// <summary>要给玩家看的提示（显示在设置页最上面）。</summary>
	public static readonly List<string> Notices = new List<string>();

	// 不带热键名，整句查得到韩语、日语（UiText）
	public const string NoticeText = "检测到日语补丁：F7 是它切换注音显示的键，不能改。本 Mod 用 F7 的热键已改成不绑定，可以在设置页「热键」里另绑别的键。";

	private static string Mark => Path.Combine(Path.GetDirectoryName(typeof(JapanesePatchCompat).Assembly.Location) ?? ".", "StoryReview", "compat-jp-ruby");

	public static void Apply()
	{
		try
		{
			if (!ConflictDetector.LoadedPluginNames().ContainsKey(RubyPluginId) || File.Exists(Mark))
			{
				return;
			}
			List<string> taken = ConflictDetector.YieldHotkeys(RubyKey);
			if (taken.Count > 0)
			{
				Notices.Add(NoticeText);
				FileLogger.Log("[兼容] 检测到日语补丁的注音插件，F7 让给它，本 Mod 改成不绑定的热键：" + string.Join("、", taken.ToArray()));
			}
			Directory.CreateDirectory(Path.GetDirectoryName(Mark));
			File.WriteAllText(Mark, "");
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[兼容] 处理日语补丁的 F7 失败: " + e.Message);
		}
	}
}
