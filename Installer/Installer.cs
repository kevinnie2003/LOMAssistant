using System;
using static InstallerText;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LOMAssistantInstaller;

/// <summary>
/// 咖啡伴侣一键安装器。放在发布压缩包根目录，和 BepInEx\、winhttp.dll 等安装文件放在一起。
/// 1. 自动从 Steam 库里找游戏目录（找不到可手动选）。
/// 2. 列出 BepInEx\plugins 里的其他 Mod，已知会冲突的默认勾选，安装时移到「DisabledMods」文件夹（不删除）。
/// 3. 先从 GitHub 下载最新版安装；连不上或下载失败，就安装压缩包里自带的这一版。
/// 4. 不覆盖 BepInEx\config 里的配置。
/// </summary>
internal static class Program
{
	[STAThread]
	private static void Main(string[] args)
	{
		Application.EnableVisualStyles();
		Application.SetCompatibleTextRenderingDefault(false);
		string game = null;
		string autoLog = null;
		for (int i = 0; i < args.Length - 1; i++)
		{
			if (args[i] == "--game")
			{
				game = args[i + 1];
			}
			else if (args[i] == "--diag")
			{
				InstallerForm.DiagLogArg = args[i + 1]; // 测试用：导出诊断后把路径写到这个文件并退出
			}
			else if (args[i] == "--auto")
			{
				autoLog = args[i + 1]; // 测试用：自动安装，把日志写到这个文件后退出
			}
			else if (args[i] == "--shot")
			{
				InstallerForm.ShotArg = args[i + 1]; // 测试用：等联网检查完，把窗口画成图片存到这个文件后退出
			}
			else if (args[i] == "--size")
			{
				string[] wh = args[i + 1].Split('x');
				InstallerForm.ShotSize = new Size(int.Parse(wh[0]), int.Parse(wh[1])); // 测试用：窗口按这个大小开，看小屏幕下的样子
			}
		}
		Application.Run(new InstallerForm(game, autoLog));
	}
}

internal class InstallerForm : Form
{
	private const string Repo = "kevinnie2003/LOMAssistant";
	private const string ModDll = @"BepInEx\plugins\LOMAssistant\LOMAssistant.dll";
	private const string DisabledFolder = @"BepInEx\DisabledMods";
	// 2.8.2 及以前停用的 Mod 放在这里（中文文件夹名），不去搬动
	private const string OldDisabledFolder = @"BepInEx\已停用的Mod";

	// 文件名包含这些字样的插件，已知和本 Mod 改的是同一批游戏代码
	private static readonly string[][] KnownConflicts =
	{
		new[] { "ScriptTrainer", "多功能 Mod（Jim97）" },
		new[] { "ControlMyDestiny", "知天命" },
		new[] { "ZhaoHuoSystem", "赵活系统" },
		new[] { "DiceMaster", "骰子大师" },
		new[] { "MortalAnytimeSave", "随时存档独立版，已内置" },
		new[] { "骰子", "骰子大师" },
		new[] { "LOMAssistant", "活侠传助手 / 本 Mod 的旧版" },
		new[] { "活侠传助手", "活侠传助手" },
	};

	// 配色取自游戏界面：墨色标题栏、米色底、朱红按钮
	private static readonly Color Ink = Color.FromArgb(43, 38, 34);
	private static readonly Color Paper = Color.FromArgb(246, 242, 234);
	private static readonly Color Card = Color.FromArgb(255, 253, 249);
	private static readonly Color Accent = Color.FromArgb(140, 47, 38);
	private static readonly Color AccentHover = Color.FromArgb(166, 60, 48);
	private static readonly Color Muted = Color.FromArgb(120, 110, 98);

	private readonly string _payload = AppDomain.CurrentDomain.BaseDirectory;
	// 压缩包里随 Mod 带的更新内容（tools/pack.py 放进去）。安装器自己不带版本号、不带更新内容，
	// 这样每次发版的安装器一个字节都不变，杀毒软件和 SmartScreen 按文件记的信誉才能一直累积
	private const string PayloadChangelog = @"BepInEx\plugins\LOMAssistant\CHANGELOG";
	private readonly TextBox _gameBox = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
	private readonly Label _versionFrom = new Label { AutoSize = true, Font = new Font(FontName, 18f, FontStyle.Bold), ForeColor = Color.FromArgb(120, 110, 98), Margin = new Padding(0) };
	private readonly Label _versionTo = new Label { AutoSize = true, Font = new Font(FontName, 18f, FontStyle.Bold), ForeColor = Color.FromArgb(140, 47, 38), Margin = new Padding(0) };
	private readonly Label _versionNote = new Label { AutoSize = true, ForeColor = Color.FromArgb(120, 110, 98), Margin = new Padding(0, 2, 0, 0) };
	private readonly RichTextBox _notes = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, DetectUrls = false, ScrollBars = RichTextBoxScrollBars.Vertical };
	private readonly Button _tabNotes = new Button { Text = L("更新内容"), AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(0, 0, 4, 0), Padding = new Padding(8, 2, 8, 2) };
	private readonly Button _tabLog = new Button { Text = L("日志"), AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(0), Padding = new Padding(8, 2, 8, 2) };
	private string _changelog = ReadPayload(PayloadChangelog + ".md") ?? "";
	// 更新内容的译文（英、日、韩），没翻的版本照旧显示中文，见 InstallerText.Changelog
	private string _changelogTranslated = ChangelogSuffix != null ? ReadPayload(PayloadChangelog + "." + ChangelogSuffix + ".md") : null;
	// 游戏目录路径里有中文等非英文字符时的提醒：BepInEx 预加载会失败（IL Compile Error），Mod 不生效
	private readonly Label _pathWarning = new Label { AutoSize = true, ForeColor = Color.FromArgb(178, 43, 33), MaximumSize = new Size(560, 0), Margin = new Padding(0, 4, 0, 4), Visible = false };
	private readonly Label _pluginsHint = new Label { AutoSize = true, ForeColor = Color.FromArgb(120, 110, 98), Text = L("勾选的移到「DisabledMods」，不删除") };
	private readonly Label _pluginsNone = new Label { AutoSize = true, Text = L("没有发现其他 Mod。"), ForeColor = Color.FromArgb(120, 110, 98) };
	private readonly CheckedListBox _plugins = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false, HorizontalScrollbar = true, BorderStyle = BorderStyle.FixedSingle, MinimumSize = new Size(0, 110) };
	private readonly TextBox _log = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None };
	private readonly CheckBox _skipLogo = new CheckBox { Text = L("跳过游戏开头 Logo（想恢复就取消勾选再安装一次）"), AutoSize = true, Checked = true, Margin = new Padding(0, 4, 0, 6) };
	// 不联网：不去 GitHub 查新版，只装压缩包自带的（想留在旧版的玩家用，比已装的旧也照装）；记在注册表里，下次打开还是这样
	private readonly CheckBox _offline = new CheckBox { Text = L("不联网检查更新，只装压缩包自带的版本"), AutoSize = true, Checked = OfflineSaved, Margin = new Padding(0, 0, 0, 6) };
	private const string SettingsKey = @"Software\LOMAssistant\Installer";
	private readonly Button _install = new Button { Text = L("一键安装 / 更新"), Dock = DockStyle.Fill, Height = 46, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
	private readonly Button _uninstall = new Button { Text = L("卸载本 Mod"), Dock = DockStyle.Fill, Height = 40, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
	private readonly Button _export = new Button { Text = L("导出诊断（Mod 不生效时用，打包日志发给作者）"), Dock = DockStyle.Fill, Height = 40, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
	private readonly Button _browse = new Button { Text = L("选择…"), AutoSize = true, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
	private readonly List<string> _pluginPaths = new List<string>();

	private string _game;
	private Version _latest;
	private string _latestUrl;
	private bool _onlineDone;

	private readonly string _autoLog;
	internal static string DiagLogArg;
	internal static string ShotArg;
	internal static Size? ShotSize;

	[System.Runtime.InteropServices.DllImport("user32.dll")]
	private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
	private readonly string _diagLog = DiagLogArg;

	public InstallerForm(string game, string autoLog)
	{
		_autoLog = autoLog;
		Text = L("咖啡伴侣 安装器");
		Font = new Font(FontName, 10f);
		AutoScaleMode = AutoScaleMode.Dpi;
		ClientSize = new Size(1280, 720);
		MinimumSize = new Size(900, 600);
		StartPosition = FormStartPosition.CenterScreen;
		BackColor = Paper;

		// 左栏：游戏目录、版本、其他 Mod、安装按钮
		var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, BackColor = Paper, Margin = new Padding(0, 0, 14, 0) };
		left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		var dirRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
		dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		dirRow.Controls.Add(_gameBox, 0, 0);
		dirRow.Controls.Add(_browse, 1, 0);
		var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
		buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
		_export.Text = L("导出诊断");
		_export.Margin = new Padding(0, 0, 6, 0);
		_uninstall.Margin = new Padding(6, 0, 0, 0);
		buttons.Controls.Add(_export, 0, 0);
		buttons.Controls.Add(_uninstall, 1, 0);
		Control[] rows =
		{
			Heading(L("游戏目录")),
			dirRow,
			_pathWarning,
			VersionCard(),
			Heading(L("其他 Mod")),
			_pluginsHint,
			_pluginsNone,
			_plugins, // 占满剩下的高度，窗口拉高列表跟着变高，并把下面的压到底部
			_skipLogo,
			_offline,
			_install,
			buttons,
		};
		left.RowCount = rows.Length;
		for (int i = 0; i < rows.Length; i++)
		{
			left.RowStyles.Add(rows[i] == _plugins ? new RowStyle(SizeType.Percent, 100f) : new RowStyle(SizeType.AutoSize));
			left.Controls.Add(rows[i], 0, i);
		}

		// 右栏：「更新内容」「日志」两页
		var tabs = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0, 6, 0, 0) };
		tabs.Controls.Add(_tabNotes);
		tabs.Controls.Add(_tabLog);
		var card = new Panel { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(14, 10, 8, 10) };
		card.Controls.Add(_notes);
		card.Controls.Add(_log);
		var right = new Panel { Dock = DockStyle.Fill, BackColor = Paper, Margin = new Padding(0) };
		right.Controls.Add(card);
		right.Controls.Add(tabs);
		_tabNotes.Click += delegate { ShowTab(true); };
		_tabLog.Click += delegate { ShowTab(false); };

		var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20, 10, 20, 18), ColumnCount = 2, RowCount = 1, BackColor = Paper };
		body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 600f));
		body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
		body.Controls.Add(left, 0, 0);
		body.Controls.Add(right, 1, 0);

		_log.BackColor = Card;
		_log.ForeColor = Color.FromArgb(70, 62, 55);
		_notes.BackColor = Card;
		_notes.ForeColor = Color.FromArgb(55, 48, 42);
		_gameBox.BackColor = Card;
		_plugins.BackColor = Card;
		_install.BackColor = Accent;
		_install.ForeColor = Color.White;
		_install.Font = new Font(FontName, 13f, FontStyle.Bold);
		_install.Height = 52;
		_install.FlatAppearance.BorderSize = 0;
		_install.FlatAppearance.MouseOverBackColor = AccentHover;
		_install.FlatAppearance.MouseDownBackColor = Ink;
		foreach (Button b in new[] { _export, _uninstall, _browse })
		{
			b.BackColor = Card;
			b.ForeColor = Ink;
			b.FlatAppearance.BorderColor = Color.FromArgb(200, 190, 175);
		}
		_browse.Margin = new Padding(8, 0, 0, 0);
		foreach (Button b in new[] { _tabNotes, _tabLog })
		{
			b.FlatAppearance.BorderSize = 0;
			b.FlatAppearance.MouseOverBackColor = Color.FromArgb(236, 229, 216);
		}
		ShowTab(true);

		Controls.Add(body);

		_gameBox.TabStop = false;
		ActiveControl = _install;
		_browse.Click += delegate { BrowseGame(); };
		_install.Click += delegate { StartInstall(); };
		_export.Click += delegate { ExportDiagnostics(); };
		_uninstall.Click += delegate { Uninstall(); };

		Log(L("咖啡伴侣 安装器"));
		SetGame(game ?? FindGame());
		if (_game == null)
		{
			Log(L("没有自动找到游戏目录，请点「选择…」手动选择游戏目录（里面有 Mortal.exe）。"));
		}
		_noUpdate = _offline.Checked;
		if (_noUpdate)
		{
			Log(L("已关闭联网检查更新，不连 GitHub。"));
			_onlineDone = true;
			UpdateVersions();
		}
		else
		{
			CheckOnline();
		}
		_offline.CheckedChanged += delegate
		{
			SaveOffline(_offline.Checked);
			// 关掉不联网时还没查过就现在查
			if (!_offline.Checked && !_checkedOnline)
			{
				_onlineDone = false;
				CheckOnline();
			}
			UpdateVersions();
		};
		// 小屏幕（1366x768 笔记本）放不下就缩到屏幕工作区里，不让下面的按钮跑到任务栏底下
		Load += delegate
		{
			Rectangle area = Screen.FromControl(this).WorkingArea;
			if (Width > area.Width || Height > area.Height)
			{
				MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
				Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
				Location = area.Location + new Size((area.Width - Width) / 2, (area.Height - Height) / 2);
			}
			if (ShotSize != null)
			{
				MinimumSize = new Size(0, 0);
				Size = ShotSize.Value;
			}
		};
		if (_autoLog != null)
		{
			Shown += delegate { StartInstall(); };
		}
		else if (_diagLog != null)
		{
			Shown += delegate { ExportDiagnostics(); };
		}
		else if (ShotArg != null)
		{
			var timer = new System.Windows.Forms.Timer { Interval = 500 };
			int waited = 0;
			timer.Tick += delegate
			{
				waited += 500;
				if (!_onlineDone && waited < 12000)
				{
					return;
				}
				timer.Stop();
				// PrintWindow 才画得出富文本框（DrawToBitmap 画出来是空的）
				using (var bitmap = new Bitmap(Width, Height))
				{
					using (Graphics g = Graphics.FromImage(bitmap))
					{
						IntPtr hdc = g.GetHdc();
						PrintWindow(Handle, hdc, 2);
						g.ReleaseHdc(hdc);
					}
					bitmap.Save(ShotArg, System.Drawing.Imaging.ImageFormat.Png);
				}
				Close();
			};
			Shown += delegate { timer.Start(); };
		}
	}

	/// <summary>读安装器旁边（解压出来的压缩包里）的文本文件。单独下载的安装器旁边没有，返回 null，等联网读 GitHub 上的。</summary>
	private static string ReadPayload(string relative)
	{
		try
		{
			string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relative);
			return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n") : null;
		}
		catch
		{
			return null;
		}
	}

	// 版本对比：已安装 → 将安装，大字
	private Control VersionCard()
	{
		var card = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, BackColor = Card, Padding = new Padding(14, 8, 14, 8), Margin = new Padding(0, 10, 0, 2) };
		card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		card.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
		card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
		var fromCaption = new Label { Text = L("已安装"), AutoSize = true, ForeColor = Muted, Margin = new Padding(0) };
		var toCaption = new Label { Text = L("将安装"), AutoSize = true, ForeColor = Muted, Margin = new Padding(0) };
		var arrow = new Label { Text = "→", AutoSize = true, ForeColor = Muted, Font = new Font(FontName, 16f), Margin = new Padding(14, 10, 14, 0) };
		card.Controls.Add(fromCaption, 0, 0);
		card.Controls.Add(toCaption, 2, 0);
		card.Controls.Add(_versionFrom, 0, 1);
		card.Controls.Add(arrow, 1, 1);
		card.Controls.Add(_versionTo, 2, 1);
		card.Controls.Add(_versionNote, 0, 2);
		card.SetColumnSpan(_versionNote, 3);
		return card;
	}

	private void ShowTab(bool notes)
	{
		_notes.Visible = notes;
		_log.Visible = !notes;
		_tabNotes.BackColor = notes ? Card : Paper;
		_tabLog.BackColor = notes ? Paper : Card;
		_tabNotes.ForeColor = notes ? Accent : Muted;
		_tabLog.ForeColor = notes ? Muted : Accent;
		_tabNotes.Font = new Font(Font, notes ? FontStyle.Bold : FontStyle.Regular);
		_tabLog.Font = new Font(Font, notes ? FontStyle.Regular : FontStyle.Bold);
	}

	// ---------------- 更新内容 ----------------

	private static readonly Regex VersionHeading = new Regex(@"^## ([0-9.]+)(.*)$");

	/// <summary>CHANGELOG 里比已安装版本新、不超过将安装版本的那几段；已是最新（或没装）就只放将安装的那一版。</summary>
	private void RenderNotes(Version installed, Version target)
	{
		var sections = new List<(Version Version, string Title, List<string> Lines)>();
		foreach (string line in Changelog(_changelog, _changelogTranslated).Split('\n'))
		{
			Match m = VersionHeading.Match(line);
			if (m.Success)
			{
				Version v;
				if (Version.TryParse(m.Groups[1].Value, out v))
				{
					sections.Add((v, m.Groups[1].Value + m.Groups[2].Value, new List<string>()));
				}
				continue;
			}
			if (sections.Count > 0)
			{
				sections[sections.Count - 1].Lines.Add(line);
			}
		}
		var shown = sections.Where(x => (target == null || x.Version <= target) && installed != null && x.Version > installed && Short(x.Version) != Short(installed)).ToList();
		string heading;
		if (shown.Count > 0)
		{
			heading = F("从 {0} 更新到 {1} 的新内容", Short(installed), Short(shown[0].Version));
		}
		else
		{
			shown = sections.Where(x => target == null || x.Version <= target).Take(1).ToList();
			heading = shown.Count > 0 ? F("{0} 的更新内容", Short(shown[0].Version)) : L("更新内容");
		}
		_notes.Clear();
		Append(heading + "\n", new Font(FontName, 13f, FontStyle.Bold), Ink);
		if (shown.Count == 0)
		{
			Append("\n" + L("没有找到更新说明。"), Font, Muted);
			return;
		}
		foreach (var section in shown)
		{
			Append("\n" + section.Title + "\n", new Font(FontName, 11.5f, FontStyle.Bold), Accent);
			foreach (string raw in section.Lines)
			{
				string line = raw.TrimEnd();
				if (line.Length == 0)
				{
					continue;
				}
				string indent = "";
				if (line.StartsWith("  - ") || line.StartsWith("    - "))
				{
					indent = "      ◦ ";
					line = line.TrimStart().Substring(2);
				}
				else if (line.StartsWith("- "))
				{
					indent = "  • ";
					line = line.Substring(2);
				}
				Append(indent, Font, Muted);
				AppendInline(line);
				Append("\n", Font, Ink);
			}
		}
		_notes.SelectionStart = 0;
		_notes.ScrollToCaret();
	}

	// 行内的 **粗体**、[文字](链接)、`代码` 去掉标记
	private void AppendInline(string line)
	{
		line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1");
		line = line.Replace("`", "");
		string[] parts = line.Split(new[] { "**" }, StringSplitOptions.None);
		for (int i = 0; i < parts.Length; i++)
		{
			Append(parts[i], i % 2 == 1 ? new Font(Font, FontStyle.Bold) : Font, Color.FromArgb(55, 48, 42));
		}
	}

	// 英语界面的字体（Segoe UI）没有汉字：夹在英文里的中文（「原汁原味」这类）RichTextBox 会自己换一种字体，
	// 连带后面的英文也变成那种字体。中文、全角符号单独拿出来，用微软雅黑写
	private static readonly Regex CjkRun = new Regex(@"([⺀-鿿豈-﫿＀-￯　-〿]+)");

	private void Append(string text, Font font, Color color)
	{
		if (InstallerText.Current == InstallerText.Lang.English && CjkRun.IsMatch(text))
		{
			foreach (string part in CjkRun.Split(text))
			{
				if (part.Length > 0)
				{
					AppendRun(part, CjkRun.IsMatch(part) ? new Font("Microsoft YaHei UI", font.Size, font.Style) : font, color);
				}
			}
			return;
		}
		AppendRun(text, font, color);
	}

	private void AppendRun(string text, Font font, Color color)
	{
		_notes.SelectionStart = _notes.TextLength;
		_notes.SelectionLength = 0;
		_notes.SelectionFont = font;
		_notes.SelectionColor = color;
		_notes.AppendText(text);
	}

	private Label Heading(string text)
	{
		return new Label { Text = text, AutoSize = true, ForeColor = Ink, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 8, 0, 4) };
	}

	// 一行写明：已安装哪版，这次会装哪版、从哪来
	private void UpdateVersions()
	{
		Version installed = _game != null ? DllVersion(Path.Combine(_game, ModDll)) : null;
		Version bundled = BundledVersion();
		Version target = null;
		string note;
		if (_noUpdate)
		{
			target = bundled;
			note = bundled != null ? L("已关闭联网检查，安装压缩包自带的版本") : L("已关闭联网检查，这个安装器也不在解压后的压缩包里");
		}
		else if (!_onlineDone)
		{
			target = bundled;
			note = L("正在检查 GitHub 上的最新版…");
		}
		else if (_latest != null && (bundled == null || _latest > bundled && Short(_latest) != Short(bundled)))
		{
			target = _latest;
			note = L("将从 GitHub 下载最新版");
		}
		else if (bundled != null)
		{
			target = bundled;
			note = _latest != null ? L("压缩包里的就是最新版") : L("GitHub 连不上，安装压缩包自带的版本");
		}
		else
		{
			note = L("连不上 GitHub，这个安装器也不在解压后的压缩包里");
		}
		if (installed != null && target != null && Short(installed) == Short(target))
		{
			note = _noUpdate ? L("已经装的就是压缩包里的版本") : L("已经是最新版");
		}
		else if (installed != null && target != null && installed > target)
		{
			note = _noUpdate ? L("会换成压缩包自带的旧版本") : L("已经是最新版");
		}
		_versionFrom.Text = _game == null ? "-" : installed != null ? Short(installed) : L("未安装");
		_versionTo.Text = target != null ? Short(target) : "-";
		_versionNote.Text = note;
		RenderNotes(installed, target);
	}

	private void Log(string message)
	{
		if (InvokeRequired)
		{
			BeginInvoke(new Action<string>(Log), message);
			return;
		}
		_log.AppendText(message + Environment.NewLine);
	}

	private static string Short(Version v)
	{
		return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
	}

	private Version BundledVersion()
	{
		return DllVersion(Path.Combine(_payload, ModDll));
	}

	private static Version DllVersion(string path)
	{
		try
		{
			return File.Exists(path) ? AssemblyName.GetAssemblyName(path).Version : null;
		}
		catch
		{
			return null;
		}
	}

	// ---------------- 游戏目录 ----------------

	private static bool IsGameDir(string dir)
	{
		return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "Mortal.exe"));
	}

	private string FindGame()
	{
		if (IsGameDir(_payload))
		{
			return _payload.TrimEnd('\\');
		}
		var steamRoots = new List<string>();
		foreach (string value in new[]
		{
			RegistryValue(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"),
			RegistryValue(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
			RegistryValue(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"),
		})
		{
			if (!string.IsNullOrEmpty(value))
			{
				steamRoots.Add(value.Replace('/', '\\'));
			}
		}
		var libraries = new List<string>(steamRoots);
		foreach (string root in steamRoots)
		{
			string vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
			try
			{
				if (File.Exists(vdf))
				{
					foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
					{
						libraries.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
					}
				}
			}
			catch
			{
			}
		}
		foreach (string lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			string dir = Path.Combine(lib, @"steamapps\common\LegendOfMortal");
			if (IsGameDir(dir))
			{
				return dir;
			}
		}
		return null;
	}

	private static string RegistryValue(RegistryKey hive, string key, string name)
	{
		try
		{
			using (RegistryKey k = hive.OpenSubKey(key))
			{
				return k != null ? k.GetValue(name) as string : null;
			}
		}
		catch
		{
			return null;
		}
	}

	private void BrowseGame()
	{
		using (var dialog = new FolderBrowserDialog { Description = L("选择《活侠传》游戏目录（里面有 Mortal.exe）") })
		{
			if (dialog.ShowDialog(this) != DialogResult.OK)
			{
				return;
			}
			if (!IsGameDir(dialog.SelectedPath))
			{
				MessageBox.Show(this, L("这个文件夹里没有 Mortal.exe，不是游戏目录。\n\nSteam 中右键《活侠传》→ 管理 → 浏览本地文件，打开的就是游戏目录。"), L("选错了"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}
			SetGame(dialog.SelectedPath);
		}
	}

	// Steam 在注册表里存的路径是全小写（d:/steam），这里换成磁盘上的实际大小写，只为显示好看
	private static string RealCase(string path)
	{
		// 从盘符开始逐级往下找实际名字（不能往上递归：「C:」会被当成 C 盘的当前目录，导致无限递归）
		try
		{
			string full = Path.GetFullPath(path);
			string root = Path.GetPathRoot(full);
			string current = root.ToUpperInvariant();
			foreach (string part in full.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
			{
				string[] match = Directory.GetDirectories(current, part);
				current = Path.Combine(current, match.Length > 0 ? Path.GetFileName(match[0]) : part);
			}
			return current;
		}
		catch
		{
			return path;
		}
	}

	private void SetGame(string dir)
	{
		_game = IsGameDir(dir) ? RealCase(dir) : null;
		_gameBox.Text = _game ?? L("（未找到，请点「选择…」）");
		UpdateVersions();
		RefreshPlugins();
		_pathWarning.Text = L("游戏目录的路径里有中文或其他非英文字符，BepInEx 会加载失败，Mod 不会生效。请把整个游戏文件夹移到只有英文、数字的路径下（比如 D:\\Games\\LegendOfMortal；Steam 版在库里右键活侠传 → 属性 → 已安装文件 → 移动安装文件夹），再运行安装器。");
		_pathWarning.Visible = NonAsciiPath(_game);
		if (_game != null)
		{
			Log(F("游戏目录：{0}", _game));
			if (_pathWarning.Visible)
			{
				Log(_pathWarning.Text);
			}
		}
	}

	private void RefreshPlugins()
	{
		_plugins.Items.Clear();
		_pluginPaths.Clear();
		string plugins = _game != null ? Path.Combine(_game, @"BepInEx\plugins") : null;
		if (plugins != null && Directory.Exists(plugins))
		{
			CollectPlugins(plugins);
		}
		string patchers = _game != null ? Path.Combine(_game, @"BepInEx\patchers") : null;
		if (patchers != null && Directory.Exists(patchers))
		{
			CollectPatchers(patchers);
		}
		_plugins.Visible = _pluginPaths.Count > 0;
		_pluginsNone.Visible = _pluginPaths.Count == 0;
		_pluginsHint.Visible = _pluginPaths.Count > 0; // 没发现别的 Mod 时这句用不着，省一行
	}

	private void CollectPlugins(string plugins)
	{
		string ours = Path.Combine(plugins, "LOMAssistant") + "\\";
		foreach (string dll in Directory.GetFiles(plugins, "*.dll", SearchOption.AllDirectories))
		{
			if (dll.StartsWith(ours, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			string rel = dll.Substring(plugins.Length + 1);
			string known = null;
			foreach (string[] pair in KnownConflicts)
			{
				if (rel.IndexOf(pair[0], StringComparison.OrdinalIgnoreCase) >= 0)
				{
					known = pair[1];
					break;
				}
			}
			// 「DiceMaster\DiceMaster.dll」这种文件夹和文件同名的只显示一次，省地方给冲突说明
			string name = Path.GetDirectoryName(rel);
			if (string.IsNullOrEmpty(name) || !string.Equals(Path.GetFileName(name), Path.GetFileNameWithoutExtension(rel), StringComparison.OrdinalIgnoreCase))
			{
				name = rel;
			}
			_pluginPaths.Add(dll);
			_plugins.Items.Add(known != null ? F("{0} （已知冲突：{1}）", name, L(known)) : name, known != null);
		}
	}

	// 本 Mod 不用预加载补丁，patchers 里的 DLL 都是别的 Mod 留下的。有它们时 BepInEx 预加载阶段会去加载，
	// 在部分电脑上直接失败（Could not run preloader），整个 BepInEx 都起不来，所以默认停用
	private void CollectPatchers(string patchers)
	{
		foreach (string dll in Directory.GetFiles(patchers, "*.dll", SearchOption.AllDirectories))
		{
			_pluginPaths.Add(dll);
			_plugins.Items.Add(F("{0}　【预加载补丁：本 Mod 不需要，可能导致 BepInEx 启动失败】", @"patchers\" + dll.Substring(patchers.Length + 1)), true);
		}
	}

	// ---------------- 卸载 ----------------

	// 删掉本 Mod 和它的配置，恢复开头 Logo。还有别的 Mod 就把 BepInEx 加载器留给它们；没有了就连加载器一起删，
	// 游戏目录回到没装 Mod 的样子。「DisabledMods」、别的 Mod 的配置和存档（在游戏存档文件夹里，含随时存档、剧情回顾收录）都不动
	private void Uninstall()
	{
		if (_game == null)
		{
			MessageBox.Show(this, L("请先选择游戏目录。"), L("卸载"), MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}
		if (Process.GetProcessesByName("Mortal").Length > 0)
		{
			MessageBox.Show(this, L("游戏正在运行，请先退出游戏再卸载。"), L("卸载"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}
		RefreshPlugins();
		int others = _pluginPaths.Count;
		string question = L("确定卸载咖啡伴侣吗？") + "\n\n"
			+ (others > 0
				? F("还有 {0} 个别的 Mod，BepInEx 加载器会留给它们用，只删本 Mod。", others)
				: L("没有别的 Mod 了，BepInEx 加载器也一起删掉，游戏回到没装 Mod 的样子。"))
			+ "\n\n" + L("存档不受影响（随时存档、剧情回顾收录的节点也留着，以后重装还能接着用）。");
		if (MessageBox.Show(this, question, L("卸载"), MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
		{
			return;
		}
		try
		{
			try
			{
				Log(SplashPatch.Apply(_game, false));
			}
			catch (Exception e)
			{
				Log(F("恢复开头 Logo 失败（{0}）。", e.Message));
			}
			DeletePath(@"BepInEx\plugins\LOMAssistant");
			DeletePath(@"BepInEx\config\LOMAssistant.cfg");
			if (others == 0)
			{
				foreach (string rel in new[] { "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt", @"BepInEx\core", @"BepInEx\cache", @"BepInEx\LogOutput.log", @"BepInEx\ErrorLog.log" })
				{
					DeletePath(rel);
				}
				// 剩下的文件夹里没有文件了就删掉（「DisabledMods」、别的 Mod 的配置还在的话会留下）
				foreach (string rel in new[] { @"BepInEx\plugins", @"BepInEx\patchers", @"BepInEx\config", "BepInEx" })
				{
					string dir = Path.Combine(_game, rel);
					if (Directory.Exists(dir) && Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length == 0)
					{
						DeletePath(rel);
					}
				}
			}
			bool disabled = Directory.Exists(Path.Combine(_game, DisabledFolder)) || Directory.Exists(Path.Combine(_game, OldDisabledFolder));
			Finish(true, L("已卸载咖啡伴侣。") + (disabled ? "\n\n" + L("以前停用的别的 Mod 还在「BepInEx\\DisabledMods」里，想用的话自己挪回去。") : ""), L("卸载"));
		}
		catch (UnauthorizedAccessException)
		{
			AskElevate();
		}
		catch (Exception e)
		{
			Finish(false, F("卸载没有完成：{0}\n\n请确认游戏已退出，再试一次。", e.Message), L("卸载"));
		}
	}

	private void DeletePath(string rel)
	{
		string path = Path.Combine(_game, rel);
		if (Directory.Exists(path))
		{
			Directory.Delete(path, recursive: true);
		}
		else if (File.Exists(path))
		{
			File.Delete(path);
		}
		else
		{
			return;
		}
		Log(F("已删除 {0}", rel));
	}

	// ---------------- 联网检查 ----------------

	// 这次打开安装器后查过 GitHub 没有
	private bool _checkedOnline;

	// 安装在后台线程跑，读这个，不去碰勾选框
	private volatile bool _noUpdate;

	private static bool OfflineSaved
	{
		get
		{
			try
			{
				using (RegistryKey k = Registry.CurrentUser.OpenSubKey(SettingsKey))
				{
					return k != null && k.GetValue("Offline") is int v && v == 1;
				}
			}
			catch
			{
				return false;
			}
		}
	}

	private void SaveOffline(bool on)
	{
		_noUpdate = on;
		try
		{
			using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SettingsKey))
			{
				k.SetValue("Offline", on ? 1 : 0, RegistryValueKind.DWord);
			}
		}
		catch (Exception e)
		{
			Log(F("没能记住「不联网检查更新」（{0}），下次打开要重新勾选。", e.Message));
		}
	}

	private void CheckOnline()
	{
		_checkedOnline = true;
		var thread = new Thread(() =>
		{
			try
			{
				ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
				var request = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases/latest");
				request.UserAgent = "LOMAssistant-Installer";
				request.Timeout = 8000;
				request.ReadWriteTimeout = 8000;
				string json;
				using (var response = request.GetResponse())
				using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
				{
					json = reader.ReadToEnd();
				}
				Match tag = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([0-9.]+)\"");
				Match url = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+LOMAssistant\\.zip)\"");
				if (tag.Success && url.Success)
				{
					_latest = new Version(tag.Groups[1].Value);
					_latestUrl = url.Groups[1].Value;
				}
				try
				{
					var notes = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/contents/CHANGELOG.md");
					notes.UserAgent = "LOMAssistant-Installer";
					notes.Accept = "application/vnd.github.raw";
					notes.Timeout = 8000;
					notes.ReadWriteTimeout = 8000;
					using (var response = notes.GetResponse())
					using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
					{
						string text = reader.ReadToEnd().Replace("\r\n", "\n");
						if (text.Contains("\n## "))
						{
							_changelog = text;
						}
					}
				}
				catch
				{
					// 取不到就用安装器里自带的那份
				}
				if (ChangelogSuffix != null)
				{
					try
					{
						var notes = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/contents/CHANGELOG." + ChangelogSuffix + ".md");
						notes.UserAgent = "LOMAssistant-Installer";
						notes.Accept = "application/vnd.github.raw";
						notes.Timeout = 8000;
						notes.ReadWriteTimeout = 8000;
						using (var response = notes.GetResponse())
						using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
						{
							string text = reader.ReadToEnd().Replace("\r\n", "\n");
							if (text.Contains("\n## "))
							{
								_changelogTranslated = text;
							}
						}
					}
					catch
					{
						// 取不到就用安装器里自带的那份译文
					}
				}
			}
			catch (Exception e)
			{
				Log(F("连不上 GitHub（{0}），将安装压缩包自带的版本。", e.Message));
			}
			_onlineDone = true;
			BeginInvoke(new Action(() =>
			{
				UpdateVersions();
			}));
		}) { IsBackground = true };
		thread.Start();
	}

	// ---------------- 导出诊断 ----------------

	private void ExportDiagnostics()
	{
		try
		{
			string path = Diagnostics.Export(_game, _log.Text);
			Log(F("已导出诊断：{0}", path));
			try
			{
				Clipboard.SetText(path);
			}
			catch
			{
			}
			if (_diagLog != null)
			{
				File.WriteAllText(_diagLog, path, Encoding.UTF8);
				Close();
				return;
			}
			try
			{
				System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
			}
			catch
			{
			}
			MessageBox.Show(this, F("已导出：\n{0}\n\n路径已复制，资源管理器里也已选中它。反馈问题时把这个压缩包发给作者（QQ 群 496887060）。", path), L("导出诊断"), MessageBoxButtons.OK, MessageBoxIcon.Information);
		}
		catch (Exception e)
		{
			Log(F("导出诊断失败：{0}", e.Message));
			MessageBox.Show(this, F("导出失败：{0}", e.Message), L("导出诊断"), MessageBoxButtons.OK, MessageBoxIcon.Error);
		}
	}

	// ---------------- 安装 ----------------

	private void StartInstall()
	{
		if (_game == null)
		{
			MessageBox.Show(this, L("请先选择游戏目录。"), L("安装"), MessageBoxButtons.OK, MessageBoxIcon.Information);
			return;
		}
		if (NonAsciiPath(_game) && MessageBox.Show(this, L("游戏目录的路径里有中文或其他非英文字符，BepInEx 会加载失败，Mod 不会生效。请把整个游戏文件夹移到只有英文、数字的路径下（比如 D:\\Games\\LegendOfMortal；Steam 版在库里右键活侠传 → 属性 → 已安装文件 → 移动安装文件夹），再运行安装器。") + "\n\n" + L("仍然要安装吗？"), L("安装"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
		{
			return;
		}
		if (Process.GetProcessesByName("Mortal").Length > 0)
		{
			MessageBox.Show(this, L("游戏正在运行，请先退出游戏再安装。"), L("安装"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
			return;
		}
		var disable = new List<string>();
		for (int i = 0; i < _pluginPaths.Count; i++)
		{
			if (_plugins.GetItemChecked(i))
			{
				disable.Add(_pluginPaths[i]);
			}
		}
		_install.Enabled = false;
		_browse.Enabled = false;
		_offline.Enabled = false;
		var thread = new Thread(() => Install(disable)) { IsBackground = true };
		thread.Start();
	}

	private void Install(List<string> disable)
	{
		try
		{
			for (int i = 0; i < 40 && !_onlineDone && !_noUpdate; i++)
			{
				Thread.Sleep(250);
			}
			// 已经是最新版（且加载器在）就不再下载、覆盖，只处理勾选的冲突 Mod
			Version installed = DllVersion(Path.Combine(_game, ModDll));
			Version newest = BundledVersion();
			bool offline = _noUpdate;
			if (!offline && _latest != null && (newest == null || _latest > newest))
			{
				newest = _latest;
			}
			// 混进了 BepInEx 5 的文件时，加载器多半被换掉了，版本一样也要重装一遍
			bool mixed = File.Exists(Path.Combine(_game, @"BepInEx\core\BepInEx.dll")) || File.Exists(Path.Combine(_game, @"BepInEx\core\BepInEx.Preloader.dll"));
			// 加载器开关被别的 Mod 管理器关掉了：也重装一遍，复制过去的 doorstop_config.ini 会把它打开
			bool switchedOff = Diagnostics.DoorstopDisabled(_game);
			if (switchedOff)
			{
				Log(L("加载器开关被关掉了（doorstop_config.ini 里 enabled = false），重新安装把它打开。"));
			}
			// 不联网时只看是不是和压缩包里的一样：已装的更新也换成压缩包里的（玩家就是想装回这一版）
			if (installed != null && newest != null && File.Exists(Path.Combine(_game, "winhttp.dll")) && !mixed && !switchedOff
				&& (Short(installed) == Short(newest) || installed > newest && !offline))
			{
				if (Diagnostics.IgnoreDisableSwitch(_game))
				{
					Log(L("已打开加载器的 ignore_disable_switch：Steam 被带 Mod 的游戏拉起来过时，从它启动的游戏也照样加载 Mod。"));
				}
				DisablePlugins(disable);
				Logo();
				Finish(true, (offline ? F("已经装的就是压缩包里的 {0}，不需要重新安装。", Short(installed)) : F("已经是最新版本 {0}，不需要重新安装。", Short(installed)))
					+ (disable.Count > 0 ? "\n\n" + L("勾选的 Mod 已移到「BepInEx\\DisabledMods」。") : ""));
				return;
			}
			string source = ChooseSource(offline);
			if (source == Downgrade)
			{
				Finish(false, F("没能从 GitHub 下载最新版（{0}），压缩包自带的 {1} 比已经装的 {2} 还旧，没有安装，已装的版本保持不变。\n\n请稍后再试，或到 B站视频简介里的夸克网盘链接、GitHub 的 Releases 页面下载最新版的压缩包。", _downloadError ?? L("连不上 GitHub"), Short(BundledVersion()), Short(DllVersion(Path.Combine(_game, ModDll)))));
				return;
			}
			if (source == null)
			{
				Finish(false, L("没有可安装的文件：压缩包里没有安装文件，也没能从 GitHub 下载。请把整个压缩包解压后再运行安装器。"));
				return;
			}
			DisablePlugins(disable);
			RemoveBepInEx5Core();
			int copied = CopyTree(source, _game);
			Log(F("已复制 {0} 个文件。", copied));
			Version now = DllVersion(Path.Combine(_game, ModDll));
			if (now == null || !File.Exists(Path.Combine(_game, "winhttp.dll")))
			{
				Finish(false, L("安装后检查失败：游戏目录里缺少 Mod 文件。"));
				return;
			}
			Logo();
			// GitHub 上有新版但没下载下来（多半是网络连不上 GitHub）：装的是压缩包自带的旧版，要说清楚，不能只说「安装完成」
			if (_downloadError != null && _latest != null && now < _latest && Short(now) != Short(_latest))
			{
				Finish(true, F("没能从 GitHub 下载最新版 {0}（{1}），这次装的是压缩包自带的 {2}。\n\n要更新到 {0}，请到 B站视频简介里的夸克网盘链接或 GitHub 的 Releases 页下载新的压缩包，解压后运行里面的安装器。", Short(_latest), _downloadError, Short(now)),
					L("没能更新到最新版"), MessageBoxIcon.Warning);
				return;
			}
			Finish(true, F("安装完成，当前版本 {0}。\n\n正常启动游戏即可，游戏里按 F8 打开设置，Tab 打开咖啡伴侣窗口。", Short(now)));
		}
		catch (UnauthorizedAccessException e)
		{
			// 写进日志是哪个文件被拒绝（只读、杀毒软件拦截、文件夹权限），导出诊断时看得到
			Log(F("写入被拒绝：{0}", e.Message));
			BeginInvoke(new Action(AskElevate));
		}
		catch (Exception e)
		{
			Finish(false, F("安装失败：{0}", e.Message));
		}
	}

	private void Logo()
	{
		try
		{
			Log(SplashPatch.Apply(_game, _skipLogo.Checked));
		}
		catch (Exception e)
		{
			Log(F("设置开头 Logo 失败（{0}），不影响 Mod 使用。", e.Message));
		}
	}

	// 有更新的版本就从 GitHub 下载；否则（或下载失败）用压缩包自带的
	// 这次安装时下载最新版失败的原因；没下载或下载成功为 null
	private string _downloadError;

	private string ChooseSource(bool offline)
	{
		_downloadError = null;
		Version bundled = BundledVersion();
		bool hasBundled = bundled != null;
		if (offline)
		{
			if (!hasBundled)
			{
				return null;
			}
			Version old = DllVersion(Path.Combine(_game, ModDll));
			Log(old != null && old > bundled && Short(old) != Short(bundled)
				? F("已关闭联网检查，把已装的 {1} 换成压缩包自带的 {0}。", Short(bundled), Short(old))
				: F("已关闭联网检查，安装压缩包自带的版本 {0}。", Short(bundled)));
			return _payload;
		}
		if (_latest != null && _latestUrl != null && (bundled == null || Short(_latest) != Short(bundled) && _latest > bundled))
		{
			try
			{
				return Download();
			}
			catch (Exception e)
			{
				_downloadError = e.Message;
				Log(F("下载最新版失败（{0}）。", e.Message));
			}
		}
		if (hasBundled)
		{
			// 下载不了最新版时不往回装：已经装的比压缩包自带的新（玩家用旧压缩包里的安装器更新），装下去就降级了
			Version installed = DllVersion(Path.Combine(_game, ModDll));
			if (installed != null && installed > bundled && Short(installed) != Short(bundled))
			{
				Log(F("已装的 {0} 比压缩包自带的 {1} 新，不降级。", Short(installed), Short(bundled)));
				return Downgrade;
			}
			Log(F("安装压缩包自带的版本 {0}。", Short(bundled)));
			return _payload;
		}
		return null;
	}

	// ChooseSource 的结果：下载不了、压缩包自带的又比已装的旧，不装
	private const string Downgrade = "<downgrade>";

	private string Download()
	{
		string temp = Path.Combine(Path.GetTempPath(), "LOMAssistant_update");
		if (Directory.Exists(temp))
		{
			Directory.Delete(temp, recursive: true);
		}
		Directory.CreateDirectory(temp);
		string zip = Path.Combine(temp, "LOMAssistant.zip");
		Log(F("正在从 GitHub 下载 {0}…", Short(_latest)));
		var request = (HttpWebRequest)WebRequest.Create(_latestUrl);
		request.UserAgent = "LOMAssistant-Installer";
		request.Timeout = 15000;
		request.ReadWriteTimeout = 15000;
		using (var response = request.GetResponse())
		using (Stream input = response.GetResponseStream())
		using (FileStream output = File.Create(zip))
		{
			input.CopyTo(output);
		}
		string extracted = Path.Combine(temp, "files");
		// 只解出要装进游戏的文件：压缩包里的安装器 exe 解到临时文件夹会被杀毒软件当成木马隔离
		using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Read, Encoding.UTF8))
		{
			foreach (ZipArchiveEntry entry in archive.Entries)
			{
				string rel = entry.FullName.Replace('/', '\\');
				if (entry.Name.Length == 0 || !IsPayload(rel))
				{
					continue;
				}
				string target = Path.GetFullPath(Path.Combine(extracted, rel));
				if (!target.StartsWith(Path.GetFullPath(extracted) + "\\", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				Directory.CreateDirectory(Path.GetDirectoryName(target));
				entry.ExtractToFile(target, true);
			}
		}
		if (DllVersion(Path.Combine(extracted, ModDll)) == null)
		{
			throw new InvalidDataException(L("下载的压缩包里没有 Mod 文件"));
		}
		Log(L("下载完成。"));
		return extracted;
	}

	private void DisablePlugins(List<string> dlls)
	{
		if (dlls.Count == 0)
		{
			return;
		}
		string plugins = Path.Combine(_game, @"BepInEx\plugins");
		string patchers = Path.Combine(_game, @"BepInEx\patchers");
		string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
		var moved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string dll in dlls)
		{
			// patchers 里的停用到单独的子文件夹，和插件分开
			bool isPatcher = dll.StartsWith(patchers + "\\", StringComparison.OrdinalIgnoreCase);
			string root = isPatcher ? patchers : plugins;
			string target = Path.Combine(_game, DisabledFolder, stamp, isPatcher ? "patchers" : "");
			// 在根目录就只移这个 DLL；在子文件夹里就把整个子文件夹移走
			string rel = dll.Substring(root.Length + 1);
			int slash = rel.IndexOf('\\');
			string item = slash < 0 ? dll : Path.Combine(root, rel.Substring(0, slash));
			if (!moved.Add(item) || !(File.Exists(item) || Directory.Exists(item)))
			{
				continue;
			}
			Directory.CreateDirectory(target);
			string dest = Path.Combine(target, Path.GetFileName(item));
			for (int n = 2; File.Exists(dest) || Directory.Exists(dest); n++)
			{
				dest = Path.Combine(target, Path.GetFileNameWithoutExtension(item) + "_" + n + Path.GetExtension(item));
			}
			if (Directory.Exists(item))
			{
				Directory.Move(item, dest);
			}
			else
			{
				File.Move(item, dest);
			}
			Log(F("已停用：{0}（移到 {1}）", (isPatcher ? @"patchers\" : "") + item.Substring(root.Length + 1), DisabledFolder));
		}
	}

	// 有的 Mod（如立绘替换「邪神认知污染ON」）自带 BepInEx 5，照它的说明装会把 BepInEx 5 的核心文件混进来，
	// 和本 Mod 用的 BepInEx 6 混在一起游戏可能起不来。这些文件 BepInEx 6 用不到，删掉；共用的文件接下来会被覆盖成 BepInEx 6 的
	private void RemoveBepInEx5Core()
	{
		string core = Path.Combine(_game, @"BepInEx\core");
		var removed = new List<string>();
		foreach (string name in new[] { "BepInEx.dll", "BepInEx.xml", "BepInEx.Preloader.dll", "BepInEx.Preloader.xml", "BepInEx.Harmony.dll", "BepInEx.Harmony.xml", "HarmonyXInterop.dll", "0Harmony20.dll" })
		{
			string path = Path.Combine(core, name);
			if (File.Exists(path))
			{
				File.Delete(path);
				removed.Add(name);
			}
		}
		if (removed.Count > 0)
		{
			Log(F("清理了混进来的 BepInEx 5 文件：{0}", string.Join("、", removed.ToArray())));
		}
	}

	private static readonly string[] PayloadFiles = { "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" };
	private static readonly string[] PayloadFolders = { @"BepInEx\core", @"BepInEx\plugins\LOMAssistant" };

	private static bool IsPayload(string rel) => PayloadFiles.Any(name => rel.Equals(name, StringComparison.OrdinalIgnoreCase))
		|| PayloadFolders.Any(dir => rel.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase));

	private int CopyTree(string source, string dest)
	{
		if (SamePath(source, dest))
		{
			Log(L("安装文件已经在游戏目录里，跳过复制。"));
			return 0;
		}
		// 只复制安装包里本 Mod 的东西：加载器的几个文件、BepInEx\core、BepInEx\plugins\LOMAssistant。
		// 安装器所在的文件夹里可能还有别的东西（有人把压缩包解压到 D 盘根目录、下载文件夹），绝不能整个拷进游戏目录
		var files = new List<string>();
		foreach (string name in PayloadFiles)
		{
			string path = Path.Combine(source, name);
			if (File.Exists(path))
			{
				files.Add(path);
			}
		}
		foreach (string dir in PayloadFolders)
		{
			string path = Path.Combine(source, dir);
			if (Directory.Exists(path))
			{
				files.AddRange(Directory.GetFiles(path, "*", SearchOption.AllDirectories));
			}
		}
		int count = 0;
		foreach (string file in files)
		{
			string rel = file.Substring(source.TrimEnd('\\').Length + 1);
			string target = Path.Combine(dest, rel);
			Directory.CreateDirectory(Path.GetDirectoryName(target));
			// 目标文件是只读的话覆盖会被拒绝：先去掉只读
			if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
			{
				File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
				Log(F("已去掉只读属性：{0}", rel));
			}
			File.Copy(file, target, overwrite: true);
			count++;
		}
		return count;
	}

	private static bool SamePath(string a, string b)
	{
		return a != null && b != null && string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
	}

	private void Finish(bool ok, string message, string title = null, MessageBoxIcon? icon = null)
	{
		Log(message.Replace("\n\n", " "));
		BeginInvoke(new Action(() =>
		{
			if (_autoLog != null)
			{
				File.WriteAllText(_autoLog, (ok ? "OK" : "FAIL") + Environment.NewLine + _log.Text, Encoding.UTF8);
				Close();
				return;
			}
			SetGame(_game);
			_install.Enabled = true;
			_browse.Enabled = true;
			_offline.Enabled = true;
			MessageBox.Show(this, message, title ?? L(ok ? "安装完成" : "安装失败"), MessageBoxButtons.OK, icon ?? (ok ? MessageBoxIcon.Information : MessageBoxIcon.Error));
		}));
	}

	/// <summary>
	/// 写不进游戏目录。以前会自己用 runas 重新打开成管理员，但在一些玩家电脑上不起作用（一直重复弹窗），
	/// 手动右键「以管理员身份运行」才行：所以直接告诉玩家怎么做。已经是管理员还写不进去，多半是杀毒软件拦的。
	/// </summary>
	private void AskElevate()
	{
		_install.Enabled = true;
		_browse.Enabled = true;
		_offline.Enabled = true;
		bool admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
			.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
		Log(L(admin ? "没有写入游戏目录的权限（安装器已经是以管理员身份运行）。" : "没有写入游戏目录的权限（安装器不是以管理员身份运行）。"));
		string message = admin
			? L("已经以管理员身份运行，还是写不进游戏目录。\n\n多半是杀毒软件拦住了：先退出 360、火绒、电脑管家等杀毒软件（或把游戏目录加进信任），再点一次「一键安装 / 更新」。\n\n还不行的话，点「导出诊断」把桌面上的压缩包发到 QQ 群。")
			: L("没有写入游戏目录的权限（游戏可能装在 Program Files 里，或者被杀毒软件拦住了）。\n\n请关掉安装器，右键「咖啡伴侣安装器 Installer.exe」→「以管理员身份运行」，再点「一键安装 / 更新」。\n\n还不行的话，先退出 360、火绒、电脑管家等杀毒软件（或把游戏目录加进信任）再试。");
		MessageBox.Show(this, message, L("没有写入权限"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
	}
}
