using System.Collections.Generic;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Combat;
using Mortal.Core;
using UnityEngine;
using UnityEngine.UI;

namespace LOMAssistant.Features.Combat;

/// <summary>
/// 战斗时左上角的「技能 / 特殊」列表：整列上移到屏幕顶部，并在内容超出可用高度时等比缩小，保证全部显示在屏幕内。
/// 两条途径找到列表：①CombatSkillTextPanel.Setup 钩子；②SkillListScanner 每秒按「技能/特殊」文字反查（不依赖游戏用哪个函数生成）。
/// </summary>
internal static class SkillShrinkPatch
{
	[HarmonyPostfix]
	[HarmonyPatch(typeof(CombatSkillTextPanel), "Setup")]
	public static void OnItemCreated(CombatSkillTextPanel __instance)
	{
		try
		{
			if (ConfigManager.EnableCombatSkillShrink.Value && __instance.transform.parent is RectTransform container)
			{
				SkillShrinker.Ensure(container);
			}
		}
		catch (System.Exception ex)
		{
			FileLogger.LogError("[技能缩小] " + ex);
		}
	}
}

internal class SkillListScanner : MonoBehaviour
{
	private float next;

	public static void Ensure(GameObject host)
	{
		if (host.GetComponent<SkillListScanner>() == null)
		{
			host.AddComponent<SkillListScanner>();
		}
	}

	private void Update()
	{
		if (Time.unscaledTime < next)
		{
			return;
		}
		next = Time.unscaledTime + 1f;
		try
		{
			if (!ConfigManager.EnableCombatSkillShrink.Value)
			{
				return;
			}
			Scan();
		}
		catch (System.Exception ex)
		{
			FileLogger.LogError("[技能缩小] 扫描 " + ex);
		}
	}

	private static void Scan()
	{
		foreach (VerticalLayoutGroup g in Object.FindObjectsOfType<VerticalLayoutGroup>())
		{
			if (g != null && g.gameObject.activeInHierarchy)
			{
				SkillShrinker.Ensure((RectTransform)g.transform);
			}
		}
	}
}

internal class SkillShrinker : MonoBehaviour
{
	private const float TopMargin = 0.015f;

	private const float BottomReserve = 0.13f;

	private const float MinScale = 0.25f;

	private int logCount;

	private float lastScale = 1f;

	public static void Ensure(RectTransform container)
	{
		if (container.name != "BuffContainer" || container.parent == null || container.parent.name != "PlayerStatus" || container.GetComponent<SkillShrinker>() != null)
		{
			return;
		}
		LayoutGroup lg = container.GetComponent<LayoutGroup>();
		FileLogger.Log("[技能缩小] 接管列表容器：" + Path(container) + " 布局=" + (lg == null ? "无" : lg.GetType().Name) + " 尺寸=" + container.rect.size + " 枢轴=" + container.pivot + " 子项=" + container.childCount);
		Vector2 topLeft = new Vector2(0f, 1f);
		if (container.pivot != topLeft)
		{
			Vector2 shift = Vector2.Scale(topLeft - container.pivot, Vector2.Scale(container.rect.size, container.localScale));
			container.pivot = topLeft;
			container.anchoredPosition += shift;
		}
		container.gameObject.AddComponent<SkillShrinker>();
	}

	private static string Path(Transform t)
	{
		string path = t.name;
		while (t.parent != null)
		{
			t = t.parent;
			path = t.name + "/" + path;
		}
		return path;
	}

	private void LateUpdate()
	{
		RectTransform rt = (RectTransform)transform;
		ArrangeColumns(rt, ConfigManager.EnableCombatSkillShrink.Value);
		if (!ConfigManager.EnableCombatSkillShrink.Value)
		{
			Apply(1f);
			return;
		}
		Canvas canvas = GetComponentInParent<Canvas>();
		if (canvas == null)
		{
			return;
		}
		RectTransform canvasRect = canvas.rootCanvas.transform as RectTransform;
		if (canvasRect == null)
		{
			return;
		}
		float top = float.MinValue;
		float bottom = float.MaxValue;
		for (int i = 0; i < rt.childCount; i++)
		{
			RectTransform child = rt.GetChild(i) as RectTransform;
			if (child == null || !child.gameObject.activeSelf)
			{
				continue;
			}
			Bounds b = RectTransformUtility.CalculateRelativeRectTransformBounds(rt, child);
			if (b.max.y > top)
			{
				top = b.max.y;
			}
			if (b.min.y < bottom)
			{
				bottom = b.min.y;
			}
		}
		if (top == float.MinValue)
		{
			Apply(1f);
			return;
		}
		float unitToWorld = rt.lossyScale.y / Mathf.Max(0.0001f, rt.localScale.y);
		float contentWorld = (top - bottom) * unitToWorld;
		Vector3[] corners = new Vector3[4];
		canvasRect.GetWorldCorners(corners);
		float screenBottom = corners[0].y;
		float screenTop = corners[1].y;
		float screenH = screenTop - screenBottom;
		float targetTop = screenTop - screenH * TopMargin;
		float limitBottom = screenBottom + screenH * BottomReserve;
		float available = targetTop - limitBottom;
		float scale = contentWorld <= 0.0001f || available <= 0f ? 1f : Mathf.Clamp(available / contentWorld, MinScale, 1f);
		Apply(scale);
		// 把列表内容顶端移到屏幕顶部（以容器左上角为基准）
		float pivotY = targetTop - top * scale * unitToWorld;
		Vector3 pos = rt.position;
		if (Mathf.Abs(pos.y - pivotY) > 0.0005f)
		{
			rt.position = new Vector3(pos.x, pivotY, pos.z);
		}
		if (logCount < 6 && Mathf.Abs(scale - lastLogged) > 0.01f)
		{
			logCount++;
			lastLogged = scale;
			FileLogger.Log("[技能缩小] 条目数=" + rt.childCount + " 内容高=" + contentWorld.ToString("F1") + " 可用高=" + available.ToString("F1") + " 缩放=" + scale.ToString("F2"));
		}
	}

	private float lastLogged = 1f;

	private const int ColumnMax = 14;

	private const float ColumnGap = 6f;

	private readonly List<RectTransform> active = new List<RectTransform>();

	// 每列最多 14 条，先填满左边再填下一列：前 14 条仍交给竖向布局，其余条目脱离布局，按「同一行」复制第一列的位置并向右平移
	private void ArrangeColumns(RectTransform rt, bool enabled)
	{
		active.Clear();
		for (int i = 0; i < rt.childCount; i++)
		{
			RectTransform child = rt.GetChild(i) as RectTransform;
			if (child != null && child.gameObject.activeSelf)
			{
				active.Add(child);
			}
		}
		for (int i = 0; i < active.Count; i++)
		{
			RectTransform child = active[i];
			bool overflow = enabled && i >= ColumnMax;
			LayoutElement le = child.GetComponent<LayoutElement>();
			if (le == null)
			{
				if (!overflow)
				{
					continue;
				}
				le = child.gameObject.AddComponent<LayoutElement>();
			}
			if (le.ignoreLayout != overflow)
			{
				le.ignoreLayout = overflow;
			}
			if (!overflow)
			{
				continue;
			}
			RectTransform reference = active[i % ColumnMax];
			int col = i / ColumnMax;
			child.anchorMin = reference.anchorMin;
			child.anchorMax = reference.anchorMax;
			child.pivot = reference.pivot;
			child.sizeDelta = reference.sizeDelta;
			child.anchoredPosition = reference.anchoredPosition + new Vector2(col * (reference.rect.width + ColumnGap), 0f);
		}
	}

	private void Apply(float scale)
	{
		lastScale = scale;
		if (Mathf.Abs(transform.localScale.x - scale) > 0.002f)
		{
			transform.localScale = new Vector3(scale, scale, 1f);
		}
	}
}
