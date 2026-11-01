using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace LOMAssistant.Features.Memo;

/// <summary>
/// 备忘录的存放：全部写在 BepInEx\plugins\LOMAssistant\StoryReview\memo.json（和数值页那几个标记文件同一个目录），
/// 跟着 Mod 走、不进游戏存档，所以读档、换存档、重开一局都还在。改了立刻写盘（写临时文件再换过去，
/// 中途崩了也不把原来的写坏）。截止日期记的是绝对的旬数，重开一局后日期对不上的条目自己删。
/// </summary>
public static class MemoStore
{
	private const int Version = 1;

	[Serializable]
	private sealed class MemoFile
	{
		public int version = Version;
		public List<MemoItem> items = new List<MemoItem>();
	}

	private static readonly string Dir = Path.Combine(Path.GetDirectoryName(typeof(MemoStore).Assembly.Location) ?? ".", "StoryReview");
	private static string FullPath => Path.Combine(Dir, "memo.json");

	private static List<MemoItem> _items;

	/// <summary>全部备忘录（按建的先后）。第一次用时读盘。</summary>
	public static List<MemoItem> Items
	{
		get
		{
			if (_items == null)
			{
				Load();
			}
			return _items;
		}
	}

	private static void Load()
	{
		_items = new List<MemoItem>();
		try
		{
			if (!System.IO.File.Exists(FullPath))
			{
				return;
			}
			MemoFile file = JsonConvert.DeserializeObject<MemoFile>(System.IO.File.ReadAllText(FullPath, Encoding.UTF8));
			foreach (MemoItem item in file?.items ?? new List<MemoItem>())
			{
				if (item == null || string.IsNullOrEmpty(item.key))
				{
					continue;
				}
				if (string.IsNullOrEmpty(item.id))
				{
					item.id = Guid.NewGuid().ToString("N");
				}
				_items.Add(item);
			}
		}
		catch (Exception ex)
		{
			// 读不出来就空着开始，别把游戏搞崩；原来的文件留着不动
			_items = new List<MemoItem>();
			FileLogger.LogWarning("[备忘录] 读取失败，从空的开始: " + ex.Message);
		}
	}

	/// <summary>写盘。改完条目就调一次。</summary>
	public static void Save()
	{
		try
		{
			Directory.CreateDirectory(Dir);
			string tmp = FullPath + ".tmp";
			System.IO.File.WriteAllText(tmp, JsonConvert.SerializeObject(new MemoFile { items = _items ?? new List<MemoItem>() }, Formatting.Indented), Encoding.UTF8);
			if (System.IO.File.Exists(FullPath))
			{
				System.IO.File.Replace(tmp, FullPath, null);
			}
			else
			{
				System.IO.File.Move(tmp, FullPath);
			}
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[备忘录] 写入失败: " + ex.Message);
		}
	}
}
