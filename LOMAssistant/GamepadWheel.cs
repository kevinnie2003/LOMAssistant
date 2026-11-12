using System;
using HarmonyLib;
using LOMAssistant.Features.Story;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace LOMAssistant;

/// <summary>
/// 手柄转轮：在剧情和养成地图上（游戏菜单、状态页、回想都没开，咖啡伴侣窗口也关着）按住 LB，屏幕中间弹出 8 格圆环；
/// 按着 LB 时右摇杆推向一格再松开（回到中间）就执行那一格；松开 LB 或按 B 就收起。转轮开着时游戏收不到手柄输入。
/// LB 在游戏里是战役技能 1 和菜单往左翻页，这两处本来不弹转轮。
/// </summary>
public class GamepadWheel : MonoBehaviour
{
	private sealed class Slot
	{
		public string Label; // 圆环上的短名字
		public string Name; // 选中时圆心写的全名
		public Action Run;
	}

	// 从正上方起顺时针
	private static readonly Slot[] Slots =
	{
		new Slot { Label = "此刻", Name = "此刻", Run = () => StoryReviewPanel.OpenTab(0) },
		new Slot { Label = "剧情图", Name = "剧情图", Run = () => StoryReviewPanel.OpenTab(2) },
		new Slot { Label = "数值", Name = "数值", Run = () => StoryReviewPanel.OpenTab(6) },
		new Slot { Label = "设置", Name = "设置", Run = () => StoryReviewPanel.OpenTab(8) },
		new Slot { Label = "快进已读", Name = "一键快进已读对话", Run = LOMAssistant.Features.Extras.SkipReadToggle.Toggle },
		new Slot { Label = "回退到判定前", Name = "回退到判定前", Run = () => LOMAssistant.Features.AnytimeSave.AnytimeSave.Instance?.Rollback() },
		new Slot { Label = "读最新存档", Name = "随时存档：读取最新存档", Run = () => LOMAssistant.Features.AnytimeSave.AnytimeSave.Instance?.LoadLatest() },
		new Slot { Label = "存新档", Name = "随时存档：存新档", Run = () => LOMAssistant.Features.AnytimeSave.AnytimeSave.Instance?.SaveSlot() },
	};

	private const int Size = 512; // 圆环贴图边长
	private const float Outer = 0.5f, Inner = 0.27f, Hub = 0.235f; // 半径（占边长的比例）
	private const float Sector = 360f / 8f;

	private static GamepadWheel _instance;
	private bool _open;
	private int _selected = -1;
	private Texture2D _ring, _wedge, _disc, _line;
	private float _scale = -1f;
	private int _font = -1;
	private bool _dark;
	private GUIStyle _label, _labelOn, _center, _hint;

	/// <summary>转轮开着：游戏的输入回调都挡下（InputGuard.Suppress）。</summary>
	public static bool IsOpen => _instance != null && _instance._open;

	public static void Ensure(GameObject host)
	{
		if (_instance == null)
		{
			_instance = host.GetComponent<GamepadWheel>() ?? host.AddComponent<GamepadWheel>();
		}
	}

	private void Update()
	{
		Gamepad pad = Gamepad.current;
		if (pad == null || !LOMAssistant.Config.ConfigManager.EnableGamepadWheel.Value)
		{
			_open = false;
			return;
		}
		if (GamepadCursor.IgnoreLbRelease && !pad.leftShoulder.isPressed && !pad.leftShoulder.wasReleasedThisFrame)
		{
			GamepadCursor.IgnoreLbRelease = false;
		}
		if (!_open)
		{
			if (pad.leftShoulder.wasPressedThisFrame && !pad.rightShoulder.isPressed && Allowed())
			{
				_open = true;
				_selected = -1;
			}
			return;
		}
		// 只在按着 LB 的时候显示：松开 LB、按 B 就收起（不执行）
		if (!pad.leftShoulder.isPressed || pad.buttonEast.wasPressedThisFrame || !Allowed())
		{
			_open = false;
			_selected = -1;
			return;
		}
		// 推出去一半以上算指着某格；指过某格以后摇杆回到中间就执行它
		Vector2 stick = pad.rightStick.ReadValue();
		if (stick.magnitude > 0.5f)
		{
			float angle = Mathf.Atan2(stick.x, stick.y) * Mathf.Rad2Deg; // 正上方 0°，顺时针为正
			_selected = Mathf.RoundToInt((angle + 360f) % 360f / Sector) % Slots.Length;
		}
		else if (stick.magnitude < 0.2f && _selected >= 0)
		{
			int run = _selected;
			_open = false;
			_selected = -1;
			// LB 还按着：松开时窗口别当成往左切页
			GamepadCursor.IgnoreLbRelease = true;
			try
			{
				Slots[run].Run();
			}
			catch (Exception e)
			{
				FileLogger.LogWarning("[手柄转轮] 执行「" + Slots[run].Name + "」失败: " + e.Message);
			}
		}
	}

	private void OnDisable() => _open = false;

	/// <summary>现在能弹转轮：剧情或养成地图，游戏的菜单、状态页、存档、回想都没开，咖啡伴侣的窗口、隐藏界面也没开。</summary>
	private static bool Allowed()
	{
		if (UiHider.Active || StoryReviewPanel.OwnsEscape || EventPicker.OwnsEscape || GUIUtility.keyboardControl != 0)
		{
			return false;
		}
		string scene = SceneManager.GetActiveScene().name;
		if (scene == "Story")
		{
			var story = Mortal.Story.StoryManager.Instance;
			return story != null && story.EnableAction && !Traverse.Create(story).Field("_panelOpen").GetValue<bool>()
				&& !Traverse.Create(story).Field("_logOpen").GetValue<bool>();
		}
		if (scene == "Free")
		{
			var free = Mortal.Free.FreeManager.Instance;
			return free != null && free.EnableHotKey;
		}
		return false;
	}

	private void OnGUI()
	{
		if (!_open || Event.current.type != EventType.Repaint)
		{
			return;
		}
		float scale = PanelTheme.UiScale;
		if (scale <= 0f)
		{
			return;
		}
		Styles(scale);
		Textures();
		int depth = GUI.depth;
		GUI.depth = -2000;
		Color old = GUI.color;
		GUI.color = new Color(0f, 0f, 0f, 0.35f);
		GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);

		float d = Mathf.Min(560f * scale, Screen.height * 0.72f);
		Vector2 c = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
		var box = new Rect(c.x - d / 2f, c.y - d / 2f, d, d);

		// 圆环底、选中的扇形、格子之间的细线、圆心
		GUI.color = WithAlpha(PanelTheme.Paper, 0.94f);
		GUI.DrawTexture(box, _ring);
		if (_selected >= 0)
		{
			Matrix4x4 m = GUI.matrix;
			GUIUtility.RotateAroundPivot(_selected * Sector, c);
			GUI.color = PanelTheme.Cinnabar;
			GUI.DrawTexture(box, _wedge);
			GUI.matrix = m;
		}
		GUI.color = WithAlpha(PanelTheme.Edge, 0.7f);
		for (int i = 0; i < Slots.Length; i++)
		{
			Matrix4x4 m = GUI.matrix;
			GUIUtility.RotateAroundPivot(i * Sector + Sector / 2f, c);
			GUI.DrawTexture(new Rect(c.x - 0.75f, c.y - d * Outer, 1.5f, d * (Outer - Inner)), _line);
			GUI.matrix = m;
		}
		GUI.color = WithAlpha(PanelTheme.PaperDeep, 0.96f);
		GUI.DrawTexture(box, _disc);
		GUI.color = old;

		// 格子名字：放在圆环中线上
		float mid = d * (Outer + Inner) / 2f;
		float w = d * (Outer - Inner) * 1.35f, h = d * (Outer - Inner) * 0.9f;
		for (int i = 0; i < Slots.Length; i++)
		{
			float a = i * Sector * Mathf.Deg2Rad;
			Vector2 p = c + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * mid;
			GUI.Label(new Rect(p.x - w / 2f, p.y - h / 2f, w, h), TextConverter.ForDisplay(Slots[i].Label), i == _selected ? _labelOn : _label);
		}
		float hub = d * Hub * 2f * 0.8f;
		var hubRect = new Rect(c.x - hub / 2f, c.y - hub / 2f, hub, hub);
		if (_selected >= 0)
		{
			GUI.Label(hubRect, TextConverter.ForDisplay(Slots[_selected].Name), _center);
		}
		else
		{
			GUI.Label(hubRect, TextConverter.ForDisplay("右摇杆选择，松开执行"), _hint);
		}
		GUI.depth = depth;
	}

	private static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, a);

	// 圆环、扇形、圆心都是白色贴图，画的时候用 GUI.color 上色；边缘按像素算覆盖率做抗锯齿
	private void Textures()
	{
		if (_ring != null)
		{
			return;
		}
		_ring = Make((r, deg) => Band(r, Inner, Outer));
		_wedge = Make((r, deg) => Band(r, Inner, Outer) * Arc(deg, r));
		_disc = Make((r, deg) => Band(r, -1f, Hub));
		_line = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
		_line.SetPixel(0, 0, Color.white);
		_line.Apply();
	}

	private static float Band(float r, float inner, float outer)
	{
		float px = 1f / Size; // 一个像素
		return Mathf.Clamp01((r - inner) / px + 0.5f) * Mathf.Clamp01((outer - r) / px + 0.5f);
	}

	// 正上方那一格（-22.5° 到 22.5°），按弧长换成像素做抗锯齿
	private static float Arc(float deg, float r)
	{
		float edge = Sector / 2f - Mathf.Abs(Mathf.DeltaAngle(0f, deg));
		float px = r > 0f ? Mathf.Rad2Deg / (r * Size) : 1f;
		return Mathf.Clamp01(edge / px + 0.5f);
	}

	private static Texture2D Make(Func<float, float, float> alpha)
	{
		var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
		var pixels = new Color32[Size * Size];
		for (int y = 0; y < Size; y++)
		{
			for (int x = 0; x < Size; x++)
			{
				float dx = (x + 0.5f) / Size - 0.5f, dy = (y + 0.5f) / Size - 0.5f;
				float r = Mathf.Sqrt(dx * dx + dy * dy);
				float deg = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg; // 贴图 y 朝上：正上方 0°，顺时针为正
				pixels[y * Size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(alpha(r, deg) * 255f));
			}
		}
		tex.SetPixels32(pixels);
		tex.Apply();
		return tex;
	}

	private void OnDestroy()
	{
		foreach (Texture2D t in new[] { _ring, _wedge, _disc, _line })
		{
			if (t != null)
			{
				Destroy(t);
			}
		}
	}

	private void Styles(float scale)
	{
		if (Mathf.Approximately(scale, _scale) && _dark == PanelTheme.Dark && _font == LOMAssistant.Config.ConfigManager.PanelFont.Value)
		{
			return;
		}
		_scale = scale;
		_dark = PanelTheme.Dark;
		_font = LOMAssistant.Config.ConfigManager.PanelFont.Value;
		_label = PanelTheme.Label(scale, 20, PanelTheme.Ink, wrap: true);
		_label.alignment = TextAnchor.MiddleCenter;
		_label.fontStyle = FontStyle.Bold;
		_labelOn = new GUIStyle(_label);
		_labelOn.normal.textColor = PanelTheme.OnCinnabar;
		_center = PanelTheme.Label(scale, 22, PanelTheme.Ink, wrap: true);
		_center.alignment = TextAnchor.MiddleCenter;
		_center.fontStyle = FontStyle.Bold;
		_hint = PanelTheme.Label(scale, 17, PanelTheme.InkMuted, wrap: true);
		_hint.alignment = TextAnchor.MiddleCenter;
	}
}
