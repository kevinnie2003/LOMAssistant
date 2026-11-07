using HarmonyLib;
using LOMAssistant.Config;
using LOMAssistant.Features.Story;
using Mortal.Core;
using UnityEngine;
using UnityEngine.EventSystems;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 设置页热键（默认 F8）：打开咖啡伴侣窗口的「设置」页（以前的 F8 小面板已经并进设置页）。
/// 存档备注热键（默认也是 F8）：在存档/读档界面选中了手动存档时，直接给这个存档写备注。
/// 另外每帧跑倍速、后台运行、对白字号这几个开关，检测到冲突 Mod 时自动打开设置页把提示给玩家看。
/// </summary>
public class ModPanel : MonoBehaviour
{
	private bool _conflictShown;

	// 玩家最后选中的存档栏（点 Mod 窗口会清掉 uGUI 的选中，所以记下来）
	private MonoBehaviour _noteSlot;

	private void Update()
	{
		TrackSelectedSlot();
		if (!SaveNoteEditor.IsOpen)
		{
			// 存档备注键先看：在存档界面选中了手动存档就写备注（和设置页同一个键时，这里优先）
			MonoBehaviour slot = InputGuard.HotkeyDown(ConfigManager.SaveNoteKey.Value) ? GetNoteSlot() : null;
			if (slot != null)
			{
				SaveNoteEditor.Open(Traverse.Create(slot).Field("_slot").GetValue<string>(), () => Traverse.Create(slot).Method("Setup").GetValue());
			}
			else if (InputGuard.HotkeyDown(ConfigManager.PanelKey.Value))
			{
				StoryReviewPanel.ToggleSettings();
			}
		}
		GameSpeed.Tick();
		BackgroundRun.Tick();
		FontScale.Tick();
		if (InputGuard.HotkeyDown(ConfigManager.AiPanelKey.Value))
		{
			StoryReviewPanel.ToggleAi();
		}
		// 检测到新的冲突时自动打开一次设置页，玩家不按键也能看到；提示过的不再打开，红字留在设置页
		if (ConflictDetector.HasNewWarning && !_conflictShown && StoryReviewPanel.OpenSettings())
		{
			_conflictShown = true;
			ConflictDetector.RememberWarnings();
		}
	}

	private void TrackSelectedSlot()
	{
		GameObject selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
		if (selected == null)
		{
			return;
		}
		MonoBehaviour slot = (MonoBehaviour)selected.GetComponentInParent<SaveSlotPanel>()
			?? (MonoBehaviour)selected.GetComponentInParent<LoadSlotPanel>()
			?? selected.GetComponentInParent<RecentSaveSlotPanel>();
		if (slot != null)
		{
			_noteSlot = slot;
		}
	}

	// 记下的存档栏：所在界面还开着、而且是有内容的手动存档才算
	private MonoBehaviour GetNoteSlot()
	{
		if (_noteSlot == null || !_noteSlot.isActiveAndEnabled)
		{
			return null;
		}
		var tr = Traverse.Create(_noteSlot);
		if (!SaveNotePatch.IsManualSlot(tr.Field("_slot").GetValue<string>()) || tr.Field("_currentData").GetValue() == null)
		{
			return null;
		}
		return _noteSlot;
	}
}
