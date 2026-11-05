using System;
using System.Collections.Generic;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 锁定心相、四种性格（性情、处世、修养、道德）、阴阳、银两、贡献度：咖啡伴侣窗口「数值」页这几行各有个小锁，点一下锁住，
/// 游戏里的剧情、选项、修炼等对它的增减都不生效；这一页的加减、设定照常能改。再点一下解锁。
/// 锁住哪几项记在配置里，重启游戏、换存档都还锁着；读档、开新档时照常读入存档里的值，之后再锁住。
/// </summary>
public static class MoodLockPatch
{
	/// <summary>能上锁的数值。</summary>
	public static readonly GameStatType[] Lockable =
	{
		GameStatType.心理衛生, GameStatType.性情, GameStatType.處世, GameStatType.修養, GameStatType.道德,
		GameStatType.陰陽內功, GameStatType.銀兩, GameStatType.個人貢獻度,
	};

	/// <summary>被锁住的数值，从配置 LockedStats 读出。</summary>
	private static HashSet<GameStatType> _locked;

	private static Texture2D _closed, _open;

	/// <summary>面板里手动加减、输入数值时置真：锁只拦游戏自己的变化，不拦玩家手动改。</summary>
	private static bool _manual;

	/// <summary>读档、开新档时大于 0：存档里的值照常读进来。</summary>
	private static int _loading;

	private static HashSet<GameStatType> Locked
	{
		get
		{
			if (_locked == null)
			{
				_locked = new HashSet<GameStatType>();
				foreach (string name in (ConfigManager.LockedStats.Value ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
				{
					if (Enum.TryParse(name.Trim(), out GameStatType type) && Array.IndexOf(Lockable, type) >= 0)
					{
						_locked.Add(type);
					}
				}
			}
			return _locked;
		}
	}

	public static bool IsLocked(GameStat stat) => Locked.Contains(stat.StatType);

	public static void Toggle(GameStat stat)
	{
		if (!Locked.Remove(stat.StatType))
		{
			Locked.Add(stat.StatType);
		}
		ConfigManager.LockedStats.Value = string.Join(",", Locked);
	}

	/// <summary>执行一次手动修改，这期间不拦截。</summary>
	public static void Manual(Action change)
	{
		_manual = true;
		try
		{
			change();
		}
		finally
		{
			_manual = false;
		}
	}

	// ---------- 拦截数值变化 ----------

	// 只拦玩家自己的数值（同类型的数值对象别处也可能有）
	private static bool Allow(GameStat stat)
	{
		if (_manual || _loading > 0 || Locked.Count == 0 || !Locked.Contains(stat.StatType))
		{
			return true;
		}
		return !ReferenceEquals(PlayerStatManagerData.Instance?.Stats?.Get(stat.StatType), stat);
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(GameStat), "AddValue")]
	public static bool AddValue_Prefix(GameStat __instance) => Allow(__instance);

	[HarmonyPrefix]
	[HarmonyPatch(typeof(GameStat), "SetValue")]
	public static bool SetValue_Prefix(GameStat __instance) => Allow(__instance);

	[HarmonyPrefix]
	[HarmonyPatch(typeof(GameStat), "Reset")]
	public static bool Reset_Prefix(GameStat __instance) => Allow(__instance);

	// 读档和开新档时放行，存档里的值照常读进来
	[HarmonyPrefix]
	[HarmonyPatch(typeof(PlayerStatManagerData), "ResetData")]
	public static void ResetData_Prefix() => _loading++;

	[HarmonyFinalizer]
	[HarmonyPatch(typeof(PlayerStatManagerData), "ResetData")]
	public static void ResetData_Finalizer() => _loading--;

	[HarmonyPrefix]
	[HarmonyPatch(typeof(PlayerStatManagerData), "Load")]
	public static void Load_Prefix() => _loading++;

	[HarmonyFinalizer]
	[HarmonyPatch(typeof(PlayerStatManagerData), "Load")]
	public static void Load_Finalizer() => _loading--;

	// ---------- 画锁（不依赖任何游戏图片或字体） ----------

	public static Texture2D Icon(bool closed)
	{
		if (_closed == null || _open == null)
		{
			_closed = DrawLock(true);
			_open = DrawLock(false);
		}
		return closed ? _closed : _open;
	}

	private static Texture2D DrawLock(bool closed)
	{
		const int n = 48;
		var pixels = new Color32[n * n];
		var white = new Color32(255, 255, 255, 255);
		float cx = 24f;
		float ringY = closed ? 26f : 32f;
		const float outer = 11f, inner = 6.5f;

		for (int y = 0; y < n; y++)
		{
			for (int x = 0; x < n; x++)
			{
				bool on = false;
				// 锁身
				if (x >= 8 && x < 40 && y >= 4 && y < 26)
				{
					on = true;
					// 钥匙孔
					float dx = x - cx, dy = y - 16f;
					if (dx * dx + dy * dy < 9f || (Mathf.Abs(dx) < 1.8f && y >= 8 && y < 16))
					{
						on = false;
					}
				}
				else
				{
					// 锁环：上半圆 + 左腿 + （锁上时）右腿
					float dx = x + 0.5f - cx, dy = y + 0.5f - ringY;
					float d = Mathf.Sqrt(dx * dx + dy * dy);
					if (dy >= 0f && d <= outer && d >= inner)
					{
						on = true;
					}
					else if (dy < 0f && y >= 24)
					{
						bool leftLeg = dx <= -inner && dx >= -outer;
						bool rightLeg = dx >= inner && dx <= outer;
						on = leftLeg || (closed && rightLeg);
					}
				}
				if (on)
				{
					pixels[y * n + x] = white;
				}
			}
		}
		var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
		tex.SetPixels32(pixels);
		tex.Apply();
		return tex;
	}
}
