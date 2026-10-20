using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace LOMAssistant.Features.AnytimeSave;

public partial class AnytimeSave
{
	private string compatibilityId;

	// 以前核验过的契约对应的存档标识：游戏更新后确认存读档逻辑没变，旧档继续可读
	private static readonly HashSet<string> AcceptedIds = new HashSet<string>(StringComparer.Ordinal)
	{
		"CA3DF8712A594A7636DA70D24105C88C0EE0920F49669F122B379CE17A640B6E", // MortalAnytimeSave 原版，游戏 1.0.6000.3
		"82C718B84503409D5D32218A90AE862DC1C3C2888270BE585E239C07A488E64E", // 本 Mod 2.1.1 的契约（游戏 1.0.6000.5 之前），游戏更新后存档继续可读
	};

	/// <summary>把游戏里存读档用到的方法和类型结构跟内置契约比对，有改动就停用，返回原因；通过返回 null。</summary>
	private string CheckCompatibility()
	{
		if (IntPtr.Size != 8) return "随时存档需要 64 位游戏。";
		try
		{
			string managed = Path.Combine(Paths.GameRootPath, "Mortal_Data", "Managed");
			coreHash = HashFile(Path.Combine(managed, "Mortal.Core.dll"));
			storyHash = HashFile(Path.Combine(managed, "Mortal.Story.dll"));
			string contract = ReadResource("contract.tsv.gz");
			var differences = CompatibilityContract.Differences(CompatibilityContract.Parse(contract), CompatibilityContract.Snapshot(managed));
			if (differences.Count != 0)
			{
				foreach (string difference in differences) FileLogger.LogError("[随时存档] 游戏改动：" + difference);
				string files = string.Join("、", differences.Select(d => d.Substring(0, Math.Max(0, d.IndexOf('|')))).Distinct().ToArray());
				return "存读档相关的游戏代码有冲突（" + files + "），随时存档已停用。请检查是否装了会修改游戏文件的其它 Mod，或者游戏最近有大更新、Mod 还没适配。已有的随时存档没有改动。";
			}
			// 和原版 MortalAnytimeSave 的算法一致，同一份契约下两边的存档互通
			compatibilityId = Hash("ams-format-2-replay-1\n" + contract + "\n" + Hash(ReadResource("Replay.lua")));
			FileLogger.Log("[随时存档] 版本核验通过：game=" + Application.version + " id=" + compatibilityId);
			return null;
		}
		catch (Exception ex) { return "无法核验游戏版本，随时存档已停用：" + ex.Message; }
	}

	private void ValidateSaveCompatibility(SaveFile data)
	{
		if (data == null) throw new InvalidDataException("存档内容为空");
		if (data.format == 2 && (data.compatibilityId == compatibilityId || AcceptedIds.Contains(data.compatibilityId ?? ""))) return;
		throw new InvalidDataException("这个存档来自不兼容的游戏或 Mod 版本（存档：" + data.version + "，当前：" + Application.version + "），文件没有改动");
	}
}
