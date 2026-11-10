using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using LOMAssistant.Config;
using LOMAssistant.Features.AnytimeSave;
using MoonSharp.Interpreter;
using Mortal.Core;
using Mortal.Story;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace LOMAssistant;

/// <summary>
/// 随机事件自选：剧本走到 checkpointmanager.Position（工作、闲逛等按权重抽一个事件）时先停下，
/// 列出这次满足条件的事件，选一个就触发它，也可以选「随机」照游戏原本的概率抽。
/// 出现条件没满足的事件折叠在下面（写明要什么条件），点了也照样触发。
/// 不带随机值的分支判断，打开「分支判定手动选择」时也在这里停下，列出每一支的条件、对白看过没（见 BranchPicker）。
/// 条件里带随机值的分支判断（checkpointmanager.Switch，比如飞天门修炼时「随机值 ≤ 小梅好感」就被小梅拉去跑步）也一样停下，
/// 列出这次能走到的几条路和概率，选哪条就把这次判断里的随机值定成让它成立的数（其他条件照常判断，见 SwitchRandom）。
/// 停下的办法参考赵活系统（Toufff，MIT）：把剧本里的调用包一层，在 Lua 协程里等待，选完再执行原调用。
/// </summary>
public class EventPicker : MonoBehaviour
{
	private enum State { Idle, Waiting, Released }

	private static readonly Regex Call = new Regex(@"(?<![\w.])checkpointmanager\.Position\(\s*""([A-Za-z0-9_]+)""\s*\)", RegexOptions.Compiled);
	private static readonly Regex SwitchCall = new Regex(@"(?<![\w.])checkpointmanager\.Switch\(\s*""([A-Za-z0-9_]+)""\s*\)", RegexOptions.Compiled);

	private static EventPicker _instance;
	private static State _state;
	private static string _pool;
	private static readonly List<Choice> Choices = new List<Choice>();
	/// <summary>键盘、手柄停在哪：事件在 Choices 里的序号，或 FoldSlot（展开、收起按钮）、RandomSlot（随机）。</summary>
	private static int _highlight;
	private const int RandomSlot = -1, FoldSlot = -2;
	/// <summary>键盘、手柄换了选中项，下一次画的时候把它滚进视野。</summary>
	private static bool _scrollToHighlight;

	/// <summary>选中的事件，游戏下次从这个事件池抽结果时用掉。</summary>
	internal static PositionEventData Pending;
	/// <summary>选中的是出现条件没满足的事件：抽结果时不再看条件。</summary>
	private static bool _pendingUnmet;
	/// <summary>条件没满足的事件展开着（这次开游戏期间记住）。</summary>
	private static bool _showUnmet;

	public static bool IsWaiting => _state == State.Waiting;

	/// <summary>选择框开着：Esc、手柄、右键归它用，不交给游戏（见 InputGuard），也不替玩家选，一定要在框里选一项。</summary>
	/// <summary>关掉的那一帧也算：游戏的对话框可能比本 Mod 晚一步读到同一下按键。</summary>
	public static bool OwnsEscape => (_state == State.Waiting && Choices.Count > 0) || Time.frameCount == _closedFrame;

	private static int _closedFrame = -1;



	private class Choice
	{
		public PositionEventData Data;
		/// <summary>分支判断：选它时把随机值定成这个数。</summary>
		public int Forced;
		/// <summary>「总是」「不出」记在这个编号下（事件编号，或「Switch:判断名#第几支」）。</summary>
		public string Key;
		public string Name;
		public string Effects;
		// 去处保留原文，量高度和显示时才逐项翻译，切换语言后不残留旧译文。
		public string[] Exits;
		public string DisplayEffects => Exits == null || Exits.Length == 0 ? Effects :
			(string.IsNullOrEmpty(Effects) ? "" : Effects + "\n") + string.Join("；", Exits.Select(TextConverter.ForDisplay));
		public int Rate;
		/// <summary>这个事件的剧情一句都没读过（和地图建筑提示的「没看过」一样）。</summary>
		public bool Unread;
		/// <summary>出现条件没满足（排在能触发的后面，不参与随机和键盘选择）。</summary>
		public bool Unmet;
	}

	/// <summary>能触发的有几个（Choices 里排在前面）。</summary>
	private static int Met => Choices.Count(c => !c.Unmet);

	/// <summary>正在问的是分支判断（不是事件池）时，判断名。</summary>
	private static string _switch;
	/// <summary>正在问的是「分支判定手动选择」（不带随机值的分支判断），Choice.Forced 是第几支。</summary>
	private static bool _branchMode;
	/// <summary>
	/// 这段剧本里已经选过的判断（判断名 → 第几支，0 是按游戏判断）。同一个判断在一段剧本里常出现好几次（开场按它摆立绘、后面按它接对白），
	/// 第一次选了哪支，后面几次照着走，不再问，免得两次选得不一样、立绘和对白对不上。换剧本、换场景作废。
	/// </summary>
	private static readonly Dictionary<string, int> Picked = new Dictionary<string, int>(System.StringComparer.Ordinal);
	/// <summary>等选的时候 Fungus 的黑屏改由这里画（见 DrawFade）。</summary>
	internal static Fungus.CameraManager Fade;
	private static bool _drawErrorLogged;

	private float _scale = -1f;
	private int _fontMode = -1;
	private GUIStyle _label, _title, _button, _highlighted, _effects, _window, _random, _randomHighlighted, _rule, _ruleOn, _ruleHighlighted;
	private bool _dark;
	private float _opacity;
	private Rect _windowRect;
	private Vector2 _scroll;
	private string _lastLayout;

	public static void Ensure(GameObject host)
	{
		if (_instance == null)
		{
			_instance = host.GetComponent<EventPicker>() ?? host.AddComponent<EventPicker>();
			InputGuard.Ensure();
			SceneManager.activeSceneChanged += (a, b) => Reset();
		}
	}

	private static void Reset()
	{
		_state = State.Idle;
		_pool = null;
		_switch = null;
		_branchMode = false;
		Choices.Clear();
		Picked.Clear();
		Pending = null;
		_pendingUnmet = false;
		SwitchRandom.Pending = null;
	}

	/// <summary>
	/// 给剧本里每个 checkpointmanager.Position("名字")、checkpointmanager.Switch("名字") 包上等待；只在协程里等，主线程直接执行。
	/// 分支判断带上剧本名，选择框里写每条路接到哪段剧情。
	/// </summary>
	public static string Instrument(string code, string script = "")
	{
		code = Call.Replace(code, m => "(function() local _, __lom_main = coroutine.running(); if not __lom_main and __lom_wait_position then while __lom_wait_position(\""
			+ m.Groups[1].Value + "\") do coroutine.yield() end end return " + m.Value + " end)()");
		return SwitchCall.Replace(code, m => "(function() local _, __lom_main = coroutine.running(); if not __lom_main and __lom_wait_switch then while __lom_wait_switch(\""
			+ m.Groups[1].Value + "\", \"" + (script ?? "") + "\") do coroutine.yield() end end return " + m.Value + " end)()");
	}

	public static void Bind(Script script)
	{
		script.Globals["__lom_wait_position"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(a.Count > 0 && Wait(a[0].String)));
		script.Globals["__lom_wait_switch"] = DynValue.NewCallback((c, a) => DynValue.NewBoolean(a.Count > 1 && WaitSwitch(a[0].String, a[1].String)));
	}

	/// <summary>新剧本开始执行：上一段剧本里没选完的作废。</summary>
	public static void ScriptLoaded()
	{
		Picked.Clear();
		if (_state != State.Idle)
		{
			_state = State.Idle;
			_pool = null;
			_switch = null;
			_branchMode = false;
			Choices.Clear();
		}
	}

	/// <summary>剧本每帧问一次：还要不要等。</summary>
	private static bool Wait(string pool)
	{
		try
		{
			if (_state == State.Waiting && _pool == pool)
			{
				return true;
			}
			if (_state == State.Released && _pool == pool)
			{
				_state = State.Idle;
				_pool = null;
				return false;
			}
			if (_instance == null || !ConfigManager.EnableEventPicker.Value || AnytimeSave.Replaying || AnytimeSave.Restoring)
			{
				return false;
			}
			// 剧情回顾「此刻」页里已经选定了这个池子的事件，就不再问
			if (Pending != null && PositionEventCache.Instance.GetPositionEvents(pool).Contains(Pending))
			{
				return false;
			}
			Choices.Clear();
			var unmet = new List<Choice>();
			foreach (PositionEventData data in PositionEventCache.Instance.GetPositionEvents(pool))
			{
				int rate = data.GetRate();
				// 条件没满足的只在设置里打开「强制触发未满足条件的事件」时列出
				string condition = rate > 0 || !ConfigManager.ForceUnmetEvents.Value ? null : UnmetCondition(data);
				if (rate <= 0 && condition == null)
				{
					continue;
				}
				string effects = PositionEventCache.Instance.GetEffects(data);
				var choice = new Choice { Data = data, Key = data.name, Rate = rate, Name = TextConverter.ToSimplified(DataHelper.GetEventName(data)), Effects = effects, Unread = MapEventHints.Unread(PositionEventCache.Instance.ResultScript(data)) };
				if (condition != null)
				{
					choice.Unmet = true;
					choice.Effects = "（要 " + condition + "）" + (string.IsNullOrEmpty(effects) ? "" : "\n" + effects);
					unmet.Add(choice);
				}
				else
				{
					Choices.Add(choice);
				}
			}
			// 能触发的不到两个时照旧不问（不然只有一个能触发的地方每次都要停下来）
			if (Choices.Count < 2)
			{
				Choices.Clear();
				return false;
			}
			Choices.AddRange(unmet);
			_state = State.Waiting;
			MarkRollback();
			_pool = pool;
			_switch = null;
			_branchMode = false;
			HighlightDefault();
			StopSkip();
			FileLogger.Log($"[事件自选] {pool}：{Choices.Count} 个可触发事件，等待选择");
			return true;
		}
		catch (System.Exception ex)
		{
			FileLogger.LogWarning("[事件自选] 读取事件池失败，按原本随机: " + ex.Message);
			_state = State.Idle;
			Choices.Clear();
			return false;
		}
	}

	/// <summary>事件的出现条件没满足时，条件写成一句（带现在的值）；满足、没设条件或读不出时返回 null。</summary>
	private static string UnmetCondition(PositionEventData data)
	{
		if (!data.GetFieldValue<bool>("_toggleCondition"))
		{
			return null;
		}
		ConditionResultItem item = data.GetFieldValue<ConditionResultItem>("_activeCondition");
		if (item == null || item.Check())
		{
			return null;
		}
		string text = TextConverter.ToSimplifiedUnlessJapanese(DataHelper.ExtractConditions(item, out _));
		return string.IsNullOrWhiteSpace(RichText.Strip(text)) ? null : text;
	}

	// 键盘、手柄的选中项默认停在「随机」：直接按确认就是按原本概率抽；停在第一项会让人误以为会抽到它。
	// 设了「总是」的能走到时停在它上面
	private static void HighlightDefault()
	{
		int always = Choices.FindIndex(c => !c.Unmet && EventRules.Get(c.Key) == EventRules.Always);
		_highlight = always >= 0 ? always : RandomSlot;
	}

	/// <summary>
	/// 剧本每帧问一次：这个分支判断要不要停下来等选。条件里有随机值、能走到两条以上的路时按「随机事件自选」问；
	/// 不带随机值的按「分支判定手动选择」问（换一支有新内容才问）。
	/// </summary>
	private static bool WaitSwitch(string name, string script)
	{
		string pool = "switch:" + name;
		try
		{
			if (_state == State.Waiting && _pool == pool)
			{
				return true;
			}
			if (_state == State.Released && _pool == pool)
			{
				_state = State.Idle;
				_pool = null;
				_switch = null;
				_branchMode = false;
				return false;
			}
			if (_instance == null || AnytimeSave.Replaying || AnytimeSave.Restoring)
			{
				return false;
			}
			List<SwitchRandom.Arm> arms = ConfigManager.EnableEventPicker.Value ? SwitchRandom.Arms(name) : null;
			if (arms == null || arms.Count < 2)
			{
				return SwitchRandom.Arms(name) == null && BranchPicker.Enabled && WaitBranch(name, script, pool);
			}
			Choices.Clear();
			foreach (SwitchRandom.Arm arm in arms)
			{
				var (label, condition) = SwitchRandom.Describe(name, script, arm.Branch, arm.Value);
				Choices.Add(new Choice { Key = SwitchRandom.RuleKey(name, arm.Branch), Forced = arm.Value, Rate = arm.Count, Name = label, Effects = condition });
			}
			_state = State.Waiting;
			MarkRollback();
			_pool = pool;
			_switch = name;
			_branchMode = false;
			HighlightDefault();
			StopSkip();
			FileLogger.Log($"[分支自选] {name}：{Choices.Count} 条路，等待选择");
			return true;
		}
		catch (System.Exception ex)
		{
			FileLogger.LogWarning("[分支自选] 读取分支判断失败，按原本随机: " + ex.Message);
			_state = State.Idle;
			_switch = null;
			_branchMode = false;
			Choices.Clear();
			return false;
		}
	}

	// 分支判定手动选择：每一支写成「第几支　会走这一支　看过没」，下面一行是条件（满不满足、现在的值）和接到哪里
	private static bool WaitBranch(string name, string script, string pool)
	{
		if (Picked.TryGetValue(name, out int picked))
		{
			if (picked > 0)
			{
				BranchOverride.Set(name, picked);
			}
			FileLogger.Log("[分支] " + name + "：这段剧本里选过，" + (picked > 0 ? "照样走第 " + picked + " 支" : "照样按游戏判断"));
			return false;
		}
		List<BranchPicker.Arm> arms = BranchPicker.Arms(name, script, out int predicted);
		if (arms == null || !BranchPicker.Worth(arms, predicted))
		{
			return false;
		}
		Choices.Clear();
		int fallback = arms.Count;
		foreach (BranchPicker.Arm arm in arms)
		{
			string label = (arm.Branch == fallback ? "以上条件都不满足" : $"第 {arm.Branch} 支") + (arm.Branch == predicted ? "　<color=#A3302A>会走这一支</color>" : "") + "　" + BranchPicker.ReadState(arm);
			string mark = arm.Holds == true ? RichText.Green("满足") + "　" : arm.Holds == false ? RichText.Red("不满足") + "　" : "";
			string detail = arm.Branch == fallback ? "" : mark + arm.Condition;
			Choices.Add(new Choice { Key = SwitchRandom.RuleKey(name, arm.Branch), Forced = arm.Branch, Name = label, Effects = detail, Exits = arm.Exits });
		}
		_state = State.Waiting;
		MarkRollback();
		_pool = pool;
		_switch = name;
		_branchMode = true;
		_highlight = RandomSlot;
		StopSkip();
		FileLogger.Log($"[分支] {name}：{arms.Count} 支，游戏会走第 {predicted} 支，等待选择");
		return true;
	}

	// 弹出选择框时记一个分支回退点（弹框前一句）：选一支看完后按 F4 回到这里，再选另一支
	private static void MarkRollback()
	{
		try
		{
			AnytimeSave.Instance?.MarkBranchPoint();
		}
		catch (System.Exception ex)
		{
			FileLogger.LogWarning("[分支] 记分支回退点失败: " + ex.Message);
		}
	}

	// 弹出选择框时停住快进（按住 Ctrl、一键快进已读），选完要看的剧情不会被快进带过去
	private static void StopSkip()
	{
		try
		{
			if (SceneManager.GetActiveScene().name == "Story" && StoryManager.Instance != null)
			{
				StoryManager.Instance.SkipDialog(false);
			}
		}
		catch (System.Exception ex)
		{
			FileLogger.LogWarning("[事件自选] 停住快进失败: " + ex.Message);
		}
	}

	private static void Choose(Choice choice)
	{
		if (_switch != null && _branchMode)
		{
			if (choice != null)
			{
				BranchOverride.Set(_switch, choice.Forced);
			}
			Picked[_switch] = choice?.Forced ?? 0;
			FileLogger.Log("[分支] " + _switch + "：" + (choice == null ? "按游戏判断" : "走第 " + choice.Forced + " 支"));
		}
		else if (_switch != null)
		{
			SwitchRandom.Pending = choice == null ? null : (_switch, choice.Forced);
			FileLogger.Log("[分支自选] " + _switch + "：" + (choice == null ? "随机" : choice.Name));
		}
		else
		{
			Pending = choice?.Data;
			_pendingUnmet = choice != null && choice.Unmet;
			FileLogger.Log("[事件自选] " + _pool + "：" + (choice == null ? "随机" : DataHelper.GetEventName(choice.Data) + (choice.Unmet ? "（条件没满足）" : "")));
		}
		_state = State.Released;
		_closedFrame = Time.frameCount;
		Choices.Clear();
	}

	/// <summary>游戏从事件池抽结果：有选中的、而且现在还满足条件，就直接给它。</summary>
	internal static bool TryPick(PositionResultData pool, out string result)
	{
		result = null;
		PositionEventData pick = Pending;
		if (pick == null || !pool.List.Contains(pick))
		{
			return false;
		}
		Pending = null;
		bool unmet = _pendingUnmet;
		_pendingUnmet = false;
		if (!unmet && pick.GetRate() <= 0)
		{
			FileLogger.LogWarning("[事件自选] 选中的事件现在不满足条件，按原本随机：" + DataHelper.GetEventName(pick));
			return false;
		}
		result = pool.GetFieldValue<string>("_scriptPrefix") + "_" + pick.ResultType;
		return true;
	}

	private void Update()
	{
		if (_state != State.Waiting || Choices.Count == 0)
		{
			return;
		}
		int met = Met;
		List<int> order = NavOrder();
		int at = Mathf.Max(0, order.IndexOf(_highlight));
		Keyboard kb = Keyboard.current;
		Gamepad pad = Gamepad.current;
		if ((kb != null && kb.upArrowKey.wasPressedThisFrame) || (pad != null && pad.dpad.up.wasPressedThisFrame))
		{
			_highlight = order[(at + order.Count - 1) % order.Count];
			_scrollToHighlight = true;
		}
		if ((kb != null && kb.downArrowKey.wasPressedThisFrame) || (pad != null && pad.dpad.down.wasPressedThisFrame))
		{
			_highlight = order[(at + 1) % order.Count];
			_scrollToHighlight = true;
		}
		if (kb != null)
		{
			for (int i = 0; i < Mathf.Min(9, met); i++)
			{
				if (kb[Key.Digit1 + i].wasPressedThisFrame)
				{
					Choose(Choices[i]);
					return;
				}
			}
		}
		if ((kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)) || (pad != null && pad.buttonSouth.wasPressedThisFrame))
		{
			if (_highlight == FoldSlot)
			{
				ToggleUnmet();
			}
			else
			{
				Choose(_highlight >= 0 && _highlight < Choices.Count ? Choices[_highlight] : null);
			}
		}
	}

	/// <summary>键盘、手柄上下切换的顺序，和画出来的一样：能触发的事件、展开收起按钮、（展开时）条件没满足的事件、随机。</summary>
	private static List<int> NavOrder()
	{
		var order = new List<int>();
		for (int i = 0; i < Choices.Count; i++)
		{
			if (!Choices[i].Unmet)
			{
				order.Add(i);
			}
		}
		if (order.Count < Choices.Count)
		{
			order.Add(FoldSlot);
			if (_showUnmet)
			{
				order.AddRange(Enumerable.Range(0, Choices.Count).Where(i => Choices[i].Unmet));
			}
		}
		order.Add(RandomSlot);
		return order;
	}

	private void ToggleUnmet()
	{
		_showUnmet = !_showUnmet;
		_lastLayout = null;
		// 收起时停在条件没满足的事件上：回到按钮上
		if (!_showUnmet && _highlight >= 0 && _highlight < Choices.Count && Choices[_highlight].Unmet)
		{
			_highlight = FoldSlot;
		}
	}

	private void BuildStyles(float scale)
	{
		// 和咖啡伴侣主窗口同一套配色（宣纸底、墨色字，跟着深色模式变）
		_scale = scale;
		_fontMode = ModFont.Mode;
		_dark = PanelTheme.Dark;
		_opacity = PanelTheme.DialogOpacity;
		_label = PanelTheme.Label(scale, 20, PanelTheme.Ink);
		_effects = PanelTheme.Label(scale, 16, PanelTheme.InkMuted);
		_effects.padding = new RectOffset((int)(12f * scale), 0, 0, (int)(4f * scale));
		_button = PanelTheme.Button(scale, 19);
		_button.alignment = TextAnchor.MiddleLeft;
		_button.wordWrap = true;
		_highlighted = new GUIStyle(_button) { fontStyle = FontStyle.Bold };
		_highlighted.normal.background = _highlighted.hover.background = PanelTheme.Framed(PanelTheme.PaperDeep, PanelTheme.Cinnabar);
		_highlighted.normal.textColor = _highlighted.hover.textColor = PanelTheme.Cinnabar;
		_rule = PanelTheme.Button(scale, 16);
		_rule.margin = new RectOffset((int)(4f * scale), 0, (int)(3f * scale), (int)(3f * scale));
		_ruleOn = PanelTheme.Primary(scale, 16);
		_ruleOn.margin = _rule.margin;
		_ruleHighlighted = new GUIStyle(_rule) { fontStyle = FontStyle.Bold };
		_ruleHighlighted.normal.background = _ruleHighlighted.hover.background = PanelTheme.Framed(PanelTheme.PaperDeep, PanelTheme.Cinnabar);
		_ruleHighlighted.normal.textColor = _ruleHighlighted.hover.textColor = PanelTheme.Cinnabar;
		_random = PanelTheme.Primary(scale, 19);
		// 手柄、键盘停在「随机」上时和停在事件上一样：深一点的底、朱砂边框和字
		_randomHighlighted = new GUIStyle(_highlighted) { alignment = TextAnchor.MiddleCenter };
		_window = PanelTheme.Window(scale);
		_title = PanelTheme.Label(scale, 22, PanelTheme.Ink, false);
		_title.fontStyle = FontStyle.Bold;
		_title.alignment = TextAnchor.MiddleCenter;
		_title.margin = new RectOffset(0, 0, 0, (int)(8f * scale));
		float width = 640f * scale;
		_windowRect = new Rect((Screen.width - width) / 2f, 80f * scale, width, 10f);
	}

	private void OnGUI()
	{
		if (_state != State.Waiting || Choices.Count == 0)
		{
			return;
		}
		float scale = PanelTheme.UiScale;
		if (scale <= 0f)
		{
			return;
		}
		// 剧情的黑屏淡入淡出是 Fungus 在 OnGUI 里画的一整张黑图：判断常在淡入前，窗口要画在它上面，不然被黑屏盖住像卡死。
		// IMGUI 的窗口比普通绘制先上屏，GUI.depth 排在前面也照样被这张图盖住；等选的时候 Fungus 不画，
		// 黑屏改成选择框后面的一个整屏窗口，窗口之间按前后顺序画，选择框一定在它上面
		GUI.depth = -2000;
		if (ModFont.NeedsRebuild(scale, _scale, _fontMode) || _dark != PanelTheme.Dark || _opacity != PanelTheme.DialogOpacity)
		{
			BuildStyles(scale);
		}
		if (Fade != null && Fade.GetFieldValue<float>("fadeAlpha") > 0f)
		{
			GUI.Window(584130, new Rect(0f, 0f, Screen.width, Screen.height), DrawFade, GUIContent.none, GUIStyle.none);
			GUI.BringWindowToBack(584130);
		}
		string layout = _pool + "|" + Choices.Count;
		if (layout != _lastLayout && Event.current.type == EventType.Layout)
		{
			_lastLayout = layout;
			_windowRect.height = 10f;
			_scroll = Vector2.zero;
		}
		_windowRect = GUILayout.Window(584131, _windowRect, DrawWindow, "", _window);
		GUI.BringWindowToFront(584131);
		// 选事件时游戏停着等，窗口拖不出屏幕：左右整个留在屏幕里，上下至少露出 40 像素
		_windowRect.x = Mathf.Clamp(_windowRect.x, 0f, Mathf.Max(0f, Screen.width - _windowRect.width));
		_windowRect.y = Mathf.Clamp(_windowRect.y, 0f, Mathf.Max(0f, Screen.height - 40f * scale));
		InputGuard.Report("EventPicker", _windowRect);
	}

	// Fungus 那张黑图照它现在的透明度画满屏（它自己的 OnGUI 在等选时跳过，见 EventPickerPatch.CameraManager_OnGUI_Prefix）
	private static void DrawFade(int id)
	{
		if (Fade == null || Event.current.type != EventType.Repaint)
		{
			return;
		}
		float alpha = Fade.GetFieldValue<float>("fadeAlpha");
		Texture2D texture = Fade.GetFieldValue<Texture2D>("screenFadeTexture");
		if (alpha <= 0f || texture == null)
		{
			return;
		}
		Color color = GUI.color;
		GUI.color = new Color(1f, 1f, 1f, alpha);
		GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), texture);
		GUI.color = color;
	}

	private static string Label(int i, int total)
	{
		Choice c = Choices[i];
		if (_branchMode)
		{
			return (i < 9 ? (i + 1) + ". " : "") + c.Name;
		}
		// 随机事件名按当前语言重新取；分支文字照旧，编号和权重不动。
		string name = c.Data != null ? DataHelper.GetEventName(c.Data) : c.Name;
		if (c.Unmet)
		{
			return name + "　<color=#73644F>条件没满足</color>" + (c.Unread ? "　" + RichText.Golden("没看过") : "");
		}
		int rate = EventRules.EffectiveRate(c.Key, c.Rate, Choices.Where(x => !x.Unmet).Select(x => x.Key));
		string odds = rate == 0 && EventRules.Get(c.Key) == EventRules.Never ? "不出" : $"{rate * 100f / Mathf.Max(1, total):0.#}%";
		return (i < 9 ? (i + 1) + ". " : "") + name + $"（{odds}）" + (c.Unread ? "　" + RichText.Golden("没看过") : "");
	}

	// 事件名、效果里的绿、红、金是给深色底配的，画窗口期间在宣纸底上换成看得清的同色系（PanelTheme.MapColors）
	private void DrawWindow(int id)
	{
		PanelTheme.Painting = true;
		try
		{
			DrawChoices();
		}
		catch (System.Exception ex) when (!(ex is ExitGUIException))
		{
			// 画不出来时窗口是空的，玩家只看到黑屏：记一次日志，好查是哪里出错
			if (!_drawErrorLogged)
			{
				_drawErrorLogged = true;
				FileLogger.LogWarning("[事件自选] 选择框画不出来: " + ex);
			}
		}
		finally
		{
			PanelTheme.Painting = false;
		}
	}

	private void DrawChoices()
	{
		GUILayout.Label(_branchMode ? "这次走哪一支" : _switch != null ? "这次走哪条路" : "这次触发哪个事件", _title);
		int met = Met;
		var metKeys = Choices.Where(x => !x.Unmet).Select(x => x.Key).ToList();
		int total = Choices.Where(c => !c.Unmet).Sum(c => EventRules.EffectiveRate(c.Key, c.Rate, metKeys));
		// 条件没满足的：默认收起，展开后列在能触发的下面
		int shown = _showUnmet ? Choices.Count : met;
		string fold = met < Choices.Count ? (_showUnmet ? "收起条件没满足的事件" : $"展开条件没满足的事件（{Choices.Count - met}）") : null;
		// 窗口高度随内容变，滚动区不给高度会被压成一行：按内容算出高度，超过屏幕七成再滚动
		float ruleWidth = 64f * _scale;
		float width = _windowRect.width - _window.padding.horizontal - 24f * _scale - ruleWidth * 2f - 8f * _scale;
		float height = 0f;
		for (int i = 0; i < shown; i++)
		{
			height += _button.CalcHeight(new GUIContent(Label(i, total)), width) + _button.margin.vertical;
			string effects = Choices[i].DisplayEffects;
			if (!string.IsNullOrEmpty(effects))
			{
				height += _effects.CalcHeight(new GUIContent(effects), width) + _effects.margin.vertical;
			}
		}
		if (fold != null)
		{
			height += _rule.CalcHeight(new GUIContent(fold), width) + _rule.margin.vertical;
			if (_showUnmet)
			{
				height += _effects.CalcHeight(new GUIContent(Warning), width) + _effects.margin.vertical;
			}
		}
		float viewHeight = Mathf.Min(height + 4f, Screen.height * 0.7f);
		_scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(viewHeight));
		for (int i = 0; i < shown; i++)
		{
			Choice c = Choices[i];
			if (i == met && fold != null)
			{
				DrawFold(fold, viewHeight);
			}
			string text = Label(i, total);
			string effects = c.DisplayEffects;
			GUILayout.BeginHorizontal();
			if (GUILayout.Button(text, i == _highlight ? _highlighted : _button))
			{
				Choose(c);
				GUIUtility.ExitGUI();
			}
			if (i == _highlight)
			{
				ScrollTo(GUILayoutUtility.GetLastRect(), viewHeight);
			}
			// 条件没满足的不参与随机、选分支不是抽随机，都不给「总是」「不出」
			if (c.Unmet || _branchMode)
			{
				GUILayout.Space(ruleWidth * 2f + _rule.margin.left * 2f);
				GUILayout.EndHorizontal();
				// 没有说明的（「以上条件都不满足」那支）不画空行：算高度时也没算它，画了就多出滚动条
				if (!string.IsNullOrEmpty(effects))
				{
					GUILayout.Label(effects, _effects);
				}
				continue;
			}
			// 「总是」：以后这里能抽到它就直接触发它；「不出」：以后不会抽到它。再点一次取消
			int rule = EventRules.Get(c.Key);
			if (GUILayout.Button("总是", rule == EventRules.Always ? _ruleOn : _rule, GUILayout.Width(ruleWidth)))
			{
				EventRules.Set(c.Key, c.Name, rule == EventRules.Always ? EventRules.None : EventRules.Always);
			}
			if (GUILayout.Button("不出", rule == EventRules.Never ? _ruleOn : _rule, GUILayout.Width(ruleWidth)))
			{
				EventRules.Set(c.Key, c.Name, rule == EventRules.Never ? EventRules.None : EventRules.Never);
			}
			GUILayout.EndHorizontal();
			if (!string.IsNullOrEmpty(effects))
			{
				GUILayout.Label(effects, _effects);
			}
		}
		if (!_showUnmet && fold != null)
		{
			DrawFold(fold, viewHeight);
		}
		GUILayout.EndScrollView();
		GUILayout.Space(6f * _scale);
		string bottom = _branchMode ? "按游戏判断" : Choices.Any(c => !c.Unmet && EventRules.Get(c.Key) != EventRules.None) ? "随机（按上面的概率）" : "随机（按原本概率）";
		if (GUILayout.Button(bottom, _highlight == RandomSlot ? _randomHighlighted : _random))
		{
			Choose(null);
			GUIUtility.ExitGUI();
		}
		GUI.DragWindow();
	}

	// 键盘、手柄选中的这一项在滚动区外面时，滚到看得见（画的时候才知道它在哪）
	private void ScrollTo(Rect rect, float viewHeight)
	{
		if (!_scrollToHighlight || Event.current.type != EventType.Repaint)
		{
			return;
		}
		_scrollToHighlight = false;
		if (rect.yMin < _scroll.y)
		{
			_scroll.y = rect.yMin;
		}
		else if (rect.yMax > _scroll.y + viewHeight)
		{
			_scroll.y = rect.yMax - viewHeight + 8f * _scale;
		}
	}

	private const string Warning = "<color=#A3302A>点了会无视条件直接触发，不改旗标，之后的剧情可能对不上</color>";

	private void DrawFold(string text, float viewHeight)
	{
		if (GUILayout.Button(text, _highlight == FoldSlot ? _ruleHighlighted : _rule))
		{
			ToggleUnmet();
			GUIUtility.ExitGUI();
		}
		if (_highlight == FoldSlot)
		{
			ScrollTo(GUILayoutUtility.GetLastRect(), viewHeight);
		}
		if (_showUnmet)
		{
			GUILayout.Label(Warning, _effects);
		}
	}
}

internal static class EventPickerPatch
{
	// 等选的时候 Fungus 不自己画黑屏，交给选择框画在窗口下面
	[HarmonyPrefix]
	[HarmonyPatch(typeof(Fungus.CameraManager), "OnGUI")]
	public static bool CameraManager_OnGUI_Prefix(Fungus.CameraManager __instance)
	{
		// 平时也记下来：刚停下的那一帧选择框可能比它先画，要已经知道黑屏画成什么样
		EventPicker.Fade = __instance;
		return !EventPicker.IsWaiting;
	}

	// 在信息窗口解析之后、随时存档包装之前；开了随时存档时由它在包装前调用 Instrument，这里跳过
	[HarmonyPrefix]
	[HarmonyPriority(Priority.LowerThanNormal)]
	[HarmonyPatch(typeof(Fungus.LuaEnvironment), "LoadLuaFunction")]
	public static void LoadLuaFunction_Prefix(Fungus.LuaEnvironment __instance, ref string luaString, string friendlyName)
	{
		if (friendlyName == null || !friendlyName.EndsWith(".LuaScript") || friendlyName == "tmp.LuaScript")
		{
			return;
		}
		EventPicker.ScriptLoaded();
		EventPicker.Bind(__instance.Interpreter);
		if (AnytimeSave.Instance == null)
		{
			luaString = EventPicker.Instrument(luaString, friendlyName.Substring(0, friendlyName.Length - ".LuaScript".Length));
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(PositionResultData), "GetResult")]
	public static bool GetResult_Prefix(PositionResultData __instance, ref string __result)
	{
		try
		{
			if (EventPicker.TryPick(__instance, out string result))
			{
				__result = result;
				return false;
			}
		}
		catch (System.Exception ex)
		{
			FileLogger.LogWarning("[事件自选] 指定事件失败，按原本随机: " + ex.Message);
		}
		try
		{
			if (EventRules.TryPick(__instance, out string result))
			{
				__result = result;
				return false;
			}
		}
		catch (System.Exception ex)
		{
			FileLogger.LogWarning("[事件规则] 按规则抽事件失败，按原本随机: " + ex.Message);
		}
		return true;
	}
}

/// <summary>
/// 随机事件的「总是」「不出」：在随机事件选择框里给某个事件设上，记在配置 EventRules 里（事件编号=1 总是、=2 不出）。
/// 游戏从事件池抽结果时（没有在选择框里点名选哪个，包括选「随机」和关掉事件自选时）：
/// 能触发的事件里有「总是」的就只在它们之间抽；否则把「不出」的去掉再按原本的权重抽；全都「不出」就照原样抽。
/// 比如练功时的教小竹练功、小梅练功，闲逛时的和小梅跑步，想刷出来就设「总是」，不想被打扰就设「不出」。
/// </summary>
internal static class EventRules
{
	public const int None = 0, Always = 1, Never = 2;

	private static Dictionary<string, int> _rules;
	private static string _parsedFrom;

	private static Dictionary<string, int> Rules
	{
		get
		{
			string raw = ConfigManager.EventRules.Value ?? "";
			if (_rules == null || _parsedFrom != raw)
			{
				_parsedFrom = raw;
				_rules = new Dictionary<string, int>();
				foreach (string part in raw.Split(';'))
				{
					string[] kv = part.Split('=');
					if (kv.Length == 2 && int.TryParse(kv[1], out int v) && (v == Always || v == Never) && kv[0].Trim().Length > 0)
					{
						_rules[kv[0].Trim()] = v;
					}
				}
			}
			return _rules;
		}
	}

	public static int Get(PositionEventData data) => data != null ? Get(data.name) : None;

	public static int Get(string key) => key != null && Rules.TryGetValue(key, out int v) ? v : None;

	public static void Set(string key, string label, int rule)
	{
		var rules = new Dictionary<string, int>(Rules);
		if (rule == None)
		{
			rules.Remove(key);
		}
		else
		{
			rules[key] = rule;
		}
		ConfigManager.EventRules.Value = string.Join(";", rules.Select(kv => kv.Key + "=" + kv.Value).ToArray());
		FileLogger.Log("[事件规则] " + RichText.Strip(label ?? "") + "（" + key + "）：" + (rule == Always ? "总是" : rule == Never ? "不出" : "取消"));
	}

	public static int EffectiveRate(PositionEventData data, int rate, IEnumerable<PositionEventData> pool) => EffectiveRate(data.name, rate, pool.Select(d => d.name));

	/// <summary>按规则算出来的权重：同一池里有能触发的「总是」时，只有它们保留原权重。</summary>
	public static int EffectiveRate(string key, int rate, IEnumerable<string> pool)
	{
		int rule = Get(key);
		bool anyAlways = pool.Any(d => Get(d) == Always);
		if (anyAlways)
		{
			return rule == Always ? rate : 0;
		}
		bool allNever = pool.All(d => Get(d) == Never);
		return rule == Never && !allNever ? 0 : rate;
	}

	public static bool TryPick(PositionResultData pool, out string result)
	{
		result = null;
		if (Rules.Count == 0)
		{
			return false;
		}
		var candidates = new List<(PositionEventData Data, int Rate)>();
		foreach (PositionEventData data in pool.List)
		{
			int rate = data != null ? data.GetRate() : 0;
			if (rate > 0)
			{
				candidates.Add((data, rate));
			}
		}
		if (!candidates.Any(c => Get(c.Data) != None))
		{
			return false;
		}
		var weighted = candidates.Select(c => (c.Data, Rate: EffectiveRate(c.Data, c.Rate, candidates.Select(x => x.Data)))).Where(c => c.Rate > 0).ToList();
		int total = weighted.Sum(c => c.Rate);
		if (total <= 0)
		{
			return false;
		}
		int roll = Random.Range(0, total);
		foreach (var (data, rate) in weighted)
		{
			if (roll < rate)
			{
				result = pool.GetFieldValue<string>("_scriptPrefix") + "_" + data.ResultType;
				FileLogger.Log("[事件规则] " + pool.name + " 按规则抽到：" + DataHelper.GetEventName(data));
				return true;
			}
			roll -= rate;
		}
		return false;
	}
}
