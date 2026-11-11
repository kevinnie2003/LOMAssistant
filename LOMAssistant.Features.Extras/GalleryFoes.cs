using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LOMAssistant.Config;
using Mortal.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 鉴赏的「决斗」页：把决斗场景里每个角色的战斗立绘列出来（正常、攻击、受击、防御等变体）。
/// 立绘来自 combat / combat_02 这两个资源包，按容器里的路径分组；名字取自游戏的 CombatCharacter 名表。
/// </summary>
public sealed partial class Gallery
{
	private const float FoeListWidth = 250f;
	private const float FoePoseWidth = 230f;
	private const float FoeRowHeight = 58f;

	/// <summary>
	/// 立绘变体归成四类显示。同一个含义在各角色上拼写不统一（idle / stand / standby / stand_by / normal，
	/// defense / defence / block，attack / attack1 / attack2 / attack_01），
	/// 还有两个角色把角色名也拼进了变体名（combat_girl4_attack_01），所以按关键词包含来判断，不做精确匹配。
	/// 顺序就是显示顺序。
	/// </summary>
	private static readonly (string Group, string[] Keys)[] FoePoseGroups =
	{
		("正常", new[] { "idle", "stand", "normal" }),
		("攻击", new[] { "attack" }),
		("受击", new[] { "hurt" }),
		("防御", new[] { "block", "defen" }),
	};

	private sealed class FoePose
	{
		public string Variant;
		public string Group;
		public string Path;
		public Sprite Sprite;
	}

	private sealed class Foe
	{
		public string Folder;
		public string Id;        // 游戏里的角色编号，例如 special4、girl4、trainee1
		public string Name;      // 资源包目录里的中文名（兜底用）
		public string SortKey;
		public readonly List<FoePose> Poses = new List<FoePose>();
	}

	private static List<Foe> _foes;
	private Foe _foe;
	private FoePose _foePose;
	private string _foeSearch = "";
	private Vector2 _foeListScroll, _foePoseScroll;
	private float _foeZoom;
	private Vector2 _foePan;
	private bool _foeDragging;
	private Vector2 _foeGrab;
	private readonly List<AssetBundle> _foeBundles = new List<AssetBundle>();
	private Coroutine _foeLoadRoutine;
	private bool _foeLoading;
	private GUIStyle _foeRow, _foeRowOn, _foeSmall, _foeName, _foeNameOn, _foeHint;
	private float _foeStyleScale = -1f;
	private bool _foeStyleDark;
	private float _foeStatsShare = 0.61f;
	private int _foeResizeControl;
	private float _foeResizeGrab;

	// ---------------- 扫描 ----------------

	private void OpenFoes()
	{
		_tab = FoesTab;
		EnsureFoeBundles();
	}

	/// <summary>
	/// 需要就把立绘包读进来。幂等，可以每帧调。
	/// 重点是 <see cref="DrawFoes"/> 里也要调一次：关掉鉴赏再打开时 Gallery 不会重置页签，
	/// OnGUI 直接画到这一页，走不到 OpenFoes，只靠页签点击触发就会一片空白。
	/// </summary>
	private void EnsureFoeBundles()
	{
		if (_foes != null || _foeLoadRoutine != null)
		{
			return;
		}
		// 先置位再起协程：OnGUI 一帧里可能进来多次，协程要下一帧才执行，
		// 不先占住会重复起好几个。
		_foeLoading = true;
		_foeLoadRoutine = StartCoroutine(LoadFoeBundles());
	}

	/// <summary>把 combat / combat_02 两个包异步读进来，再从容器里列出角色和他们各自的变体。</summary>
	private System.Collections.IEnumerator LoadFoeBundles()
	{
		ReleaseFoePoses();
		_foe = null;
		_foePose = null;
		_foes = null;

		string root = BundleRoot();
		if (root == null)
		{
			_foeLoading = false;
			_foeLoadRoutine = null;
			FrameNotice(TextConverter.ForDisplay("找不到游戏资源目录，无法读取决斗立绘"), 8f);
			yield break;
		}

		// 立绘路径只登记在容器的名字表里，而且被压缩过，只能把包装进来问 Unity 要
		foreach (string path in System.IO.Directory.GetFiles(root, "combat*.bundle"))
		{
			AssetBundleCreateRequest request = AssetBundle.LoadFromFileAsync(path);
			yield return request;
			AssetBundle bundle = request.assetBundle;
			if (bundle == null)
			{
				FileLogger.LogWarning("[鉴赏] 决斗：加载失败 " + System.IO.Path.GetFileName(path));
				continue;
			}
			_foeBundles.Add(bundle);
		}

		try
		{
			BuildFoeList();
		}
		catch (Exception e)
		{
			// 建列表出错时留一个空清单：既不每帧重试，也能靠「关掉再打开」重来
			FileLogger.LogWarning("[鉴赏] 决斗：建列表失败: " + e);
			_foes ??= new List<Foe>();
		}
		_foeLoading = false;
		_foeLoadRoutine = null;
	}

	/// <summary>从已经加载的包里读容器名字表，按「角色目录 / 变体」分组。</summary>
	private void BuildFoeList()
	{
		var byFolder = new Dictionary<string, Foe>(StringComparer.Ordinal);
		foreach (AssetBundle bundle in _foeBundles)
		{
			if (bundle == null)
			{
				continue;
			}
			string[] names;
			try
			{
				names = bundle.GetAllAssetNames();
			}
			catch (Exception e)
			{
				FileLogger.LogWarning("[鉴赏] 决斗：读资产名失败: " + e.Message);
				continue;
			}
			foreach (string name in names)
			{
				if (!name.StartsWith("Assets/__Project/Images/Combat/", StringComparison.Ordinal)
					|| !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				string[] parts = name.Substring("Assets/__Project/Images/Combat/".Length).Split('/');
				if (parts.Length != 2)
				{
					continue;
				}
				if (!byFolder.TryGetValue(parts[0], out Foe foe))
				{
					GalleryFoeNames.Table.TryGetValue(parts[0], out var row);
					byFolder[parts[0]] = foe = new Foe
					{
						Folder = parts[0],
						Id = row.Id ?? "",
						Name = string.IsNullOrEmpty(row.Name) ? parts[0] : row.Name,
						SortKey = row.Id ?? parts[0],
					};
				}
				string variant = parts[1].Substring(0, parts[1].Length - 4);
				if (foe.Poses.Any(p => p.Variant == variant))
				{
					continue;
				}
				foe.Poses.Add(new FoePose { Variant = variant, Group = FoePoseGroup(variant), Path = name });
			}
		}

		// 有配置而没有独立立绘的角色也可查面板，不依赖人名表是否已经收录。
		try
		{
			foreach (DuelProfile profile in GalleryDuelCatalog.Instance.Stats)
				foreach (string folder in profile.Folders)
					if (!byFolder.ContainsKey(folder))
						byFolder[folder] = new Foe { Folder = folder, Id = profile.CharacterId, Name = profile.Name, SortKey = profile.CharacterId };
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[鉴赏] 补充决斗角色失败: " + e.Message);
		}
		foreach (Foe foe in byFolder.Values)
		{
			foe.Poses.Sort((a, b) =>
			{
				int ra = Array.FindIndex(FoePoseGroups, g => g.Group == a.Group);
				int rb = Array.FindIndex(FoePoseGroups, g => g.Group == b.Group);
				return ra != rb ? ra.CompareTo(rb) : string.CompareOrdinal(a.Variant, b.Variant);
			});
		}
		_foes = byFolder.Values
			.OrderBy(f => f.SortKey, StringComparer.Ordinal)
			.ThenBy(f => f.Folder, StringComparer.Ordinal)
			.ToList();
		FileLogger.Log($"[鉴赏] 决斗：{_foes.Count} 个决斗角色");
		// 和动图页一样，进页面就选中第一个
		if (_foe == null && _foes.Count > 0)
		{
			SelectFoe(_foes[0]);
		}
	}


	/// <summary>变体名归到四类里的一类（同一个含义在不同角色上拼写不一）。</summary>
	private static string FoePoseGroup(string variant)
	{
		string name = (variant ?? "").ToLowerInvariant();
		foreach ((string group, string[] keys) in FoePoseGroups)
		{
			foreach (string key in keys)
			{
				if (name.Contains(key))
				{
					return group;
				}
			}
		}
		return "其他";
	}

	// ---------------- 名字 ----------------

	/// <summary>角色名按界面语言取：和其他页面一样查人名表，没有译名的显示原名。</summary>
	private static string FoeDisplay(Foe foe)
	{
		return foe == null ? "" : TextConverter.ForDisplay(foe.Name);
	}

	// ---------------- 加载立绘 ----------------

	private void SelectFoe(Foe foe)
	{
		if (foe == null || foe == _foe)
		{
			return;
		}
		_foe = foe;
		_foePose = null;
		SelectFoeStats();
		// 立绘包是全部角色共用的，切角色不能卸载，只在关掉鉴赏时统一释放
		if (foe.Poses.Count > 0)
		{
			SelectFoePose(foe.Poses[0]);
		}
	}

	private void SelectFoePose(FoePose pose)
	{
		if (pose == null)
		{
			return;
		}
		_foePose = pose;
		_foeZoom = 0f;
		_foePan = Vector2.zero;
		if (pose.Sprite == null)
		{
			pose.Sprite = LoadFoeSprite(pose.Path);
		}
	}

	private Sprite LoadFoeSprite(string assetPath)
	{
		if (!FoeBundlesAlive())
		{
			// 这里不能顺手重建：Draw 每帧都会调进来，重建会连带卸载正在用的包，变成每帧刷一遍。
			// 交给下一次打开鉴赏时的 OpenFoes 处理。
			return null;
		}
		foreach (AssetBundle bundle in _foeBundles)
		{
			if (bundle == null)
			{
				continue;
			}
			try
			{
				Sprite sprite = bundle.LoadAsset<Sprite>(assetPath);
				if (sprite != null)
				{
					return sprite;
				}
			}
			catch (Exception)
			{
				// 这个包没有这个路径，换下一个
			}
		}
		return null;
	}

	private void ReleaseFoePoses()
	{
		foreach (AssetBundle bundle in _foeBundles)
		{
			try
			{
				bundle?.Unload(true);
			}
			catch (Exception e)
			{
				FileLogger.LogWarning("[鉴赏] 卸载决斗立绘包失败: " + e.Message);
			}
		}
		_foeBundles.Clear();
		if (_foes != null)
		{
			foreach (Foe foe in _foes)
			{
				foreach (FoePose pose in foe.Poses)
				{
					pose.Sprite = null;
				}
			}
		}
	}

	/// <summary>关掉鉴赏就卸载，和动图页一样。下次进来再异步加载一遍。</summary>
	private void CloseFoes()
	{
		EndFoeResize();
		if (_foeLoadRoutine != null)
		{
			StopCoroutine(_foeLoadRoutine);
			_foeLoadRoutine = null;
		}
		_foeLoading = false;
		ReleaseFoePoses();
		_foes = null;
		_foe = null;
		_foePose = null;
		_foeEncounters.Clear();
		_foeEncounter = null;
		_foeRadar?.Dispose();
		_foeRadar = null;
		if (_foeStatsSkin != null) Destroy(_foeStatsSkin);
		_foeStatsSkin = null;
	}

	/// <summary>立绘包还在不在（Unity 对象可能被回收，引用非空但 == null）。</summary>
	private bool FoeBundlesAlive()
	{
		if (_foeBundles.Count == 0)
		{
			return false;
		}
		foreach (AssetBundle bundle in _foeBundles)
		{
			if (bundle == null)
			{
				return false;
			}
		}
		return true;
	}

	// ---------------- 绘制 ----------------

	private void FoeStyles()
	{
		if (_foeRow != null && Mathf.Approximately(_foeStyleScale, _scale) && _foeStyleDark == PanelTheme.Dark)
		{
			return;
		}
		_foeStyleScale = _scale;
		_foeStyleDark = PanelTheme.Dark;
		_foeRow = new GUIStyle(_track) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
		_foeRowOn = new GUIStyle(_trackOn) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
		_foeSmall = PanelTheme.Label(_scale, 16, PanelTheme.InkMuted, wrap: false);
		// 行里的名字：不带底框，颜色和按钮字一样（选中的那行跟着选中按钮的字色）
		_foeName = new GUIStyle(_foeSmall) { fontSize = _foeRow.fontSize, clipping = TextClipping.Clip };
		_foeName.normal.textColor = _track.normal.textColor;
		_foeNameOn = new GUIStyle(_foeName);
		_foeNameOn.normal.textColor = _trackOn.normal.textColor;
		_foeHint = new GUIStyle(_foeSmall) { alignment = TextAnchor.MiddleLeft };
	}

	private void DrawFoes(Rect screen)
	{
		EnsureFoeBundles();
		FoeStyles();
		DrawHeader();
		float top = S(110f);
		float left = S(40f);
		float width = screen.width - S(80f);
		// 导出位置和动图页共用（默认桌面），可以在这里改，目录不存在会自动建
		if (_frameExportDirEdit == null)
		{
			_frameExportDirEdit = ConfigManager.GalleryFrameExportDir.Value ?? "";
		}
		GUI.Label(new Rect(left, top, S(80f), S(44f)), TextConverter.ForDisplay("导出到"), _muted);
		var dirField = new Rect(left + S(84f), top, width - S(280f), S(44f));
		_frameExportDirEdit = GUI.TextField(dirField, _frameExportDirEdit ?? "", _field);
		if (GUI.Button(new Rect(dirField.xMax + S(10f), top, S(150f), S(44f)), TextConverter.ForDisplay("打开文件夹"), _button))
		{
			OpenExportFolder();
		}

		var body = new Rect(left, top + S(58f), width, screen.height - top - S(58f) - S(40f));

		var list = new Rect(body.x, body.y, Mathf.Min(S(FoeListWidth), body.width * 0.14f), body.height);
		var poses = new Rect(list.xMax + S(12f), body.y, Mathf.Min(S(FoePoseWidth), body.width * 0.115f), body.height);
		var right = new Rect(poses.xMax + S(12f), body.y, body.width - list.width - poses.width - S(24f), body.height);
		DrawFoeList(list);
		DrawFoePoses(poses);
		DrawFoeDetail(right);

		if (Time.unscaledTime < _frameNoticeUntil)
		{
			var notice = new Rect(0f, screen.height - S(48f), screen.width, S(40f));
			Fill(notice, new Color(0f, 0f, 0f, 0.62f));
			GUI.Label(notice, _frameNotice, _hint);
		}
	}

	private void DrawFoeList(Rect area)
	{
		var search = new Rect(area.x, area.y, area.width, S(40f));
		GUI.SetNextControlName("LOMAssistant_FoeSearch");
		_foeSearch = GUI.TextField(search, _foeSearch ?? "", _field);
		// 空着、也没在输入时，框里写个淡色的「搜索」提示（竖直居中，点进去就消失）
		if (string.IsNullOrEmpty(_foeSearch) && GUI.GetNameOfFocusedControl() != "LOMAssistant_FoeSearch")
		{
			GUI.Label(new Rect(search.x + S(10f), search.y, search.width - S(20f), search.height), TextConverter.ForDisplay("搜索"), _foeHint);
		}
		List<Foe> shown = _foes ?? new List<Foe>();
		string query = TextConverter.ToSimplified((_foeSearch ?? "").Trim());
		if (query.Length > 0)
		{
			shown = shown.Where(f => TextConverter.ToSimplified(FoeDisplay(f) + " " + f.Name + " " + f.Folder).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
		}
		GUI.Label(new Rect(area.x, search.yMax + S(4f), area.width, S(26f)),
			_foeLoading ? TextConverter.ForDisplay("正在加载…")
				: string.Format(TextConverter.ForDisplay("共 {0} 个"), shown.Count), _foeSmall);

		float listTop = search.yMax + S(34f);
		var box = new Rect(area.x, listTop, area.width, Mathf.Max(S(80f), area.yMax - listTop));
		float content = Mathf.Max(box.height, shown.Count * (S(FoeRowHeight) + S(4f)));
		_foeListScroll = GUI.BeginScrollView(box, _foeListScroll, new Rect(0f, 0f, area.width - S(18f), content));
		for (int i = 0; i < shown.Count; i++)
		{
			Foe foe = shown[i];
			var row = new Rect(0f, i * (S(FoeRowHeight) + S(4f)), area.width - S(20f), S(FoeRowHeight));
			bool on = foe == _foe;
			if (GUI.Button(row, GUIContent.none, on ? _foeRowOn : _foeRow))
			{
				SelectFoe(foe);
			}
			GUI.Label(new Rect(row.x + S(12f), row.y + S(4f), row.width - S(24f), S(28f)),
				FoeDisplay(foe), on ? _foeNameOn : _foeName);
			GUI.Label(new Rect(row.x + S(12f), row.y + S(31f), row.width - S(24f), S(24f)), foe.Folder, _foeSmall);
		}
		GUI.EndScrollView();
	}

	private void DrawFoePoses(Rect area)
	{
		if (_foe == null)
		{
			if (_foeLoading)
			{
				GUI.Label(new Rect(area.x, area.y, area.width, S(28f)), TextConverter.ForDisplay("正在加载…"), _foeHint);
			}
			return;
		}
		GUI.Label(new Rect(area.x, area.y, area.width, S(28f)),
			_foe == null ? TextConverter.ForDisplay("先选角色")
				: string.Format(TextConverter.ForDisplay("{0} 张立绘"), _foe.Poses.Count), _foeSmall);
		var box = new Rect(area.x, area.y + S(32f), area.width, Mathf.Max(S(80f), area.height - S(32f)));
		float content = Mathf.Max(box.height, _foe.Poses.Count * (S(FoeRowHeight) + S(4f)));
		_foePoseScroll = GUI.BeginScrollView(box, _foePoseScroll, new Rect(0f, 0f, area.width - S(18f), content));
		for (int i = 0; i < _foe.Poses.Count; i++)
		{
			FoePose pose = _foe.Poses[i];
			var row = new Rect(0f, i * (S(FoeRowHeight) + S(4f)), area.width - S(20f), S(FoeRowHeight));
			bool on = pose == _foePose;
			if (GUI.Button(row, GUIContent.none, on ? _foeRowOn : _foeRow))
			{
				SelectFoePose(pose);
			}
			GUI.Label(new Rect(row.x + S(12f), row.y + S(4f), row.width - S(24f), S(28f)),
				TextConverter.ForDisplay(pose.Group), on ? _foeNameOn : _foeName);
			GUI.Label(new Rect(row.x + S(12f), row.y + S(31f), row.width - S(24f), S(24f)), pose.Variant, _foeSmall);
		}
		GUI.EndScrollView();
	}

	private void DrawFoeDetail(Rect area)
	{
		// 面板与技能属于当前立绘详情；展开后立绘、姿势列表及导出操作始终可见。
		Fill(area, PanelTheme.PaperDeep);
		// 标题栏（到下面立绘、面板开始处，高 52）：按钮贴在右端、上下占满，宽度跟着文字
		float bar = S(52f);
		string toggle = TextConverter.ForDisplay("面板与技能");
		float toggleWidth = Mathf.Min(_track.CalcSize(new GUIContent(toggle)).x + S(48f), area.width * 0.4f);
		GUI.Label(new Rect(area.x + S(16f), area.y, area.width - toggleWidth - S(32f), bar), FoeDisplay(_foe), _body);
		if (GUI.Button(new Rect(area.xMax - toggleWidth, area.y, toggleWidth, bar),
			TextConverter.ForDisplay("面板与技能"), _foeShowStats ? _trackOn : _track))
		{
			_foeShowStats = !_foeShowStats;
			EndFoeResize();
			_foeDragging = false;
			_foeZoom = 0f;
			_foePan = Vector2.zero;
		}
		var content = new Rect(area.x, area.y + bar, area.width, area.height - bar);
		if (_foeShowStats)
		{
			float gap = S(14f);
			float available = Mathf.Max(1f, content.width - gap);
			float statsWidth = ClampFoeStatsWidth(available * _foeStatsShare, available);
			HandleFoeStatsResize(content, available, gap, ref statsWidth);
			var portrait = new Rect(content.x, content.y, available - statsWidth, content.height);
			DrawFoePortrait(portrait);
			DrawFoeStats(new Rect(portrait.xMax + gap, content.y, statsWidth, content.height));
			var divider = new Rect(portrait.xMax, content.y, gap, content.height);
			// 分隔线：一根细线，鼠标移上去或拖动时加粗变亮，整条都能拖
			bool active = _foeResizeControl != 0 || divider.Contains(Event.current.mousePosition);
			float line = active ? S(3f) : S(1f);
			Fill(new Rect(divider.center.x - line / 2f, divider.y, line, divider.height), active ? PanelTheme.Ink : PanelTheme.Edge);
		}
		else DrawFoePortrait(content);
	}

	private float ClampFoeStatsWidth(float width, float available)
	{
		// 极窄窗口也给立绘和资料各留空间；拖动不改变文字缩放。
		float minStats = Mathf.Min(S(340f), available * 0.6f);
		float minPortrait = Mathf.Min(S(200f), available * 0.25f);
		return Mathf.Clamp(width, minStats, available - minPortrait);
	}

	private void HandleFoeStatsResize(Rect content, float available, float gap, ref float width)
	{
		int id = GUIUtility.GetControlID(0x4C4F4D52, FocusType.Passive, content);
		Event e = Event.current;
		Rect divider = new Rect(content.xMax - width - gap, content.y, gap, content.height);
		if (_foeResizeControl != 0 && !Application.isFocused) EndFoeResize();
		if (e.type == EventType.MouseDown && e.button == 0 && divider.Contains(e.mousePosition))
		{
			_foeResizeControl = id;
			GUIUtility.hotControl = id;
			_foeResizeGrab = e.mousePosition.x - divider.center.x;
			_foeDragging = false;
			e.Use();
		}
		else if (_foeResizeControl == id && e.type == EventType.MouseDrag)
		{
			width = ClampFoeStatsWidth(content.xMax - (e.mousePosition.x - _foeResizeGrab + gap / 2f), available);
			_foeStatsShare = width / available;
			e.Use();
		}
		else if (_foeResizeControl == id && (e.rawType == EventType.MouseUp || e.type == EventType.Ignore))
		{
			EndFoeResize();
			e.Use();
		}
	}

	private void EndFoeResize()
	{
		if (_foeResizeControl != 0 && GUIUtility.hotControl == _foeResizeControl) GUIUtility.hotControl = 0;
		_foeResizeControl = 0;
	}

	private void DrawFoePortrait(Rect area)
	{
		// 和动图页一样：上面是图，控制条放在底部
		float bar = S(44f);
		var view = new Rect(area.x, area.y, area.width, Mathf.Max(S(120f), area.height - bar - S(10f)));
		DrawChecker(view);
		HandleFoeViewMouse(view);
		GUI.BeginGroup(view);
		var localView = new Rect(0, 0, view.width, view.height);

		if (_foePose != null && _foePose.Sprite != null)
		{
			Sprite sprite = _foePose.Sprite;
			Rect part = sprite.textureRect;
			float fit = Mathf.Min((view.width - S(16f)) / part.width, (view.height - S(16f)) / part.height);
			float scale = _foeZoom > 0f ? _foeZoom : fit;
			float w = part.width * scale, h = part.height * scale;
			var dst = new Rect(localView.center.x - w / 2f + _foePan.x, localView.center.y - h / 2f + _foePan.y, w, h);
			GUI.DrawTextureWithTexCoords(dst, sprite.texture,
				new Rect(part.x / sprite.texture.width, part.y / sprite.texture.height,
					part.width / sprite.texture.width, part.height / sprite.texture.height));
		}
		else
		{
			GUI.Label(localView, TextConverter.ForDisplay(
				_foeLoading ? "正在加载…" : _foe == null ? "从左边选一个角色" : "从中间选一个变体"), _hint);
		}
		if (_foe != null && _foePose != null)
		{
			GUI.Label(new Rect(S(8f), view.height - S(26f), view.width - S(16f), S(22f)),
				FoeDisplay(_foe) + " · " + TextConverter.ForDisplay(_foePose.Group)
				+ "　" + TextConverter.ForDisplay("滚轮缩放，按住拖动"), _foeSmall);
		}
		GUI.EndGroup();

		// 底部控制条：重置视图、导出 PNG（按钮高度和动图页底部那排一样）
		var toolbar = new Rect(area.x, area.yMax - bar, area.width, bar);
		float buttonWidth = Mathf.Min(S(150f), (area.width - S(10f)) / 2f);
		if (GUI.Button(new Rect(toolbar.x, toolbar.y, buttonWidth, bar),
			TextConverter.ForDisplay("重置视图"), _track))
		{
			_foeZoom = 0f;
			_foePan = Vector2.zero;
		}
		bool canExport = _foePose?.Sprite != null;
		bool wasEnabled = GUI.enabled;
		GUI.enabled = wasEnabled && canExport;
		if (GUI.Button(new Rect(toolbar.xMax - buttonWidth, toolbar.y, buttonWidth, bar),
			TextConverter.ForDisplay("导出 PNG"), _button))
		{
			ExportFoeImage();
		}
		GUI.enabled = wasEnabled;
	}

	/// <summary>立绘的滚轮缩放与拖动。只在立绘区域内响应，浮层和按钮各自吃掉自己的事件。</summary>
	private void HandleFoeViewMouse(Rect view)
	{
		Event e = Event.current;
		if (e == null || _foePose == null || _foePose.Sprite == null)
		{
			return;
		}
		if (e.type == EventType.ScrollWheel && view.Contains(e.mousePosition))
		{
			// 往上滚放大、往下滚缩小；以指针为锚点，缩放后指针下的位置不动
			float before = _foeZoom > 0f ? _foeZoom : CurrentFoeFit(view);
			float after = Mathf.Clamp(before * (e.delta.y > 0f ? 0.88f : 1.14f), 0.15f, 6f);
			// 让指针指着的那个点缩放后还在原处：pan' = d - (d - pan) * k
			Vector2 d = e.mousePosition - view.center;
			_foePan = d - (d - _foePan) * (after / before);
			_foeZoom = after;
			e.Use();
		}
		else if (e.type == EventType.MouseDown && e.button == 0 && view.Contains(e.mousePosition))
		{
			_foeDragging = true;
			_foeGrab = e.mousePosition - _foePan;
		}
		else if (e.type == EventType.MouseDrag && _foeDragging)
		{
			_foePan = e.mousePosition - _foeGrab;
			e.Use();
		}
		else if (e.type == EventType.MouseUp)
		{
			_foeDragging = false;
		}
	}

	/// <summary>把当前选中的立绘导出成 PNG，文件放在动图页那个导出目录里。</summary>
	private void ExportFoeImage()
	{
		Sprite sprite = _foePose?.Sprite;
		if (sprite == null)
		{
			return;
		}
		string dir = ExportDirectory();
		string name = SafeFileName(FoeDisplay(_foe) + "_" + _foePose.Variant) + ".png";
		try
		{
			System.IO.Directory.CreateDirectory(dir);
			int width = Mathf.RoundToInt(sprite.textureRect.width);
			int height = Mathf.RoundToInt(sprite.textureRect.height);
			Color32[] pixels = ReadSprite(sprite, new Dictionary<Texture2D, Color32[]>());
			var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
			{
				hideFlags = HideFlags.HideAndDontSave,
			};
			texture.SetPixels32(pixels);
			texture.Apply();
			byte[] png = ImageConversion.EncodeToPNG(texture);
			UnityEngine.Object.Destroy(texture);
			System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, name), png);
			FrameNotice(string.Format(TextConverter.ForDisplay("已导出：{0}"), name), 6f);
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[鉴赏] 导出决斗立绘失败: " + e);
			FrameNotice(TextConverter.ForDisplay("导出失败，换个目录试试"), 6f);
		}
	}

	private float CurrentFoeFit(Rect view)
	{
		if (_foePose?.Sprite == null)
		{
			return 1f;
		}
		Rect part = _foePose.Sprite.textureRect;
		return Mathf.Min((view.width - S(16f)) / part.width, (view.height - S(16f)) / part.height);
	}

}
