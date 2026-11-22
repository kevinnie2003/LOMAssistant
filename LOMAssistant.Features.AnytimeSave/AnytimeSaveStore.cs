using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LOMAssistant.Config;
using LOMAssistant.Features.Story;
using Mortal.Core;
using Newtonsoft.Json;

namespace LOMAssistant.Features.AnytimeSave;

/// <summary>随时存档列表里一条的摘要：存档时顺手写进索引，列表和悬停提示都看它，不用把存档整个读出来。</summary>
[Serializable]
public class SaveSummary
{
	public string file = "";
	public int number;
	public string savedAt = "", scene = "", gameTime = "", script = "", preview = "", note = "";
	/// <summary>存档时游戏的存档栏位（「传奇018」的 018；从节点存档重玩时是 replay），列表按栏位分组用；以前的索引里没有，是 null，打开列表时补上。</summary>
	public string slot;
	/// <summary>存档经过的剧本和哈希，用来看游戏更新有没有改到这些剧本；以前的索引里没有，是 null。</summary>
	public List<ScriptStamp> scripts;
	/// <summary>存档时之前的几句对白（最近的在最后）。</summary>
	public List<string> lines = new List<string>();
	/// <summary>这一段剧情里做过的选择（最近的在最后）。</summary>
	public List<string> choices = new List<string>();
	/// <summary>读不出来时的原因（文件坏了、版本不兼容），列表里照样显示，读不了。</summary>
	public string error;
}

/// <summary>
/// 随时存档的存放：每次保存都是一个新文件（Anytime_0001.json 往后编号），超过上限删最旧的；
/// 以前 8 个槽位的 Slot_XX.json 照样列出来、照样能读。摘要和备注存在同一目录的 index.json 里。
/// </summary>
public partial class AnytimeSave
{
	private const string IndexFile = "index.json";
	private static readonly Regex NewName = new Regex(@"^Anytime_(\d+)\.json$");
	private static readonly Regex OldName = new Regex(@"^Slot_(\d+)\.json$");
	// 存档内容是包在外层 JSON 里的字符串，里面的引号都带反斜杠
	private static readonly Regex SlotField = new Regex(@"\\""nativeSlot\\"":\\""([^\\""]*)\\""");
	private List<SaveSummary> summaries;

	/// <summary>随时存档用不了时的原因（版本核验没通过等）；能用时是 null。</summary>
	public static string Unavailable { get; private set; }

	private static string IndexPath => Path.Combine(StoreRoot, IndexFile);

	/// <summary>全部随时存档，新的在前。第一次调用时读索引，补上索引里没有的文件（以前的槽位存档），去掉已经不在的。</summary>
	public List<SaveSummary> Saves()
	{
		if (summaries != null)
		{
			return summaries;
		}
		summaries = new List<SaveSummary>();
		try
		{
			if (File.Exists(IndexPath))
			{
				summaries = JsonConvert.DeserializeObject<List<SaveSummary>>(File.ReadAllText(IndexPath, Encoding.UTF8)) ?? new List<SaveSummary>();
			}
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[随时存档] 索引读取失败，重新整理: " + ex.Message);
			summaries = new List<SaveSummary>();
		}
		bool changed = summaries.RemoveAll(s => s == null || !File.Exists(Path.Combine(StoreRoot, s.file))) > 0;
		if (Directory.Exists(StoreRoot))
		{
			foreach (string path in Directory.GetFiles(StoreRoot, "*.json"))
			{
				string name = Path.GetFileName(path);
				if (name == IndexFile || summaries.Any(s => s.file == name) || (!NewName.IsMatch(name) && !OldName.IsMatch(name)))
				{
					continue;
				}
				SaveSummary summary;
				try
				{
					summary = Summarize(ReadSave(path), name);
				}
				catch (Exception ex)
				{
					summary = new SaveSummary { file = name, savedAt = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss"), error = ex.Message };
				}
				summaries.Add(summary);
				changed = true;
			}
		}
		// 以前的索引没记栏位：从存档文件里找出来补上（只找这一项，不整个解析）
		foreach (SaveSummary s in summaries.Where(s => s.slot == null))
		{
			try
			{
				Match m = SlotField.Match(File.ReadAllText(Path.Combine(StoreRoot, s.file), Encoding.UTF8));
				s.slot = m.Success ? m.Groups[1].Value : "";
			}
			catch (Exception)
			{
				s.slot = "";
			}
			changed = true;
		}
		Sort();
		if (changed)
		{
			WriteIndex();
		}
		return summaries;
	}

	private void Sort()
	{
		summaries.Sort((a, b) =>
		{
			int byTime = string.CompareOrdinal(b.savedAt, a.savedAt);
			return byTime != 0 ? byTime : b.number.CompareTo(a.number);
		});
	}

	private void WriteIndex()
	{
		try
		{
			Directory.CreateDirectory(StoreRoot);
			string tmp = IndexPath + ".tmp";
			File.WriteAllText(tmp, JsonConvert.SerializeObject(summaries, Formatting.Indented), Encoding.UTF8);
			if (File.Exists(IndexPath))
			{
				File.Delete(IndexPath);
			}
			File.Move(tmp, IndexPath);
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[随时存档] 索引写入失败: " + ex.Message);
		}
	}

	/// <summary>新存档的文件名：比现有最大编号大一。</summary>
	private string NextFile(out int number)
	{
		number = 1;
		if (Directory.Exists(StoreRoot))
		{
			foreach (string path in Directory.GetFiles(StoreRoot, "Anytime_*.json"))
			{
				Match m = NewName.Match(Path.GetFileName(path));
				if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n >= number)
				{
					number = n + 1;
				}
			}
		}
		return "Anytime_" + number.ToString("0000") + ".json";
	}

	private static int NumberOf(string name)
	{
		Match m = NewName.Match(name);
		if (m.Success && int.TryParse(m.Groups[1].Value, out int n))
		{
			return n;
		}
		m = OldName.Match(name);
		return m.Success && int.TryParse(m.Groups[1].Value, out n) ? -n : 0;
	}

	/// <summary>列表、悬停提示里显示的名字：新存档 #12，以前的槽位存档「旧槽位 3」。</summary>
	public static string DisplayName(SaveSummary s) => s.number > 0 ? "#" + s.number : s.number < 0 ? "旧槽位 " + (-s.number) : s.file;

	private SaveSummary Summarize(SaveFile data, string name)
	{
		var summary = new SaveSummary
		{
			file = name,
			number = NumberOf(name),
			savedAt = data.savedAt ?? "",
			scene = data.scene ?? "",
			gameTime = data.gameTime ?? "",
			script = data.scene == "Free" ? "" : data.currentScript ?? data.rootScript ?? "",
			preview = RichText.Strip(data.preview ?? ""),
			scripts = data.scripts,
			slot = data.nativeSlot ?? "",
		};
		List<Entry> trace = data.trace ?? new List<Entry>();
		int end = data.scene == "Story" && data.target >= 0 && data.target < trace.Count ? data.target : trace.Count - 1;
		for (int i = 0; i <= end; i++)
		{
			Entry e = trace[i];
			if (e.kind == "say" && i < end)
			{
				string line = Regex.Replace(e.label ?? "", @"<[^>]+>|\{[^}]+\}", "").Trim();
				if (line.Length > 0)
				{
					summary.lines.Add(TextConverter.ToSimplified(line));
				}
			}
			else if ((e.kind == "choose" || e.kind == "timer") && i < end && e.type == "number")
			{
				string chosen = ChosenOption(e);
				if (chosen.Length > 0)
				{
					summary.choices.Add(chosen);
				}
			}
		}
		if (summary.lines.Count > 6)
		{
			summary.lines.RemoveRange(0, summary.lines.Count - 6);
		}
		if (summary.choices.Count > 4)
		{
			summary.choices.RemoveRange(0, summary.choices.Count - 4);
		}
		return summary;
	}

	/// <summary>一次选择选了哪个：记录里是选项编号列表和选中的序号（从 1 数）。</summary>
	private static string ChosenOption(Entry e)
	{
		string[] keys = (e.label ?? "").Split('|');
		if (!double.TryParse(e.value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v))
		{
			return "";
		}
		int index = (int)v - 1;
		if (index < 0 || index >= keys.Length)
		{
			return "";
		}
		string key = Regex.Match(keys[index], @"O_[A-Za-z0-9_]+").Value;
		return key.Length > 0 ? StoryScripts.OptionText(key) : "";
	}

	/// <summary>存了新档以后：写进索引，超过上限删最旧的（以前的槽位存档也算在内）。</summary>
	private void Remember(SaveFile data, string name)
	{
		Saves();
		summaries.RemoveAll(s => s.file == name);
		summaries.Add(Summarize(data, name));
		Sort();
		// 0 = 不限，不自动删；有备注的是玩家特意留的，不删也不算进上限
		int limit = ConfigManager.AnytimeSaveLimit.Value;
		while (limit > 0 && summaries.Count(s => string.IsNullOrEmpty(s.note)) > limit)
		{
			int i = summaries.FindLastIndex(s => string.IsNullOrEmpty(s.note));
			SaveSummary oldest = summaries[i];
			DeleteFiles(oldest.file);
			summaries.RemoveAt(i);
			FileLogger.Log("[随时存档] 超过上限 " + limit + " 个（有备注的不算），删掉最旧的 " + oldest.file);
		}
		WriteIndex();
	}

	private static void DeleteFiles(string name)
	{
		string path = Path.Combine(StoreRoot, name);
		foreach (string p in new[] { path, path + ".bak", path + ".tmp" })
		{
			try
			{
				if (File.Exists(p))
				{
					File.Delete(p);
				}
			}
			catch (Exception ex)
			{
				FileLogger.LogWarning("[随时存档] 删除失败 " + p + ": " + ex.Message);
			}
		}
	}

	/// <summary>删掉一个随时存档。</summary>
	public void Delete(string name)
	{
		Saves();
		DeleteFiles(name);
		summaries.RemoveAll(s => s.file == name);
		WriteIndex();
		Notice("已删除随时存档");
	}

	/// <summary>一次删掉多个随时存档（批量删除）：索引只写一次，只提示一条。</summary>
	public void Delete(ICollection<string> names)
	{
		Saves();
		foreach (string name in names)
		{
			DeleteFiles(name);
		}
		summaries.RemoveAll(s => names.Contains(s.file));
		WriteIndex();
		Notice(string.Format("已删除 {0} 个随时存档", names.Count));
	}

	/// <summary>改备注（空字符串就是去掉备注）。</summary>
	public void SetNote(string name, string note)
	{
		SaveSummary s = Saves().FirstOrDefault(x => x.file == name);
		if (s == null)
		{
			return;
		}
		s.note = (note ?? "").Trim();
		WriteIndex();
	}

	/// <summary>F7：读最近的那个随时存档。</summary>
	public void LoadLatest()
	{
		// 游戏更新改过经过的剧本的读不了，跳过
		SaveSummary latest = Saves().FirstOrDefault(s => s.error == null && ChangedScript(s.scripts) == null);
		if (latest == null)
		{
			Notice(Saves().Any(s => s.error == null) ? "随时存档经过的剧本都被游戏更新改过了，读不了" : "还没有随时存档");
			return;
		}
		LoadFile(latest.file);
	}

	/// <summary>当前游戏日期，写进存档摘要。</summary>
	private static string GameTimeText()
	{
		try
		{
			GameTime t = PlayerStatManagerData.Instance?.GameTime;
			return ReferenceEquals(t, null) ? "" : StoryAtlas.DateText(t);
		}
		catch
		{
			return "";
		}
	}
}
