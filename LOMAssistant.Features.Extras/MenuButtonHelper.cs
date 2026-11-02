using System.Collections.Generic;
using System.Text;
using Mortal.Core;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LOMAssistant.Features.Extras;

internal static class MenuButtonHelper
{
	// Only the components that write text; font switchers (LeanLocalizedTextFont etc.)
	// must stay so every state (normal/hover/...) gets the current language's font.
	private static readonly HashSet<string> TextLocalizers = new HashSet<string>
	{
		"LeanLocalizedText",
		"LeanLocalizedTextMesh",
		"LeanLocalizedTextMeshPro",
		"LeanLocalizedTextMeshProUGUI"
	};

	/// <summary>
	/// Clones an existing menu button right after the template, relabels it, replaces
	/// its click handlers and tightens the parent layout so all buttons still fit.
	/// </summary>
	/// <summary>场景里叫这个名字的、本 Mod 加的按钮全部显示或隐藏（设置页开关改了马上生效）。</summary>
	public static void SetVisible(string objectName, bool visible)
	{
		foreach (Button button in Resources.FindObjectsOfTypeAll<Button>())
		{
			if (button != null && button.name == objectName && button.gameObject.scene.IsValid())
			{
				button.gameObject.SetActive(visible);
			}
		}
	}

	public static Button CloneButton(Button template, string label, string objectName)
	{
		label = TextConverter.ForDisplay(label);
		Transform parent = template.transform.parent;
		Transform existing = parent.Find(objectName);
		if (existing != null)
		{
			return existing.GetComponent<Button>();
		}

		GameObject clone = Object.Instantiate(template.gameObject, parent, worldPositionStays: false);
		clone.name = objectName;
		clone.transform.SetSiblingIndex(template.transform.GetSiblingIndex() + 1);

		foreach (MonoBehaviour mb in clone.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
		{
			if (mb != null && TextLocalizers.Contains(mb.GetType().Name))
			{
				Object.DestroyImmediate(mb);
			}
		}
		// 模板按钮（如「继续游戏」）上的 InputButton 把手柄键（返回键 B）绑到按钮上；复制后它指向的是复制出来的按钮，
		// 按 B 关菜单时会连带点一下我们的按钮（存完档按 B 返回就空过了一回合）。复制出来的按钮不要任何按键绑定。
		foreach (InputButton ib in clone.GetComponentsInChildren<InputButton>(includeInactive: true))
		{
			Object.DestroyImmediate(ib);
		}
		foreach (Text t in clone.GetComponentsInChildren<Text>(includeInactive: true))
		{
			t.text = label;
		}
		foreach (TMP_Text t in clone.GetComponentsInChildren<TMP_Text>(includeInactive: true))
		{
			t.text = label;
		}

		Button button = clone.GetComponent<Button>();
		button.onClick = new Button.ButtonClickedEvent();
		button.interactable = true;
		clone.SetActive(true);

		FitVerticalLayout(parent, (RectTransform)template.transform);
		return button;
	}

	/// <summary>
	/// 把游戏的按钮复制到别的父节点下（新版存档界面用），去掉按键绑定和文字本地化，换文字、清空点击。
	/// </summary>
	public static Button CloneInto(Button template, Transform parent, string label, string objectName)
	{
		label = TextConverter.ForDisplay(label);
		GameObject clone = Object.Instantiate(template.gameObject, parent, worldPositionStays: false);
		clone.name = objectName;
		foreach (MonoBehaviour mb in clone.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
		{
			if (mb != null && TextLocalizers.Contains(mb.GetType().Name))
			{
				Object.DestroyImmediate(mb);
			}
		}
		foreach (InputButton ib in clone.GetComponentsInChildren<InputButton>(includeInactive: true))
		{
			Object.DestroyImmediate(ib);
		}
		foreach (Text t in clone.GetComponentsInChildren<Text>(includeInactive: true))
		{
			t.text = label;
		}
		foreach (TMP_Text t in clone.GetComponentsInChildren<TMP_Text>(includeInactive: true))
		{
			t.text = label;
		}
		Button button = clone.GetComponent<Button>();
		button.onClick = new Button.ButtonClickedEvent();
		button.interactable = true;
		clone.SetActive(true);
		return button;
	}

	/// <summary>
	/// The menus stack buttons in a VerticalLayoutGroup laid out for the original count;
	/// an extra button pushes the last one off screen. Shrink the spacing so the stack
	/// keeps the height it had before the new button was added.
	/// </summary>
	private static void FitVerticalLayout(Transform parent, RectTransform sample)
	{
		VerticalLayoutGroup layout = parent.GetComponent<VerticalLayoutGroup>();
		if (layout == null)
		{
			FileLogger.Log("菜单按钮：父节点没有 VerticalLayoutGroup，不调整间距。");
			return;
		}

		int count = 0;
		foreach (Transform child in parent)
		{
			if (child.gameObject.activeSelf && child.GetComponent<LayoutElement>()?.ignoreLayout != true)
			{
				count++;
			}
		}
		if (count < 2)
		{
			return;
		}

		float h = sample.rect.height;
		if (h <= 0f)
		{
			h = LayoutUtility.GetPreferredHeight(sample);
		}
		// (count-1) buttons with old spacing == count buttons with new spacing.
		int oldCount = count - 1;
		float oldHeight = oldCount * h + (oldCount - 1) * layout.spacing;
		float newSpacing = (oldHeight - count * h) / (count - 1);

		string msg = $"菜单按钮：{MenuButtonHelper.DescribeHierarchy(parent)} 按钮数 {count}，按钮高 {h:0.#}，" +
			$"间距 {layout.spacing:0.#} -> {newSpacing:0.#}，controlHeight={layout.childControlHeight}，" +
			$"fitter={(parent.GetComponent<ContentSizeFitter>() != null)}";
		LOMAssistantPlugin.LogInfo(msg);
		FileLogger.Log(msg);

		layout.spacing = newSpacing;
		LayoutRebuilder.MarkLayoutForRebuild((RectTransform)parent);
	}

	public static string DescribeHierarchy(Transform t)
	{
		var sb = new StringBuilder();
		for (Transform cur = t; cur != null; cur = cur.parent)
		{
			sb.Insert(0, "/" + cur.name);
		}
		return sb.ToString();
	}
}

/// <summary>
/// 菜单按钮的手柄上下顺序。游戏菜单的按钮把上下跳到哪个按钮写死在场景里，
/// 复制出来的按钮也照抄了，手柄按上下会跳过它。这里按当前显示的按钮（同一父节点、按排列顺序）
/// 重新串起上下顺序；按钮显示/隐藏变化时自动重串。
/// </summary>
internal class MenuNavChain : MonoBehaviour
{
	private readonly List<Button> _last = new List<Button>();

	public static void Ensure(Transform parent)
	{
		if (parent.GetComponent<MenuNavChain>() == null)
		{
			parent.gameObject.AddComponent<MenuNavChain>();
		}
	}

	// 菜单每次打开都重串一次，防止游戏在打开时又改回原来的设置
	private void OnEnable()
	{
		_last.Clear();
	}

	private void LateUpdate()
	{
		var buttons = new List<Button>();
		foreach (Transform child in transform)
		{
			Button b = child.GetComponent<Button>();
			if (b != null && child.gameObject.activeInHierarchy && b.interactable)
			{
				buttons.Add(b);
			}
		}
		if (SameAs(buttons))
		{
			return;
		}
		_last.Clear();
		_last.AddRange(buttons);
		for (int i = 0; i < buttons.Count; i++)
		{
			Navigation nav = buttons[i].navigation;
			nav.mode = Navigation.Mode.Explicit;
			nav.selectOnUp = i > 0 ? buttons[i - 1] : null;
			nav.selectOnDown = i < buttons.Count - 1 ? buttons[i + 1] : null;
			buttons[i].navigation = nav;
		}
	}

	private bool SameAs(List<Button> buttons)
	{
		if (buttons.Count != _last.Count)
		{
			return false;
		}
		for (int i = 0; i < buttons.Count; i++)
		{
			if (buttons[i] != _last[i])
			{
				return false;
			}
		}
		return true;
	}
}
