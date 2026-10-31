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


	public static ConfigElement<int> PinballFixedScore;

	public static ConfigElement<KeyCode> PanelKey;

	public static ConfigElement<KeyCode> SaveNoteKey;

	public static ConfigElement<bool> CheatUnlimitedTraining;

	public static ConfigElement<bool> CheatUnlimitedUpgrade;

	public static ConfigElement<bool> CheatFreeUpgradeMoney;
	public static ConfigElement<bool> CheatDevelopIgnoreStory;

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

	public static ConfigElement<bool> ForceUnmetEvents;

	public static ConfigElement<bool> ForceUnmetOptions;

	public static ConfigElement<bool> EnableBranchAhead;
	public static ConfigElement<bool> BranchAheadAutoSnapshot;
	public static ConfigElement<int> BranchAheadSnapshots;
	public static ConfigElement<bool> BranchAheadHint;
	public static ConfigElement<bool> BranchAheadStopSkip;
	public static ConfigElement<bool> EnableMapHints;
	public static ConfigElement<bool> ShowItemUses;
	public static ConfigElement<bool> MapHintsNowOnly;

	public static ConfigElement<bool> EnableOneClickUnlock;

	public static ConfigElement<bool> EnableStoryReview;

	public static ConfigElement<KeyCode> StoryReviewKey;

	public static ConfigElement<int> GamepadPanelButton;

	public static ConfigElement<bool> EnableRightClickHideUI;

	public static ConfigElement<bool> EnableBackgroundRun;

	public static ConfigElement<float> FlagNoticeSeconds;

	public static ConfigElement<int> FontScale;
	public static ConfigElement<int> GameFontScale;
	public static ConfigElement<int> AiProvider;
	public static ConfigElement<KeyCode> AiPanelKey;
	public static ConfigElement<string> AiBaseUrl;
	public static ConfigElement<string> AiModel;
	public static ConfigElement<string> AiApiKey;
	public static ConfigElement<int> AiSpoiler;
	public static ConfigElement<bool> AiHints;
	public static ConfigElement<bool> AiDebugLog;
	public static ConfigElement<bool> AiDeepMode;
	public static ConfigElement<bool> AiFastMode;
	public static ConfigElement<string> AiDeepModel;

	public static ConfigElement<int> PanelFont;
	public static ConfigElement<int> PanelScale;

	public static ConfigElement<int> PanelTheme;

	public static ConfigElement<int> UiLanguage;

	public static ConfigElement<KeyCode> HideUIKey;


	public static ConfigElement<bool> EnableDuelActionForecast;

	public static ConfigElement<float> DuelForecastX;

	public static ConfigElement<float> DuelForecastY;

	public static ConfigElement<float> DuelForecastWidth;

	public static ConfigElement<float> DuelForecastHeight;

	public static ConfigElement<bool> EnableSkillColumns;

	public static ConfigElement<float> BranchPanelX;

	public static ConfigElement<float> BranchPanelY;

	public static ConfigElement<float> BranchPanelAutoHide;

	public static ConfigElement<float> PopupOpacity;

	public static ConfigElement<float> PanelOpacity;
	public static ConfigElement<float> DialogOpacity;

	public static ConfigElement<int> SaveSlotCount;
	public static ConfigElement<int> SaveListOrder;
	public static ConfigElement<int> TitlePartnerChoice;
	public static ConfigElement<int> LegendSlotPages;

	public static ConfigElement<bool> EnableFlowSpeedup;

	public static ConfigElement<bool> EnableFastStartup;

	public static ConfigElement<int> AutoSaveHistory;

	public static ConfigElement<bool> EnableSaveBrowser;

	public static ConfigElement<bool> EnableShopAllItems;
	public static ConfigElement<bool> EnableStoryJump;


	public static ConfigElement<bool> EnableDebugPanel;

	public static ConfigElement<KeyCode> DebugPanelKey;

	public static ConfigElement<bool> EnableAnytimeSave;

	public static ConfigElement<KeyCode> AnytimeSaveKey;

	public static ConfigElement<KeyCode> SkipReadKey;

	public static ConfigElement<KeyCode> SkipActionKey;

	public static ConfigElement<bool> EnableSkipRead;

	public static ConfigElement<bool> BreakStatLimit;

	public static ConfigElement<string> CustomTitle;
	public static ConfigElement<string> EventRules;

	public static ConfigElement<string> LockedStats;

	public static ConfigElement<string> PresetCustom;

	public static ConfigElement<bool> PresetChooserShown;
	public static ConfigElement<int> OptionResultMode;
	public static ConfigElement<bool> ColorOptionDeltas;

	public static ConfigElement<int> AnytimeSaveLimit;

	public static ConfigElement<KeyCode> AnytimeLoadKey;

	public static ConfigElement<KeyCode> AnytimePanelKey;

	public static ConfigElement<KeyCode> AnytimeRollbackKey;

	public static ConfigElement<bool> LogToFile;

	public static ConfigElement<bool> EnableUpdateCheck;

	public static ConfigElement<string> LastSeenVersion;

	private static ConfigFile Config => TestConfig ?? LOMAssistantPlugin.Instance.Config;

	/// <summary>不开游戏跑测试时用的配置文件（tests/）；游戏里一直是空，用插件自己的配置。</summary>
	internal static ConfigFile TestConfig;

	public static void Init()
	{
		// 配置文件已经有了、却还没有选预设弹窗这一项：是从 2.x 更新上来的老玩家，不弹
		bool updated = false;
		try
		{
			updated = System.IO.File.Exists(Config.ConfigFilePath) && !System.IO.File.ReadAllText(Config.ConfigFilePath).Contains("PresetChooserShown");
		}
		catch (Exception)
		{
		}
		CreateConfigElements();
		LoadConfig();
		if (updated)
		{
			PresetChooserShown.Value = true;
		}
		MigrateStoryReviewKey();
		MigrateHideUIKey();
		MigrateHotkeyToggles();
		MigrateAnytimeLimit();
	}

	/// <summary>随时存档上限的默认值从 100 改成不限（0）：还是旧默认值 100 的改成 0，自己改过的不动。只换一次。</summary>
	private static void MigrateAnytimeLimit()
	{
		try
		{
			string mark = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(ConfigManager).Assembly.Location) ?? ".", "StoryReview", "anytime-limit");
			if (System.IO.File.Exists(mark))
			{
				return;
			}
			if (AnytimeSaveLimit.Value == 100)
			{
				AnytimeSaveLimit.Value = 0;
			}
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(mark));
			System.IO.File.WriteAllText(mark, "");
		}
		catch (Exception ex)
		{
			DicePlugin.LogError("随时存档上限迁移失败: " + ex.Message);
		}
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
	/// 隐藏界面看 CG、一键快进、数值面板热键以前各有一个开关，现在热键能设成不绑定，开关不再用：
	/// 以前关掉的，对应热键改成不绑定，开关打开（配置文件里留着也不起作用）。只换一次。
	/// </summary>
	private static void MigrateHotkeyToggles()
	{
		try
		{
			string mark = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(ConfigManager).Assembly.Location) ?? ".", "StoryReview", "hotkey-toggles");
			if (System.IO.File.Exists(mark))
			{
				return;
			}
			foreach (var (toggle, key) in new[] { (EnableRightClickHideUI, HideUIKey), (EnableSkipRead, SkipReadKey), (EnableDebugPanel, DebugPanelKey) })
			{
				if (!toggle.Value)
				{
					key.Value = KeyCode.None;
					toggle.Value = true;
				}
			}
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(mark));
			System.IO.File.WriteAllText(mark, "");
		}
		catch (Exception ex)
		{
			DicePlugin.LogError("热键开关迁移失败: " + ex.Message);
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
		PinballFixedScore = new ConfigElement<int>("PinballFixedScore", "弹珠小游戏固定总分：离开小游戏时总分直接改为该值（影响剧情判定）。-1=关闭", -1);
		PanelKey = new ConfigElement<KeyCode>("PanelKey", "打开咖啡伴侣窗口「设置」页的热键", KeyCode.F8);
		SaveNoteKey = new ConfigElement<KeyCode>("SaveNoteKey", "存档备注：在存档/读档界面选中手动存档后按这个键写备注（和设置页热键相同时，在存档界面优先写备注）", KeyCode.F8);
		CheatUnlimitedTraining = new ConfigElement<bool>("CheatUnlimitedTraining", "无限修炼：修炼时不消耗可用次数和武学点（武学点是 0 时修炼不了，数值页能改）。true=开，false=关", defaultValue: true);
		CheatUnlimitedUpgrade = new ConfigElement<bool>("CheatUnlimitedUpgrade", "无限锻造、炼丹：锻造、炼丹时不消耗可用次数和锻造点、炼丹点（点数是 0 时做不了，数值页能改）。true=开，false=关", defaultValue: true);
		CheatDevelopIgnoreStory = new ConfigElement<bool>("CheatDevelopIgnoreStory", "锻冶场、炼丹房开发新装备、新药时不看剧情条件（还没走到解锁它的剧情也能开发）；等级和门派规模的要求照旧。true=开，false=关", defaultValue: false);
		CheatFreeUpgradeMoney = new ConfigElement<bool>("CheatFreeUpgradeMoney", "锻造、炼丹不消耗银两，银两不够也能升级。true=开，false=关", defaultValue: true);
		CheatDuelInvincible = new ConfigElement<bool>("CheatDuelInvincible", "决斗中无敌：我方不受伤害。true=开，false=关", defaultValue: false);
		CheatDuelNoDamage = new ConfigElement<bool>("CheatDuelNoDamage", "决斗中造成伤害为 0：敌方不受伤害。true=开，false=关", defaultValue: false);
		CheatBattleInvincible = new ConfigElement<bool>("CheatBattleInvincible", "团战中我方（主角和同伴）不会受伤：挨打、中毒都不扣血，敌人照常受伤。true=开，false=关", defaultValue: false);
		CheatBattleNoCooldown = new ConfigElement<bool>("CheatBattleNoCooldown", "团战绝招没有冷却：放完马上又能用。true=开，false=关", defaultValue: false);
		GameSpeed = new ConfigElement<float>("GameSpeed", "游戏倍速（在游戏原本速度上再乘以该值；1=原速）", 1f);
		ActionMode = new ConfigElement<int>("ActionMode", "养成行动点：0=正常，1=不消耗（扣到剩 1 点后不再扣，可一直行动；用「空过」结束本旬），2=每旬固定 N 点（N 见 ActionsPerRound）", 0);
		ActionsPerRound = new ConfigElement<int>("ActionsPerRound", "行动点选「每旬N点」时，每旬固定几点（1～5，原版每旬 3 点）。有的选项要「剩余行动点大于 2」才出现，设成 3 点以上就能在旬初触发", 1);
		ShowFlagChanges = new ConfigElement<bool>("ShowFlagChanges", "剧情改动旗标时在屏幕上提示（可能剧透）。true=开，false=关", defaultValue: false);
		EnableMapHints = new ConfigElement<bool>("EnableMapHints", "养成地图事件提示：鼠标停在建筑上列出这里的主线、支线、随机事件和触发条件；有现在就能触发、这一局还没走过的主线或支线的建筑一直亮着。true=开，false=关", defaultValue: true);
		ShowItemUses = new ConfigElement<bool>("ShowItemUses", "物品用途：背包里选中贵重品、杂物、书，或在商店里看商品时，说明后面列出哪些剧情会判定要不要有它、会把它收走（可能剧透任务名）。true=开，false=关", defaultValue: false);
		MapHintsNowOnly = new ConfigElement<bool>("MapHintsNowOnly", "地图建筑事件提示只列现在点进去就会触发的主线、支线，时间不对、条件不满足的只写个数；随机事件本来就只列现在能抽到的。true=开，false=关", defaultValue: false);
		EnableEventPicker = new ConfigElement<bool>("EnableEventPicker", "随机事件自选：剧情走到随机事件（工作、闲逛等）时先停下，列出满足条件的事件选一个，也可以选随机。true=开，false=关", defaultValue: false);
		ForceUnmetEvents = new ConfigElement<bool>("ForceUnmetEvents", "随机事件自选里也列出出现条件没满足的事件，点了无视条件直接触发（不改旗标，之后的剧情可能对不上）。true=开，false=关", defaultValue: false);
		ForceUnmetOptions = new ConfigElement<bool>("ForceUnmetOptions", "条件不满足就不出现的选项也列出来（红字标「条件没满足」，悬停看条件），点了直接选；锁住的选项条件改不到时给「强行解锁」（都不改旗标，之后的剧情可能对不上）。true=开，false=关", defaultValue: false);
		EnableBranchAhead = new ConfigElement<bool>("EnableBranchAhead", "判定前选分支：剧情走到属性、好感、旗标的分支判断（条件里没有随机值的那种）时先停下，列出每一支的条件和当前值、这一支后面的对白看过没，选一支就把数值改成走那一条路；不想改就按空格、点窗口外面或按 Esc 照现在这样判定。true=开，false=关", defaultValue: false);
		BranchAheadAutoSnapshot = new ConfigElement<bool>("BranchAheadAutoSnapshot", "判定前选分支：停下等你选的时候顺手存一个随时存档（备注「【分支判定】判定前 · 判定名」）。看完一支想走另一支，从「随时存档」读回来就行，不用从头再走一遍；只清自己存的这几张，你自己的存档一张都不动。true=开，false=关", defaultValue: true);
		BranchAheadSnapshots = new ConfigElement<int>("BranchAheadSnapshots", "判定前选分支：判定前自动存档保留几张（1～10）。留 1 张就只能退回最近这一处判定，留 2 张以上连续过两处判定时也退得回去。只清理本 Mod 自己存的那几张", 2);
		BranchAheadHint = new ConfigElement<bool>("BranchAheadHint", "判定前选分支：一处判定所有支都已经满足、换别的支也没有别的对白或去处时，不弹窗，只在屏幕下方说一句「已满足 · 改了只少一句」。true=说，false=什么都不说", defaultValue: true);
		BranchAheadStopSkip = new ConfigElement<bool>("BranchAheadStopSkip", "判定前选分支：正按着 Ctrl（或按了 F2 一键快进）时撞上判定窗，先把快进停住，看清了再决定；松开再按 Ctrl 才会继续快进。true=停，false=让快进把窗口带过去", defaultValue: true);
		EnableDuelActionForecast = new ConfigElement<bool>("EnableDuelActionForecast", "决斗中显示敌方本回合预选的行动；敌方意图较高时可能根据你的出招临场应变。默认关。true=开，false=关", defaultValue: false);
		DuelForecastX = new ConfigElement<float>("DuelForecastX", "决斗敌方行动预测窗口的横向位置（占屏幕宽度的比例，-1=默认靠右）。拖动窗口会自动保存", -1f);
		DuelForecastY = new ConfigElement<float>("DuelForecastY", "决斗敌方行动预测窗口的纵向位置（占屏幕高度的比例，-1=默认居中）。拖动窗口会自动保存", -1f);
		DuelForecastWidth = new ConfigElement<float>("DuelForecastWidth", "决斗敌方行动预测窗口的宽度（占屏幕宽度的比例，-1=默认）。调整窗口大小会自动保存", -1f);
		DuelForecastHeight = new ConfigElement<float>("DuelForecastHeight", "决斗敌方行动预测窗口的高度（占屏幕高度的比例，-1=按内容自适应）。调整窗口大小会自动保存", -1f);
		EnableSkillColumns = new ConfigElement<bool>("EnableSkillColumns", "技能表全显示：战斗时左上角我方的「技能、特殊」列表太长、会压到血条时分列显示（先填满左边再填下一列）。true=开，false=关", defaultValue: true);
		EnableOneClickUnlock = new ConfigElement<bool>("EnableOneClickUnlock", "被锁住的选项旁显示「一键设置」：把没满足的解锁条件（属性、好感、旗标）改到满足，选项当场解锁。true=开，false=关", defaultValue: true);
		FlagNoticeSeconds = new ConfigElement<float>("FlagNoticeSeconds", "剧情旗标变化提示在屏幕上停留几秒（按真实时间，开倍速也一样；原版 1 秒）。0=按游戏原样", 4f);
		FontScale = new ConfigElement<int>("FontScale", "剧情对白的文字大小（百分比，只放大对白框里的字，乘在「游戏文字大小」上）。100=原样，120=大两成", 100);
		GameFontScale = new ConfigElement<int>("GameFontScale", "游戏里全部文字的大小（百分比，菜单、按钮、数值、对白都放大，适合 Steam Deck 等小屏幕；放得太大有的字会挤出框）。100=原样，120=大两成", 100);
		AiPanelKey = new ConfigElement<KeyCode>("AiPanelKey", "打开咖啡伴侣窗口的 AI 页并把光标放进输入框的热键（已经在 AI 页时再按一次关上）", KeyCode.F3);
		AiProvider = new ConfigElement<int>("AiProvider", "AI 攻略用哪家服务商（序号，在咖啡伴侣窗口「AI」页里选）", 0);
		AiBaseUrl = new ConfigElement<string>("AiBaseUrl", "AI 服务商的 API 地址（选服务商时自动填）", "https://api.deepseek.com/v1");
		AiModel = new ConfigElement<string>("AiModel", "AI 用的模型名（选服务商时自动填建议的，可以改）", "deepseek-flash");
		AiApiKey = new ConfigElement<string>("AiApiKey", "AI 服务商的 API Key：只存在这台电脑的配置文件里，只发给选的服务商", "");
		AiDeepMode = new ConfigElement<bool>("AiDeepMode", "AI 深度模式：换用更强的模型（AiDeepModel），回答更长，答前把结论对照查到的资料核对一遍。更准，也更贵、更慢", defaultValue: false);
		AiFastMode = new ConfigElement<bool>("AiFastMode", "AI 极速模式：少查几次资料、提示词更短、不让模型多想，免费额度和本地小模型也能较快回答；答案更粗略。和深度模式只能开一个", defaultValue: false);
		AiDeepModel = new ConfigElement<string>("AiDeepModel", "深度模式用的模型名；空着就和平时用同一个模型", "deepseek-v4-pro");
		AiDebugLog = new ConfigElement<bool>("AiDebugLog", "把每次发给 AI 的人设、现状、查资料的结果和用量写进 Mod 文件夹的 logs/ai_debug.txt，方便核对", defaultValue: false);
		AiHints = new ConfigElement<bool>("AiHints", "女主角人设问到攻略时，在回答后面单独给一段攻略提示；关掉就只用她的口吻聊天", defaultValue: true);
		AiSpoiler = new ConfigElement<int>("AiSpoiler", "AI 剧透程度：0=想知道什么说什么，1=只说当前和走过的剧情，2=只给方向提示", 0);
		UiLanguage = new ConfigElement<int>("UiLanguage", "咖啡伴侣窗口等 Mod 界面的语言：0=跟随游戏（装了日语补丁时显示日语，装了英文补丁时显示英语），1=简体中文，2=繁体中文，3=韩语，4=日语，5=英语", 0);
		PanelTheme = new ConfigElement<int>("PanelTheme", "咖啡伴侣窗口的配色：0=浅色（宣纸底），1=深色（墨色底，默认），2=跟随系统（Windows 的应用模式）", 1);
		PanelFont = new ConfigElement<int>("PanelFont", "咖啡伴侣窗口等 Mod 界面的字体：0=思源宋体（游戏对话用的字体，默认），1=微软雅黑（系统字体）", 0);
		PanelScale = new ConfigElement<int>("PanelScale", "咖啡伴侣窗口等 Mod 界面的文字大小（百分比，窗口里的字和按钮一起放大，适合 Steam Deck 等小屏幕）。100=原样，130=大三成", 100);
		// 2.8.3 起默认关（开着时切到别的窗口，背景音乐不停，鼠标经过地图建筑还会响，玩家以为是 bug）。
		// 老的配置名 EnableBackgroundRun 在玩家配置里存着 true，换个名字让所有人都从「关」开始
		EnableBackgroundRun = new ConfigElement<bool>("KeepRunningInBackground", "后台继续运行：切到别的窗口时游戏不暂停，背景音乐也不停。true=开，false=关（默认，游戏原本的做法：切走就停）", defaultValue: false);
		EnableRightClickHideUI = new ConfigElement<bool>("EnableRightClickHideUI", "隐藏界面看 CG：剧情里按热键（HideUIKey，默认鼠标右键）隐藏对话框等界面和本 Mod 的窗口，只看背景、CG 和立绘，方便截图；再按一次、点左键或按其他键恢复。已不用：不想要就把 HideUIKey 设成 None", defaultValue: true);
		PresetChooserShown = new ConfigElement<bool>("PresetChooserShown", "第一次打开咖啡伴侣窗口时的选预设弹窗已经出过（自动记录，不用改）", defaultValue: false);
		PresetCustom = new ConfigElement<string>("PresetCustom", "设置页「预设」里「自定义」记下的开关（从自定义换到别的预设时自动记录，点「自定义」换回来）：配置名=值，分号隔开", "");
		LockedStats = new ConfigElement<string>("LockedStats", "数值页锁住的数值（在数值页点锁，自动记录）：数值名，逗号隔开。清空就全部解锁", "");
		EventRules = new ConfigElement<string>("EventRules", "随机事件的「总是」「不出」（在随机事件选择框里点，自动记录）：事件编号=1 总是触发、=2 不出，分号隔开。清空就全部取消", "");
		OptionResultMode = new ConfigElement<int>("OptionResultMode", "对话选项后面的后果说明（选了会怎样、解锁条件）：0=总是显示（默认），1=鼠标停在选项上（或手柄选中）才显示，2=不显示", 0);
		ColorOptionDeltas = new ConfigElement<bool>("ColorOptionDeltas", "选项后果里的加减上色：加的绿色、减的红色（对话选项、掷骰、悬停提示、咖啡伴侣窗口）。关掉后和其他文字同色。true=开，false=关", defaultValue: true);
		CustomTitle = new ConfigElement<string>("CustomTitle", "自定义称号：对话框名字上方、人物介绍、属性页显示的称号换成这个（空=照游戏原样）。在数值页「称号」里改", "");
		BreakStatLimit = new ConfigElement<bool>("BreakStatLimit", "突破属性上限：体力、内力、轻功、学问、嘴力、心相、性情、处世、修养、道德、阴阳、命运、魅力、厨艺、抗毒、抗麻，刀剑、暗器、拳掌，儒学、道学、释学原本最高 100，打开后上限改成 9999；关掉后超过的部分会在下次变化时截回。true=开，false=关", defaultValue: false);
		EnableSkipRead = new ConfigElement<bool>("EnableSkipRead", "一键快进已读对话：剧情里按一下热键（默认 F2）一直跳过读过的对话，再按一下停，碰到没读过的对话自己停。已不用：不想要就把 SkipReadKey 设成 None", defaultValue: true);
		SkipReadKey = new ConfigElement<KeyCode>("SkipReadKey", "一键快进的热键：剧情里按一下一直跳过读过的对话（像一直按着 Ctrl），再按一下停；碰到没读过的对话会自己停", KeyCode.F2);
		SkipActionKey = new ConfigElement<KeyCode>("SkipActionKey", "空过的热键：养成地图上、剧情里的自由行动菜单里按一下，和 ESC 菜单的「空过」一样消耗 1 次行动（要先开着「空过」按钮）。默认不绑定", KeyCode.None);
		HideUIKey = new ConfigElement<KeyCode>("HideUIKey", "隐藏界面看 CG 的按键，默认鼠标右键（Mouse1）；习惯右键做别的事可以改成键盘上的键，如 H", KeyCode.Mouse1);
		EnableStoryReview = new ConfigElement<bool>("EnableStoryReview", "剧情回顾：游戏在剧情存档点、每旬开头自动存档时，在存档文件夹里收录这一刻的进度（LOMStory_ 开头，跟着 Steam 云同步），以后可以从这里重玩。true=开，false=关", defaultValue: true);
		GamepadPanelButton = new ConfigElement<int>("GamepadPanelButton", "手柄开关咖啡伴侣窗口的按键：0=按下左摇杆（L3），1=按下右摇杆（R3），2=LB 和 RB 一起按", 0);
		StoryReviewKey = new ConfigElement<KeyCode>("StoryReviewKey", "咖啡伴侣窗口（此刻、路线图、剧情图、节点存档、随时存档、收集进度、数值、设置）的热键", KeyCode.Tab);
		ShowBranchChecks = new ConfigElement<bool>("ShowBranchChecks", "剧情走到分支判断时在屏幕上列出各分支条件（满足为绿、不满足为红，可能剧透）。true=开，false=关", defaultValue: false);
		BranchPanelX = new ConfigElement<float>("BranchPanelX", "剧情分支小窗的位置（占屏幕宽度的比例，-1=默认左侧）。拖动小窗会自动保存", -1f);
		BranchPanelY = new ConfigElement<float>("BranchPanelY", "剧情分支小窗的位置（占屏幕高度的比例，-1=默认）。拖动小窗会自动保存", -1f);
		BranchPanelAutoHide = new ConfigElement<float>("BranchPanelAutoHide", "剧情分支小窗多少秒没有新判断就自动收起（0=不自动收起）", 15f);
		PanelOpacity = new ConfigElement<float>("PanelOpacity", "咖啡伴侣窗口的背景不透明度（0.3～1，1=不透明）。调低能透过窗口看到游戏画面，文字和按钮不变。默认 0.8", 0.8f);
		PopupOpacity = new ConfigElement<float>("PopupOpacity", "提示框的底色不透明度（0.3～1，1=不透明）：地图建筑提示、剧情分支小窗、决斗敌方预选、快进、读档进度这些浮在游戏画面上的提示。调低能透过提示框看到游戏画面，文字不变", 0.8f);
		DialogOpacity = new ConfigElement<float>("DialogOpacity", "小窗的底色不透明度（0.3～1，1=不透明）：随机事件选择、存档备注。调低能透过小窗看到游戏画面，文字和按钮不变", 1f);
		EnableFastStartup = new ConfigElement<bool>("EnableFastStartup", "开游戏更快：标题画面按钮浮现的开场动画加快 6 倍。true=开（默认），false=关，改后重启生效", defaultValue: true);
		EnableFlowSpeedup = new ConfigElement<bool>("EnableFlowSpeedup", "流程提速：切场景不再写多余的全局存档副本、读档时旗标查表、快进时指令不空等、数值变化后的停顿缩到 0.2 秒（没显示的不停顿）、养成地图上行动后的等待和转场加快 3 倍、决斗回合之间不干等。默认开；只有怀疑它导致问题时才关。true=开，false=关", defaultValue: true);
		TitlePartnerChoice = new ConfigElement<int>("TitlePartnerChoice", "标题画面显示的人物：0=跟随结局（默认），1=不显示，2=小师妹，3=龙湘，4=叶云裳，5=小兰，6=小梅，7=小竹，8=大师兄。回到标题画面时生效", 0);
		SaveListOrder = new ConfigElement<int>("SaveListOrder", "新版存档、读档界面里手动存档的排列：0=按栏位号（默认），1=最近保存的在上面，2=按栏位号、读档页也列出所有空栏位（点空栏位在那一格开新游戏，同原版）。打开界面时都会选中并滚到上次保存的那个存档", 0);
		SaveSlotCount = new ConfigElement<int>("SaveSlotCount", "手动存档栏位数量（原版 20，小于 20 时按 20）", 100);
		LegendSlotPages = new ConfigElement<int>("LegendSlotPages", "传奇栏位页数：保存传奇和图鉴的传奇界面每页 12 格，多于 1 页时下面出现翻页按钮（原版 1 页）", 5);
		AutoSaveHistory = new ConfigElement<int>("AutoSaveHistory", "每类自动存档（养成/剧情/战斗）额外保留的旧档数量，0=不保留", 5);
		EnableSaveBrowser = new ConfigElement<bool>("EnableSaveBrowser", "新版存档、读档界面：左边列表（心上人头像、备注、备注和删除按钮），右边详情（心上人立绘、任务、读档后的对白）。true=开（默认），false=用游戏原来的界面，改后重启生效", defaultValue: true);
		EnableShopAllItems = new ConfigElement<bool>("EnableShopAllItems", "四师兄商店售卖全部物品（书籍、杂物、特殊物品；不卖《证类本草》，提前买会导致六月后山事件出 bug）。true=开，false=关", defaultValue: false);
		EnableStoryJump = new ConfigElement<bool>("EnableStoryJump", "剧情跳转：剧情图、路线图里选中一段剧情，可以「跳到这里」直接从那一段开演（包括还没走到的），中间的旗标、任务进度按剧本补上。补剧情、收集分支用，正常游玩不建议。true=开（默认），false=关", defaultValue: true);
		EnableDebugPanel = new ConfigElement<bool>("EnableDebugPanel", "数值面板热键（F9 打开「数值」页）。已不用：不想要就把 DebugPanelKey 设成 None", defaultValue: true);
		DebugPanelKey = new ConfigElement<KeyCode>("DebugPanelKey", "打开数值面板的热键，如 F9 / P 等", KeyCode.F9);
		EnableAnytimeSave = new ConfigElement<bool>("EnableAnytimeSave", "是否启用随时存档（剧情台词、选项、掷骰和养成界面都能存，每次存一个新档，不占原版存档栏）。true=开，false=关，改后重启生效", defaultValue: true);
		AnytimeSaveLimit = new ConfigElement<int>("AnytimeSaveLimit", "随时存档最多保留几个（有备注的不算，也不删）：下次存新的随时存档时，超出的旧存档会一次删掉；0=不限（默认），不自动删", 0);
		AnytimeSaveKey = new ConfigElement<KeyCode>("AnytimeSaveKey", "随时存档：存一个新的随时存档的热键", KeyCode.F5);
		AnytimeLoadKey = new ConfigElement<KeyCode>("AnytimeLoadKey", "随时存档：读取最近一个随时存档的热键", KeyCode.F7);
		AnytimePanelKey = new ConfigElement<KeyCode>("AnytimePanelKey", "打开咖啡伴侣窗口的随时存档页（列表、读取、备注、删除）的热键", KeyCode.F6);
		AnytimeRollbackKey = new ConfigElement<KeyCode>("AnytimeRollbackKey", "回退到剧情判定前一句的热键（只在内存里保留最近几个判定点，切场景后清空）", KeyCode.F4);
		LogToFile = new ConfigElement<bool>("LogToFile", "是否写入独立日志文件。日志位置：BepInEx\\plugins\\LOMAssistant\\logs\\", defaultValue: true);
		EnableUpdateCheck = new ConfigElement<bool>("EnableUpdateCheck", "进游戏后联网（GitHub）看有没有新版本，有就在咖啡伴侣窗口右上角用小字提示，不弹窗。true=开（默认），false=关", defaultValue: true);
		LastSeenVersion = new ConfigElement<string>("LastSeenVersion", "上次看过更新提示的版本（自动记录，不用改）", "");
	}
}
