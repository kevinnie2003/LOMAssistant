using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// 安装器的界面语言：按 Windows 的显示语言选简体、繁体、韩语、日语，其他语言一律英语。
/// 文字都按简体写，显示前用 L / F 换：繁体用系统的简繁转换，韩、日、英查下面的表，表里没有的照旧显示简体。
/// 更新内容（CHANGELOG）有译文的版本用译文（CHANGELOG.en/ja/ko.md），没翻的版本照旧显示中文。
/// </summary>
internal static class InstallerText
{
	public enum Lang { Simplified, Traditional, English, Japanese, Korean }

	public static readonly Lang Current = Detect();

	/// <summary>界面字体：日语、韩语、英语用各自系统自带的界面字体，中文用微软雅黑。</summary>
	public static string FontName => Current == Lang.Japanese ? "Yu Gothic UI" : Current == Lang.Korean ? "Malgun Gothic" : Current == Lang.English ? "Segoe UI" : "Microsoft YaHei UI";

	private static Lang Detect()
	{
		CultureInfo culture = CultureInfo.CurrentUICulture;
		string name = culture.Name, code = culture.TwoLetterISOLanguageName;
		// 测试用：命令行加 --lang=ja（或 en、ko、zh-Hans、zh-Hant）换语言
		foreach (string arg in Environment.GetCommandLineArgs())
		{
			if (arg.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase))
			{
				name = arg.Substring(7);
				code = name.Split('-')[0].ToLowerInvariant();
			}
		}
		switch (code)
		{
			case "zh":
				return name.Contains("Hant") || name.EndsWith("TW") || name.EndsWith("HK") || name.EndsWith("MO") ? Lang.Traditional : Lang.Simplified;
			case "ja":
				return Lang.Japanese;
			case "ko":
				return Lang.Korean;
			default:
				return Lang.English;
		}
	}

	/// <summary>更新内容译文文件的后缀（en、ja、ko），简体、繁体为 null。</summary>
	public static string ChangelogSuffix => Current == Lang.English ? "en" : Current == Lang.Japanese ? "ja" : Current == Lang.Korean ? "ko" : null;

	public static string L(string text)
	{
		switch (Current)
		{
			case Lang.Simplified:
				return text;
			case Lang.Traditional:
				return ToTraditional(text);
		}
		int column = Current == Lang.English ? 0 : Current == Lang.Japanese ? 1 : 2;
		return Table.TryGetValue(text, out string[] row) ? row[column] : text;
	}

	public static string F(string format, params object[] args) => string.Format(L(format), args);

	/// <summary>路径里有中文等非 ASCII 字符：游戏装在这种路径下，BepInEx 预加载会失败（Harmony 报 IL Compile Error），Mod 不生效。</summary>
	public static bool NonAsciiPath(string path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return false;
		}
		foreach (char c in path)
		{
			if (c > 127)
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>中文更新内容里有译文的版本换成译文（按「## 版本」分段），没翻的版本照旧；繁体整篇转繁体。</summary>
	public static string Changelog(string chinese, string translated)
	{
		if (Current == Lang.Traditional)
		{
			return ToTraditional(chinese);
		}
		if (string.IsNullOrEmpty(translated))
		{
			return chinese;
		}
		Dictionary<string, string> sections = Sections(translated);
		var result = new StringBuilder();
		foreach (KeyValuePair<string, string> section in Sections(chinese))
		{
			result.Append(sections.TryGetValue(section.Key, out string text) ? text : section.Value);
		}
		return result.ToString();
	}

	// 「## 2.7.0（日期）」开头的一段一段，键是版本号；第一个标题前面的部分键为空
	private static Dictionary<string, string> Sections(string text)
	{
		var sections = new Dictionary<string, string>();
		string key = "";
		var current = new StringBuilder();
		foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
		{
			if (line.StartsWith("## "))
			{
				sections[key] = current.ToString();
				current.Clear();
				string rest = line.Substring(3).TrimStart('v', 'V');
				int end = 0;
				while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '.'))
				{
					end++;
				}
				key = rest.Substring(0, end);
			}
			current.Append(line).Append('\n');
		}
		sections[key] = current.ToString();
		return sections;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int LCMapStringEx(string locale, uint flags, string src, int srcLength, StringBuilder dest, int destLength, IntPtr version, IntPtr reserved, IntPtr param);

	private const uint LCMAP_TRADITIONAL_CHINESE = 0x04000000;

	private static string ToTraditional(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text;
		}
		try
		{
			var dest = new StringBuilder(text.Length + 16);
			int n = LCMapStringEx("zh-TW", LCMAP_TRADITIONAL_CHINESE, text, text.Length, dest, dest.Capacity, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
			// 逐字转换后再换成繁中习惯用语（和 Mod 界面同一张表）
			return LOMAssistant.UiTraditional.Fix(n > 0 ? dest.ToString(0, n) : text);
		}
		catch
		{
			return text;
		}
	}

	// 简体 → 英语、日语、韩语
	private static readonly Dictionary<string, string[]> Table = Build(new[]
	{
		new[] { "咖啡伴侣 安装器", "Coffee Companion Installer", "珈琲のおとも インストーラー", "커피 동반자 설치 프로그램" },
		new[] { "更新内容", "What's new", "更新内容", "업데이트 내용" },
		new[] { "日志", "Log", "ログ", "로그" },
		new[] { "游戏目录", "Game folder", "ゲームフォルダ", "게임 폴더" },
		new[] { "其他 Mod", "Other mods", "ほかの MOD", "다른 Mod" },
		new[] { "勾选的移到「DisabledMods」，不删除", "Checked mods are moved to 「DisabledMods」, not deleted", "チェックした MOD は「DisabledMods」（無効化した MOD）フォルダへ移します。削除はしません", "체크한 Mod는 「DisabledMods」(사용 중지한 Mod) 폴더로 옮깁니다. 삭제하지 않습니다" },
		new[] { "没有发现其他 Mod。", "No other mods found.", "ほかの MOD は見つかりませんでした。", "다른 Mod가 없습니다." },
		new[] { "跳过游戏开头 Logo（想恢复就取消勾选再安装一次）", "Skip opening logos (uncheck and reinstall to undo)", "ゲーム冒頭のロゴを飛ばす（戻すにはチェックを外して再インストール）", "게임 시작 로고 건너뛰기(되돌리려면 체크를 풀고 다시 설치)" },
		new[] { "不联网检查更新，只装压缩包自带的版本", "No online update check (install this zip's version)", "オンラインで更新を確認しない（この ZIP の版を入れる）", "온라인 업데이트 확인 안 함(이 압축 파일의 버전 설치)" },
		new[] { "一键安装 / 更新", "Install / Update", "インストール／更新", "설치 / 업데이트" },
		new[] { "卸载本 Mod", "Uninstall", "アンインストール", "제거" },
		new[] { "导出诊断", "Export diagnostics", "診断を書き出す", "진단 내보내기" },
		new[] { "选择…", "Browse…", "選択…", "선택…" },
		new[] { "多功能 Mod（Jim97）", "多功能 Mod (Jim97)", "多功能 Mod（Jim97）", "多功能 Mod(Jim97)" },
		new[] { "随时存档独立版，已内置", "standalone 随时存档, now built in", "随时存档の単体版（内蔵済み）", "随时存档 단독판(이미 내장)" },
		new[] { "活侠传助手 / 本 Mod 的旧版", "活侠传助手 / an old version of this mod", "活侠传助手／このMODの旧版", "活侠传助手 / 이 Mod의 구버전" },
		new[] { "没有自动找到游戏目录，请点「选择…」手动选择游戏目录（里面有 Mortal.exe）。", "Couldn't find the game folder. Click 「Browse…」 and pick the folder that contains Mortal.exe.", "ゲームフォルダが見つかりません。「選択…」を押して、Mortal.exe があるフォルダを選んでください。", "게임 폴더를 찾지 못했습니다. 「선택…」을 눌러 Mortal.exe가 있는 폴더를 고르세요." },
		new[] { "已安装", "Installed", "インストール済み", "설치됨" },
		new[] { "将安装", "Will install", "インストール予定", "설치 예정" },
		new[] { "从 {0} 更新到 {1} 的新内容", "What's new from {0} to {1}", "{0} から {1} への更新内容", "{0}에서 {1}(으)로의 업데이트 내용" },
		new[] { "{0} 的更新内容", "What's new in {0}", "{0} の更新内容", "{0} 업데이트 내용" },
		new[] { "没有找到更新说明。", "No release notes found.", "更新内容が見つかりません。", "업데이트 내용을 찾지 못했습니다." },
		new[] { "正在检查 GitHub 上的最新版…", "Checking GitHub for the latest version…", "GitHub で最新版を確認中…", "GitHub에서 최신 버전 확인 중…" },
		new[] { "将从 GitHub 下载最新版", "The latest version will be downloaded from GitHub", "GitHub から最新版をダウンロードします", "GitHub에서 최신 버전을 내려받습니다" },
		new[] { "压缩包里的就是最新版", "The zip already has the latest version", "ZIP に入っているのが最新版です", "압축 파일에 든 것이 최신 버전입니다" },
		new[] { "GitHub 连不上，安装压缩包自带的版本", "Can't reach GitHub; installing the version in the zip", "GitHub に接続できないため、ZIP に同梱の版をインストールします", "GitHub에 연결할 수 없어 압축 파일에 든 버전을 설치합니다" },
		new[] { "连不上 GitHub，这个安装器也不在解压后的压缩包里", "Can't reach GitHub, and this installer isn't inside the extracted zip", "GitHub に接続できず、このインストーラーも展開した ZIP の中にありません", "GitHub에 연결할 수 없고, 이 설치 프로그램도 압축을 푼 폴더 안에 있지 않습니다" },
		new[] { "已经是最新版", "Already up to date", "最新版です", "이미 최신 버전입니다" },
		new[] { "已关闭联网检查，安装压缩包自带的版本", "Online check is off; installing the version in this zip", "オンライン確認オフ。この ZIP の版をインストールします", "온라인 확인 꺼짐. 이 압축 파일의 버전을 설치합니다" },
		new[] { "已关闭联网检查，这个安装器也不在解压后的压缩包里", "Online check is off, and this installer isn't inside the extracted zip", "オンライン確認がオフで、このインストーラーも展開した ZIP の中にありません", "온라인 확인이 꺼져 있고, 이 설치 프로그램도 압축을 푼 폴더 안에 있지 않습니다" },
		new[] { "已经装的就是压缩包里的版本", "The version in this zip is already installed", "この ZIP の版がすでに入っています", "이 압축 파일의 버전이 이미 설치되어 있습니다" },
		new[] { "会换成压缩包自带的旧版本", "Will switch to the older version in this zip", "この ZIP に入っている古い版に戻します", "이 압축 파일에 든 이전 버전으로 바꿉니다" },
		new[] { "未安装", "Not installed", "未インストール", "설치 안 됨" },
		new[] { "选择《活侠传》游戏目录（里面有 Mortal.exe）", "Choose the Legend of Mortal game folder (the one with Mortal.exe)", "『活俠傳』のゲームフォルダ（Mortal.exe があるフォルダ）を選んでください", "『활협전』 게임 폴더(Mortal.exe가 있는 폴더)를 고르세요" },
		new[] { "这个文件夹里没有 Mortal.exe，不是游戏目录。\n\nSteam 中右键《活侠传》→ 管理 → 浏览本地文件，打开的就是游戏目录。", "This folder has no Mortal.exe, so it isn't the game folder.\n\nIn Steam, right-click Legend of Mortal → Manage → Browse local files; the folder that opens is the game folder.", "このフォルダには Mortal.exe がないため、ゲームフォルダではありません。\n\nSteam で『活俠傳』を右クリック → 管理 → ローカルファイルを閲覧 で開くフォルダがゲームフォルダです。", "이 폴더에는 Mortal.exe가 없어 게임 폴더가 아닙니다.\n\nSteam에서 『활협전』 우클릭 → 관리 → 로컬 파일 보기로 열리는 폴더가 게임 폴더입니다." },
		new[] { "选错了", "Wrong folder", "フォルダが違います", "잘못된 폴더" },
		new[] { "（未找到，请点「选择…」）", "(not found, click 「Browse…」)", "（見つかりません。「選択…」を押してください）", "(찾지 못함, 「선택…」을 누르세요)" },
		new[] { "游戏目录：{0}", "Game folder: {0}", "ゲームフォルダ：{0}", "게임 폴더: {0}" },
		new[] { "{0} （已知冲突：{1}）", "{0} (known conflict: {1})", "{0}（既知の競合：{1}）", "{0} (알려진 충돌: {1})" },
		new[] { "{0}　【预加载补丁：本 Mod 不需要，可能导致 BepInEx 启动失败】", "{0}　[preloader patch: not needed by this mod, may stop BepInEx from starting]", "{0}　【プリローダーパッチ：このMODには不要。BepInEx が起動しない原因になることがあります】", "{0}　【프리로더 패치: 이 Mod에는 필요 없고 BepInEx가 시작되지 않는 원인이 될 수 있음】" },
		new[] { "请先选择游戏目录。", "Choose the game folder first.", "先にゲームフォルダを選んでください。", "먼저 게임 폴더를 고르세요." },
		new[] { "卸载", "Uninstall", "アンインストール", "제거" },
		new[] { "安装", "Install", "インストール", "설치" },
		new[] { "游戏正在运行，请先退出游戏再卸载。", "The game is running. Quit it before uninstalling.", "ゲームが起動中です。終了してからアンインストールしてください。", "게임이 실행 중입니다. 종료한 뒤 제거하세요." },
		new[] { "游戏正在运行，请先退出游戏再安装。", "The game is running. Quit it before installing.", "ゲームが起動中です。終了してからインストールしてください。", "게임이 실행 중입니다. 종료한 뒤 설치하세요." },
		new[] { "确定卸载咖啡伴侣吗？", "Uninstall Coffee Companion?", "珈琲のおともをアンインストールしますか？", "커피 동반자를 제거할까요?" },
		new[] { "还有 {0} 个别的 Mod，BepInEx 加载器会留给它们用，只删本 Mod。", "There are {0} other mods, so the BepInEx loader stays for them; only this mod is removed.", "ほかに {0} 個の MOD があるため、BepInEx ローダーは残し、このMODだけを削除します。", "다른 Mod가 {0}개 있어 BepInEx 로더는 남겨 두고 이 Mod만 삭제합니다." },
		new[] { "没有别的 Mod 了，BepInEx 加载器也一起删掉，游戏回到没装 Mod 的样子。", "No other mods are left, so the BepInEx loader is removed too and the game goes back to unmodded.", "ほかの MOD がないので BepInEx ローダーも削除し、MOD を入れる前の状態に戻します。", "다른 Mod가 없으므로 BepInEx 로더도 함께 삭제해 Mod를 설치하기 전 상태로 되돌립니다." },
		new[] { "存档不受影响（随时存档、剧情回顾收录的节点也留着，以后重装还能接着用）。", "Saves are not touched (quick saves and branch saves are kept too, so you can use them again after reinstalling).", "セーブには影響しません（クイックセーブや分岐セーブも残るので、再インストール後も使えます）。", "저장 파일에는 영향이 없습니다(퀵세이브와 분기 저장도 남아 있어 다시 설치하면 이어서 쓸 수 있습니다)." },
		new[] { "恢复开头 Logo 失败（{0}）。", "Failed to restore the opening logos ({0}).", "冒頭ロゴの復元に失敗しました（{0}）。", "시작 로고 복원 실패({0})." },
		new[] { "已卸载咖啡伴侣。", "Coffee Companion has been uninstalled.", "珈琲のおともをアンインストールしました。", "커피 동반자를 제거했습니다." },
		new[] { "以前停用的别的 Mod 还在「BepInEx\\DisabledMods」里，想用的话自己挪回去。", "Mods you disabled earlier are still in 「BepInEx\\DisabledMods」; move them back yourself if you want them.", "以前無効にしたほかの MOD は「BepInEx\\DisabledMods」に残っています。使う場合は自分で戻してください。", "이전에 사용 중지한 다른 Mod는 「BepInEx\\DisabledMods」에 남아 있습니다. 쓰려면 직접 옮기세요." },
		new[] { "卸载没有完成：{0}\n\n请确认游戏已退出，再试一次。", "Uninstall didn't finish: {0}\n\nMake sure the game is closed, then try again.", "アンインストールが完了しませんでした：{0}\n\nゲームを終了してから、もう一度お試しください。", "제거를 마치지 못했습니다: {0}\n\n게임이 종료됐는지 확인한 뒤 다시 시도하세요." },
		new[] { "已删除 {0}", "Deleted {0}", "{0} を削除しました", "{0} 삭제함" },
		new[] { "连不上 GitHub（{0}），将安装压缩包自带的版本。", "Can't reach GitHub ({0}); the version in the zip will be installed.", "GitHub に接続できません（{0}）。ZIP に同梱の版をインストールします。", "GitHub에 연결할 수 없습니다({0}). 압축 파일에 든 버전을 설치합니다." },
		new[] { "已导出诊断：{0}", "Diagnostics exported: {0}", "診断を書き出しました：{0}", "진단을 내보냈습니다: {0}" },
		new[] { "加载器开关被关掉了（doorstop_config.ini 里 enabled = false），重新安装把它打开。", "The loader was switched off (enabled = false in doorstop_config.ini); reinstalling to turn it back on.", "ローダーがオフになっています（doorstop_config.ini の enabled = false）。再インストールしてオンに戻します。", "로더가 꺼져 있습니다(doorstop_config.ini의 enabled = false). 다시 설치해서 켭니다." },
		new[] { "已导出：\n{0}\n\n路径已复制，资源管理器里也已选中它。反馈问题时把这个压缩包发给作者（QQ 群 496887060）。", "Exported:\n{0}\n\nThe path has been copied and the file is selected in Explorer. When reporting a problem, attach this zip to a GitHub issue.", "書き出しました：\n{0}\n\nパスはコピー済みで、エクスプローラーでも選択してあります。不具合を報告するときは、この ZIP を GitHub の Issues に添付してください。", "내보냈습니다:\n{0}\n\n경로를 복사했고 탐색기에서도 선택해 두었습니다. 문제를 알릴 때 이 압축 파일을 GitHub Issues에 첨부해 주세요." },
		new[] { "导出诊断失败：{0}", "Failed to export diagnostics: {0}", "診断の書き出しに失敗しました：{0}", "진단 내보내기 실패: {0}" },
		new[] { "导出失败：{0}", "Export failed: {0}", "書き出しに失敗しました：{0}", "내보내기 실패: {0}" },
		new[] { "已经是最新版本 {0}，不需要重新安装。", "Already on the latest version {0}; no need to reinstall.", "すでに最新版 {0} です。再インストールは不要です。", "이미 최신 버전 {0}입니다. 다시 설치할 필요가 없습니다." },
		new[] { "已经装的就是压缩包里的 {0}，不需要重新安装。", "{0} from this zip is already installed; no need to reinstall.", "この ZIP の {0} がすでに入っています。再インストールは不要です。", "이 압축 파일의 {0}이(가) 이미 설치되어 있습니다. 다시 설치할 필요가 없습니다." },
		new[] { "已关闭联网检查更新，不连 GitHub。", "Online update check is off; not connecting to GitHub.", "オンラインでの更新確認はオフです。GitHub には接続しません。", "온라인 업데이트 확인이 꺼져 있어 GitHub에 연결하지 않습니다." },
		new[] { "已关闭联网检查，安装压缩包自带的版本 {0}。", "Online check is off; installing {0} from this zip.", "オンライン確認はオフです。この ZIP の {0} をインストールします。", "온라인 확인이 꺼져 있어 이 압축 파일의 {0}을(를) 설치합니다." },
		new[] { "已关闭联网检查，把已装的 {1} 换成压缩包自带的 {0}。", "Online check is off; replacing the installed {1} with {0} from this zip.", "オンライン確認はオフです。インストール済みの {1} をこの ZIP の {0} に置き換えます。", "온라인 확인이 꺼져 있어 설치된 {1}을(를) 이 압축 파일의 {0}(으)로 바꿉니다." },
		new[] { "没能记住「不联网检查更新」（{0}），下次打开要重新勾选。", "Couldn't save the \"no online update check\" setting ({0}); you'll need to check it again next time.", "「オンラインで更新を確認しない」の設定を保存できませんでした（{0}）。次回もう一度チェックしてください。", "「온라인 업데이트 확인 안 함」 설정을 저장하지 못했습니다({0}). 다음에 다시 체크해 주세요." },
		new[] { "已打开加载器的 ignore_disable_switch：Steam 被带 Mod 的游戏拉起来过时，从它启动的游戏也照样加载 Mod。", "Turned on the loader's ignore_disable_switch: games started from a Steam that was launched by a modded game will still load the mod.", "ローダーの ignore_disable_switch をオンにしました。MOD 入りのゲームから起動された Steam から始めたゲームでも MOD が読み込まれます。", "로더의 ignore_disable_switch를 켰습니다. Mod가 적용된 게임이 실행한 Steam에서 시작한 게임에서도 Mod가 로드됩니다." },
		new[] { "勾选的 Mod 已移到「BepInEx\\DisabledMods」。", "Checked mods were moved to 「BepInEx\\DisabledMods」.", "チェックした MOD は「BepInEx\\DisabledMods」へ移しました。", "체크한 Mod는 「BepInEx\\DisabledMods」로 옮겼습니다." },
		new[] { "没有可安装的文件：压缩包里没有安装文件，也没能从 GitHub 下载。请把整个压缩包解压后再运行安装器。", "Nothing to install: the zip has no install files and the download from GitHub failed. Extract the whole zip, then run the installer.", "インストールするファイルがありません：ZIP にファイルがなく、GitHub からのダウンロードもできませんでした。ZIP をすべて展開してからインストーラーを実行してください。", "설치할 파일이 없습니다: 압축 파일에 설치 파일이 없고 GitHub에서 내려받지도 못했습니다. 압축 파일 전체를 푼 뒤 설치 프로그램을 실행하세요." },
		new[] { "已复制 {0} 个文件。", "Copied {0} files.", "{0} 個のファイルをコピーしました。", "파일 {0}개를 복사했습니다." },
		new[] { "安装后检查失败：游戏目录里缺少 Mod 文件。", "Post-install check failed: mod files are missing from the game folder.", "インストール後の確認に失敗しました：ゲームフォルダに MOD のファイルがありません。", "설치 후 확인 실패: 게임 폴더에 Mod 파일이 없습니다." },
		new[] { "安装完成，当前版本 {0}。\n\n正常启动游戏即可，游戏里按 F8 打开设置，Tab 打开咖啡伴侣窗口。", "Installed. Current version: {0}.\n\nStart the game as usual. In game, press F8 for settings and Tab for the Coffee Companion window.", "インストールしました。現在のバージョン：{0}。\n\nいつも通りゲームを起動してください。ゲーム中は F8 で設定、Tab で珈琲のおともウィンドウを開きます。", "설치를 마쳤습니다. 현재 버전: {0}.\n\n평소처럼 게임을 실행하세요. 게임에서 F8로 설정, Tab으로 커피 동반자 창을 엽니다." },
		new[] { "写入被拒绝：{0}", "Write denied: {0}", "書き込みが拒否されました：{0}", "쓰기 거부: {0}" },
		new[] { "安装失败：{0}", "Install failed: {0}", "インストールに失敗しました：{0}", "설치 실패: {0}" },
		new[] { "设置开头 Logo 失败（{0}），不影响 Mod 使用。", "Failed to set the opening logos ({0}); the mod still works.", "冒頭ロゴの設定に失敗しました（{0}）。MOD の動作には影響しません。", "시작 로고 설정 실패({0}). Mod 사용에는 지장이 없습니다." },
		new[] { "下载最新版失败（{0}）。", "Failed to download the latest version ({0}).", "最新版のダウンロードに失敗しました（{0}）。", "최신 버전 다운로드 실패({0})." },
		new[] { "没能更新到最新版", "Could not update to the latest version", "最新版に更新できませんでした", "최신 버전으로 업데이트하지 못했습니다" },
		new[] { "没能从 GitHub 下载最新版 {0}（{1}），这次装的是压缩包自带的 {2}。\n\n要更新到 {0}，请到 B站视频简介里的夸克网盘链接或 GitHub 的 Releases 页下载新的压缩包，解压后运行里面的安装器。", "Could not download the latest version {0} from GitHub ({1}), so the version in the zip, {2}, was installed.\n\nTo update to {0}, download the new zip from Nexus Mods or the GitHub Releases page, extract it and run the installer inside.", "GitHub から最新版 {0} をダウンロードできなかったため（{1}）、ZIP に同梱の {2} をインストールしました。\n\n{0} に更新するには、Nexus Mods か GitHub の Releases ページから新しい ZIP をダウンロードし、展開して中のインストーラーを実行してください。", "GitHub에서 최신 버전 {0}을(를) 내려받지 못해({1}) 압축 파일에 든 {2}을(를) 설치했습니다.\n\n{0}(으)로 업데이트하려면 Nexus Mods나 GitHub의 Releases 페이지에서 새 압축 파일을 내려받아 풀고, 안에 있는 설치 프로그램을 실행하세요." },
		new[] { "安装压缩包自带的版本 {0}。", "Installing the version in the zip: {0}.", "ZIP に同梱の版 {0} をインストールします。", "압축 파일에 든 버전 {0}을(를) 설치합니다." },
		new[] { "已装的 {0} 比压缩包自带的 {1} 新，不降级。", "The installed {0} is newer than {1} in the zip; not downgrading.", "インストール済みの {0} は ZIP 同梱の {1} より新しいため、ダウングレードしません。", "설치된 {0}이(가) 압축 파일의 {1}보다 새 버전이라 낮추지 않습니다." },
		new[] { "没能从 GitHub 下载最新版（{0}），压缩包自带的 {1} 比已经装的 {2} 还旧，没有安装，已装的版本保持不变。\n\n请稍后再试，或到 B站视频简介里的夸克网盘链接、GitHub 的 Releases 页面下载最新版的压缩包。", "Couldn't download the latest version from GitHub ({0}), and {1} in the zip is older than the installed {2}, so nothing was installed; your current version is kept.\n\nTry again later, or download the latest zip from the GitHub Releases page.", "GitHub から最新版をダウンロードできず（{0}）、ZIP 同梱の {1} はインストール済みの {2} より古いため、インストールしませんでした。今の版はそのままです。\n\n後でもう一度試すか、GitHub の Releases ページから最新版の ZIP をダウンロードしてください。", "GitHub에서 최신 버전을 내려받지 못했고({0}), 압축 파일의 {1}이(가) 설치된 {2}보다 오래되어 설치하지 않았습니다. 지금 버전은 그대로입니다.\n\n나중에 다시 시도하거나 GitHub Releases 페이지에서 최신 압축 파일을 내려받으세요." },
		new[] { "连不上 GitHub", "can't reach GitHub", "GitHub に接続できません", "GitHub에 연결할 수 없음" },
		new[] { "正在从 GitHub 下载 {0}…", "Downloading {0} from GitHub…", "GitHub から {0} をダウンロード中…", "GitHub에서 {0} 내려받는 중…" },
		new[] { "下载的压缩包里没有 Mod 文件", "The downloaded zip has no mod files", "ダウンロードした ZIP に MOD のファイルがありません", "내려받은 압축 파일에 Mod 파일이 없습니다" },
		new[] { "下载完成。", "Download complete.", "ダウンロード完了。", "다운로드 완료." },
		new[] { "已停用：{0}（移到 {1}）", "Disabled: {0} (moved to {1})", "無効化：{0}（{1} へ移動）", "사용 중지: {0}({1}로 이동)" },
		new[] { "清理了混进来的 BepInEx 5 文件：{0}", "Removed stray BepInEx 5 files: {0}", "紛れ込んだ BepInEx 5 のファイルを削除しました：{0}", "섞여 들어온 BepInEx 5 파일을 정리했습니다: {0}" },
		new[] { "安装文件已经在游戏目录里，跳过复制。", "Install files are already in the game folder; skipping the copy.", "インストールファイルはすでにゲームフォルダにあるため、コピーを省きます。", "설치 파일이 이미 게임 폴더에 있어 복사를 건너뜁니다." },
		new[] { "已去掉只读属性：{0}", "Cleared the read-only flag: {0}", "読み取り専用を解除しました：{0}", "읽기 전용 해제: {0}" },
		new[] { "安装完成", "Installed", "インストール完了", "설치 완료" },
		new[] { "安装失败", "Install failed", "インストール失敗", "설치 실패" },
		new[] { "没有写入游戏目录的权限（安装器已经是以管理员身份运行）。", "No permission to write to the game folder (the installer is already running as administrator).", "ゲームフォルダへの書き込み権限がありません（インストーラーはすでに管理者として実行中）。", "게임 폴더에 쓸 권한이 없습니다(설치 프로그램은 이미 관리자 권한으로 실행 중)." },
		new[] { "没有写入游戏目录的权限（安装器不是以管理员身份运行）。", "No permission to write to the game folder (the installer is not running as administrator).", "ゲームフォルダへの書き込み権限がありません（インストーラーは管理者として実行されていません）。", "게임 폴더에 쓸 권한이 없습니다(설치 프로그램이 관리자 권한으로 실행되지 않음)." },
		new[] { "已经以管理员身份运行，还是写不进游戏目录。\n\n多半是杀毒软件拦住了：先退出 360、火绒、电脑管家等杀毒软件（或把游戏目录加进信任），再点一次「一键安装 / 更新」。\n\n还不行的话，点「导出诊断」把桌面上的压缩包发到 QQ 群。",
			"Already running as administrator, but still can't write to the game folder.\n\nMost likely your antivirus is blocking it: quit it (or add the game folder to its trusted list), then click 「Install / Update」 again.\n\nIf that doesn't help, click 「Export diagnostics」 and attach the zip on your desktop to a GitHub issue.",
			"管理者として実行していますが、ゲームフォルダに書き込めません。\n\nおそらくウイルス対策ソフトがブロックしています。いったん終了するか、ゲームフォルダを信頼済みに追加してから、もう一度「インストール／更新」を押してください。\n\nそれでもだめなら「診断を書き出す」を押し、デスクトップの ZIP を GitHub の Issues に添付してください。",
			"관리자 권한으로 실행했는데도 게임 폴더에 쓸 수 없습니다.\n\n대개 백신 프로그램이 막고 있습니다. 백신을 잠시 끄거나 게임 폴더를 신뢰 목록에 추가한 뒤 「설치 / 업데이트」를 다시 누르세요.\n\n그래도 안 되면 「진단 내보내기」를 눌러 바탕 화면의 압축 파일을 GitHub Issues에 첨부해 주세요." },
		new[] { "没有写入游戏目录的权限（游戏可能装在 Program Files 里，或者被杀毒软件拦住了）。\n\n请关掉安装器，右键「咖啡伴侣安装器 Installer.exe」→「以管理员身份运行」，再点「一键安装 / 更新」。\n\n还不行的话，先退出 360、火绒、电脑管家等杀毒软件（或把游戏目录加进信任）再试。",
			"No permission to write to the game folder (the game may be in Program Files, or your antivirus is blocking it).\n\nClose the installer, right-click 「咖啡伴侣安装器 Installer.exe」 → 「Run as administrator」, then click 「Install / Update」.\n\nIf that still fails, quit your antivirus (or add the game folder to its trusted list) and try again.",
			"ゲームフォルダへの書き込み権限がありません（ゲームが Program Files にあるか、ウイルス対策ソフトがブロックしています）。\n\nインストーラーを閉じ、「咖啡伴侣安装器 Installer.exe」を右クリック →「管理者として実行」してから「インストール／更新」を押してください。\n\nそれでもだめなら、ウイルス対策ソフトを終了するか、ゲームフォルダを信頼済みに追加してから再度お試しください。",
			"게임 폴더에 쓸 권한이 없습니다(게임이 Program Files에 있거나 백신 프로그램이 막고 있습니다).\n\n설치 프로그램을 닫고 「咖啡伴侣安装器 Installer.exe」를 우클릭 → 「관리자 권한으로 실행」한 뒤 「설치 / 업데이트」를 누르세요.\n\n그래도 안 되면 백신을 잠시 끄거나 게임 폴더를 신뢰 목록에 추가한 뒤 다시 시도하세요." },
		new[] { "游戏目录的路径里有中文或其他非英文字符，BepInEx 会加载失败，Mod 不会生效。请把整个游戏文件夹移到只有英文、数字的路径下（比如 D:\\Games\\LegendOfMortal；Steam 版在库里右键活侠传 → 属性 → 已安装文件 → 移动安装文件夹），再运行安装器。",
			"The game folder path contains Chinese or other non-English characters, so BepInEx fails to load and the mod won't work. Move the whole game folder to a path with only English letters and numbers (for example D:\\Games\\LegendOfMortal; for the Steam version, right-click the game in your library → Properties → Installed Files → Move install folder), then run the installer again.",
			"ゲームフォルダのパスに中国語などの英語以外の文字が含まれているため、BepInEx の読み込みに失敗し、MOD が動作しません。ゲームフォルダ全体を英数字だけのパス（例：D:\\Games\\LegendOfMortal。Steam 版はライブラリで右クリック → プロパティ → インストール済みファイル → インストールフォルダを移動）に移してから、インストーラーを実行し直してください。",
			"게임 폴더 경로에 중국어 등 영어가 아닌 문자가 있어 BepInEx 로딩에 실패하고 Mod가 작동하지 않습니다. 게임 폴더 전체를 영문·숫자만 있는 경로(예: D:\\Games\\LegendOfMortal, Steam 버전은 라이브러리에서 우클릭 → 속성 → 설치된 파일 → 설치 폴더 이동)로 옮긴 뒤 설치 프로그램을 다시 실행하세요." },
		new[] { "仍然要安装吗？", "Install anyway?", "それでもインストールしますか？", "그래도 설치할까요?" },
		new[] { "没有写入权限", "No write permission", "書き込み権限なし", "쓰기 권한 없음" },
		// 开头 Logo（SplashPatch）
		new[] { "没有找到游戏设置文件，没有改动开头 Logo。", "Game settings file not found; the opening logos were left as they are.", "ゲームの設定ファイルが見つからないため、冒頭ロゴは変更していません。", "게임 설정 파일을 찾지 못해 시작 로고를 바꾸지 않았습니다." },
		new[] { "游戏设置文件的格式和预期不同（可能游戏更新了），没有改动开头 Logo。", "The game settings file isn't in the expected format (the game may have updated); the opening logos were left as they are.", "ゲームの設定ファイルの形式が想定と違うため（ゲームが更新された可能性）、冒頭ロゴは変更していません。", "게임 설정 파일 형식이 예상과 달라(게임이 업데이트됐을 수 있음) 시작 로고를 바꾸지 않았습니다." },
		new[] { "开头 Logo 已经是跳过的。", "The opening logos are already skipped.", "冒頭ロゴはすでに飛ばす設定です。", "시작 로고는 이미 건너뛰도록 되어 있습니다." },
		new[] { "开头 Logo 保持原样。", "The opening logos are left as they are.", "冒頭ロゴはそのままです。", "시작 로고는 그대로입니다." },
		new[] { "已设置跳过开头 Logo（改了游戏设置文件里的一个开关，原文件备份为 globalgamemanagers.lom-original）。", "The opening logos will be skipped (one switch in the game settings file was changed; the original is backed up as globalgamemanagers.lom-original).", "冒頭ロゴを飛ばすよう設定しました（ゲーム設定ファイルのスイッチを 1 つ変更。元のファイルは globalgamemanagers.lom-original に保存）。", "시작 로고를 건너뛰도록 설정했습니다(게임 설정 파일의 스위치 하나를 바꿈, 원본은 globalgamemanagers.lom-original로 백업)." },
		new[] { "已恢复开头 Logo。", "The opening logos have been restored.", "冒頭ロゴを元に戻しました。", "시작 로고를 되돌렸습니다." },
	});

	private static Dictionary<string, string[]> Build(string[][] rows)
	{
		var table = new Dictionary<string, string[]>();
		foreach (string[] row in rows)
		{
			table[row[0]] = new[] { row[1], row[2], row[3] };
		}
		return table;
	}
}
