using System.Collections.Generic;
using System.Linq;
using LOMAssistant.Config;
using LOMAssistant.Features.Story;
using Mortal.Core;
using Mortal.Story;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace LOMAssistant.Features.Ai;

/// <summary>
/// 小伙伴的形象：按表情取图，告诉画的人取图上哪一块。内置的只有一种，从人设的立绘上裁；
/// 以后玩家自带的图（比如 Q 版）另写一个实现、在 For 里按设置挑，画的代码不用动。
/// </summary>
internal abstract class CompanionLook
{
	/// <summary>这个表情的图和要画的那一块（贴图坐标 0～1，原点左下）、宽高比；图还没载入返回 false。head 为真只要头。</summary>
	public abstract bool Frame(string mood, bool head, out Texture texture, out Rect uv, out float aspect);

	private static readonly Dictionary<string, CompanionLook> Built = new Dictionary<string, CompanionLook>();

	/// <summary>这位人设现在用的形象：现在只有人设自带的立绘这一种。以后加玩家自带的图，在这里按设置挑。</summary>
	public static CompanionLook For(AiPersona persona)
	{
		if (!Built.TryGetValue(persona.Id, out CompanionLook look))
		{
			Built[persona.Id] = look = new PortraitLook(persona);
		}
		return look;
	}
}

/// <summary>人设的立绘：半身从头顶裁到胸口，缩小时只裁头。立绘都是同一个像素比例（头约 280 像素宽），头在哪由 HeadAnchor 量。</summary>
internal sealed class PortraitLook : CompanionLook
{
	private readonly AiPersona _persona;

	public PortraitLook(AiPersona persona)
	{
		_persona = persona;
	}

	public override bool Frame(string mood, bool head, out Texture texture, out Rect uv, out float aspect)
	{
		texture = null;
		uv = default;
		aspect = 1f;
		Sprite sprite = StoryReviewPanel.AiPortrait(_persona, mood);
		if (sprite == null)
		{
			return false;
		}
		Rect tr = sprite.textureRect;
		Vector2 anchor = StoryReviewPanel.HeadAnchor(sprite, _persona.Folder + "/" + (string.IsNullOrEmpty(mood) ? _persona.Moods[0] : mood));
		// 只要头时裁一个正方形，从头顶量到下巴以下：头顶是头发、头巾的最高处，戴头巾、头发蓬的人脸在更下面，裁 300 只到眼睛
		float side = Mathf.Min(390f, Mathf.Min(tr.width, tr.height));
		float w = head ? side : Mathf.Min(420f, tr.width), h = head ? side : Mathf.Min(480f, tr.height);
		float left = Mathf.Clamp(anchor.x * tr.width - w / 2f, 0f, tr.width - w);
		float top = Mathf.Clamp(anchor.y * tr.height - (head ? 14f : 26f), 0f, tr.height - h);
		texture = sprite.texture;
		uv = new Rect((tr.x + left) / texture.width, (tr.yMax - top - h) / texture.height, w / texture.width, h / texture.height);
		aspect = w / h;
		return true;
	}
}

/// <summary>
/// 画面上的小伙伴：半身像（没选人设时是朱砂印「攻」），不带名字，下缘渐渐淡出；默认在右下角、对白框右侧，可以拖。
/// 说话时在旁边出气泡，只有她说的话，不写系统说明；气泡避开选项、掷骰线路和对白的字，避不开就缩成一个小点，鼠标移上去再展开。
/// 点小伙伴或气泡打开 AI 页追问；鼠标移上去出「暂停发言」「缩小」「关闭陪玩」；有选项、掷骰时多一个「这里怎么选？」。
/// 只有它自己和气泡接鼠标，别处的点击照常给游戏。
/// </summary>
internal sealed class CompanionPet : MonoBehaviour
{
	private const int ControlId = 0x434D5054;

	private static CompanionPet _instance;
	private static readonly Vector3[] Corners = new Vector3[4];

	// 形象右下角在屏幕上的位置；大小随展开、收起变，拿右下角定位，收起时不会跳开
	private Vector2 _corner;
	private bool _placed, _pressed, _dragged, _menuOpen;
	private int _screenWidth, _screenHeight;
	private Vector2 _pressAt, _offset;
	private float _scale = -1f, _opacity = -1f, _hoverUntil, _avoidAt = -10f;
	private int _fontMode = -1;
	private GUIStyle _box, _text, _button, _seal, _chip;
	private Texture _lastTexture;
	private Rect _lastUv;
	private float _lastAspect = 0.875f;
	private string _lastLook = "";
	private readonly List<Rect> _avoid = new List<Rect>();

	public static void Ensure(GameObject host)
	{
		if (_instance == null)
		{
			_instance = host.GetComponent<CompanionPet>() ?? host.AddComponent<CompanionPet>();
		}
	}

	private void Update()
	{
		if (GUIUtility.keyboardControl == 0 && AiCompanion.Supported && InputGuard.HotkeyDown(ConfigManager.AiCompanionKey.Value))
		{
			ConfigManager.AiCompanion.Value = !ConfigManager.AiCompanion.Value;
		}
		AiCompanion.Tick();
		// 松开鼠标的那一帧这里先跑到：没拖动就算点了一下
		if (_pressed && !Input.GetMouseButton(0))
		{
			EndPress(click: !_dragged);
		}
	}

	private void OnGUI()
	{
		if (!AiCompanion.On || PlayerStatManagerData.Instance == null)
		{
			return;
		}
		string scene = SceneManager.GetActiveScene().name;
		float scale = PanelTheme.UiScale;
		if (AiCompanion.Hidden(scene) || scale <= 0f)
		{
			return;
		}
		EnsureStyles(scale);
		float now = Time.realtimeSinceStartup;
		if (now - _avoidAt > 0.3f)
		{
			_avoidAt = now;
			RefreshAvoid(scene);
		}
		AiPersona persona = AiCompanion.Persona;
		bool collapsed = ConfigManager.AiCompanionCollapsed.Value;
		Rect figure = FigureRect(persona, collapsed, scale);
		Vector2 mouse = Event.current.mousePosition;

		// 鼠标在它身上（或刚离开一会儿）：旁边出几个小按钮
		Rect buttons = ButtonsRect(figure, scale);
		var reach = Rect.MinMaxRect(Mathf.Min(figure.xMin, buttons.xMin), Mathf.Min(figure.yMin, buttons.yMin), Mathf.Max(figure.xMax, buttons.xMax), figure.yMax);
		if (reach.Contains(mouse) || _pressed)
		{
			_hoverUntil = now + 0.6f;
		}
		bool hover = now < _hoverUntil && !_dragged;
		if (hover)
		{
			DrawButtons(buttons, scale);
		}
		if (_menuOpen && !_dragged)
		{
			DrawAsk(figure, scale);
		}
		HandlePointer(figure, scale);
		DrawFigure(figure, persona, collapsed, scale, now);
		DrawBubble(figure, scale, now, mouse);
		InputGuard.Report("Companion", hover ? reach : figure);
	}

	// ---------------- 形象 ----------------

	private Rect FigureRect(AiPersona persona, bool collapsed, float scale)
	{
		if (!_placed || _screenWidth != Screen.width || _screenHeight != Screen.height)
		{
			_screenWidth = Screen.width;
			_screenHeight = Screen.height;
			float x = ConfigManager.AiCompanionX.Value, y = ConfigManager.AiCompanionY.Value;
			_corner = new Vector2(x >= 0f ? x * Screen.width : Screen.width - 14f * scale, y >= 0f ? y * Screen.height : Screen.height - 12f * scale);
			_placed = true;
		}
		float w, h;
		if (collapsed)
		{
			w = h = 84f * scale;
		}
		else if (persona == null)
		{
			w = h = 104f * scale;
		}
		else
		{
			w = 150f * scale;
			h = w / _lastAspect;
		}
		_corner.x = Mathf.Clamp(_corner.x, w + 4f, Screen.width - 4f);
		_corner.y = Mathf.Clamp(_corner.y, h + 4f, Screen.height - 4f);
		return new Rect(_corner.x - w, _corner.y - h, w, h);
	}

	private void DrawFigure(Rect figure, AiPersona persona, bool collapsed, float scale, float now)
	{
		if (Event.current.type != EventType.Repaint)
		{
			return;
		}
		Color before = GUI.color;
		// 不说话的时候（暂停、到上限）画淡一些
		GUI.color = new Color(1f, 1f, 1f, AiCompanion.Muted || AiCompanion.Capped ? 0.55f : 1f);
		if (persona == null)
		{
			GUI.Label(figure, "攻", _seal);
		}
		else
		{
			string mood = AiCompanion.Mood;
			string key = persona.Id + "|" + mood + "|" + collapsed;
			if (CompanionLook.For(persona).Frame(mood, collapsed, out Texture texture, out Rect uv, out float aspect))
			{
				_lastTexture = texture;
				_lastUv = uv;
				_lastAspect = collapsed ? _lastAspect : aspect;
				_lastLook = key;
			}
			else if (!_lastLook.StartsWith(persona.Id + "|") || _lastLook.EndsWith("|" + collapsed) == false)
			{
				// 换了人或换了展开、收起，新图还没载入：先不画，免得画成上一位
				_lastTexture = null;
			}
			if (_lastTexture != null)
			{
				DrawFading(figure, _lastTexture, _lastUv, collapsed ? 0.1f : 0.14f);
			}
		}
		GUI.color = before;
		// 正在想：头顶冒三个点
		if (AiCompanion.Busy && AiCompanion.Current == null)
		{
			string dots = new string('·', 1 + (int)(now * 2.5f) % 3);
			GUI.Box(new Rect(figure.x - 6f * scale, figure.y - 6f * scale, 44f * scale, 28f * scale), dots, PanelTheme.Sync(_chip));
		}
	}

	// 半身像不压名牌：最下面一段一条条越画越淡，胸口那道切边就看不出来了（fade 是淡出那段占的高度比例）
	private static void DrawFading(Rect dest, Texture texture, Rect uv, float fade)
	{
		// 全部对齐到整像素、淡出那段一行像素画一条：位置带小数时每块各自取整，块和块之间会差出一行（漏出底下的画面成一道白线，或叠画成一道深线）
		int x = Mathf.RoundToInt(dest.x), y = Mathf.RoundToInt(dest.y), w = Mathf.RoundToInt(dest.width), h = Mathf.Max(2, Mathf.RoundToInt(dest.height));
		int rows = Mathf.Clamp(Mathf.RoundToInt(h * fade), 1, h - 1), solid = h - rows;
		GUI.DrawTextureWithTexCoords(new Rect(x, y, w, solid), texture, new Rect(uv.x, uv.y + uv.height * rows / h, uv.width, uv.height * solid / h));
		Color before = GUI.color;
		for (int i = 0; i < rows; i++)
		{
			// 从上往下第 solid + i 行；贴图坐标的原点在左下，所以越往下 y 越小
			GUI.color = new Color(before.r, before.g, before.b, before.a * (1f - (i + 0.5f) / rows));
			GUI.DrawTextureWithTexCoords(new Rect(x, y + solid + i, w, 1f), texture, new Rect(uv.x, uv.y + uv.height * (rows - 1 - i) / h, uv.width, uv.height / h));
		}
		GUI.color = before;
	}

	// 按住拖动；没拖就松开算点了一下：打开 AI 页追问
	private void HandlePointer(Rect figure, float scale)
	{
		Event e = Event.current;
		if (e == null || e.button != 0)
		{
			return;
		}
		if (e.type == EventType.MouseDown && figure.Contains(e.mousePosition))
		{
			_pressed = true;
			_dragged = false;
			_pressAt = e.mousePosition;
			_offset = e.mousePosition - _corner;
			GUIUtility.hotControl = ControlId;
			e.Use();
		}
		else if (e.type == EventType.MouseDrag && GUIUtility.hotControl == ControlId)
		{
			if ((e.mousePosition - _pressAt).sqrMagnitude > 36f * scale * scale)
			{
				_dragged = true;
			}
			if (_dragged)
			{
				_corner = e.mousePosition - _offset;
			}
			e.Use();
		}
		else if (e.rawType == EventType.MouseUp && GUIUtility.hotControl == ControlId)
		{
			EndPress(click: !_dragged);
			e.Use();
		}
	}

	private void EndPress(bool click)
	{
		if (GUIUtility.hotControl == ControlId)
		{
			GUIUtility.hotControl = 0;
		}
		if (_dragged)
		{
			ConfigManager.AiCompanionX.Value = _corner.x / Screen.width;
			ConfigManager.AiCompanionY.Value = _corner.y / Screen.height;
		}
		bool wasPressed = _pressed;
		_pressed = _dragged = false;
		if (click && wasPressed)
		{
			FollowUp(null);
		}
	}

	/// <summary>打开 AI 页、切到陪玩的这位；刚说的那句话带过去。question 不空就直接问。</summary>
	private static void FollowUp(string question)
	{
		AiCompanion.Remark last = AiCompanion.Last;
		// 说了有一阵的话不带过去：玩家多半是想问别的
		if (last != null && Time.realtimeSinceStartup - last.Until > 180f)
		{
			last = null;
		}
		StoryReviewPanel.OpenAiFor(AiCompanion.Persona?.Id ?? AiPersona.AssistantId, last, question);
		AiCompanion.Dismiss();
	}

	// ---------------- 小按钮 ----------------

	private static readonly string[] ButtonNames = new string[3];

	private Rect ButtonsRect(Rect figure, float scale)
	{
		// 「暂停发言」是临时的（到这段剧情结束），不叫「安静」：那是话多程度里一档的名字
		ButtonNames[0] = AiCompanion.Muted ? "恢复发言" : "暂停发言";
		ButtonNames[1] = ConfigManager.AiCompanionCollapsed.Value ? "放大" : "缩小";
		ButtonNames[2] = "关闭陪玩";
		float width = ButtonNames.Sum(n => ButtonWidth(n, scale)) + 4f * scale * 2f, height = 30f * scale;
		// 放在形象靠屏幕中间的那一侧、贴着底边；底边那一格被「这里怎么选？」占着时排在它上面一行：
		// 那个按钮一直在原地，鼠标移上来、移开都不挪动
		bool leftSide = figure.center.x > Screen.width / 2f;
		float y = figure.yMax - height - (_menuOpen ? height + 6f * scale : 0f);
		return new Rect(leftSide ? figure.x - 6f * scale - width : figure.xMax + 6f * scale, y, width, height);
	}

	private float ButtonWidth(string name, float scale) => _button.CalcSize(new GUIContent(name)).x + 8f * scale;

	private void DrawButtons(Rect r, float scale)
	{
		float x = r.x;
		for (int i = 0; i < ButtonNames.Length; i++)
		{
			float w = ButtonWidth(ButtonNames[i], scale);
			if (GUI.Button(new Rect(x, r.y, w, r.height), ButtonNames[i], _button))
			{
				switch (i)
				{
					case 0: AiCompanion.ToggleMute(); break;
					case 1: ConfigManager.AiCompanionCollapsed.Value = !ConfigManager.AiCompanionCollapsed.Value; break;
					default: ConfigManager.AiCompanion.Value = false; break;
				}
			}
			x += w + 4f * scale;
		}
		// 按钮上面一行小字：它不说话的时候写明为什么；平时没有（用量在 AI 页看，不在游戏画面上给玩家看）
		string info = AiCompanion.Capped ? "已达 token 上限，本次游戏不再发言"
			: AiCompanion.Muted ? "已暂停发言，本段剧情结束后恢复"
			: AiCompanion.LastError.Length > 0 ? "发言失败　" + AiCompanion.LastError : "";
		if (info.Length > 0 && Event.current.type == EventType.Repaint)
		{
			var content = new GUIContent(info);
			Vector2 size = _chip.CalcSize(content);
			float left = r.center.x > Screen.width / 2f ? r.xMax - size.x : r.x;
			GUI.Box(new Rect(Mathf.Clamp(left, 0f, Mathf.Max(0f, Screen.width - size.x)), r.y - size.y - 4f * scale, size.x, size.y), content, PanelTheme.Sync(_chip));
		}
	}

	// 画面上有选项、掷骰：给一个直接问「这里怎么选」的按钮（走 AI 页原有的问答，会查资料）
	private void DrawAsk(Rect figure, float scale)
	{
		const string name = "这里怎么选？";
		float w = ButtonWidth(name, scale), h = 30f * scale;
		bool leftSide = figure.center.x > Screen.width / 2f;
		// 一直贴着形象底边，悬停时出来的那排小按钮排在它上面
		var r = new Rect(leftSide ? figure.x - 6f * scale - w : figure.xMax + 6f * scale, figure.yMax - h, w, h);
		if (GUI.Button(r, name, _button))
		{
			FollowUp("这里应该怎么选？");
		}
		InputGuard.Report("CompanionAsk", r);
	}

	// ---------------- 气泡 ----------------

	private void DrawBubble(Rect figure, float scale, float now, Vector2 mouse)
	{
		AiCompanion.Remark remark = AiCompanion.Current;
		if (remark == null || remark.Text.Length == 0 || _dragged)
		{
			return;
		}
		float width = Mathf.Min(460f * scale, Screen.width * 0.3f);
		float inner = width - _box.padding.horizontal;
		// 右上角留给「×」的位置不排字
		string text = StoryReviewPanel.Wrap(TextConverter.Verbatim + StoryReviewPanel.RichFromMarkdown(remark.Text), _text, inner - 22f * scale);
		float height = _box.padding.vertical + _text.CalcHeight(new GUIContent(text), inner);
		bool right = figure.center.x > Screen.width / 2f;
		// 先放形象上方；会挡住选项或对白的字就一直往上挪到挡不着的地方（形象挨着对白框时，正上方总会碰到对白那一块）。
		// 往上没地方了再试形象靠屏幕中间的那一侧；都不行才缩成一个小点
		var above = Clamped(new Rect(right ? figure.xMax - width : figure.x, figure.y - height - 8f * scale, width, height));
		var beside = new Rect(right ? figure.x - width - 8f * scale : figure.xMax + 8f * scale, figure.yMax - height, width, height);
		Rect spot = above;
		if (!SlideUp(ref spot, 8f * scale))
		{
			spot = Fits(beside) ? beside : default;
		}
		if (spot.width <= 0f)
		{
			var chip = new Rect(right ? figure.xMax - 44f * scale : figure.x, figure.y - 34f * scale, 44f * scale, 28f * scale);
			InputGuard.Report("CompanionBubble", chip);
			if (!chip.Contains(mouse))
			{
				if (Event.current.type == EventType.Repaint)
				{
					GUI.Box(chip, "…", PanelTheme.Sync(_chip));
				}
				return;
			}
			// 鼠标在小点上：照样展开在上方，玩家自己要看
			spot = Clamped(above);
		}
		else
		{
			InputGuard.Report("CompanionBubble", spot);
		}
		// 鼠标停在气泡上时不消失
		if (!remark.Writing && spot.Contains(mouse))
		{
			remark.Until = Mathf.Max(remark.Until, now + 2f);
		}
		Color before = GUI.color;
		// 快消失时淡出
		GUI.color = new Color(1f, 1f, 1f, remark.Writing ? 1f : Mathf.Clamp01((remark.Until - now) / 0.4f));
		if (Event.current.type == EventType.Repaint)
		{
			GUI.Box(spot, GUIContent.none, PanelTheme.Sync(_box));
			float y = spot.y + _box.padding.top;
			GUI.Label(new Rect(spot.x + _box.padding.left, y, inner, spot.yMax - y), text, _text);
		}
		// 两个按钮每次都画、先后不变：右上角的「×」先接点击，其余地方点了去 AI 页追问
		var close = new Rect(spot.xMax - 26f * scale, spot.y + 4f * scale, 22f * scale, 22f * scale);
		bool closed = GUI.Button(close, "×", _button);
		bool asked = GUI.Button(spot, GUIContent.none, GUIStyle.none);
		GUI.color = before;
		if (closed)
		{
			AiCompanion.Dismiss();
		}
		else if (asked)
		{
			FollowUp(null);
		}
	}

	private bool Fits(Rect r) => r.x >= 0f && r.y >= 0f && r.xMax <= Screen.width && r.yMax <= Screen.height && !_avoid.Any(a => a.Overlaps(r));

	// 碰到要让开的东西就挪到它上面，直到不碰；挪出屏幕顶上了返回 false
	private bool SlideUp(ref Rect r, float gap)
	{
		for (int tries = 0; tries <= _avoid.Count; tries++)
		{
			float top = float.MaxValue;
			foreach (Rect a in _avoid)
			{
				if (a.Overlaps(r))
				{
					top = Mathf.Min(top, a.y);
				}
			}
			if (top == float.MaxValue)
			{
				return r.y >= 0f;
			}
			r.y = top - r.height - gap;
			if (r.y < 0f)
			{
				return false;
			}
		}
		return false;
	}

	private static Rect Clamped(Rect r)
	{
		r.x = Mathf.Clamp(r.x, 0f, Mathf.Max(0f, Screen.width - r.width));
		r.y = Mathf.Clamp(r.y, 0f, Mathf.Max(0f, Screen.height - r.height));
		return r;
	}

	// 气泡要让开的地方：亮着的选项按钮、掷骰的线路、对白的字
	private void RefreshAvoid(string scene)
	{
		_avoid.Clear();
		_menuOpen = false;
		if (scene != "Story" && scene != "Free")
		{
			return;
		}
		try
		{
			foreach (Button button in OptionInfoPatch.ButtonKeys.Keys)
			{
				if (button != null && button.isActiveAndEnabled)
				{
					_avoid.Add(ScreenRect((RectTransform)button.transform));
					_menuOpen = true;
				}
			}
			foreach (DiceMenuDialog dice in FindObjectsOfType<DiceMenuDialog>())
			{
				if (!dice.isActiveAndEnabled)
				{
					continue;
				}
				_menuOpen = true;
				foreach (Button button in dice.CachedButtons ?? new Button[0])
				{
					if (button != null && button.gameObject.activeInHierarchy)
					{
						_avoid.Add(ScreenRect((RectTransform)button.transform));
					}
				}
			}
			foreach (Fungus.SayDialog say in FindObjectsOfType<Fungus.SayDialog>())
			{
				if (!say.isActiveAndEnabled)
				{
					continue;
				}
				Text story = say.GetComponentsInChildren<Text>().FirstOrDefault(t => t.name == "StoryText");
				if (story != null && !string.IsNullOrEmpty(story.text))
				{
					_avoid.Add(ScreenRect(story.rectTransform));
				}
			}
		}
		catch (System.Exception ex)
		{
			FileLogger.Log("[AI] 主动陪玩量选项位置失败（已忽略）: " + ex.Message);
		}
	}

	// 一个 uGUI 物体在屏幕上占的地方（IMGUI 坐标，原点左上）
	private static Rect ScreenRect(RectTransform rt)
	{
		Canvas canvas = rt.GetComponentInParent<Canvas>();
		Canvas root = canvas != null ? canvas.rootCanvas : null;
		Camera camera = root != null && root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
		rt.GetWorldCorners(Corners);
		Vector2 a = RectTransformUtility.WorldToScreenPoint(camera, Corners[0]), b = RectTransformUtility.WorldToScreenPoint(camera, Corners[2]);
		return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Screen.height - Mathf.Max(a.y, b.y), Mathf.Max(a.x, b.x), Screen.height - Mathf.Min(a.y, b.y));
	}

	// ---------------- 样式 ----------------

	private void EnsureStyles(float scale)
	{
		// 和画面上其他提示框一个样子：墨色底、宣纸色字、褐色细边，底色不透明度跟「提示框不透明度」
		if (_box != null && !ModFont.NeedsRebuild(scale, _scale, _fontMode) && Mathf.Approximately(_opacity, ConfigManager.PopupOpacity.Value))
		{
			return;
		}
		_scale = scale;
		_fontMode = ModFont.Mode;
		_opacity = ConfigManager.PopupOpacity.Value;
		_box = PanelTheme.Toast(scale, 18);
		_box.padding = new RectOffset(Mathf.RoundToInt(14f * scale), Mathf.RoundToInt(14f * scale), Mathf.RoundToInt(10f * scale), Mathf.RoundToInt(12f * scale));
		_text = PanelTheme.Label(scale, 19, PanelTheme.ToastText);
		_chip = PanelTheme.Toast(scale, 14);
		_chip.wordWrap = false;
		_chip.alignment = TextAnchor.MiddleCenter;
		_button = PanelTheme.ToastButton(scale, 14);
		_button.normal.background = PanelTheme.Framed(new Color(0.08f, 0.07f, 0.06f, 0.85f), new Color(0.55f, 0.45f, 0.33f, 0.9f));
		_button.padding = new RectOffset(Mathf.RoundToInt(6f * scale), Mathf.RoundToInt(6f * scale), 0, 0);
		_button.margin = new RectOffset(0, 0, 0, 0);
		// 没选人设时的朱砂印「攻」，和 AI 页人设那一排的一样
		Color edge = new Color(PanelTheme.Cinnabar.r * 0.7f, PanelTheme.Cinnabar.g * 0.7f, PanelTheme.Cinnabar.b * 0.7f, 1f);
		_seal = PanelTheme.Label(scale, 44, PanelTheme.OnCinnabar, false);
		_seal.alignment = TextAnchor.MiddleCenter;
		_seal.fontStyle = FontStyle.Bold;
		_seal.normal.background = PanelTheme.Framed(PanelTheme.Cinnabar, edge);
		_seal.border = new RectOffset(2, 2, 2, 2);
		_seal.clipping = TextClipping.Clip;
	}
}
