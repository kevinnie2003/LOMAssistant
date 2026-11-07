using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using UnityEngine.SceneManagement;

namespace LOMAssistant;

/// <summary>
/// Binarizer 加载器的剧情 Mod。它在游戏运行时才用 Mods 文件夹里的文件顶替剧本（story\*.lua），游戏资源本身没变，
/// 直接读资源看到的还是原版，所以读剧本时先查这里。表在 Binarizer 里，反射读；没装 Binarizer 时是空的，一切照旧。
/// </summary>
internal static class ModScripts
{
	private static Dictionary<string, string> _scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

	/// <summary>插件载入的先后不定，第一个场景载入时所有插件都载完了，那时再读表。在那之前表是空的，按原版读。</summary>
	public static void Prepare() => SceneManager.sceneLoaded += SceneLoaded;

	private static void SceneLoaded(Scene scene, LoadSceneMode mode)
	{
		SceneManager.sceneLoaded -= SceneLoaded;
		try
		{
			// 不用 AccessTools.TypeByName：没装 Binarizer 时它会把所有程序集的类型扫一遍
			Type found = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType("Mortal.HookCustomStory", false)).FirstOrDefault(t => t != null);
			if (found != null && AccessTools.Field(found, "mapStory")?.GetValue(null) is Dictionary<string, string> table)
			{
				_scripts = new Dictionary<string, string>(table, StringComparer.OrdinalIgnoreCase);
				FileLogger.Log($"[剧情 Mod] Binarizer：剧本 {_scripts.Count} 个");
			}
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[剧情 Mod] 读 Binarizer 的剧本表失败: " + ex.Message);
		}
	}

	/// <summary>Mod 顶替的剧本原文，不是 Mod 的剧本返回 null。读法和 Binarizer 一样，随时存档的哈希才对得上。</summary>
	public static string Text(string name)
	{
		if (name == null || !_scripts.TryGetValue(name, out string path))
		{
			return null;
		}
		try
		{
			return File.Exists(path) ? File.ReadAllText(path) : null;
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[剧情 Mod] 读 " + path + " 失败: " + ex.Message);
			return null;
		}
	}

	/// <summary>Mod 新增的剧本：known 里没有的那些。</summary>
	public static List<string> Added(HashSet<string> known)
	{
		return _scripts.Keys.Where(name => !known.Contains(name)).ToList();
	}

	/// <summary>Mod 剧本的指纹，换了 Mod 或改了文件就变。没有 Mod 剧本时是空串。</summary>
	public static string Fingerprint
	{
		get
		{
			if (_scripts.Count == 0)
			{
				return "";
			}
			var text = new StringBuilder();
			foreach (var pair in _scripts.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
			{
				var file = new FileInfo(pair.Value);
				text.Append(pair.Key).Append(':').Append(file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "").Append('|');
			}
			using (SHA256 sha = SHA256.Create())
			{
				return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
			}
		}
	}
}
