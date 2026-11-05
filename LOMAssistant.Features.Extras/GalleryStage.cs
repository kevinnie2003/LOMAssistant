using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 鉴赏的「立绘」页：选背景（场景图或已解锁的 CG），摆上任意角色的立绘，换差分、拖动、缩放、翻转、调整前后，
/// 再隐藏界面就能截图。角色和差分从资源目录列出来，游戏里有什么就有什么。摆放不保存。
/// </summary>
public sealed partial class Gallery
{
	private const string PortraitRoot = "Assets/__Project/Images/Characters/";
	private const string BackgroundRoot = "Assets/__Project/Images/Background/";

	private const float MinSize = 0.2f, MaxSize = 3f;

	private const int PickColumns = 5, PickRows = 4;

	private sealed class Placed
	{
		public string Folder, Mood;
		public float X = 0.5f, Y = 1f, Size = 1f; // X、Y 是脚底中点在屏幕上的比例，Size 是身高占屏幕高的比例
		public bool Flip;
	}

	private sealed class Figure
	{
		public string Folder, Name;
		public List<string> Moods = new List<string>();
	}

	// 文件夹名带中文的直接用，这几个是纯英文
	private static readonly Dictionary<string, string> FigureNames = new Dictionary<string, string>
	{
		{ "Sister", "小师妹" }, { "Brother1", "大师兄" }, { "Brother2", "二师兄" }, { "Brother3", "三师兄" }, { "Brother4", "四师兄" }, { "Master", "掌门人" },
	};

	private static readonly Regex FolderPrefix = new Regex(@"^[A-Za-z]+\d*(?:_\d+)?_(?=[^\x00-\x7F])");

	private List<Figure> _figures;
	private List<string> _sceneBacks;
	private readonly List<Placed> _placed = new List<Placed>();
	private readonly Dictionary<string, AsyncOperationHandle<Sprite>> _stageLoads = new Dictionary<string, AsyncOperationHandle<Sprite>>();
	private Placed _selected;
	private Figure _figure;
	private bool _hideUi, _dragging, _backIsCg, _pickingBack;
	private int _backIndex, _pickPage;
	private Vector2 _grab, _figureScroll, _moodScroll;
	private string _figureSearch = "";

	private void OpenStage()
	{
		_tab = StageTab;
		if (_figures != null)
		{
			return;
		}
		var figures = new Dictionary<string, Figure>();
		foreach (var locator in Addressables.ResourceLocators)
		{
			foreach (object key in locator.Keys)
			{
				if (!(key is string path) || !path.EndsWith(".png", StringComparison.Ordinal))
				{
					continue;
				}
				if (path.StartsWith(PortraitRoot, StringComparison.Ordinal))
				{
					string[] parts = path.Substring(PortraitRoot.Length).Split('/');
					if (parts.Length == 2)
					{
						if (!figures.TryGetValue(parts[0], out Figure figure))
						{
							figure = new Figure { Folder = parts[0], Name = FigureName(parts[0]) };
							figures[parts[0]] = figure;
						}
						string mood = parts[1].Substring(0, parts[1].Length - 4);
						if (!figure.Moods.Contains(mood))
						{
							figure.Moods.Add(mood);
						}
					}
				}
			}
		}
		foreach (Figure figure in figures.Values)
		{
			figure.Moods.Sort(StringComparer.Ordinal);
		}
		_figures = figures.Values.OrderBy(f => FigureRank(f.Folder)).ThenBy(f => f.Folder, StringComparer.Ordinal).ToList();
		_sceneBacks = ScenePaths();
		FileLogger.Log($"[鉴赏] 立绘 {_figures.Count} 个角色，背景 {_sceneBacks.Count} 张");
	}

	private static List<string> ScenePaths()
	{
		var paths = new SortedSet<string>(StringComparer.Ordinal);
		foreach (var locator in Addressables.ResourceLocators)
		{
			foreach (object key in locator.Keys)
			{
				if (key is string path && path.StartsWith(BackgroundRoot, StringComparison.Ordinal) && path.EndsWith(".png", StringComparison.Ordinal))
				{
					paths.Add(path);
				}
			}
		}
		return paths.ToList();
	}

	private static string FigureName(string folder)
	{
		if (FigureNames.TryGetValue(folder, out string name))
		{
			return name;
		}
		return FolderPrefix.Replace(folder, "").Replace('_', '·');
	}

	private static int FigureRank(string folder) => folder.StartsWith("Sister", StringComparison.Ordinal) || folder.StartsWith("Girl", StringComparison.Ordinal) ? 0
		: folder.StartsWith("Player", StringComparison.Ordinal) ? 1
		: folder.StartsWith("Brother", StringComparison.Ordinal) || folder == "Master" ? 2
		: folder.StartsWith("Special", StringComparison.Ordinal) ? 3 : 4;

	private Sprite StageSprite(string path)
	{
		if (!_stageLoads.TryGetValue(path, out AsyncOperationHandle<Sprite> handle))
		{
			handle = Addressables.LoadAssetAsync<Sprite>(path);
			_stageLoads[path] = handle;
		}
		return handle.IsDone && handle.Status == AsyncOperationStatus.Succeeded ? handle.Result : null;
	}

	private static string PathOf(Placed item) => PortraitRoot + item.Folder + "/" + item.Mood + ".png";

	private List<Picture> CgBacks => _pictures.Where(p => p.Unlocked && p.Path == null && p.Direct == null).ToList();

	private string BackPath()
	{
		if (_backIsCg)
		{
			List<Picture> list = CgBacks;
			return list.Count > 0 ? "pic_" + list[Mathf.Clamp(_backIndex, 0, list.Count - 1)].Key : null;
		}
		return _sceneBacks.Count > 0 ? _sceneBacks[Mathf.Clamp(_backIndex, 0, _sceneBacks.Count - 1)] : null;
	}

	private List<(string Path, Color? Back)> BackItems => _backIsCg
		? CgBacks.Select(p => ("pic_" + p.Key, p.Back)).ToList()
		: _sceneBacks.Select(path => (path, (Color?)null)).ToList();

	/// <summary>放掉舞台上已经不用的图。</summary>
	private void TrimStage()
	{
		var keep = new HashSet<string>(_placed.Select(PathOf));
		string back = BackPath();
		if (back != null)
		{
			keep.Add(back);
		}
		if (_pickingBack)
		{
			keep.UnionWith(BackItems.Skip(_pickPage * PickColumns * PickRows).Take(PickColumns * PickRows).Select(item => item.Path));
		}
		foreach (string path in _stageLoads.Keys.Where(k => !keep.Contains(k)).ToList())
		{
			Addressables.Release(_stageLoads[path]);
			_stageLoads.Remove(path);
		}
	}

	private void CloseStage()
	{
		_placed.Clear();
		_selected = null;
		_hideUi = _dragging = _pickingBack = false;
		foreach (AsyncOperationHandle<Sprite> handle in _stageLoads.Values)
		{
			Addressables.Release(handle);
		}
		_stageLoads.Clear();
	}

	/// <summary>Esc、右键：界面藏着时先恢复界面。</summary>
	private bool StageBack()
	{
		if (_tab == StageTab && _pickingBack)
		{
			_pickingBack = false;
			TrimStage();
			return true;
		}
		if (_tab == StageTab && _hideUi)
		{
			_hideUi = false;
			return true;
		}
		return false;
	}

	private static Rect RectOf(Placed item, Sprite sprite)
	{
		float height = Screen.height * item.Size;
		float width = height * sprite.textureRect.width / sprite.textureRect.height;
		return new Rect(item.X * Screen.width - width / 2f, item.Y * Screen.height - height, width, height);
	}

	private void OpenBackPicker()
	{
		_pickingBack = true;
		_pickPage = _backIndex / (PickColumns * PickRows);
		TrimStage();
	}

	/// <summary>选背景：缩略图一页页翻，只读当前这一页。</summary>
	private void DrawBackPicker(Rect screen)
	{
		Fill(screen, PanelTheme.Paper);
		GUI.Label(new Rect(S(40f), S(26f), S(300f), S(52f)), TextConverter.ForDisplay("选择背景"), _title);
		if (GUI.Button(new Rect(S(320f), S(30f), S(120f), S(46f)), TextConverter.ForDisplay("场景"), !_backIsCg ? _primary : _button))
		{
			_pickPage = 0;
			SetBack(false, 0);
		}
		if (GUI.Button(new Rect(S(450f), S(30f), S(120f), S(46f)), "CG", _backIsCg ? _primary : _button))
		{
			_pickPage = 0;
			SetBack(true, 0);
		}
		if (GUI.Button(new Rect(Screen.width - S(160f), S(30f), S(120f), S(46f)), TextConverter.ForDisplay("返回"), _button))
		{
			StageBack();
			return;
		}
		List<(string Path, Color? Back)> items = BackItems;
		int pages = Mathf.Max(1, Mathf.CeilToInt(items.Count / (float)(PickColumns * PickRows)));
		float gap = S(12f), top = S(96f), footer = S(70f);
		float width = (Screen.width - S(80f) - gap * (PickColumns - 1)) / PickColumns;
		float height = (Screen.height - top - footer - gap * (PickRows - 1)) / PickRows;
		for (int i = 0; i < PickColumns * PickRows; i++)
		{
			int index = _pickPage * PickColumns * PickRows + i;
			if (index >= items.Count)
			{
				break;
			}
			var card = new Rect(S(40f) + i % PickColumns * (width + gap), top + i / PickColumns * (height + gap), width, height);
			if (GUI.Button(card, GUIContent.none, index == _backIndex ? _trackOn : _card))
			{
				_pickingBack = false;
				SetBack(_backIsCg, index);
				return;
			}
			Sprite sprite = StageSprite(items[index].Path);
			if (sprite != null)
			{
				DrawFit(new Rect(card.x + S(4f), card.y + S(4f), card.width - S(8f), card.height - S(8f)), sprite, items[index].Back);
			}
		}
		float y = Screen.height - S(56f), middle = Screen.width / 2f;
		GUI.enabled = _pickPage > 0;
		if (GUI.Button(new Rect(middle - S(240f), y, S(140f), S(46f)), TextConverter.ForDisplay("上一页"), _button))
		{
			_pickPage--;
			TrimStage();
		}
		GUI.enabled = _pickPage < pages - 1;
		if (GUI.Button(new Rect(middle + S(100f), y, S(140f), S(46f)), TextConverter.ForDisplay("下一页"), _button))
		{
			_pickPage++;
			TrimStage();
		}
		GUI.enabled = true;
		GUI.Label(new Rect(middle - S(90f), y, S(180f), S(46f)), (_pickPage + 1) + " / " + pages, _center);
	}

	private void DrawStage(Rect screen)
	{
		if (_pickingBack)
		{
			DrawBackPicker(screen);
			return;
		}
		Fill(screen, Color.black);
		string back = BackPath();
		Sprite backSprite = back != null ? StageSprite(back) : null;
		if (backSprite != null)
		{
			Color? color = _backIsCg && CgBacks.Count > 0 ? CgBacks[Mathf.Clamp(_backIndex, 0, CgBacks.Count - 1)].Back : null;
			if (color != null)
			{
				Fill(screen, color.Value);
			}
			DrawFit(screen, backSprite, null);
		}
		foreach (Placed item in _placed)
		{
			Sprite sprite = StageSprite(PathOf(item));
			if (sprite == null)
			{
				continue;
			}
			Rect rect = RectOf(item, sprite);
			Rect part = sprite.textureRect;
			Texture2D texture = sprite.texture;
			var uv = new Rect(part.x / texture.width, part.y / texture.height, part.width / texture.width, part.height / texture.height);
			if (item.Flip)
			{
				uv = new Rect(uv.xMax, uv.y, -uv.width, uv.height);
			}
			GUI.DrawTextureWithTexCoords(rect, texture, uv);
			if (item == _selected && !_hideUi)
			{
				Outline(rect, PanelTheme.Cinnabar);
			}
		}
		if (_hideUi)
		{
			if (Time.unscaledTime < _hintUntil)
			{
				var hint = new Rect(0f, Screen.height - S(48f), Screen.width, S(40f));
				Fill(hint, new Color(0f, 0f, 0f, 0.6f));
				GUI.Label(hint, TextConverter.ForDisplay("右键或 Esc 恢复界面"), _hint);
			}
			HandleStageMouse(new Rect(0f, 0f, 0f, 0f));
			return;
		}
		Color paper = PanelTheme.Paper;
		Fill(new Rect(0f, 0f, Screen.width, S(92f)), new Color(paper.r, paper.g, paper.b, 0.9f));
		var panel = new Rect(Screen.width - S(440f), S(92f), S(440f), Screen.height - S(92f));
		Fill(panel, new Color(paper.r, paper.g, paper.b, 0.9f));
		DrawHeader();
		HandleStageMouse(panel);
		DrawStagePanel(panel);
	}

	private static void Outline(Rect rect, Color color)
	{
		Fill(new Rect(rect.x, rect.y, rect.width, 2f), color);
		Fill(new Rect(rect.x, rect.yMax - 2f, rect.width, 2f), color);
		Fill(new Rect(rect.x, rect.y, 2f, rect.height), color);
		Fill(new Rect(rect.xMax - 2f, rect.y, 2f, rect.height), color);
	}

	private void HandleStageMouse(Rect panel)
	{
		Event e = Event.current;
		bool free = !panel.Contains(e.mousePosition) && (_hideUi || e.mousePosition.y > S(92f));
		if (e.type == EventType.MouseDown && e.button == 0 && free)
		{
			_selected = _placed.AsEnumerable().Reverse().FirstOrDefault(item =>
			{
				Sprite sprite = StageSprite(PathOf(item));
				return sprite != null && RectOf(item, sprite).Contains(e.mousePosition);
			});
			if (_selected != null)
			{
				_dragging = true;
				_grab = e.mousePosition - new Vector2(_selected.X * Screen.width, _selected.Y * Screen.height);
				_figure = _figures.FirstOrDefault(f => f.Folder == _selected.Folder) ?? _figure;
				e.Use();
			}
		}
		else if (e.type == EventType.MouseDrag && _dragging && _selected != null)
		{
			_selected.X = (e.mousePosition.x - _grab.x) / Screen.width;
			_selected.Y = (e.mousePosition.y - _grab.y) / Screen.height;
			e.Use();
		}
		else if (e.type == EventType.MouseUp)
		{
			_dragging = false;
		}
		else if (e.type == EventType.ScrollWheel && free && _selected != null)
		{
			Resize(e.delta.y > 0f ? 0.95f : 1.05f);
			e.Use();
		}
	}

	private void Resize(float by) => _selected.Size = Mathf.Clamp(_selected.Size * by, MinSize, MaxSize);

	private void DrawStagePanel(Rect panel)
	{
		float x = panel.x + S(16f), width = panel.width - S(32f), half = (width - S(8f)) / 2f, third = (width - S(16f)) / 3f;
		float y = panel.y + S(10f);
		GUI.Label(new Rect(x, y, width, S(28f)), TextConverter.ForDisplay("背景"), _muted);
		y += S(30f);
		if (GUI.Button(new Rect(x, y, half, S(38f)), TextConverter.ForDisplay("场景"), !_backIsCg ? _trackOn : _track))
		{
			SetBack(false, 0);
		}
		if (GUI.Button(new Rect(x + half + S(8f), y, half, S(38f)), "CG", _backIsCg ? _trackOn : _track))
		{
			SetBack(true, 0);
		}
		y += S(44f);
		int count = _backIsCg ? CgBacks.Count : _sceneBacks.Count;
		if (GUI.Button(new Rect(x, y, third, S(38f)), TextConverter.ForDisplay("上一页"), _track) && count > 0)
		{
			SetBack(_backIsCg, (_backIndex - 1 + count) % count);
		}
		if (GUI.Button(new Rect(x + third + S(8f), y, third, S(38f)), TextConverter.ForDisplay("选择背景"), _track))
		{
			OpenBackPicker();
		}
		if (GUI.Button(new Rect(x + (third + S(8f)) * 2f, y, third, S(38f)), TextConverter.ForDisplay("下一页"), _track) && count > 0)
		{
			SetBack(_backIsCg, (_backIndex + 1) % count);
		}
		y += S(50f);

		GUI.Label(new Rect(x, y, width, S(28f)), TextConverter.ForDisplay("角色（点一下放到画面上）"), _muted);
		y += S(30f);
		_figureSearch = GUI.TextField(new Rect(x, y, width, S(36f)), _figureSearch, _field);
		y += S(42f);
		string query = TextConverter.ToSimplified(_figureSearch.Trim());
		List<Figure> figures = query.Length == 0 ? _figures : _figures.Where(f => TextConverter.ToSimplified(f.Name).Contains(query) || f.Folder.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
		float listHeight = (panel.yMax - y - S(170f)) / 2f;
		var figureBox = new Rect(x, y, width, listHeight);
		if (figureBox.Contains(Event.current.mousePosition))
		{
			_figureScroll.y -= GamepadCursor.ScrollDelta().y;
		}
		_figureScroll = GUI.BeginScrollView(figureBox, _figureScroll, new Rect(0f, 0f, width - S(20f), figures.Count * S(36f)));
		for (int i = 0; i < figures.Count; i++)
		{
			var row = new Rect(0f, i * S(36f), width - S(20f), S(34f));
			if (row.yMax >= _figureScroll.y && row.y <= _figureScroll.y + listHeight
				&& GUI.Button(row, TextConverter.ForDisplay(TextConverter.ToSimplifiedUnlessJapanese(figures[i].Name)), figures[i] == _figure ? _trackOn : _track))
			{
				Place(figures[i]);
			}
		}
		GUI.EndScrollView();
		y += listHeight + S(8f);

		GUI.Label(new Rect(x, y, width, S(28f)), TextConverter.ForDisplay("差分"), _muted);
		y += S(30f);
		var moodBox = new Rect(x, y, width, listHeight - S(38f));
		if (moodBox.Contains(Event.current.mousePosition))
		{
			_moodScroll.y -= GamepadCursor.ScrollDelta().y;
		}
		List<string> moods = _selected != null && _figure != null && _figure.Folder == _selected.Folder ? _figure.Moods : new List<string>();
		_moodScroll = GUI.BeginScrollView(moodBox, _moodScroll, new Rect(0f, 0f, width - S(20f), moods.Count * S(36f)));
		for (int i = 0; i < moods.Count; i++)
		{
			var row = new Rect(0f, i * S(36f), width - S(20f), S(34f));
			if (row.yMax >= _moodScroll.y && row.y <= _moodScroll.y + moodBox.height && GUI.Button(row, moods[i], moods[i] == _selected.Mood ? _trackOn : _track))
			{
				_selected.Mood = moods[i];
				TrimStage();
			}
		}
		GUI.EndScrollView();

		float bottom = panel.yMax - S(144f);
		GUI.enabled = _selected != null;
		if (GUI.Button(new Rect(x, bottom, third, S(40f)), TextConverter.ForDisplay("放大"), _track))
		{
			Resize(1.1f);
		}
		if (GUI.Button(new Rect(x + third + S(8f), bottom, third, S(40f)), TextConverter.ForDisplay("缩小"), _track))
		{
			Resize(0.9f);
		}
		if (GUI.Button(new Rect(x + (third + S(8f)) * 2f, bottom, third, S(40f)), TextConverter.ForDisplay("翻转"), _track))
		{
			_selected.Flip = !_selected.Flip;
		}
		bottom += S(46f);
		if (GUI.Button(new Rect(x, bottom, third, S(40f)), TextConverter.ForDisplay("移到前面"), _track))
		{
			Layer(1);
		}
		if (GUI.Button(new Rect(x + third + S(8f), bottom, third, S(40f)), TextConverter.ForDisplay("移到后面"), _track))
		{
			Layer(-1);
		}
		if (GUI.Button(new Rect(x + (third + S(8f)) * 2f, bottom, third, S(40f)), TextConverter.ForDisplay("删除"), _track))
		{
			_placed.Remove(_selected);
			_selected = null;
			TrimStage();
		}
		GUI.enabled = true;
		bottom += S(46f);
		if (GUI.Button(new Rect(x, bottom, width, S(40f)), TextConverter.ForDisplay("隐藏界面"), _track))
		{
			_hideUi = true;
			_hintUntil = Time.unscaledTime + HintSeconds;
		}
	}

	private void SetBack(bool cg, int index)
	{
		_backIsCg = cg;
		_backIndex = index;
		TrimStage();
	}

	private void Layer(int by)
	{
		int at = _placed.IndexOf(_selected);
		int to = Mathf.Clamp(at + by, 0, _placed.Count - 1);
		_placed.RemoveAt(at);
		_placed.Insert(to, _selected);
	}

	private void Place(Figure figure)
	{
		_figure = figure;
		string mood = figure.Moods.FirstOrDefault(m => m == "normal" || m.EndsWith("_normal", StringComparison.Ordinal)) ?? figure.Moods[0];
		_selected = new Placed { Folder = figure.Folder, Mood = mood, X = 0.35f + _placed.Count % 5 * 0.07f };
		_placed.Add(_selected);
		_moodScroll = Vector2.zero;
	}
}
