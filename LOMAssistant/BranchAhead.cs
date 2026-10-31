using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using LOMAssistant.Config;
using LOMAssistant.Features.AnytimeSave;
using LOMAssistant.Features.Story;
using Mortal.Core;
using Mortal.Story;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LOMAssistant;

/// <summary>
/// 判定前的分支表：条件型的分支判断（SwitchResultData，条件里不带随机值的那种，如「修养 ≥ 60」）走到时先停下，
/// 列出每一支的条件和当前值、这一支后面的对白看过没，选哪一支就把数值改成那条路需要的方向：
/// 选第 N 支 = 把它前面的支压到不满足、这一支改到满足；选「以上都不满足」= 把每一支都压破。
///
/// 停下的办法和 <see cref="EventPicker"/> 一样（剧本里的 __lom_wait_switch 里每帧问一次），
/// 只是那边管带随机值的判断，这边管纯条件的判断；改数值用 <see cref="ConditionSolver"/>。
/// 带随机值的判断仍然归 EventPicker（那里能算出概率，这里算不了）。
///
/// 「判定前」这个思路取自 MortalStoryWorkbench（剧情随行，xiaozhanshang，MIT）的台词预告；
/// 反向改数（把已经满足的压到不满足）参考赵活系统（Toufff，MIT）的条件求解写法。
/// 本文件由 DeepSeek-V4.1 Flash（通过 WorkBuddy）按 F1-ZX 的需求编写，逻辑行为与每一处取舍由 F1-ZX 定、并逐轮实机验证。
/// </summary>
public sealed class BranchAhead : MonoBehaviour
{
	private enum State { Idle, Waiting, Released }

	/// <summary>窗口里的一行：一支条件，或最后一行的「以上都不满足」。</summary>
	private sealed class Row
	{
		/// <summary>第几支（1 起算）；0 = 以上都不满足。</summary>
		public int Branch;
		public string Condition;
		public string Where;
		/// <summary>0 不知道；1 没看过；2 看过一半；3 看过。</summary>
		public int Seen = 0;
		public bool Taken;
	}

	private const int WindowId = 584132;

	private static BranchAhead _instance;
	private static State _state;
	private static string _name;
	private static readonly List<Row> Rows = new List<Row>();
	private static string _message;
	private static int _highlight;

	private float _scale = -1f;
	private int _fontMode = -1;
	private bool _dark;
	private float _opacity;
	private Rect _rect;
	private Vector2 _scroll;
	private string _lastLayout;
	private GUIStyle _window, _title, _row, _rowTaken, _hint, _key, _keyOn;
	private GUIStyle _tip;
	private float _tipScale = -1f;

	/// <summary>刚放下窗口的帧数：这一两帧里 Esc 仍归窗口，别让它顺手把游戏的菜单也开出来。</summary>
	private static int _escFrame = -10;

	/// <summary>这一处判定停下来的帧：新弹出的头两帧不接「点空白放行」，免得顺带把手上的那一下点击吃掉。</summary>
	private static int _openedFrame = -10;

	/// <summary>Ctrl 快进是不是已经被窗口压掉过一次（日志只记一条，免得每帧刷）。</summary>
	private static bool _skipTold;
	private static int _skipErrors;
	private static AccessTools.FieldRef<StoryManager, bool> _skipDialog;

	private static string _tipText;
	private static float _tipAt;
	private static string _tipKey;

	private const float TipLife = 4.5f;

	public static void Ensure(GameObject host)
	{
		if (_instance == null)
		{
			_instance = host.GetComponent<BranchAhead>() ?? host.AddComponent<BranchAhead>();
		}
	}

	/// <summary>
	/// 窗口开着（或刚被 Esc 关掉的这一帧）：Esc 归窗口用，不交给游戏。
	/// 剧情里按 Esc 原本是开游戏菜单（<c>StoryManager.MenuActionChanged</c> → <c>Menu(true)</c>），
	/// 那会把时间停住、把剧情判成暂停；等窗口这边再放行，脚本协程已经没人推了，剧情就卡死在那儿。
	/// </summary>
	internal static bool OwnsEscape
	{
		get
		{
			return _state == State.Waiting || Time.frameCount - _escFrame <= 1;
		}
	}

	/// <summary>剧本换了一本：上一处没选完的作废（判定已经不会再执行了）。</summary>
	public static void ScriptLoaded()
	{
		if (_state != State.Idle)
		{
			_state = State.Idle;
			_name = null;
			Rows.Clear();
		}
	}

	/// <summary>
	/// 剧本每帧问一次：这处判定要不要停下来等选。返回 true 就一直等（协程 yield），选完（或被跳过）返回 false，
	/// 之后游戏用改好的数值照常执行这次判断。
	/// </summary>
	internal static bool Wait(string name, string script)
	{
		try
		{
			// 宿主没了（窗口画不出来、也没人处理 Esc）就别再等：否则脚本会永远停在 while 里
			if (_instance == null && _state != State.Idle)
			{
				FileLogger.LogWarning("[分支判定] 窗口宿主不在了，按原样放行");
				_state = State.Idle;
				_name = null;
				Rows.Clear();
				return false;
			}
			if (_state == State.Waiting && _name == name)
			{
				return true;
			}
			if (_state == State.Released && _name == name)
			{
				_state = State.Idle;
				_name = null;
				return false;
			}
			if (_instance == null || !ConfigManager.EnableBranchAhead.Value || AnytimeSave.Replaying || AnytimeSave.Restoring)
			{
				return false;
			}
			SwitchResultData data = SwitchRandom.Find(name);
			if (data == null)
			{
				return false;
			}
			var tr = Traverse.Create(data);
			if (tr.Field("_useCount").GetValue<bool>())
			{
				return false; // 按次数判断的没有条件可看
			}
			List<ConditionResultItem> items = tr.Field("_items").GetValue<List<ConditionResultItem>>();
			if (items == null || items.Count == 0)
			{
				return false;
			}
			StoryScript story = StoryScripts.Get(script);
			int now = Current(items);
			// 只有一支、而且现在已经满足、兜底那一支也没有自己的对白或去处：改了只是少一句，停下来只是打扰。
			// 但也不能一声不吭——玩家点开判定没反应，会以为功能坏了，所以在屏幕下方说一句（尽量短）。
			if (items.Count == 1 && now == 1 && !Content(story, name, 0, items.Count))
			{
				if (ConfigManager.BranchAheadHint.Value)
				{
					string condition = RichText.Strip(DataHelper.SwitchBranchCondition(name, 1) ?? "").Trim();
					// 条件写得长（好几个且/or）的时候不整段搬上来，一句话提示反而看不清
					if (condition.Length == 0 || condition.Length > 28)
					{
						condition = "这处判定";
					}
					Tip("worth:" + script + "|" + name, condition + " 已满足 · 改了只会少一句");
				}
				return false;
			}

			Build(name, items, story, now);
			// 「判定前存档」：做完一支想走另一支，从随时存档读回来就行，不用从头再走一遍。
			// 存档出问题绝不能影响判定本身，所以整段自己兜住
			if (ConfigManager.BranchAheadAutoSnapshot.Value)
			{
				try
				{
					FileLogger.Log("[分支判定] " + name + "：" + Snapshot(name));
				}
				catch (System.Exception ex)
				{
					FileLogger.LogWarning("[分支判定] 判定前存档失败（不影响判定）: " + ex.Message);
				}
			}
			_state = State.Waiting;
			_name = name;
			_message = null;
			_openedFrame = Time.frameCount;
			_skipTold = false;
			_highlight = Rows.FindIndex(r => r.Taken);
			if (_highlight < 0)
			{
				_highlight = Rows.Count - 1;
			}
			FileLogger.Log($"[分支判定] {name}：{items.Count} 支，按当前数值走{Where(now, items.Count)}，等待选择；"
				+ Diagnose(script, name, story, items.Count));
			return true;
		}
		catch (System.Exception ex)
		{
			FileLogger.LogWarning("[分支判定] 读不出这处判定，按原本的数值走: " + ex.Message);
			_state = State.Idle;
			_name = null;
			Rows.Clear();
			return false;
		}
	}

	/// <summary>按当前数值会走第几支（1 起算；比支数多 1 的是「以上都不满足」）。只读条件，不执行判断。</summary>
	private static int Current(List<ConditionResultItem> items)
	{
		for (int i = 0; i < items.Count; i++)
		{
			if (items[i].Check())
			{
				return i + 1;
			}
		}
		return items.Count + 1;
	}

	private static string Where(int branch, int total)
	{
		return branch <= 0 || branch > total ? "「以上都不满足」那一支" : "第 " + branch + " 支";
	}

	/// <summary>诊断用：为什么看不到「接到哪段剧情」「看过没」——剧本没读到，还是已读系统没就绪，
	/// 还是这一支根本没从剧本里认出对白（arm 编码对不上）。</summary>
	private static string Diagnose(string script, string name, StoryScript story, int total)
	{
		if (story == null)
		{
			return "剧本「" + script + "」没读到（所以没有「接到哪段剧情」和「看过没」）";
		}
		string read = ReadStorySystem.Instance == null ? "已读系统未就绪" : "已读系统在";
		var arms = new List<string>();
		foreach (Row row in Rows)
		{
			arms.Add((row.Branch == 0 ? "兜底" : row.Branch + "支")
				+ "=看过" + row.Seen + "/对白" + KeysOn(story, name, row.Branch, total).Count);
		}
		return "剧本 " + story.Talk.Count + " 句对白；" + read + "；" + string.Join(" ", arms.ToArray());
	}

	private static void Build(string name, List<ConditionResultItem> items, StoryScript story, int now)
	{
		Rows.Clear();
		for (int i = 0; i < items.Count; i++)
		{
			Rows.Add(new Row
			{
				Branch = i + 1,
				// 条件文字带绿、红（满足/不满足），画的时候由 PanelTheme.MapColors 换成宣纸底上看得清的颜色
				Condition = (DataHelper.SwitchBranchCondition(name, i + 1) ?? "").Trim(),
				Where = WhereOn(story, name, i + 1, items.Count),
				Seen = SeenOn(story, name, i + 1, items.Count),
				Taken = now == i + 1,
			});
		}
		// 兜底那一支：Lua 里写 else 的记 arm 0，写 elseif r == 支数+1 的记支数+1，两种都算
		Rows.Add(new Row
		{
			Branch = 0,
			Condition = "以上条件都不满足",
			Where = WhereOn(story, name, 0, items.Count),
			Seen = SeenOn(story, name, 0, items.Count),
			Taken = now > items.Count,
		});
	}

	/// <summary>这一支下面有没有自己的对白或去处。用来判断「改了会不会只是少一句」。</summary>
	private static bool Content(StoryScript story, string name, int branch, int total)
	{
		if (story == null)
		{
			return true; // 读不到剧本就不猜，照常停下
		}
		if (KeysOn(story, name, branch, total).Count > 0)
		{
			return true;
		}
		return WhereOn(story, name, branch, total).Length > 0;
	}

	/// <summary>这一支上的对白 key（分支判断自己这一支下面的台词）。</summary>
	private static List<string> KeysOn(StoryScript story, string name, int branch, int total)
	{
		var keys = new List<string>();
		if (story == null)
		{
			return keys;
		}
		foreach (var (path, _, key) in story.Talk)
		{
			if (string.IsNullOrEmpty(key))
			{
				continue;
			}
			foreach (StoryStep step in path)
			{
				if (step.Kind != StoryStep.Branch || step.Key != name)
				{
					continue;
				}
				bool match = branch == 0 ? step.Arm == 0 || step.Arm == total + 1 : step.Arm == branch;
				if (match && !keys.Contains(key))
				{
					keys.Add(key);
				}
				break;
			}
		}
		return keys;
	}

	/// <summary>这一支接到哪段剧情（写不出就返回空）。</summary>
	private static string WhereOn(StoryScript story, string name, int branch, int total)
	{
		if (story == null)
		{
			return "";
		}
		int[] arms = branch == 0 ? new[] { 0, total + 1 } : new[] { branch };
		return string.Join("；", story.Exits
			.Where(e => e.Path.Any(p => p.Kind == StoryStep.Branch && p.Key == name && arms.Contains(p.Arm)))
			.Select(e => e.ToTitle ? "回到标题（结局）" : e.ToMap ? "回到地图" : e.Target.Length > 0 ? "接到「" + StoryScripts.SegmentName(e.Target) + "」" : "")
			.Where(x => x.Length > 0).Distinct().Take(2).ToArray());
	}

	/// <summary>这一支后面的对白读过多少：1 没看过，2 看过一半，3 都看过，0 不知道。</summary>
	private static int SeenOn(StoryScript story, string name, int branch, int total)
	{
		ReadStorySystem read = ReadStorySystem.Instance;
		List<string> keys = KeysOn(story, name, branch, total);
		if (read == null || keys.Count == 0)
		{
			return 0;
		}
		int seen = keys.Count(k => read.ExistKey(k));
		return seen == 0 ? 1 : seen == keys.Count ? 3 : 2;
	}

	/// <summary>「判定前」存档的备注前缀；清理时只认它，别人的存档一个都不碰。</summary>
	private const string SnapshotNote = "【分支判定】判定前 · ";

	/// <summary>留几张判定前存档：留 1 张只能退回最近这一处，2 张以上连过两处判定也退得回去。</summary>
	private static int SnapshotKeep => Mathf.Clamp(ConfigManager.BranchAheadSnapshots.Value, 1, 10);

	/// <summary>
	/// 停下之前存一个随时存档（走咖啡伴侣自己的 SaveSlot，备注写「【分支判定】判定前 · 判定名」），
	/// 之后在随时存档页里就能读回这处判定、换一条路再走一遍。
	///
	/// 判定点在 <c>checkpointmanager.Switch</c> 上：这时多半正处在「演出中」（busy），咖啡伴侣会判成
	/// 「不在台词或选项上」而拒绝保存。存不成的话就临时把执行位置指到最后一条记录（判定前那句台词）、
	/// 把 busy 压下去再存一次，存完立刻还原 —— 只借它的位置，不改任何游戏数据。
	/// </summary>
	private static string Snapshot(string name)
	{
		AnytimeSave save = AnytimeSave.Instance;
		if (save == null)
		{
			return "随时存档还没初始化，这次不存";
		}
		string note = SnapshotNote + name;
		try
		{
			var tr = Traverse.Create(save);
			// 存成功就把「已保存到随时存档 #N」压掉：每处判定都弹一遍会打断看窗口
			string message = tr.Field("message").GetValue<string>();
			float messageUntil = tr.Field("messageUntil").GetValue<float>();
			string file = save.SaveSlot(note);
			if (string.IsNullOrEmpty(file))
			{
				var trace = tr.Field("trace").GetValue<System.Collections.IList>();
				if (trace != null && trace.Count > 0)
				{
					int active = tr.Field("active").GetValue<int>();
					bool busy = tr.Field("busy").GetValue<bool>();
					tr.Field("active").SetValue(trace.Count - 1);
					tr.Field("busy").SetValue(false);
					try
					{
						file = save.SaveSlot(note);
					}
					finally
					{
						tr.Field("active").SetValue(active);
						tr.Field("busy").SetValue(busy);
					}
				}
			}
			if (string.IsNullOrEmpty(file))
			{
				return "随时存档没让存这次（演出中途），跳过";
			}
			tr.Field("message").SetValue(message);
			tr.Field("messageUntil").SetValue(messageUntil);
			int removed = CleanSnapshots(save, file);
			return "已存「判定前」存档 " + file + (removed > 0 ? "（清掉旧的 " + removed + " 张）" : "");
		}
		catch (System.Exception ex)
		{
			return "判定前存档失败：" + (ex.InnerException ?? ex).Message;
		}
	}

	/// <summary>只清理本 Mod 自己存的判定前存档：备注带前缀的，按新的在前留前几张，其余删掉。别人的一张都不动。</summary>
	private static int CleanSnapshots(AnytimeSave save, string keep)
	{
		List<SaveSummary> mine = save.Saves()
			.Where(s => s != null && s.note != null && s.note.StartsWith(SnapshotNote, System.StringComparison.Ordinal))
			.ToList();
		int removed = 0;
		for (int i = SnapshotKeep; i < mine.Count; i++)
		{
			if (mine[i].file == keep)
			{
				continue;
			}
			try
			{
				// Delete 会顺手弹一句「已删除随时存档」，这里是在判定窗前，压掉它再还原
				var tr = Traverse.Create(save);
				string message = tr.Field("message").GetValue<string>();
				float until = tr.Field("messageUntil").GetValue<float>();
				save.Delete(mine[i].file);
				tr.Field("message").SetValue(message);
				tr.Field("messageUntil").SetValue(until);
				removed++;
			}
			catch (System.Exception ex)
			{
				FileLogger.LogWarning("[分支判定] 清理旧判定前存档失败 " + mine[i].file + "：" + ex.Message);
			}
		}
		return removed;
	}

	/// <summary>屏幕下方说一句就走，不弹窗、不拦剧情。同一处判定在同一次提示没消失前不重复刷。</summary>
	private static void Tip(string key, string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		if (_tipKey == key && Time.unscaledTime - _tipAt < TipLife)
		{
			return;
		}
		_tipKey = key;
		_tipText = text;
		_tipAt = Time.unscaledTime;
	}

	/// <summary>
	/// 把数值改到这一支：前面那些支压到不满足，选中那一支改到满足；「以上都不满足」把每一支都压破。
	/// 改不走就原样停下并说明原因，不动数值。返回 null 表示改好了，否则是给玩家看的原因。
	/// </summary>
	private static string Choose(List<ConditionResultItem> items, int branch)
	{
		var writes = new List<ConditionSolver.Write>();
		for (int i = 0; i < items.Count; i++)
		{
			if (branch != 0 && i + 1 > branch)
			{
				break; // 排在后面的支不用动：判定从第一支起取第一个满足的
			}
			List<ConditionSolver.Write> part = branch != 0 && i + 1 == branch
				? ConditionSolver.Satisfy(items[i])
				: ConditionSolver.Break(items[i]);
			if (part == null)
			{
				return "第 " + (i + 1) + " 支改不动：它的条件里有物品、次数或组合判断，只能靠数值改不了";
			}
			foreach (ConditionSolver.Write write in part)
			{
				ConditionSolver.Write same = writes.FirstOrDefault(w => w.Target == write.Target);
				if (same == null)
				{
					writes.Add(write);
				}
				else if (same.Value != write.Value)
				{
					return "这一支和前面的支要把同一个数值改成两个不同的数，改不走";
				}
			}
		}
		foreach (ConditionSolver.Write write in writes)
		{
			if (!ConditionSolver.Apply(write))
			{
				return "有数值超出上下限（或改不回去），写不进去";
			}
		}
		int now = Current(items);
		int want = branch == 0 ? items.Count + 1 : branch;
		if (now != want)
		{
			return "改完还是走" + Where(now, items.Count) + "：这一支的条件比前面那些宽松，会先碰到前面的";
		}
		FileLogger.Log($"[分支判定] {_name}：改到{Where(want, items.Count)}（改了 {writes.Count} 个数值）");
		return null;
	}

	private void Update()
	{
		if (_state != State.Waiting)
		{
			return;
		}
		// 自愈：状态是「等选择」但没有一行可画，窗口不会出来，脚本却会一直等下去 —— 那才是真的卡死
		if (Rows.Count == 0)
		{
			FileLogger.LogWarning("[分支判定] 状态还停在等选择，但一行都没有了，按原样放行");
			Release(null);
			return;
		}
		// Ctrl 快进正开着：那会一路把对白翻过去，窗口还没看清就被带走了，先把快进停住
		MuteSkip();

		Keyboard kb = Keyboard.current;
		Gamepad pad = Gamepad.current;
		if ((kb != null && kb.upArrowKey.wasPressedThisFrame) || (pad != null && pad.dpad.up.wasPressedThisFrame))
		{
			_highlight = (_highlight + Rows.Count - 1) % Rows.Count;
		}
		if ((kb != null && kb.downArrowKey.wasPressedThisFrame) || (pad != null && pad.dpad.down.wasPressedThisFrame))
		{
			_highlight = (_highlight + 1) % Rows.Count;
		}
		// 不想改就走：空格、点窗口外面、Esc 都算「照现在的数值判定」。
		// 空格和点画面本来就是玩家往下看对白时要按的那一下，跟停下来的成本重合，不用先去找按钮。
		if (kb != null && kb.spaceKey.wasPressedThisFrame)
		{
			Release("按空格没改");
			return;
		}
		if (ClickedOutside())
		{
			Release("点窗口外面没改");
			return;
		}
		if (kb != null && kb.escapeKey.wasPressedThisFrame)
		{
			// 停下来只是为了让玩家选，不想选就按 Esc 照现在的数值判定，跟没有这个功能一样
			Release("按 Esc 没改");
			return;
		}
		if (kb != null)
		{
			for (int i = 0; i < Mathf.Min(9, Rows.Count); i++)
			{
				if (kb[Key.Digit1 + i].wasPressedThisFrame)
				{
					Pick(Rows[i]);
					return;
				}
			}
		}
		if ((kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)) || (pad != null && pad.buttonSouth.wasPressedThisFrame))
		{
			Pick(Rows[_highlight]);
		}
	}

	/// <summary>选这一支：改数值，改得动就放行；改不动就把原因写在窗口里，仍旧停在这里（可以改选别的、或按 Esc 按原样走）。</summary>
	private static void Pick(Row row)
	{
		try
		{
			SwitchResultData data = SwitchRandom.Find(_name);
			List<ConditionResultItem> items = data != null ? Traverse.Create(data).Field("_items").GetValue<List<ConditionResultItem>>() : null;
			if (items == null)
			{
				Release(null);
				return;
			}
			string error = Choose(items, row.Branch);
			if (error != null)
			{
				_message = error;
				return;
			}
			Release("改到" + Where(row.Branch, items.Count));
		}
		catch (System.Exception ex)
		{
			FileLogger.LogWarning("[分支判定] 改数值失败: " + ex.Message);
			_message = "改数值时出错：" + ex.Message;
		}
	}

	/// <summary>
	/// 这一帧有没有点在窗口外面。剧情里点画面本来就是「下一句」，拿它当放行最自然：
	/// 想改的人才去点「满足」，不改的人照常点一下画面就往下走了。
	/// 新弹出的头两帧不算（那一下点击是玩家低头之前手上的动作，也怕把窗口刚画出来时误判成外面）。
	/// </summary>
	private bool ClickedOutside()
	{
		Mouse mouse = Mouse.current;
		if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
		{
			return false;
		}
		if (Time.frameCount - _openedFrame < 2 || _rect.width <= 0f)
		{
			return false;
		}
		Vector2 p = mouse.position.ReadValue();
		return !_rect.Contains(new Vector2(p.x, Screen.height - p.y));
	}

	/// <summary>
	/// Ctrl 快进（游戏里是 <c>StoryManager</c> 上的一个 bool）每帧把「下一句」置上、把时间调快，
	/// 窗口弹出来也会被一路带过去，玩家根本来不及点。窗口开着的时候把这个 bool 压回 false：
	/// 调的是游戏公开的 <c>SkipDialog(false)</c>，它顺带把时间流速恢复正常。
	/// 快进就此中断，玩家松开再按一次 Ctrl 才会继续 —— 正好是「撞上窗口停一下」。
	/// 只动快进这一个开关，不碰输入绑定，也不影响点击翻页。
	/// </summary>
	private static void MuteSkip()
	{
		if (!ConfigManager.BranchAheadStopSkip.Value)
		{
			return;
		}
		StoryManager story = StoryManager.Instance;
		if (story == null)
		{
			return;
		}
		try
		{
			if (_skipDialog == null)
			{
				_skipDialog = AccessTools.FieldRefAccess<StoryManager, bool>("_skipDialog");
			}
			if (!_skipDialog(story))
			{
				// 已经不在快进了：下一次撞上窗口再记一条
				_skipTold = false;
				return;
			}
			story.SkipDialog(false);
			if (!_skipTold)
			{
				_skipTold = true;
				FileLogger.Log("[分支判定] " + _name + "：Ctrl 快进撞上窗口，已中断（松开再按 Ctrl 才继续）");
			}
		}
		catch (System.Exception ex)
		{
			if (_skipErrors++ < 3)
			{
				FileLogger.LogWarning("[分支判定] 中断快进失败（不影响判定）: " + ex.Message);
			}
		}
	}

	/// <summary>放行：下一次问就返回 false，游戏用现在的数值执行判断。</summary>
	internal static void Release(string note)
	{
		if (note != null)
		{
			FileLogger.Log("[分支判定] " + _name + "：" + note
				+ "，继续（时间流速 " + Time.timeScale + (StoryManager.Instance != null ? "，剧情暂停 " + StoryManager.Instance.IsStoryPause : "") + "）");
		}
		_escFrame = Time.frameCount;
		_state = State.Released;
		_message = null;
		Rows.Clear();
	}

	private void BuildStyles(float scale)
	{
		_scale = scale;
		_fontMode = ModFont.Mode;
		_dark = PanelTheme.Dark;
		_opacity = PanelTheme.DialogOpacity;
		_window = PanelTheme.Window(scale);
		_title = PanelTheme.Label(scale, 22, PanelTheme.Ink, false);
		_title.fontStyle = FontStyle.Bold;
		_title.alignment = TextAnchor.MiddleCenter;
		_title.margin = new RectOffset(0, 0, 0, (int)(8f * scale));
		_row = PanelTheme.Button(scale, 19);
		_row.alignment = TextAnchor.MiddleLeft;
		_row.wordWrap = true;
		_rowTaken = new GUIStyle(_row);
		_rowTaken.normal.background = _rowTaken.hover.background = PanelTheme.Framed(PanelTheme.PaperDeep, PanelTheme.Cinnabar);
		_rowTaken.normal.textColor = _rowTaken.hover.textColor = PanelTheme.Cinnabar;
		_hint = PanelTheme.Label(scale, 16, PanelTheme.InkMuted);
		_hint.padding = new RectOffset((int)(12f * scale), 0, 0, (int)(4f * scale));
		_key = PanelTheme.Button(scale, 16);
		_key.margin = new RectOffset((int)(4f * scale), 0, (int)(3f * scale), (int)(3f * scale));
		_keyOn = PanelTheme.Primary(scale, 16);
		_keyOn.margin = _key.margin;
		float width = 760f * scale;
		_rect = new Rect((Screen.width - width) / 2f, 80f * scale, width, 10f);
	}

	private void OnGUI()
	{
		DrawWindow();
		DrawTip();
	}

	private void DrawWindow()
	{
		if (_state != State.Waiting || Rows.Count == 0)
		{
			return;
		}
		float scale = PanelTheme.UiScale;
		if (scale <= 0f)
		{
			return;
		}
		if (ModFont.NeedsRebuild(scale, _scale, _fontMode) || _dark != PanelTheme.Dark || _opacity != PanelTheme.DialogOpacity)
		{
			BuildStyles(scale);
		}
		string layout = _name + "|" + Rows.Count;
		if (layout != _lastLayout && Event.current.type == EventType.Layout)
		{
			_lastLayout = layout;
			_rect.height = 10f;
			_scroll = Vector2.zero;
		}
		_rect = GUILayout.Window(WindowId, _rect, DrawWindowContent, "", _window);
		// 判定时游戏停着等，窗口拖不出屏幕：左右整个留在屏幕里，上下至少露出 40 像素
		_rect.x = Mathf.Clamp(_rect.x, 0f, Mathf.Max(0f, Screen.width - _rect.width));
		_rect.y = Mathf.Clamp(_rect.y, 0f, Mathf.Max(0f, Screen.height - 40f * scale));
		InputGuard.Report("BranchAhead", _rect);
	}

	/// <summary>「这里改了没意义」那种一句话提示：没有窗口的时候也要能出，所以不放进窗口里画。</summary>
	private void DrawTip()
	{
		if (string.IsNullOrEmpty(_tipText) || UiHider.Active)
		{
			return;
		}
		float age = Time.unscaledTime - _tipAt;
		if (age > TipLife)
		{
			_tipText = null;
			return;
		}
		float scale = PanelTheme.UiScale;
		if (scale <= 0f)
		{
			return;
		}
		if (_tip == null || ModFont.NeedsRebuild(scale, _tipScale, _fontMode))
		{
			_tipScale = scale;
			_tip = PanelTheme.Toast(scale, 18);
			_tip.alignment = TextAnchor.MiddleCenter;
		}
		var content = new GUIContent(_tipText);
		float padding = 26f * scale;
		float width = Mathf.Min(_tip.CalcSize(content).x + padding, Screen.width * 0.66f);
		float height = _tip.CalcHeight(content, width - padding) + 12f * scale;
		// 对话框上沿大约在屏幕 72% 处：贴着它往上放，别压住正在看的对白
		float y = Mathf.Max(8f, Screen.height * 0.69f - height);
		float alpha = age > TipLife - 0.9f ? Mathf.Clamp01((TipLife - age) / 0.9f) : 1f;
		Color before = GUI.color;
		GUI.color = new Color(1f, 1f, 1f, alpha);
		GUI.Box(new Rect((Screen.width - width) * 0.5f, y, width, height), content, PanelTheme.Sync(_tip));
		GUI.color = before;
	}

	private void DrawWindowContent(int id)
	{
		// 对白里的绿、红、金是给深色底配的，画窗口期间在宣纸底上换成看得清的同色系
		PanelTheme.Painting = true;
		try
		{
			DrawRows();
		}
		finally
		{
			PanelTheme.Painting = false;
		}
	}

	private void DrawRows()
	{
		GUILayout.Label("这次走哪一支", _title);
		int taken = Rows.FindIndex(r => r.Taken);
		string where = taken < 0 ? "" : Rows[taken].Branch == 0 ? "「以上都不满足」那一支" : "第 " + Rows[taken].Branch + " 支";
		GUILayout.Label((where.Length > 0 ? "按现在的数值会走" + where + "。" : "按现在的数值哪一支都不满足。")
			+ "选一支就把数值改成走那一条路；不想改就按空格、点窗口外面或按 Esc，照现在的数值接着往下走。", _hint);

		float width = _rect.width - _window.padding.horizontal - 24f * _scale;
		float height = 0f;
		for (int i = 0; i < Rows.Count; i++)
		{
			height += _row.CalcHeight(new GUIContent(Label(i)), width) + _row.margin.vertical;
			string note = Note(Rows[i]);
			if (note.Length > 0)
			{
				height += _hint.CalcHeight(new GUIContent(note), width) + _hint.margin.vertical;
			}
		}
		_scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(Mathf.Min(height + 8f, Screen.height * 0.7f)));
		for (int i = 0; i < Rows.Count; i++)
		{
			Row row = Rows[i];
			GUILayout.BeginHorizontal();
			if (GUILayout.Button(Label(i), i == _highlight ? _rowTaken : _row, GUILayout.ExpandWidth(true)))
			{
				Pick(row);
				GUILayout.EndHorizontal();
				GUILayout.EndScrollView();
				return;
			}
			GUILayout.EndHorizontal();
			string note = Note(row);
			if (note.Length > 0)
			{
				GUILayout.Label(note, _hint);
			}
		}
		GUILayout.EndScrollView();

		if (!string.IsNullOrEmpty(_message))
		{
			GUILayout.Label(RichText.Red(_message), _hint);
		}
		GUILayout.BeginHorizontal();
		for (int i = 0; i < Mathf.Min(9, Rows.Count); i++)
		{
			if (GUILayout.Button((i + 1).ToString(), i == _highlight ? _keyOn : _key, GUILayout.Width(34f * _scale)))
			{
				Pick(Rows[i]);
				GUILayout.EndHorizontal();
				return;
			}
		}
		GUILayout.FlexibleSpace();
		if (GUILayout.Button("不改，按现在的数值判定", _key, GUILayout.Width(220f * _scale)))
		{
			Release("没改数值");
			GUILayout.EndHorizontal();
			return;
		}
		GUILayout.EndHorizontal();
		GUI.DragWindow(new Rect(0f, 0f, _rect.width, 28f * _scale));
	}

	/// <summary>一行写什么：第几支、条件、现在会走哪一支、看过没。</summary>
	private static string Label(int i)
	{
		Row row = Rows[i];
		string condition = row.Branch == 0 ? "以上都不满足" : row.Condition.Length > 0 ? row.Condition : "第 " + row.Branch + " 支（条件读不出来）";
		string head = (i < 9 ? (i + 1) + ". " : "") + condition;
		string taken = row.Taken ? "　" + RichText.Golden("← 会走这支") : "";
		// 三种状态都要写出来：只标「没看过」的话，全看过的支看起来跟「读不出」一模一样
		string seen = row.Seen == 1 ? "　" + RichText.Golden("没看过")
			: row.Seen == 2 ? "　（看过一半）"
			: row.Seen == 3 ? "　（看过）"
			: "";
		return PanelTheme.MapColors(head + taken + seen);
	}

	/// <summary>行下面那一行小字：这一支接到哪里。</summary>
	private static string Note(Row row)
	{
		return row.Where.Length > 0 ? PanelTheme.MapColors(row.Where) : "";
	}
}
