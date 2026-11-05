using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using LOMAssistant.Config;
using LOMAssistant.Features.Story;
using Mortal.Core;
using Mortal.Story;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.UI;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 鉴赏：标题画面多一个「鉴赏」按钮，能看剧情 CG、结局 CG，听游戏音乐。
/// CG 只算玩家见过的：跨存档的已读对白和各个存档里的剧情记录能反推出以前看过的图；都推不出来的（一支里没有对白）从这个版本起遇到时记进自己的文件。音乐全部开放。
/// 已读记录在读档时可能丢一段（ReadStoryKeeper 补上之前丢的补不回来），所以还要看存档。存档只读不写。
/// 没解锁的图一律不读取。
/// </summary>
public sealed partial class Gallery : MonoBehaviour
{
	private sealed class Picture
	{
		public string Key;
		public Sprite Direct; // 结局 CG 直接带图，其余按编号从资源里读
		public string Path; // 场景图的资源路径，场景图全部开放
		public Color? Back; // 透明底的线稿要垫底色：插图画在书页上，大图垫白底或黑底
		public string Kind = "";
		public readonly HashSet<string> Records = new HashSet<string>();
		public bool Unlocked;
		public readonly HashSet<string> Gate = new HashSet<string>();
	}

	private sealed class Track
	{
		public string Key, Title;
	}

	private const int PictureTab = 0, MusicTab = 1, StageTab = 2;

	private static readonly int[] TabOrder = { PictureTab, StageTab, MusicTab };

	private const int Columns = 4, Rows = 3, TrackColumns = 3;

	private const float HintSeconds = 3f;

	// 游戏插图垫的书页 pic_book_1 的平均色（书页在剧情场景里，标题画面拿不到）
	private static readonly Color BookPage = new Color(0.882f, 0.835f, 0.769f);

	private const string AllKinds = "全部";

	private static readonly string[] Kinds = { AllKinds, "人物", "战斗", "工作", "日常", "剧情", "场景", "结局" };

	// 图片编号的开头：编号是开发组按内容取的，比按剧本猜人物可靠
	private static readonly string[] PersonKeys = { "girl", "big_girl", "sister", "brother1", "door_girl", "dance", "yeh_walk" };
	private static readonly string[] FightKeys = { "big_fight", "big_kill", "big_pk", "big_sword", "fight_event", "yeh_fight", "bash", "player_", "sword_" };
	private static readonly string[] DailyKeys = { "free", "bakefish", "casino", "chess", "eating", "farm", "fishing", "sleep", "spa", "study", "teashop", "train", "inn", "center_", "room", "mall", "useless" };

	private static readonly Regex PictureCall = new Regex(@"mainui\.Show(Big)?Picture\(""([^""]+)""(?:\s*,\s*(\d+))?");
	private static readonly Regex Say = new Regex(@"^say\(.*GetStoryText\(""([^""]+)""\)");
	private static readonly Regex AddStory = new Regex(@"statmodifymanager\.AddStory\(""([^""]+)""");
	private static readonly Regex Word = new Regex(@"[A-Za-z0-9_]{4,}");

	private static Gallery _instance;
	private static Button _titleButton;
	private static int _closedFrame = -10;

	/// <summary>鉴赏开着（或刚关掉的这一帧）：Esc、右键和手柄归鉴赏用，不交给游戏。</summary>
	internal static bool IsOpen => (_instance != null && _instance._open) || Time.frameCount - _closedFrame <= 1;
	private static HashSet<string> _known;

	private static string RecordPath => Path.Combine(Paths.ConfigPath, "LOMAssistant_Gallery.txt");

	private bool _open, _scanning, _musicChanged;
	private int _tab = PictureTab;
	private int _page, _viewing = -1;
	private string _kind = AllKinds;
	private HashSet<string> _played = new HashSet<string>();
	private List<Picture> _pictures;
	private List<Picture> _shown = new List<Picture>();
	private List<Track> _tracks;
	private Track _playing;
	private readonly Dictionary<string, AsyncOperationHandle<Sprite>> _loads = new Dictionary<string, AsyncOperationHandle<Sprite>>();

	private GUIStyle _title, _body, _muted, _button, _primary, _card, _center, _track, _trackOn, _hint, _field;
	private float _hintUntil;
	private float _scale = -1f;
	private bool _dark;

	public static void Ensure(GameObject host)
	{
		if (_instance == null)
		{
			_instance = host.GetComponent<Gallery>() ?? host.AddComponent<Gallery>();
		}
	}

	/// <summary>剧情里出现过的图记下来。只写自己的文件，不碰存档。</summary>
	internal static void Seen(string key)
	{
		try
		{
			if (Known().Add(key))
			{
				File.AppendAllText(RecordPath, key + "\n", new UTF8Encoding(false));
			}
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[鉴赏] 记录解锁失败: " + e.Message);
		}
	}

	private static HashSet<string> Known()
	{
		if (_known == null)
		{
			_known = new HashSet<string>();
			if (File.Exists(RecordPath))
			{
				_known.UnionWith(File.ReadAllLines(RecordPath).Where(line => line.Length > 0));
			}
		}
		return _known;
	}

	internal static void AddButton(TitleManager title)
	{
		if (!ConfigManager.TitleGallery.Value)
		{
			return;
		}
		Button library = Traverse.Create(title).Field("_libraryButton").GetValue<Button>();
		if (library == null)
		{
			return;
		}
		GameObject source = (library.GetComponentInParent<TitleOptionButton>()?.gameObject) ?? library.gameObject;
		GameObject copy = Instantiate(source, source.transform.parent);
		copy.name = "Gallery";
		copy.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
		// 复制来的按钮还连着打开讲经堂的事件
		foreach (EventTrigger trigger in copy.GetComponentsInChildren<EventTrigger>(true))
		{
			Destroy(trigger);
		}
		Button button = copy.GetComponentInChildren<Button>(true);
		button.onClick = new Button.ButtonClickedEvent();
		button.onClick.AddListener(Open);
		_titleButton = button;
		// 游戏打开讲经堂等面板时会停用这组按钮，鉴赏也要一起停用
		Traverse buttons = Traverse.Create(title).Field("_buttons");
		buttons.SetValue(buttons.GetValue<Button[]>().Append(button).ToArray());
		// 标题按钮的上下是写死的，复制来的按钮要接进去，手柄才走得到
		Navigation above = library.navigation;
		FileLogger.Log("[鉴赏] 标题按钮的导航方式: " + above.mode);
		if (above.mode == Navigation.Mode.Explicit)
		{
			Selectable below = above.selectOnDown;
			Navigation own = above;
			own.selectOnUp = library;
			button.navigation = own;
			above.selectOnDown = button;
			library.navigation = above;
			if (below != null)
			{
				Navigation next = below.navigation;
				next.selectOnUp = button;
				below.navigation = next;
			}
		}
		Text text = copy.GetComponentsInChildren<Text>(true).FirstOrDefault(t => t.name == "Text");
		if (text != null)
		{
			// 文字上挂的多语言组件会把字换回「讲经堂」
			foreach (MonoBehaviour behaviour in text.GetComponents<MonoBehaviour>())
			{
				if (!(behaviour is Graphic) && !(behaviour is BaseMeshEffect))
				{
					Destroy(behaviour);
				}
			}
			text.text = TextConverter.ForDisplay("鉴赏");
		}
	}

	private static void Open()
	{
		if (_instance == null)
		{
			return;
		}
		_instance._open = true;
		_instance._viewing = -1;
		_instance._page = 0;
		if (_instance._pictures != null)
		{
			_instance.Refresh();
		}
		else if (!_instance._scanning)
		{
			_instance._scanning = true;
			_instance.StartCoroutine(_instance.Scan());
		}
	}

	private void Close()
	{
		_open = false;
		_closedFrame = Time.frameCount;
		_viewing = -1;
		Trim(new HashSet<string>());
		CloseStage();
		GamepadCursor.Release();
		// 用手柄操作时点击会清掉 uGUI 的选中，选回来才能继续用十字键
		if (_titleButton != null)
		{
			_titleButton.Select();
		}
		if (_musicChanged)
		{
			_musicChanged = false;
			_playing = null;
			SoundManager.Instance.StopMusic();
			MusicData music = TitleManager.Instance != null ? Traverse.Create(TitleManager.Instance).Field("_musicData").GetValue<MusicData>() : null;
			if (music != null)
			{
				SoundManager.Instance.PlayMusic(music.Key);
			}
		}
	}

	/// <summary>读一遍全部剧本，找出每张图、每首曲子和它们前后的对白。</summary>
	private IEnumerator Scan()
	{
		yield return null;
		var pictures = new SortedDictionary<string, Picture>(StringComparer.Ordinal);
		List<Track> tracks = GalleryMusic.Tracks.Select(m => new Track { Key = m.Key, Title = GalleryMusic.Title(m.Traditional, m.Simplified, m.English) }).ToList();
		TextAsset[] assets = Resources.LoadAll<TextAsset>("story/chinesetraditional");
		for (int i = 0; i < assets.Length; i++)
		{
			if (assets[i] != null)
			{
				Read(assets[i].text, pictures);
				Resources.UnloadAsset(assets[i]);
			}
			if (i % 200 == 0)
			{
				yield return null;
			}
		}
		// 有的剧本写了图片编号，资源里却没有这张图（游戏里什么也不显示），不收录
		yield return Addressables.InitializeAsync();
		bool Exists(string key) => !Addressables.ResourceLocators.Any() || Addressables.ResourceLocators.Any(locator => locator.Locate("pic_" + key, null, out IList<IResourceLocation> found) && found.Count > 0);
		_pictures = pictures.Values.Where(p => Exists(p.Key)).ToList();
		LibraryItemCollection ends = LibrarySystem.Instance != null ? LibrarySystem.Instance.EndCg : null;
		if (ends != null)
		{
			_pictures.AddRange(ends.List.Where(item => item.Picture != null).Select(item => new Picture { Key = "end_" + item.name, Direct = item.Picture }));
		}
		_pictures.AddRange(ScenePaths().Select(path => new Picture { Key = path, Path = path }));
		foreach (Picture picture in _pictures)
		{
			picture.Kind = KindOf(picture);
		}
		_tracks = tracks;
		var wanted = new HashSet<string>(_pictures.SelectMany(p => p.Records));
		Task<HashSet<string>> played = Task.Run(() => PlayedRecords(wanted));
		while (!played.IsCompleted)
		{
			yield return null;
		}
		_played = played.Status == TaskStatus.RanToCompletion ? played.Result : new HashSet<string>();
		_scanning = false;
		FileLogger.Log($"[鉴赏] 剧本里找到 {_pictures.Count} 张图");
		Refresh();
	}

	/// <summary>各个存档里出现过哪些剧情记录。只读，在后台线程做；存档是二进制，直接在文字里找编号。</summary>
	private static HashSet<string> PlayedRecords(HashSet<string> wanted)
	{
		var played = new HashSet<string>();
		string[] files;
		try
		{
			files = Directory.GetFiles(Application.persistentDataPath, "*.dat", SearchOption.AllDirectories);
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[鉴赏] 找不到存档目录: " + e.Message);
			return played;
		}
		foreach (string file in files)
		{
			string name = Path.GetFileName(file);
			if ((!name.StartsWith("Save_", StringComparison.Ordinal) && !name.StartsWith("Legend_", StringComparison.Ordinal)) || name == "Save_universe.dat")
			{
				continue;
			}
			try
			{
				using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
				using (var reader = new StreamReader(stream, Encoding.GetEncoding(28591)))
				{
					foreach (Match word in Word.Matches(reader.ReadToEnd()))
					{
						if (wanted.Contains(word.Value))
						{
							played.Add(word.Value);
						}
					}
				}
			}
			catch (Exception e)
			{
				FileLogger.LogWarning("[鉴赏] 读存档失败，跳过: " + name + " " + e.Message);
			}
		}
		FileLogger.Log($"[鉴赏] 存档里找到 {played.Count} 条相关剧情记录");
		return played;
	}

	private static string KindOf(Picture picture)
	{
		string key = picture.Key;
		bool Starts(string[] heads) => heads.Any(head => key.StartsWith(head, StringComparison.Ordinal));
		if (picture.Path != null)
		{
			return "场景";
		}
		if (picture.Direct != null)
		{
			return "结局";
		}
		if (key.Contains("_work"))
		{
			return "工作";
		}
		if (key.Contains("_free"))
		{
			return "日常";
		}
		return Starts(PersonKeys) ? "人物" : Starts(FightKeys) ? "战斗" : Starts(DailyKeys) ? "日常" : "剧情";
	}

	private static void Read(string script, IDictionary<string, Picture> pictures)
	{
		string[] lines = script.Split('\n').Select(line => line.Trim()).ToArray();
		for (int i = 0; i < lines.Length; i++)
		{
			Match picture = PictureCall.Match(lines[i]);
			if (picture.Success)
			{
				if (!pictures.TryGetValue(picture.Groups[2].Value, out Picture entry))
				{
					entry = new Picture { Key = picture.Groups[2].Value, Back = BookPage };
					pictures[entry.Key] = entry;
				}
				if (picture.Groups[1].Success)
				{
					entry.Back = picture.Groups[3].Value == "1" ? Color.black : Color.white;
				}
				entry.Gate.UnionWith(Nearby(lines, i));
				entry.Records.UnionWith(RecordsAfter(lines, i));
			}
		}
	}

	/// <summary>同一支里紧接着的对白；后面没有就找前面的。读过这句就说明走到过这里，分岔之外的对白不算。</summary>
	private static List<string> Nearby(string[] lines, int at)
	{
		List<string> keys = Walk(lines, at, 1, "if ", "end");
		return keys.Count > 0 ? keys : Walk(lines, at, -1, "end", "if ");
	}

	/// <summary>同一支里后面写下的剧情记录：存档里有它，说明走过了这里。</summary>
	private static List<string> RecordsAfter(string[] lines, int at)
	{
		var keys = new List<string>();
		int depth = 0;
		for (int i = at + 1; i < lines.Length; i++)
		{
			string line = lines[i];
			if (line.StartsWith("if ", StringComparison.Ordinal))
			{
				depth++;
			}
			else if (line == "end")
			{
				if (depth == 0)
				{
					break;
				}
				depth--;
			}
			else if (depth == 0 && (line.StartsWith("elseif ", StringComparison.Ordinal) || line == "else"))
			{
				break;
			}
			Match record = AddStory.Match(line);
			if (record.Success)
			{
				keys.Add(record.Groups[1].Value);
			}
		}
		return keys;
	}

	private static List<string> Walk(string[] lines, int at, int step, string opens, string closes)
	{
		var keys = new List<string>();
		int depth = 0;
		for (int i = at + step; i >= 0 && i < lines.Length; i += step)
		{
			string line = lines[i];
			if (opens == "if " ? line.StartsWith(opens, StringComparison.Ordinal) : line == opens)
			{
				depth++;
			}
			else if (closes == "if " ? line.StartsWith(closes, StringComparison.Ordinal) : line == closes)
			{
				if (depth == 0)
				{
					break;
				}
				depth--;
			}
			else if (depth == 0 && (line.StartsWith("elseif ", StringComparison.Ordinal) || line == "else"))
			{
				break;
			}
			Match say = Say.Match(line);
			if (say.Success)
			{
				keys.Add(say.Groups[1].Value);
				if (depth == 0)
				{
					break;
				}
			}
		}
		return keys;
	}

	private void Refresh()
	{
		bool showLocked = ConfigManager.GalleryShowLocked.Value;
		ReadStorySystem read = ReadStorySystem.Instance;
		bool Heard(HashSet<string> gate)
		{
			try
			{
				return read != null && gate.Any(read.ExistKey);
			}
			catch (NullReferenceException)
			{
				return false; // 已读记录还没读进来
			}
		}
		LibraryItemCollection ends = LibrarySystem.Instance != null ? LibrarySystem.Instance.EndCg : null;
		foreach (Picture picture in _pictures)
		{
			picture.Unlocked = showLocked || picture.Path != null || (picture.Direct != null
				? ends?.List.Any(item => item.Picture == picture.Direct && item.Count > 0) == true
				: Known().Contains("pic:" + picture.Key) || Heard(picture.Gate) || picture.Records.Overlaps(_played));
		}
		_shown = Listed.Where(p => p.Unlocked).ToList();
	}

	private IEnumerable<Picture> Listed => _kind == AllKinds ? _pictures : _pictures.Where(p => p.Kind == _kind);

	private void ShowKind(string kind)
	{
		_kind = kind;
		_page = 0;
		_shown = Listed.Where(p => p.Unlocked).ToList();
		KeepPage();
	}

	private void Update()
	{
		if (!_open || StoryReviewPanel.PanelOwnsEscape)
		{
			return;
		}
		Gamepad pad = Gamepad.current;
		bool back = (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) || (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
			|| (pad != null && pad.buttonEast.wasPressedThisFrame);
		if (back)
		{
			if (StageBack())
			{
				return;
			}
			if (_viewing >= 0)
			{
				View(-1);
			}
			else
			{
				Close();
			}
			return;
		}
		// 手柄：和咖啡伴侣窗口一样用摇杆移动指针、A 点击，LB / RB 切换页
		if (pad != null)
		{
			GamepadCursor.Update(new Rect(0f, 0f, Screen.width, Screen.height));
			int step = _pictures != null && _viewing < 0 && !_hideUi && !_pickingBack ? GamepadCursor.ShoulderStep(pad) : 0;
			if (step != 0)
			{
				SwitchTab((Array.IndexOf(TabOrder, _tab) + step + TabOrder.Length) % TabOrder.Length);
			}
		}
		if (_viewing >= 0 && Keyboard.current != null)
		{
			if (Keyboard.current.leftArrowKey.wasPressedThisFrame)
			{
				Step(-1);
			}
			else if (Keyboard.current.rightArrowKey.wasPressedThisFrame)
			{
				Step(1);
			}
		}
	}

	private void SwitchTab(int index)
	{
		if (TabOrder[index] == StageTab)
		{
			OpenStage();
		}
		else
		{
			_tab = TabOrder[index];
		}
	}

	private void Step(int by)
	{
		if (_shown.Count > 0)
		{
			View((_viewing + by + _shown.Count) % _shown.Count);
		}
	}

	private void View(int index)
	{
		if (_viewing < 0 && index >= 0)
		{
			_hintUntil = Time.unscaledTime + HintSeconds;
		}
		_viewing = index;
		KeepPage();
	}

	private IEnumerable<Picture> PageItems => Listed.Skip(_page * Columns * Rows).Take(Columns * Rows);

	private int Pages => Mathf.Max(1, Mathf.CeilToInt(Listed.Count() / (float)(Columns * Rows)));

	/// <summary>只留这一页和正在看的图，别的放掉，翻多少页内存都不涨。</summary>
	private void KeepPage()
	{
		var keep = new HashSet<string>(PageItems.Select(p => p.Key));
		if (_viewing >= 0 && _viewing < _shown.Count)
		{
			keep.Add(_shown[_viewing].Key);
		}
		Trim(keep);
	}

	private void Trim(HashSet<string> keep)
	{
		foreach (string key in _loads.Keys.Where(k => !keep.Contains(k)).ToList())
		{
			Addressables.Release(_loads[key]);
			_loads.Remove(key);
		}
	}

	private Sprite SpriteOf(Picture picture, out bool failed)
	{
		failed = false;
		if (picture.Direct != null)
		{
			return picture.Direct;
		}
		if (!_loads.TryGetValue(picture.Key, out AsyncOperationHandle<Sprite> handle))
		{
			handle = Addressables.LoadAssetAsync<Sprite>(picture.Path ?? "pic_" + picture.Key);
			_loads[picture.Key] = handle;
		}
		if (!handle.IsDone)
		{
			return null;
		}
		failed = handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null;
		return failed ? null : handle.Result;
	}

	private float S(float value) => value * _scale;

	private void Styles()
	{
		float scale = PanelTheme.UiScale;
		if (_title != null && Mathf.Approximately(scale, _scale) && _dark == PanelTheme.Dark)
		{
			return;
		}
		_scale = scale;
		_dark = PanelTheme.Dark;
		_title = PanelTheme.Label(scale, 34, PanelTheme.Ink, wrap: false);
		_body = PanelTheme.Label(scale, 22, PanelTheme.Ink, wrap: false);
		_muted = PanelTheme.Label(scale, 20, PanelTheme.InkMuted, wrap: false);
		_center = new GUIStyle(_muted) { alignment = TextAnchor.MiddleCenter };
		_button = PanelTheme.Button(scale, 22);
		_primary = PanelTheme.Primary(scale, 22);
		_card = PanelTheme.Button(scale, 18);
		_track = PanelTheme.Button(scale, 18);
		_trackOn = PanelTheme.Primary(scale, 18);
		_hint = PanelTheme.Label(scale, 20, PanelTheme.ToastText, wrap: false);
		_hint.alignment = TextAnchor.MiddleCenter;
		_field = PanelTheme.Field(scale, 18);
	}

	private static void Fill(Rect rect, Color color)
	{
		Color old = GUI.color;
		GUI.color = color;
		GUI.DrawTexture(rect, Texture2D.whiteTexture);
		GUI.color = old;
	}

	private static void DrawFit(Rect box, Sprite sprite, Color? back)
	{
		Rect part = sprite.textureRect;
		Texture2D texture = sprite.texture;
		float fit = Mathf.Min(box.width / part.width, box.height / part.height);
		var rect = new Rect(box.x + (box.width - part.width * fit) / 2f, box.y + (box.height - part.height * fit) / 2f, part.width * fit, part.height * fit);
		if (back != null)
		{
			Fill(rect, back.Value);
		}
		GUI.DrawTextureWithTexCoords(rect, texture, new Rect(part.x / texture.width, part.y / texture.height, part.width / texture.width, part.height / texture.height));
	}

	private void OnGUI()
	{
		if (!_open || PanelTheme.UiScale <= 0f)
		{
			return;
		}
		GUI.depth = 1;
		Styles();
		var screen = new Rect(0f, 0f, Screen.width, Screen.height);
		InputGuard.Report("Gallery", screen);
		if (_viewing >= 0 && _viewing < _shown.Count)
		{
			Fill(screen, _shown[_viewing].Back ?? Color.black);
			DrawViewer(screen);
			return;
		}
		if (_tab == StageTab && _pictures != null)
		{
			DrawStage(screen);
			return;
		}
		Fill(screen, PanelTheme.Paper);
		if (_pictures == null)
		{
			GUI.Label(screen, TextConverter.ForDisplay("正在读取剧本"), _center);
			return;
		}
		DrawHeader();
		var body = new Rect(S(40f), S(110f), Screen.width - S(80f), Screen.height - S(130f));
		if (_tab == MusicTab)
		{
			DrawMusic(body);
		}
		else
		{
			for (int i = 0; i < Kinds.Length; i++)
			{
				if (GUI.Button(new Rect(body.x + i * S(130f), body.y - S(8f), S(120f), S(42f)), TextConverter.ForDisplay(Kinds[i]), _kind == Kinds[i] ? _trackOn : _track))
				{
					ShowKind(Kinds[i]);
				}
			}
			DrawPictures(new Rect(body.x, body.y + S(46f), body.width, body.height - S(46f)));
		}
	}

	private void DrawHeader()
	{
		GUI.Label(new Rect(S(40f), S(26f), S(120f), S(52f)), TextConverter.ForDisplay("鉴赏"), _title);
		if (GUI.Button(new Rect(S(170f), S(30f), S(120f), S(46f)), "CG", _tab == PictureTab ? _primary : _button))
		{
			_tab = PictureTab;
		}
		if (GUI.Button(new Rect(S(300f), S(30f), S(120f), S(46f)), TextConverter.ForDisplay("立绘"), _tab == StageTab ? _primary : _button))
		{
			OpenStage();
		}
		if (GUI.Button(new Rect(S(430f), S(30f), S(120f), S(46f)), TextConverter.ForDisplay("音乐"), _tab == MusicTab ? _primary : _button))
		{
			_tab = MusicTab;
		}
		if (_tab == PictureTab)
		{
			GUI.Label(new Rect(S(570f), S(38f), S(360f), S(36f)), string.Format(TextConverter.ForDisplay("已解锁 {0} / {1}"), _shown.Count, Listed.Count()), _muted);
			bool showLocked = ConfigManager.GalleryShowLocked.Value;
			if (GUI.Button(new Rect(Screen.width - S(420f), S(30f), S(250f), S(46f)), TextConverter.ForDisplay("显示未解锁内容"), showLocked ? _primary : _button))
			{
				ConfigManager.GalleryShowLocked.Value = !showLocked;
				Refresh();
				KeepPage();
			}
		}
		if (GUI.Button(new Rect(Screen.width - S(160f), S(30f), S(120f), S(46f)), TextConverter.ForDisplay("返回"), _button))
		{
			Close();
		}
	}

	private void DrawPictures(Rect body)
	{
		float gap = S(16f);
		float footer = S(60f);
		float width = (body.width - gap * (Columns - 1)) / Columns;
		float height = (body.height - footer - gap * (Rows - 1)) / Rows;
		int index = 0;
		foreach (Picture picture in PageItems)
		{
			var card = new Rect(body.x + index % Columns * (width + gap), body.y + index / Columns * (height + gap), width, height);
			index++;
			GUI.enabled = picture.Unlocked;
			if (GUI.Button(card, GUIContent.none, _card))
			{
				View(_shown.IndexOf(picture));
			}
			GUI.enabled = true;
			if (!picture.Unlocked)
			{
				GUI.Label(card, TextConverter.ForDisplay("未解锁"), _center);
				continue;
			}
			Sprite sprite = SpriteOf(picture, out bool failed);
			if (sprite != null)
			{
				DrawFit(new Rect(card.x + S(4f), card.y + S(4f), card.width - S(8f), card.height - S(8f)), sprite, picture.Back);
			}
			else if (failed)
			{
				GUI.Label(card, TextConverter.ForDisplay("图片无法显示"), _center);
			}
		}
		float y = body.yMax - S(46f);
		float middle = body.x + body.width / 2f;
		GUI.enabled = _page > 0;
		if (GUI.Button(new Rect(middle - S(240f), y, S(140f), S(46f)), TextConverter.ForDisplay("上一页"), _button))
		{
			_page--;
			KeepPage();
		}
		GUI.enabled = _page < Pages - 1;
		if (GUI.Button(new Rect(middle + S(100f), y, S(140f), S(46f)), TextConverter.ForDisplay("下一页"), _button))
		{
			_page++;
			KeepPage();
		}
		GUI.enabled = true;
		GUI.Label(new Rect(middle - S(90f), y, S(180f), S(46f)), (_page + 1) + " / " + Pages, _center);
	}

	private void DrawViewer(Rect screen)
	{
		Sprite sprite = SpriteOf(_shown[_viewing], out bool failed);
		if (sprite != null)
		{
			DrawFit(screen, sprite, null);
		}
		else if (failed)
		{
			GUI.Label(screen, TextConverter.ForDisplay("图片无法显示"), _center);
		}
		if (GUI.Button(screen, GUIContent.none, GUIStyle.none))
		{
			Step(1);
		}
		// 进来时提示几秒就消失，不挡截图
		if (Time.unscaledTime < _hintUntil)
		{
			var hint = new Rect(0f, Screen.height - S(48f), Screen.width, S(40f));
			Fill(hint, new Color(0f, 0f, 0f, 0.6f));
			GUI.Label(hint, string.Format(TextConverter.ForDisplay("{0} / {1}　左键或方向键切换，右键或 Esc 返回"), _viewing + 1, _shown.Count), _hint);
		}
	}

	private void DrawMusic(Rect body)
	{
		float gap = S(12f);
		float width = (body.width - gap * (TrackColumns - 1)) / TrackColumns;
		float height = S(48f);
		for (int i = 0; i < _tracks.Count; i++)
		{
			Track track = _tracks[i];
			int rows = Mathf.CeilToInt(_tracks.Count / (float)TrackColumns);
			var cell = new Rect(body.x + i / rows * (width + gap), body.y + i % rows * (height + gap), width, height);
			if (GUI.Button(cell, track.Title, track == _playing ? _trackOn : _track))
			{
				SoundManager.Instance.StopMusic();
				SoundManager.Instance.ResetMusic();
				SoundManager.Instance.PlayMusic(track.Key);
				_playing = track;
				_musicChanged = true;
			}
		}
		float y = body.yMax - S(46f);
		GUI.Label(new Rect(body.x, y, body.width - S(160f), S(46f)), _playing != null ? string.Format(TextConverter.ForDisplay("正在播放：{0}"), _playing.Title) : TextConverter.ForDisplay("点曲名播放"), _body);
		GUI.enabled = _playing != null;
		if (GUI.Button(new Rect(body.xMax - S(140f), y, S(140f), S(46f)), TextConverter.ForDisplay("停止"), _button))
		{
			SoundManager.Instance.StopMusic();
			_playing = null;
			_musicChanged = true;
		}
		GUI.enabled = true;
	}
}

[HarmonyPatch]
public static class GalleryPatch
{
	[HarmonyPrefix]
	[HarmonyPatch(typeof(StoryMainUI), nameof(StoryMainUI.ShowPicture))]
	public static void ShowPicture_Prefix(string key) => Gallery.Seen("pic:" + key);

	[HarmonyPrefix]
	[HarmonyPatch(typeof(StoryMainUI), nameof(StoryMainUI.ShowBigPicture))]
	public static void ShowBigPicture_Prefix(string key) => Gallery.Seen("pic:" + key);

	[HarmonyPostfix]
	[HarmonyPatch(typeof(TitleManager), "Awake")]
	public static void Awake_Postfix(TitleManager __instance)
	{
		try
		{
			Gallery.AddButton(__instance);
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[鉴赏] 添加标题按钮失败: " + e.Message);
		}
	}
}
