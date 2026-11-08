using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LOMAssistant.Config;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 鉴赏的「动图」页：直接读游戏本体 <c>Mortal_Data/StreamingAssets/aa/*/battle_*.bundle</c> 里的 Sprite，
/// 按「同前缀 + 编号连续」认成一段序列帧动画，可以播放、逐帧看、调帧率、换缩放、导出透明底 GIF。
/// 不依赖任何解包出来的图片，装好游戏本体就能用；一次只加载一个角色的包，换角色就把上一个卸掉。
/// </summary>
public sealed partial class Gallery
{
	private const float FrameCharWidth = 250f;
	private const float FrameClipWidth = 230f;
	private const float FrameRowHeight = 44f;
	private const float FrameStageBarHeight = 150f;

	private static readonly Regex FrameName = new Regex(@"^(.*?)[._-]?(\d+)$");

	/// <summary>道具图标、书籍、货币这些也按编号排，但每张是不同的东西，不是逐帧动画，分组时跳过。</summary>
	private static readonly string[] FramePropPrefixes =
	{
		"item", "icon", "book", "food", "cloth", "weapon", "medicine", "material", "special_item",
	};

	private static bool IsPropIcon(string prefix)
	{
		foreach (string skip in FramePropPrefixes)
		{
			if (prefix.StartsWith(skip, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>一级分类：动图都在这一页里分开放。
	/// 立绘（portrait_*）不算：那里绝大多数是静态表情差分，而且是「立绘」页签的活。</summary>
	private static readonly string[] FrameCategories = { AllKinds, "角色", "特效", "剧情" };

	/// <summary>带序列帧的资源包前缀：其余包（背景、道具、书、视频、立绘）没有逐帧动画，不列出来。</summary>
	private static readonly string[] FrameBundlePrefixes = { "battle_", "particle", "picture" };

	/// <summary>包名 → 游戏里的名字。没列到的按 FrameTitleOf 里的规则推。</summary>
	private static readonly Dictionary<string, string> FrameTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		{ "battle_girl_003", "虞小梅" },
		{ "battle_girl4", "龙湘" },
		{ "battle_girl6_01", "郁竹" },
		{ "battle_girl9", "上官莹" },
		{ "battle_event_girl6_full", "郁竹（全武装）" },
		{ "battle_special_003", "叶云舟" },
		{ "battle_special_004", "樊啸天" },
		{ "battle_special_102", "南宫深" },
		{ "battle_special_103", "南宫浅" },
		{ "battle_special_401", "王二壮" },
		{ "battle_special_404", "丹霞子" },
		{ "battle_special_811", "唐杉" },
		{ "battle_special_999", "瑞笙" },
		{ "battle_brother1", "大师兄" },
		{ "battle_brother2", "二师兄" },
		{ "battle_brother4", "四师兄" },
	};

	private sealed class FrameCharacter
	{
		public string Key;   // battle_guard_001（资源包名）
		public string Path;
		public string Title; // 游戏里的名字：守卫、龙湘、飞石帮……
		public string Kind;  // 一级分类：角色 / 特效 / 剧情 / 立绘
	}

	private sealed class FrameClip
	{
		public string Name;  // guard1_idle
		public List<Sprite> Sprites;
		public int Width, Height;
	}

	private List<FrameCharacter> _frameCharacters;
	private List<FrameCharacter> _frameCharShown = new List<FrameCharacter>();
	private FrameCharacter _frameCharacter;
	private List<FrameClip> _frameClips;
	private FrameClip _frameClip;
	private AssetBundle _frameBundle;
	private Coroutine _frameLoadRoutine;
	private string _frameSearch = "";
	private string _frameKind = AllKinds;
	private Vector2 _frameCharScroll, _frameClipScroll;
	private int _frameIndex;
	private bool _framePlaying, _frameLoop = true, _frameLoading, _frameExporting;
	private float _frameNextAt;
	private string _frameNotice = "";
	private float _frameNoticeUntil;
	private string _frameExportResult;
	private GUIStyle _frameRow, _frameRowOn, _frameSmall;
	private float _frameRowScale = -1f;
	private float _frameZoom;
	private string _frameExportDirEdit;
	private static Texture2D _frameChecker;

	/// <summary>GIF 的时间精度只有 10 毫秒、且延时低于 20 毫秒会被查看器当成 100 毫秒，
	/// 所以帧率上限按 50 算（20 毫秒一帧）。</summary>
	private const int MaxFrameFps = 50;

	private int FrameFps => Mathf.Clamp(ConfigManager.GalleryFrameFps.Value, 1, MaxFrameFps);

	/// <summary>GIF 里实际写进去的延时（单位 1/100 秒）：四舍五入到 10 毫秒，下限 2（20 毫秒）。</summary>
	private static int FrameDelayCentiseconds(int fps)
	{
		return Math.Min(65535, Math.Max(2, (Mathf.RoundToInt(1000f / Math.Max(1, fps)) + 5) / 10));
	}

	/// <summary>每帧实际耗时（秒）。预览和导出都用它，保证看到多快、导出就多快。</summary>
	private float FrameStep => FrameDelayCentiseconds(FrameFps) / 100f;

	/// <summary>游戏本体的 Addressables 包目录（Windows 下是 StandaloneWindows64）。</summary>
	private static string BundleRoot()
	{
		string root = Path.Combine(Application.streamingAssetsPath ?? "", "aa");
		if (!Directory.Exists(root))
		{
			return null;
		}
		foreach (string dir in Directory.GetDirectories(root))
		{
			if (Directory.GetFiles(dir, "*.bundle").Length > 0)
			{
				return dir;
			}
		}
		return null;
	}

	private void OpenFrames()
	{
		_tab = FramesTab;
		if (_frameCharacters == null)
		{
			ScanFrameCharacters();
		}
	}

	// ---------------- 角色（包）列表 ----------------

	private void ScanFrameCharacters()
	{
		string root = BundleRoot();
		if (root == null)
		{
			_frameCharacters = new List<FrameCharacter>();
			FrameNotice(TextConverter.ForDisplay("找不到游戏资源目录，无法读取动图"), 8f);
			return;
		}
		var characters = new List<FrameCharacter>();
		foreach (string path in Directory.GetFiles(root, "*.bundle"))
		{
			string key = Path.GetFileName(path);
			int cut = key.IndexOf("_assets_all_", StringComparison.Ordinal);
			key = cut > 0 ? key.Substring(0, cut) : Path.GetFileNameWithoutExtension(key);
			// 只收带逐帧动画的包；背景、道具、书、视频这些没有序列帧，列出来点开也是空的
			if (!FrameBundlePrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
			{
				continue;
			}
			characters.Add(new FrameCharacter
			{
				Key = key,
				Path = path,
				Title = FrameTitleOf(key),
				Kind = FrameKindOf(key),
			});
		}
		// 分类按「角色、特效、剧情、立绘」排，同类里再按名字
		int Rank(string kind) => Array.IndexOf(FrameCategories, kind);
		characters.Sort((a, b) => Rank(a.Kind) != Rank(b.Kind)
			? Rank(a.Kind).CompareTo(Rank(b.Kind))
			: string.CompareOrdinal(a.Title, b.Title) != 0
				? string.CompareOrdinal(a.Title, b.Title)
				: string.CompareOrdinal(a.Key, b.Key));
		_frameCharacters = characters;
		FileLogger.Log($"[鉴赏] 动图：找到 {characters.Count} 个带序列帧的资源包");
		RefilterCharacters();
		if (_frameCharacter == null && _frameCharShown.Count > 0)
		{
			SelectCharacter(_frameCharShown[0]);
		}
	}

	/// <summary>包名 → 一级分类。</summary>
	private static string FrameKindOf(string key)
	{
		if (key.StartsWith("battle_", StringComparison.OrdinalIgnoreCase))
		{
			return "角色";
		}
		if (key.StartsWith("particle", StringComparison.OrdinalIgnoreCase))
		{
			return "特效";
		}
		if (key.StartsWith("picture", StringComparison.OrdinalIgnoreCase))
		{
			return "剧情";
		}
		if (key.StartsWith("portrait_", StringComparison.OrdinalIgnoreCase)
			|| key.StartsWith("portarit_", StringComparison.OrdinalIgnoreCase))
		{
			return "立绘";
		}
		return "其他";
	}

	/// <summary>包名 → 游戏里的名字。表里没有的按前缀归类。</summary>
	private static string FrameTitleOf(string key)
	{
		if (FrameTitles.TryGetValue(key, out string title))
		{
			return title;
		}
		if (key.StartsWith("battle_stone", StringComparison.OrdinalIgnoreCase))
		{
			return "飞石帮";
		}
		if (key.StartsWith("battle_ghost", StringComparison.OrdinalIgnoreCase))
		{
			return "地狱道";
		}
		if (key.StartsWith("battle_trainee", StringComparison.OrdinalIgnoreCase))
		{
			return "杂兵";
		}
		if (key.StartsWith("battle_tang", StringComparison.OrdinalIgnoreCase))
		{
			return "唐门";
		}
		if (key.StartsWith("battle_beggar", StringComparison.OrdinalIgnoreCase))
		{
			return "乞丐";
		}
		if (key.StartsWith("battle_pirate", StringComparison.OrdinalIgnoreCase))
		{
			return "海盗";
		}
		if (key.StartsWith("battle_badguy", StringComparison.OrdinalIgnoreCase))
		{
			return "歹人";
		}
		if (key.StartsWith("battle_guard", StringComparison.OrdinalIgnoreCase))
		{
			return "守卫";
		}
		if (key.StartsWith("battle_assassin", StringComparison.OrdinalIgnoreCase))
		{
			return "刺客";
		}
		if (key.StartsWith("battle_player", StringComparison.OrdinalIgnoreCase))
		{
			return "主角";
		}
		if (key.StartsWith("battle_sister", StringComparison.OrdinalIgnoreCase))
		{
			return "小师妹";
		}
		if (key.StartsWith("battle_big_tang", StringComparison.OrdinalIgnoreCase))
		{
			return "唐大鲸";
		}
		if (key.StartsWith("battle_man", StringComparison.OrdinalIgnoreCase))
		{
			return "平民";
		}
		if (key.StartsWith("battle_brother", StringComparison.OrdinalIgnoreCase))
		{
			return "师兄";
		}
		if (key.StartsWith("battle_special", StringComparison.OrdinalIgnoreCase))
		{
			return "特殊角色";
		}
		if (key.StartsWith("battle_girl", StringComparison.OrdinalIgnoreCase))
		{
			return "女性角色";
		}
		if (key.StartsWith("particle", StringComparison.OrdinalIgnoreCase))
		{
			return "粒子";
		}
		if (key.StartsWith("picture", StringComparison.OrdinalIgnoreCase))
		{
			return "剧情插图";
		}
		if (key.StartsWith("portrait_", StringComparison.OrdinalIgnoreCase)
			|| key.StartsWith("portarit_", StringComparison.OrdinalIgnoreCase))
		{
			return "立绘";
		}
		return key;
	}

	/// <summary>重新筛选列表。</summary>
	private void RefreshCharacters()
	{
		RefilterCharacters();
	}

	private void RefilterCharacters()
	{
		if (_frameCharacters == null)
		{
			_frameCharShown = new List<FrameCharacter>();
			return;
		}
		string search = (_frameSearch ?? "").Trim();
		_frameCharShown = _frameCharacters
			.Where(c => (_frameKind == AllKinds || c.Kind == _frameKind)
				&& (search.Length == 0
					|| (c.Title + " " + c.Key).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0))
			.ToList();
		// 当前选中的不在筛选结果里就放下，避免右边还留着别的分类的内容
		if (_frameCharacter != null && !_frameCharShown.Contains(_frameCharacter))
		{
			_frameCharacter = null;
			_frameClips = null;
			_frameClip = null;
			UnloadCharacter();
		}
	}

	// ---------------- 加载一个角色包 ----------------

	private void SelectCharacter(FrameCharacter character)
	{
		// 正在加载同一个角色就别重复起协程；已经加载好的也不用重来。
		// 上次加载失败（_frameClips 还是 null、也没在加载）时允许再点一次重试
		if (character == null
			|| (character == _frameCharacter && (_frameClips != null || _frameLoading)))
		{
			return;
		}
		_frameCharacter = character;
		_frameClips = null;
		_frameClip = null;
		_frameIndex = 0;
		_framePlaying = false;
		if (_frameLoadRoutine != null)
		{
			StopCoroutine(_frameLoadRoutine);
		}
		_frameLoadRoutine = StartCoroutine(LoadCharacter(character));
	}

	private IEnumerator LoadCharacter(FrameCharacter character)
	{
		UnloadCharacter();
		_frameLoading = true;
		AssetBundleCreateRequest request = AssetBundle.LoadFromFileAsync(character.Path);
		yield return request;
		_frameLoading = false;
		AssetBundle bundle = request.assetBundle;
		if (_frameCharacter != character)
		{
			// 加载期间又点了别的角色
			if (bundle != null)
			{
				bundle.Unload(false);
			}
			_frameLoadRoutine = null;
			yield break;
		}
		if (bundle == null)
		{
			FileLogger.LogWarning($"[鉴赏] 动图：{character.Key} 的包加载失败（bundle 为空）");
			FrameNotice(TextConverter.ForDisplay("这个角色的资源包读不出来"), 6f);
			_frameLoadRoutine = null;
			yield break;
		}
		_frameBundle = bundle;

		// Sprite 的存放位置每个包都不一样：
		//  - battle_*：容器里只登记一张 Animator，Sprite 全在 preloadTable（只能靠依赖链带进内存）
		//  - picture_02：容器里列全了 236 个 Sprite 的路径（可以直接按类型/按名字取）
		// 所以先拍内存快照，再依次尝试各种取法，最后统一用差集把「本次新加载的」捞回来。
		var sprites = new List<Sprite>();
		var seen = new HashSet<Sprite>();
		var before = new HashSet<Sprite>(Resources.FindObjectsOfTypeAll<Sprite>().Where(x => x != null));

		// ① 容器里登记了 Sprite 的包，一步到位
		try
		{
			Sprite[] typed = bundle.LoadAllAssets<Sprite>();
			if (typed != null)
			{
				foreach (Sprite sprite in typed)
				{
					if (sprite != null && seen.Add(sprite))
					{
						sprites.Add(sprite);
					}
				}
			}
		}
		catch (Exception e)
		{
			FileLogger.LogWarning($"[鉴赏] 动图：{character.Key} LoadAllAssets<Sprite> 失败: {e.Message}");
		}
		FileLogger.Log($"[鉴赏] 动图：{character.Key} 直接取到 {sprites.Count} 个 Sprite");

		// ② 按容器里的资产名逐个加载，把依赖（AnimationClip → Sprite）带进内存
		string[] names = Array.Empty<string>();
		try
		{
			names = bundle.GetAllAssetNames() ?? Array.Empty<string>();
		}
		catch (Exception e)
		{
			FileLogger.LogWarning($"[鉴赏] 动图：{character.Key} GetAllAssetNames 失败: {e.Message}");
		}
		foreach (string name in names)
		{
			try
			{
				Sprite one = bundle.LoadAsset<Sprite>(name);
				if (one != null && seen.Add(one))
				{
					sprites.Add(one);
				}
			}
			catch (Exception)
			{
				// 这个名字不是 Sprite，跳过
			}
			try
			{
				foreach (UnityEngine.Object sub in bundle.LoadAssetWithSubAssets(name))
				{
					if (sub is Sprite sprite && seen.Add(sprite))
					{
						sprites.Add(sprite);
					}
				}
			}
			catch (Exception)
			{
				// 同上
			}
		}

		// ③ 无参 LoadAllAssets 会把 preloadTable 里的对象整个加载进内存
		try
		{
			bundle.LoadAllAssets();
		}
		catch (Exception e)
		{
			FileLogger.LogWarning($"[鉴赏] 动图：{character.Key} 无参 LoadAllAssets 失败: {e.Message}");
		}

		// ④ 差集：不管前面哪一步把 Sprite 带进了内存，这里一次收齐
		foreach (Sprite sprite in Resources.FindObjectsOfTypeAll<Sprite>())
		{
			if (sprite != null && !before.Contains(sprite) && seen.Add(sprite))
			{
				sprites.Add(sprite);
			}
		}
		FileLogger.Log($"[鉴赏] 动图：{character.Key} 资产名 {names.Length} 个，合计取到 {sprites.Count} 个 Sprite");

		FileLogger.Log($"[鉴赏] 动图：{character.Key} 取到 {sprites.Count} 个 Sprite");
		if (sprites.Count == 0)
		{
			FrameNotice(TextConverter.ForDisplay("这个角色的资源包读不出来"), 6f);
			_frameLoadRoutine = null;
			yield break;
		}

		_frameClips = GroupClips(sprites.ToArray());
		_frameLoadRoutine = null;
		if (_frameClips.Count > 0)
		{
			SelectClip(_frameClips[0]);
		}
		else
		{
			FileLogger.LogWarning($"[鉴赏] 动图：{character.Key} 有 {sprites.Count} 个 Sprite，但分组后没有 4 帧以上的动作；"
				+ "sample names: " + string.Join(", ", sprites.Take(5).Select(x => x.name)));
			FrameNotice(TextConverter.ForDisplay("这个角色没有可播放的序列"), 6f);
		}
	}

	/// <summary>按已知前缀逐个尝试加载 Sprite（LoadAllAssets 拿不到时的最后手段）。</summary>
	private static List<Sprite> LoadSpritesByName(AssetBundle bundle, string characterKey)
	{
		var sprites = new List<Sprite>();
		// 常见动作前缀
		string[] actions = { "idle", "run", "dash", "attack", "attack1", "attack2", "attack3", "die", "dead", "hurt", "stand", "standby", "block", "defense", "jump", "kick", "intro", "shoot", "flee", "ready", "roll", "talk", "walk", "fall" };
		string prefix = characterKey.Replace("battle_", "");
		// 角色名可能带数字后缀，如 guard_001 -> guard1
		string name = prefix.Replace("_", "");
		foreach (string action in actions)
		{
			for (int i = 0; i < 100; i++)
			{
				string spriteName = name + "_" + action + "_" + i;
				Sprite sprite = bundle.LoadAsset<Sprite>(spriteName);
				if (sprite != null)
				{
					sprites.Add(sprite);
				}
				else if (i > 0)
				{
					break; // 这个动作序列结束
				}
			}
		}
		return sprites;
	}

	private static List<Sprite> SpritesFromAnimations(UnityEngine.Object[] all)
	{
		var sprites = new List<Sprite>();
		try
		{
			var type = Type.GetType("UnityEditor.AnimationUtility, UnityEditor");
			var method = type?.GetMethod("GetObjectReferenceCurveBindings");
			var frameMethod = type?.GetMethod("GetObjectReferenceCurve");
			if (type == null || method == null || frameMethod == null)
			{
				FileLogger.LogWarning("[鉴赏] 动图：AnimationUtility 不可用（" + (type == null ? "类型找不到" : "方法找不到") + "）");
				return sprites;
			}
			foreach (UnityEngine.Object obj in all)
			{
				if (!(obj is AnimationClip clip))
				{
					continue;
				}
				var bindings = (Array)method.Invoke(null, new object[] { clip });
				foreach (object binding in bindings)
				{
					var curve = frameMethod.Invoke(null, new object[] { clip, binding, null });
					var keys = curve?.GetType().GetField("key")?.GetValue(curve) as Array;
					if (keys == null)
					{
						continue;
					}
					foreach (object key in keys)
					{
						var value = key.GetType().GetField("value")?.GetValue(key);
						if (value is Sprite sprite && !sprites.Contains(sprite))
						{
							sprites.Add(sprite);
						}
					}
				}
			}
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[鉴赏] 动图：读动画曲线失败: " + e.Message);
		}
		return sprites;
	}

	private static List<FrameClip> GroupClips(Sprite[] sprites)
	{
		var buckets = new Dictionary<string, SortedDictionary<int, Sprite>>(StringComparer.Ordinal);
		foreach (Sprite sprite in sprites)
		{
			if (sprite == null)
			{
				continue;
			}
			Match match = FrameName.Match(sprite.name);
			if (!match.Success || !int.TryParse(match.Groups[2].Value, out int index))
			{
				continue;
			}
			string prefix = match.Groups[1].Value.TrimEnd('.', '_', '-');
			if (prefix.Length == 0 || IsPropIcon(prefix))
			{
				continue;
			}
			if (!buckets.TryGetValue(prefix, out SortedDictionary<int, Sprite> slot))
			{
				buckets[prefix] = slot = new SortedDictionary<int, Sprite>();
			}
			slot[index] = sprite;
		}

		var clips = new List<FrameClip>();
		foreach (KeyValuePair<string, SortedDictionary<int, Sprite>> pair in buckets)
		{
			List<int> numbers = pair.Value.Keys.ToList();
			if (numbers.Count < 4)
			{
				continue;
			}
			List<int> run = LongestRun(numbers);
			if (run.Count < 4)
			{
				continue;
			}
			var spritesOfClip = run.Select(n => pair.Value[n]).ToList();
			Rect rect = spritesOfClip[0].rect;
			clips.Add(new FrameClip
			{
				Name = pair.Key,
				Sprites = spritesOfClip,
				Width = Mathf.RoundToInt(rect.width),
				Height = Mathf.RoundToInt(rect.height),
			});
		}
		return clips.OrderByDescending(c => c.Sprites.Count).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
	}

	private static List<int> LongestRun(List<int> numbers)
	{
		var best = new List<int> { numbers[0] };
		var current = new List<int> { numbers[0] };
		for (int i = 1; i < numbers.Count; i++)
		{
			if (numbers[i] == numbers[i - 1] + 1)
			{
				current.Add(numbers[i]);
			}
			else
			{
				if (current.Count > best.Count)
				{
					best = current;
				}
				current = new List<int> { numbers[i] };
			}
		}
		return current.Count > best.Count ? current : best;
	}

	private void SelectClip(FrameClip clip)
	{
		if (clip == null)
		{
			return;
		}
		_frameClip = clip;
		_frameIndex = 0;
		_framePlaying = true;
		_frameNextAt = 0f;
	}

	private void UnloadCharacter()
	{
		if (_frameBundle != null)
		{
			try
			{
				// 必须连对象一起销毁。只卸包的话 Sprite 还留在内存里，
				// 下次再打开同一个角色时「加载前后差集」永远是 0，就什么都取不到。
				// 鉴赏只在标题画面打开，这里是本 Mod 自己加载的包，游戏没在用。
				_frameBundle.Unload(true);
			}
			catch (Exception e)
			{
				FileLogger.LogWarning("[鉴赏] 卸载角色包失败: " + e.Message);
			}
			_frameBundle = null;
		}
	}

	// ---------------- 绘制 ----------------

	private void DrawFrames(Rect screen)
	{
		FrameStyles();
		DrawHeader();
		float top = S(110f);
		float left = S(40f);
		float width = screen.width - S(80f);

		// 导出位置：默认在本 Mod 目录下的 GalleryGif，可以在这里改，目录不存在会自动建
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
		var chars = new Rect(body.x, body.y, S(FrameCharWidth), body.height);
		var clips = new Rect(chars.xMax + S(12f), body.y, S(FrameClipWidth), body.height);
		var stage = new Rect(clips.xMax + S(12f), body.y, body.width - chars.width - clips.width - S(24f), body.height);
		DrawCharacterList(chars);
		DrawClipList(clips);
		DrawFrameStage(stage);

		if (Time.unscaledTime < _frameNoticeUntil)
		{
			var notice = new Rect(0f, screen.height - S(48f), screen.width, S(40f));
			Fill(notice, new Color(0f, 0f, 0f, 0.62f));
			GUI.Label(notice, _frameNotice, _hint);
		}
	}

	private void FrameStyles()
	{
		if (_frameRow != null && Mathf.Approximately(_frameRowScale, _scale))
		{
			return;
		}
		_frameRowScale = _scale;
		_frameRow = new GUIStyle(_track) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
		_frameRowOn = new GUIStyle(_trackOn) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };
		_frameSmall = PanelTheme.Label(_scale, 16, PanelTheme.InkMuted, wrap: false);
	}

	private void DrawCharacterList(Rect area)
	{
		var search = new Rect(area.x, area.y, area.width, S(40f));
		string before = _frameSearch;
		_frameSearch = GUI.TextField(search, _frameSearch ?? "", _field);
		if (_frameSearch != before)
		{
			RefreshCharacters();
		}
		// 一级分类：角色 / 特效 / 剧情 / 立绘，都在这一个页签里分开放
		float gap = S(5f);
		int count = FrameCategories.Length;
		float cell = (area.width - gap * (count - 1)) / count;
		for (int i = 0; i < count; i++)
		{
			var box = new Rect(area.x + i * (cell + gap), search.yMax + S(8f), cell, S(34f));
			if (GUI.Button(box, TextConverter.ForDisplay(FrameCategories[i]), _frameKind == FrameCategories[i] ? _trackOn : _track))
			{
				_frameKind = FrameCategories[i];
				RefreshCharacters();
				if (_frameCharacter == null && _frameCharShown.Count > 0)
				{
					SelectCharacter(_frameCharShown[0]);
				}
			}
		}

		GUI.Label(new Rect(area.x, search.yMax + S(8f) + S(34f) + S(4f), area.width, S(20f)),
			string.Format(TextConverter.ForDisplay("共 {0} 个"), _frameCharShown.Count), _frameSmall);

		float listTop = search.yMax + S(8f) + S(34f) + S(24f);
		var list = new Rect(area.x, listTop, area.width, Mathf.Max(S(80f), area.yMax - listTop));
		float content = Mathf.Max(list.height, _frameCharShown.Count * (S(FrameRowHeight) + S(4f)));
		_frameCharScroll = GUI.BeginScrollView(list, _frameCharScroll, new Rect(0f, 0f, area.width - S(18f), content));
		for (int i = 0; i < _frameCharShown.Count; i++)
		{
			FrameCharacter character = _frameCharShown[i];
			var row = new Rect(0f, i * (S(FrameRowHeight) + S(4f)), area.width - S(20f), S(FrameRowHeight));
			bool on = character == _frameCharacter;
			if (GUI.Button(row, GUIContent.none, on ? _frameRowOn : _frameRow))
			{
				SelectCharacter(character);
			}
			bool wasEnabled = GUI.enabled;
			GUI.enabled = false;
			// 主行是游戏里的名字，副行留着资源包名，方便对照
			GUI.Label(new Rect(row.x + S(10f), row.y + S(3f), row.width - S(20f), S(22f)), TextConverter.ForDisplay(character.Title), _frameRow);
			GUI.Label(new Rect(row.x + S(10f), row.y + S(23f), row.width - S(20f), S(18f)), character.Key, _frameSmall);
			GUI.enabled = wasEnabled;
		}
		GUI.EndScrollView();
	}

	private void DrawClipList(Rect area)
	{
		GUI.Label(new Rect(area.x, area.y, area.width, S(28f)),
			_frameCharacter == null ? TextConverter.ForDisplay("先选角色")
				: _frameLoading ? TextConverter.ForDisplay("正在加载…")
				: _frameClips == null ? ""
				: string.Format(TextConverter.ForDisplay("{0} 段动作"), _frameClips.Count), _frameSmall);

		if (_frameClips == null)
		{
			return;
		}
		var list = new Rect(area.x, area.y + S(32f), area.width, Mathf.Max(S(80f), area.height - S(32f)));
		float content = Mathf.Max(list.height, _frameClips.Count * (S(FrameRowHeight) + S(4f)));
		_frameClipScroll = GUI.BeginScrollView(list, _frameClipScroll, new Rect(0f, 0f, area.width - S(18f), content));
		for (int i = 0; i < _frameClips.Count; i++)
		{
			FrameClip clip = _frameClips[i];
			var row = new Rect(0f, i * (S(FrameRowHeight) + S(4f)), area.width - S(20f), S(FrameRowHeight));
			bool on = clip == _frameClip;
			if (GUI.Button(row, GUIContent.none, on ? _frameRowOn : _frameRow))
			{
				SelectClip(clip);
			}
			bool wasEnabled = GUI.enabled;
			GUI.enabled = false;
			GUI.Label(new Rect(row.x + S(10f), row.y + S(3f), row.width - S(20f), S(22f)), clip.Name, _frameRow);
			GUI.Label(new Rect(row.x + S(10f), row.y + S(23f), row.width - S(20f), S(18f)),
				string.Format(TextConverter.ForDisplay("{0} 帧 · {1}×{2}"), clip.Sprites.Count, clip.Width, clip.Height), _frameSmall);
			GUI.enabled = wasEnabled;
		}
		GUI.EndScrollView();
	}

	private void DrawFrameStage(Rect stage)
	{
		var image = new Rect(stage.x, stage.y, stage.width, Mathf.Max(S(160f), stage.height - S(FrameStageBarHeight)));
		DrawChecker(image);

		Sprite sprite = _frameClip != null && _frameIndex < _frameClip.Sprites.Count ? _frameClip.Sprites[_frameIndex] : null;
		if (sprite == null)
		{
			GUI.Label(image, TextConverter.ForDisplay(
				_frameCharacter == null ? "从左边选一个角色" : _frameLoading ? "正在加载…" : "从中间选一段动作"), _hint);
			return;
		}

		if (_frameZoom > 0f)
		{
			Rect part = sprite.textureRect;
			float fit = Mathf.Min((image.width - S(24f)) / part.width, (image.height - S(24f)) / part.height);
			var rect = new Rect(
				image.x + (image.width - part.width * fit) / 2f,
				image.y + (image.height - part.height * fit) / 2f,
				part.width * fit,
				part.height * fit);
			GUI.DrawTextureWithTexCoords(rect, sprite.texture,
				new Rect(part.x / sprite.texture.width, part.y / sprite.texture.height, part.width / sprite.texture.width, part.height / sprite.texture.height));
		}
		else
		{
			DrawFit(new Rect(image.x + S(12f), image.y + S(12f), image.width - S(24f), image.height - S(24f)), sprite, null);
		}

		float y = image.yMax + S(10f);
		var play = new Rect(stage.x, y, S(120f), S(44f));
		if (GUI.Button(play, TextConverter.ForDisplay(_framePlaying ? "暂停" : "播放"), _framePlaying ? _button : _primary))
		{
			_framePlaying = !_framePlaying;
			_frameNextAt = 0f;
		}
		if (GUI.Button(new Rect(play.xMax + S(8f), y, S(56f), S(44f)), "◀", _button))
		{
			_framePlaying = false;
			StepFrame(-1);
		}
		if (GUI.Button(new Rect(play.xMax + S(72f), y, S(56f), S(44f)), "▶", _button))
		{
			_framePlaying = false;
			StepFrame(1);
		}

		int total = _frameClip == null ? 0 : _frameClip.Sprites.Count;
		var seek = new Rect(play.xMax + S(140f), y + S(10f), Mathf.Max(S(120f), stage.width - S(430f)), S(24f));
		_frameIndex = DrawFrameSlider(seek, _frameIndex, 0, Mathf.Max(0, total - 1));
		GUI.Label(new Rect(seek.xMax + S(10f), y + S(8f), S(90f), S(28f)), (total == 0 ? 0 : _frameIndex + 1) + " / " + total, _frameSmall);

		float y2 = y + S(52f);
		GUI.Label(new Rect(stage.x, y2, S(56f), S(40f)), TextConverter.ForDisplay("帧率"), _frameSmall);
		var fpsBar = new Rect(stage.x + S(58f), y2 + S(8f), S(160f), S(24f));
		int fps = DrawFrameSlider(fpsBar, FrameFps, 1, MaxFrameFps);
		if (fps != FrameFps)
		{
			ConfigManager.GalleryFrameFps.Value = fps;
		}
		GUI.Label(new Rect(fpsBar.xMax + S(8f), y2, S(48f), S(40f)), FrameFps.ToString(), _frameSmall);

		if (GUI.Button(new Rect(fpsBar.xMax + S(56f), y2, S(96f), S(40f)),
			TextConverter.ForDisplay(_frameLoop ? "循环开" : "循环关"), _frameLoop ? _trackOn : _track))
		{
			_frameLoop = !_frameLoop;
		}
		if (GUI.Button(new Rect(fpsBar.xMax + S(160f), y2, S(110f), S(40f)),
			TextConverter.ForDisplay(_frameZoom > 0f ? "原始尺寸" : "适应窗口"), _frameZoom > 0f ? _trackOn : _track))
		{
			_frameZoom = _frameZoom > 0f ? 0f : 1f;
		}

		string[] bgLabels = { "底色 深", "底色 白", "底色 黑" };
		int bg = Mathf.Clamp(ConfigManager.GalleryFrameBg.Value, 0, 2);
		if (GUI.Button(new Rect(fpsBar.xMax + S(276f), y2, S(96f), S(40f)),
			TextConverter.ForDisplay(bgLabels[bg]), _track))
		{
			ConfigManager.GalleryFrameBg.Value = (bg + 1) % 3;
		}

		var export = new Rect(stage.xMax - S(180f), y2, S(180f), S(40f));
		bool canExport = !_frameExporting && _frameClip != null && !_frameLoading;
		bool wasOn = GUI.enabled;
		GUI.enabled = wasOn && canExport;
		if (GUI.Button(export, TextConverter.ForDisplay(_frameExporting ? "导出中…" : "导出 GIF"), _button))
		{
			ExportClip();
		}
		GUI.enabled = wasOn;

		FrameNoticeTick();
	}

	private void DrawChecker(Rect box)
	{
		// 透明底的图在深色棋盘格上轮廓不清，可以让用户换成白底或黑底
		switch (ConfigManager.GalleryFrameBg.Value)
		{
			case 1:
				Fill(box, Color.white);
				return;
			case 2:
				Fill(box, Color.black);
				return;
		}
		Fill(box, new Color(0.1f, 0.1f, 0.11f, 1f));
		if (_frameChecker == null)
		{
			_frameChecker = new Texture2D(2, 2, TextureFormat.RGBA32, false)
			{
				hideFlags = HideFlags.HideAndDontSave,
				filterMode = FilterMode.Point,
				wrapMode = TextureWrapMode.Repeat,
			};
			var dark = new Color32(26, 26, 29, 255);
			var light = new Color32(34, 34, 38, 255);
			_frameChecker.SetPixels32(new[] { light, dark, dark, light });
			_frameChecker.Apply();
		}
		float cell = Mathf.Max(6f, S(14f));
		GUI.DrawTextureWithTexCoords(box, _frameChecker, new Rect(0f, 0f, box.width / (cell * 2f), box.height / (cell * 2f)));
	}

	/// <summary>自绘滑条：点、拖都改值，返回新值。</summary>
	private int DrawFrameSlider(Rect rect, int value, int min, int max)
	{
		if (max <= min)
		{
			return min;
		}
		value = Mathf.Clamp(value, min, max);
		float t = (value - min) / (float)(max - min);
		float lineY = rect.y + rect.height / 2f - S(2f);
		Fill(new Rect(rect.x, lineY, rect.width, S(4f)), PanelTheme.PaperDeep);
		Fill(new Rect(rect.x, lineY, rect.width * t, S(4f)), PanelTheme.Cinnabar);
		Fill(new Rect(rect.x + rect.width * t - S(5f), rect.y + rect.height / 2f - S(9f), S(10f), S(18f)), PanelTheme.Cinnabar);

		Event current = Event.current;
		if ((current.type == EventType.MouseDown || current.type == EventType.MouseDrag) && rect.Contains(current.mousePosition))
		{
			current.Use();
			return Mathf.RoundToInt(min + Mathf.Clamp01((current.mousePosition.x - rect.x) / rect.width) * (max - min));
		}
		return value;
	}

	private void FrameNotice(string text, float seconds = 4f)
	{
		_frameNotice = text;
		_frameNoticeUntil = Time.unscaledTime + seconds;
	}

	private void FrameNoticeTick()
	{
		if (_frameExportResult == null)
		{
			return;
		}
		string result = _frameExportResult;
		_frameExportResult = null;
		_frameExporting = false;
		FrameNotice(result, 6f);
	}

	// ---------------- 播放 ----------------

	private void StepFrame(int delta)
	{
		if (_frameClip == null || _frameClip.Sprites.Count == 0)
		{
			return;
		}
		int count = _frameClip.Sprites.Count;
		_frameIndex = (_frameIndex + delta + count) % count;
	}

	private void TickFrames()
	{
		Keyboard keyboard = Keyboard.current;
		if (keyboard != null)
		{
			if (keyboard.spaceKey.wasPressedThisFrame)
			{
				_framePlaying = !_framePlaying;
				_frameNextAt = 0f;
			}
			if (keyboard.leftArrowKey.wasPressedThisFrame)
			{
				_framePlaying = false;
				StepFrame(-1);
			}
			if (keyboard.rightArrowKey.wasPressedThisFrame)
			{
				_framePlaying = false;
				StepFrame(1);
			}
			if (keyboard.upArrowKey.wasPressedThisFrame)
			{
				SelectAdjacentClip(-1);
			}
			if (keyboard.downArrowKey.wasPressedThisFrame)
			{
				SelectAdjacentClip(1);
			}
		}
		if (_frameClip == null || !_framePlaying)
		{
			return;
		}
		float now = Time.unscaledTime;
		if (now < _frameNextAt)
		{
			return;
		}
		_frameNextAt = now + FrameStep;
		_frameIndex++;
		if (_frameIndex >= _frameClip.Sprites.Count)
		{
			if (_frameLoop)
			{
				_frameIndex = 0;
			}
			else
			{
				_frameIndex = _frameClip.Sprites.Count - 1;
				_framePlaying = false;
			}
		}
	}

	private void SelectAdjacentClip(int delta)
	{
		if (_frameClips == null || _frameClip == null)
		{
			return;
		}
		int index = _frameClips.IndexOf(_frameClip);
		int next = Mathf.Clamp(index + delta, 0, _frameClips.Count - 1);
		if (next != index)
		{
			SelectClip(_frameClips[next]);
		}
	}

	/// <summary>关掉鉴赏时把角色包卸掉，不占着内存。</summary>
	private void CloseFrames()
	{
		if (_frameLoadRoutine != null)
		{
			StopCoroutine(_frameLoadRoutine);
			_frameLoadRoutine = null;
		}
		UnloadCharacter();
		_frameClips = null;
		_frameClip = null;
		_framePlaying = false;
		_frameLoading = false;
	}

	// ---------------- 导出 ----------------

	private void ExportClip()
	{
		if (_frameClip == null || _frameExporting)
		{
			return;
		}
		var frames = new List<(Color32[], int, int)>();
		var cache = new Dictionary<Texture2D, Color32[]>();
		try
		{
			foreach (Sprite sprite in _frameClip.Sprites)
			{
				frames.Add((ReadSprite(sprite, cache), Mathf.RoundToInt(sprite.textureRect.width), Mathf.RoundToInt(sprite.textureRect.height)));
			}
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[鉴赏] 读帧像素失败: " + e);
			FrameNotice(TextConverter.ForDisplay("读不出这一段的像素，导不了"), 6f);
			return;
		}
		string name = SafeFileName(_frameClip.Name) + ".gif";
		string dir = ExportDirectory();
		string path;
		try
		{
			Directory.CreateDirectory(dir);
			path = Path.Combine(dir, name);
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[鉴赏] 导出目录不可用 " + dir + ": " + e.Message);
			FrameNotice(TextConverter.ForDisplay("这个导出目录用不了，换一个"), 6f);
			return;
		}
		// 和预览用同一套延时，导出的速度和看到的一样
		int delay = FrameDelayCentiseconds(FrameFps) * 10;
		_frameExporting = true;
		FrameNotice(string.Format(TextConverter.ForDisplay("正在导出 {0}…"), name), 120f);
		Task.Run(() =>
		{
			try
			{
				GifWriter.Save(path, frames, delay);
				_frameExportResult = string.Format(TextConverter.ForDisplay("已导出：{0}"), name);
			}
			catch (Exception e)
			{
				FileLogger.LogWarning("[鉴赏] 导出 GIF 失败: " + e);
				_frameExportResult = string.Format(TextConverter.ForDisplay("导出失败：{0}"), e.Message);
			}
		});
	}

	/// <summary>把 sprite 在纹理上的那块抠出来。纹理多半不可读，用 RenderTexture 拷一份。</summary>
	private static Color32[] ReadSprite(Sprite sprite, Dictionary<Texture2D, Color32[]> cache)
	{
		Texture2D texture = sprite.texture;
		if (!cache.TryGetValue(texture, out Color32[] source))
		{
			source = ReadTexture(texture);
			cache[texture] = source;
		}
		Rect part = sprite.textureRect;
		int width = Mathf.RoundToInt(part.width), height = Mathf.RoundToInt(part.height);
		int x0 = Mathf.RoundToInt(part.x), y0 = Mathf.RoundToInt(part.y);
		var pixels = new Color32[Mathf.Max(1, width * height)];
		for (int y = 0; y < height; y++)
		{
			int sy = y0 + y;
			if (sy < 0 || sy >= texture.height)
			{
				continue;
			}
			for (int x = 0; x < width; x++)
			{
				int sx = x0 + x;
				if (sx < 0 || sx >= texture.width)
				{
					continue;
				}
				pixels[y * width + x] = source[sy * texture.width + sx];
			}
		}
		return pixels;
	}

	private static Color32[] ReadTexture(Texture2D texture)
	{
		if (texture.isReadable)
		{
			return texture.GetPixels32();
		}
		RenderTexture previous = RenderTexture.active;
		RenderTexture temp = RenderTexture.GetTemporary(texture.width, texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
		Graphics.Blit(texture, temp);
		RenderTexture.active = temp;
		var copy = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false)
		{
			hideFlags = HideFlags.HideAndDontSave,
		};
		copy.ReadPixels(new Rect(0f, 0f, texture.width, texture.height), 0, 0);
		copy.Apply();
		RenderTexture.active = previous;
		RenderTexture.ReleaseTemporary(temp);
		Color32[] pixels = copy.GetPixels32();
		Destroy(copy);
		return pixels;
	}

	/// <summary>页面上填的导出目录；空着就用配置里的。填了会顺手记进配置。</summary>
	private string ExportDirectory()
	{
		string dir = (_frameExportDirEdit ?? "").Trim();
		if (dir.Length == 0)
		{
			dir = ConfigManager.GalleryFrameExportDir.Value;
		}
		if (dir != ConfigManager.GalleryFrameExportDir.Value)
		{
			ConfigManager.GalleryFrameExportDir.Value = dir;
		}
		return dir;
	}

	private void OpenExportFolder()
	{
		string dir = ExportDirectory();
		try
		{
			Directory.CreateDirectory(dir);
			System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[鉴赏] 打开导出文件夹失败: " + e.Message);
			FrameNotice(TextConverter.ForDisplay("打不开这个文件夹"), 5f);
		}
	}

	private static string SafeFileName(string name)
	{
		char[] invalid = Path.GetInvalidFileNameChars();
		var builder = new System.Text.StringBuilder(name.Length);
		foreach (char c in name)
		{
			builder.Append(invalid.Contains(c) || c == '/' || c == '\\' ? '_' : c);
		}
		return builder.ToString().Trim();
	}
}
