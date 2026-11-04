using System;
using System.Collections.Generic;
using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Core;
using Mortal.Story;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 剧情分支条件提示（F8 三选一：不显示、小窗、上方提示，默认不显示）。上方提示和旗标变化提示一样用游戏的飘字，
/// 只列走到的这一支和它前面没满足的几支。小窗的说明：剧情走到分支判断（SwitchResultData.Execute）时，
/// 在屏幕左侧的小窗里列出这一轮判断的全部分支条件，满足的绿色、不满足的红色，实际走的那个分支整行高亮。
/// 「一轮」：几秒内接连发生的判断算同一轮，按发生顺序排列；隔了一段时间再有新判断，就清掉上一轮只显示新的
/// （旧的记录在骰子大师的历史记录里也能看）。一段时间没有新判断会自动收起（配置 BranchPanelAutoHide）。
/// 小窗可以拖动（位置记在配置里），也可以关掉，下次有新判断时再出现。
/// 条件文字用信息窗口同一套格式（需 X → 当前 Y）。按次数判断的分支不提示；同一判断 2 秒内重复执行只记一次。
/// </summary>
public static class BranchNotifyPatch
{
	internal class Entry
	{
		public string Title;
		public List<string> Lines = new List<string>();
		public int Taken; // 0 起算的行号，-1 表示都不满足
	}

	private const float GroupGap = 3f; // 距上一次判断超过这么多秒，就算新的一轮
	private const int MaxEntries = 20;
	private const int MaxNotices = 4; // 上方提示最多几条，多了挡画面
	internal static readonly List<Entry> Entries = new List<Entry>();
	internal static int Revision;
	internal static float LastEntryTime;

	private static string _lastName;
	private static float _lastTime;

	[HarmonyPostfix]
	[HarmonyPatch(typeof(SwitchResultData), "Execute")]
	public static void Execute_Postfix(SwitchResultData __instance, int __result)
	{
		if (ConfigManager.ShowBranchChecks.Value == 0)
		{
			return;
		}
		try
		{
			if (StoryMainUI.Instance == null || !StoryMainUI.Instance.isActiveAndEnabled)
			{
				return;
			}
			var tr = Traverse.Create(__instance);
			if (tr.Field("_useCount").GetValue<bool>())
			{
				return;
			}
			List<ConditionResultItem> items = tr.Field("_items").GetValue<List<ConditionResultItem>>();
			if (items == null || items.Count == 0)
			{
				return;
			}
			string name = __instance.name ?? "";
			if (name == _lastName && Time.unscaledTime - _lastTime < 2f)
			{
				return;
			}
			_lastName = name;
			_lastTime = Time.unscaledTime;

			string note = tr.Field("_devNote").GetValue<string>() ?? "";
			int newline = note.IndexOf('\n');
			if (newline >= 0)
			{
				note = note.Substring(0, newline);
			}
			note = TextConverter.ToSimplified(note.Trim());
			string label = note.Length > 0 ? name + "　" + note : name;
			string result = __result <= items.Count ? (items.Count > 1 ? "走第 " + __result + " 个" : "满足") : "都不满足";

			var entry = new Entry { Title = label + " · " + result, Taken = __result <= items.Count ? __result - 1 : -1 };
			var arms = new List<string>();
			for (int i = 0; i < items.Count; i++)
			{
				string conditions = DataHelper.ExtractConditions(items[i], out bool _);
				if (string.IsNullOrEmpty(conditions))
				{
					conditions = "无条件";
				}
				arms.Add(conditions);
				entry.Lines.Add((items.Count > 1 ? (i + 1) + ". " : "") + conditions);
			}
			if (ConfigManager.ShowBranchChecks.Value == 2)
			{
				int last = entry.Taken >= 0 ? entry.Taken : arms.Count - 1;
				for (int i = Math.Max(0, last - MaxNotices + 1); i <= last; i++)
				{
					FlagNotifyPatch.ShowNotice("【分支】" + arms[i]);
				}
				FileLogger.Log($"分支：{label} 共 {items.Count} 个，结果 {__result}");
				return;
			}
			if (Time.unscaledTime - LastEntryTime > GroupGap)
			{
				Entries.Clear();
			}
			Entries.Add(entry);
			if (Entries.Count > MaxEntries)
			{
				Entries.RemoveAt(0);
			}
			LastEntryTime = Time.unscaledTime;
			Revision++;
			BranchPanel.Show();
			FileLogger.Log($"分支：{label} 共 {items.Count} 个，结果 {__result}");
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("分支条件提示失败: " + e.Message);
		}
	}
}

/// <summary>分支条件小窗（IMGUI）。判断都发生在剧情里；剧情接着进战斗、回地图时小窗留着，到自动收起的时间才收起，标题画面不显示。</summary>
public class BranchPanel : MonoBehaviour
{
	private const int WindowId = 0x42524E43; // "BRNC"

	private static BranchPanel _instance;

	private bool _visible;
	private int _drawnRevision = -1;
	private Rect _rect;
	private bool _placed;
	private float _scale = -1f;
	private int _fontMode = -1;
	private GUIStyle _window;
	private GUIStyle _title;
	private GUIStyle _line;
	private GUIStyle _taken;
	private GUIStyle _close;
	private Texture2D _takenBg;
	private float _opacity = -1f;

	public static void Show()
	{
		if (_instance == null)
		{
			_instance = LOMAssistantPlugin.Instance.gameObject.AddComponent<BranchPanel>();
			// 回到标题画面才清掉；进战斗、回地图时留着，战斗前最后一句的判断进了战斗还看得到（按自动收起的秒数消失）
			SceneManager.sceneLoaded += (scene, mode) =>
			{
				if (scene.name == "Title")
				{
					BranchNotifyPatch.Entries.Clear();
				}
			};
		}
		_instance._visible = true;
	}

	private void OnGUI()
	{
		// 隐藏界面看 CG 时本 Mod 的窗口也不画
		if (UiHider.Active)
		{
			return;
		}
		float autoHide = ConfigManager.BranchPanelAutoHide.Value;
		if (autoHide > 0f && Time.unscaledTime - BranchNotifyPatch.LastEntryTime > autoHide)
		{
			return; // 一段时间没有新判断，自动收起
		}
		if (!_visible || ConfigManager.ShowBranchChecks.Value != 1 || BranchNotifyPatch.Entries.Count == 0
			|| SceneManager.GetActiveScene().name == "Title")
		{
			return;
		}
		float scale = LOMAssistant.PanelTheme.UiScale;
		if (ModFont.NeedsRebuild(scale, _scale, _fontMode) || !Mathf.Approximately(ConfigManager.PopupOpacity.Value, _opacity))
		{
			BuildStyles(scale);
		}
		if (!_placed)
		{
			float x = ConfigManager.BranchPanelX.Value;
			float y = ConfigManager.BranchPanelY.Value;
			_rect = new Rect(x >= 0f ? x * Screen.width : 20f * _scale, y >= 0f ? y * Screen.height : Screen.height * 0.18f, 620f * _scale, 10f);
			_placed = true;
		}
		// 内容变了就按新内容重算高度
		if (_drawnRevision != BranchNotifyPatch.Revision && Event.current.type == EventType.Layout)
		{
			_drawnRevision = BranchNotifyPatch.Revision;
			_rect.height = 10f;
		}
		Rect before = _rect;
		_rect = GUILayout.Window(WindowId, _rect, Draw, "", _window);
		InputGuard.Report("Branch", _rect);
		_rect.x = Mathf.Clamp(_rect.x, 0f, Screen.width - 40f);
		_rect.y = Mathf.Clamp(_rect.y, 0f, Screen.height - 40f);
		// 拖动结束（松开鼠标）时记下位置
		_moved |= before.x != _rect.x || before.y != _rect.y;
		if (_moved && Event.current.rawType == EventType.MouseUp)
		{
			SavePosition();
		}
	}

	private bool _moved;

	private void SavePosition()
	{
		_moved = false;
		ConfigManager.BranchPanelX.Value = _rect.x / Screen.width;
		ConfigManager.BranchPanelY.Value = _rect.y / Screen.height;
		ConfigManager.SetConfigValue(ConfigManager.BranchPanelX, ConfigManager.BranchPanelX.Value);
		ConfigManager.SetConfigValue(ConfigManager.BranchPanelY, ConfigManager.BranchPanelY.Value);
	}

	private void Draw(int id)
	{
		GUILayout.BeginHorizontal();
		GUILayout.Label("剧情分支", _title);
		GUILayout.FlexibleSpace();
		if (GUILayout.Button("×", _close, GUILayout.Width(32f * _scale)))
		{
			_visible = false;
		}
		GUILayout.EndHorizontal();
		foreach (BranchNotifyPatch.Entry entry in BranchNotifyPatch.Entries)
		{
			GUILayout.Space(4f * _scale);
			GUILayout.Label(entry.Title, _title);
			for (int i = 0; i < entry.Lines.Count; i++)
			{
				GUILayout.Label(entry.Lines[i], i == entry.Taken ? _taken : _line);
			}
		}
		GUI.DragWindow();
	}

	private void BuildStyles(float scale)
	{
		_scale = scale;
		_fontMode = ModFont.Mode;
		int fontSize = Mathf.Max(11, (int)(19f * scale));
		// 和其他提示框同一种样子（墨色底、宣纸色字、褐色细边），不透明度用设置里的「提示框不透明度」；
		// 走的那一行的高亮跟着按比例变化，始终看得出来
		_opacity = ConfigManager.PopupOpacity.Value;
		float alpha = PanelTheme.ToastOpacity;
		_takenBg = Solid(new Color(0.85f, 0.78f, 0.55f, Mathf.Max(0.15f, alpha * 0.4f)));
		_window = PanelTheme.Toast(scale, 19);
		_window.padding = new RectOffset((int)(12f * scale), (int)(12f * scale), (int)(8f * scale), (int)(10f * scale));
		_title = new GUIStyle(GUI.skin.label) { fontSize = fontSize, fontStyle = FontStyle.Bold, richText = true, wordWrap = true };
		ModFont.Apply(_title);
		_title.normal.textColor = PanelTheme.ToastText;
		_line = new GUIStyle(GUI.skin.label) { fontSize = fontSize, richText = true, wordWrap = true };
		ModFont.Apply(_line);
		_line.padding = new RectOffset((int)(14f * scale), 4, 2, 2);
		_line.normal.textColor = new Color(0.85f, 0.82f, 0.76f);
		_taken = new GUIStyle(_line);
		_taken.normal.background = _takenBg;
		_close = PanelTheme.ToastButton(scale, 19);
		_close.padding = new RectOffset(0, 0, 0, 0);
	}

	private static Texture2D Solid(Color color)
	{
		var texture = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
		texture.SetPixel(0, 0, color);
		texture.Apply();
		return texture;
	}
}
