using System.Linq;
using HarmonyLib;
using LOMAssistant.Config;
using LOMAssistant.Features.Story;
using Mortal.Core;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace LOMAssistant.Features.Library;

public static class LibraryDisplayPatch
{
	public const int HINT = 1, FULL = 2;
	private const int UnlockValue = 7777777;

	[HarmonyPrefix]
	[HarmonyPatch(typeof(LibraryAchievementItemPanel), "Setup")]
	[HarmonyPatch(typeof(LibraryEndItemPanel), "Setup")]
	[HarmonyPatch(typeof(LibraryDeadItemPanel), "Setup")]
	public static void Setup_Prefix(LibraryItemData data)
	{
		if (ConfigManager.EnableLibraryDisplay.Value == FULL && data.Count == 0)
		{
			data.Set(7777777);
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(LibraryAchievementItemPanel), "Setup")]
	[HarmonyPatch(typeof(LibraryEndItemPanel), "Setup")]
	[HarmonyPatch(typeof(LibraryDeadItemPanel), "Setup")]
	public static void Setup_Postfix(LibraryItemPanel __instance, LibraryItemData data)
	{
		if (__instance is LibraryEndItemPanel || __instance is LibraryDeadItemPanel)
		{
			LibraryHoverHint.Track(__instance);
		}
		if (ConfigManager.EnableLibraryDisplay.Value == FULL)
		{
			Text text = null;
			if (__instance is LibraryEndItemPanel)
			{
				text = Traverse.Create(__instance).Field("_index").GetValue<Text>();
			}
			else if (__instance is LibraryAchievementItemPanel)
			{
				text = Traverse.Create(__instance).Field("_title").GetValue<Text>();
			}
			if (data.Count == 0 && text != null)
			{
				text.text += TextConverter.ForDisplay("(未解锁)");
			}
			FileLogger.Log("讲经堂显示：Setup_Postfix 被调用，data=" + ((data != null) ? data.name : "null"));
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(LibraryItemPanel), "UpdateContent")]
	public static void UpdateContent_Prefix(LibraryItemPanel __instance)
	{
		if (ConfigManager.EnableLibraryDisplay.Value == FULL)
		{
			LibraryItemData value = Traverse.Create(__instance).Field("_data").GetValue<LibraryItemData>();
			if (value != null && value.Count == 7777777)
			{
				value.Reset();
			}
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(LibraryItemPanel), "UpdateData")]
	public static void UpdateData_Prefix(LibraryItemPanel __instance)
	{
		if (ConfigManager.EnableLibraryDisplay.Value == FULL)
		{
			LibraryItemData value = Traverse.Create(__instance).Field("_data").GetValue<LibraryItemData>();
			if (value != null && value.Count == 0)
			{
				value.Set(7777777);
			}
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(LibraryEndItemPanel), "OnClick")]
	public static bool OnClick_Prefix(LibraryItemPanel __instance)
	{
		if (ConfigManager.EnableLibraryDisplay.Value != FULL)
		{
			return true;
		}
		LibraryItemEvent value = Traverse.Create(__instance).Field("_clickEvent").GetValue<LibraryItemEvent>();
		LibraryItemData value2 = Traverse.Create(__instance).Field("_data").GetValue<LibraryItemData>();
		if (value != null)
		{
			value.Raise(value2);
			LOMAssistantPlugin.LogInfo("讲经堂显示：点击结局项 " + ((value2 != null) ? value2.name : "null"));
			FileLogger.Log("讲经堂显示：点击结局项 " + ((value2 != null) ? value2.name : "null"));
		}
		return false;
	}
}

internal sealed class LibraryHoverHint : MonoBehaviour
{
	private sealed class Target : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
	{
		public void OnPointerEnter(PointerEventData e) => _hovered = this;

		public void OnPointerExit(PointerEventData e) => Leave();

		private void OnDisable() => Leave();

		private void Leave()
		{
			if (_hovered == this)
			{
				_hovered = null;
			}
		}
	}

	private static Target _hovered;
	private static Target _textFor;
	private static string _text = "";
	private static int _version = -1;
	private static string _lang;

	private GUIStyle _box;
	private float _scale = -1f;
	private int _fontMode = -1;

	public static void Track(LibraryItemPanel item)
	{
		_textFor = null;
		if (item.GetComponent<Target>() == null)
		{
			item.gameObject.AddComponent<Target>();
		}
		if (!StoryAtlas.Ready && !StoryAtlas.Building && StoryAtlas.Status.Length == 0)
		{
			StoryAtlas.Start();
		}
	}

	private void OnGUI()
	{
		if (_hovered == null || ConfigManager.EnableLibraryDisplay.Value == 0 || Event.current.type != EventType.Repaint || UiHider.Active || InputGuard.ModWindowOpen)
		{
			return;
		}
		if (!StoryAtlas.Ready)
		{
			StoryAtlas.Tick(true);
		}
		string text = Describe(_hovered);
		if (text.Length == 0)
		{
			return;
		}
		float scale = PanelTheme.UiScale;
		if (_box == null || ModFont.NeedsRebuild(scale, _scale, _fontMode))
		{
			_scale = scale;
			_fontMode = ModFont.Mode;
			_box = PanelTheme.Toast(scale, 18);
			_box.alignment = TextAnchor.UpperLeft;
			_box.padding = new RectOffset((int)(14f * scale), (int)(14f * scale), (int)(10f * scale), (int)(10f * scale));
		}
		var content = new GUIContent(text);
		float width = 540f * scale;
		float height = _box.CalcHeight(content, width);
		Vector2 at = Event.current.mousePosition;
		float x = at.x + 24f * scale;
		if (x + width > Screen.width)
		{
			x = at.x - width - 16f * scale;
		}
		float y = Mathf.Clamp(at.y + 16f * scale, 0f, Mathf.Max(0f, Screen.height - height));
		GUI.Box(new Rect(Mathf.Max(0f, x), y, width, height), content, PanelTheme.Sync(_box));
	}

	private static string Describe(Target target)
	{
		if (!StoryAtlas.Ready)
		{
			return StoryAtlas.Status;
		}
		string lang = TextConverter.DisplayLanguage;
		if (target != _textFor || _version != StoryAtlas.Version || _lang != lang)
		{
			_textFor = target;
			_version = StoryAtlas.Version;
			_lang = lang;
			LibraryItemPanel panel = target.GetComponent<LibraryItemPanel>();
			LibraryItemData data = Traverse.Create(panel).Field("_data").GetValue<LibraryItemData>();
			bool death = panel is LibraryDeadItemPanel;
			StoryEnding ending = data == null ? null : StoryAtlas.Endings.FirstOrDefault(e => e.Item == data || (e.Death == death && e.Id == data.name));
			_text = ending == null ? "" : "<b>" + StoryReviewPanel.EndingNumber(ending) + ending.Title + "</b>\n" + StoryReviewPanel.EndingWays(ending, onPaper: false);
		}
		return _text;
	}
}
