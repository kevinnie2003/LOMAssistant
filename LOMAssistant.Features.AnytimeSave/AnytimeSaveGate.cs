using System;
using System.Collections.Generic;

namespace LOMAssistant.Api;

/// <summary>一个随时存档记着的位置，给 <see cref="AnytimeSaveGate"/> 的读档判断用。</summary>
public sealed class AnytimeSaveInfo
{
	/// <summary>存档时游戏用的原版存档栏位（SaveSystem.CurrentSlot）。</summary>
	public string NativeSlot { get; internal set; }

	/// <summary>这段剧情开头的剧本名（不带 .LuaScript）。</summary>
	public string RootScript { get; internal set; }

	/// <summary>存档时游戏记的当前剧本（PlayerStatManagerData.CurrentStoryScript）。</summary>
	public string CurrentScript { get; internal set; }

	/// <summary>存档时的场景：Story、Free 或 Combat。</summary>
	public string Scene { get; internal set; }
}

/// <summary>
/// 给其他 Mod 用的接口：本 Mod 的随时存档还原不了别的 Mod 自己记的状态时，由那个 Mod 登记在什么情况下停用随时存档。
/// 用法（其他 Mod 软依赖本 Mod，插件 GUID「LOMAssistant」）：
/// <code>
/// AnytimeSaveGate.Register("com.example.mymod",
///     blocked: () =&gt; InMyMode ? "当前是 MOD 进度，请用 MOD 的存读档" : null,
///     loadBlocked: info =&gt; IsMySlot(info.NativeSlot) ? "这个随时存档是 MOD 进度，读不了" : null);
/// </code>
/// blocked 返回非空时：不能存随时存档、不能读随时存档、不能回退到判定前，剧情也不再为随时存档做记录（随机事件自选照常）；
/// 返回的文字直接显示给玩家。loadBlocked 按要读的那个随时存档判断，返回非空就不读。两个都可以是 null。
/// 回调出错按没拦处理，并记日志。
/// </summary>
public static class AnytimeSaveGate
{
	private sealed class Gate
	{
		internal Func<string> Blocked;
		internal Func<AnytimeSaveInfo, string> LoadBlocked;
	}

	private static readonly Dictionary<string, Gate> Gates = new Dictionary<string, Gate>(StringComparer.Ordinal);

	/// <summary>登记（同一个 owner 再登记会替换掉之前的）。owner 一般用插件 GUID。</summary>
	public static void Register(string owner, Func<string> blocked, Func<AnytimeSaveInfo, string> loadBlocked = null)
	{
		if (string.IsNullOrEmpty(owner))
		{
			throw new ArgumentException("owner 不能为空", nameof(owner));
		}
		lock (Gates)
		{
			Gates[owner] = new Gate { Blocked = blocked, LoadBlocked = loadBlocked };
		}
		FileLogger.Log("[随时存档] " + owner + " 登记了停用条件");
	}

	public static void Unregister(string owner)
	{
		if (owner == null)
		{
			return;
		}
		lock (Gates)
		{
			Gates.Remove(owner);
		}
	}

	/// <summary>现在有没有 Mod 要停用随时存档；有就返回原因。</summary>
	internal static string BlockedReason()
	{
		foreach (var (owner, gate) in Snapshot())
		{
			string reason = Ask(owner, gate.Blocked);
			if (reason != null)
			{
				return reason;
			}
		}
		return null;
	}

	/// <summary>这个随时存档有没有 Mod 不让读；有就返回原因。</summary>
	internal static string LoadBlockedReason(AnytimeSaveInfo info)
	{
		foreach (var (owner, gate) in Snapshot())
		{
			string reason = Ask(owner, gate.Blocked) ?? (gate.LoadBlocked == null ? null : Ask(owner, () => gate.LoadBlocked(info)));
			if (reason != null)
			{
				return reason;
			}
		}
		return null;
	}

	private static List<(string, Gate)> Snapshot()
	{
		lock (Gates)
		{
			var list = new List<(string, Gate)>(Gates.Count);
			foreach (KeyValuePair<string, Gate> pair in Gates)
			{
				list.Add((pair.Key, pair.Value));
			}
			return list;
		}
	}

	private static string Ask(string owner, Func<string> question)
	{
		if (question == null)
		{
			return null;
		}
		try
		{
			string reason = question();
			return string.IsNullOrWhiteSpace(reason) ? null : reason;
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[随时存档] " + owner + " 的停用条件出错，按没拦处理：" + e.Message);
			return null;
		}
	}
}
