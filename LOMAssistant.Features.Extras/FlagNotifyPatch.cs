using System;
using System.Collections;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Core;
using Mortal.Story;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 剧情旗标变化提示（F8 开关，默认关）：剧情脚本改动旗标时，在屏幕上用游戏自带的消息提示显示
/// 「旗标名 旧值 → 新值」，例如救没救下大师兄、神兵帖进度，可以当场知道。
/// 只拦剧情脚本用的 StatModifyManager.SetFlag/AddFlag，读档时逐个恢复旗标不经过这里，不会刷屏。
/// 「旗标暂存」这类脚本内部临时用的旗标不提示。
/// 另外：F9 面板的「心上人」只显示编号，这里在旁边显示人名。
/// </summary>
public static class FlagNotifyPatch
{
	[HarmonyPrefix]
	[HarmonyPatch(typeof(StatModifyManager), "SetFlag")]
	public static void SetFlag_Prefix(string id, out int __state) => __state = CurrentState(id);

	[HarmonyPostfix]
	[HarmonyPatch(typeof(StatModifyManager), "SetFlag")]
	public static void SetFlag_Postfix(string id, int __state) => Notify(id, __state);

	[HarmonyPrefix]
	[HarmonyPatch(typeof(StatModifyManager), "AddFlag")]
	public static void AddFlag_Prefix(string id, out int __state) => __state = CurrentState(id);

	[HarmonyPostfix]
	[HarmonyPatch(typeof(StatModifyManager), "AddFlag")]
	public static void AddFlag_Postfix(string id, int __state) => Notify(id, __state);

	private static int CurrentState(string id)
	{
		if (!ConfigManager.ShowFlagChanges.Value)
		{
			return 0;
		}
		try
		{
			FlagData flag = MissionManagerData.Instance?.GetFlag(id);
			return flag != null ? flag.State : 0;
		}
		catch
		{
			return 0;
		}
	}

	private static void Notify(string id, int before)
	{
		if (!ConfigManager.ShowFlagChanges.Value)
		{
			return;
		}
		try
		{
			FlagData flag = MissionManagerData.Instance?.GetFlag(id);
			if (flag == null || flag.State == before)
			{
				return;
			}
			string note = flag.DevNote ?? "";
			int newline = note.IndexOf('\n');
			if (newline >= 0)
			{
				note = note.Substring(0, newline);
			}
			note = TextConverter.ToSimplified(note.Trim());
			if (note.Contains("暂存"))
			{
				return;
			}
			string label = note.Length > 0 ? note : id;
			string message = $"【旗标】{label}  {before} → {flag.State}";
			FileLogger.Log($"旗标变化：{id} {label} {before} -> {flag.State}");
			ShowNotice(message);
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("旗标变化提示失败: " + e.Message);
		}
	}

	/// <summary>屏幕上方的提示：用游戏自带的飘字，分支判断也走这里。</summary>
	internal static void ShowNotice(string message)
	{
		StoryMainUI ui = StoryMainUI.Instance;
		if (ui != null && ui.isActiveAndEnabled)
		{
			ui.DisplayMessageText(TextConverter.ForDisplay(message));
			Linger(ui);
		}
	}

	/// <summary>
	/// 游戏的提示只停 1 秒、按游戏时间算，开倍速时一闪就没；而且弹出时就把停留时间读走了，事后改字段不起作用，
	/// 切场景时还会跟着场景一起销毁。所以把刚弹出的这条停掉游戏的动画，挪到一块跨场景保留的画布上（屏幕位置和大小不变），
	/// 由本 Mod 按真实时间停留配置的秒数、往上飘，再淡出。
	/// </summary>
	private static void Linger(StoryMainUI ui)
	{
		float seconds = ConfigManager.FlagNoticeSeconds.Value;
		if (seconds <= 0f)
		{
			return;
		}
		GameMessagePanel panel = Traverse.Create(ui).Field("_messagePanel").GetValue<GameMessagePanel>();
		if (panel == null || panel.transform.childCount == 0)
		{
			return;
		}
		FloatingText text = panel.transform.GetChild(panel.transform.childCount - 1).GetComponent<FloatingText>();
		Canvas source = panel.GetComponentInParent<Canvas>();
		if (text == null || source == null)
		{
			return;
		}
		CanvasGroup group = Traverse.Create(text).Field("_canvasGroup").GetValue<CanvasGroup>();
		if (group == null)
		{
			return;
		}
		text.StopAllCoroutines();
		Canvas root = source.rootCanvas;
		Camera cam = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
		Vector2 screen = RectTransformUtility.WorldToScreenPoint(cam, text.transform.position);
		NoticeCanvas host = NoticeCanvas.Get(root);
		var rt = (RectTransform)text.transform;
		float height = rt.rect.height > 1f ? rt.rect.height : 40f;
		rt.SetParent(host.transform, worldPositionStays: false);
		// 横向放在屏幕正中（游戏画布按摄像机换算时横坐标会偏到右边），纵向用游戏原来的高度；
		// 同时还在屏幕上的提示（一次改了好几面旗标）一条接一条往下排，不叠在一起
		RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)host.transform, screen, null, out Vector2 local);
		Active.RemoveAll(a => a == null);
		rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
		// 游戏的飘字框宽度固定，长一点的文字换行后第二行被裁掉：改成一行放到底，不裁
		foreach (Text label in rt.GetComponentsInChildren<Text>(true))
		{
			label.alignment = TextAnchor.MiddleCenter;
			label.horizontalOverflow = HorizontalWrapMode.Overflow;
			label.verticalOverflow = VerticalWrapMode.Overflow;
			label.fontSize = Mathf.RoundToInt(label.fontSize * TextScale);
		}
		foreach (TMPro.TMP_Text label in rt.GetComponentsInChildren<TMPro.TMP_Text>(true))
		{
			label.alignment = TMPro.TextAlignmentOptions.Center;
			label.enableAutoSizing = false;
			label.enableWordWrapping = false;
			label.overflowMode = TMPro.TextOverflowModes.Overflow;
			label.fontSize *= TextScale;
		}
		// 排在还在屏幕上的最低一条下面（按条数算会撞上先出现、还没消失的提示）；排满了整体往上挪
		float step = height * TextScale * 1.1f;
		float y = local.y;
		foreach (RectTransform shown in Active)
		{
			y = Mathf.Min(y, shown.anchoredPosition.y - step);
		}
		// 排的高度按 100% 算（字小了能多排几条，大了少排几条），都不会往下盖住对白
		float lowest = local.y - MaxRows * height * BaseScale * 1.1f;
		if (y < lowest)
		{
			foreach (RectTransform shown in Active)
			{
				shown.anchoredPosition += new Vector2(0f, lowest - y);
			}
			y = lowest;
		}
		rt.anchoredPosition = new Vector2(0f, y);
		Active.Add(rt);
		host.StartCoroutine(Float(text, group, seconds));
	}

	private const float BaseScale = 0.8f; // 100% 是游戏飘字的八成大小
	private const int MaxRows = 8; // 100% 时第一条下面最多排几条

	private static float TextScale => BaseScale * Mathf.Clamp(ConfigManager.TopNoticeScale.Value, 50, 150) / 100f;

	// 还在屏幕上的旗标、分支提示
	private static readonly System.Collections.Generic.List<RectTransform> Active = new System.Collections.Generic.List<RectTransform>();

	private static IEnumerator Float(FloatingText text, CanvasGroup group, float seconds)
	{
		const float Rise = 60f; // 停留期间往上飘的距离（参考分辨率下的像素）
		var rt = (RectTransform)text.transform;
		float elapsed = 0f;
		while (text != null && group != null && group.alpha > 0f)
		{
			float dt = Time.unscaledDeltaTime;
			elapsed += dt;
			rt.anchoredPosition += new Vector2(0f, Rise * dt / seconds);
			if (elapsed > seconds)
			{
				group.alpha -= 3f * dt;
			}
			yield return null;
		}
		Active.Remove(rt);
		if (text != null)
		{
			Object.Destroy(text.gameObject);
		}
	}

	/// <summary>跨场景保留的提示画布，缩放设置照抄游戏的主画布，挪过来的提示大小不变。</summary>
	private sealed class NoticeCanvas : MonoBehaviour
	{
		private static NoticeCanvas _instance;

		public static NoticeCanvas Get(Canvas like)
		{
			if (_instance == null)
			{
				var go = new GameObject("LOMAssistant.FlagNotice", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
				Object.DontDestroyOnLoad(go);
				var canvas = go.GetComponent<Canvas>();
				canvas.renderMode = RenderMode.ScreenSpaceOverlay;
				canvas.sortingOrder = 30000;
				_instance = go.AddComponent<NoticeCanvas>();
			}
			CanvasScaler from = like.GetComponent<CanvasScaler>();
			CanvasScaler to = _instance.GetComponent<CanvasScaler>();
			if (from != null)
			{
				to.uiScaleMode = from.uiScaleMode;
				to.referenceResolution = from.referenceResolution;
				to.screenMatchMode = from.screenMatchMode;
				to.matchWidthOrHeight = from.matchWidthOrHeight;
				to.scaleFactor = from.scaleFactor;
			}
			return _instance;
		}
	}

	// F9「心上人」：括号里原本是加成值（永远是 +0），改成显示人名
	[HarmonyPostfix]
	[HarmonyPatch(typeof(TestGameStatPanel), "UpdateValue")]
	public static void TestGameStatPanel_UpdateValue_Postfix(TestGameStatPanel __instance)
	{
		try
		{
			var tr = Traverse.Create(__instance);
			GameStat stat = tr.Field("_stat").GetValue<GameStat>();
			if (stat == null || stat.StatType != GameStatType.愛人)
			{
				return;
			}
			Text add = tr.Field("_addText").GetValue<Text>();
			if (add == null)
			{
				return;
			}
			string key = "PlayerInfo/Lover/" + stat.FinalValue;
			string name = LocalizationManager.Instance.LocaleResolver.GetString(key);
			if (!string.IsNullOrEmpty(name) && name != key)
			{
				add.text = "(" + TextConverter.ForDisplay(TextConverter.ToSimplified(name)) + ")";
			}
		}
		catch
		{
		}
	}
}
