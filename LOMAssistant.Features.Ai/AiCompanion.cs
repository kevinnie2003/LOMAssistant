using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using LOMAssistant.Config;
using LOMAssistant.Features.Story;
using Mortal.Core;
using Mortal.Story;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LOMAssistant.Features.Ai;

/// <summary>
/// 主动陪玩：选定的人设（或攻略助手）常驻游戏画面，看到玩家做了选择、掷了骰、打完一场、走完一段剧情，适时说一句看法。
/// 事件从游戏自己的回想记录里读（对白、选择、掷骰、数值和好感变化，都是玩家已经看到的），再加几处只读的钩子（决斗和战役胜负、剧情记录、插画）。
/// 说不说由这里决定：按「话多程度」筛事件、合并挨着的事件、去重、冷却、每小时上限、每次游戏的 token 上限。
/// 每次开口只发一个不带查资料工具的短请求，模型拿不到没发给它的剧情；它只评论和建议，不改存档、不碰剧情走向。
/// 说过的话只留在内存里：同一段剧情里的往来接着带，另外只带最近三句（免得一再说同样的话），重启游戏就没有，不做长期记忆。
/// </summary>
public static class AiCompanion
{
	/// <summary>画面上的一句话。</summary>
	public sealed class Remark
	{
		public string Text = "";
		public string Mood = "";
		/// <summary>当时发给模型的事件，追问时接在问题前面。</summary>
		public string Card = "";
		/// <summary>气泡显示到什么时候（真实时间）。</summary>
		public float Until;
		/// <summary>还在一个字一个字收。</summary>
		public bool Writing;
	}

	private sealed class Happening
	{
		/// <summary>0 = 大事，安静档也说；1 = 正常档起；2 = 话多档才说。</summary>
		public int Weight;
		public string Kind = "";
		/// <summary>去重用：同一件事一次游戏里只说一次。</summary>
		public string Key = "";
		public string Card = "";
		public string Mood = "";
		public float At;
		// 选项、掷骰：之后紧跟着的数值、好感变化算在它头上，开口前才拼成一段话
		public string Chosen = "", OptionKey = "", Shown = "";
		public bool Fate;
		public List<string> Others = new List<string>(), OtherKeys = new List<string>();
		public readonly List<string> Changes = new List<string>();
		/// <summary>小伙伴本人的好感变了多少。</summary>
		public int SelfDelta;
		/// <summary>剧本里这个选项带不带后果：-1 还没查，0 不带，1 带。</summary>
		public int Noted = -1;
		/// <summary>这件事之后又演了几句对白，其中几句是以前读过的：选了、掷了之后走的是不是读过的路，看它。</summary>
		public int After, AfterRead;
	}

	// 事件发生后等这么久没有新事件再开口，这期间的事并成一条
	private const float MergeSeconds = 2.5f;
	// 轮不到开口的事过了这么久就不说了；大事多等一阵（战役打完要等回到剧情里才开口）
	private const float StaleSeconds = 20f, BigStaleSeconds = 90f;
	// 大事可以不等冷却插队一次，但和上一句之间至少隔这么久
	private const float BigGap = 20f;
	// 三档：安静、正常、话多
	private static readonly float[] MinGap = { 300f, 120f, 45f };
	private static readonly int[] HourCap = { 6, 15, 30 };
	// 一段剧情里连着的往来最多留几轮（一问一答算一轮），换剧本清空
	private const int ThreadTurns = 4;

	private static readonly List<Happening> Pending = new List<Happening>();
	private static readonly List<LogItem> Fresh = new List<LogItem>();
	/// <summary>上次开口之后的对白（「说话人：原文」）。</summary>
	private static readonly List<string> Lines = new List<string>();
	private static readonly Queue<bool> ReadMarks = new Queue<bool>();
	private static readonly HashSet<string> Said = new HashSet<string>();
	private static readonly List<float> Spoken = new List<float>();
	private static readonly List<ChatMessage> Thread = new List<ChatMessage>();
	/// <summary>
	/// 最近说过的几句话，每次开口都带上，让它知道自己刚说过什么：换一段剧情 Thread 就清空，
	/// 它看不到之前的话，实测五次打赢有四次以同一句开头。只留在内存里、只留几句，不是长期记忆。
	/// </summary>
	private static readonly List<string> Recent = new List<string>();
	private const int RecentKept = 3;
	private static readonly Dictionary<string, FieldInfo> StoryFields = new Dictionary<string, FieldInfo>();

	private static object _lastLog;
	private static bool _logWasEmpty, _active, _cancelled, _jumped;
	private static string _lastScene = "", _threadScript = "", _system = "", _systemKey = "", _mutedScript, _reactMood = "", _recentPersona = "";
	private static int? _lastRounds;
	private static float _lastEventAt, _lastSpokeAt = -10000f, _loadedAt = -10000f, _quietUntil, _systemAt, _reactUntil;
	private static int _failures;

	/// <summary>现在画面上的那句话；没有就是 null。</summary>
	public static Remark Current { get; private set; }

	/// <summary>最近一句（气泡消失以后追问还用得上）。</summary>
	public static Remark Last { get; private set; }

	public static bool Busy { get; private set; }
	public static int TokensUsed { get; private set; }
	public static int CachedUsed { get; private set; }
	/// <summary>这次游戏的 token 上限到了，不再开口。</summary>
	public static bool Capped { get; private set; }
	/// <summary>最近一次请求出错的原因（第一行）；成功一次就清掉。</summary>
	public static string LastError { get; private set; } = "";

	/// <summary>游戏有回想记录（1.0.6001 起）才用得了。</summary>
	public static bool Supported => StoryLogCompat.Available;

	public static bool On => ConfigManager.AiCompanion.Value && Supported;

	// 随时存档恢复进度时会把剧本飞快重演一遍，那些不是玩家刚做的事
	private static bool Live => On && !LOMAssistant.Features.AnytimeSave.AnytimeSave.Replaying && !LOMAssistant.Features.AnytimeSave.AnytimeSave.Restoring;

	public static int Level => Mathf.Clamp(ConfigManager.AiCompanionLevel.Value, 0, 2);

	/// <summary>陪玩的是谁：null 是攻略助手。和 AI 页现在聊的那位分开设。</summary>
	public static AiPersona Persona => AiPersona.Find(ConfigManager.AiCompanionPersona.Value ?? "");

	/// <summary>玩家让它先别说话：到这段剧情（剧本）结束，或者再点一次。</summary>
	public static bool Muted => _mutedScript != null;

	public static void ToggleMute()
	{
		_mutedScript = Muted ? null : PlayerStatManagerData.Instance?.CurrentStoryScript ?? "";
		if (Muted)
		{
			Pending.Clear();
			Dismiss();
		}
	}

	/// <summary>现在该摆的表情：刚说的那句话的，或者事件一发生先换上的；空 = 平常。</summary>
	public static string Mood
	{
		get
		{
			float now = Time.realtimeSinceStartup;
			if (Last != null && Last.Mood.Length > 0 && now < Last.Until + 8f)
			{
				return Last.Mood;
			}
			return now < _reactUntil ? _reactMood : "";
		}
	}

	/// <summary>这些时候小伙伴不显示、也不开口：标题画面、战役和小游戏里、隐藏界面看 CG、Mod 的窗口开着。</summary>
	public static bool Hidden(string scene)
	{
		return scene == "Title" || scene == "Battle" || scene == "Pinball" || scene == "Break" || scene == "DemoEnd"
			|| UiHider.Active || StoryReviewPanel.IsOpen || EventPicker.IsWaiting || LOMAssistant.Features.Extras.Gallery.IsOpen;
	}

	/// <summary>收起画面上的气泡。</summary>
	public static void Dismiss()
	{
		if (Current != null && !Current.Writing)
		{
			Current.Until = 0f;
			Current = null;
		}
	}

	/// <summary>玩家在 AI 页发问了：正要开口的那句让路。</summary>
	public static void Cancel()
	{
		if (Busy)
		{
			_cancelled = true;
			LlmClient.Abort();
		}
	}

	/// <summary>每帧调一次（画小伙伴的那个组件调）。</summary>
	public static void Tick()
	{
		if (!On)
		{
			if (_active)
			{
				Reset();
				_active = false;
			}
			return;
		}
		_active = true;
		float now = Time.realtimeSinceStartup;
		string scene = SceneManager.GetActiveScene().name;
		if (scene == "Title")
		{
			if (_lastScene != scene)
			{
				Reset();
			}
			_lastScene = scene;
			return;
		}
		try
		{
			ReadLog(now);
			WatchScene(scene, now);
			WatchDate(scene, now);
			if (Current != null && !Current.Writing && now >= Current.Until)
			{
				Current = null;
			}
			if (_mutedScript != null && _mutedScript != (PlayerStatManagerData.Instance?.CurrentStoryScript ?? ""))
			{
				_mutedScript = null;
			}
			Flush(scene, now);
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[AI] 主动陪玩出错（这一帧跳过）: " + ex);
			Pending.Clear();
		}
	}

	private static void Reset()
	{
		Cancel();
		Recent.Clear();
		Pending.Clear();
		Lines.Clear();
		ReadMarks.Clear();
		Thread.Clear();
		_threadScript = "";
		_lastLog = null;
		_logWasEmpty = false;
		_lastRounds = null;
		_mutedScript = null;
		Current = null;
		Last = null;
	}

	// ---------------- 看游戏里发生了什么 ----------------

	private static void ReadLog(float now)
	{
		object before = _lastLog;
		Fresh.Clear();
		StoryLogCompat.ReadNew(ref _lastLog, ref _logWasEmpty, Fresh);
		// 记号换了却没有新条目：读档、新开一局把回想整个换掉了。手上的事和对白都是上一份进度的，丢掉
		if (Fresh.Count == 0 && !ReferenceEquals(before, _lastLog) && before != null)
		{
			_loadedAt = now;
			Pending.Clear();
			Lines.Clear();
			ReadMarks.Clear();
			Thread.Clear();
			return;
		}
		// 随时存档恢复进度时会把剧本飞快重演一遍，那些不是玩家刚做的事
		if (LOMAssistant.Features.AnytimeSave.AnytimeSave.Replaying || LOMAssistant.Features.AnytimeSave.AnytimeSave.Restoring)
		{
			_loadedAt = now;
			Pending.Clear();
			return;
		}
		if (Fresh.Count == 0 || now - _loadedAt < 1f)
		{
			return;
		}
		AiPersona persona = Persona;
		foreach (LogItem item in Fresh)
		{
			Observe(item, now, persona);
		}
	}

	private static void Observe(LogItem item, float now, AiPersona persona)
	{
		string shown = Plain(item.Shown);
		switch (item.Kind)
		{
			case LogItem.Story:
				if (shown.Length > 0)
				{
					Lines.Add(AiKnowledge.Speaker(item.Speaker) + "：" + Short(shown, 60));
					if (Lines.Count > 40)
					{
						Lines.RemoveRange(0, Lines.Count - 40);
					}
					// 这一句以前读过没有：游戏对读过的对白才放开快进（设置里开了「跳过未读」时分不出来，都当没读过）
					bool read = StoryFlag("_enableSkip") && !SystemSettings.SkipUnreadStory;
					ReadMarks.Enqueue(read);
					while (ReadMarks.Count > 8)
					{
						ReadMarks.Dequeue();
					}
					foreach (Happening waiting in Pending)
					{
						waiting.After++;
						waiting.AfterRead += read ? 1 : 0;
					}
				}
				break;
			case LogItem.Choice:
				AddChoice(item, shown, now);
				break;
			case LogItem.Dice:
				if (shown.Length > 0)
				{
					// 用「骰子手动选择」直接点的线路：游戏记下的点数是没掷时的旧数，和这条线路的条件对不上，不拿给模型看，只说走到了哪个结果
					string outcome = item.Args.Length > 0 ? StoryScripts.OptionText(item.Args[0]) : "";
					bool picked = now - LOMAssistant.Features.Dice.DiceManualSelectionPatch.ChosenAt < 10f && outcome.Length > 0 && outcome != item.Args[0];
					Add(new Happening
					{
						Kind = "dice", Weight = 1, Shown = shown, Fate = item.Args.Length > 3 && item.Args[3] == "1", Chosen = picked ? Short(outcome, 60) : "",
						Key = "dice|" + Script() + "|" + item.Key + "|" + (item.Args.Length > 0 ? item.Args[0] : ""),
					}, now);
				}
				break;
			case LogItem.Stat:
			case LogItem.Relationship:
			case LogItem.Talent:
			case LogItem.Message:
				AddChange(item, shown, now, persona);
				break;
		}
	}

	private static void AddChoice(LogItem item, string shown, float now)
	{
		string key = item.Key.StartsWith("O_", StringComparison.Ordinal) ? item.Key
			: OptionTracking.TextKeys.TryGetValue(item.Key, out string known) ? known : StoryScripts.OptionKey(item.Key, out string _);
		bool found = key.StartsWith("O_", StringComparison.Ordinal);
		string chosen = found ? StoryScripts.OptionText(key) : "";
		if (chosen.Length == 0 || chosen == key)
		{
			chosen = shown.Length > 0 ? shown : Plain(item.Key);
		}
		if (chosen.Length == 0)
		{
			return;
		}
		var happening = new Happening
		{
			Kind = "choice", Weight = 2, Chosen = Short(chosen, 40), OptionKey = found ? key : "",
			Key = "choice|" + Script() + "|" + (found ? key : chosen),
		};
		// 同一个菜单里摆着的其他选项（玩家都看到了）
		foreach (string other in OptionInfoPatch.MenuKeys)
		{
			if (other == key || happening.Others.Count >= 4 || !other.StartsWith("O_", StringComparison.Ordinal))
			{
				continue;
			}
			string text = StoryScripts.OptionText(other);
			if (text.Length > 0 && text != other)
			{
				happening.Others.Add(Short(text, 30));
				happening.OtherKeys.Add(other);
			}
		}
		Add(happening, now);
	}

	// 数值、好感、天赋、物品的变化：紧跟在选择、掷骰后面的算它的结果，否则单独算一件事
	private static void AddChange(LogItem item, string shown, float now, AiPersona persona)
	{
		if (shown.Length == 0)
		{
			return;
		}
		Happening open = Pending.LastOrDefault(h => h.Kind == "choice" || h.Kind == "dice" || h.Kind == "change");
		if (open == null)
		{
			open = new Happening { Kind = "change", Weight = 2, Key = "change|" + Script() + "|" + shown };
			Add(open, now);
		}
		if (open.Changes.Count < 8 && !open.Changes.Contains(shown))
		{
			open.Changes.Add(shown);
		}
		if (item.Kind == LogItem.Relationship && persona != null && item.Key == persona.Id && item.Args.Length > 0 && int.TryParse(item.Args[0], out int delta))
		{
			open.SelfDelta += delta;
		}
		_lastEventAt = now;
	}

	private static void Add(Happening happening, float now)
	{
		happening.At = now;
		Pending.Add(happening);
		_lastEventAt = now;
		if (happening.Mood.Length > 0)
		{
			React(happening.Mood, now);
		}
	}

	// 表情先行：事情一发生先换个表情，不花 token
	private static void React(string mood, float now)
	{
		_reactMood = mood;
		_reactUntil = now + 15f;
	}

	private static string Pick(params string[] wanted)
	{
		AiPersona persona = Persona;
		return persona == null ? "" : wanted.FirstOrDefault(w => persona.Moods.Contains(w)) ?? "";
	}

	/// <summary>决斗打完了（钩在 CombatManager.GameOver 上）。</summary>
	internal static void OnDuel(bool win)
	{
		if (!Live)
		{
			return;
		}
		Add(new Happening
		{
			Kind = "duel", Weight = 0, Key = "duel|" + Script() + "|" + win, Card = AiCompanionPrompts.Duel(win),
			Mood = win ? Pick("laugh2", "laugh1", "proud") : Pick("nervous2", "sad2", "shock", "sad1"),
		}, Time.realtimeSinceStartup);
	}

	/// <summary>战役打完了（钩在 GameLevelManager.ShowGameOver 上）。</summary>
	internal static void OnBattle(string type)
	{
		if (!Live)
		{
			return;
		}
		bool win = type == "FriendWin";
		Add(new Happening
		{
			Kind = "battle", Weight = 0, Key = "battle|" + Script() + "|" + type, Card = AiCompanionPrompts.Battle(type),
			Mood = win ? Pick("laugh2", "laugh1", "proud") : Pick("nervous2", "sad2", "shock", "sad1"),
		}, Time.realtimeSinceStartup);
	}

	/// <summary>游戏往剧情记录里写了新的一条（钩在 StatModifyManager.AddStory 上）：一段剧情告一段落，自带一句摘要。</summary>
	internal static void OnRecord(string key)
	{
		if (!Live || string.IsNullOrEmpty(key) || PlayerStatManagerData.Instance?.StoryKeyList?.Contains(key) == true)
		{
			return;
		}
		string legend = StoryScripts.Legend(key);
		if (legend.Length > 0)
		{
			Add(new Happening { Kind = "record", Weight = 1, Key = "record|" + key, Card = AiCompanionPrompts.Record(Short(legend, 200)) }, Time.realtimeSinceStartup);
		}
	}

	/// <summary>画面上出了插画（钩在 StoryMainUI.ShowPicture、ShowBigPicture 上）。</summary>
	internal static void OnPicture(string key)
	{
		if (Live && !string.IsNullOrEmpty(key))
		{
			Add(new Happening { Kind = "picture", Weight = 1, Key = "picture|" + key, Card = AiCompanionPrompts.Picture() }, Time.realtimeSinceStartup);
		}
	}

	// 进了死亡、结局的画面
	private static void WatchScene(string scene, float now)
	{
		if (scene == _lastScene)
		{
			return;
		}
		_lastScene = scene;
		if (scene != "GameOver" && scene != "End")
		{
			return;
		}
		bool death = scene == "GameOver";
		string key = PlayerStatManagerData.Instance?.CurrentSceneKey ?? "", title = "", desc = "";
		try
		{
			LibrarySystem library = LibrarySystem.Instance;
			LibraryItemData data = key.Length == 0 || library == null ? null : death ? library.Dead.Get(key) : library.EndGame.Get(key);
			if (data != null)
			{
				title = Plain(LocalizationManager.Instance.LocaleResolver.GetString(data.GetTitleKey()));
				desc = Short(Plain(LocalizationManager.Instance.LocaleResolver.GetString(data.GetDescKey())), 160);
			}
		}
		catch (Exception ex)
		{
			FileLogger.Log("[AI] 读结局名称失败: " + ex.Message);
		}
		Add(new Happening
		{
			Kind = "ending", Weight = 0, Key = "ending|" + scene + "|" + key, Card = AiCompanionPrompts.Ending(title, desc, death),
			Mood = death ? Pick("shock", "sad2", "cry1", "nervous2") : "",
		}, now);
	}

	// 养成地图上日期往后走了一旬：新的一旬，顺便看有没有快过期的事
	private static void WatchDate(string scene, float now)
	{
		PlayerStatManagerData player = PlayerStatManagerData.Instance;
		int? rounds = StoryAtlas.Rounds(player?.GameTime);
		if (rounds == null || rounds == _lastRounds)
		{
			return;
		}
		bool next = _lastRounds != null && rounds == _lastRounds + 1 && scene == "Free" && now - _loadedAt > 5f;
		_lastRounds = rounds;
		if (!next)
		{
			return;
		}
		Add(new Happening
		{
			Kind = "round", Weight = 1, Key = "round|" + rounds,
			Card = AiCompanionPrompts.NewRound(StoryAtlas.DateText(rounds.Value), player.CurrentActionCount),
		}, now);
		if (!ConfigManager.DeadlineReminder.Value)
		{
			return;
		}
		// 限时剧情提醒列出来的、只剩这一旬或下一旬的
		var urgent = DeadlineReminder.Expiring().Where(i => i.Left <= 1).Take(3).ToList();
		if (urgent.Count > 0)
		{
			Add(new Happening
			{
				Kind = "deadline", Weight = 0, Key = "deadline|" + string.Join("|", urgent.Select(i => i.Title + i.Left).ToArray()),
				Card = AiCompanionPrompts.Deadline(urgent.Select(i => AiCompanionPrompts.DeadlineItem(i.Title, i.Left, i.Place)).ToList()),
				Mood = Pick("serious", "nervous1"),
			}, now);
		}
	}

	// ---------------- 说不说 ----------------

	private static void Flush(string scene, float now)
	{
		if (Pending.Count == 0)
		{
			return;
		}
		var stale = Pending.Where(h => now - h.At > (Weigh(h) == 0 ? BigStaleSeconds : StaleSeconds)).ToList();
		if (stale.Count > 0)
		{
			LogInfo("等太久没轮到，不说了", stale);
			Pending.RemoveAll(stale.Contains);
		}
		if (Pending.Count == 0 || now - _lastEventAt < MergeSeconds || Busy || AiChat.Busy || Hidden(scene) || Current != null)
		{
			return;
		}
		if (Muted || now < _quietUntil || !AiChat.Configured)
		{
			LogInfo(Muted ? "玩家点了暂停发言，不说" : now < _quietUntil ? "连着失败，先停十分钟，不说" : "AI 还没配好，不说", Pending);
			Pending.Clear();
			return;
		}
		int cap = ConfigManager.AiCompanionTokenCap.Value;
		if (cap > 0 && TokensUsed >= cap)
		{
			LogInfo("到了 token 上限，不说", Pending);
			Capped = true;
			Pending.Clear();
			return;
		}
		Capped = false;
		int level = Level;
		// 快进中、重读读过的剧情：只说大事
		bool skipping = StoryFlag("_skipDialog"), rereading = ReadMarks.Count >= 4 && ReadMarks.Count(read => read) * 4 >= ReadMarks.Count * 3;
		var worth = Pending.Where(h => Weigh(h) <= (skipping || Reread(h, rereading) ? 0 : level) && !Said.Contains(h.Key)).OrderBy(Weigh).ThenByDescending(h => h.At).ToList();
		if (worth.Count == 0)
		{
			LogInfo(skipping ? "快进中，只说大事" : "档位不够、是读过的剧情或者说过了，不说", Pending);
			Pending.Clear();
			return;
		}
		bool big = Weigh(worth[0]) == 0;
		float since = now - _lastSpokeAt;
		// 还在冷却：先留着，过期了自然丢掉。大事可以插队一次（也不紧挨着上一句）；上一句就是插队说的，这次得等满冷却，
		// 不然大事一件接一件时每二十秒就说一句，安静档也不安静
		bool cooling = since < MinGap[level];
		if (cooling && !(big && since >= BigGap && !_jumped))
		{
			return;
		}
		Spoken.RemoveAll(t => now - t > 3600f);
		if (Spoken.Count >= HourCap[level] + (big ? 2 : 0))
		{
			LogInfo("这一小时说够了，不说", Pending);
			Pending.Clear();
			return;
		}
		LogInfo("开口", worth);
		// 挨着的几件事并成一条，最要紧的放前面
		worth = worth.Take(3).ToList();
		int spoiler = Mathf.Clamp(ConfigManager.AiSpoiler.Value, 0, 2);
		string card = string.Join("\n", worth.Select(h => Card(h, spoiler)).Where(c => c.Length > 0).ToArray());
		foreach (Happening h in Pending)
		{
			Said.Add(h.Key);
		}
		if (Said.Count > 2000)
		{
			Said.Clear();
		}
		Pending.Clear();
		if (card.Length == 0)
		{
			return;
		}
		string mood = worth.Select(h => MoodOf(h)).FirstOrDefault(m => m.Length > 0) ?? "";
		if (mood.Length > 0)
		{
			React(mood, now);
		}
		_jumped = cooling;
		LOMAssistantPlugin.Instance.StartCoroutine(Speak(scene, card));
	}

	// 这件事是不是在重读读过的剧情。选择、掷骰看它之后演的对白：读过的剧情里选了一条没走过的路，后面是新的，照常说；
	// 之后还没演对白，或者是别的事，看最近几句是不是大多读过
	private static bool Reread(Happening h, bool lately)
	{
		bool picked = h.Kind == "choice" || h.Kind == "dice";
		return picked ? h.After > 0 && h.AfterRead == h.After : lately;
	}

	// 调试记录开着时，把每次说不说、为什么记进 Mod 日志：统计「有多少事、说了几句」用
	private static void LogInfo(string what, List<Happening> list)
	{
		if (AiDebug.On)
		{
			FileLogger.Log("[陪玩] " + what + "：" + string.Join("、", list.Select(h => h.Kind + "/" + Weigh(h) + (h.After > 0 ? "（之后 " + h.After + " 句里读过 " + h.AfterRead + "）" : "")).ToArray()));
		}
	}

	// 选项要到开口前才知道算不算「有后果」：后面跟了变化，或者剧本里这个选项本来就带着数值、旗标、跳转
	private static int Weigh(Happening h)
	{
		if (h.SelfDelta != 0)
		{
			return 0;
		}
		if (h.Kind == "choice")
		{
			if (h.Noted < 0)
			{
				h.Noted = h.OptionKey.Length > 0 && OptionInfoPatch.OptionNote(h.OptionKey).Length > 0 ? 1 : 0;
			}
			return h.Changes.Count > 0 || h.Noted == 1 ? 1 : 2;
		}
		return h.Weight;
	}

	private static string MoodOf(Happening h) => h.SelfDelta > 0 ? Pick("shy", "laugh1", "close_eye_laugh") : h.SelfDelta < 0 ? Pick("angry1", "sad2", "gloomy1", "nervous1") : h.Mood;

	private static readonly Regex Rise = new Regex(@"[+＋]\s*\d+"), Fall = new Regex(@"[-－−]\s*\d+");

	// 给模型的事实。剧透「全说」时多带选项提示里那行后果说明（没选的也带）；「只给方向」时变化不给数字
	private static string Card(Happening h, int spoiler)
	{
		List<string> changes = spoiler == 2 ? h.Changes.Select(c => Fall.Replace(Rise.Replace(c, "↑"), "↓")).ToList() : h.Changes;
		string card;
		switch (h.Kind)
		{
			case "choice":
			{
				string note = "";
				var others = new List<(string, string)>();
				if (spoiler == 0)
				{
					note = h.OptionKey.Length > 0 ? Short(AiKnowledge.Clean(OptionInfoPatch.OptionNote(h.OptionKey)), 160) : "";
					for (int i = 0; i < h.OtherKeys.Count && others.Count < 3; i++)
					{
						string other = Short(AiKnowledge.Clean(OptionInfoPatch.OptionNote(h.OtherKeys[i])), 120);
						if (other.Length > 0)
						{
							others.Add((h.Others[i], other));
						}
					}
				}
				card = AiCompanionPrompts.Choice(h.Chosen, h.Others, changes, note, others);
				break;
			}
			case "dice":
				card = h.Chosen.Length > 0 ? AiCompanionPrompts.DicePicked(h.Chosen, changes) : AiCompanionPrompts.Dice(h.Shown, h.Fate, changes);
				break;
			case "change":
				card = changes.Count > 0 ? AiCompanionPrompts.Changes(changes) : "";
				break;
			default:
				card = h.Card;
				break;
		}
		return h.SelfDelta != 0 && card.Length > 0 ? card + AiCompanionPrompts.SelfAffection(h.SelfDelta > 0) : card;
	}

	// ---------------- 开口 ----------------

	private static IEnumerator Speak(string scene, string card)
	{
		Busy = true;
		_cancelled = false;
		float started = Time.realtimeSinceStartup;
		AiPersona persona = Persona;
		var remark = new Remark { Card = card, Writing = true };
		ChatMessage turn = null;
		List<ChatMessage> send = null;
		try
		{
			turn = Prepare(persona, scene, card, started, out send);
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[AI] 主动陪玩准备这句话时出错: " + ex);
		}
		if (turn == null)
		{
			Busy = false;
			yield break;
		}
		LlmReply reply = null;
		// 思考照模型平时的样子开着（说出来的话更好），思考也算在输出额度里：话只有一两句，额度仍给够，免得想到一半被截断。
		// 问答的「回答方式」（极速、标准、深度）不管这里：主动陪玩只有一种说法，不分轻重
		yield return LlmClient.Complete(AiChat.Provider, ConfigManager.AiBaseUrl.Value, ConfigManager.AiApiKey.Value, Model, _system, send, false, ReplyTokens, r => reply = r, false,
			text =>
			{
				// 一边收一边显示：收到第一个字气泡才出来。界面是英、韩、日文时先攒十来个字，看清不是中文再露面；流到一半发现是中文就收起来
				string shown = AiChat.Showable(text);
				remark.Text = Foreign(shown) || (AiPrompts.RewriteRule != null && shown.Length < 12) ? "" : shown;
				if (remark.Text.Length > 0 && Current == null && !_cancelled)
				{
					remark.Until = float.MaxValue;
					Current = remark;
				}
				else if (remark.Text.Length == 0)
				{
					Hide(remark);
				}
			}, true);
		string said = null, mood = "";
		try
		{
			said = Receive(reply, persona, out mood);
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[AI] 主动陪玩整理这句话时出错: " + ex);
		}
		// 界面是英、韩、日文，说出来的却是中文（给它的现状、事件和人设补充是中文写的）：和问答一样，拿这句话再请它改写成界面语言
		if (said != null && Foreign(said))
		{
			Hide(remark);
			FileLogger.Log("[AI] 主动陪玩的话不是界面语言，让它改写一遍");
			LlmReply rewritten = null;
			yield return LlmClient.Complete(AiChat.Provider, ConfigManager.AiBaseUrl.Value, ConfigManager.AiApiKey.Value, Model, AiPrompts.RewriteRule + AiPrompts.RewriteSpeaker(persona),
				new List<ChatMessage> { new ChatMessage { Role = ChatMessage.User, Text = said } }, false, ReplyTokens, r => rewritten = r, false, null, true);
			try
			{
				said = Rewritten(rewritten);
			}
			catch (Exception ex)
			{
				said = null;
				FileLogger.LogWarning("[AI] 主动陪玩改写这句话时出错: " + ex);
			}
		}
		Busy = false;
		remark.Writing = false;
		if (said == null || !Show(remark, said, mood, turn, started))
		{
			Hide(remark);
		}
	}

	// 界面是英、韩、日文时，这句话是不是中文写的（简体、繁体界面不用查）。一两句话里汉字过了三个、又比本国文字多，就算
	private static bool Foreign(string text) => AiPrompts.RewriteRule != null && AiChat.WrongLanguage(text, "", TextConverter.DisplayLanguage, 3);

	// 陪玩用的模型：单独设了就用它，否则是问答平时的模型（深度模式的强模型不拿来说这一两句话）
	private static string Model => ConfigManager.AiCompanionModel.Value.Trim().Length > 0 ? ConfigManager.AiCompanionModel.Value.Trim() : ConfigManager.AiModel.Value;

	// 一句话的输出额度（含思考）、带给它看的最近对白句数
	private const int ReplyTokens = 1500, LinesSent = 12;

	// 拼好要发的内容：系统提示词、这段剧情里前面的往来、这一次的现状和事件
	private static ChatMessage Prepare(AiPersona persona, string scene, string card, float started, out List<ChatMessage> send)
	{
		// 系统提示词一段时间里保持不变（服务商的缓存靠它）：换人、换语言、换剧透档，或者过了十分钟（经历多了几条）才重拼
		// （玩家放在这位文件夹里的人设文字、表情图改过也重拼）
		string systemKey = (persona?.Id ?? "") + "|" + AiCustom.StampFor(persona) + "|" + TextConverter.DisplayLanguage + "|" + ConfigManager.AiSpoiler.Value + "|" + StoryAtlas.Ready;
		if (systemKey != _systemKey || started - _systemAt > 600f)
		{
			_system = AiCompanionPrompts.SystemPrompt(persona);
			_systemKey = systemKey;
			_systemAt = started;
			Thread.Clear();
		}
		// 同一段剧情里前面说过的事和话接着带上，换了剧本或带得够多了就从头来
		string script = Script();
		if (script != _threadScript || Thread.Count >= ThreadTurns * 2)
		{
			Thread.Clear();
			_threadScript = script;
		}
		var lines = Lines.Skip(Math.Max(0, Lines.Count - LinesSent)).ToList();
		// 最近说过的几句是这一位说的；换了人就不带
		if ((persona?.Id ?? "") != _recentPersona)
		{
			Recent.Clear();
			_recentPersona = persona?.Id ?? "";
		}
		var turn = new ChatMessage { Role = ChatMessage.User, Text = AiCompanionPrompts.Turn(AiCompanionPrompts.State(persona, scene), lines, Recent, card) };
		send = Thread.Concat(new[] { turn }).ToList();
		AiDebug.Turn(AiCompanionPrompts.DebugWho(persona), _system, turn.Text, Model);
		return turn;
	}

	// 回复回来了：记用量，去掉记号、取出表情。返回 null 是这句不显示
	private static string Receive(LlmReply reply, AiPersona persona, out string mood)
	{
		mood = "";
		if (_cancelled)
		{
			return null;
		}
		if (reply == null || reply.Error != null)
		{
			LastError = (reply?.Error ?? "").Split('\n')[0];
			FileLogger.LogWarning("[AI] 主动陪玩这次没说成: " + (reply?.Error ?? "没有回复").Replace("\n", " "));
			// 连着三次不成（额度用完、被限流、断网）：安静十分钟，不一直去撞
			if (++_failures >= 3)
			{
				_failures = 0;
				_quietUntil = Time.realtimeSinceStartup + 600f;
			}
			return null;
		}
		_failures = 0;
		LastError = "";
		TokensUsed += reply.InputTokens + reply.OutputTokens;
		CachedUsed += reply.CachedTokens;
		AiDebug.Reply(reply);
		string said = AiChat.StripIds(AiChat.StripToolMarkup(reply.Text ?? ""), true).Trim();
		said = persona != null ? AiChat.TakeMood(said, persona, out mood) : AiChat.Showable(said);
		if (TextConverter.DisplayLanguage == "ChineseTraditional")
		{
			said = TextConverter.ToTraditional(said);
		}
		return said.Length > 0 ? said : null;
	}

	// 改写回来的话：记用量。改写不成、改完还是中文就不显示：宁可少说一句，也不把中文给别的语言的玩家看
	private static string Rewritten(LlmReply rewritten)
	{
		if (_cancelled || rewritten == null || rewritten.Error != null)
		{
			FileLogger.LogWarning("[AI] 主动陪玩改写没成，这句不显示: " + (rewritten?.Error ?? "没有回复").Replace("\n", " "));
			return null;
		}
		TokensUsed += rewritten.InputTokens + rewritten.OutputTokens;
		CachedUsed += rewritten.CachedTokens;
		AiDebug.Reply(rewritten);
		string text = AiChat.Showable(AiChat.StripIds(AiChat.StripToolMarkup(rewritten.Text ?? ""), true)).Trim();
		return text.Length > 0 && !Foreign(text) ? text : null;
	}

	// 把这句话放上画面，记进这段剧情的往来。等得太久（气泡里只有她的话，隔太久玩家对不上是哪件事）就不显示
	private static bool Show(Remark remark, string said, string mood, ChatMessage turn, float started)
	{
		float now = Time.realtimeSinceStartup;
		if (now - started > 45f)
		{
			return false;
		}
		remark.Text = Short(said, 200);
		remark.Mood = mood;
		remark.Until = now + Mathf.Clamp(4f + said.Length * 0.12f, 5f, 15f);
		Current = remark;
		Last = remark;
		Recent.Add(Short(said, 80));
		if (Recent.Count > RecentKept)
		{
			Recent.RemoveAt(0);
		}
		Thread.Add(turn);
		Thread.Add(new ChatMessage { Role = ChatMessage.Assistant, Text = said });
		Lines.Clear();
		_lastSpokeAt = now;
		Spoken.Add(now);
		return true;
	}

	private static void Hide(Remark remark)
	{
		if (Current == remark)
		{
			Current = null;
		}
	}

	// ---------------- 小工具 ----------------

	private static string Script() => PlayerStatManagerData.Instance?.CurrentStoryScript ?? "";

	// 游戏文字去掉富文本标记、换行，汉字转简体（日文不动）
	private static string Plain(string text) => TextConverter.ToSimplified(RichText.Strip(text ?? "")).Replace("\r", "").Replace("\n", " ").Trim();

	private static string Short(string text, int max) => text.Length > max ? text.Substring(0, max) + "…" : text;

	// StoryManager 的私有开关（_skipDialog 正在快进、_enableSkip 这一句读过）
	private static bool StoryFlag(string name)
	{
		StoryManager story = StoryManager.Instance;
		if (story == null)
		{
			return false;
		}
		if (!StoryFields.TryGetValue(name, out FieldInfo field))
		{
			StoryFields[name] = field = AccessTools.Field(typeof(StoryManager), name);
		}
		return field != null && field.GetValue(story) is bool on && on;
	}
}
