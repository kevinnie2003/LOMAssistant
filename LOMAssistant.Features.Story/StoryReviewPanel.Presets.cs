using System.Collections.Generic;
using System.Linq;
using LOMAssistant.Config;
using UnityEngine;

namespace LOMAssistant.Features.Story;

/// <summary>
/// 设置页最上面的「预设」：原汁原味、辅助、进阶、全开四档，一次改好剧情提示、养成战斗辅助和作弊类开关。
/// 只动这些开关；存档、提速、界面、热键等设置不变。当前设置正好是哪一档就高亮哪一档，都不是就高亮「自定义」。
/// 从自定义换到别的预设时，先把这些开关记进配置 PresetCustom，点「自定义」能换回来。
/// 点一下按钮变成「确定？」，几秒内再点一次才换，免得误点把自己调好的设置冲掉。
/// </summary>
public partial class StoryReviewPanel
{
	private sealed class Preset
	{
		public string Name;
		public string Tagline = ""; // 适合谁，弹窗里单独一行高亮
		public string Note;
		public (IConfigElement Element, object Value)[] Values;
	}

	private static Preset[] _presets;

	private int _presetConfirm = -1;
	private float _presetConfirmUntil;

	// 作弊类：无限修炼锻造、不花银两从「辅助」起打开，其余只有「全开」打开（「决斗敌方不受伤」「突破属性上限」哪一档都不开）
	private static (IConfigElement, object)[] Cheats(bool training, bool on) => new (IConfigElement, object)[]
	{
		(ConfigManager.BreakStatLimit, false),
		(ConfigManager.CheatUnlimitedTraining, training),
		(ConfigManager.CheatUnlimitedUpgrade, training),
		(ConfigManager.CheatFreeUpgradeMoney, training),
		(ConfigManager.CheatDevelopIgnoreStory, on),
		(ConfigManager.CheatDuelInvincible, on),
		(ConfigManager.CheatDuelNoDamage, false),
		(ConfigManager.CheatBattleInvincible, on),
		(ConfigManager.CheatBattleNoCooldown, on),
	};

	private static Preset[] Presets => _presets ??= new[]
	{
		new Preset
		{
			Name = "原汁原味",
			Tagline = "建议新玩家选择",
			Note = "关掉选项提示、骰子和随机事件自选、地图提示、直接获胜、剧情跳转等辅助和全部作弊，只留随时存档、节点存档、提速这些不影响游戏的功能",
			Values = new (IConfigElement, object)[]
			{
				(ConfigManager.ShowBranchChecks, false),
				(ConfigManager.ShowFlagChanges, false),
				(ConfigManager.EnableEventPicker, false),
				(ConfigManager.ForceUnmetEvents, false),
				(ConfigManager.ForceUnmetOptions, false),
				(ConfigManager.EnableBranchPicker, false),
				(ConfigManager.EnableOneClickUnlock, false),
				(ConfigManager.EnableOptionInfo, false),
				(ConfigManager.ShowItemUses, false),
				(ConfigManager.ShowReadMarks, false),
				(ConfigManager.BattleCompanions, false),
				(ConfigManager.BattleMorale, false),
				(ConfigManager.BattleFeel, false),
				(ConfigManager.OptionResultMode, 2),
				(ConfigManager.EnableManualDiceSelection, false),
				(ConfigManager.EnableMapHints, false),
				(ConfigManager.DeadlineReminder, false),
				(ConfigManager.EnableSkipAction, false),
				(ConfigManager.EnableCombatAssist, false),
				(ConfigManager.EnableBattleWin, false),
				(ConfigManager.EnableDuelActionForecast, false),
				(ConfigManager.EnableLibraryDisplay, 0),
				(ConfigManager.EnableShopAllItems, false),
				(ConfigManager.EnableStoryJump, false),
			}.Concat(Cheats(false, false)).ToArray(),
		},
		new Preset
		{
			Name = "辅助",
			Tagline = "建议多周目玩家选择",
			Note = "选项后果、骰子自选、地图提示、空过、直接获胜按钮、无限修炼锻造和不花银两打开；分支小窗、旗标提示、随机事件自选、剧情跳转和其他作弊关闭",
			Values = new (IConfigElement, object)[]
			{
				(ConfigManager.ShowBranchChecks, false),
				(ConfigManager.ShowFlagChanges, false),
				(ConfigManager.EnableEventPicker, false),
				(ConfigManager.ForceUnmetEvents, false),
				(ConfigManager.ForceUnmetOptions, false),
				(ConfigManager.EnableBranchPicker, false),
				(ConfigManager.EnableOneClickUnlock, true),
				(ConfigManager.EnableOptionInfo, true),
				(ConfigManager.ShowItemUses, false),
				(ConfigManager.ShowReadMarks, false),
				(ConfigManager.BattleCompanions, false),
				(ConfigManager.BattleMorale, true),
				(ConfigManager.BattleFeel, true),
				(ConfigManager.OptionResultMode, 0),
				(ConfigManager.EnableManualDiceSelection, true),
				(ConfigManager.EnableMapHints, true),
				(ConfigManager.DeadlineReminder, true),
				(ConfigManager.EnableSkipAction, true),
				(ConfigManager.EnableCombatAssist, true),
				(ConfigManager.EnableBattleWin, true),
				(ConfigManager.EnableDuelActionForecast, false),
				(ConfigManager.EnableLibraryDisplay, 1),
				(ConfigManager.EnableShopAllItems, false),
				(ConfigManager.EnableStoryJump, false),
			}.Concat(Cheats(true, false)).ToArray(),
		},
		// 介于「辅助」和「全开」之间：比「全开」少了分支判定手动选择、强制触发未满足条件的事件和选项，以及会让战斗、商店失去意义的作弊
		new Preset
		{
			Name = "进阶",
			Tagline = "建议补全剧情的老玩家选择",
			Note = "打开分支小窗、旗标提示、随机事件自选、剧情跳转等剧情辅助，不开分支判定手动选择和强制触发未满足条件的事件和选项；作弊只开无限修炼锻造和不花银两；讲经堂只提示达成条件，四师兄不卖全部物品",
			Values = new (IConfigElement, object)[]
			{
				(ConfigManager.ShowBranchChecks, true),
				(ConfigManager.ShowFlagChanges, true),
				(ConfigManager.EnableEventPicker, true),
				(ConfigManager.ForceUnmetEvents, false),
				(ConfigManager.ForceUnmetOptions, false),
				(ConfigManager.EnableBranchPicker, false),
				(ConfigManager.EnableOneClickUnlock, true),
				(ConfigManager.EnableOptionInfo, true),
				(ConfigManager.ShowItemUses, true),
				(ConfigManager.ShowReadMarks, true),
				(ConfigManager.BattleCompanions, true),
				(ConfigManager.BattleMorale, true),
				(ConfigManager.BattleFeel, true),
				(ConfigManager.OptionResultMode, 0),
				(ConfigManager.EnableManualDiceSelection, true),
				(ConfigManager.EnableMapHints, true),
				(ConfigManager.DeadlineReminder, true),
				(ConfigManager.EnableSkipAction, true),
				(ConfigManager.EnableCombatAssist, true),
				(ConfigManager.EnableBattleWin, true),
				(ConfigManager.EnableDuelActionForecast, true),
				(ConfigManager.EnableLibraryDisplay, 1),
				(ConfigManager.EnableShopAllItems, false),
				(ConfigManager.EnableStoryJump, true),
			}.Concat(Cheats(true, false)).ToArray(),
		},
		new Preset
		{
			Name = "全开",
			Tagline = "建议想收集全部剧情分支的玩家选择",
			Note = "在「进阶」之上再打开分支判定手动选择、强制触发未满足条件的事件和选项、四师兄售卖全部物品，讲经堂全部解锁，以及战斗无敌等作弊",
			Values = new (IConfigElement, object)[]
			{
				(ConfigManager.ShowBranchChecks, true),
				(ConfigManager.ShowFlagChanges, true),
				(ConfigManager.EnableEventPicker, true),
				(ConfigManager.ForceUnmetEvents, true),
				(ConfigManager.ForceUnmetOptions, true),
				(ConfigManager.EnableBranchPicker, true),
				(ConfigManager.EnableOneClickUnlock, true),
				(ConfigManager.EnableOptionInfo, true),
				(ConfigManager.ShowItemUses, true),
				(ConfigManager.ShowReadMarks, true),
				(ConfigManager.BattleCompanions, true),
				(ConfigManager.BattleMorale, true),
				(ConfigManager.BattleFeel, true),
				(ConfigManager.OptionResultMode, 0),
				(ConfigManager.EnableManualDiceSelection, true),
				(ConfigManager.EnableMapHints, true),
				(ConfigManager.DeadlineReminder, true),
				(ConfigManager.EnableSkipAction, true),
				(ConfigManager.EnableCombatAssist, true),
				(ConfigManager.EnableBattleWin, true),
				(ConfigManager.EnableDuelActionForecast, true),
				(ConfigManager.EnableLibraryDisplay, 2),
				(ConfigManager.EnableShopAllItems, true),
				(ConfigManager.EnableStoryJump, true),
			}.Concat(Cheats(true, true)).ToArray(),
		},
	};

	private static int Differences(Preset preset) => preset.Values.Count(v => !Equals(v.Element.BoxedValue, v.Value));

	// 预设管到的全部开关
	private static IEnumerable<IConfigElement> PresetElements => Presets.SelectMany(p => p.Values).Select(v => v.Element).Distinct();

	private static void SaveCustom()
	{
		ConfigManager.PresetCustom.Value = string.Join(";", PresetElements.Select(e => e.Name + "=" + e.BoxedValue));
	}

	/// <summary>记下的自定义设置；没记过时为空。</summary>
	private static Preset Custom()
	{
		var values = new List<(IConfigElement, object)>();
		foreach (string pair in (ConfigManager.PresetCustom.Value ?? "").Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries))
		{
			string[] kv = pair.Split('=');
			IConfigElement element = kv.Length == 2 ? PresetElements.FirstOrDefault(e => e.Name == kv[0]) : null;
			if (element?.BoxedValue is bool && bool.TryParse(kv[1], out bool b))
			{
				values.Add((element, b));
			}
			else if (element?.BoxedValue is int && int.TryParse(kv[1], out int n))
			{
				values.Add((element, n));
			}
		}
		return values.Count == 0 ? null : new Preset { Name = "自定义", Note = "换回从自定义换到别的预设之前的开关", Values = values.ToArray() };
	}

	/// <summary>设置页最上面一行：预设按钮，当前是哪一档。</summary>
	private void DrawPresets()
	{
		GUIStyle line = Middle(_label);
		GUILayout.BeginHorizontal();
		GUILayout.Label("预设", line, GUILayout.Width(S(60)), Ctl());
		int current = -1;
		for (int i = 0; i < Presets.Length; i++)
		{
			if (Differences(Presets[i]) == 0)
			{
				current = i;
			}
		}
		if (_presetConfirm >= 0 && Time.realtimeSinceStartup > _presetConfirmUntil)
		{
			_presetConfirm = -1;
		}
		// 最后一个是「自定义」：不是哪一档时就是它
		Preset custom = Custom();
		if (current < 0)
		{
			current = Presets.Length;
		}
		for (int i = 0; i <= Presets.Length; i++)
		{
			Preset preset = i < Presets.Length ? Presets[i] : custom;
			if (preset == null)
			{
				// 没记过自定义：只在当前不是哪一档时亮着，点了没用
				GUIStyle only = i == current ? _primary : _button;
				GUILayout.Button("自定义", only, GUILayout.Width(TextWidth("自定义", only, S(120))), Ctl());
				Hover(GUILayoutUtility.GetLastRect(), "改了预设管的任意一个开关，就是自定义");
				continue;
			}
			int changes = Differences(preset);
			bool confirming = _presetConfirm == i;
			string text = confirming ? string.Format("确定？改 {0} 项", changes) : preset.Name;
			GUIStyle style = i == current || confirming ? _primary : _button;
			if (GUILayout.Button(text, style, GUILayout.Width(TextWidth(text, style, S(120))), Ctl()) && i != current && changes > 0)
			{
				if (confirming)
				{
					if (current == Presets.Length && i < Presets.Length)
					{
						SaveCustom();
					}
					foreach (var (element, value) in preset.Values)
					{
						element.BoxedValue = value;
					}
					_presetConfirm = -1;
					Notice(string.Format("已换成「{0}」，改了 {1} 项", preset.Name, changes));
				}
				else
				{
					_presetConfirm = i;
					_presetConfirmUntil = Time.realtimeSinceStartup + 4f;
				}
			}
			Hover(GUILayoutUtility.GetLastRect(), (preset.Tagline.Length > 0 ? preset.Tagline + "\n" : "") + preset.Note + "\n存档、提速、界面、热键等其他设置不变");
		}
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
	}

	/// <summary>
	/// 新玩家第一次打开咖啡伴侣窗口时，在窗口中间弹出几档预设让玩家选；选了或点「以后再说」都不再弹。
	/// 从 2.x 更新上来的老玩家不弹（ConfigManager.Init 里直接记成弹过）。
	/// </summary>
	private void DrawPresetChooser()
	{
		if (Event.current.type == EventType.Repaint)
		{
			Fill(new Rect(0, 0, _windowRect.width, _windowRect.height), new Color(0.1f, 0.08f, 0.05f, 0.35f));
		}
		int count = Presets.Length;
		float gap = S(count > 3 ? 16 : 20), pad = S(28);
		// 卡片宽度：最宽 300，一整排要放得进窗口（两边各留 40）
		float card = Mathf.Min(S(300), (_windowRect.width - S(80) - pad * 2 - gap * (count - 1)) / count);
		float inner = card - S(28);
		// 韩语、英语按空格换行；中文、日语自己断行，标点不放到行首
		bool spaced = TextConverter.DisplayLanguage is UiText.Korean or UiText.English;
		GUIStyle note = new GUIStyle(_label) { wordWrap = spaced };
		GUIStyle tagline = new GUIStyle(_label) { fontStyle = FontStyle.Bold, wordWrap = spaced };
		tagline.normal.textColor = Cinnabar;
		// 标语、说明按内容量出高度，四张卡片一样高，最长的那张也放得下（以前写死高度，字多的会被截掉）
		string Wrap(string text, GUIStyle style) => spaced ? text : WrapCjk(TextConverter.ForDisplay(text), style, inner);
		var taglines = Presets.Select(p => Wrap(p.Tagline, tagline)).ToArray();
		var notes = Presets.Select(p => Wrap(p.Note, note)).ToArray();
		// 挤到行末的标点会超出断行宽度：框比断行宽度多留一个字，免得被裁掉
		float boxWidth = inner + (spaced ? 0 : note.fontSize);
		float taglineHeight = taglines.Max(t => tagline.CalcHeight(new GUIContent(t), boxWidth));
		float noteHeight = notes.Max(t => note.CalcHeight(new GUIContent(t), boxWidth));
		float cardHeight = S(10) + S(34) + S(6) + taglineHeight + S(10) + noteHeight + S(14);
		float width = card * count + gap * (count - 1) + pad * 2;
		float height = S(104) + cardHeight + S(12) + S(42) + S(16) + S(36) + S(20);
		var box = new Rect((_windowRect.width - width) / 2, Mathf.Max(0, (_windowRect.height - height) / 2), width, height);
		if (Event.current.type == EventType.Repaint)
		{
			Fill(box, PaperLight);
			Frame(box, Cinnabar, 2);
		}
		GUI.Label(new Rect(box.x + pad, box.y + S(20), width - pad * 2, S(36)), "选一个预设", _heading);
		GUI.Label(new Rect(box.x + pad, box.y + S(60), width - pad * 2, S(30)), "之后随时可以在设置页最上面换，也可以逐项调整。存档、提速、界面、热键等设置不受影响。", _muted);
		float y = box.y + S(104);
		for (int i = 0; i < count; i++)
		{
			Preset preset = Presets[i];
			var r = new Rect(box.x + pad + i * (card + gap), y, card, cardHeight);
			if (Event.current.type == EventType.Repaint)
			{
				Frame(r, InkLine, 1);
			}
			GUI.Label(new Rect(r.x + S(14), r.y + S(10), inner, S(34)), preset.Name, Middle(_heading));
			float ty = r.y + S(10) + S(34) + S(6);
			GUI.Label(new Rect(r.x + S(14), ty, boxWidth, taglineHeight), taglines[i], tagline);
			GUI.Label(new Rect(r.x + S(14), ty + taglineHeight + S(10), boxWidth, noteHeight), notes[i], note);
			if (GUI.Button(new Rect(r.x, r.yMax + S(12), card, S(42)), string.Format("用「{0}」", preset.Name), _button))
			{
				foreach (var (element, value) in preset.Values)
				{
					element.BoxedValue = value;
				}
				ConfigManager.PresetChooserShown.Value = true;
				Notice(string.Format("已换成「{0}」，之后可以在设置页最上面换", preset.Name));
			}
		}
		float later = S(140);
		if (GUI.Button(new Rect(box.xMax - pad - later, y + cardHeight + S(12) + S(42) + S(16), later, S(36)), "以后再说", _button))
		{
			ConfigManager.PresetChooserShown.Value = true;
		}
	}

	// 不能出现在行首的标点；不能留在行末的开括号
	private const string NoLineStart = "，。、；：？！）」』》〉】…—·,.;:?!)";
	private const string NoLineEnd = "（「『《〈【(";

	private readonly Dictionary<string, string> _wrapped = new Dictionary<string, string>();

	/// <summary>
	/// 中文、日语按字断行：一行放不下就换行，但标点不放到行首（挤在上一行末尾），开括号不留在行末（带到下一行）。
	/// IMGUI 自带的换行会把「、」「，」排到行首。
	/// </summary>
	private string WrapCjk(string text, GUIStyle style, float width)
	{
		string key = text + "|" + (int)width + "|" + style.fontSize;
		if (_wrapped.TryGetValue(key, out string done))
		{
			return done;
		}
		var lines = new List<string>();
		var line = new System.Text.StringBuilder();
		foreach (char c in text)
		{
			if (c == '\n')
			{
				lines.Add(line.ToString());
				line.Clear();
				continue;
			}
			if (line.Length > 0 && style.CalcSize(new GUIContent(line.ToString() + c)).x > width && NoLineStart.IndexOf(c) < 0)
			{
				string carry = "";
				if (line.Length > 1 && NoLineEnd.IndexOf(line[line.Length - 1]) >= 0)
				{
					carry = line[line.Length - 1].ToString();
					line.Length--;
				}
				lines.Add(line.ToString());
				line.Clear();
				line.Append(carry);
			}
			line.Append(c);
		}
		lines.Add(line.ToString());
		return _wrapped[key] = string.Join("\n", lines);
	}
}
