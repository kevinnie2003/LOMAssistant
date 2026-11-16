using System.Collections.Generic;
using UnityEngine;

namespace LOMAssistant.Features.Story;

public partial class StoryReviewPanel
{
	/// <summary>
	/// 会越积越长的列表（随时存档、节点存档）只画看得见的附近几屏：量过高度的、离得远的只留空；
	/// 没量过的先按常见高度留空，滚到附近再画、再量。每一行包在 Row() 里画，画完调 Measure，整轮画完调 End。
	/// </summary>
	private sealed class LazyRows
	{
		private sealed class Height
		{
			public string Key;
			public float Value = -1f;
		}

		private readonly Dictionary<string, Height> _heights = new Dictionary<string, Height>();
		private readonly List<(Height Row, string Key, float Value, bool IsRow)> _measured = new List<(Height, string, float, bool)>();
		private float _typical, _top, _view, _y, _fallback;
		private string _typicalKey, _baseKey;
		private Height _current;
		// 不用 GUIStyle.none：那样里面第一个、最后一个控件的外边距会算到组外面，留空时高度对不上
		internal static readonly GUIStyle Wrapper = new GUIStyle();

		/// <summary>开始画一轮。baseKey 是会改变行高的整体条件（窗口宽度、字号、语言），变了全部重量。</summary>
		internal void Begin(string baseKey, float top, float view, int count, float fallback)
		{
			if (_heights.Count > count + 500)
			{
				_heights.Clear();
			}
			if (_typicalKey != baseKey)
			{
				_typicalKey = baseKey;
				_typical = 0f;
			}
			_baseKey = baseKey;
			_top = top;
			_view = view;
			_y = 0f;
			_fallback = fallback;
			_measured.Clear();
		}

		private float Estimate => _typical > 0f ? _typical : _fallback;

		/// <summary>
		/// 下一行（id 唯一，state 是这一行自己会改变高度的内容）：离得远就留空、返回 false；要画就返回 true，
		/// 调用方画完后调 Measure。isRow 为 false 的（组标题）不拿来估计其他行的高度。
		/// </summary>
		internal bool Row(string id, string state, float estimate = -1f)
		{
			string key = _baseKey + "|" + state;
			if (!_heights.TryGetValue(id, out Height row))
			{
				_heights[id] = row = new Height();
			}
			if (row.Key != key)
			{
				row.Key = key;
				row.Value = -1f;
			}
			float height = row.Value > 0f ? row.Value : estimate > 0f ? estimate : Estimate;
			if (_y + height < _top - _view || _y > _top + _view * 2f)
			{
				GUILayout.Space(height);
				_y += height;
				return false;
			}
			_current = row;
			GUILayout.BeginVertical(Wrapper);
			return true;
		}

		/// <summary>画完这一行：量高度（整轮画完再记，免得同一次刷新里前后两遍排得不一样）。</summary>
		internal void Measure(float estimate = -1f, bool isRow = true)
		{
			GUILayout.EndVertical();
			Height row = _current;
			if (Event.current.type == EventType.Repaint)
			{
				_measured.Add((row, row.Key, GUILayoutUtility.GetLastRect().height, isRow));
			}
			_y += row.Value > 0f ? row.Value : estimate > 0f ? estimate : Estimate;
		}

		internal void End()
		{
			foreach (var (row, key, value, isRow) in _measured)
			{
				if (row.Key == key && value > 0f)
				{
					row.Value = value;
					if (isRow && _typical <= 0f)
					{
						_typical = value;
					}
				}
			}
			_measured.Clear();
		}
	}
}
