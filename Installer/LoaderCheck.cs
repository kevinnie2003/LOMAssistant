using System;
using System.Collections.Generic;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace LOMAssistantInstaller;

/// <summary>
/// 诊断里的「加载器排查」：BepInEx 一次都没启动（没有 LogOutput.log）时，查游戏为什么没加载游戏目录里的 winhttp.dll。
/// 查的都是只读的东西：游戏目录里有哪些文件、系统的已知 DLL 名单、兼容模式、智能应用控制、代码完整性日志里的拦截记录。
/// 不查杀毒软件、别的进程的命令行和模块、映像劫持设置：这些是恶意程序侦察环境的典型行为，会让杀毒软件把安装器判成木马。
/// 每一项单独 try，读不到就写原因，不影响别的项。
/// </summary>
internal static class LoaderCheck
{
	private static readonly string[] ProxyNames = { "winhttp.dll", "version.dll", "winmm.dll" };

	public static string Report(string game)
	{
		var sb = new StringBuilder();
		sb.AppendLine();
		sb.AppendLine("加载器排查：");
		Section(sb, "游戏目录里的文件", () => GameRoot(game));
		Section(sb, "BepInEx\\core 里的文件", () => CoreFiles(game));
		Section(sb, "系统已知 DLL 名单（在名单里的名字，游戏只会加载系统自带的那份）", KnownDlls);
		Section(sb, "Mortal.exe 的兼容模式设置", CompatLayers);
		Section(sb, "DOORSTOP_ 开头的环境变量（DOORSTOP_DISABLE 有值时，没开 ignore_disable_switch 的加载器什么都不做）", DoorstopEnv);
		Section(sb, "智能应用控制", SmartAppControl);
		Section(sb, "代码完整性日志里和游戏有关的拦截记录（最近 14 天）", CodeIntegrity);
		return sb.ToString();
	}

	private static void Section(StringBuilder sb, string title, Func<IEnumerable<string>> lines)
	{
		sb.AppendLine("  " + title + "：");
		try
		{
			var list = lines().ToList();
			if (list.Count == 0)
			{
				sb.AppendLine("    （无）");
			}
			foreach (string line in list)
			{
				sb.AppendLine("    " + line);
			}
		}
		catch (Exception e)
		{
			sb.AppendLine("    读取失败：" + e.Message);
		}
	}

	private static IEnumerable<string> GameRoot(string game)
	{
		foreach (string file in Directory.GetFiles(game).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
		{
			var info = new FileInfo(file);
			yield return info.Name + "  " + info.Length + " 字节  " + info.LastWriteTime.ToString("yyyy-MM-dd HH:mm") + Attr(info);
		}
	}

	private static IEnumerable<string> CoreFiles(string game)
	{
		string core = Path.Combine(game, @"BepInEx\core");
		if (!Directory.Exists(core))
		{
			yield return "没有这个文件夹";
			yield break;
		}
		foreach (string file in Directory.GetFiles(core).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
		{
			var info = new FileInfo(file);
			yield return info.Name + "  " + info.Length + " 字节" + Attr(info);
		}
	}

	// 只读、隐藏、系统这些属性一般没关系，有的话写出来
	private static string Attr(FileInfo info)
	{
		FileAttributes odd = info.Attributes & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Offline | FileAttributes.Encrypted);
		return odd != 0 ? "  [" + odd + "]" : "";
	}

	private static IEnumerable<string> KnownDlls()
	{
		using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs"))
		{
			if (key == null)
			{
				yield return "读不到";
				yield break;
			}
			foreach (string value in key.GetValueNames())
			{
				string dll = Convert.ToString(key.GetValue(value)) ?? "";
				if (ProxyNames.Any(n => dll.Equals(n, StringComparison.OrdinalIgnoreCase)))
				{
					yield return value + " = " + dll + "（在名单里：游戏目录里同名的文件不会被加载）";
				}
			}
		}
	}

	private static IEnumerable<string> CompatLayers()
	{
		foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
		{
			using (RegistryKey key = hive.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers"))
			{
				if (key == null)
				{
					continue;
				}
				foreach (string value in key.GetValueNames())
				{
					if (value.IndexOf("Mortal", StringComparison.OrdinalIgnoreCase) >= 0 || value.IndexOf("LegendOfMortal", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						yield return (hive == Registry.CurrentUser ? "HKCU" : "HKLM") + "  " + value + " = " + key.GetValue(value);
					}
				}
			}
		}
	}

	// 别的用 Doorstop 的程序（Mod 管理器、开了 Mod 的其他游戏）启动子进程时会设 DOORSTOP_DISABLE；
	// 设进了用户或系统的环境变量、或 Steam 继承了它，从 Steam 开的游戏里加载器就会直接跳过
	private static IEnumerable<string> DoorstopEnv()
	{
		foreach (EnvironmentVariableTarget target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
		{
			foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables(target))
			{
				string name = Convert.ToString(e.Key);
				if (name.StartsWith("DOORSTOP", StringComparison.OrdinalIgnoreCase))
				{
					yield return (target == EnvironmentVariableTarget.Process ? "安装器继承的" : target == EnvironmentVariableTarget.User ? "用户" : "系统") + "  " + name + " = " + e.Value;
				}
			}
		}
	}

	private static IEnumerable<string> SmartAppControl()
	{
		using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CI\Policy"))
		{
			object v = key?.GetValue("VerifiedAndReputablePolicyState");
			if (v == null)
			{
				yield return "没有这项设置（系统不支持或从没开过）";
				yield break;
			}
			int state = Convert.ToInt32(v);
			yield return state == 0 ? "关闭" : state == 1 ? "开启（会拦没有签名的 DLL）" : state == 2 ? "评估模式（只记录，不拦）" : "未知值 " + state;
		}
	}

	private static IEnumerable<string> CodeIntegrity()
	{
		var hits = new List<string>();
		var query = new EventLogQuery("Microsoft-Windows-CodeIntegrity/Operational", PathType.LogName, "*[System[TimeCreated[timediff(@SystemTime) <= 1209600000]]]") { ReverseDirection = true };
		using (var reader = new EventLogReader(query))
		{
			int scanned = 0;
			for (EventRecord e = reader.ReadEvent(); e != null && scanned < 5000 && hits.Count < 20; e = reader.ReadEvent(), scanned++)
			{
				using (e)
				{
					string text;
					try
					{
						text = e.FormatDescription() ?? "";
					}
					catch
					{
						text = string.Join(" ", e.Properties.Select(p => Convert.ToString(p.Value)).ToArray());
					}
					if (text.IndexOf("Mortal", StringComparison.OrdinalIgnoreCase) >= 0 || ProxyNames.Any(n => text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0 && text.IndexOf("LegendOfMortal", StringComparison.OrdinalIgnoreCase) >= 0))
					{
						string one = text.Replace("\r", " ").Replace("\n", " ");
						hits.Add(e.TimeCreated?.ToString("yyyy-MM-dd HH:mm") + "  事件 " + e.Id + "  " + (one.Length > 400 ? one.Substring(0, 400) + "…" : one));
					}
				}
			}
		}
		return hits;
	}

}
