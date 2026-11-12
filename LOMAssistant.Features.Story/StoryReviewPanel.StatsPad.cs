using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LOMAssistant.Features.Story;

/// <summary>
/// 数值页的手柄直接改数：十字键在能改的数值格之间跳（上下按行、左右按格），当前格描一圈朱红框；
/// 右摇杆上下加减 1，左右加减一步（和这一行的加减按钮一样，一般是 5）；按住会连续加减。用过十字键以后右摇杆不再滚动页面，
/// 页面跟着当前格滚；左摇杆、A 键照旧移动指针、点击。
/// </summary>
public partial class StoryReviewPanel
{
	private readonly List<(string Id, Rect Rect)> _padRows = new List<(string, Rect)>();
	private readonly List<(string Id, Rect Rect)> _padRowsDrawing = new List<(string, Rect)>();
	private string _padFocus;
	private int _padColumns = 1;
	private int _padFrame = -1;
	private float _padRepeatAt;
	private Vector2Int _padHeld;
	private bool _padScrollTo;

	/// <summary>手柄在数值页上选着一格：十字键、右摇杆归数值页用（GamepadCursor 看这个）。</summary>
	internal static bool StatsPadActive => Instance != null && Instance._visible && Instance._tab == StatsTab && Instance._padFocus != null
		&& Instance._usageRow == null && !Instance._statPresetsOpen;

	/// <summary>每帧一次（在 Layout 事件里）：读十字键、右摇杆，移动当前格或加减它的数值。</summary>
	private void StatsPadInput(Rect view)
	{
		if (Event.current.type != EventType.Layout || _padFrame == Time.frameCount)
		{
			return;
		}
		_padFrame = Time.frameCount;
		Gamepad pad = Gamepad.current;
		if (pad == null || _padRows.Count == 0 || LOMAssistant.GamepadWheel.IsOpen)
		{
			return;
		}
		int index = _padRows.FindIndex(p => p.Id == _padFocus);
		// 十字键：上下按行（一行几格），左右按格
		int move = pad.dpad.up.wasPressedThisFrame ? -_padColumns : pad.dpad.down.wasPressedThisFrame ? _padColumns
			: pad.dpad.left.wasPressedThisFrame ? -1 : pad.dpad.right.wasPressedThisFrame ? 1 : 0;
		if (move != 0)
		{
			index = index < 0 ? 0 : Mathf.Clamp(index + move, 0, _padRows.Count - 1);
			_padFocus = _padRows[index].Id;
			_padScrollTo = true;
			GUIUtility.keyboardControl = 0;
			return;
		}
		if (index < 0)
		{
			_padFocus = null;
			return;
		}
		// 右摇杆：上下加减 1，左右加减一步（加减按钮的步长）；推住不放，过一会儿开始连续加减
		Vector2 stick = pad.rightStick.ReadValue();
		var dir = new Vector2Int(Mathf.Abs(stick.x) > 0.6f ? (int)Mathf.Sign(stick.x) : 0, Mathf.Abs(stick.y) > 0.6f ? (int)Mathf.Sign(stick.y) : 0);
		if (dir.x != 0 && dir.y != 0)
		{
			// 斜推：按推得多的那个方向算
			if (Mathf.Abs(stick.x) >= Mathf.Abs(stick.y)) dir.y = 0; else dir.x = 0;
		}
		if (dir == Vector2Int.zero)
		{
			_padHeld = Vector2Int.zero;
			return;
		}
		float now = Time.unscaledTime;
		if (dir == _padHeld && now < _padRepeatAt)
		{
			return;
		}
		_padRepeatAt = now + (dir == _padHeld ? 0.08f : 0.35f);
		_padHeld = dir;
		StatRow row = _statSections?.SelectMany(s => s.Rows).FirstOrDefault(r => r.Id == _padFocus && r.Set != null);
		if (row == null)
		{
			return;
		}
		int delta = dir.y != 0 ? dir.y : dir.x * row.Step;
		SetManual(row, row.Get() + delta);
		_statInputs.Remove(row.Id);
	}

	/// <summary>画完一行能改的数值：记下它的位置（下一帧十字键按这个顺序跳），是当前格就描框。</summary>
	private void StatsPadRow(StatRow row)
	{
		if (row.Set == null || Event.current.type != EventType.Repaint)
		{
			return;
		}
		Rect rect = GUILayoutUtility.GetLastRect();
		_padRowsDrawing.Add((row.Id, rect));
		if (row.Id == _padFocus)
		{
			Color old = GUI.color;
			GUI.color = PanelTheme.Cinnabar;
			float t = Mathf.Max(2f, S(3));
			GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, t), Texture2D.whiteTexture);
			GUI.DrawTexture(new Rect(rect.x, rect.yMax - t, rect.width, t), Texture2D.whiteTexture);
			GUI.DrawTexture(new Rect(rect.x, rect.y, t, rect.height), Texture2D.whiteTexture);
			GUI.DrawTexture(new Rect(rect.xMax - t, rect.y, t, rect.height), Texture2D.whiteTexture);
			GUI.color = old;
		}
	}

	/// <summary>一帧画完：换上这帧记下的各行位置；当前格不在了（换了分组、翻了页）就从第一格起。</summary>
	private void StatsPadEnd(int columns)
	{
		if (Event.current.type != EventType.Repaint)
		{
			return;
		}
		_padColumns = Mathf.Max(1, columns);
		_padRows.Clear();
		_padRows.AddRange(_padRowsDrawing);
		_padRowsDrawing.Clear();
		if (_padFocus != null && !_padRows.Any(p => p.Id == _padFocus))
		{
			_padFocus = _padRows.Count > 0 ? _padRows[0].Id : null;
		}
	}

	/// <summary>十字键刚移动过：把当前格滚进可见范围（滚动区高 viewHeight）。</summary>
	private void StatsPadScroll(float viewHeight)
	{
		if (!_padScrollTo || _padFocus == null)
		{
			return;
		}
		_padScrollTo = false;
		int i = _padRows.FindIndex(p => p.Id == _padFocus);
		if (i < 0)
		{
			return;
		}
		Rect rect = _padRows[i].Rect;
		float margin = S(12);
		if (rect.y - margin < _statsScroll.y)
		{
			_statsScroll.y = Mathf.Max(0f, rect.y - margin);
		}
		else if (rect.yMax + margin > _statsScroll.y + viewHeight)
		{
			_statsScroll.y = rect.yMax + margin - viewHeight;
		}
	}
}
