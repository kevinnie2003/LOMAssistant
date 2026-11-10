using System;
using System.IO;
using System.Text;
using HarmonyLib;
using Mortal.Core;
using UnityEngine.UI;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 存档备注：每个手动存档旁边存一个 Save_XXX.dat.note（UTF-8 文本），会随云存档一起同步。
/// 存档/读档界面在存档栏下方显示“【备注】”；在存档/读档界面按 F8，可给所选存档写备注。
/// </summary>
public static class SaveNotePatch
{
	// slot -> note, so the F8 panel doesn't re-read the file on every GUI event.
	private static readonly System.Collections.Generic.Dictionary<string, string> Cache = new System.Collections.Generic.Dictionary<string, string>();

	public static string GetNote(string slot)
	{
		ImportFromSaveSlotExpander();
		ImportFromSaveSlotMemo();
		if (slot != null && Cache.TryGetValue(slot, out string cached))
		{
			return cached;
		}
		try
		{
			string path = GetNotePath(slot);
			string note = path != null && File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8).Trim() : "";
			if (path != null)
			{
				Cache[slot] = note;
			}
			return note;
		}
		catch (Exception e)
		{
			FileLogger.LogError($"存档备注：读取 {slot} 失败: {e.Message}");
			return "";
		}
	}

	public static void SetNote(string slot, string note)
	{
		try
		{
			string path = GetNotePath(slot);
			if (path == null)
			{
				return;
			}
			note = (note ?? "").Trim();
			if (note.Length == 0)
			{
				if (File.Exists(path))
				{
					File.Delete(path);
				}
			}
			else
			{
				File.WriteAllText(path, note, new UTF8Encoding(false));
			}
			Cache[slot] = note;
			FileLogger.Log($"存档备注：{slot} -> {note}");
		}
		catch (Exception e)
		{
			LOMAssistantPlugin.LogError($"存档备注：保存 {slot} 失败: {e.Message}");
		}
	}

	/// <summary>Only numbered manual slots ("001"...) get notes.</summary>
	public static bool IsManualSlot(string slot)
	{
		return !string.IsNullOrEmpty(slot) && int.TryParse(slot, out _);
	}

	private static string GetNotePath(string slot)
	{
		if (!IsManualSlot(slot) || SaveSystem.Instance == null)
		{
			return null;
		}
		string dat = Traverse.Create(SaveSystem.Instance).Method("GetSaveDataFile", new[] { typeof(string) }).GetValue<string>(slot);
		return dat + ".note";
	}

	// ---- 从 LOM_SaveSlotMemo 迁移备注（一次性） ----

	private const string MemoImportedMarker = "LOMAssistant_SaveSlotMemo.imported";
	private static bool _memoImportChecked;

	/// <summary>
	/// LOM_SaveSlotMemo（韩国 DC 画廊的存档格、备注 Mod）把备注存在每个存档旁边的 Save_NNN.memo.txt（UTF-8，可多行）。
	/// 第一次读备注时把它们导入成我们的 Save_NNN.dat.note：存档文件不在的、已有我们备注的栏位跳过，
	/// 对方的文件不改动；导入后在存档目录写一个标记，之后不再重复导入（否则在我们这边删掉的备注下次启动又会回来）。
	/// </summary>
	private static void ImportFromSaveSlotMemo()
	{
		if (_memoImportChecked || SaveSystem.Instance == null)
		{
			return;
		}
		_memoImportChecked = true;
		try
		{
			string anyDat = Traverse.Create(SaveSystem.Instance).Method("GetSaveDataFile", new[] { typeof(string) }).GetValue<string>("001");
			string dir = anyDat != null ? Path.GetDirectoryName(anyDat) : null;
			if (dir == null || !Directory.Exists(dir) || File.Exists(Path.Combine(dir, MemoImportedMarker)))
			{
				return;
			}
			string[] memos = Directory.GetFiles(dir, "Save_*.memo.txt");
			if (memos.Length == 0)
			{
				return;
			}
			int imported = 0, skipped = 0;
			foreach (string memo in memos)
			{
				string name = Path.GetFileName(memo);
				string slot = name.Substring("Save_".Length, name.Length - "Save_".Length - ".memo.txt".Length);
				if (!IsManualSlot(slot))
				{
					continue;
				}
				string path = GetNotePath(slot);
				if (path == null || !File.Exists(path.Substring(0, path.Length - ".note".Length)))
				{
					continue;
				}
				// 对方的备注可以多行、很长，列表里只显示第一行；我们的备注一行、最多 30 字：取第一行，长的截断（原文件还在）
				string value = File.ReadAllText(memo, Encoding.UTF8).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
				int newline = value.IndexOf('\n');
				value = (newline >= 0 ? value.Substring(0, newline) : value).Trim();
				if (value.Length > SaveNoteEditor.MaxLength)
				{
					value = value.Substring(0, SaveNoteEditor.MaxLength);
				}
				if (value.Length == 0)
				{
					continue;
				}
				if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8).Trim().Length > 0)
				{
					skipped++;
					continue;
				}
				SetNote(slot, value);
				imported++;
			}
			File.WriteAllText(Path.Combine(dir, MemoImportedMarker), $"咖啡伴侣已于 {DateTime.Now:yyyy-MM-dd HH:mm:ss} 导入 LOM_SaveSlotMemo 的备注（Save_NNN.memo.txt）：{imported} 条，跳过 {skipped} 条（已有备注）。\r\n删除本文件会在下次启动时重新导入。\r\n", new UTF8Encoding(false));
			string msg = $"存档备注：已从 LOM_SaveSlotMemo 导入 {imported} 条备注，跳过 {skipped} 条（已有备注）。";
			LOMAssistantPlugin.LogInfo(msg);
			FileLogger.Log(msg);
		}
		catch (Exception e)
		{
			LOMAssistantPlugin.LogError("存档备注：导入 LOM_SaveSlotMemo 备注失败: " + e.Message);
		}
	}

	// ---- 从 LOM_SaveSlotExpander 迁移备注（一次性） ----

	private const string ExpanderCommentFile = "SaveSlotComments.ini";
	private const string ImportedMarker = ".imported";
	private static bool _importChecked;

	/// <summary>
	/// LOM_SaveSlotExpander 把备注存在存档目录的 SaveSlotComments.ini（“001 = 备注”）。
	/// 第一次读备注时把它们导入成我们的 Save_XXX.dat.note：已有我们备注的栏位跳过，
	/// 对方的 ini 不改动；导入后写一个 .imported 标记，之后不再重复导入
	/// （否则在我们这边删掉的备注下次启动又会回来）。
	/// </summary>
	private static void ImportFromSaveSlotExpander()
	{
		if (_importChecked || SaveSystem.Instance == null)
		{
			return;
		}
		_importChecked = true;
		try
		{
			string anyDat = Traverse.Create(SaveSystem.Instance).Method("GetSaveDataFile", new[] { typeof(string) }).GetValue<string>("001");
			string dir = anyDat != null ? Path.GetDirectoryName(anyDat) : null;
			string ini = dir != null ? Path.Combine(dir, ExpanderCommentFile) : null;
			if (ini == null || !File.Exists(ini) || File.Exists(ini + ImportedMarker))
			{
				return;
			}
			int imported = 0, skipped = 0;
			foreach (string raw in File.ReadAllLines(ini, Encoding.UTF8))
			{
				string line = raw.Trim();
				if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[')
				{
					continue;
				}
				int sep = line.IndexOf('=');
				if (sep <= 0)
				{
					continue;
				}
				string key = line.Substring(0, sep).Trim();
				string value = line.Substring(sep + 1).Trim();
				if (!int.TryParse(key, out int number) || number <= 0 || value.Length == 0)
				{
					continue;
				}
				string slot = number.ToString().PadLeft(3, '0');
				string path = GetNotePath(slot);
				if (path == null)
				{
					continue;
				}
				if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8).Trim().Length > 0)
				{
					skipped++;
					continue;
				}
				SetNote(slot, value);
				imported++;
			}
			File.WriteAllText(ini + ImportedMarker, $"咖啡伴侣已于 {DateTime.Now:yyyy-MM-dd HH:mm:ss} 导入本文件的备注：{imported} 条，跳过 {skipped} 条（已有备注）。\r\n删除本文件会在下次启动时重新导入。\r\n", new UTF8Encoding(false));
			string msg = $"存档备注：已从 LOM_SaveSlotExpander 导入 {imported} 条备注，跳过 {skipped} 条（已有备注）。";
			LOMAssistantPlugin.LogInfo(msg);
			FileLogger.Log(msg);
		}
		catch (Exception e)
		{
			LOMAssistantPlugin.LogError("存档备注：导入 LOM_SaveSlotExpander 备注失败: " + e.Message);
		}
	}

	// ---- 显示：在各存档栏位下方单独一行显示“【备注】” ----

	[HarmonyPostfix]
	[HarmonyPatch(typeof(SaveSlotPanel), "SetupText")]
	public static void SaveSlotPanel_SetupText_Postfix(SaveSlotPanel __instance, GameSave data)
	{
		AppendNote(__instance, data);
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(LoadSlotPanel), "SetupText")]
	public static void LoadSlotPanel_SetupText_Postfix(LoadSlotPanel __instance, GameSave data)
	{
		AppendNote(__instance, data);
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(RecentSaveSlotPanel), "SetupText")]
	public static void RecentSaveSlotPanel_SetupText_Postfix(RecentSaveSlotPanel __instance, GameSave data)
	{
		AppendNote(__instance, data);
	}

	private static void AppendNote(object panel, GameSave data)
	{
		var tr = Traverse.Create(panel);
		string note = data != null ? GetNote(tr.Field("_slot").GetValue<string>()) : "";
		SaveNoteLabel.Apply((UnityEngine.Component)panel, tr.Field("_titleText").GetValue<Text[]>(), tr.Field("_timeText").GetValue<Text[]>(), note);
	}

	// ---- 删除存档时一并删除备注 ----

	[HarmonyPostfix]
	[HarmonyPatch(typeof(SaveSystem), "DeleteSaveData", new[] { typeof(string) })]
	public static void SaveSystem_DeleteSaveData_Postfix(string slot)
	{
		SetNote(slot, "");
	}
}
