using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using Newtonsoft.Json;
using UnityEngine;

namespace LOMAssistant.Features.Story;

/// <summary>
/// 剧本解析结果的缓存：开游戏后生成时间线要把 2200 个剧本逐个解析一遍（几秒），解析完存到 BepInEx\cache 里，
/// 下次开游戏时游戏版本、咖啡伴侣的 DLL 和剧情 Mod 的剧本都没变就直接读缓存（在后台线程读，不占画面）。其中一样变了就重新解析。
/// </summary>
public static class StoryScriptCache
{
	private static string FilePath => Path.Combine(Paths.CachePath, "LOMAssistant", "StoryScripts.json");

	// 第一行写这个，对得上才用：游戏版本 + 这份 DLL 的修改时间（解析的写法改了，DLL 一定换过）+ 剧情 Mod 剧本的指纹
	private static string Key
	{
		get
		{
			string dll = typeof(StoryScriptCache).Assembly.Location;
			long built = File.Exists(dll) ? File.GetLastWriteTimeUtc(dll).Ticks : 0;
			string mods = ModScripts.Fingerprint;
			return "v1|" + Application.version + "|" + built + (mods.Length > 0 ? "|" + mods : "");
		}
	}

	/// <summary>在后台读缓存；没有缓存、对不上或读坏了时结果是 null。</summary>
	public static Task<List<StoryScript>> Load()
	{
		string path = FilePath, key = Key;
		return Task.Run(() =>
		{
			try
			{
				if (!File.Exists(path))
				{
					return null;
				}
				using (var reader = new StreamReader(path, Encoding.UTF8))
				{
					if (reader.ReadLine() != key)
					{
						return null;
					}
					using (var json = new JsonTextReader(reader))
					{
						return new JsonSerializer().Deserialize<List<StoryScript>>(json);
					}
				}
			}
			catch (Exception ex)
			{
				FileLogger.LogWarning("[剧情回顾] 剧本缓存读取失败，重新解析: " + ex.Message);
				return null;
			}
		});
	}

	/// <summary>在后台把解析结果写进缓存（先写临时文件再换掉）。</summary>
	public static void Save(List<StoryScript> scripts)
	{
		string path = FilePath, key = Key;
		Task.Run(() =>
		{
			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				string tmp = path + ".tmp";
				using (var writer = new StreamWriter(tmp, false, new UTF8Encoding(false)))
				{
					writer.WriteLine(key);
					new JsonSerializer().Serialize(writer, scripts);
				}
				if (File.Exists(path))
				{
					File.Delete(path);
				}
				File.Move(tmp, path);
			}
			catch (Exception ex)
			{
				FileLogger.LogWarning("[剧情回顾] 剧本缓存保存失败: " + ex.Message);
			}
		});
	}
}
