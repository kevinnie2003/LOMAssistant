using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Core;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 开新档的「继承」页：点「开始」时记下每一项加了多少，下次在这一页点「沿用上次」，
/// 照记录逐格点游戏自己的加减号。命运点数照扣、上限照卡，点数不够时点到用完为止，点完还能自己调。
/// 记录存在 Mod 自己的文件里，不碰存档。
/// </summary>
public static class FateBonusRepeat
{
	private const string ButtonName = "LOMAssistant_FateBonusRepeat";
	private const string Label = "沿用上次";

	private static readonly FieldInfo CurrentAdd = AccessTools.Field(typeof(FateBonusItem), "_currentAdd");
	private static readonly MethodInfo AllItems = AccessTools.Method(typeof(FateBonusPanel), "GetAllItems");

	private static string RecordPath => Path.Combine(Paths.ConfigPath, "LOMAssistant_FateBonus.json");

	private static Dictionary<string, int> _record;

	private static Dictionary<string, int> Record
	{
		get
		{
			if (_record == null)
			{
				_record = new Dictionary<string, int>();
				try
				{
					if (File.Exists(RecordPath))
					{
						_record = JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(RecordPath, Encoding.UTF8)) ?? _record;
					}
				}
				catch (Exception e)
				{
					FileLogger.LogWarning("继承分配：读取记录失败: " + e.Message);
				}
			}
			return _record;
		}
	}

	// 「开始」把加的点写进数值之前记下来（之后游戏会把每项加的点清零）
	[HarmonyPrefix]
	[HarmonyPatch(typeof(FateBonusPanel), nameof(FateBonusPanel.ConfirmStart))]
	public static void ConfirmStart_Prefix(FateBonusPanel __instance, bool ____isFinished)
	{
		if (____isFinished)
		{
			return;
		}
		try
		{
			var record = new Dictionary<string, int>();
			foreach (FateBonusItem item in Items(__instance))
			{
				int add = (int)CurrentAdd.GetValue(item);
				string key = Key(item);
				if (add != 0 && key != null)
				{
					record[key] = add;
				}
			}
			// 一点都没加就不覆盖上次的记录
			if (record.Count == 0)
			{
				return;
			}
			_record = record;
			string tmp = RecordPath + ".tmp";
			File.WriteAllText(tmp, JsonConvert.SerializeObject(record, Formatting.Indented), Encoding.UTF8);
			if (File.Exists(RecordPath))
			{
				File.Delete(RecordPath);
			}
			File.Move(tmp, RecordPath);
			FileLogger.Log($"继承分配：记下 {record.Count} 项");
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("继承分配：记录失败: " + e.Message);
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(FateBonusPanel), nameof(FateBonusPanel.OnPanelOpen))]
	public static void OnPanelOpen_Postfix(FateBonusPanel __instance)
	{
		try
		{
			Button button = FindButton(__instance);
			if (button == null)
			{
				if (!ConfigManager.FateBonusRepeat.Value)
				{
					return;
				}
				button = AddButton(__instance);
				if (button == null)
				{
					return;
				}
			}
			button.gameObject.SetActive(ConfigManager.FateBonusRepeat.Value);
			button.interactable = Record.Count > 0;
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("继承分配：添加按钮失败: " + e.Message);
		}
	}

	private static Button FindButton(FateBonusPanel panel)
	{
		foreach (Button b in panel.GetComponentsInChildren<Button>(includeInactive: true))
		{
			if (b.name == ButtonName)
			{
				return b;
			}
		}
		return null;
	}

	// 复制「重置」按钮（没有就复制「开始」），放在它旁边
	private static Button AddButton(FateBonusPanel panel)
	{
		Button template = FindByMethod(panel, "ResetAll") ?? FindByMethod(panel, "OpenStartConfirm");
		if (template == null)
		{
			FileLogger.LogWarning("继承分配：继承页里没找到「重置」「开始」按钮，不加按钮。");
			return null;
		}
		bool laidOut = template.transform.parent.GetComponent<LayoutGroup>() != null;
		Button button = MenuButtonHelper.CloneButton(template, Label, ButtonName);
		if (!laidOut)
		{
			// 没有自动排版：放在模板按钮正上方，不叠在一起
			var rt = (RectTransform)button.transform;
			rt.anchoredPosition += new Vector2(0f, ((RectTransform)template.transform).rect.height * 1.25f);
		}
		// 按钮按两个字排的，四个字放不下：字号自动缩小到放得下
		foreach (Text t in button.GetComponentsInChildren<Text>(includeInactive: true))
		{
			t.resizeTextForBestFit = true;
			t.resizeTextMinSize = 10;
			t.resizeTextMaxSize = t.fontSize;
			t.horizontalOverflow = HorizontalWrapMode.Wrap;
		}
		foreach (TMP_Text t in button.GetComponentsInChildren<TMP_Text>(includeInactive: true))
		{
			t.enableAutoSizing = true;
			t.fontSizeMax = t.fontSize;
			t.fontSizeMin = 10f;
		}
		button.onClick.AddListener(() => Apply(panel));
		FileLogger.Log("继承分配：已加入按钮 " + MenuButtonHelper.DescribeHierarchy(button.transform) + "，模板 " + template.name + (laidOut ? "" : "（手动摆放）"));
		return button;
	}

	private static Button FindByMethod(FateBonusPanel panel, string method)
	{
		foreach (Button b in panel.GetComponentsInChildren<Button>(includeInactive: true))
		{
			if (b.name == ButtonName)
			{
				continue;
			}
			UnityEvent click = b.onClick;
			for (int i = 0; i < click.GetPersistentEventCount(); i++)
			{
				if (click.GetPersistentMethodName(i) == method)
				{
					return b;
				}
			}
		}
		return null;
	}

	/// <summary>先重置，再照记录逐格点游戏自己的加减号；点不动（点数用完、到了上限）就换下一项。</summary>
	private static void Apply(FateBonusPanel panel)
	{
		try
		{
			panel.ResetAll();
			int done = 0, partial = 0;
			foreach (FateBonusItem item in Items(panel))
			{
				string key = Key(item);
				if (key == null || !Record.TryGetValue(key, out int target) || target == 0)
				{
					continue;
				}
				int dir = Math.Sign(target);
				for (int guard = 0; guard < 1000; guard++)
				{
					int now = (int)CurrentAdd.GetValue(item);
					if ((target - now) * dir <= 0)
					{
						break;
					}
					if (dir > 0)
					{
						item.AddPoint();
					}
					else
					{
						item.MinusPoint();
					}
					if ((int)CurrentAdd.GetValue(item) == now)
					{
						break;
					}
				}
				if ((int)CurrentAdd.GetValue(item) == target)
				{
					done++;
				}
				else
				{
					partial++;
				}
			}
			panel.UpdateAllModifyButton();
			FileLogger.Log($"继承分配：沿用上次，{done} 项点满，{partial} 项没点到（点数不够或到了上限）");
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("继承分配：沿用失败: " + e);
		}
	}

	private static IEnumerable<FateBonusItem> Items(FateBonusPanel panel)
	{
		var list = new List<FateBonusItem>();
		if (AllItems?.Invoke(panel, null) is IEnumerable items)
		{
			foreach (object o in items)
			{
				if (o is FateBonusItem item && item != null)
				{
					list.Add(item);
				}
			}
		}
		return list;
	}

	// 按项目对应的数值、好感、旗标资源的名字记，和界面语言无关
	private static string Key(FateBonusItem item)
	{
		object stat = AccessTools.Field(item.GetType(), "_stat")?.GetValue(item);
		string name = (stat as UnityEngine.Object)?.name;
		return string.IsNullOrEmpty(name) ? null : item.GetType().Name + "/" + name;
	}
}
