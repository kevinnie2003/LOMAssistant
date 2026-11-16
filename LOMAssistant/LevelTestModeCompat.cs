using System.Runtime.CompilerServices;
using HarmonyLib;
using Mortal.Core;

namespace LOMAssistant;

/// <summary>
/// 游戏的关卡测试模式（战斗不自动存档、输了不进死亡结局）：1.0.6001 起才有 LevelTestMode，更早的版本没有。
/// 方法体里只要写了这个类型，旧版游戏里整个方法都编译不了（补在原生方法上的话，原生方法也跟着报错），
/// 所以只在这里、只在标了 NoInlining 的 *Now 方法里提到它，先看 Available 再调。
/// </summary>
internal static class LevelTestModeCompat
{
	internal static readonly bool Available = AccessTools.TypeByName("Mortal.Core.LevelTestMode") != null;

	/// <summary>测试模式开着；旧版游戏没有测试模式，总是 false。</summary>
	internal static bool IsActive => Available && IsActiveNow();

	internal static void Begin()
	{
		if (Available)
		{
			BeginNow();
		}
	}

	internal static void End()
	{
		if (Available)
		{
			EndNow();
		}
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static bool IsActiveNow() => LevelTestMode.IsActive;

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void BeginNow() => LevelTestMode.Begin();

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void EndNow() => LevelTestMode.End();
}
