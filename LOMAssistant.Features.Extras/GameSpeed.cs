using HarmonyLib;
using LOMAssistant.Config;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 游戏倍速：在游戏自己设定的 Time.timeScale 上再乘一个倍率。
/// 游戏会自己改 timeScale（暂停=0、剧情快进=10、战斗加速等），所以不能直接写死，
/// 而是每帧检查：值跟我们上次写的不一样，说明是游戏改的，记下来当作基准再乘倍率。
/// 暂停（0）乘完还是 0，不受影响。
/// 战役（Battle 场景）和剧情里自动往下走的台词（剧本写成不等点击、停一下就换下一句）按原速：
/// 前者倍速下来不及看清、操作，后者一闪而过。
/// </summary>
public static class GameSpeed
{
	public static readonly float[] Options = { 1f, 2f, 3f, 5f };

	private static float _base = 1f;
	private static float _applied = -1f;

	/// <summary>正在放不等点击、自己往下走的台词（从这样的一句开始，到下一句要点击的台词、弹出选项为止）。</summary>
	internal static bool AutoSay;

	public static void Tick()
	{
		float mult = Exempt() ? 1f : Mathf.Clamp(ConfigManager.GameSpeed.Value, 0.1f, 10f);
		float current = Time.timeScale;
		if (!Mathf.Approximately(current, _applied))
		{
			_base = current; // 游戏自己改的
		}
		float target = _base * mult * FlowSpeedup.FreeBoost();
		if (!Mathf.Approximately(current, target))
		{
			Time.timeScale = target;
		}
		_applied = target;
	}

	private static bool Exempt()
	{
		string scene = SceneManager.GetActiveScene().name;
		// 战役默认按原速（设置「倍速作用于战役」打开时照样加速）
		if (scene == "Battle" && !ConfigManager.GameSpeedInBattle.Value)
		{
			return true;
		}
		if (AutoSay && scene != "Story")
		{
			AutoSay = false;
		}
		return AutoSay;
	}

	public static void Set(float mult)
	{
		ConfigManager.GameSpeed.Value = mult;
		ConfigManager.SetConfigValue(ConfigManager.GameSpeed, mult);
		FileLogger.Log($"游戏倍速 -> {mult}x");
	}
}

/// <summary>剧本说一句台词时记下这句等不等点击（sayoptions.waitforinput）；弹出选项时自动往下走的那几句就结束了。</summary>
internal static class GameSpeedSayPatch
{
	// 剧本的 say() 直接调 DoSay（Say 也是转调它）
	[HarmonyPrefix]
	[HarmonyPatch(typeof(Fungus.SayDialog), nameof(Fungus.SayDialog.DoSay))]
	public static void DoSay_Prefix(bool waitForInput) => GameSpeed.AutoSay = !waitForInput;

	[HarmonyPrefix]
	[HarmonyPatch(typeof(Fungus.MenuDialog), nameof(Fungus.MenuDialog.AddOption), new[] { typeof(string), typeof(bool), typeof(Fungus.LuaEnvironment), typeof(MoonSharp.Interpreter.Closure) })]
	public static void AddOption_Prefix() => GameSpeed.AutoSay = false;
}
