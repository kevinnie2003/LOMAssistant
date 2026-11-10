using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace LOMAssistant.Features.Ai;

/// <summary>
/// 玩家自己放的人设和图。都在 BepInEx\config\LOMAssistant_AI\personas\ 下，一个子文件夹一位：
/// 文件夹名和内置人设（或「攻略助手」）同名的，只换图，人设文字仍用内置的；
/// 别的名字、里面有 persona.txt 的，是自建人设：名字就是文件夹名，人设文字是那个文件，没有游戏里的对白、人物志、好感可用。
/// 图按文件名认（png、jpg）：default 是平时的，head 是头像和缩小时用的，其余的文件名是表情名。整张照原样画，不裁。
/// 每隔几秒看一眼文件夹有没有变，改了不用重启。
/// </summary>
internal static class AiCustom
{
	internal const string IdPrefix = "custom:";
	internal const string DefaultMood = "default", HeadName = "head", TextName = "persona.txt";

	private const int MaxText = 8000, MaxMoods = 24;
	private const long MaxImageBytes = 20L * 1024 * 1024;
	private static readonly char[] Digits = "0123456789".ToCharArray();
	// 表情名要写进「[表情: 名字]」里，带这些字符的认不出来，不当表情用（图还是能当 default、head 以外的备用图）
	private static readonly char[] NotInMood = " \t[]【】()（）,，:：、".ToCharArray();

	internal static string Folder => Path.Combine(AiSessions.Folder, "personas");

	private sealed class Entry
	{
		public string Dir = "", Stamp = "";
		/// <summary>换的是哪位内置人设的图（攻略助手是空字符串）；自建人设为 null。</summary>
		public string BuiltIn;
		public AiPersona Persona;
		/// <summary>表情名（小写）→ 文件。</summary>
		public readonly SortedDictionary<string, string> Files = new SortedDictionary<string, string>(StringComparer.Ordinal);
		public readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();
	}

	private static List<Entry> _entries = new List<Entry>();
	private static List<AiPersona> _personas = new List<AiPersona>();
	private static float _scannedAt = -100f;

	/// <summary>玩家自建的人设，按文件夹名排。</summary>
	public static List<AiPersona> Personas
	{
		get
		{
			Refresh();
			return _personas;
		}
	}

	public static AiPersona Find(string id)
	{
		Refresh();
		return _personas.FirstOrDefault(p => p.Id == id);
	}

	/// <summary>这位（null = 攻略助手）有没有玩家放的图。</summary>
	public static bool Has(AiPersona persona)
	{
		Entry entry = EntryFor(persona);
		return entry != null && entry.Files.Count > 0;
	}

	/// <summary>
	/// 这位这个表情该用的图；没有玩家放的图返回 null。找图：head（只在要头像时）→ 这个表情 → 名字只差末尾数字的 → default
	/// → 内置人设平时那个表情 → 文件夹里第一张。
	/// </summary>
	public static Texture2D Image(AiPersona persona, string mood, bool head)
	{
		Entry entry = EntryFor(persona);
		// 一张读不了就从表里去掉、换下一张候选
		for (int tries = 0; entry != null && entry.Files.Count > 0 && tries < 4; tries++)
		{
			Texture2D texture = Load(entry, Pick(entry, persona, mood, head));
			if (texture != null)
			{
				return texture;
			}
		}
		return null;
	}

	/// <summary>人设按钮上的小头像：取头像那张图上的一块正方形（竖图取最上面，横图取中间）。没有图返回 false。</summary>
	public static bool Icon(AiPersona persona, out Texture texture, out Rect uv)
	{
		uv = new Rect(0f, 0f, 1f, 1f);
		Texture2D image = Image(persona, "", true);
		texture = image;
		if (image == null)
		{
			return false;
		}
		float w = image.width, h = image.height;
		uv = h > w ? new Rect(0f, 1f - w / h, 1f, w / h) : new Rect((1f - h / w) / 2f, 0f, h / w, 1f);
		return true;
	}

	private static void Refresh()
	{
		if (Time.realtimeSinceStartup - _scannedAt > 3f)
		{
			Scan();
		}
	}

	private static Entry EntryFor(AiPersona persona)
	{
		Refresh();
		if (persona != null && persona.Custom)
		{
			return _entries.FirstOrDefault(e => e.Persona != null && e.Persona.Id == persona.Id);
		}
		string id = persona?.Id ?? "";
		return _entries.FirstOrDefault(e => e.BuiltIn == id);
	}

	private static string Pick(Entry entry, AiPersona persona, string mood, bool head)
	{
		var files = entry.Files;
		if (head && files.ContainsKey(HeadName))
		{
			return HeadName;
		}
		mood = (mood ?? "").ToLowerInvariant();
		if (mood.Length > 0 && mood != HeadName)
		{
			if (files.ContainsKey(mood))
			{
				return mood;
			}
			string kind = mood.TrimEnd(Digits);
			string same = files.Keys.FirstOrDefault(k => k != HeadName && k.TrimEnd(Digits) == kind);
			if (same != null)
			{
				return same;
			}
		}
		if (files.ContainsKey(DefaultMood))
		{
			return DefaultMood;
		}
		string usual = persona != null && !persona.Custom ? persona.Moods[0] : "";
		if (usual.Length > 0 && files.ContainsKey(usual))
		{
			return usual;
		}
		return files.Keys.FirstOrDefault(k => k != HeadName) ?? files.Keys.First();
	}

	private static Texture2D Load(Entry entry, string name)
	{
		if (entry.Textures.TryGetValue(name, out Texture2D cached) && cached != null)
		{
			return cached;
		}
		string file = entry.Files[name];
		Texture2D texture = null;
		try
		{
			texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
			if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(file), true))
			{
				throw new InvalidDataException("不是能读的图片");
			}
			texture.wrapMode = TextureWrapMode.Clamp;
			texture.filterMode = FilterMode.Trilinear;
			// 不跟着场景切换、清理没用的资源时被收走
			texture.hideFlags = HideFlags.HideAndDontSave;
			entry.Textures[name] = texture;
			return texture;
		}
		catch (Exception ex)
		{
			if (texture != null)
			{
				UnityEngine.Object.Destroy(texture);
			}
			// 这个文件不再试（文件变了会整个重新扫），免得每帧都读一遍
			entry.Files.Remove(name);
			FileLogger.LogWarning("[AI] 读不了自定义人设的图 " + Path.GetFileName(file) + ": " + ex.Message);
			return null;
		}
	}

	private static void Release(Entry entry)
	{
		foreach (Texture2D texture in entry.Textures.Values)
		{
			if (texture != null)
			{
				UnityEngine.Object.Destroy(texture);
			}
		}
		entry.Textures.Clear();
	}

	// ---------------- 扫文件夹 ----------------

	private static void Scan()
	{
		_scannedAt = Time.realtimeSinceStartup;
		var found = new List<Entry>();
		try
		{
			if (Directory.Exists(Folder))
			{
				var builtIn = new List<(string Id, List<string> Names)> { ("", FolderNames(null)) };
				builtIn.AddRange(AiPersona.All.Select(p => (p.Id, FolderNames(p))));
				foreach (string dir in Directory.GetDirectories(Folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
				{
					Entry entry = Read(dir, builtIn);
					if (entry != null)
					{
						found.Add(entry);
					}
				}
			}
		}
		catch (Exception ex)
		{
			// 这次没扫成（文件夹正被占用之类）：照旧用上次的
			FileLogger.LogWarning("[AI] 读自定义人设文件夹失败: " + ex.Message);
			return;
		}
		foreach (Entry old in _entries.Where(e => !found.Contains(e)))
		{
			Release(old);
		}
		_entries = found;
		_personas = found.Where(e => e.Persona != null).Select(e => e.Persona).ToList();
	}

	private static Entry Read(string dir, List<(string Id, List<string> Names)> builtIn)
	{
		string name = Path.GetFileName(dir);
		int match = builtIn.FindIndex(b => b.Names.Contains(name, StringComparer.OrdinalIgnoreCase));
		string textFile = match < 0 ? Directory.GetFiles(dir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), TextName, StringComparison.OrdinalIgnoreCase)) : null;
		if (match < 0 && textFile == null)
		{
			return null;
		}
		var entry = new Entry { Dir = dir, BuiltIn = match >= 0 ? builtIn[match].Id : null };
		var stamp = new StringBuilder(name);
		foreach (string file in Directory.GetFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
		{
			string ext = Path.GetExtension(file).ToLowerInvariant();
			bool image = ext == ".png" || ext == ".jpg" || ext == ".jpeg";
			if (!image && file != textFile)
			{
				continue;
			}
			var info = new FileInfo(file);
			stamp.Append('|').Append(info.Name).Append(info.LastWriteTimeUtc.Ticks).Append(':').Append(info.Length);
			string mood = Path.GetFileNameWithoutExtension(file).Trim().ToLowerInvariant();
			if (image && info.Length <= MaxImageBytes && mood.Length > 0 && !entry.Files.ContainsKey(mood))
			{
				entry.Files[mood] = file;
			}
		}
		entry.Stamp = stamp.ToString();
		// 文件没动过：接着用上次的（图已经载入了）
		Entry same = _entries.FirstOrDefault(e => e.Dir == dir && e.Stamp == entry.Stamp);
		if (same != null)
		{
			return same;
		}
		if (textFile != null)
		{
			string text = ReadText(textFile).Trim();
			if (text.Length == 0)
			{
				return null;
			}
			var moods = new List<string> { DefaultMood };
			moods.AddRange(entry.Files.Keys.Where(k => k != DefaultMood && k != HeadName && k.IndexOfAny(NotInMood) < 0).Take(MaxMoods));
			entry.Persona = new AiPersona
			{
				Id = IdPrefix + name, Name = name, Custom = true, Stamp = entry.Stamp,
				Traits = text.Length > MaxText ? text.Substring(0, MaxText) : text, Moods = moods.ToArray(),
			};
		}
		return entry;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern int MultiByteToWideChar(uint codePage, uint flags, byte[] bytes, int byteCount, [Out] char[] chars, int charCount);

	// 人设文字：按 UTF-8 读；不是 UTF-8 的（老版本记事本默认存成系统编码，中文系统上是 GBK）按系统编码读
	private static string ReadText(string file)
	{
		byte[] bytes = File.ReadAllBytes(file);
		if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
		{
			return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
		}
		if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
		{
			return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
		}
		if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
		{
			return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
		}
		try
		{
			return new UTF8Encoding(false, true).GetString(bytes);
		}
		catch (ArgumentException)
		{
		}
		try
		{
			int count = MultiByteToWideChar(0, 0, bytes, bytes.Length, null, 0);
			if (count > 0)
			{
				var chars = new char[count];
				MultiByteToWideChar(0, 0, bytes, bytes.Length, chars, count);
				return new string(chars);
			}
		}
		catch (Exception ex)
		{
			FileLogger.Log("[AI] 按系统编码读人设文字失败: " + ex.Message);
		}
		return Encoding.UTF8.GetString(bytes);
	}

	// ---------------- 文件夹名、说明文件 ----------------

	/// <summary>
	/// 内置人设（null = 攻略助手）的文件夹可以叫什么：各语言里的名字和角色编号都认，换了界面语言不用改名。
	/// 第一个是现在界面语言里的名字，建文件夹时用它。
	/// </summary>
	private static List<string> FolderNames(AiPersona persona)
	{
		string name = persona != null ? persona.Name : "攻略助手";
		var names = new[]
		{
			TextConverter.ForDisplay(name), name, TextConverter.ToTraditional(name), UiText.Translate(name, UiText.Korean),
			UiText.Translate(name, UiText.Japanese), UiText.Translate(name, UiText.English), persona != null ? persona.Id : "assistant",
		};
		return names.Select(Clean).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	private static string Clean(string name)
	{
		char[] bad = Path.GetInvalidFileNameChars();
		return new string((name ?? "").Where(c => Array.IndexOf(bad, c) < 0).ToArray()).Trim().TrimEnd('.');
	}

	/// <summary>
	/// 玩家点「打开文件夹」：建好文件夹，给攻略助手和列出来的内置人设（见过的那几位）各建一个空的子文件夹，写一份说明。
	/// 返回文件夹路径。
	/// </summary>
	public static string Prepare(IEnumerable<AiPersona> shown)
	{
		Directory.CreateDirectory(Folder);
		var existing = Directory.GetDirectories(Folder).Select(Path.GetFileName).ToList();
		// 见过的几位，加上以前已经建过文件夹的：在标题画面打开时算不出见过谁，不能因此漏掉已有的文件夹
		var wanted = new HashSet<AiPersona>(shown.Where(p => p != null && !p.Custom));
		var listed = new List<AiPersona> { null };
		listed.AddRange(AiPersona.All.Where(p => wanted.Contains(p) || FolderNames(p).Any(n => existing.Contains(n, StringComparer.OrdinalIgnoreCase))));
		var rows = new List<(string Folder, AiPersona Persona)>();
		foreach (AiPersona p in listed)
		{
			List<string> names = FolderNames(p);
			string have = existing.FirstOrDefault(e => names.Contains(e, StringComparer.OrdinalIgnoreCase));
			if (have == null)
			{
				have = names[0];
				Directory.CreateDirectory(Path.Combine(Folder, have));
			}
			rows.Add((have, p));
			// 内置人设的文件夹里放一份 Mod 写的人设文字，给自建人设的玩家照着写；每次打开都重写，跟着 Mod 更新
			if (p != null)
			{
				File.WriteAllText(Path.Combine(Folder, have, ReferenceName), Reference(p).Replace("\n", "\r\n"), new UTF8Encoding(true));
			}
		}
		string readme = L("说明.txt", "README.txt", "README.txt", "README.txt", "說明.txt");
		// 带 BOM：系统编码不是 UTF-8 的电脑上，记事本靠它认出来
		File.WriteAllText(Path.Combine(Folder, readme), Readme(rows).Replace("\n", "\r\n"), new UTF8Encoding(true));
		_scannedAt = -100f;
		return Folder;
	}

	private static string L(string zh, string en, string ko, string ja, string hant) => AiPrompts.L(zh, en, ko, ja, hant);

	/// <summary>内置人设文件夹里的参考文本叫这个名字。扫文件夹时只认图和 persona.txt，它不会被读进去。</summary>
	internal const string ReferenceName = "reference.txt";

	// 参考文本：开头几句说明按界面语言写；下面是 Mod 给这位写的称呼和性子，原样给出（只有简体中文这一份）
	private static string Reference(AiPersona persona)
	{
		string note = L(
			"这是 Mod 给这位内置人设写的人设文字，只供参照，改这个文件不起作用。\n"
			+ "对话时还会加上游戏里的人物志、玩家读过的她的对白、这一局和她有关的经历，那些取自游戏，不在这里。\n"
			+ "自建人设时，在自己的子文件夹里新建 persona.txt，照这样写这个人是谁、性格、说话的方式、怎么称呼赵活，用哪种语言写都可以。",
			"This is the persona text the mod wrote for this built-in persona. It is for reference only; editing this file changes nothing.\n"
			+ "In a conversation the mod also adds the character's entry in the game's records, lines of theirs the player has read and what happened with them in this playthrough. Those come from the game and are not shown here.\n"
			+ "For your own persona, create persona.txt in your own subfolder and describe who the person is, their personality, how they talk and what they call Zhao Huo. Write it in any language. The text below is in Simplified Chinese, the only version the mod has.",
			"이 파일은 모드가 이 기본 인물에게 써 둔 인물 설정입니다. 참고용이며, 이 파일을 고쳐도 반영되지 않습니다.\n"
			+ "대화할 때는 게임의 인물지, 플레이어가 읽은 이 인물의 대사, 이번 회차에서 이 인물과 관련해 겪은 일도 함께 넣습니다. 그 내용은 게임에서 가져오며 여기에는 없습니다.\n"
			+ "인물을 직접 만들 때는 자신의 하위 폴더에 persona.txt를 새로 만들고, 이 인물이 누구인지, 성격, 말투, 조활을 어떻게 부르는지를 적으세요. 어떤 언어로 써도 됩니다. 아래 내용은 중국어 간체이며, 모드에는 이 한 가지만 있습니다.",
			"このファイルは、Mod がこの内蔵キャラクターのために書いたキャラクター設定です。参考用で、このファイルを書き換えても反映されません。\n"
			+ "会話のときは、ゲームの人物誌、プレイヤーが読んだこの人の台詞、今回のプレイでこの人と関わった出来事も加えます。それらはゲームから取るもので、ここにはありません。\n"
			+ "自分でキャラクターを作るときは、自分のサブフォルダに persona.txt を新しく作り、この人が誰か、性格、話し方、趙活の呼び方を書いてください。どの言語で書いてもかまいません。下の文章は簡体字中国語で、Mod にはこの一種類しかありません。",
			"這是 Mod 幫這位內建人設寫的人設文字，只供參照，改這個檔案不會生效。\n"
			+ "對話時還會加上遊戲裡的人物誌、玩家讀過的她的對白、這一局和她有關的經歷，那些取自遊戲，不在這裡。\n"
			+ "自建人設時，在自己的子資料夾裡新增 persona.txt，照這樣寫這個人是誰、個性、說話的方式、怎麼稱呼趙活，用哪種語言寫都可以。下面的文字是簡體中文，Mod 只有這一份。");
		var sb = new StringBuilder(note).Append("\n\n----------\n\n");
		if (persona.Address.Length > 0)
		{
			sb.Append(persona.Address).Append("\n\n");
		}
		return sb.Append(persona.Traits).Append('\n').ToString();
	}

	// 说明文件：按界面语言各写一份，末尾列出内置人设的文件夹名和各自能用的表情名
	private static string Readme(List<(string Folder, AiPersona Persona)> rows)
	{
		var sb = new StringBuilder(L(ReadmeZh, ReadmeEn, ReadmeKo, ReadmeJa, ReadmeHant));
		string language = TextConverter.DisplayLanguage;
		bool chinese = language != UiText.English && language != UiText.Korean && language != UiText.Japanese;
		foreach (var (folder, persona) in rows)
		{
			sb.Append(folder).Append(L("：", ": ", ": ", "：", "："));
			if (persona == null)
			{
				sb.Append(L("没有表情，只用 default.png 和 head.png", "no expressions; only default.png and head.png are used",
					"표정 없음. default.png와 head.png만 씁니다", "表情なし。default.png と head.png だけを使います", "沒有表情，只用 default.png 和 head.png"));
			}
			else
			{
				sb.Append(string.Join(L("、", ", ", ", ", "、", "、"), persona.Moods.Select(m =>
				{
					string meaning = chinese ? AiPersona.MoodMeaningOf(m) : "";
					return meaning.Length > 0 ? m + "（" + TextConverter.ForDisplay(meaning) + "）" : m;
				}).ToArray()));
			}
			sb.Append('\n');
		}
		return sb.ToString();
	}

	private const string ReadmeZh =
		"咖啡伴侣 · AI 陪玩的自定义人设和图片\n\n"
		+ "这个文件夹里，一个子文件夹对应一位人设。改完几秒后生效，不用重启游戏。\n\n"
		+ "【给内置人设换图】\n"
		+ "子文件夹的名字和内置人设相同（文末列出了已经建好的），放进去的图会替换 AI 陪玩页的立绘和游戏画面上的形象。人设文字仍用内置的。\n"
		+ "攻略助手只换游戏画面上的形象。\n\n"
		+ "【自建人设】\n"
		+ "1. 新建一个子文件夹，文件夹名就是这位的名字。\n"
		+ "2. 在里面放一个 persona.txt，写这个人是谁、性格、说话的方式、怎么称呼赵活。存成 UTF-8，最多读前 8000 字。写法可以参照内置人设文件夹里的 reference.txt。\n"
		+ "3. 图可放可不放，没有图时显示名字的第一个字。\n"
		+ "放好后，这位会出现在 AI 陪玩页上方的人设一排里，也能选作主动陪玩的人选。\n\n"
		+ "【图怎么放】\n"
		+ "- 支持 png、jpg，建议用透明底的 png。整张图照原样显示，不裁剪。单张不超过 20 MB。\n"
		+ "- default.png：平时显示的图。只放这一张也可以。\n"
		+ "- head.png：头像，用在人设按钮上和游戏画面上缩小的时候。没有就用 default.png。\n"
		+ "- 其余的图是表情，文件名就是表情名。\n"
		+ "  内置人设：用文末列出的表情名。某个表情没有图时，用名字只差末尾数字的那张，再没有就用 default.png。\n"
		+ "  自建人设：表情名自己取，不要带空格、括号、逗号、冒号。AI 会按说话的内容从这些表情里选。\n\n"
		+ "【内置人设的文件夹名和表情名】\n";

	private const string ReadmeHant =
		"咖啡伴侶 · AI 陪玩的自訂人設和圖片\n\n"
		+ "這個資料夾裡，一個子資料夾對應一位人設。改完幾秒後生效，不用重新啟動遊戲。\n\n"
		+ "【幫內建人設換圖】\n"
		+ "子資料夾的名稱和內建人設相同（文末列出了已經建好的），放進去的圖會取代 AI 陪玩頁的立繪和遊戲畫面上的形象。人設文字仍用內建的。\n"
		+ "攻略助手只換遊戲畫面上的形象。\n\n"
		+ "【自建人設】\n"
		+ "1. 新增一個子資料夾，資料夾名稱就是這位的名字。\n"
		+ "2. 在裡面放一個 persona.txt，寫這個人是誰、個性、說話的方式、怎麼稱呼趙活。存成 UTF-8，最多讀前 8000 字。寫法可以參照內建人設資料夾裡的 reference.txt。\n"
		+ "3. 圖可放可不放，沒有圖時顯示名字的第一個字。\n"
		+ "放好後，這位會出現在 AI 陪玩頁上方的人設那一排，也能選作主動陪玩的人選。\n\n"
		+ "【圖怎麼放】\n"
		+ "- 支援 png、jpg，建議用透明背景的 png。整張圖照原樣顯示，不裁切。單張不超過 20 MB。\n"
		+ "- default.png：平時顯示的圖。只放這一張也可以。\n"
		+ "- head.png：頭像，用在人設按鈕上和遊戲畫面上縮小的時候。沒有就用 default.png。\n"
		+ "- 其餘的圖是表情，檔名就是表情名稱。\n"
		+ "  內建人設：用文末列出的表情名稱。某個表情沒有圖時，用名稱只差結尾數字的那張，再沒有就用 default.png。\n"
		+ "  自建人設：表情名稱自己取，不要帶空格、括號、逗號、冒號。AI 會依說話的內容從這些表情裡選。\n\n"
		+ "【內建人設的資料夾名稱和表情名稱】\n";

	private const string ReadmeEn =
		"Coffee Companion · Custom personas and images for the AI Companion\n\n"
		+ "Each subfolder here is one persona. Changes take effect a few seconds after you save; no restart is needed.\n\n"
		+ "[Replacing a built-in persona's images]\n"
		+ "Name the subfolder after a built-in persona (the ones already created are listed at the end). Images in it replace the portrait on the AI Companion tab and the figure on the game screen. The persona text stays built-in.\n"
		+ "For the Guide, only the figure on the game screen is replaced.\n\n"
		+ "[Adding your own persona]\n"
		+ "1. Create a subfolder; its name is the persona's name.\n"
		+ "2. Put a persona.txt in it describing who this is, their personality, how they talk and what they call Zhao Huo. Save it as UTF-8; only the first 8000 characters are read. The reference.txt in each built-in persona's folder shows how the built-in ones are written.\n"
		+ "3. Images are optional. Without any, the first character of the name is shown.\n"
		+ "The persona then appears in the persona row at the top of the AI Companion tab and can be chosen as the proactive companion.\n\n"
		+ "[Images]\n"
		+ "- png and jpg are supported; png with a transparent background is recommended. The whole image is shown as is, uncropped. 20 MB at most per image.\n"
		+ "- default.png: the usual image. This one alone is enough.\n"
		+ "- head.png: the avatar, used on the persona button and when the figure on the game screen is shrunk. Falls back to default.png.\n"
		+ "- Any other image is an expression, named by its file name.\n"
		+ "  Built-in personas: use the expression names listed at the end. If an expression has no image, the one whose name differs only in the trailing digit is used, then default.png.\n"
		+ "  Your own personas: name the expressions as you like, without spaces, brackets, commas or colons. The AI picks one of them to match what it says.\n\n"
		+ "[Folder and expression names of the built-in personas]\n";

	private const string ReadmeKo =
		"커피 동반자 · AI 동행의 사용자 지정 인물과 이미지\n\n"
		+ "이 폴더에서는 하위 폴더 하나가 인물 한 명입니다. 저장하고 몇 초 뒤에 반영되며, 게임을 다시 시작할 필요가 없습니다.\n\n"
		+ "[기본 인물의 이미지 바꾸기]\n"
		+ "하위 폴더 이름을 기본 인물의 이름과 같게 하면(이미 만들어 둔 폴더는 맨 아래에 적혀 있습니다), 그 안의 이미지가 AI 동행 탭의 스탠딩 일러스트와 게임 화면의 모습을 대신합니다. 인물 설정은 기본 것을 그대로 씁니다.\n"
		+ "공략 도우미는 게임 화면의 모습만 바뀝니다.\n\n"
		+ "[인물 직접 만들기]\n"
		+ "1. 하위 폴더를 새로 만듭니다. 폴더 이름이 곧 인물의 이름입니다.\n"
		+ "2. 그 안에 persona.txt를 넣고, 이 인물이 누구인지, 성격, 말투, 조활을 어떻게 부르는지를 적습니다. UTF-8로 저장하세요. 앞에서부터 8000자까지만 읽습니다. 쓰는 방식은 기본 인물 폴더에 있는 reference.txt를 참고하세요.\n"
		+ "3. 이미지는 없어도 됩니다. 없으면 이름의 첫 글자를 표시합니다.\n"
		+ "넣고 나면 AI 동행 탭 위쪽의 인물 줄에 나타나고, 동행의 한마디의 동행 인물로도 고를 수 있습니다.\n\n"
		+ "[이미지 넣는 법]\n"
		+ "- png, jpg를 지원하며 배경이 투명한 png를 권장합니다. 이미지는 자르지 않고 그대로 표시합니다. 한 장에 20 MB까지입니다.\n"
		+ "- default.png: 평소에 표시하는 이미지입니다. 이 한 장만 있어도 됩니다.\n"
		+ "- head.png: 얼굴 아이콘입니다. 인물 버튼과, 게임 화면에서 작게 줄였을 때 씁니다. 없으면 default.png를 씁니다.\n"
		+ "- 그 밖의 이미지는 표정이며, 파일 이름이 표정 이름입니다.\n"
		+ "  기본 인물: 맨 아래에 적힌 표정 이름을 쓰세요. 어떤 표정의 이미지가 없으면 이름이 끝의 숫자만 다른 이미지를 쓰고, 그것도 없으면 default.png를 씁니다.\n"
		+ "  직접 만든 인물: 표정 이름은 자유롭게 정하되 공백, 괄호, 쉼표, 콜론은 넣지 마세요. AI가 말하는 내용에 맞춰 그중에서 고릅니다.\n\n"
		+ "[기본 인물의 폴더 이름과 표정 이름]\n";

	private const string ReadmeJa =
		"珈琲のおとも · AIお供のカスタムキャラクターと画像\n\n"
		+ "このフォルダでは、サブフォルダ一つがキャラクター一人です。保存して数秒後に反映され、ゲームの再起動は不要です。\n\n"
		+ "【内蔵キャラクターの画像を差し替える】\n"
		+ "サブフォルダの名前を内蔵キャラクターと同じにすると（作成済みのものは末尾に記載）、中の画像が AIお供タブの立ち絵とゲーム画面の姿に置き換わります。キャラクター設定は内蔵のものをそのまま使います。\n"
		+ "攻略アシスタントは、ゲーム画面の姿だけが変わります。\n\n"
		+ "【自分でキャラクターを作る】\n"
		+ "1. サブフォルダを新しく作ります。フォルダ名がそのままキャラクターの名前になります。\n"
		+ "2. その中に persona.txt を置き、この人が誰か、性格、話し方、趙活の呼び方を書きます。UTF-8 で保存してください。読み込むのは先頭から 8000 字までです。書き方は、内蔵キャラクターのフォルダにある reference.txt を参考にしてください。\n"
		+ "3. 画像はなくてもかまいません。ない場合は名前の最初の一文字を表示します。\n"
		+ "置くと、AIお供タブ上部のキャラクター欄に表示され、お供のひとことのお供にも選べます。\n\n"
		+ "【画像の置き方】\n"
		+ "- png と jpg に対応しています。背景が透明な png をおすすめします。画像は切り抜かず、そのまま表示します。1 枚 20 MB までです。\n"
		+ "- default.png：ふだん表示する画像です。この 1 枚だけでもかまいません。\n"
		+ "- head.png：顔アイコンです。キャラクターのボタンと、ゲーム画面で小さくしたときに使います。なければ default.png を使います。\n"
		+ "- それ以外の画像は表情で、ファイル名が表情名になります。\n"
		+ "  内蔵キャラクター：末尾に記載した表情名を使ってください。ある表情の画像がないときは、名前が末尾の数字だけ違う画像を使い、それもなければ default.png を使います。\n"
		+ "  自作キャラクター：表情名は自由に付けられますが、空白、括弧、読点、コロンは入れないでください。AI が話す内容に合わせてその中から選びます。\n\n"
		+ "【内蔵キャラクターのフォルダ名と表情名】\n";
}
