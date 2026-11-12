using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Story;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 一键快进（类似 galgame 的「跳过已读」）：剧情里按一下热键（默认 F2），相当于一直按着 Ctrl，再按一下停。
/// 用的是游戏自己的快进（StoryManager.SkipDialog）：读过的对话一路跳过，碰到没读过的对话游戏会自己停下
/// （游戏设置里开了「跳过未读」就不停），到选项、掷骰时没有可跳的也就停在那里。
/// </summary>
public class SkipReadToggle : MonoBehaviour
{
	private static readonly AccessTools.FieldRef<StoryManager, bool> SkipDialog = AccessTools.FieldRefAccess<StoryManager, bool>("_skipDialog");
	private static readonly AccessTools.FieldRef<StoryManager, bool> EnableSkip = AccessTools.FieldRefAccess<StoryManager, bool>("_enableSkip");
	private static readonly AccessTools.FieldRef<StoryManager, bool> LogOpen = AccessTools.FieldRefAccess<StoryManager, bool>("_logOpen");

	/// <summary>这次快进是不是本 Mod 打开的（按着 Ctrl 的快进不显示提示）。</summary>
	private bool _ours;
	private GUIStyle _style;
	private float _scale = -1f;
	private int _fontMode = -1;

	private static bool _requested;

	/// <summary>和按一下快进键一样（手柄转轮用）：下一帧开始或停止快进。</summary>
	public static void Toggle() => _requested = true;

	public static void Ensure(GameObject host)
	{
		if (host.GetComponent<SkipReadToggle>() == null)
		{
			host.AddComponent<SkipReadToggle>();
		}
	}

	private void Update()
	{
		StoryManager story = SceneManager.GetActiveScene().name == "Story" ? StoryManager.Instance : null;
		if (story == null)
		{
			_ours = false;
			_requested = false;
			return;
		}
		bool skipping = SkipDialog(story);
		if (!skipping)
		{
			// 游戏自己停了（没读过的对话、松开 Ctrl、选项）：本 Mod 的快进也算结束
			_ours = false;
		}
		bool requested = _requested;
		_requested = false;
		if (!requested && (GUIUtility.keyboardControl != 0 || !InputGuard.HotkeyDown(ConfigManager.SkipReadKey.Value)))
		{
			return;
		}
		if (skipping)
		{
			story.SkipDialog(false);
			_ours = false;
			return;
		}
		// 和游戏按 Ctrl 时一样的条件：剧情没暂停、回想没开着、这一句允许快进
		if (story.EnableAction && !story.IsStoryPause && !LogOpen(story) && EnableSkip(story)
			&& !LOMAssistant.Features.AnytimeSave.AnytimeSave.Replaying && !LOMAssistant.Features.AnytimeSave.AnytimeSave.Restoring)
		{
			story.SkipDialog(true);
			_ours = true;
		}
	}

	private void OnGUI()
	{
		if (!_ours || UiHider.Active)
		{
			return;
		}
		float scale = LOMAssistant.PanelTheme.UiScale;
		if (ModFont.NeedsRebuild(scale, _scale, _fontMode))
		{
			_scale = scale;
			_fontMode = ModFont.Mode;
			_style = PanelTheme.Toast(scale, 20);
			_style.alignment = TextAnchor.MiddleCenter;
			_style.wordWrap = false;
		}
		var content = new GUIContent("快进已读对话中（" + InputGuard.KeyName(ConfigManager.SkipReadKey.Value) + " 停止）");
		Vector2 size = _style.CalcSize(content) + new Vector2(24f * scale, 10f * scale);
		GUI.Box(new Rect((Screen.width - size.x) / 2f, 12f * scale, size.x, size.y), content, PanelTheme.Sync(_style));
	}
}
