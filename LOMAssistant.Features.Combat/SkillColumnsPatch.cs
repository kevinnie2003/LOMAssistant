using System.Collections.Generic;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Combat;
using UnityEngine;
using UnityEngine.UI;

namespace LOMAssistant.Features.Combat;

/// <summary>
/// 技能表分列显示（思路来自爱死的 PR #9，默认关）：战斗时左上角我方的「技能 / 特殊」列表条目太多会超出屏幕、压住血条。
/// 游戏 1.0.6001.2 起自己会挤压这个列表（条目重叠到只露名字，再整列缩小）；打开这一项时关掉游戏的挤压，改用分列，关上时交还给游戏。
/// 放不下时分列：量出从第一条往下到血条上方能放几条，第一列照原样竖排这么多条，其余每列同样多条向右排开（先填满左边再填下一列）。
/// 放得下时和原版完全一样。只在列表增减条目时排一次，不每帧计算。
/// </summary>
public static class SkillColumnsPatch
{
	private const float ColumnGap = 6f;
	private const float BottomReserve = 0.12f; // 屏幕底部这一截是血条、内力条，列表不能压到

	// 每个出场角色的 CombatActionController 都有一个「Buff 状态容器」，战斗中加减状态时往里面放、从里面删条目
	[HarmonyPostfix]
	[HarmonyPatch(typeof(CombatActionController), "Awake")]
	public static void Awake_Postfix(CombatActionController __instance)
	{
		try
		{
			Transform container = Traverse.Create(__instance).Field("_stateBuffContainer").GetValue<Transform>();
			// 只排我方的：敌方的列表在右边，再往右分列就出屏幕了
			if (container != null && container.parent != null && container.parent.name == "PlayerStatus" && container.GetComponent<SkillColumns>() == null)
			{
				container.gameObject.AddComponent<SkillColumns>();
				FileLogger.Log("[技能表分列] 已接管我方状态列表");
			}
		}
		catch (System.Exception ex)
		{
			FileLogger.LogWarning("[技能表分列] " + ex.Message);
		}
	}

	/// <summary>
	/// 挂在我方的状态列表上：条目一增一减（OnTransformChildrenChanged）就在这一帧末尾重新排一次。
	/// 放到帧末是因为游戏 Destroy 的条目要到帧末才真正移走，新条目的布局也是那时才算好。
	/// </summary>
	private sealed class SkillColumns : MonoBehaviour
	{
		private bool _pending = true;
		private int _logged;
		private readonly List<RectTransform> _items = new List<RectTransform>();
		// 游戏自带的挤压（CombatBuffLayoutFitter）；旧版游戏没有，按名字找，找不到就是 null
		private Behaviour _fitter;

		private void Awake() => _fitter = GetComponent("CombatBuffLayoutFitter") as Behaviour;

		private void OnTransformChildrenChanged() => _pending = true;

		private void LateUpdate()
		{
			if (!_pending)
			{
				return;
			}
			_pending = false;
			var container = (RectTransform)transform;
			_items.Clear();
			foreach (Transform child in container)
			{
				if (child.gameObject.activeSelf && child is RectTransform item)
				{
					_items.Add(item);
				}
			}
			// 先全部交回原来的竖向布局，量出第一条的位置和每条的间距
			SetOverflow(int.MaxValue);
			UseGameSqueeze(!ConfigManager.EnableSkillColumns.Value);
			int perColumn = int.MaxValue;
			if (ConfigManager.EnableSkillColumns.Value && _items.Count > 1)
			{
				LayoutRebuilder.ForceRebuildLayoutImmediate(container);
				perColumn = RowsThatFit(container);
				// 前几次记一下量出来的结果，分列不对时好查
				if (_logged++ < 3)
				{
					FileLogger.Log($"[技能表分列] {_items.Count} 条，一列放得下 {perColumn} 条");
				}
			}
			if (_items.Count <= perColumn)
			{
				return;
			}
			// 第一列留在布局里；后面的条目不参加布局，按第一列同一行的位置往右挪
			SetOverflow(perColumn);
			LayoutRebuilder.ForceRebuildLayoutImmediate(container);
			for (int i = perColumn; i < _items.Count; i++)
			{
				RectTransform item = _items[i], row = _items[i % perColumn];
				item.anchorMin = row.anchorMin;
				item.anchorMax = row.anchorMax;
				item.pivot = row.pivot;
				item.sizeDelta = row.sizeDelta;
				item.anchoredPosition = row.anchoredPosition + new Vector2(i / perColumn * (row.rect.width + ColumnGap), 0f);
			}
		}

		/// <summary>
		/// 开关游戏自带的挤压。关掉时把间距和大小恢复成原样（它记着的缩放也归位），分列按原来的大小量；
		/// 打开时让它重新按条数算一遍。两边改的是同一个列表，不能同时开着。
		/// </summary>
		private void UseGameSqueeze(bool on)
		{
			if (_fitter == null || _fitter.enabled == on)
			{
				return;
			}
			var tr = Traverse.Create(_fitter);
			if (on)
			{
				tr.Field("_lastCount").SetValue(-1);
				_fitter.enabled = true;
				return;
			}
			_fitter.enabled = false;
			GetComponent<VerticalLayoutGroup>().spacing = tr.Field("_defaultSpacing").GetValue<float>();
			Vector3 scale = tr.Field("_defaultScale").GetValue<Vector3>();
			if (scale != Vector3.zero)
			{
				transform.localScale = scale;
			}
			tr.Field("_currentScale").SetValue(1f);
			tr.Field("_targetScale").SetValue(1f);
			tr.Field("_scaleVelocity").SetValue(0f);
			tr.Field("_velocity").SetValue(0f);
		}

		/// <summary>从第一条往下到血条上方（屏幕底部留出 BottomReserve）能放几条。</summary>
		private int RowsThatFit(RectTransform container)
		{
			Canvas canvas = container.GetComponentInParent<Canvas>();
			if (canvas == null)
			{
				return int.MaxValue;
			}
			var corners = new Vector3[4];
			((RectTransform)canvas.rootCanvas.transform).GetWorldCorners(corners);
			float limit = corners[0].y + (corners[1].y - corners[0].y) * BottomReserve;
			_items[0].GetWorldCorners(corners);
			float top = corners[1].y, height = corners[1].y - corners[0].y;
			float step = Mathf.Abs(_items[0].position.y - _items[1].position.y);
			if (step <= 0f)
			{
				return int.MaxValue;
			}
			// 第 n 条的底边在 top - (n - 1) * step - height
			return Mathf.Max(1, Mathf.FloorToInt((top - height - limit) / step) + 1);
		}

		private void SetOverflow(int perColumn)
		{
			for (int i = 0; i < _items.Count; i++)
			{
				bool overflow = i >= perColumn;
				LayoutElement element = _items[i].GetComponent<LayoutElement>();
				if (element == null && overflow)
				{
					element = _items[i].gameObject.AddComponent<LayoutElement>();
				}
				if (element != null)
				{
					element.ignoreLayout = overflow;
				}
			}
		}
	}
}
