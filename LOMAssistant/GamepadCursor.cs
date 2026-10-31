using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LOMAssistant;

/// <summary>
/// 用手柄操作咖啡伴侣窗口：左摇杆 / 十字键移动鼠标指针（只在窗口范围内），A 键点击（按住可以拖动），右摇杆滚动。
/// 窗口是 IMGUI 画的，没有按钮焦点可以切换，所以用手柄模拟鼠标；点击用 Windows 的 SendInput，和真鼠标一样，
/// 悬停提示、拖动滚动也都照常。游戏用手柄时会锁住并隐藏指针，窗口开着时放开，关窗口后恢复。
/// </summary>
internal static class GamepadCursor
{
	private const float DeadZone = 0.15f;

	private static bool _active;
	private static bool _pressing;
	private static Vector2 _pos;
	private static bool _moving;
	private static CursorLockMode _lockBefore;
	private static bool _visibleBefore;

	/// <summary>窗口开着时每帧调用；area 是窗口的 IMGUI 坐标。</summary>
	public static void Update(Rect area)
	{
		Gamepad pad = Gamepad.current;
		Mouse mouse = Mouse.current;
		if (pad == null || mouse == null)
		{
			return;
		}
		if (!_active)
		{
			_active = true;
			_lockBefore = Cursor.lockState;
			_visibleBefore = Cursor.visible;
		}
		// 游戏发现在用手柄时会把指针锁住藏起来（每次切换输入设备都会），窗口开着就一直放开
		if (Cursor.lockState != CursorLockMode.None)
		{
			Cursor.lockState = CursorLockMode.None;
		}
		Cursor.visible = true;

		float dt = Time.unscaledDeltaTime;
		Vector2 stick = pad.leftStick.ReadValue();
		Vector2 move = stick.magnitude > DeadZone ? stick.normalized * (stick.magnitude * stick.magnitude) * Screen.height * 1.1f : Vector2.zero;
		move += pad.dpad.ReadValue() * Screen.height * 0.35f;
		if (move != Vector2.zero)
		{
			if (!_moving)
			{
				_pos = mouse.position.ReadValue();
				_moving = true;
			}
			// 屏幕坐标原点在左下，IMGUI 在左上
			_pos += move * dt;
			_pos.x = Mathf.Clamp(_pos.x, area.xMin + 2f, area.xMax - 2f);
			_pos.y = Mathf.Clamp(_pos.y, Screen.height - area.yMax + 2f, Screen.height - area.yMin - 2f);
			mouse.WarpCursorPosition(_pos);
		}
		else
		{
			_moving = false;
		}

		if (pad.buttonSouth.wasPressedThisFrame)
		{
			Send(MOUSEEVENTF_LEFTDOWN, 0);
			_pressing = true;
		}
		else if (_pressing && !pad.buttonSouth.isPressed)
		{
			Send(MOUSEEVENTF_LEFTUP, 0);
			_pressing = false;
		}

	}

	/// <summary>
	/// 右摇杆这一帧该滚多少像素（x 向右、y 向上为正）。由窗口里的各滚动区域直接加到滚动位置上：
	/// 模拟系统滚轮会被按格取整，滚起来一顿一顿的。
	/// </summary>
	public static Vector2 ScrollDelta()
	{
		Gamepad pad = Gamepad.current;
		if (!_active || pad == null)
		{
			return Vector2.zero;
		}
		Vector2 stick = pad.rightStick.ReadValue();
		if (stick.magnitude <= DeadZone)
		{
			return Vector2.zero;
		}
		return stick * stick.magnitude * Screen.height * 1.2f * Time.unscaledDeltaTime;
	}

	private enum PadStyle { Xbox, PlayStation, Switch }

	// 按手柄型号写键名：位置一样（下面的键点击、右边的键返回），PS、Switch 手柄上印的字不同
	private static PadStyle Style
	{
		get
		{
			string name = Gamepad.current?.GetType().Name ?? "";
			return name.Contains("DualShock") || name.Contains("DualSense") ? PadStyle.PlayStation
				: name.Contains("Switch") ? PadStyle.Switch
				: PadStyle.Xbox;
		}
	}

	/// <summary>窗口里的手柄操作提示。</summary>
	public static string Hint => Style switch
	{
		PadStyle.PlayStation => "手柄：× 点击 · 右摇杆滚动 · L1 / R1 切页 · ○ 关闭",
		PadStyle.Switch => "手柄：B 点击 · 右摇杆滚动 · L / R 切页 · A 关闭",
		_ => "手柄：A 点击 · 右摇杆滚动 · LB / RB 切页 · B 关闭",
	};

	/// <summary>设置页「手柄开关本窗口」三个选项的名字。</summary>
	public static string[] ToggleNames => Style switch
	{
		PadStyle.PlayStation => new[] { "L3", "R3", "L1+R1" },
		PadStyle.Switch => new[] { "L3", "R3", "L+R" },
		_ => new[] { "L3", "R3", "LB+RB" },
	};

	private static bool _chordUsed; // LB + RB 一起按拿来开关窗口了，松开时不再算切页

	/// <summary>开关窗口的手柄键按下了（设置页可选 L3、R3、LB + RB 一起按）。</summary>
	public static bool TogglePressed(Gamepad pad)
	{
		bool pressed;
		switch (LOMAssistant.Config.ConfigManager.GamepadPanelButton.Value)
		{
			case 1:
				pressed = pad.rightStickButton.wasPressedThisFrame;
				break;
			case 2:
				pressed = pad.leftShoulder.isPressed && pad.rightShoulder.isPressed && (pad.leftShoulder.wasPressedThisFrame || pad.rightShoulder.wasPressedThisFrame);
				_chordUsed |= pressed;
				break;
			default:
				pressed = pad.leftStickButton.wasPressedThisFrame;
				break;
		}
		if (!pad.leftShoulder.isPressed && !pad.rightShoulder.isPressed && !pad.leftShoulder.wasReleasedThisFrame && !pad.rightShoulder.wasReleasedThisFrame)
		{
			_chordUsed = false;
		}
		return pressed;
	}

	/// <summary>LB / RB 切页：松开时算（-1 / 1）；LB + RB 一起按开关窗口那次不算。</summary>
	public static int ShoulderStep(Gamepad pad)
	{
		if (_chordUsed)
		{
			return 0;
		}
		return pad.leftShoulder.wasReleasedThisFrame ? -1 : pad.rightShoulder.wasReleasedThisFrame ? 1 : 0;
	}

	/// <summary>窗口关了：松开还按着的左键，指针恢复成打开前的样子。</summary>
	public static void Release()
	{
		if (_pressing)
		{
			Send(MOUSEEVENTF_LEFTUP, 0);
			_pressing = false;
		}
		if (_active)
		{
			_active = false;
			_moving = false;
			Cursor.lockState = _lockBefore;
			Cursor.visible = _visibleBefore;
		}
	}

	private const uint INPUT_MOUSE = 0;
	private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
	private const uint MOUSEEVENTF_LEFTUP = 0x0004;

	[StructLayout(LayoutKind.Sequential)]
	private struct MouseInput
	{
		public int Dx;
		public int Dy;
		public int MouseData;
		public uint Flags;
		public uint Time;
		public IntPtr ExtraInfo;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Input
	{
		public uint Type;
		// 联合体里鼠标输入最长，只放它，大小就和系统要的一样
		public MouseInput Mouse;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern uint SendInput(uint count, Input[] inputs, int size);

	private static bool _unavailable;

	private static void Send(uint flags, int data)
	{
		if (_unavailable)
		{
			return;
		}
		try
		{
			var input = new Input { Type = INPUT_MOUSE, Mouse = new MouseInput { Flags = flags, MouseData = data } };
			SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Input)));
		}
		catch (Exception e)
		{
			_unavailable = true;
			FileLogger.LogError("手柄模拟鼠标不可用: " + e.Message);
		}
	}
}

/// <summary>
/// 咖啡伴侣窗口开着时，游戏不跟着切换「键盘鼠标 / 手柄」：用手柄移动指针、点击时两种输入来回交替，
/// 游戏每切一次就把指针锁住藏起来，指针会一闪一闪。关窗口后照常切换。
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(Mortal.Core.PlayerInputController), "OnControlsChanged")]
public static class GamepadControlsChangedGate
{
	private static bool Prefix() => !LOMAssistant.Features.Story.StoryReviewPanel.OwnsEscape && !LOMAssistant.BranchAhead.OwnsEscape;
}
