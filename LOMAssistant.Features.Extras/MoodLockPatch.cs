using System;
using HarmonyLib;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 锁定心相：咖啡伴侣面板「数值」页的「心相」一行有个小锁，点一下锁住，心相数值就不再变化（增减都拦，面板里的加减按钮也置灰）；再点一下解锁。
/// 锁定状态不保存：重启游戏、读档、开新档都会自动解锁（不然读档时旧值会留在新档里）。
/// </summary>
public static class MoodLockPatch
{
	/// <summary>被锁住的心相属性；为空表示没锁。</summary>
	private static GameStat _locked;

	private static Texture2D _closed, _open;

	/// <summary>面板里手动加减、输入数值时置真：锁只拦游戏自己的变化，不拦玩家手动改。</summary>
	private static bool _manual;

	public static bool IsLocked => _locked != null;

	public static void Toggle(GameStat stat)
	{
		_locked = _locked == null ? stat : null;
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

	private static bool Allow(GameStat stat) => _locked == null || _manual || stat != _locked;

	[HarmonyPrefix]
	[HarmonyPatch(typeof(GameStat), "AddValue")]
	public static bool AddValue_Prefix(GameStat __instance) => Allow(__instance);

	[HarmonyPrefix]
	[HarmonyPatch(typeof(GameStat), "SetValue")]
	public static bool SetValue_Prefix(GameStat __instance) => Allow(__instance);

	[HarmonyPrefix]
	[HarmonyPatch(typeof(GameStat), "Reset")]
	public static bool Reset_Prefix(GameStat __instance) => Allow(__instance);

	// 读档和重置前先解锁，免得旧存档的心相带进新存档
	[HarmonyPrefix]
	[HarmonyPatch(typeof(PlayerStatManagerData), "ResetData")]
	public static void ResetData_Prefix() => _locked = null;

	[HarmonyPrefix]
	[HarmonyPatch(typeof(PlayerStatManagerData), "Load")]
	public static void Load_Prefix() => _locked = null;

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
