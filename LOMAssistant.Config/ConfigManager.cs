using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace LOMAssistant.Config;

public static class ConfigManager
{
	internal static readonly Dictionary<string, IConfigElement> ConfigElements = new Dictionary<string, IConfigElement>();

	private const string SEC_NAME = "LOMAssistant";

	public static ConfigElement<bool> EnableOptionInfo;

	public static ConfigElement<bool> EnableManualDiceSelection;

	public static ConfigElement<bool> EnableLibraryDisplay;

	public static ConfigElement<bool> EnableCombatAssist;

	public static ConfigElement<bool> EnableSkipAction;

	public static ConfigElement<bool> EnableBattleWin;

	public static ConfigElement<float> PinballScoreMultiplier;

	public static ConfigElement<int> PinballFixedScore;

	public static ConfigElement<KeyCode> PanelKey;

	public static ConfigElement<KeyCode> SaveNoteKey;

	public static ConfigElement<bool> CheatUnlimitedTraining;

	public static ConfigElement<bool> CheatUnlimitedUpgrade;

	public static ConfigElement<bool> CheatFreeUpgradeMoney;

	public static ConfigElement<bool> CheatDuelInvincible;

	public static ConfigElement<bool> CheatDuelNoDamage;

	public static ConfigElement<bool> CheatBattleInvincible;

	public static ConfigElement<bool> CheatBattleNoCooldown;

	public static ConfigElement<float> GameSpeed;

	public static ConfigElement<int> ActionMode;

	public static ConfigElement<int> ActionsPerRound;

	public static ConfigElement<bool> ShowFlagChanges;

	public static ConfigElement<bool> ShowBranchChecks;

	public static ConfigElement<bool> EnableEventPicker;

	public static ConfigElement<bool> EnableOneClickUnlock;

	public static ConfigElement<bool> EnableStoryReview;

	public static ConfigElement<KeyCode> StoryReviewKey;

	public static ConfigElement<bool> EnableRightClickHideUI;

	public static ConfigElement<bool> EnableBackgroundRun;

	public static ConfigElement<float> FlagNoticeSeconds;

	public static ConfigElement<int> FontScale;

	public static ConfigElement<KeyCode> HideUIKey;

	public static ConfigElement<bool> EnableFastDuelRounds;

	public static ConfigElement<bool> EnableCombatSkillShrink;

	public static ConfigElement<float> BranchPanelX;

	public static ConfigElement<float> BranchPanelY;

	public static ConfigElement<float> BranchPanelAutoHide;

	public static ConfigElement<float> PopupOpacity;

	public static ConfigElement<float> PanelOpacity;

	public static ConfigElement<int> SaveSlotCount;

	public static ConfigElement<bool> EnableFlowSpeedup;

	public static ConfigElement<bool> EnableFastStartup;

	public static ConfigElement<int> AutoSaveHistory;

	public static ConfigElement<bool> EnableSaveBrowser;

	public static ConfigElement<bool> EnableShopAllItems;

	public static ConfigElement<bool> EnableShopAllBooks;

	public static ConfigElement<bool> EnableDebugPanel;

	public static ConfigElement<KeyCode> DebugPanelKey;

	public static ConfigElement<bool> EnableAnytimeSave;

	public static ConfigElement<KeyCode> AnytimeSaveKey;

	public static ConfigElement<KeyCode> SkipReadKey;

	public static ConfigElement<bool> EnableSkipRead;

	public static ConfigElement<bool> BreakStatLimit;

	public static ConfigElement<string> CustomTitle;

	public static ConfigElement<int> AnytimeSaveLimit;

	public static ConfigElement<KeyCode> AnytimeLoadKey;

	public static ConfigElement<KeyCode> AnytimePanelKey;

	public static ConfigElement<KeyCode> AnytimeRollbackKey;

	public static ConfigElement<bool> LogToFile;

	public static ConfigElement<bool> EnableUpdateCheck;

	public static ConfigElement<string> LastSeenVersion;

	private static ConfigFile Config => LOMAssistantPlugin.Instance.Config;

	public static void Init()
	{
		CreateConfigElements();
		LoadConfig();
		MigrateStoryReviewKey();
		MigrateHideUIKey();
	}

	/// <summary>
	/// 隐藏界面看 CG 从「右键开关 + 另配热键（默认不绑）」改成「开关 + 一个按键（默认右键）」：
	/// 老配置里热键没绑就补成右键；以前关了右键但绑了别的键的，开关打开、只用那个键。只换一次。
	/// </summary>
	private static void MigrateHideUIKey()
	{
		try
		{
			string mark = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(ConfigManager).Assembly.Location) ?? ".", "StoryReview", "hide-ui-key");
			if (System.IO.File.Exists(mark))
			{
				return;
			}
			if (HideUIKey.Value == KeyCode.None)
			{
				HideUIKey.Value = KeyCode.Mouse1;
			}
			else if (!EnableRightClickHideUI.Value)
			{
				EnableRightClickHideUI.Value = true;
			}
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(mark));
			System.IO.File.WriteAllText(mark, "");
		}
		catch (Exception ex)
		{
			DicePlugin.LogError("隐藏界面热键迁移失败: " + ex.Message);
		}
	}

	/// <summary>
	/// 剧情回顾的热键从 F3 改成 Tab（1.6.2）：老配置里还是原来默认的 F3 就换成 Tab，玩家自己设过别的键不动。
	/// 只换一次，记在 Mod 文件夹里，之后玩家再设回 F3 也不会被改掉。
	/// </summary>
	private static void MigrateStoryReviewKey()
	{
		try
		{
			string mark = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(ConfigManager).Assembly.Location) ?? ".", "StoryReview", "hotkey-tab");
			if (System.IO.File.Exists(mark))
			{
				return;
			}
			if (StoryReviewKey.Value == KeyCode.F3)
			{
				StoryReviewKey.Value = KeyCode.Tab;
				SetConfigValue(StoryReviewKey, KeyCode.Tab);
			}
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(mark));
			System.IO.File.WriteAllText(mark, "");
		}
		catch (Exception ex)
		{
			DicePlugin.LogError("剧情回顾热键迁移失败: " + ex.Message);
		}
	}

	internal static void RegisterConfigElement<T>(ConfigElement<T> configElement)
	{
		ConfigEntry<T> entry = Config.Bind("LOMAssistant", configElement.Name, configElement.Value, configElement.Description);
		entry.SettingChanged += delegate
		{
			configElement.Value = entry.Value;
		};
		ConfigElements.Add(configElement.Name, configElement);
	}

	public static T GetConfigValue<T>(ConfigElement<T> element)
	{
		if (!Config.TryGetEntry("LOMAssistant", element.Name, out ConfigEntry<T> entry))
		{
			throw new Exception("Could not get config entry '" + element.Name + "'");
		}
		return entry.Value;
	}

	public static void SetConfigValue<T>(ConfigElement<T> element, T value)
	{
		if (Config.TryGetEntry("LOMAssistant", element.Name, out ConfigEntry<T> entry))
		{
			entry.Value = value;
		}
		else
		{
			DicePlugin.LogInfo("Could not get config entry '" + element.Name + "'");
		}
	}

	public static void LoadConfig()
	{
		foreach (KeyValuePair<string, IConfigElement> configElement in ConfigElements)
		{
			ConfigDefinition key = new ConfigDefinition("LOMAssistant", configElement.Key);
			if (Config.ContainsKey(key))
			{
				ConfigEntryBase configEntryBase = Config[key];
				if (configEntryBase != null)
				{
					configElement.Value.BoxedValue = configEntryBase.BoxedValue;
				}
			}
		}
	}

	public static void SaveConfig()
	{
		Config.Save();
	}

	private static void CreateConfigElements()
	{
		EnableOptionInfo = new ConfigElement<bool>("EnableOptionInfo", "是否启用选项提示（选项结果、解锁条件）、地点事件提示和回想里的判定记录。true=开，false=关，改后重启生效", defaultValue: true);
		EnableManualDiceSelection = new ConfigElement<bool>("EnableManualDiceSelection", "是否启用骰子手动选择（点击线路直接选中）。true=开，false=关，改后重启生效", defaultValue: true);
		EnableLibraryDisplay = new ConfigElement<bool>("EnableLibraryDisplay", "是否启用讲经堂解锁显示。true=开，false=关，改后重启生效", defaultValue: true);
		EnableCombatAssist = new ConfigElement<bool>("EnableCombatAssist", "是否启用战斗辅助（ESC界面增加直接获胜按钮）。true=开，false=关，改后重启生效", defaultValue: true);
		EnableSkipAction = new ConfigElement<bool>("EnableSkipAction", "是否启用空过（养成界面 ESC 菜单增加“空过”按钮，消耗 1 次行动、不触发任何事件）。true=开，false=关，改后重启生效", defaultValue: true);
		EnableBattleWin = new ConfigElement<bool>("EnableBattleWin", "是否启用团战直接获胜（团战暂停界面增加“直接获胜”按钮）。true=开，false=关，改后重启生效", defaultValue: true);
		PinballScoreMultiplier = new ConfigElement<float>("PinballScoreMultiplier", "弹珠小游戏得分倍率（每次加分乘以该值，扣分不受影响）。1=原版，2=双倍", 1f);
		PinballFixedScore = new ConfigElement<int>("PinballFixedScore", "弹珠小游戏固定总分：离开小游戏时总分直接改为该值（影响剧情判定）。-1=关闭", -1);
		PanelKey = new ConfigElement<KeyCode>("PanelKey", "打开咖啡伴侣窗口「设置」页的热键", KeyCode.F8);
		SaveNoteKey = new ConfigElement<KeyCode>("SaveNoteKey", "存档备注：在存档/读档界面选中手动存档后按这个键写备注（和设置页热键相同时，在存档界面优先写备注）", KeyCode.F8);
		CheatUnlimitedTraining = new ConfigElement<bool>("CheatUnlimitedTraining", "无限修炼次数：修炼时可用次数和武学点用完自动补满，可以一直修炼。true=开，false=关", defaultValue: false);
		CheatUnlimitedUpgrade = new ConfigElement<bool>("CheatUnlimitedUpgrade", "无限锻造、炼丹次数：可用次数和锻造点、炼丹点用完自动补满，可以一直锻造、炼丹。true=开，false=关", defaultValue: false);
		CheatFreeUpgradeMoney = new ConfigElement<bool>("CheatFreeUpgradeMoney", "锻造、炼丹不消耗银两，银两不够也能升级。true=开，false=关", defaultValue: false);
		CheatDuelInvincible = new ConfigElement<bool>("CheatDuelInvincible", "决斗中无敌：我方不受伤害。true=开，false=关", defaultValue: false);
		CheatDuelNoDamage = new ConfigElement<bool>("CheatDuelNoDamage", "决斗中造成伤害为 0：敌方不受伤害。true=开，false=关", defaultValue: false);
		CheatBattleInvincible = new ConfigElement<bool>("CheatBattleInvincible", "团战中我方（主角和同伴）不会受伤：挨打、中毒都不扣血，敌人照常受伤。true=开，false=关", defaultValue: false);
		CheatBattleNoCooldown = new ConfigElement<bool>("CheatBattleNoCooldown", "团战绝招没有冷却：放完马上又能用。true=开，false=关", defaultValue: false);
		GameSpeed = new ConfigElement<float>("GameSpeed", "游戏倍速（在游戏原本速度上再乘以该值；1=原速）", 1f);
		ActionMode = new ConfigElement<int>("ActionMode", "养成行动点：0=正常，1=不消耗（扣到剩 1 点后不再扣，可一直行动；用「空过」结束本旬），2=每旬固定 N 点（N 见 ActionsPerRound）", 0);
		ActionsPerRound = new ConfigElement<int>("ActionsPerRound", "行动点选「每旬N点」时，每旬固定几点（1～5，原版每旬 3 点）。有的选项要「剩余行动点大于 2」才出现，设成 3 点以上就能在旬初触发", 1);
		ShowFlagChanges = new ConfigElement<bool>("ShowFlagChanges", "剧情改动旗标时在屏幕上提示（可能剧透）。true=开，false=关", defaultValue: false);
		EnableEventPicker = new ConfigElement<bool>("EnableEventPicker", "随机事件自选：剧情走到随机事件（工作、闲逛等）时先停下，列出满足条件的事件选一个，也可以选随机。true=开，false=关", defaultValue: false);
		EnableFastDuelRounds = new ConfigElement<bool>("EnableFastDuelRounds", "决斗回合之间的等待缩短：身上状态（buff）多时回合间不再干等几秒，效果和结算不变。默认开；只有怀疑它导致问题时才关。true=开，false=关", defaultValue: true);
		EnableCombatSkillShrink = new ConfigElement<bool>("EnableCombatSkillShrink", "技能表全显示：战斗时左上角的「技能、特殊」列表条目太多会超出屏幕，开启后超过 14 条自动分第二列、超过 28 条分第三列（每列最多 14 条，先填满左边再填下一列），再按屏幕高度等比缩小，保证全部显示。true=开，false=关", defaultValue: true);
		EnableOneClickUnlock = new ConfigElement<bool>("EnableOneClickUnlock", "被锁住的选项旁显示「一键设置」：把没满足的解锁条件（属性、好感、旗标）改到满足，选项当场解锁。true=开，false=关", defaultValue: true);
		FlagNoticeSeconds = new ConfigElement<float>("FlagNoticeSeconds", "剧情旗标变化提示在屏幕上停留几秒（按真实时间，开倍速也一样；原版 1 秒）。0=按游戏原样", 4f);
		FontScale = new ConfigElement<int>("FontScale", "剧情对白的文字大小（百分比，只放大对白框里的字，不放大界面）。100=原样，120=大两成", 100);
		EnableBackgroundRun = new ConfigElement<bool>("EnableBackgroundRun", "后台继续运行：切到别的窗口时游戏不暂停，背景音乐也不停。true=开，false=关（游戏原本的做法：切走就停）", defaultValue: true);
		EnableRightClickHideUI = new ConfigElement<bool>("EnableRightClickHideUI", "隐藏界面看 CG：剧情里按热键（HideUIKey，默认鼠标右键）隐藏对话框等界面和本 Mod 的窗口，只看背景、CG 和立绘，方便截图；再按一次恢复。true=开，false=关", defaultValue: true);
		CustomTitle = new ConfigElement<string>("CustomTitle", "自定义称号：对话框名字上方、人物介绍、属性页显示的称号换成这个（空=照游戏原样）。在数值页「称号」里改", "");
		BreakStatLimit = new ConfigElement<bool>("BreakStatLimit", "突破属性上限：体力、内力、轻功、学问、嘴力、心相、性情、处世、修养、道德、阴阳、命运、魅力、厨艺、抗毒、抗麻原本最高 100，打开后上限改成 9999；关掉后超过的部分会在下次变化时截回。true=开，false=关", defaultValue: false);
		EnableSkipRead = new ConfigElement<bool>("EnableSkipRead", "一键快进已读对话：剧情里按一下热键（默认 F2）一直跳过读过的对话，再按一下停，碰到没读过的对话自己停。true=开，false=关", defaultValue: true);
		SkipReadKey = new ConfigElement<KeyCode>("SkipReadKey", "一键快进的热键：剧情里按一下一直跳过读过的对话（像一直按着 Ctrl），再按一下停；碰到没读过的对话会自己停", KeyCode.F2);
		HideUIKey = new ConfigElement<KeyCode>("HideUIKey", "隐藏界面看 CG 的按键，默认鼠标右键（Mouse1）；习惯右键做别的事可以改成键盘上的键，如 H", KeyCode.Mouse1);
		EnableStoryReview = new ConfigElement<bool>("EnableStoryReview", "剧情回顾：走到大节点（任务、事件的开头，以及会改变剧情走向的选项）时，在存档文件夹里收录这一段开头的进度（LOMStory_ 开头，跟着 Steam 云同步），以后可以从这里重玩。true=开，false=关", defaultValue: true);
		StoryReviewKey = new ConfigElement<KeyCode>("StoryReviewKey", "咖啡伴侣窗口（此刻、路线图、剧情图、节点存档、随时存档、结局速查、数值、设置）的热键", KeyCode.Tab);
		ShowBranchChecks = new ConfigElement<bool>("ShowBranchChecks", "剧情走到分支判断时在屏幕上列出各分支条件（满足为绿、不满足为红，可能剧透）。true=开，false=关", defaultValue: false);
		BranchPanelX = new ConfigElement<float>("BranchPanelX", "剧情分支小窗的位置（占屏幕宽度的比例，-1=默认左侧）。拖动小窗会自动保存", -1f);
		BranchPanelY = new ConfigElement<float>("BranchPanelY", "剧情分支小窗的位置（占屏幕高度的比例，-1=默认）。拖动小窗会自动保存", -1f);
		BranchPanelAutoHide = new ConfigElement<float>("BranchPanelAutoHide", "剧情分支小窗多少秒没有新判断就自动收起（0=不自动收起）", 15f);
		PanelOpacity = new ConfigElement<float>("PanelOpacity", "咖啡伴侣窗口的背景不透明度（0.3～1，1=不透明）。调低能透过窗口看到游戏画面，文字和按钮不变", 1f);
		PopupOpacity = new ConfigElement<float>("PopupOpacity", "剧情分支小窗的背景不透明度（0=完全透明，1=不透明），改后重启游戏生效", 0.5f);
		EnableFastStartup = new ConfigElement<bool>("EnableFastStartup", "开游戏更快：标题画面按钮浮现的开场动画加快 6 倍。true=开（默认），false=关，改后重启生效", defaultValue: true);
		EnableFlowSpeedup = new ConfigElement<bool>("EnableFlowSpeedup", "流程提速：切场景不再写多余的全局存档副本、读档时旗标查表、回想关着时不重排、快进时指令不空等、数值变化后的停顿缩到 0.2 秒（没显示的不停顿）、养成地图上行动后的等待和转场加快 3 倍。默认开；只有怀疑它导致问题时才关。true=开，false=关", defaultValue: true);
		SaveSlotCount = new ConfigElement<int>("SaveSlotCount", "手动存档栏位数量（原版 20，小于 20 时按 20）", 100);
		AutoSaveHistory = new ConfigElement<int>("AutoSaveHistory", "每类自动存档（养成/剧情/战斗）额外保留的旧档数量，0=不保留", 5);
		EnableSaveBrowser = new ConfigElement<bool>("EnableSaveBrowser", "新版存档、读档界面：左边列表（心上人头像、备注、备注和删除按钮），右边详情（心上人立绘、任务、读档后的对白）。true=开（默认），false=用游戏原来的界面，改后重启生效", defaultValue: true);
		EnableShopAllBooks = new ConfigElement<bool>("EnableShopAllBooks", "四师兄商店售卖全部功法（包括各门派、线路限定、平时买不到的功法秘笈；没拥有的才卖；不卖《证类本草》和剧情里拿不到的松风剑法、天师剑法、四平枪谱）。true=开，false=关", defaultValue: false);
		EnableShopAllItems = new ConfigElement<bool>("EnableShopAllItems", "四师兄商店售卖全部物品（书籍、杂物、特殊物品；不卖《证类本草》，提前买会导致六月后山事件出 bug）。true=开，false=关", defaultValue: false);
		EnableDebugPanel = new ConfigElement<bool>("EnableDebugPanel", "是否启用数值面板热键（F9 打开「数值」页：属性、好感、门派、天赋、旗标）。true=开，false=关", defaultValue: true);
		DebugPanelKey = new ConfigElement<KeyCode>("DebugPanelKey", "打开数值面板的热键，如 F9 / P 等", KeyCode.F9);
		EnableAnytimeSave = new ConfigElement<bool>("EnableAnytimeSave", "是否启用随时存档（剧情台词、选项、掷骰和养成界面都能存，每次存一个新档，不占原版存档栏）。true=开，false=关，改后重启生效", defaultValue: true);
		AnytimeSaveLimit = new ConfigElement<int>("AnytimeSaveLimit", "随时存档最多保留几个，超过就自动删最旧的", 100);
		AnytimeSaveKey = new ConfigElement<KeyCode>("AnytimeSaveKey", "随时存档：存一个新的随时存档的热键", KeyCode.F5);
		AnytimeLoadKey = new ConfigElement<KeyCode>("AnytimeLoadKey", "随时存档：读取最近一个随时存档的热键", KeyCode.F7);
		AnytimePanelKey = new ConfigElement<KeyCode>("AnytimePanelKey", "打开咖啡伴侣窗口的随时存档页（列表、读取、备注、删除）的热键", KeyCode.F6);
		AnytimeRollbackKey = new ConfigElement<KeyCode>("AnytimeRollbackKey", "回退到剧情判定前一句的热键（只在内存里保留最近几个判定点，切场景后清空）", KeyCode.F4);
		LogToFile = new ConfigElement<bool>("LogToFile", "是否写入独立日志文件。日志位置：BepInEx\\plugins\\LOMAssistant\\logs\\", defaultValue: true);
		EnableUpdateCheck = new ConfigElement<bool>("EnableUpdateCheck", "进游戏后联网（GitHub）看有没有新版本，有就在咖啡伴侣窗口右上角用小字提示，不弹窗。true=开（默认），false=关", defaultValue: true);
		LastSeenVersion = new ConfigElement<string>("LastSeenVersion", "上次看过更新提示的版本（自动记录，不用改）", "");
	}
}
