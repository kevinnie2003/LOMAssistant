using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LOMAssistant.Config;
using LOMAssistant.Features.Story;
using Mortal.Core;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.UI;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 新版存档、读档界面：保留游戏面板的外框、标题、关闭按钮和确认框，把原来的栏位藏起来，换成左边列表、右边详情。
/// 列表每条有心上人头像、游戏日期、存档时间、备注，旁边就是「备注」「删除」按钮；详情栏是心上人立绘、任务、读档后的头几句对白。
/// 读档页分「手动存档」「自动存档」两页，自动存档页最上面是当前进度（原来的「最近存档」），下面按剧情、养成、战斗列出全部旧档。
/// 读档、存档、删除都还是调用游戏原来的栏位（藏起来的那些），确认框也是游戏自己的，读档防连点、备注照常起作用。
/// </summary>
public class SaveBrowser : MonoBehaviour
{
	private static readonly Color Txt = new Color(0.91f, 0.88f, 0.82f, 1f);
	private static readonly Color Gold = new Color(0.79f, 0.64f, 0.42f, 1f);
	private static readonly Color Dim = new Color(0.59f, 0.55f, 0.49f, 1f);
	private static readonly Color LineColor = new Color(0.35f, 0.31f, 0.25f, 1f);
	private static readonly Color PaneColor = new Color(0.03f, 0.028f, 0.025f, 0.5f);

	// 原来的这些子物体换成新界面后藏起来（只调透明、不停用：游戏打开面板时还会刷新、选中它们）
	private static readonly string[] LoadHidden = { "Logo", "RecentSave", "AutoSave", "Line", "Scroll View", "Auto", "Manual", "Recent", "GamepadIcon", "GamepadIcon (1)" };
	private static readonly string[] SaveHidden = { "Logo", "Line", "Scroll View" };

	/// <summary>心上人是「自己」：还没有心上人。</summary>
	private const int NoLover = 99;

	private const string NewGame = "新游戏";

	private static readonly (string Category, string Name)[] AutoGroups = { ("auto", "剧情"), ("auto_free", "养成"), ("auto_battle", "战斗") };

	/// <summary>心上人（游戏数值 lover）对应的立绘。</summary>
	private static readonly Dictionary<int, string> Portraits = new Dictionary<int, string>
	{
		{ 0, "Sister" }, { 1, "Girl1_瑞杏" }, { 2, "Girl2_葉雲裳" }, { 3, "Girl5_小梅" }, { 4, "Girl9_上官瑩" },
		{ 5, "Girl6_小蘭" }, { 6, "Girl7_小竹" }, { 7, "Girl8_小菊" }, { 8, "Girl4_龍湘" },
	};

	private sealed class Entry
	{
		public string Slot = ""; // 删除、备注用的栏位名（001、auto_1 …）
		public string Label = ""; // 传奇001
		public string Caption = ""; // 自动存档的类别、「当前进度」
		public GameSave Data;
		public bool Latest;
		public bool LastSaved; // 这次游戏里最后保存的手动存档
		public bool LastLoaded; // 这次游戏里最后读取的手动存档
		public bool CanNote;
		public bool CanDelete;
		public bool Compact;
		public Action Primary;
	}

	private bool _isLoad;
	private MonoBehaviour _panel;
	private RectTransform _root, _listContent, _detail;
	private ScrollRect _scroll;
	private Font _calligraphy, _serif;
	private Button _buttonTemplate;
	private int _tab; // 读档页：0 手动，1 自动
	private readonly List<Button> _tabs = new List<Button>();
	private readonly List<(Entry Entry, Button Button)> _rows = new List<(Entry, Button)>();
	private int _selected;
	private Entry _shown;

	// 详情栏
	private Image _portrait;
	private Text _loverCaption, _loverName, _date, _info, _mission, _section, _lines;
	private InputField _noteBox;
	private Button _primaryButton, _deleteButton;
	private readonly Dictionary<Entry, Text> _rowNotes = new Dictionary<Entry, Text>();
	private float _noteEditedAt = -10f;
	private static SaveBrowser _current;

	public static bool Active(MonoBehaviour panel) => panel != null && panel.GetComponent<SaveBrowser>() is SaveBrowser b && b._root != null;

	// ---------------- 补丁 ----------------

	// 打开面板时游戏自己会刷新一遍栏位（UpdateSlot），新界面等打开完再刷新一次就够了
	private static bool _opening;

	[HarmonyPrefix]
	[HarmonyPatch(typeof(LoadGamePanel), "OnPanelOpen")]
	public static void LoadGamePanel_OnPanelOpen_Prefix() => _opening = true;

	[HarmonyPostfix]
	[HarmonyPatch(typeof(LoadGamePanel), "OnPanelOpen")]
	public static void LoadGamePanel_OnPanelOpen_Postfix(LoadGamePanel __instance) => Open(__instance, load: true);

	[HarmonyPrefix]
	[HarmonyPatch(typeof(SaveGamePanel), "OnPanelOpen")]
	public static void SaveGamePanel_OnPanelOpen_Prefix() => _opening = true;

	[HarmonyPostfix]
	[HarmonyPatch(typeof(SaveGamePanel), "OnPanelOpen")]
	public static void SaveGamePanel_OnPanelOpen_Postfix(SaveGamePanel __instance) => Open(__instance, load: false);

	// 记住最后读取的手动栏位（栏位名全是数字：001、002…），存在存档目录的一个小文件里，重启游戏也还在；
	// 上次保存的不用记，直接看存档里的保存时间
	private const string LastLoadedFile = "LOMAssistant_LastLoaded.txt";
	private static string _lastLoaded;
	private static bool _lastLoadedRead;

	private static bool IsManualSlot(string slot) => !string.IsNullOrEmpty(slot) && slot.All(char.IsDigit);

	private static string LastLoadedPath()
	{
		if (SaveSystem.Instance == null)
		{
			return null;
		}
		string dat = Traverse.Create(SaveSystem.Instance).Method("GetSaveDataFile", new[] { typeof(string) }).GetValue<string>("001");
		string dir = string.IsNullOrEmpty(dat) ? null : System.IO.Path.GetDirectoryName(dat);
		return dir == null ? null : System.IO.Path.Combine(dir, LastLoadedFile);
	}

	private static string LastLoadedSlot()
	{
		if (!_lastLoadedRead)
		{
			try
			{
				string path = LastLoadedPath();
				if (path != null)
				{
					_lastLoadedRead = true;
					if (System.IO.File.Exists(path))
					{
						string slot = System.IO.File.ReadAllText(path).Trim();
						if (IsManualSlot(slot))
						{
							_lastLoaded = slot;
						}
					}
				}
			}
			catch (Exception e)
			{
				FileLogger.LogError("存档界面：读取上次读取的存档失败: " + e.Message);
			}
		}
		return _lastLoaded;
	}

	// 游戏读档时先 SetSlot 再读取
	[HarmonyPostfix]
	[HarmonyPatch(typeof(SaveSystem), "SetSlot")]
	public static void SaveSystem_SetSlot_Postfix(string slot)
	{
		if (!IsManualSlot(slot) || slot == _lastLoaded)
		{
			return;
		}
		_lastLoaded = slot;
		_lastLoadedRead = true;
		try
		{
			string path = LastLoadedPath();
			if (path != null)
			{
				System.IO.File.WriteAllText(path, slot);
			}
		}
		catch (Exception e)
		{
			FileLogger.LogError("存档界面：保存上次读取的存档失败: " + e.Message);
		}
	}

	// 删除、存档以后游戏会刷新原来的栏位，新界面跟着刷新
	[HarmonyPostfix]
	[HarmonyPatch(typeof(LoadGamePanel), "UpdateSlot")]
	public static void LoadGamePanel_UpdateSlot_Postfix(LoadGamePanel __instance)
	{
		if (!_opening)
		{
			__instance.GetComponent<SaveBrowser>()?.Refresh();
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(SaveGamePanel), "UpdateSlot")]
	public static void SaveGamePanel_UpdateSlot_Postfix(SaveGamePanel __instance)
	{
		if (!_opening)
		{
			__instance.GetComponent<SaveBrowser>()?.Refresh();
		}
	}

	// 确认框关掉后，游戏会把选中项放回原来的栏位（藏着的），这里再选回新界面
	[HarmonyPostfix]
	[HarmonyPatch(typeof(LoadGamePanel), "CloseDeletePanel")]
	public static void LoadGamePanel_CloseDeletePanel_Postfix(LoadGamePanel __instance) => __instance.GetComponent<SaveBrowser>()?.Reselect();

	[HarmonyPostfix]
	[HarmonyPatch(typeof(SaveGamePanel), "CloseConfirmPanel")]
	public static void SaveGamePanel_CloseConfirmPanel_Postfix(SaveGamePanel __instance) => __instance.GetComponent<SaveBrowser>()?.Reselect();

	// 删除确认后游戏要选中「原来选中物体的父节点下第一个子物体」，新界面的物体已经刷新掉了，出错不影响删除本身
	[HarmonyFinalizer]
	[HarmonyPatch(typeof(LoadGamePanel), "ConfirmDeleteSlot")]
	public static Exception LoadGamePanel_ConfirmDeleteSlot_Finalizer(LoadGamePanel __instance, Exception __exception)
	{
		if (__exception != null && Active(__instance))
		{
			FileLogger.Log("[存档界面] 删除后恢复选中项出错（已忽略）：" + __exception.Message);
			__instance.GetComponent<SaveBrowser>()?.Reselect();
			return null;
		}
		return __exception;
	}

	private static void Open(MonoBehaviour panel, bool load)
	{
		_opening = false;
		try
		{
			SaveBrowser browser = panel.GetComponent<SaveBrowser>() ?? panel.gameObject.AddComponent<SaveBrowser>();
			browser._isLoad = load;
			browser._panel = panel;
			if (browser._root == null && !browser.Build())
			{
				return;
			}
			browser._selected = 0;
			browser.Refresh();
		}
		catch (Exception e)
		{
			FileLogger.LogError("[存档界面] 打开失败，用原来的界面: " + e);
		}
	}

	// ---------------- 搭界面 ----------------

	private bool Build()
	{
		Transform container = _panel.transform.Find("Container");
		if (container == null)
		{
			FileLogger.LogWarning("[存档界面] 找不到 Container，用原来的界面");
			return false;
		}
		if (!FindTemplates())
		{
			FileLogger.LogWarning("[存档界面] 找不到原来栏位的字体或按钮，用原来的界面");
			return false;
		}
		foreach (string name in _isLoad ? LoadHidden : SaveHidden)
		{
			Transform t = container.Find(name);
			if (t != null)
			{
				CanvasGroup g = t.GetComponent<CanvasGroup>() ?? t.gameObject.AddComponent<CanvasGroup>();
				g.alpha = 0f;
				g.interactable = false;
				g.blocksRaycasts = false;
			}
		}
		var size = ((RectTransform)container).rect.size;
		// 存档页的标题原来在右边，挪到和读档页一样的左上角
		if (!_isLoad && container.Find("Header") is RectTransform header)
		{
			header.anchoredPosition = new Vector2(106f, -48f);
		}
		_root = NewRect("LOMAssistant_SaveBrowser", container);
		Stretch(_root);
		// 放在确认框、关闭按钮下面
		Transform below = container.Find(_isLoad ? "DeletePanel" : "ConfirmPanel");
		_root.SetSiblingIndex(below != null ? below.GetSiblingIndex() : 0);

		float margin = 56f, top = 150f, bottom = 40f, gap = 36f;
		float listWidth = Mathf.Round((size.x - margin * 2 - gap) * 0.52f);
		float detailX = margin + listWidth + gap;
		float listTop = top + (_isLoad ? 70f : 10f);
		if (_isLoad)
		{
			BuildTabs(margin, top);
		}
		BuildList(margin, listTop, listWidth, size.y - listTop - bottom);
		BuildDetail(detailX, top, size.x - detailX - margin, size.y - top - bottom);
		FileLogger.Log($"[存档界面] {(_isLoad ? "读档" : "存档")}页已换成新界面（{size.x:0}x{size.y:0}）");
		return true;
	}

	private bool FindTemplates()
	{
		Component slot = _isLoad
			? (Component)(Traverse.Create(_panel).Field("_saveSlots").GetValue<LoadSlotPanel[]>()?.FirstOrDefault())
			: Traverse.Create(_panel).Field("_saveSlots").GetValue<SaveSlotPanel[]>()?.FirstOrDefault();
		if (slot == null)
		{
			return false;
		}
		var tr = Traverse.Create(slot);
		_calligraphy = tr.Field("_titleText").GetValue<Text[]>()?.FirstOrDefault()?.font;
		_serif = tr.Field("_timeText").GetValue<Text[]>()?.FirstOrDefault()?.font ?? tr.Field("_slotText").GetValue<Text>()?.font;
		// 按钮样式用读档页栏位上的「删除」按钮（存档页栏位没有，去读档面板上找）
		LoadGamePanel load = _isLoad ? (LoadGamePanel)_panel : LoadGamePanel.Instance;
		LoadSlotPanel loadSlot = load != null ? Traverse.Create(load).Field("_saveSlots").GetValue<LoadSlotPanel[]>()?.FirstOrDefault() : null;
		_buttonTemplate = loadSlot != null ? loadSlot.DeleteButton : null;
		if (_buttonTemplate == null)
		{
			AutoSaveSlotPanel auto = load != null ? Traverse.Create(load).Field("_autoSaveSlot").GetValue<AutoSaveSlotPanel[]>()?.FirstOrDefault() : null;
			_buttonTemplate = auto != null ? auto.DeleteButton : null;
		}
		return _calligraphy != null && _serif != null;
	}

	private void BuildTabs(float x, float y)
	{
		_tabs.Clear();
		string[] names = { "手动存档", "自动存档" };
		for (int i = 0; i < names.Length; i++)
		{
			int index = i;
			RectTransform rt = NewRect("Tab" + i, _root);
			Place(rt, x + i * 220f, y, 200f, 60f);
			Image hit = rt.gameObject.AddComponent<Image>();
			hit.color = new Color(0, 0, 0, 0);
			Button b = rt.gameObject.AddComponent<Button>();
			b.transition = Selectable.Transition.None;
			b.onClick.AddListener(() => SwitchTab(index));
			Text t = NewText(rt, "Text", _calligraphy, 40, Dim, TextAnchor.MiddleLeft);
			Stretch(t.rectTransform);
			t.text = names[i];
			RectTransform underline = NewRect("Underline", rt);
			Place(underline, 0f, 56f, 160f, 3f);
			underline.gameObject.AddComponent<Image>().color = Gold;
			_tabs.Add(b);
		}
	}

	private void SwitchTab(int tab)
	{
		if (_tab == tab)
		{
			return;
		}
		_tab = tab;
		_selected = 0;
		Refresh();
	}

	private void BuildList(float x, float y, float w, float h)
	{
		RectTransform view = NewRect("List", _root);
		Place(view, x, y, w, h);
		view.gameObject.AddComponent<RectMask2D>();
		_scroll = view.gameObject.AddComponent<ScrollRect>();
		_scroll.horizontal = false;
		_scroll.movementType = ScrollRect.MovementType.Clamped;
		_scroll.scrollSensitivity = 40f;
		Image hit = view.gameObject.AddComponent<Image>();
		hit.color = new Color(0, 0, 0, 0);
		_listContent = NewRect("Content", view);
		_listContent.anchorMin = new Vector2(0, 1);
		_listContent.anchorMax = new Vector2(1, 1);
		_listContent.pivot = new Vector2(0.5f, 1f);
		_listContent.sizeDelta = Vector2.zero;
		VerticalLayoutGroup layout = _listContent.gameObject.AddComponent<VerticalLayoutGroup>();
		layout.childControlHeight = true;
		layout.childControlWidth = true;
		layout.childForceExpandHeight = false;
		layout.spacing = 6f;
		layout.padding = new RectOffset(0, 18, 0, 10);
		ContentSizeFitter fitter = _listContent.gameObject.AddComponent<ContentSizeFitter>();
		fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
		_scroll.content = _listContent;
		_scroll.viewport = view;
		// 细滚动条
		RectTransform bar = NewRect("Scrollbar", view);
		bar.anchorMin = new Vector2(1, 0);
		bar.anchorMax = new Vector2(1, 1);
		bar.pivot = new Vector2(1, 0.5f);
		bar.sizeDelta = new Vector2(4f, 0f);
		bar.gameObject.AddComponent<Image>().color = LineColor;
		Scrollbar scrollbar = bar.gameObject.AddComponent<Scrollbar>();
		scrollbar.direction = Scrollbar.Direction.BottomToTop;
		RectTransform handle = NewRect("Handle", bar);
		Stretch(handle);
		Image handleImage = handle.gameObject.AddComponent<Image>();
		handleImage.color = new Color(0.67f, 0.63f, 0.55f, 1f);
		scrollbar.handleRect = handle;
		scrollbar.targetGraphic = handleImage;
		scrollbar.navigation = new Navigation { mode = Navigation.Mode.None };
		_scroll.verticalScrollbar = scrollbar;
		_scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
	}

	private void BuildDetail(float x, float y, float w, float h)
	{
		_detail = NewRect("Detail", _root);
		Place(_detail, x, y, w, h);
		_detail.gameObject.AddComponent<Image>().color = PaneColor;
		Border(_detail, new Color(Gold.r, Gold.g, Gold.b, 0.35f));

		// 立绘：右上角，只露出头到胸口，下沿正好落在分隔线上
		// portraitW：立绘大致占的宽度，人物中心放在这一块的中间，左边留给文字
		const float portraitH = 300f;
		float picHeight = portraitH / 0.46f, picWidth = picHeight * 1268f / 1700f;
		float portraitW = Mathf.Min(Mathf.Round(picWidth * 0.72f), w * 0.56f);
		RectTransform frame = NewRect("Portrait", _detail);
		// 遮罩只管上下（下沿落在分隔线上），左右放开：有的立绘人物偏一边、手里拿着东西，左右一裁就切到人
		Place(frame, 0f, 4f, w, portraitH);
		frame.gameObject.AddComponent<RectMask2D>();
		RectTransform pic = NewRect("Image", frame);
		_portrait = pic.gameObject.AddComponent<Image>();
		_portrait.raycastTarget = false;
		_portrait.preserveAspect = true;
		Place(pic, w - 8f - portraitW / 2f - picWidth / 2f, -0.3f * picHeight, picWidth, picHeight);
		_portraitCenter = w - 8f - portraitW / 2f;
		_portraitHeight = portraitH;

		float textW = w - portraitW - 40f;
		_loverCaption = NewText(_detail, "LoverCaption", _serif, 22, Dim, TextAnchor.UpperLeft);
		Place(_loverCaption.rectTransform, 28f, 22f, textW, 30f);
		_loverName = NewText(_detail, "Lover", _calligraphy, 54, Txt, TextAnchor.UpperLeft);
		Place(_loverName.rectTransform, 28f, 48f, textW + 60f, 66f);
		_date = NewText(_detail, "Date", _calligraphy, 34, Txt, TextAnchor.UpperLeft);
		Place(_date.rectTransform, 28f, 120f, textW + 60f, 46f);
		_info = NewText(_detail, "Info", _serif, 21, Dim, TextAnchor.UpperLeft);
		Place(_info.rectTransform, 28f, 172f, textW, 60f);
		_info.horizontalOverflow = HorizontalWrapMode.Wrap;
		_mission = NewText(_detail, "Mission", _serif, 22, Txt, TextAnchor.UpperLeft);
		Place(_mission.rectTransform, 28f, 234f, textW, 62f);

		RectTransform rule = NewRect("Rule", _detail);
		Place(rule, 24f, 304f, w - 48f, 1f);
		rule.gameObject.AddComponent<Image>().color = LineColor;
		_section = NewText(_detail, "Section", _serif, 21, Dim, TextAnchor.UpperLeft);
		Place(_section.rectTransform, 28f, 316f, w - 56f, 30f);
		_lines = NewText(_detail, "Lines", _serif, 22, Txt, TextAnchor.UpperLeft);
		Place(_lines.rectTransform, 28f, 348f, w - 56f, h - 348f - 190f);
		_lines.horizontalOverflow = HorizontalWrapMode.Wrap;
		// 放不下的整句不显示，不压到备注框上
		_lines.verticalOverflow = VerticalWrapMode.Truncate;
		_linesHeight = h - 348f - 190f;
		_lines.lineSpacing = 1.1f;

		// 备注框：点了改备注
		RectTransform note = NewRect("Note", _detail);
		Place(note, 24f, h - 178f, w - 48f, 68f);
		Image noteBack = note.gameObject.AddComponent<Image>();
		noteBack.color = new Color(0f, 0f, 0f, 0.3f);
		Border(note, new Color(Gold.r, Gold.g, Gold.b, 0.25f));
		// 备注直接在这里写：点进去打字，回车或点别处就存
		Text noteText = NewText(note, "Text", _serif, 23, Gold, TextAnchor.MiddleLeft);
		Place(noteText.rectTransform, 16f, 0f, w - 80f, 68f);
		Text placeholder = NewText(note, "Placeholder", _serif, 21, new Color(0.42f, 0.39f, 0.35f, 1f), TextAnchor.MiddleLeft);
		Place(placeholder.rectTransform, 16f, 0f, w - 80f, 68f);
		placeholder.text = "点这里写备注，回车保存";
		_noteBox = note.gameObject.AddComponent<InputField>();
		_noteBox.textComponent = noteText;
		_noteBox.placeholder = placeholder;
		_noteBox.targetGraphic = noteBack;
		_noteBox.lineType = InputField.LineType.SingleLine;
		_noteBox.characterLimit = 30;
		_noteBox.caretColor = Gold;
		_noteBox.customCaretColor = true;
		_noteBox.selectionColor = new Color(Gold.r, Gold.g, Gold.b, 0.35f);
		_noteBox.transition = Selectable.Transition.ColorTint;
		_noteBox.colors = new ColorBlock { normalColor = Color.white, highlightedColor = new Color(1.6f, 1.5f, 1.3f, 1f), selectedColor = new Color(1.6f, 1.5f, 1.3f, 1f), pressedColor = Color.white, disabledColor = Color.white, colorMultiplier = 1f };
		_noteBox.onEndEdit.AddListener(SaveNote);

		_primaryButton = MakeButton(_detail, "读取", 30, 24f, h - 88f, 260f, 62f);
		_primaryButton.onClick.AddListener(() => Act(_shown));
		_deleteButton = MakeButton(_detail, "删除", 26, 304f, h - 88f, 140f, 62f);
		_deleteButton.onClick.AddListener(() => Delete(_shown));
	}

	// ---------------- 内容 ----------------

	private void Refresh()
	{
		if (_root == null)
		{
			return;
		}
		var timer = System.Diagnostics.Stopwatch.StartNew();
		try
		{
			List<Entry> entries;
			using (SaveDataCache.Scope())
			{
				entries = _isLoad ? (_tab == 0 ? ManualEntries(includeEmpty: false) : AutoEntries()) : ManualEntries(includeEmpty: true);
			}
			if (!_isLoad || _tab == 0)
			{
				// 上次保存：手动存档里保存时间最新的那个（存档文件里带保存时间，重启游戏也还在）
				string saved = entries.Where(e => e.Data != null && e.Caption != NewGame).OrderByDescending(e => e.Data.TimeTick).FirstOrDefault()?.Slot;
				string loaded = LastLoadedSlot();
				if (string.IsNullOrEmpty(loaded))
				{
					// 从没记录过（第一次用、或继续游戏进来的）：当前栏位就是读取的那个
					loaded = SaveSystem.Instance.CurrentSlot;
				}
				foreach (Entry e in entries)
				{
					bool real = e.Data != null && e.Caption != NewGame;
					e.LastSaved = real && !string.IsNullOrEmpty(saved) && e.Slot == saved;
					e.LastLoaded = real && !string.IsNullOrEmpty(loaded) && e.Slot == loaded;
				}
			}
			foreach (Transform child in _listContent)
			{
				Destroy(child.gameObject);
			}
			_listContent.DetachChildren();
			_rows.Clear();
			_rowNotes.Clear();
			_current = this;
			if (_isLoad && _tab == 1)
			{
				string group = null;
				foreach (Entry e in entries)
				{
					if (e.Caption != group && e.Caption != "当前进度")
					{
						group = e.Caption;
						GroupHeader(group);
					}
					_rows.Add((e, Row(e)));
				}
			}
			else
			{
				foreach (Entry e in entries)
				{
					_rows.Add((e, Row(e)));
				}
			}
			if (_rows.Count == 0)
			{
				GroupHeader(_isLoad && _tab == 1 ? "还没有自动存档" : "还没有存档");
			}
			for (int i = 0; i < _tabs.Count; i++)
			{
				bool on = i == _tab;
				_tabs[i].GetComponentInChildren<Text>().color = on ? Txt : Dim;
				_tabs[i].transform.Find("Underline").gameObject.SetActive(on);
			}
			SetNavigation();
			_selected = Mathf.Clamp(_selected, 0, Mathf.Max(0, _rows.Count - 1));
			Show(_rows.Count > 0 ? _rows[_selected].Entry : null);
			Reselect();
			if (timer.ElapsedMilliseconds >= 50)
			{
				FileLogger.Log($"[存档界面] 刷新 {_rows.Count} 条用时 {timer.ElapsedMilliseconds} 毫秒");
			}
		}
		catch (Exception e)
		{
			FileLogger.LogError("[存档界面] 刷新失败: " + e);
		}
	}

	private List<Entry> ManualEntries(bool includeEmpty)
	{
		var list = new List<Entry>();
		if (_isLoad)
		{
			LoadSlotPanel[] slots = Traverse.Create(_panel).Field("_saveSlots").GetValue<LoadSlotPanel[]>() ?? new LoadSlotPanel[0];
			for (int i = 0; i < slots.Length; i++)
			{
				LoadSlotPanel slot = slots[i];
				string name = SlotName(i + 1);
				GameSave data = SaveSystem.Instance.GetSaveData(name);
				if (data == null)
				{
					// 原来的界面点空栏位就是开新游戏：最上面放一条「开始新游戏」，用第一个空栏位
					if (!list.Any(e => e.Caption == NewGame))
					{
						list.Insert(0, new Entry { Slot = name, Label = SlotLabel(name), Caption = NewGame, Compact = true, Primary = () => slot.OnTitleClick() });
					}
					continue;
				}
				list.Add(new Entry { Slot = name, Label = SlotLabel(name), Data = data, CanNote = true, CanDelete = true, Primary = () => slot.OnTitleClick() });
			}
		}
		else
		{
			SaveSlotPanel[] slots = Traverse.Create(_panel).Field("_saveSlots").GetValue<SaveSlotPanel[]>() ?? new SaveSlotPanel[0];
			for (int i = 0; i < slots.Length; i++)
			{
				SaveSlotPanel slot = slots[i];
				string name = SlotName(i + 1);
				GameSave data = SaveSystem.Instance.GetSaveData(name);
				if (data == null && !includeEmpty)
				{
					continue;
				}
				list.Add(new Entry { Slot = name, Label = SlotLabel(name), Data = data, CanNote = data != null, Primary = () => slot.OnTitleClick() });
			}
		}
		return list;
	}

	private List<Entry> AutoEntries()
	{
		var list = new List<Entry>();
		var load = (LoadGamePanel)_panel;
		RecentSaveSlotPanel recent = Traverse.Create(load).Field("_recentSavePanel").GetValue<RecentSaveSlotPanel>();
		string current = SaveSystem.Instance.CurrentSlot;
		GameSave currentData = string.IsNullOrEmpty(current) ? null : SaveSystem.Instance.GetSaveData(current);
		if (currentData != null && recent != null)
		{
			list.Add(new Entry { Slot = current, Label = SlotLabel(current), Caption = "当前进度", Data = currentData, CanNote = SaveNotePatch.IsManualSlot(current), CanDelete = true, Primary = () => recent.OnTitleClick() });
		}
		AutoSaveSlotPanel[] cards = Traverse.Create(load).Field("_autoSaveSlot").GetValue<AutoSaveSlotPanel[]>() ?? new AutoSaveSlotPanel[0];
		foreach (var (category, groupName) in AutoGroups)
		{
			AutoSaveSlotPanel card = cards.FirstOrDefault(c => c != null && BaseCategory(Traverse.Create(c).Field("_slot").GetValue<string>()) == category);
			if (card == null)
			{
				continue;
			}
			for (int i = 0; i <= AutoSaveHistoryPatch.MaxHistory; i++)
			{
				string name = i == 0 ? category : AutoSaveHistoryPatch.HistorySlot(category, i);
				AutoGameSave auto = SaveSystem.Instance.GetAutoSaveData(name);
				if (auto?.GameSave == null)
				{
					continue;
				}
				list.Add(new Entry
				{
					Slot = name, Label = SlotLabel(auto.Slot), Caption = groupName, Data = auto.GameSave, Latest = i == 0, CanDelete = true, Compact = true,
					Primary = () => LoadAuto(card, name),
				});
			}
		}
		return list;
	}

	private static void LoadAuto(AutoSaveSlotPanel card, string slot)
	{
		Traverse.Create(card).Field("_slot").SetValue(slot);
		card.Setup();
		card.OnTitleClick();
	}

	private static string BaseCategory(string slot)
	{
		if (string.IsNullOrEmpty(slot))
		{
			return "";
		}
		int cut = slot.LastIndexOf('_');
		return cut > 0 && int.TryParse(slot.Substring(cut + 1), out _) ? slot.Substring(0, cut) : slot;
	}

	private static string SlotName(int index) => index.ToString().PadLeft(3, '0');

	private static string SlotLabel(string slot)
	{
		try
		{
			string format = LocalizationManager.Instance.LocaleResolver.GetString("System/SaveSlotText");
			return TextConverter.ToSimplified(string.Format(format, slot));
		}
		catch
		{
			return slot;
		}
	}

	private static string GameDate(GameSave data)
	{
		try
		{
			var resolver = LocalizationManager.Instance.LocaleResolver;
			var time = new GameTime(data.CurrentYear, data.CurrentMonth, data.CurrentStage);
			return TextConverter.ToSimplified(string.Format(resolver.GetString("System/GameTimeText"), resolver.GetString(time.YearText), resolver.GetString(time.MonthText), resolver.GetString(time.StageText)));
		}
		catch
		{
			return $"第{data.CurrentYear}年{data.CurrentMonth}月";
		}
	}

	private static string SavedAt(GameSave data) => new DateTime(data.TimeTick).ToString("yyyy/MM/dd HH:mm");

	private static int Lover(GameSave data) => data?.Stats?.FirstOrDefault(s => s.Key == "lover")?.Value ?? 0;

	private static string LoverName(int lover)
	{
		try
		{
			string name = LocalizationManager.Instance.LocaleResolver.GetString("PlayerInfo/Lover/" + lover);
			return string.IsNullOrEmpty(name) || name.StartsWith("PlayerInfo/") ? "" : TextConverter.ToSimplified(name);
		}
		catch
		{
			return "";
		}
	}

	// ---------------- 列表行 ----------------

	private void GroupHeader(string text)
	{
		RectTransform rt = NewRect("Group", _listContent);
		rt.gameObject.AddComponent<LayoutElement>().preferredHeight = 46f;
		// 删除确认后游戏要在这一层找按钮，标题也挂一个不能点的按钮
		Button b = rt.gameObject.AddComponent<Button>();
		b.interactable = false;
		b.navigation = new Navigation { mode = Navigation.Mode.None };
		Text t = NewText(rt, "Text", _calligraphy, 30, Gold, TextAnchor.LowerLeft);
		Place(t.rectTransform, 8f, 0f, 240f, 42f);
		t.text = text;
		RectTransform line = NewRect("Line", rt);
		line.anchorMin = new Vector2(0, 0);
		line.anchorMax = new Vector2(1, 0);
		line.pivot = new Vector2(0, 0);
		line.offsetMin = new Vector2(80f, 10f);
		line.offsetMax = new Vector2(0f, 11f);
		line.gameObject.AddComponent<Image>().color = LineColor;
	}

	private Button Row(Entry e)
	{
		float height = e.Compact ? 76f : 118f;
		RectTransform rt = NewRect("Row", _listContent);
		rt.gameObject.AddComponent<LayoutElement>().preferredHeight = height;
		Image back = rt.gameObject.AddComponent<Image>();
		back.color = new Color(0, 0, 0, 0);
		Button button = rt.gameObject.AddComponent<Button>();
		button.transition = Selectable.Transition.None;
		button.onClick.AddListener(() => Act(e));
		if (e.LastSaved || e.LastLoaded)
		{
			// 上次保存、上次读取的存档：固定的底色，不随鼠标和选中移动，选中时的淡金底叠在它上面
			RectTransform mark = NewRect("LastMark", rt);
			mark.anchorMin = Vector2.zero;
			mark.anchorMax = Vector2.one;
			mark.offsetMin = Vector2.zero;
			mark.offsetMax = Vector2.zero;
			Image markImage = mark.gameObject.AddComponent<Image>();
			markImage.color = new Color(Gold.r, Gold.g, Gold.b, 0.16f);
			markImage.raycastTarget = false;
		}
		RectTransform accent = NewRect("Accent", rt);
		Place(accent, 0f, 8f, 3f, height - 16f);
		Image accentImage = accent.gameObject.AddComponent<Image>();
		accentImage.color = Gold;
		accentImage.enabled = false;
		RowView view = rt.gameObject.AddComponent<RowView>();
		view.Init(this, e, back, accentImage);

		float avatar = e.Compact ? 52f : 74f;
		AddAvatar(rt, e.Data, 14f, (height - avatar) / 2f - 2f, avatar);
		float x = 26f + avatar + 14f;
		if (e.Caption == NewGame)
		{
			Text title = NewText(rt, "Date", _calligraphy, 30, Txt, TextAnchor.UpperLeft);
			Place(title.rectTransform, 26f, 4f, 300f, 40f);
			title.text = "＋ 开始新游戏";
			Text where = NewText(rt, "Time", _serif, 18, Dim, TextAnchor.UpperLeft);
			Place(where.rectTransform, 30f, 44f, 400f, 26f);
			where.text = "存在空栏位 " + e.Label;
		}
		else if (e.Compact)
		{
			Text date = NewText(rt, "Date", _calligraphy, 30, e.Latest ? Txt : new Color(0.8f, 0.77f, 0.71f, 1f), TextAnchor.UpperLeft);
			Place(date.rectTransform, x, 4f, 300f, 40f);
			date.text = GameDate(e.Data);
			Text time = NewText(rt, "Time", _serif, 18, Dim, TextAnchor.UpperLeft);
			Place(time.rectTransform, x, 44f, 360f, 26f);
			time.supportRichText = true;
			time.text = SavedAt(e.Data) + "　" + e.Label + (e.Latest ? "　<color=#C9A26A>最新</color>" : "");
		}
		else
		{
			Text name = NewText(rt, "Name", _serif, 25, Gold, TextAnchor.UpperLeft);
			Place(name.rectTransform, x, 10f, 260f, 34f);
			// 星标和原来一样只标二周目（新周目）的存档
			name.text = e.Caption == "当前进度" ? "当前进度" : (e.Data != null && e.Data.NewGamePlus ? "★" : "") + e.Label;
			Text date = NewText(rt, "Date", _calligraphy, 36, e.Data == null ? Dim : Txt, TextAnchor.UpperLeft);
			Place(date.rectTransform, x + 140f, 4f, 360f, 46f);
			date.text = e.Data == null ? "空栏位" : GameDate(e.Data);
			Text time = NewText(rt, "Time", _serif, 20, Dim, TextAnchor.UpperLeft);
			Place(time.rectTransform, x, 50f, 400f, 28f);
			time.supportRichText = true;
			time.text = e.Data == null ? "" : SavedAt(e.Data) + (e.Caption == "当前进度" ? "　" + e.Label : "") + (e.LastSaved ? "　<color=#C9A26A>上次保存</color>" : "") + (e.LastLoaded ? "　<color=#C9A26A>上次读取</color>" : "");
			if (e.Data != null)
			{
				string note = e.CanNote ? SaveNotePatch.GetNote(e.Slot) : "";
				Text noteText = NewText(rt, "Note", _serif, 21, note.Length > 0 ? Gold : new Color(0.42f, 0.39f, 0.35f, 1f), TextAnchor.UpperLeft);
				Place(noteText.rectTransform, x - 6f, 80f, 0f, 30f);
				noteText.rectTransform.anchorMax = new Vector2(1f, 1f);
				noteText.rectTransform.offsetMax = new Vector2(-80f, noteText.rectTransform.offsetMax.y);
				noteText.text = note.Length > 0 ? "【" + note + "】" : (e.CanNote ? "（无备注）" : "");
				_rowNotes[e] = noteText;
			}
		}
		// 右边的小按钮：备注、删除
		float right = 12f;
		if (e.CanDelete && e.Data != null)
		{
			Button delete = TextButton(rt, "删除");
			AnchorRight((RectTransform)delete.transform, right, e.Compact ? 20f : 44f, 60f, 36f);
			delete.onClick.AddListener(() => Delete(e));
			right += 64f;
		}
		return button;
	}

	private void AddAvatar(RectTransform parent, GameSave data, float x, float y, float size)
	{
		if (data == null)
		{
			return;
		}
		Sprite icon = Avatar(Lover(data));
		if (icon == null)
		{
			return;
		}
		RectTransform ring = NewRect("Avatar", parent);
		Place(ring, x, y, size, size);
		Image ringImage = ring.gameObject.AddComponent<Image>();
		ringImage.sprite = Circle();
		ringImage.color = Gold;
		ringImage.raycastTarget = false;
		RectTransform mask = NewRect("Mask", ring);
		Stretch(mask);
		mask.offsetMin = new Vector2(2.5f, 2.5f);
		mask.offsetMax = new Vector2(-2.5f, -2.5f);
		Image maskImage = mask.gameObject.AddComponent<Image>();
		maskImage.sprite = Circle();
		maskImage.raycastTarget = false;
		mask.gameObject.AddComponent<Mask>().showMaskGraphic = false;
		RectTransform pic = NewRect("Icon", mask);
		Stretch(pic);
		Image picture = pic.gameObject.AddComponent<Image>();
		picture.sprite = icon;
		picture.preserveAspect = true;
		picture.raycastTarget = false;
	}

	/// <summary>列表一行：选中（键盘、手柄）或鼠标停上去时更新详情栏；实现 ILoadGameSlot，手柄的删除键照常能用。</summary>
	private sealed class RowView : MonoBehaviour, ISelectHandler, IDeselectHandler, IPointerEnterHandler, ILoadGameSlot
	{
		private SaveBrowser _owner;
		private Entry _entry;
		private Image _back, _accent;

		public string Slot => _entry.CanDelete ? _entry.Slot : "";

		public bool IsEmpty => _entry.Data == null || !_entry.CanDelete;

		public void Init(SaveBrowser owner, Entry entry, Image back, Image accent)
		{
			_owner = owner;
			_entry = entry;
			_back = back;
			_accent = accent;
		}

		public void OnSelect(BaseEventData eventData)
		{
			_owner.Focus(_entry, scroll: !(eventData is PointerEventData));
			Highlight(true);
		}

		public void OnDeselect(BaseEventData eventData) => Highlight(false);

		public void OnPointerEnter(PointerEventData eventData)
		{
			// 正在写备注时鼠标划过别的存档不切换，免得打到一半被打断
			if (Typing)
			{
				return;
			}
			if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != gameObject)
			{
				EventSystem.current.SetSelectedGameObject(gameObject, eventData);
			}
		}

		// 选中：淡淡的金色底，左边一道金线
		private void Highlight(bool on)
		{
			_back.color = on ? new Color(Gold.r, Gold.g, Gold.b, 0.1f) : new Color(0, 0, 0, 0);
			_accent.enabled = on;
		}
	}

	// ---------------- 详情栏 ----------------

	private void Focus(Entry e, bool scroll)
	{
		int index = _rows.FindIndex(r => r.Entry == e);
		if (index >= 0)
		{
			_selected = index;
			if (scroll)
			{
				ScrollTo((RectTransform)_rows[index].Button.transform);
			}
		}
		Show(e);
	}

	private void Show(Entry e)
	{
		_shown = e;
		GameSave data = e?.Data;
		_detail.gameObject.SetActive(e != null);
		if (e == null)
		{
			return;
		}
		int lover = Lover(data);
		_loverCaption.text = data == null ? "" : "心上人";
		_loverName.text = e.Caption == NewGame ? "新游戏" : data == null ? "空栏位" : lover == NoLover ? "还没有" : LoverName(lover);
		_date.text = data == null ? "" : GameDate(data);
		string kind = e.Caption == "当前进度" ? "当前进度" : e.Compact ? e.Caption + "自动存档" : "手动存档";
		_info.text = data == null ? e.Label : kind + " · " + e.Label + "\n存于 " + SavedAt(data);
		_mission.text = e.Caption == NewGame ? "在这个空栏位开始新的一局。" : data == null ? "存到这里会新建一个存档。" : Mission(data);
		ShowLines(data);
		_noteBox.gameObject.SetActive(e.CanNote && data != null);
		// 没有备注框（自动存档）时对白可以往下多占一块
		var linesSize = _lines.rectTransform.sizeDelta;
		_lines.rectTransform.sizeDelta = new Vector2(linesSize.x, _linesHeight + (_noteBox.gameObject.activeSelf ? 0f : 84f));
		string note = e.CanNote && data != null ? SaveNotePatch.GetNote(e.Slot) : "";
		_noteBox.SetTextWithoutNotify(note);
		SetButtonText(_primaryButton, e.Caption == NewGame ? "开始新游戏" : _isLoad ? "读取" : data == null ? "存到这里" : "覆盖这一栏");
		_deleteButton.gameObject.SetActive(e.CanDelete && data != null);
		ShowPortrait(data == null ? -1 : lover);
	}

	/// <summary>读档后从哪个剧本开始：游戏读档时用的是 StartStoryScript（CurrentStoryScript 存档里常是空的）。</summary>
	private static string StoryOf(GameSave data) => !string.IsNullOrEmpty(data.StartStoryScript) ? data.StartStoryScript : data.CurrentStoryScript ?? "";

	private static string Mission(GameSave data)
	{
		string script = StoryOf(data);
		string where = data.CurrentScene == "Free" ? "养成" : data.CurrentScene == "Story" ? "剧情" : data.CurrentScene == "Combat" || data.CurrentScene == "Battle" ? "战斗" : "";
		string mission = "";
		// 只写真正的任务名（剧本读完以后才知道）；拿剧本里第一句话、剧本编号凑的又长又看不懂，不写
		if (StoryAtlas.Ready && script.Length > 0 && StoryAtlas.Owner.TryGetValue(script, out StoryMission m))
		{
			string title = m.Title.Length > 10 ? m.Title.Substring(0, 10) + "…" : m.Title;
			mission = (m.Main ? "主线「" : "支线「") + title + "」";
		}
		return string.Join(" · ", new[] { where, mission }.Where(s => !string.IsNullOrEmpty(s)).ToArray());
	}

	private readonly Dictionary<string, string> _linesCache = new Dictionary<string, string>();
	private float _linesHeight;

	private void ShowLines(GameSave data)
	{
		if (data == null)
		{
			_section.text = "";
			_lines.text = "";
			return;
		}
		if (data.CurrentScene == "Free")
		{
			_section.text = "读档后回到养成地图";
			_lines.text = "";
			return;
		}
		string script = StoryOf(data);
		if (!_linesCache.TryGetValue(script, out string text))
		{
			text = "";
			try
			{
				StoryScript parsed = StoryScripts.Get(script);
				if (parsed != null)
				{
					text = string.Join("\n", parsed.Lines.Select(StoryAtlas.LineText).Where(s => s.Length > 0).Take(3).Select(s => "「" + (s.Length > 44 ? s.Substring(0, 44) + "…" : s) + "」").ToArray());
				}
			}
			catch (Exception ex)
			{
				FileLogger.Log("[存档界面] 读剧本对白失败 " + script + ": " + ex.Message);
			}
			if (text.Length == 0)
			{
				FileLogger.Log($"[存档界面] 剧本 {script} 没读到对白（剧本 {(StoryScripts.Get(script) == null ? "读不到" : "没有对白")}）");
			}
			_linesCache[script] = text;
		}
		_section.text = text.Length > 0 ? "读档后从这里开始" : "";
		_lines.text = text;
	}

	// ---------------- 动作 ----------------

	/// <summary>读档、存档、开新游戏。备注框刚关掉的半秒内不算：那一下点击是给「保存」的，不能落到后面的存档上。</summary>
	private static void Act(Entry e)
	{
		// 刚写完备注按的回车也不能落到存档上
		float noteEdited = _current != null ? _current._noteEditedAt : -10f;
		if (e == null || SaveNoteEditor.IsOpen || Typing || Time.unscaledTime - SaveNoteEditor.ClosedAt < 0.5f || Time.unscaledTime - noteEdited < 0.5f)
		{
			return;
		}
		e.Primary?.Invoke();
	}

	private void Delete(Entry e)
	{
		if (e == null || !e.CanDelete || e.Data == null || !_isLoad)
		{
			return;
		}
		// 游戏的删除确认框记下当前选中的物体，删完、取消后选回去：先选中这一行
		int index = _rows.FindIndex(r => r.Entry == e);
		if (index >= 0)
		{
			_selected = index;
			EventSystem.current?.SetSelectedGameObject(_rows[index].Button.gameObject);
		}
		((LoadGamePanel)_panel).OpenDeletePanel(e.Slot);
	}

	/// <summary>备注框写完（回车或点别处）：存下来，左边这一行的备注跟着改。</summary>
	private void SaveNote(string text)
	{
		_noteEditedAt = Time.unscaledTime;
		Entry e = _shown;
		if (e == null || !e.CanNote || e.Data == null)
		{
			return;
		}
		text = (text ?? "").Trim();
		if (text == SaveNotePatch.GetNote(e.Slot))
		{
			return;
		}
		SaveNotePatch.SetNote(e.Slot, text);
		if (_rowNotes.TryGetValue(e, out Text row) && row != null)
		{
			row.text = text.Length > 0 ? "【" + text + "】" : "（无备注）";
			row.color = text.Length > 0 ? Gold : new Color(0.42f, 0.39f, 0.35f, 1f);
		}
	}

	/// <summary>正在备注框里打字：本 Mod 的热键不响应。</summary>
	public static bool Typing
	{
		get
		{
			GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
			return selected != null && selected.GetComponent<InputField>() is InputField field && field.isFocused;
		}
	}

	private bool _pendingReselect;

	private void Reselect()
	{
		if (_rows.Count == 0 || !isActiveAndEnabled)
		{
			return;
		}
		// 改备注时输入事件暂停了，等恢复后再选
		if (EventSystem.current == null || !EventSystem.current.enabled || SaveNoteEditor.IsOpen)
		{
			_pendingReselect = true;
			return;
		}
		_pendingReselect = false;
		_selected = Mathf.Clamp(_selected, 0, _rows.Count - 1);
		EventSystem.current.SetSelectedGameObject(_rows[_selected].Button.gameObject);
	}

	private void Update()
	{
		if (_root == null)
		{
			return;
		}
		if (_pendingReselect)
		{
			Reselect();
		}
		if (!_isLoad)
		{
			return;
		}
		// 手柄 LB / RB 切换手动、自动存档
		Gamepad pad = Gamepad.current;
		if (pad != null && !SaveNoteEditor.IsOpen && Traverse.Create(_panel).Field("_deletePanel").GetValue<CommonPanel>() is CommonPanel del && !IsShown(del))
		{
			if (pad.leftShoulder.wasPressedThisFrame || pad.rightShoulder.wasPressedThisFrame)
			{
				SwitchTab(1 - _tab);
			}
		}
	}

	private static bool IsShown(CommonPanel panel)
	{
		CanvasGroup g = panel.GetComponent<CanvasGroup>();
		return g != null ? g.alpha > 0.5f : panel.gameObject.activeInHierarchy;
	}

	private void SetNavigation()
	{
		for (int i = 0; i < _rows.Count; i++)
		{
			Button row = _rows[i].Button;
			var nav = new Navigation { mode = Navigation.Mode.Explicit };
			nav.selectOnUp = i > 0 ? _rows[i - 1].Button : (_tabs.Count > 0 ? _tabs[_tab] : null);
			nav.selectOnDown = i < _rows.Count - 1 ? _rows[i + 1].Button : null;
			Button[] small = row.GetComponentsInChildren<Button>().Where(b => b != row).ToArray();
			nav.selectOnRight = small.Length > 0 ? small[small.Length - 1] : _primaryButton;
			row.navigation = nav;
			// 小按钮：从右往左排的，左右在它们之间走，最右边再往右到详情栏
			for (int j = 0; j < small.Length; j++)
			{
				var n = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = nav.selectOnUp, selectOnDown = nav.selectOnDown };
				n.selectOnLeft = j < small.Length - 1 ? small[j + 1] : row;
				n.selectOnRight = j > 0 ? small[j - 1] : _primaryButton;
				small[j].navigation = n;
			}
		}
		Button first = _rows.Count > 0 ? _rows[0].Button : null;
		foreach (Button tab in _tabs)
		{
			tab.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnDown = first, selectOnLeft = _tabs[0], selectOnRight = _tabs[_tabs.Count - 1] };
		}
		Selectable back = _rows.Count > 0 ? _rows[Mathf.Clamp(_selected, 0, _rows.Count - 1)].Button : null;
		foreach (Selectable b in new Selectable[] { _primaryButton, _deleteButton, _noteBox })
		{
			b.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnLeft = back };
		}
		var primaryNav = _primaryButton.navigation;
		primaryNav.selectOnRight = _deleteButton;
		primaryNav.selectOnUp = _noteBox;
		_primaryButton.navigation = primaryNav;
		var deleteNav = _deleteButton.navigation;
		deleteNav.selectOnLeft = _primaryButton;
		deleteNav.selectOnUp = _noteBox;
		_deleteButton.navigation = deleteNav;
		var noteNav = _noteBox.navigation;
		noteNav.selectOnDown = _primaryButton;
		_noteBox.navigation = noteNav;
	}

	private void ScrollTo(RectTransform row)
	{
		Canvas.ForceUpdateCanvases();
		float viewHeight = _scroll.viewport.rect.height;
		float contentHeight = _listContent.rect.height;
		if (contentHeight <= viewHeight)
		{
			return;
		}
		float top = -row.anchoredPosition.y - row.rect.height * (1f - row.pivot.y);
		float bottom = top + row.rect.height;
		float offset = _listContent.anchoredPosition.y;
		if (top < offset)
		{
			offset = top;
		}
		else if (bottom > offset + viewHeight)
		{
			offset = bottom - viewHeight;
		}
		_listContent.anchoredPosition = new Vector2(_listContent.anchoredPosition.x, Mathf.Clamp(offset, 0f, contentHeight - viewHeight));
	}

	// ---------------- 立绘、头像 ----------------

	private static readonly Dictionary<int, AsyncOperationHandle<Sprite>> PortraitLoads = new Dictionary<int, AsyncOperationHandle<Sprite>>();
	private int _wantedPortrait = -2;

	private void ShowPortrait(int lover)
	{
		_wantedPortrait = lover;
		_portrait.enabled = false;
		if (!LoadPortrait(lover, out AsyncOperationHandle<Sprite> handle))
		{
			return;
		}
		if (handle.IsDone)
		{
			ApplyPortrait(lover, handle);
		}
		else
		{
			handle.Completed += h => ApplyPortrait(lover, h);
		}
	}

	/// <summary>开始（或已经）读这位心上人的立绘；没有立绘返回 false。</summary>
	private static bool LoadPortrait(int lover, out AsyncOperationHandle<Sprite> handle)
	{
		handle = default;
		if (!Portraits.TryGetValue(lover, out string folder))
		{
			return false;
		}
		try
		{
			if (!PortraitLoads.TryGetValue(lover, out handle))
			{
				handle = Addressables.LoadAssetAsync<Sprite>("Assets/__Project/Images/Characters/" + folder + "/normal.png");
				PortraitLoads[lover] = handle;
			}
			return true;
		}
		catch (Exception e)
		{
			FileLogger.Log("[存档界面] 读立绘失败: " + e.Message);
			return false;
		}
	}

	/// <summary>列表里的小头像：游戏属性页那套心上人头像；心上人是「自己」（还没有）时不显示。</summary>
	private static Sprite Avatar(int lover) => lover == NoLover ? null : LoverIcon(lover);

	private void ApplyPortrait(int lover, AsyncOperationHandle<Sprite> handle)
	{
		if (this == null || _portrait == null || lover != _wantedPortrait)
		{
			return;
		}
		if (handle.Status == AsyncOperationStatus.Succeeded && handle.Result != null)
		{
			_portrait.sprite = handle.Result;
			_portrait.enabled = true;
			FitPortrait(handle.Result);
		}
	}

	private float _portraitCenter, _portraitHeight;

	/// <summary>立绘都画在同样大小的画布上：从画布 24% 处往下露出一半（头顶到腰），左右居中、不裁。</summary>
	private void FitPortrait(Sprite sprite)
	{
		Rect rect = sprite.rect;
		float picHeight = _portraitHeight / 0.5f, picWidth = picHeight * rect.width / rect.height;
		Place(_portrait.rectTransform, _portraitCenter - picWidth / 2f, -0.24f * picHeight, picWidth, picHeight);
	}

	private static int _iconSearchFrame = -1;

	/// <summary>属性页心上人头像用的图（在游戏启动时就载入的资源里，标题画面上也找得到）。</summary>
	private static readonly Dictionary<int, string> IconNames = new Dictionary<int, string>
	{
		{ 0, "icon_sister" }, { 1, "icon_girl1" }, { 2, "icon_girl2" }, { 3, "icon_girl5" }, { 4, "icon_girl9" },
		{ 5, "icon_girl6" }, { 6, "icon_girl7" }, { 7, "icon_girl8" }, { 8, "icon_girl4" },
	};

	private static Dictionary<int, Sprite> _icons;

	private static Sprite LoverIcon(int lover)
	{
		if (_icons == null || _icons.Count < IconNames.Count)
		{
			// 找不到时同一帧里不再重复找（一次刷新要画几十个头像）
			if (_iconSearchFrame == Time.frameCount)
			{
				return _icons != null && _icons.TryGetValue(lover, out Sprite cached) ? cached : null;
			}
			_iconSearchFrame = Time.frameCount;
			_icons = new Dictionary<int, Sprite>();
			try
			{
				SpriteCollectionData collection = Resources.FindObjectsOfTypeAll<SpriteCollectionData>().FirstOrDefault(c => c.name == "LoverSpriteCollection");
				if (collection != null)
				{
					foreach (int key in IconNames.Keys)
					{
						if (collection.GetByKey(key.ToString()) is Sprite sprite)
						{
							_icons[key] = sprite;
						}
					}
				}
				if (_icons.Count < IconNames.Count)
				{
					var byName = IconNames.ToDictionary(p => p.Value, p => p.Key);
					foreach (Sprite sprite in Resources.FindObjectsOfTypeAll<Sprite>())
					{
						if (sprite != null && byName.TryGetValue(sprite.name, out int key) && !_icons.ContainsKey(key))
						{
							_icons[key] = sprite;
						}
					}
				}
				if (_icons.Count < IconNames.Count)
				{
					FileLogger.Log($"[存档界面] 心上人头像只找到 {_icons.Count} 个（共 {IconNames.Count} 个）");
				}
			}
			catch (Exception e)
			{
				FileLogger.Log("[存档界面] 找心上人头像失败: " + e.Message);
			}
		}
		return _icons.TryGetValue(lover, out Sprite icon) ? icon : null;
	}

	private static Sprite _circle;

	private static Sprite Circle()
	{
		if (_circle != null)
		{
			return _circle;
		}
		const int n = 128;
		var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
		var pixels = new Color32[n * n];
		float r = n / 2f;
		for (int y = 0; y < n; y++)
		{
			for (int x = 0; x < n; x++)
			{
				float d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
				byte a = (byte)(Mathf.Clamp01(r - d) * 255f);
				pixels[y * n + x] = new Color32(255, 255, 255, a);
			}
		}
		tex.SetPixels32(pixels);
		tex.Apply();
		_circle = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
		return _circle;
	}

	// ---------------- 小工具 ----------------

	private Button MakeButton(Transform parent, string label, int fontSize, float x, float y, float w, float h)
	{
		Button b;
		if (_buttonTemplate != null)
		{
			b = MenuButtonHelper.CloneInto(_buttonTemplate, parent, label, "Button_" + label);
			var rt = (RectTransform)b.transform;
			Place(rt, x, y, w, h);
			rt.localScale = Vector3.one;
			// 边框图四周有一圈透明边：图片比按钮大一圈；点击范围只算按钮本身
			foreach (Image img in b.GetComponentsInChildren<Image>(includeInactive: true))
			{
				img.raycastTarget = false;
				if (img.transform != b.transform)
				{
					var irt = img.rectTransform;
					irt.anchorMin = irt.anchorMax = new Vector2(0.5f, 0.5f);
					irt.sizeDelta = new Vector2(w + 92f, h + 81f);
					irt.anchoredPosition = Vector2.zero;
				}
			}
			foreach (Text t in b.GetComponentsInChildren<Text>(includeInactive: true))
			{
				var trt = t.rectTransform;
				trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 0.5f);
				trt.sizeDelta = new Vector2(w, h);
				trt.anchoredPosition = Vector2.zero;
				t.fontSize = fontSize;
				t.alignment = TextAnchor.MiddleCenter;
				t.horizontalOverflow = HorizontalWrapMode.Overflow;
			}
			Image hit = b.GetComponent<Image>() ?? b.gameObject.AddComponent<Image>();
			hit.color = new Color(0, 0, 0, 0);
			hit.raycastTarget = true;
		}
		else
		{
			RectTransform rt = NewRect("Button_" + label, parent);
			Place(rt, x, y, w, h);
			Image back = rt.gameObject.AddComponent<Image>();
			back.color = new Color(0.14f, 0.13f, 0.12f, 1f);
			rt.gameObject.AddComponent<Outline>().effectColor = new Color(Gold.r, Gold.g, Gold.b, 0.6f);
			b = rt.gameObject.AddComponent<Button>();
			b.targetGraphic = back;
			Text t = NewText(rt, "Text", _calligraphy, fontSize, Txt, TextAnchor.MiddleCenter);
			Stretch(t.rectTransform);
			t.text = label;
		}
		return b;
	}

	/// <summary>只有文字的小按钮（列表行里的备注、删除）：平时灰，鼠标停上去、选中时变亮。</summary>
	private Button TextButton(Transform parent, string label)
	{
		RectTransform rt = NewRect("Button_" + label, parent);
		Image hit = rt.gameObject.AddComponent<Image>();
		hit.color = new Color(0, 0, 0, 0);
		Button b = rt.gameObject.AddComponent<Button>();
		Text t = NewText(rt, "Text", _serif, 21, Color.white, TextAnchor.MiddleCenter);
		Stretch(t.rectTransform);
		t.text = label;
		b.targetGraphic = t;
		b.colors = new ColorBlock { normalColor = Dim, highlightedColor = Gold, selectedColor = Gold, pressedColor = Txt, disabledColor = Dim, colorMultiplier = 1f, fadeDuration = 0.08f };
		return b;
	}

	/// <summary>四条细线画边框（Outline 会把整块底色也描一遍，变成一片黄）。</summary>
	private static void Border(RectTransform rt, Color color)
	{
		void Edge(string name, Vector2 min, Vector2 max, Vector2 size)
		{
			RectTransform e = NewRect(name, rt);
			e.anchorMin = min;
			e.anchorMax = max;
			e.pivot = new Vector2(0.5f, 0.5f);
			e.sizeDelta = size;
			e.anchoredPosition = Vector2.zero;
			Image img = e.gameObject.AddComponent<Image>();
			img.color = color;
			img.raycastTarget = false;
		}
		Edge("Top", new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, 1.5f));
		Edge("Bottom", new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1.5f));
		Edge("Left", new Vector2(0, 0), new Vector2(0, 1), new Vector2(1.5f, 0));
		Edge("Right", new Vector2(1, 0), new Vector2(1, 1), new Vector2(1.5f, 0));
	}

	private static void SetButtonText(Button b, string label)
	{
		foreach (Text t in b.GetComponentsInChildren<Text>(includeInactive: true))
		{
			t.text = label;
		}
	}

	private static RectTransform NewRect(string name, Transform parent)
	{
		var go = new GameObject(name, typeof(RectTransform));
		go.layer = parent.gameObject.layer;
		var rt = (RectTransform)go.transform;
		rt.SetParent(parent, false);
		return rt;
	}

	/// <summary>按左上角放：x 向右、y 向下。</summary>
	private static void Place(RectTransform rt, float x, float y, float w, float h)
	{
		rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
		rt.pivot = new Vector2(0f, 1f);
		rt.anchoredPosition = new Vector2(x, -y);
		rt.sizeDelta = new Vector2(w, h);
	}

	/// <summary>按右上角放：right 是离右边的距离。</summary>
	private static void AnchorRight(RectTransform rt, float right, float y, float w, float h)
	{
		rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
		rt.pivot = new Vector2(1f, 1f);
		rt.anchoredPosition = new Vector2(-right, -y);
		rt.sizeDelta = new Vector2(w, h);
	}

	private static void Stretch(RectTransform rt)
	{
		rt.anchorMin = Vector2.zero;
		rt.anchorMax = Vector2.one;
		rt.offsetMin = rt.offsetMax = Vector2.zero;
	}

	private static Text NewText(Transform parent, string name, Font font, int size, Color color, TextAnchor anchor)
	{
		RectTransform rt = NewRect(name, parent);
		Text t = rt.gameObject.AddComponent<Text>();
		t.font = font;
		t.fontSize = size;
		t.color = color;
		t.alignment = anchor;
		t.horizontalOverflow = HorizontalWrapMode.Overflow;
		// 不截断：这套字体行高比字号大不少，框稍矮一点整行就不显示了
		t.verticalOverflow = VerticalWrapMode.Overflow;
		t.supportRichText = false;
		t.raycastTarget = false;
		return t;
	}
}
