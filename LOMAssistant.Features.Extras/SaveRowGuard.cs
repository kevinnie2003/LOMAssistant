using System;
using System.Collections.Generic;
using HarmonyLib;
using Mortal.Core;
using UnityEngine;
using UnityEngine.UI;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 一个存档显示出错，不连累整张存档列表。
/// 游戏建手动存档列表时一格一格生成、生成完马上显示（CreateSlot 里循环调 Setup）；
/// 有一格读存档或显示日期时报错，循环就停了，后面的栏位一个都没有，最近存档那格也是空白。
/// 这里接住栏位 Setup 的报错：读不出来的存档标成「无法读取的存档」，点它不读也不存（不覆盖那个文件），可以删除；
/// 读得出来、只是信息显示不了的，照常能读。存档文件本身不动。
/// </summary>
public static class SaveRowGuard
{
	/// <summary>读不出来的手动存档栏位。</summary>
	private static readonly HashSet<string> Unreadable = new HashSet<string>(StringComparer.Ordinal);
	private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);

	[HarmonyFinalizer]
	[HarmonyPatch(typeof(SaveSlotPanel), "Setup")]
	public static Exception SaveSlotPanel_Setup_Finalizer(SaveSlotPanel __instance, Exception __exception) => Repair(__instance, __exception, false);

	[HarmonyFinalizer]
	[HarmonyPatch(typeof(LoadSlotPanel), "Setup")]
	public static Exception LoadSlotPanel_Setup_Finalizer(LoadSlotPanel __instance, Exception __exception) => Repair(__instance, __exception, false);

	[HarmonyFinalizer]
	[HarmonyPatch(typeof(RecentSaveSlotPanel), "Setup")]
	public static Exception RecentSaveSlotPanel_Setup_Finalizer(RecentSaveSlotPanel __instance, Exception __exception) => Repair(__instance, __exception, false);

	[HarmonyFinalizer]
	[HarmonyPatch(typeof(AutoSaveSlotPanel), "Setup")]
	public static Exception AutoSaveSlotPanel_Setup_Finalizer(AutoSaveSlotPanel __instance, Exception __exception) => Repair(__instance, __exception, true);

	// 读不出来的存档：点了不读档、不开新游戏、不往里存
	[HarmonyPrefix]
	[HarmonyPatch(typeof(LoadSlotPanel), "OnTitleClick")]
	public static bool LoadSlotPanel_OnTitleClick_Prefix(LoadSlotPanel __instance) => AllowClick(__instance);

	[HarmonyPrefix]
	[HarmonyPatch(typeof(SaveSlotPanel), "OnTitleClick")]
	public static bool SaveSlotPanel_OnTitleClick_Prefix(SaveSlotPanel __instance) => AllowClick(__instance);

	[HarmonyPrefix]
	[HarmonyPatch(typeof(RecentSaveSlotPanel), "OnTitleClick")]
	public static bool RecentSaveSlotPanel_OnTitleClick_Prefix(RecentSaveSlotPanel __instance) => AllowClick(__instance);

	private static Exception Repair(MonoBehaviour row, Exception error, bool auto)
	{
		if (error == null || row == null)
		{
			return error;
		}
		try
		{
			var tr = Traverse.Create(row);
			string slot = tr.Field("_slot").GetValue<string>();
			bool exists = false;
			string reason = null;
			GameSave data = null;
			try
			{
				string file = Traverse.Create(SaveSystem.Instance).Method("GetSaveDataFile", new[] { typeof(string) }).GetValue<string>(slot);
				exists = !string.IsNullOrEmpty(slot) && System.IO.File.Exists(file);
				if (exists)
				{
					data = auto ? SaveSystem.Instance.GetAutoSaveData(slot)?.GameSave : SaveSystem.Instance.GetSaveData(slot);
				}
			}
			catch (Exception e)
			{
				reason = e.GetType().Name + ": " + e.Message;
			}
			if (Reported.Add(row.GetType().Name + "|" + slot + "|" + error.GetType().Name))
			{
				FileLogger.LogError($"[存档列表] 栏位 {slot}（{row.GetType().Name}）显示出错：{error.GetType().Name}: {error.Message}"
					+ (reason != null ? $"；存档文件读不出来（{reason}），标成无法读取" : exists ? "；存档文件本身能读" : "")
					+ " | " + FirstFrame(error));
			}
			if (!exists)
			{
				return null;
			}
			if (reason != null || data == null)
			{
				if (!auto)
				{
					Unreadable.Add(slot);
				}
				SetTexts(tr, "_titleText", TextConverter.ForDisplay("无法读取的存档"));
				SetTexts(tr, "_timeText", auto ? "" : TextConverter.ForDisplay("可以删除，或换一格存档"));
			}
			else
			{
				// 文件能读，只是日期、时段显示不出来：用简单的文字，照常能读
				Unreadable.Remove(slot);
				SetTexts(tr, "_titleText", TextConverter.ForDisplay("存档信息无法显示"));
				SetTexts(tr, "_timeText", data.TimeTick > 0 && data.TimeTick <= DateTime.MaxValue.Ticks ? new DateTime(data.TimeTick).ToString("yyyy/MM/dd HH:mm:ss") : "");
			}
			SetActive(tr, "_newGamePlusIcon", false);
			SetActive(tr, "_deleteButton", true);
			// 不是空栏位：删除键能删它
			LoadGameSlot holder = tr.Field("_loadGameSlot").GetValue<LoadGameSlot>();
			if (holder != null)
			{
				holder.IsEmpty = false;
			}
		}
		catch (Exception e)
		{
			FileLogger.LogError("[存档列表] 修补出错的栏位失败：" + e.Message);
		}
		return null;
	}

	private static bool AllowClick(MonoBehaviour row)
	{
		string slot = Traverse.Create(row).Field("_slot").GetValue<string>();
		if (string.IsNullOrEmpty(slot) || !Unreadable.Contains(slot))
		{
			return true;
		}
		try
		{
			string file = Traverse.Create(SaveSystem.Instance).Method("GetSaveDataFile", new[] { typeof(string) }).GetValue<string>(slot);
			if (!System.IO.File.Exists(file))
			{
				Unreadable.Remove(slot);
				return true;
			}
		}
		catch
		{
		}
		FileLogger.Log($"[存档列表] 栏位 {slot} 的存档读不出来，没有读档也没有覆盖");
		return false;
	}

	private static void SetTexts(Traverse row, string field, string text)
	{
		object value = row.Field(field).GetValue();
		if (value is Text[] many)
		{
			foreach (Text t in many)
			{
				if (t != null) t.text = text;
			}
		}
		else if (value is Text one)
		{
			one.text = text;
		}
	}

	private static void SetActive(Traverse row, string field, bool active)
	{
		object value = row.Field(field).GetValue();
		GameObject go = value as GameObject ?? (value as Component)?.gameObject;
		if (go != null)
		{
			go.SetActive(active);
		}
	}

	private static string FirstFrame(Exception error)
	{
		string trace = error.StackTrace ?? "";
		int end = trace.IndexOf('\n');
		return (end < 0 ? trace : trace.Substring(0, end)).Trim();
	}
}
