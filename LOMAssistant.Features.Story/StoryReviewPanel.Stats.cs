using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Story;

/// <summary>
/// 「数值」页（F9 直接打开）：角色数值、武功与学识、门派、好感、天赋、旗标，一页里分几段，顶上一个搜索框全都能搜。
/// 每行：名字、当前值、加减按钮、输入框（改了数字点「设定」）。改的是游戏自己的数据，和原版测试面板一样会写进存档。
/// </summary>
public partial class StoryReviewPanel
{
	private sealed class StatRow
	{
		public string Id = "", Name = "", Note = "";
		public Func<int> Get;
		public Action<int> Set; // 设成这个值；没有就是只读（只显示）
		public int Step = 5;
		public Func<string> Extra; // 数值后面的小字，如加成、心上人是谁、等级上限
		public bool NoValue; // 数值没有意义（功法已练满、锻造未开发等），显示「—」不显示 0
		public int Of; // 大于 0 时数值写成「当前/Of」，如本层点数 2/5
		public GameStat LockStat; // 有值时这一行多一个锁按钮（心相用）
	}

	private string _statsSearch = "";
	private Vector2 _statsScroll;
	private readonly Dictionary<string, int> _sectionPages = new Dictionary<string, int>();
	// 子分页：一次只看一组（搜索时各组都搜）
	private string _statsGroup = "角色数值";
	private string _flagQuery;
	// 物品数量改了以后，功法修炼那段要跟着变（新拿到的书也要列出来），下次画的时候重新整理
	private static bool _statsDirty;
	private readonly Dictionary<string, string> _statInputs = new Dictionary<string, string>();
	private List<(string Title, List<StatRow> Rows)> _statSections;
	private object _statsFor;

	private const int FlagsPerPage = 60;

	private static readonly GameStatType[] Character =
	{
		GameStatType.體力, GameStatType.內力, GameStatType.輕功, GameStatType.學問, GameStatType.嘴力, GameStatType.心理衛生,
		GameStatType.性情, GameStatType.處世, GameStatType.修養, GameStatType.道德, GameStatType.陰陽內功, GameStatType.命運,
		GameStatType.魅力, GameStatType.廚藝, GameStatType.銀兩, GameStatType.個人貢獻度, GameStatType.抗毒, GameStatType.抗麻, GameStatType.愛人, GameStatType.變心,
	};

	private static readonly GameStatType[] Martial =
	{
		GameStatType.武功刀劍, GameStatType.武功暗器, GameStatType.武功拳掌, GameStatType.武功腿法, GameStatType.武功奇門,
		GameStatType.武功軟兵器, GameStatType.武功槍棍, GameStatType.武功內功, GameStatType.儒學, GameStatType.道學, GameStatType.佛學,
	};

	private static readonly GameStatType[] Sect =
	{
		GameStatType.門派規模, GameStatType.門派人數, GameStatType.向心力, GameStatType.門派名聲, GameStatType.武學點數,
		GameStatType.鍛造, GameStatType.毒藥, GameStatType.門派資產, GameStatType.門派貢獻,
	};

	// 上面几段没列的其它数值：游戏里有就显示，原版测试面板改不了的也能改
	private static readonly GameStatType[] Other =
	{
		GameStatType.行動次數, GameStatType.額外行動次數_1, GameStatType.額外行動次數_2, GameStatType.額外行動次數_3,
		GameStatType.防禦, GameStatType.攻擊爆擊, GameStatType.暗器爆擊, GameStatType.戰役預設血量, GameStatType.稱號, GameStatType.娘化,
		GameStatType.全武學點數, GameStatType.全鍛造點數, GameStatType.全毒藥點數, GameStatType.全貢獻度,
	};

	// 游戏本地化里没有名字的数值，用这里的名字
	private static readonly Dictionary<GameStatType, string> OwnNames = new Dictionary<GameStatType, string>
	{
		{ GameStatType.廚藝, "厨艺" },
		{ GameStatType.行動次數, "本旬行动次数" },
		{ GameStatType.額外行動次數_1, "额外行动次数 1" },
		{ GameStatType.額外行動次數_2, "额外行动次数 2" },
		{ GameStatType.額外行動次數_3, "额外行动次数 3" },
		{ GameStatType.防禦, "防御" },
		{ GameStatType.攻擊爆擊, "攻击暴击" },
		{ GameStatType.暗器爆擊, "暗器暴击" },
		{ GameStatType.戰役預設血量, "团战预设血量" },
		{ GameStatType.稱號, "称号" },
		{ GameStatType.娘化, "娘化" },
		{ GameStatType.全武學點數, "累计武学点" },
		{ GameStatType.全鍛造點數, "累计锻造点" },
		{ GameStatType.全毒藥點數, "累计炼丹点" },
		{ GameStatType.全貢獻度, "累计贡献度" },
	};

	/// <summary>F9：打开面板并切到「数值」页；已经在这一页就关掉。</summary>
	public static void ToggleStats()
	{
		// 插件的物体对 FindObjectOfType 不可见，用 Ensure 时记下的那个
		StoryReviewPanel panel = Instance;
		if (panel == null)
		{
			return;
		}
		if (panel._visible && panel._tab == StatsTab && !panel._showChangelog)
		{
			panel._visible = false;
			return;
		}
		panel._visible = true;
		StoryAtlas.RefreshProgress();
		panel._tab = StatsTab;
		panel._showChangelog = false;
		panel._statSections = null;
		panel._statInputs.Clear();
	}

	private const int StatsTab = 6;

	private void DrawStats(Rect r)
	{
		PlayerStatManagerData player = PlayerStatManagerData.Instance;
		if (player == null || player.Stats == null || SaveSystem.Instance == null || !StoryNodes.HasCurrentSave)
		{
			GUI.Label(new Rect(r.x, r.y, r.width, S(40)), "读档进入游戏后才能改数值", _muted);
			return;
		}
		if (_statSections == null || !ReferenceEquals(_statsFor, player) || _statsDirty)
		{
			_statsDirty = false;
			_statSections = BuildStatSections(player);
			_statsFor = player;
		}
		string query = SearchBox(new Rect(r.x, r.y, S(420), S(36)), ref _statsSearch);
		if (query != _flagQuery)
		{
			_flagQuery = query;
			_sectionPages.Clear();
		}
		// 搜索框下面一排分组，点哪组看哪组；在搜索时各组的结果都列出来
		float gx = r.x, gy = r.y + S(44);
		foreach (var (title, _) in _statSections)
		{
			float width = _tabOn.CalcSize(new GUIContent(title)).x * 0.85f + S(24);
			if (gx + width > r.xMax)
			{
				break;
			}
			bool on = title == _statsGroup && query.Length == 0;
			if (GUI.Button(new Rect(gx, gy, width, S(32)), title, on ? _primary : _button) && _statsGroup != title)
			{
				_statsGroup = title;
				_statsScroll = Vector2.zero;
			}
			gx += width + S(6);
		}
		var area = new Rect(r.x, r.y + S(86), r.width, r.height - S(86));
		DragScroll("stats", area, ref _statsScroll);
		GUILayout.BeginArea(area);
		_statsScroll = GUILayout.BeginScrollView(_statsScroll);
		// 一行里的东西加起来要比格子窄，不然会撑出横向滚动条；1080p 下正好排三列
		float rowWidth = S(566);
		int columns = Mathf.Max(1, (int)((area.width - S(30)) / rowWidth));
		foreach (var (title, rows) in _statSections)
		{
			if (query.Length == 0 && title != _statsGroup)
			{
				continue;
			}
			if (title == "称号")
			{
				if (query.Length == 0)
				{
					DrawTitles(player);
				}
				continue;
			}
			var shown = rows.Where(row => Matches(query, row.Name, row.Id, row.Note)).ToList();
			if (shown.Count == 0)
			{
				continue;
			}
			bool flags = title == "旗标";
			// 一段太长（旗标、书籍、物品）就分页
			int total = shown.Count;
			int pages = Mathf.Max(1, (total + FlagsPerPage - 1) / FlagsPerPage);
			_sectionPages.TryGetValue(title, out int page);
			page = Mathf.Clamp(page, 0, pages - 1);
			shown = shown.Skip(page * FlagsPerPage).Take(FlagsPerPage).ToList();
			GUILayout.Space(S(8));
			GUILayout.BeginHorizontal();
			string count = flags ? $"共 {total} 面，按编号或中文说明搜" : pages > 1 ? $"共 {total} 项" : "";
			GUILayout.Label(title + (count.Length > 0 ? $"　<size={Mathf.RoundToInt(16 * _scale)}><color=#73644F>{count}</color></size>" : ""), _heading);
			GUILayout.FlexibleSpace();
			if (pages > 1)
			{
				if (GUILayout.Button("上一页", _button, GUILayout.Width(S(90)))) page = Mathf.Max(0, page - 1);
				GUILayout.Label($"{page + 1} / {pages}", _center, GUILayout.Width(S(80)));
				if (GUILayout.Button("下一页", _button, GUILayout.Width(S(90)))) page = Mathf.Min(pages - 1, page + 1);
			}
			_sectionPages[title] = page;
			GUILayout.EndHorizontal();
			for (int i = 0; i < shown.Count; i += columns)
			{
				GUILayout.BeginHorizontal();
				for (int c = 0; c < columns && i + c < shown.Count; c++)
				{
					DrawStatRow(shown[i + c], rowWidth);
				}
				GUILayout.FlexibleSpace();
				GUILayout.EndHorizontal();
			}
		}
		GUILayout.EndScrollView();
		GUILayout.EndArea();
	}

	private void DrawStatRow(StatRow row, float width)
	{
		GUILayout.BeginHorizontal(_card, GUILayout.Width(width - S(10)));
		// 一行放得下：名字、说明太长就截短，鼠标停上去看全文
		// 文字竖直居中、每个控件一样高（和设置页一样），一行里的字和框才对得齐
		GUIStyle line = Middle(_label);
		GUIStyle muted = Middle(_muted);
		float nameWidth = S(120), extraWidth = S(80);
		GUILayout.Label(Fit(row.Name, line, nameWidth), line, GUILayout.Width(nameWidth), Ctl());
		Rect nameRect = GUILayoutUtility.GetLastRect();
		int value = row.Get();
		GUILayout.Label(row.NoValue ? "—" : row.Of > 0 ? value + "/" + row.Of : value.ToString(), line, GUILayout.Width(S(52)), Ctl());
		string extra = row.Extra?.Invoke() ?? "";
		GUILayout.Label(Fit(extra, muted, extraWidth), muted, GUILayout.Width(extraWidth), Ctl());
		Rect extraRect = GUILayoutUtility.GetLastRect();
		string full = row.Name + (row.Note.Length > 0 ? "\n" + row.Note : "");
		if (full != row.Name || Fit(row.Name, line, nameWidth) != row.Name)
		{
			Hover(nameRect, full);
			Hover(extraRect, full);
		}
		if (row.Set == null)
		{
			// 只读：只显示数值，右边空着
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			return;
		}
		// 心相锁住时，游戏自己的变化被拦，这里手动加减、输入数值照常可用
		bool locked = row.LockStat != null && LOMAssistant.Features.Extras.MoodLockPatch.IsLocked;
		bool wasEnabled = GUI.enabled;
		if (GUILayout.Button("-" + row.Step, StepButton(), GUILayout.Width(S(58)), Ctl()))
		{
			SetManual(row, value - row.Step);
			_statInputs.Remove(row.Id);
		}
		if (GUILayout.Button("+" + row.Step, StepButton(), GUILayout.Width(S(58)), Ctl()))
		{
			SetManual(row, value + row.Step);
			_statInputs.Remove(row.Id);
		}
		if (!_statInputs.TryGetValue(row.Id, out string input))
		{
			input = value.ToString();
		}
		// 银两、门派资产这种五六位数也要放得下
		string typed = GUILayout.TextField(input, _field, GUILayout.Width(S(72)), Ctl());
		if (typed != value.ToString())
		{
			_statInputs[row.Id] = typed;
		}
		else
		{
			_statInputs.Remove(row.Id);
		}
		bool changed = _statInputs.ContainsKey(row.Id) && int.TryParse(typed, out int target);
		GUI.enabled = wasEnabled && changed;
		if (GUILayout.Button("设定", changed ? _primary : _button, GUILayout.Width(S(50)), Ctl()) && int.TryParse(typed, out target))
		{
			SetManual(row, target);
			_statInputs.Remove(row.Id);
			GUIUtility.keyboardControl = 0;
		}
		GUI.enabled = wasEnabled;
		if (row.LockStat != null)
		{
			// 锁：点一下锁住心相（数值固定不变），再点解锁；重启、读档都会自动解锁
			// 锁上：朱砂红底 + 白色合上的锁；没锁：浅色底 + 深色开着的锁
			Color oldContent = GUI.contentColor;
			GUI.contentColor = locked ? Color.white : Ink;
			bool clicked = GUILayout.Button(LOMAssistant.Features.Extras.MoodLockPatch.Icon(locked), locked ? _primary : _button, GUILayout.Width(S(36)), Ctl());
			GUI.contentColor = oldContent;
			if (clicked)
			{
				LOMAssistant.Features.Extras.MoodLockPatch.Toggle(row.LockStat);
				_statInputs.Remove(row.Id);
			}
			Hover(GUILayoutUtility.GetLastRect(), locked ? "心相已锁定，游戏里的变化不会影响它（这里仍可手动加减）。点击解锁；重启游戏、读档会自动解锁" : "点击锁定心相：锁定后数值固定不变");
		}
		GUILayout.EndHorizontal();
	}

	private static void SetManual(StatRow row, int value)
	{
		if (row.LockStat != null)
		{
			LOMAssistant.Features.Extras.MoodLockPatch.Manual(() => row.Set(value));
		}
		else
		{
			row.Set(value);
		}
	}

	private GUIStyle _stepButton;
	private float _stepScale;

	// 加减按钮左右留白小一点，「+500」这样的四个字也放得下
	private GUIStyle StepButton()
	{
		if (_stepButton == null || _stepScale != _scale)
		{
			_stepButton = new GUIStyle(_button) { padding = new RectOffset((int)S(2), (int)S(2), _button.padding.top, _button.padding.bottom) };
			_stepScale = _scale;
		}
		return _stepButton;
	}

	private static List<(string, List<StatRow>)> BuildStatSections(PlayerStatManagerData player)
	{
		var sections = new List<(string, List<StatRow>)>
		{
			("角色数值", Character.Select(t => StatRowOf(player, t)).Where(x => x != null).ToList()),
			("武功与学识", Martial.Select(t => StatRowOf(player, t)).Where(x => x != null).ToList()),
			("门派", Sect.Select(t => StatRowOf(player, t)).Where(x => x != null).ToList()),
			("其他数值", Other.Concat(UnlistedStats(player)).Select(t => StatRowOf(player, t)).Where(x => x != null).ToList()),
			// 称号单独画（选游戏称号的按钮、自定义输入框），这里只占一个分组
			("称号", new List<StatRow>()),
		};
		// 好感：游戏里所有人，原版测试面板没列的（龙渊、石公远……）也在
		var favor = new List<StatRow>();
		foreach (RelationshipStat stat in player.Relationships?.List ?? new List<RelationshipStat>())
		{
			if (stat == null)
			{
				continue;
			}
			RelationshipStat s = stat;
			favor.Add(new StatRow
			{
				Id = "rel:" + s.name,
				Name = RelationshipName(s),
				Get = () => s.Value,
				Set = v => s.SetValue(v),
			});
		}
		sections.Add(("好感", favor));
		// 天赋：全部（游戏里不显示等级的也列出来）
		var talents = new List<StatRow>();
		foreach (PlayerTalentData talent in player.Talents?.List ?? new List<PlayerTalentData>())
		{
			if (talent == null)
			{
				continue;
			}
			PlayerTalentData t = talent;
			int max = Traverse.Create(t).Field("_maxLevel").GetValue<int>();
			talents.Add(new StatRow
			{
				Id = "talent:" + t.Id,
				Name = Localized(t.GetIdKey(), t.name),
				Note = Localized(t.GetDescKey(), ""),
				Get = () => t.Level,
				Set = v => t.AddLevel(Mathf.Clamp(v, 0, max) - t.Level),
				Step = 1,
				Extra = () => "/ " + max,
			});
		}
		sections.Add(("天赋", talents));
		sections.Add(("功法修炼", MartialRows()));
		sections.Add(("锻造与炼丹", UpgradeRows(player)));
		sections.Add(("设施", FacilityRows(player)));
		ItemDatabase items = ItemDatabase.Instance;
		sections.Add(("书籍", ItemRows(items?.Books)));
		sections.Add(("物品", ItemRows(items?.Miscs).Concat(ItemRows(items?.Special)).Concat(ItemRows(items?.Consumables)).ToList()));
		// 旗标：全部，按编号排，说明转简体
		var flags = new List<StatRow>();
		MissionManagerData missions = MissionManagerData.Instance;
		foreach (FlagData flag in missions?.FlagsCollection?.Where(c => c != null && c.List != null).SelectMany(c => c.List).Where(f => f != null).Distinct().OrderBy(f => f.name, StringComparer.Ordinal)
			?? Enumerable.Empty<FlagData>())
		{
			FlagData f = flag;
			string note = TextConverter.ToSimplified(f.DevNote ?? "").Replace("\n", " ").Trim();
			flags.Add(new StatRow
			{
				Id = "flag:" + f.name,
				Name = f.name,
				Note = note,
				Get = () => f.State,
				Set = v => f.State = v,
				Step = 1,
				Extra = () => note,
			});
		}
		sections.Add(("旗标", flags));
		return sections;
	}

	// 上面几组都没列到的数值（游戏以后新加的也算），放进「其他数值」
	private static IEnumerable<GameStatType> UnlistedStats(PlayerStatManagerData player)
	{
		var listed = new HashSet<GameStatType>(Character.Concat(Martial).Concat(Sect).Concat(Other));
		var list = player.Stats?.List ?? new List<GameStat>();
		return list.Where(st => st != null && !listed.Contains(st.StatType)).Select(st => st.StatType).Distinct().OrderBy(t => (int)t).ToList();
	}

	// 不推荐直接拿的书：和四师兄商店不卖的是同一批
	private static readonly Dictionary<string, string> ItemWarnings = new Dictionary<string, string>
	{
		{ "9001", "提前拿到会让六月后山事件出 bug，剧情需要时游戏会自己给。" },
		{ "2010", "剧情里拿不到、原版商店也不卖，像是没做完的书。" },
		{ "2200", "剧情里拿不到、原版商店也不卖，像是没做完的书。" },
		{ "4000", "剧情里拿不到、原版商店也不卖，像是没做完的书。" },
	};

	/// <summary>书籍、物品的数量：改成多少背包里就有多少。从没有到有时，像在商店买到一样推进相关任务。</summary>
	private static List<StatRow> ItemRows(ItemDataCollection collection)
	{
		var rows = new List<StatRow>();
		ItemDatabase items = ItemDatabase.Instance;
		foreach (ItemData item in collection?.List?.OfType<ItemData>() ?? Enumerable.Empty<ItemData>())
		{
			ItemData it = item;
			// 消耗品（团战里的药之类）游戏不往存档里写数量，改了也存不住，只显示
			bool consumable = it.ItemType == GameItemType.消耗品;
			ItemWarnings.TryGetValue(it.Id, out string warning);
			string desc = Localized(it.GetDescKey(), "").Replace("\n", " ");
			rows.Add(new StatRow
			{
				Id = "item:" + (int)it.ItemType + ":" + it.Id,
				Name = Localized(it.GetNameKey(), it.Id),
				Note = (warning != null ? "【注意】" + warning + "\n" : "") + (consumable ? "消耗品的数量不写进存档，这里只显示。\n" : "") + desc,
				Get = () => it.CurrentCount,
				Set = consumable ? null : v =>
				{
					int before = it.CurrentCount;
					items.SetItem(it.ItemType, it.Id, Mathf.Max(0, v));
					if (it.CurrentCount > before)
					{
						foreach (PlayerMissionStat stat in it.ApplyMissionStat ?? new PlayerMissionStat[0])
						{
							stat?.Mission?.SetCurrentKey(stat.Key);
						}
					}
					_statsDirty = true;
				},
				Step = 1,
				Extra = () => warning != null ? "(慎用)" : "",
			});
		}
		return rows;
	}

	/// <summary>拥有的武学书：本层的修炼点数。加满一层就用游戏自己的 DirectLevelUp 升层（和修炼界面升层一样加上这层的加成，只是不花修炼次数），多的点数算到下一层。</summary>
	private static List<StatRow> MartialRows()
	{
		var rows = new List<StatRow>();
		foreach (Book book in ItemDatabase.Instance?.Books?.List?.OfType<Book>() ?? Enumerable.Empty<Book>())
		{
			if (!book.IsMartial || book.CurrentCount <= 0)
			{
				continue;
			}
			Book b = book;
			int max = b.LevelData?.Length ?? 0;
			bool open = b.CurrentMartialLevel >= 0 && b.CurrentMartialLevel < max && b.LevelData[b.CurrentMartialLevel] != null;
			int need = open ? b.LevelData[b.CurrentMartialLevel].Points : 0;
			rows.Add(new StatRow
			{
				Id = "martial:" + b.Id,
				Name = Localized(b.GetNameKey(), b.Id),
				Note = open ? "本层修炼点数，满 " + need + " 点升到下一层，和在修炼界面升层一样加上这层的加成；多的点数算到下一层。\n只能升层不能降层：游戏没有撤回加成的办法，减点只减本层点数。" : b.CurrentMartialLevel >= max && max > 0 ? "已经练满。" : "游戏里没有这一层的数据，不能改。",
				Get = () => b.CurrentLevelPoints,
				Set = open && need > 0 ? v => SetMartialPoints(b, v) : (Action<int>)null,
				// 一层只有几点的按 1 加减，点数多的按 5
				Step = need > 20 ? 5 : 1,
				NoValue = !open,
				Of = need,
				Extra = () => open ? $"第{b.CurrentMartialLevel + 1}/{max}层" : b.CurrentMartialLevel >= max && max > 0 ? "已满级" : $"第{b.CurrentMartialLevel + 1}层无数据",
			});
			if (!open && !(b.CurrentMartialLevel >= max && max > 0))
			{
				FileLogger.LogWarning($"[数值页] 功法 {b.Id} 层数 {b.CurrentMartialLevel}，共 {max} 层，这一层没有数据");
			}
		}
		return rows;
	}

	/// <summary>功法设成这么多点：每满一层用游戏的 DirectLevelUp 升一层，剩下的留在新的一层（练满就不再加）。</summary>
	private static void SetMartialPoints(Book b, int points)
	{
		points = Mathf.Max(0, points);
		while (b.CurrentMartialLevel >= 0 && b.CurrentMartialLevel < b.MaxLevel && b.LevelData[b.CurrentMartialLevel] != null)
		{
			int need = b.LevelData[b.CurrentMartialLevel].Points;
			if (need <= 0 || points < need)
			{
				b.SetMartial(b.CurrentMartialLevel, points);
				return;
			}
			points -= need;
			b.SetMartial(b.CurrentMartialLevel, 0);
			b.DirectLevelUp();
			_statsDirty = true; // 层数变了，这一段重新整理
		}
	}

	/// <summary>锻造、炼丹设成这么多点：每满一级用游戏的 Upgrade 升一级（会改相关旗标，和开发界面一样，只是不花银两），剩下的留在新的一级。</summary>
	private static void SetUpgradePoints(UpgradeItemData u, int points)
	{
		points = Mathf.Max(0, points);
		while (u.CurrentLevel >= 0 && u.CurrentLevel < u.LevelCounts && u.UpgradeLevels[u.CurrentLevel] != null)
		{
			int need = u.UpgradeLevels[u.CurrentLevel].LevelPoints;
			if (need <= 0 || points < need)
			{
				u.Setup(u.CurrentLevel, points, u.AlreadyDevelop);
				break;
			}
			points -= need;
			u.Setup(u.CurrentLevel, 0, u.AlreadyDevelop);
			u.Upgrade(need);
			_statsDirty = true;
		}
		u.UpdateStatus();
	}

	/// <summary>锻造、炼丹的开发项目：本级的开发点数，加满一级就升级，多的点数算到下一级。</summary>
	private static List<StatRow> UpgradeRows(PlayerStatManagerData player)
	{
		var rows = new List<StatRow>();
		foreach (string field in new[] { "_weaponUpgradeItems", "_poisonUpgradeItems" })
		{
			var collection = Traverse.Create(player).Field(field).GetValue<UpgradeItemCollectionData>();
			foreach (UpgradeItemData item in collection?.List ?? new List<UpgradeItemData>())
			{
				if (item == null)
				{
					continue;
				}
				UpgradeItemData u = item;
				int max = u.LevelCounts;
				bool open = u.CurrentLevel >= 0 && u.CurrentLevel < max && u.UpgradeLevels[u.CurrentLevel] != null;
				int need = open ? u.UpgradeLevels[u.CurrentLevel].LevelPoints : 0;
				string kind = field == "_weaponUpgradeItems" ? "锻造" : "炼丹";
				rows.Add(new StatRow
				{
					Id = "upgrade:" + u.Key,
					Name = Localized(u.GetNameKey(), u.Key),
					Note = open ? kind + "：本级开发点数，满 " + need + " 点升到下一级，和在" + kind + "界面升级一样；多的点数算到下一级。\n只能升级不能降级，减点只减本级点数。" : kind + "：还没开发或已经满级。",
					Get = () => u.CurrentPoints,
					Set = open && need > 0 ? v => SetUpgradePoints(u, v) : (Action<int>)null,
					Step = need > 20 ? 5 : 1,
					NoValue = !open,
					Of = need,
					Extra = () => u.CurrentLevel < 0 ? "未开发" : u.CurrentLevel >= max ? "已满级" : $"{u.CurrentLevel}/{max}级",
				});
			}
		}
		return rows;
	}

	/// <summary>门派设施（药铺、温泉……）的等级，存在旗标里，这里用设施名显示。</summary>
	private static List<StatRow> FacilityRows(PlayerStatManagerData player)
	{
		var rows = new List<StatRow>();
		var config = Traverse.Create(player).Field("_facility").GetValue<FacilityConfigData>();
		foreach (FacilityItemData facility in config?.Data ?? new FacilityItemData[0])
		{
			if (facility == null)
			{
				continue;
			}
			var flag = Traverse.Create(facility).Field("_level").GetValue<FlagData>();
			int max = (Traverse.Create(facility).Field("_levelData").GetValue() as Array)?.Length ?? 0;
			if (flag == null)
			{
				continue;
			}
			FlagData f = flag;
			rows.Add(new StatRow
			{
				Id = "facility:" + f.name,
				Name = Localized(facility.GetNameKey(), facility.name),
				Note = "设施等级（旗标 " + f.name + "），0 是还没建。",
				Get = () => f.State,
				Set = v => f.State = Mathf.Clamp(v, 0, max),
				Step = 1,
				Extra = () => "/ " + max,
			});
		}
		return rows;
	}

	private static StatRow StatRowOf(PlayerStatManagerData player, GameStatType type)
	{
		GameStat stat;
		try
		{
			stat = player.Stats.Get(type);
		}
		catch
		{
			return null;
		}
		if (stat == null)
		{
			return null;
		}
		GameStat s = stat;
		bool money = type == GameStatType.銀兩 || type == GameStatType.門派資產;
		bool small = type == GameStatType.門派規模 || type == GameStatType.愛人 || type == GameStatType.變心 || type == GameStatType.行動次數
			|| type == GameStatType.額外行動次數_1 || type == GameStatType.額外行動次數_2 || type == GameStatType.額外行動次數_3
			|| type == GameStatType.稱號 || type == GameStatType.娘化;
		return new StatRow
		{
			LockStat = type == GameStatType.心理衛生 ? s : null,
			Id = "stat:" + (int)type,
			Name = Localized(s.GetLocaleKey(), OwnNames.TryGetValue(type, out string own) ? own : TextConverter.ToSimplified(type.ToString())),
			Get = () => s.FinalValue,
			Set = v => s.AddValue(v - s.FinalValue, totalAdd: false),
			Step = money ? 500 : small ? 1 : 5,
			Extra = () =>
			{
				if (type == GameStatType.愛人)
				{
					string who = Localized("PlayerInfo/Lover/" + s.FinalValue, "");
					return who.Length > 0 ? "(" + who + ")" : "";
				}
				return s.AdditionValue != 0 ? "(" + s.AdditionValue.ToString("+0;-0") + ")" : "";
			},
		};
	}

	private static string Localized(string key, string fallback)
	{
		try
		{
			string text = LocalizationManager.Instance.LocaleResolver.GetString(key);
			if (!string.IsNullOrEmpty(text) && text != key)
			{
				return TextConverter.ToSimplified(text);
			}
		}
		catch
		{
		}
		return TextConverter.ToSimplified(fallback ?? "");
	}

	/// <summary>好感的人名：类型名（简体）；类型没起名的（同一人的几份好感）用资源名后半段。</summary>
	private static string RelationshipName(RelationshipStat stat)
	{
		string name = stat.Type.ToString();
		if (name.All(char.IsDigit))
		{
			string[] parts = stat.name.Split('_');
			name = parts.Length > 2 ? string.Join("_", parts.Skip(2).ToArray()) : stat.name;
		}
		return TextConverter.ToSimplified(name);
	}

	private readonly Dictionary<GUIStyle, GUIStyle> _oneLine = new Dictionary<GUIStyle, GUIStyle>();

	private GUIStyle OneLine(GUIStyle style)
	{
		if (!_oneLine.TryGetValue(style, out GUIStyle one))
		{
			_oneLine[style] = one = new GUIStyle(style) { wordWrap = false, clipping = TextClipping.Clip, richText = false };
		}
		return one;
	}

	/// <summary>放不下就截短加「…」。</summary>
	private static string Fit(string text, GUIStyle style, float width)
	{
		if (string.IsNullOrEmpty(text) || style.CalcSize(new GUIContent(text)).x <= width)
		{
			return text ?? "";
		}
		for (int n = text.Length - 1; n > 0; n--)
		{
			string cut = text.Substring(0, n) + "…";
			if (style.CalcSize(new GUIContent(cut)).x <= width)
			{
				return cut;
			}
		}
		return "…";
	}

	private static string ShortNote(string note)
	{
		string first = note.Split(' ')[0];
		return first.Length > 8 ? first.Substring(0, 8) + "…" : first;
	}
}
