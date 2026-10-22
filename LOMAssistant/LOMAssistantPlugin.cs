using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.Mono;
using HarmonyLib;
using LOMAssistant.Config;
using LOMAssistant.Features.Combat;
using LOMAssistant.Features.AnytimeSave;
using LOMAssistant.Features.Dice;
using LOMAssistant.Features.Extras;
using LOMAssistant.Features.Library;
using Mortal.Story;
using UnityEngine;

namespace LOMAssistant;

[BepInPlugin("LOMAssistant", "咖啡伴侣", "2.8.0")]
public class LOMAssistantPlugin : BaseUnityPlugin
{
	private Harmony _harmony;

	public static LOMAssistantPlugin Instance { get; private set; }

	public static ManualLogSource LogSource { get; private set; }

	private void Awake()
	{
		Instance = this;
		LogSource = base.Logger;
		FileLogger.Init(Path.GetDirectoryName(base.Info.Location));
		ConfigManager.Init();
		BepInExConsole.Hide();
		LogInfo("咖啡伴侣已加载。");
		FileLogger.Log("咖啡伴侣已加载。");
		Diagnostics.LogStartup(base.Info);
		_harmony = new Harmony(base.Info.Metadata.GUID);
		Diagnostics.PatchSteam(_harmony);
		ApplyPatches();
		BepInEx5Plugins.Load();
		Diagnostics.LogPatchSummary(_harmony);
		StartCoroutine(ConflictDetector.CheckLater(_harmony));
		StartCoroutine(UpdateInfo.Check());
	}

	private void Update()
	{
		if (InputGuard.HotkeyDown(ConfigManager.DebugPanelKey.Value))
		{
			LOMAssistant.Features.Story.StoryReviewPanel.ToggleStats();
		}
	}

	private void OnDestroy()
	{
		if (_harmony != null)
		{
			_harmony.UnpatchSelf();
		}
	}

	private void EnsurePatched(string name, MethodBase method, string prefixName)
	{
		if (method == null)
		{
			FileLogger.LogError("[Init] " + name + " method NOT FOUND via AccessTools!");
			LogError(name + " 方法未找到，骰子手动选择功能可能无法工作！");
			return;
		}
		int valueOrDefault = (Harmony.GetPatchInfo(method)?.Prefixes?.Count).GetValueOrDefault();
		FileLogger.Log("[Init] " + name + " patch status: prefixes=" + valueOrDefault);
		LogInfo(name + " 补丁状态: prefixes=" + valueOrDefault);
		if (valueOrDefault == 0)
		{
			FileLogger.LogWarning("[Init] " + name + " NOT patched by PatchAll, manually patching...");
			LogWarning(name + " 未通过 PatchAll 应用，尝试手动补丁...");
			HarmonyMethod prefix = new HarmonyMethod(typeof(DiceManualSelectionPatch), prefixName);
			_harmony.Patch(method, prefix);
			int valueOrDefault2 = (Harmony.GetPatchInfo(method)?.Prefixes?.Count).GetValueOrDefault();
			FileLogger.Log("[Init] " + name + " manual patch done, prefixes=" + valueOrDefault2);
			LogInfo(name + " 手动补丁完成: prefixes=" + valueOrDefault2);
		}
	}

	// 每组补丁单独打：一组失败（例如某些环境下找不到目标方法）只记日志，不影响后面其他功能。
	private void TryPatchAll(Type type)
	{
		try
		{
			_harmony.PatchAll(type);
		}
		catch (Exception ex)
		{
			LogError(type.Name + " 补丁应用失败，该功能不可用，其他功能不受影响: " + ex.Message);
			FileLogger.LogError(type.Name + " 补丁应用失败: " + ex);
		}
	}

	private void ApplyPatches()
	{
		try
		{
			if (ConfigManager.EnableOptionInfo.Value)
			{
				TryPatchAll(typeof(OptionInfoPatch));
				LogInfo("选项信息补丁已应用。");
			}
			if (ConfigManager.EnableManualDiceSelection.Value)
			{
				TryPatchAll(typeof(DiceManualSelectionPatch));
				LogInfo("骰子手动选择补丁已应用。");
				EnsurePatched("Roll", AccessTools.Method(typeof(DiceMenuDialog), "Roll"), "Roll_Prefix");
				EnsurePatched("CheckRevolution", AccessTools.Method(typeof(DiceMenuDialog), "CheckRevolution"), "CheckRevolution_Prefix");
			}
			if (ConfigManager.EnableLibraryDisplay.Value)
			{
				TryPatchAll(typeof(LibraryDisplayPatch));
				LogInfo("讲经堂显示补丁已应用。");
			}
			if (ConfigManager.EnableCombatAssist.Value)
			{
				TryPatchAll(typeof(CombatAssistPatch));
				LogInfo("战斗辅助补丁已应用。");
			}
			if (ConfigManager.EnableSkipAction.Value)
			{
				TryPatchAll(typeof(SkipActionPatch));
				LogInfo("空过补丁已应用。");
			}
			if (ConfigManager.EnableBattleWin.Value)
			{
				TryPatchAll(typeof(BattleWinPatch));
				LogInfo("团战直接获胜补丁已应用。");
			}
			TryPatchAll(typeof(PinballScorePatch));
			LogInfo("弹珠分数补丁已应用。");
			TryPatchAll(typeof(TrainingCheatPatch));
			TryPatchAll(typeof(TrainingCheatPatch.MartialCountPatch));
			TryPatchAll(typeof(TrainingCheatPatch.UpgradeCountPatch));
			if (base.gameObject.GetComponent<ModPanel>() == null)
			{
				base.gameObject.AddComponent<ModPanel>();
				InputGuard.Ensure();
			}
			LogInfo("修炼/锻造/炼丹修改补丁已应用。");
			TryPatchAll(typeof(DuelCheatPatch));
			TryPatchAll(typeof(BattleCheatPatch));
			TryPatchAll(typeof(DuelPacePatch));
			DuelActionForecast.Ensure(base.gameObject);
			TryPatchAll(typeof(DuelActionForecastPatch));
			TryPatchAll(typeof(SkillColumnsPatch));
			TryPatchAll(typeof(CombatStackTipPatch));
			TryPatchAll(typeof(LOMAssistant.Features.Story.LegendExportButton));
			TryPatchAll(typeof(FlowSpeedup.UniverseJsonPatch));
			TryPatchAll(typeof(FlowSpeedup.FlagIndexPatch));
			TryPatchAll(typeof(FlowSpeedup.LogPatch));
			TryPatchAll(typeof(FlowSpeedup.StatPausePatch));
			TryPatchAll(typeof(FlowSpeedup.SkipPatch));
			LogInfo("决斗修改补丁已应用。");
			TryPatchAll(typeof(SaveSlotPatch));
			LogInfo("存档栏位补丁已应用。");
			TryPatchAll(typeof(SaveNotePatch));
			LogInfo("存档备注补丁已应用。");
			TryPatchAll(typeof(AutoSaveHistoryPatch));
			TryPatchAll(typeof(SaveDataCache));
			TryPatchAll(typeof(SaveDataCache.GetSaveDataPatch));
			if (ConfigManager.EnableSaveBrowser.Value)
			{
				TryPatchAll(typeof(SaveBrowser));
			}
			TryPatchAll(typeof(ActionModePatch));
			TryPatchAll(typeof(FlagNotifyPatch));
			TryPatchAll(typeof(LuaLoadTiming));
			TryPatchAll(typeof(BranchNotifyPatch));
			TryPatchAll(typeof(EventPickerPatch));
			TryPatchAll(typeof(OneClickUnlockPatch));
			EventPicker.Ensure(base.gameObject);
			if (ConfigManager.EnableNoJealousyEvent.Value)
			{
				TryPatchAll(typeof(LOMAssistant.Features.Story.NoJealousyEventPatch));
				LogInfo("取消修罗场事件补丁已应用。");
			}
			UiHider.Ensure(base.gameObject);
			SkipReadToggle.Ensure(base.gameObject);
			MapEventHints.Ensure(base.gameObject);
			TryPatchAll(typeof(MapEventHints));
			TryPatchAll(typeof(StatLimitPatch));
			TryPatchAll(typeof(TitlePatch));
			TryPatchAll(typeof(DisplayTextPatch));
			TryPatchAll(typeof(GamepadControlsChangedGate));
			TryPatchAll(typeof(MoodLockPatch));
			ConfigManager.BreakStatLimit.OnValueChanged += _ => StatLimitPatch.Apply();
			StatLimitPatch.Apply();
			FastStartup.Install(this);
			TryPatchAll(typeof(LOMAssistant.Features.Story.StoryNodePatches));
			LOMAssistant.Features.Story.StoryReviewPanel.Ensure(base.gameObject);
			TryPatchAll(typeof(LoadClickGuard));
			TryPatchAll(typeof(SettingsDiagnosticsButton));
			TryPatchAll(typeof(InputGuardActionGate));
			TryPatchAll(typeof(InputGuardDialogGate));
			TryPatchAll(typeof(InputGuardNavigationGate));
			ConfigManager.ActionsPerRound.OnValueChanged += _ => ActionModePatch.LimitThisRound();
			LogInfo("行动点模式补丁已应用（当前：" + ActionModePatch.Label(ActionModePatch.Mode) + "）。");
			LogInfo("自动存档历史补丁已应用。");
			TryPatchAll(typeof(ShopAllItemsPatch));
			TryPatchAll(typeof(BackgroundRun));
			TryPatchAll(typeof(FontScale));
			LogInfo($"四师兄商店补丁已应用（售卖全部物品：{(ConfigManager.EnableShopAllItems.Value ? "开" : "关")}）。");
			if (ConfigManager.EnableAnytimeSave.Value && AnytimeSave.Install(base.gameObject))
			{
				TryPatchAll(typeof(AnytimeSavePatches));
				LogInfo("随时存档补丁已应用。");
			}
		}
		catch (Exception ex2)
		{
			LogError("应用补丁失败: " + ex2);
			FileLogger.LogError("应用补丁失败: " + ex2);
		}
	}

	public static void LogInfo(object message)
	{
		if (LogSource != null)
		{
			LogSource.LogInfo(message);
		}
	}

	public static void LogError(object message)
	{
		if (LogSource != null)
		{
			LogSource.LogError(message);
		}
	}

	public static void LogWarning(object message)
	{
		if (LogSource != null)
		{
			LogSource.LogWarning(message);
		}
	}
}
