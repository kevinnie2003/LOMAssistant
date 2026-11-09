using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LOMAssistant.Config;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 游戏倍速：在游戏自己设定的 Time.timeScale 上再乘一个倍率。
/// 游戏会自己改 timeScale（暂停=0、剧情快进=10、战斗加速等），所以不能直接写死，
/// 而是每帧检查：值跟我们上次写的不一样，说明是游戏改的，记下来当作基准再乘倍率。
/// 游戏写的值也可能恰好等于我们上次写的（决斗加速 2 倍、倍速也设 2 倍），光比数值分不出来，
/// 所以游戏写 timeScale 的地方都改成经过 GameSet（见 GameSpeedWriters），写过就一定重取基准。
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

	/// <summary>游戏自己写 timeScale：照写，下一次 Tick 一定把它当新基准。</summary>
	internal static void GameSet(float value)
	{
		Time.timeScale = value;
		_applied = float.NaN;
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

/// <summary>
/// 把游戏里所有写 Time.timeScale 的地方（暂停、面板开关、决斗加速、切场景、剧情快进等）改成调 GameSpeed.GameSet。
/// 启动时扫游戏的 Mortal.* 程序集找出这些方法，游戏更新后多了新地方也能覆盖到。
/// 游戏只写不读 timeScale（没有「先存下来、过后写回去」），写回的不会是乘过倍率的值。
/// </summary>
internal static class GameSpeedWriters
{
	private static readonly MethodInfo Setter = AccessTools.PropertySetter(typeof(Time), nameof(Time.timeScale));
	private static readonly string[] Assemblies = { "Mortal.Battle", "Mortal.Combat", "Mortal.Core", "Mortal.Free", "Mortal.Story" };

	public static void Patch(Harmony harmony)
	{
		var transpiler = new HarmonyMethod(typeof(GameSpeedWriters), nameof(Transpiler));
		int count = 0;
		foreach (string name in Assemblies)
		{
			Assembly assembly;
			try
			{
				assembly = Assembly.Load(name);
			}
			catch (Exception e)
			{
				FileLogger.LogError("游戏倍速：读不到 " + name + "：" + e.Message);
				continue;
			}
			foreach (Type type in AccessTools.GetTypesFromAssembly(assembly))
			{
				foreach (MethodInfo method in type.GetMethods(AccessTools.allDeclared))
				{
					if (!Writes(method))
					{
						continue;
					}
					try
					{
						harmony.Patch(method, transpiler: transpiler);
						count++;
					}
					catch (Exception e)
					{
						FileLogger.LogError("游戏倍速：接管 " + type.FullName + "." + method.Name + " 失败：" + e.Message);
					}
				}
			}
		}
		FileLogger.Log("游戏倍速：接管游戏写 timeScale 的 " + count + " 处");
	}

	private static bool Writes(MethodInfo method)
	{
		if (method.IsAbstract || method.ContainsGenericParameters || method.DeclaringType.ContainsGenericParameters)
		{
			return false;
		}
		try
		{
			foreach (KeyValuePair<OpCode, object> instruction in PatchProcessor.ReadMethodBody(method))
			{
				if (instruction.Value is MethodInfo called && called == Setter)
				{
					return true;
				}
			}
		}
		catch
		{
		}
		return false;
	}

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		MethodInfo wrapper = AccessTools.Method(typeof(GameSpeed), nameof(GameSpeed.GameSet));
		foreach (CodeInstruction instruction in instructions)
		{
			if (instruction.operand is MethodInfo called && called == Setter)
			{
				instruction.opcode = OpCodes.Call;
				instruction.operand = wrapper;
			}
			yield return instruction;
		}
	}
}
