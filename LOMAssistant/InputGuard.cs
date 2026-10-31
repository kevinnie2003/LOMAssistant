// 输入隔离的做法（透明挡板 + 拦截游戏的按键回调 + 拦截 Fungus 对话框的旧式输入）参考了
// 赵活系统（https://github.com/Toufff/zhaohuo-system，Copyright (c) Toufff，MIT License），本文件为自行实现的精简版。
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace LOMAssistant;

/// <summary>
/// 输入隔离：鼠标在 Mod 的窗口（F8 面板、分支小窗、F9 旗标搜索栏等）上点击时，不让点击穿透到游戏
/// （推进对话、点到下面的按钮、打开菜单）；在 Mod 的输入框里打字时，不让键盘触发游戏的快捷键。
/// 各窗口每帧用 Report 报告自己的位置（IMGUI 坐标）。判断都在输入发生的当下做，不依赖脚本执行顺序。
/// </summary>
public class InputGuard : MonoBehaviour
{
	private struct Region
	{
		public Rect Rect;
		public int Frame;
	}

	private static readonly Dictionary<string, Region> Regions = new Dictionary<string, Region>();
	private static InputGuard _instance;
	private static bool _textFocus;
	private static bool _ownedPress; // 这次按下是从 Mod 窗口上开始的，松开前一直算 Mod 的

	private RectTransform _shieldRoot;
	private readonly Dictionary<string, RectTransform> _shields = new Dictionary<string, RectTransform>();

	private static bool _capturing;
	private static int _captureEnd = -1;

	/// <summary>设置页正在等玩家按新热键：这时所有热键都不响应（按下的那一帧也算，免得刚绑的键立刻触发）。</summary>
	public static bool CapturingKey
	{
		get => _capturing || Time.frameCount <= _captureEnd;
		set
		{
			if (_capturing && !value)
			{
				_captureEnd = Time.frameCount + 1;
			}
			_capturing = value;
		}
	}

	/// <summary>本 Mod 的热键按下了（没绑定、正在改键时都算没按）。鼠标右键、中键、侧键也可以当热键。</summary>
	public static bool HotkeyDown(KeyCode key)
	{
		// 在新版存档界面的备注框里打字时，字母、数字键不能当热键
		if (key == KeyCode.None || CapturingKey || LOMAssistant.Features.Extras.SaveBrowser.Typing)
		{
			return false;
		}
		Mouse mouse = Mouse.current;
		switch (key)
		{
			case KeyCode.Mouse1: return mouse != null && mouse.rightButton.wasPressedThisFrame;
			case KeyCode.Mouse2: return mouse != null && mouse.middleButton.wasPressedThisFrame;
			case KeyCode.Mouse3: return mouse != null && mouse.backButton.wasPressedThisFrame;
			case KeyCode.Mouse4: return mouse != null && mouse.forwardButton.wasPressedThisFrame;
			default: return Input.GetKeyDown(key);
		}
	}

	/// <summary>热键给玩家看的名字。</summary>
	public static string KeyName(KeyCode key)
	{
		switch (key)
		{
			case KeyCode.None: return "未绑定";
			case KeyCode.Mouse1: return "鼠标右键";
			case KeyCode.Mouse2: return "鼠标中键";
			case KeyCode.Mouse3: return "鼠标后退键";
			case KeyCode.Mouse4: return "鼠标前进键";
			default: return key.ToString();
		}
	}

	public static void Ensure()
	{
		if (_instance == null)
		{
			_instance = LOMAssistantPlugin.Instance.gameObject.AddComponent<InputGuard>();
		}
	}

	/// <summary>窗口在 OnGUI 里每帧调用，报告自己占的区域（IMGUI 坐标，原点左上）。不显示时不调用即可。</summary>
	public static void Report(string id, Rect guiRect)
	{
		Regions[id] = new Region { Rect = guiRect, Frame = Time.frameCount };
	}

	private static Vector2 PointerGui()
	{
		Vector2 p = Mouse.current != null ? Mouse.current.position.ReadValue() : (Vector2)Input.mousePosition;
		return new Vector2(p.x, Screen.height - p.y);
	}

	private static bool PointerOverMod()
	{
		Vector2 p = PointerGui();
		foreach (Region r in Regions.Values)
		{
			if (Time.frameCount - r.Frame <= 2 && r.Rect.Contains(p))
			{
				return true;
			}
		}
		return false;
	}

	private static bool AnyButtonDown()
	{
		Mouse m = Mouse.current;
		return m != null ? m.leftButton.isPressed || m.rightButton.isPressed || m.middleButton.isPressed
			: Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
	}

	public static bool PointerOverModWindow => PointerOverMod();

	/// <summary>有 Mod 的窗口开着（F8、F9 旗标搜索、剧情回顾、随时存档、事件自选）。剧情分支小窗只是提示，不算。</summary>
	public static bool ModWindowOpen
	{
		get
		{
			foreach (KeyValuePair<string, Region> r in Regions)
			{
				if (r.Key != "Branch" && Time.frameCount - r.Value.Frame <= 2)
				{
					return true;
				}
			}
			return false;
		}
	}

	public static bool BlockPointer
	{
		get
		{
			if (UiHider.Swallowing)
			{
				return true;
			}
			if (!Application.isFocused)
			{
				return false;
			}
			bool over = PointerOverMod();
			if (over && AnyButtonDown())
			{
				_ownedPress = true;
			}
			return over || _ownedPress;
		}
	}

	public static bool BlockKeyboard => UiHider.Swallowing || (Application.isFocused && _textFocus);

	/// <summary>某个游戏输入回调该不该被挡下（按触发它的设备判断）。</summary>
	public static bool Suppress(InputAction.CallbackContext context)
	{
		// 隐藏界面期间、恢复用的那一下，以及拿来隐藏界面的右键，都不交给游戏
		if (UiHider.Swallowing || UiHider.ClaimsRightClick(context.control))
		{
			return true;
		}
		InputDevice device = context.control != null ? context.control.device : null;
		if (device is Keyboard keyboard)
		{
			// 活侠传拓展窗口、判定前选分支的窗口开着时，Esc 用来关窗口，不打开游戏的暂停菜单
			if (context.control == keyboard.escapeKey
				&& (LOMAssistant.Features.Story.StoryReviewPanel.OwnsEscape || BranchAhead.OwnsEscape))
			{
				return true;
			}
			return BlockKeyboard;
		}
		// 咖啡伴侣窗口开着时手柄归窗口用（移动指针、点击、切页、B 关窗口），不交给游戏
		if (device is Gamepad && (LOMAssistant.Features.Story.StoryReviewPanel.OwnsEscape || BranchAhead.OwnsEscape))
		{
			return true;
		}
		if (device is UnityEngine.InputSystem.Pointer)
		{
			// 右键用来关咖啡伴侣窗口，鼠标不在窗口上时也不交给游戏
			if (device is Mouse mouse && context.control == mouse.rightButton
				&& (LOMAssistant.Features.Story.StoryReviewPanel.OwnsEscape || BranchAhead.OwnsEscape))
			{
				return true;
			}
			return BlockPointer;
		}
		return false;
	}

	private void Update()
	{
		if (_ownedPress && !AnyButtonDown())
		{
			_ownedPress = false;
		}
	}

	private void OnGUI()
	{
		// 点到 Mod 窗口以外的地方：清掉输入框焦点，键盘还给游戏
		if (Event.current.type == EventType.MouseDown && !PointerOverMod())
		{
			GUIUtility.keyboardControl = 0;
		}
		// 只有 IMGUI 输入框会占用键盘焦点；Mod 窗口都关了也不算
		if (Event.current.type == EventType.Repaint)
		{
			bool anyWindow = false;
			foreach (Region r in Regions.Values)
			{
				anyWindow |= Time.frameCount - r.Frame <= 2;
			}
			_textFocus = anyWindow && GUIUtility.keyboardControl != 0;
		}
	}

	// 透明挡板：在每个 Mod 窗口的位置放一块不可见的 uGUI 图片，挡住游戏按钮的点击
	private void LateUpdate()
	{
		try
		{
			EnsureShieldRoot();
			foreach (KeyValuePair<string, Region> pair in Regions)
			{
				if (!_shields.TryGetValue(pair.Key, out RectTransform shield) || shield == null)
				{
					shield = CreateShield(pair.Key);
					_shields[pair.Key] = shield;
				}
				bool active = Time.frameCount - pair.Value.Frame <= 2;
				shield.gameObject.SetActive(active);
				if (active)
				{
					shield.anchoredPosition = new Vector2(pair.Value.Rect.x, -pair.Value.Rect.y);
					shield.sizeDelta = pair.Value.Rect.size;
				}
			}
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("输入隔离挡板更新失败: " + e.Message);
			enabled = false;
		}
	}

	private void EnsureShieldRoot()
	{
		if (_shieldRoot != null)
		{
			return;
		}
		_shields.Clear();
		var root = new GameObject("LOMAssistant.InputShield", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
		DontDestroyOnLoad(root);
		var canvas = root.GetComponent<Canvas>();
		canvas.renderMode = RenderMode.ScreenSpaceOverlay;
		canvas.sortingOrder = 32760;
		_shieldRoot = (RectTransform)root.transform;
	}

	private RectTransform CreateShield(string id)
	{
		var go = new GameObject("Shield_" + id, typeof(RectTransform), typeof(Image));
		go.transform.SetParent(_shieldRoot, false);
		var rt = (RectTransform)go.transform;
		rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
		rt.pivot = new Vector2(0f, 1f);
		var image = go.GetComponent<Image>();
		image.color = Color.clear;
		image.raycastTarget = true;
		return rt;
	}
}

/// <summary>拦截游戏里由按键 / 鼠标触发的输入回调（推进对话、菜单、确认等）。脚本、自动播放触发的不受影响。</summary>
[HarmonyPatch]
public static class InputGuardActionGate
{
	private static readonly string[] Targets =
	{
		"Mortal.Core.InputButton:InputActionChanged",
		"Mortal.Free.FreeManager:NextInputAction", "Mortal.Free.FreeManager:MenuActionChanged",
		"Mortal.Free.FreeManager:StatusActionChanged", "Mortal.Free.FreeManager:SaveActionPerformed", "Mortal.Free.FreeManager:ToggleSelectedPerformed",
		"Mortal.Free.FreeManager:LogActionPerformed", "Mortal.Core.StoryLogPanel:CloseActionPerformed",
		"Mortal.Story.StoryManager:MenuActionChanged", "Mortal.Story.StoryManager:SubmitActionChanged", "Mortal.Story.StoryManager:StatusActionChanged",
		"Mortal.Story.StoryManager:AutoActionChanged", "Mortal.Story.StoryManager:LogActionChanged", "Mortal.Story.StoryManager:SkipActionPerformed",
		"Mortal.Story.DiceMenuDialog:NextInputAction", "Mortal.Story.DiceMenuDialog:DestinyAction",
		"Mortal.Story.DiceMenuDialog:DestinyRestartAction", "Mortal.Story.DiceMenuDialog:RangeAction",
		"Mortal.Battle.GameLevelManager:MenuActionChange", "Mortal.Battle.GameLevelManager:PlayerInputActionTriggered",
		"Mortal.Combat.CombatUIManager:MenuActionChanged", "Mortal.Combat.CombatManager:NextInputAction",
		"Mortal.Combat.CombatUIManager:SpeedActionPerformed", "Mortal.Combat.CombatUIManager:StatusActionChanged",
		"Mortal.Core.CreditPanel:PerformedAction", "Mortal.Core.CgPanel:PerformedAction", "Mortal.Core.EndGamePanel:PerformedAction",
		"Mortal.Battle.GameOverPanel:ContinueAction",
		"Mortal.Core.ButtonNavigationInputAction:NextActionPerformed", "Mortal.Core.ButtonNavigationInputAction:PreviousActionPerfomed",
		"Mortal.Core.CustomMenuDialog:NavigateActionChanged", "Mortal.Core.LoadGamePanel:DeleteActionPerformed",
		"Mortal.Core.LongClickButton:ActionStarted", "Mortal.Core.LongClickButton:ActionPerformed",
		"Mortal.Core.PlayerStatusPanel:NavLeft_1_Started", "Mortal.Core.PlayerStatusPanel:NavRight_1_Started",
		"Mortal.Story.ShopPanel:NavRighPerformed", "Mortal.Story.ShopPanel:NavLeftPerformed",
		"Mortal.Story.ShopPanel:MinusActionPerformed", "Mortal.Story.ShopPanel:PlusActionPerformed",
	};

	private static IEnumerable<MethodBase> TargetMethods()
	{
		var seen = new HashSet<MethodBase>();
		foreach (string target in Targets)
		{
			int colon = target.IndexOf(':');
			Type type = AccessTools.TypeByName(target.Substring(0, colon));
			MethodInfo method = type != null ? AccessTools.Method(type, target.Substring(colon + 1), new[] { typeof(InputAction.CallbackContext) }) : null;
			if (method != null && seen.Add(method))
			{
				yield return method;
			}
			else if (method == null)
			{
				FileLogger.Log("输入隔离：游戏里没有 " + target + "，跳过");
			}
		}
	}

	// 松开（canceled）要照常放行，否则游戏会以为按键一直按着
	private static bool Prefix(InputAction.CallbackContext __0)
	{
		return __0.canceled || !InputGuard.Suppress(__0);
	}
}

/// <summary>剧情对话框（Fungus）用旧式 Input 判断点击推进，同样要挡；判定前选分支的窗口开着时，Esc 也不让它读。</summary>
[HarmonyPatch(typeof(Fungus.DialogInput), "Update")]
public static class InputGuardDialogGate
{
	private static readonly MethodInfo MouseDown = AccessTools.Method(typeof(Input), nameof(Input.GetMouseButtonDown), new[] { typeof(int) });
	private static readonly MethodInfo ButtonDown = AccessTools.Method(typeof(Input), nameof(Input.GetButtonDown), new[] { typeof(string) });
	private static readonly MethodInfo KeyDown = AccessTools.Method(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(KeyCode) });

	private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
	{
		foreach (CodeInstruction instruction in instructions)
		{
			if (instruction.Calls(MouseDown))
			{
				instruction.operand = AccessTools.Method(typeof(InputGuardDialogGate), nameof(GetMouseButtonDown));
			}
			else if (instruction.Calls(ButtonDown))
			{
				instruction.operand = AccessTools.Method(typeof(InputGuardDialogGate), nameof(GetButtonDown));
			}
			else if (instruction.Calls(KeyDown))
			{
				instruction.operand = AccessTools.Method(typeof(InputGuardDialogGate), nameof(GetKeyDown));
			}
			yield return instruction;
		}
	}

	private static bool GetMouseButtonDown(int button) => !InputGuard.BlockPointer && Input.GetMouseButtonDown(button);

	// 判定前选分支的窗口开着时，Esc 只关窗口：不让 Fungus 的 Cancel 顺手把这段剧情掐掉
	private static bool GetButtonDown(string button) => !InputGuard.BlockKeyboard && !(BranchAhead.OwnsEscape && button == "Cancel") && Input.GetButtonDown(button);

	private static bool GetKeyDown(KeyCode key) => !(BranchAhead.OwnsEscape && key == KeyCode.Escape) && Input.GetKeyDown(key);
}

/// <summary>在 Mod 的输入框里打字时，方向键 / 回车不让游戏的 UI 导航处理；咖啡伴侣窗口、判定前选分支的窗口开着时，手柄也不让游戏的界面跟着动。</summary>
[HarmonyPatch(typeof(InputSystemUIInputModule), "ProcessNavigation")]
public static class InputGuardNavigationGate
{
	private static bool Prefix() => !InputGuard.BlockKeyboard
		&& !(Gamepad.current != null && (LOMAssistant.Features.Story.StoryReviewPanel.OwnsEscape || BranchAhead.OwnsEscape));
}
