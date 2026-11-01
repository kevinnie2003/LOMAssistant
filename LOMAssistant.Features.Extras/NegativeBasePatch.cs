using System.Reflection;
using HarmonyLib;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 数值调到加成以下：游戏的数值是「底数 + 加成（秘笈等）」，底数被限制在下限（一般是 0）以上，
/// 所以学了秘笈的数值最低只能到加成那么多。数值页设定时允许底数为负（<see cref="SetFinal"/>），最终值也能是负数
/// （游戏的最终值本来卡在下限以上，底数为负时不卡）。
/// 底数已经是负数时，游戏加减、设值不再把它拉回下限：加多少就从负数往上加；往下扣不会比现在更低。
/// 读档时存档里的负数底数照样读进来。底数没变成负数的数值完全按原版。
/// 只管主角自己的数值（数值页能改的那些）：别的数值（敌方势力的默认血量等）有的本来就是底数 0、靠下限抬到 200，
/// 按负数处理会变成 0（战役敌人一出生就死，直接获胜），这些一律按原版。
/// </summary>
public static class NegativeBasePatch
{
	private static readonly AccessTools.FieldRef<GameStat, int> ValueField = AccessTools.FieldRefAccess<GameStat, int>("_value");
	private static readonly MethodInfo Raise = AccessTools.Method(typeof(GameStat), "RaiseValueChangedEvent");

	/// <summary>读档时大于 0：存档里的负数底数照样读进来。</summary>
	private static int _loading;

	// 主角自己的数值（PlayerStatData 里的那份，整局不换对象）；第一次用时记下来
	private static readonly System.Collections.Generic.HashSet<GameStat> _playerStats = new System.Collections.Generic.HashSet<GameStat>();

	private static float _nextTry;

	private static bool IsPlayerStat(GameStat stat)
	{
		// 最终值每帧要算很多次：还没找到主角的数值时最多每秒找一次
		if (_playerStats.Count == 0 && Time.realtimeSinceStartup >= _nextTry)
		{
			_nextTry = Time.realtimeSinceStartup + 1f;
			try
			{
				var list = Traverse.Create(PlayerStatManagerData.Instance?.Stats).Field("_list").GetValue<System.Collections.IEnumerable>();
				if (list != null)
				{
					foreach (object o in list)
					{
						if (o is GameStat g)
						{
							_playerStats.Add(g);
						}
					}
				}
			}
			catch
			{
			}
		}
		return _playerStats.Contains(stat);
	}

	/// <summary>
	/// 数值页设定最终值：底数够用时走游戏自己的加减；要低于加成时直接写负数底数（最终值可以是负数，最低 -9999）。
	/// </summary>
	public static void SetFinal(GameStat stat, int target)
	{
		int addition = stat.AdditionValue;
		if (target - addition >= stat.Min)
		{
			stat.AddValue(target - stat.FinalValue, totalAdd: false);
			return;
		}
		int old = ValueField(stat);
		ValueField(stat) = Mathf.Max(target, -9999) - addition;
		Raise?.Invoke(stat, new object[] { old, ValueField(stat) });
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(GameStat), "AddValue")]
	[HarmonyPatch(typeof(GameStat), "SetValue")]
	public static void Before(GameStat __instance, out int __state) => __state = ValueField(__instance);

	// 被别的补丁拦下（比如数值锁）时原方法没跑，这里也不动
	[HarmonyPostfix]
	[HarmonyPatch(typeof(GameStat), "AddValue")]
	public static void AddValue_Postfix(GameStat __instance, int value, int __state, bool __runOriginal)
	{
		if (__runOriginal)
		{
			Keep(__instance, __state, __state + value);
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(GameStat), "SetValue")]
	public static void SetValue_Postfix(GameStat __instance, int value, int __state, bool __runOriginal)
	{
		if (!__runOriginal)
		{
			return;
		}
		if (_loading > 0 && value < __instance.Min && IsPlayerStat(__instance))
		{
			ValueField(__instance) = value;
			return;
		}
		Keep(__instance, __state, value);
	}

	// 原来就是负数底数：结果不低于原来的值，还在下限以下就保留（游戏刚把它截成了下限）
	private static void Keep(GameStat stat, int old, int wanted)
	{
		if (old >= stat.Min || !IsPlayerStat(stat))
		{
			return;
		}
		int value = Mathf.Max(wanted, old);
		if (value < stat.Min)
		{
			ValueField(stat) = value;
		}
	}

	// 最终值 = 底数 + 加成，游戏再卡在下限以上；底数是负数时不卡，最终值照算（可以是负数）
	[HarmonyPostfix]
	[HarmonyPatch(typeof(GameStat), "FinalValue", MethodType.Getter)]
	public static void FinalValue_Postfix(GameStat __instance, ref int __result)
	{
		int value = ValueField(__instance);
		if (value < __instance.Min && IsPlayerStat(__instance))
		{
			__result = Mathf.Min(__result, value + __instance.AdditionValue);
		}
	}

	// 等级文字按「数值 ÷ 每级多少」查表，负数会查到表外：按最低一级算
	[HarmonyPostfix]
	[HarmonyPatch(typeof(GameStatUtils), "GetGameStatLevel")]
	public static void GetGameStatLevel_Postfix(ref int __result)
	{
		if (__result < 0)
		{
			__result = 0;
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(PlayerStatManagerData), "Load")]
	public static void Load_Prefix() => _loading++;

	[HarmonyFinalizer]
	[HarmonyPatch(typeof(PlayerStatManagerData), "Load")]
	public static void Load_Finalizer() => _loading--;
}
