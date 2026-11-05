using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using LOMAssistant.Features.Story;

namespace LOMAssistant.Tests;

public static partial class Tests
{
	/// <summary>多分支选项先逐项翻译，再拼接编号和后续说明；原始描述仍可供内部比较。</summary>
	public static void TestOptionEffectBranches()
	{
		const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		FieldInfo language = Mod.GetType("LOMAssistant.Config.ConfigManager").GetField("UiLanguage", flags);
		object saved = language.GetValue(null);
		object setting = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(language.FieldType);
		var actions = new List<LuaParse.Action>();
		void Add(string description, int branch)
		{
			var action = new LuaParse.Action(LuaParse.ActionType.BattleScene, "Combat", branch > 0, branch);
			typeof(LuaParse.Action).GetProperty("Description").SetValue(action, description);
			actions.Add(action);
		}
		Add("设置称号 8", 0);
		Add("남궁심好感 +2", 1);
		Add("银两 +2000", 2);
		try
		{
			language.SetValue(null, setting);
			language.FieldType.GetField("m_value", flags).SetValue(setting, 3);
			string shown = (string)typeof(LuaParse.LuaParser).GetMethod("DisplayDescription", flags).Invoke(null, new object[] { actions });
			Assert(shown.Contains("칭호 8") && shown.Contains("이어서: ①남궁심 호감 +2") && shown.Contains("②은자 +2000") && !Regex.IsMatch(shown, @"[\u3400-\u9fff]"), shown);
			Assert(LuaParse.LuaParser.Description(actions) == "设置称号 8；后续: ①남궁심好感 +2 ②银两 +2000", "原始描述或分支编号改变");
			foreach (var row in new[] { (3, "스토리 발생"), (4, "ストーリー発生"), (5, "starts a scene") })
			{
				language.FieldType.GetField("m_value", flags).SetValue(setting, row.Item1);
				string jump = TextConverter.ForDisplay("若 心相 需<30：触发剧情");
				Assert(jump.Contains("30") && jump.Contains(row.Item2), jump);
			}
		}
		finally
		{
			language.SetValue(null, saved);
		}
	}

	/// <summary>快存悬停提示单独翻译备注表头，不把玩家备注当作表头的一部分。</summary>
	public static void TestAnytimeMemoHeading()
	{
		var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
		FieldInfo languageSetting = Mod.GetType("LOMAssistant.Config.ConfigManager").GetField("UiLanguage", flags);
		object savedSetting = languageSetting.GetValue(null);
		object setting = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(languageSetting.FieldType);
		FieldInfo value = languageSetting.FieldType.GetField("m_value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		MethodInfo context = Mod.GetType("LOMAssistant.Features.Story.StoryReviewPanel").GetMethod("AnytimeContext", flags);
		var summary = new LOMAssistant.Features.AnytimeSave.SaveSummary { number = 1, note = "시험 메모\nTEST_ID = 1" };
		try
		{
			languageSetting.SetValue(null, setting);
			foreach (var row in new[] { (3, "메모:"), (4, "メモ："), (5, "Note: "), (1, "备注："), (2, "備註：") })
			{
				value.SetValue(setting, row.Item1);
				string actual = (string)context.Invoke(null, new object[] { summary });
				Assert(actual.EndsWith("\n\n" + row.Item2 + summary.note), "快存备注表头或正文被改动：" + row.Item1);
			}
		}
		finally
		{
			languageSetting.SetValue(null, savedSetting);
		}
	}

	/// <summary>选择窗口保留着时切换语言：只更新显示，去处、选中项和执行计划不变。</summary>
	public static void TestChoiceDisplayLanguages()
	{
		const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		FieldInfo languageSetting = Mod.GetType("LOMAssistant.Config.ConfigManager").GetField("UiLanguage", flags);
		object savedSetting = languageSetting.GetValue(null);
		object setting = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(languageSetting.FieldType);
		FieldInfo value = languageSetting.FieldType.GetField("m_value", flags);
		Type choiceType = Mod.GetType("LOMAssistant.EventPicker").GetNestedType("Choice", flags);
		PropertyInfo displayEffects = choiceType.GetProperty("DisplayEffects");
		MethodInfo choose = typeof(StoryJump).GetMethod("ChooseNamed", flags);
		MethodInfo refresh = typeof(StoryJump.Fork).GetMethod("RefreshOptions", flags);
		string T(string text) => LOMAssistant.TextConverter.ForDisplay(text);
		string Short(string text) => text.Length > 40 ? text.Substring(0, 40) + "…" : text;
		const string condition = "旗标攻略人数_三个高手_排除龙湘 >=1";
		const string effects = "<color=#A3302A>不满足</color>　" + condition;
		string[] exits = { "接到「DIALOGUE；VALUE」", "回到标题（结局）" };
		try
		{
			languageSetting.SetValue(null, setting);
			foreach (int initial in Enumerable.Range(1, 5))
			{
				value.SetValue(setting, initial);
				object choice = Activator.CreateInstance(choiceType, true);
				choiceType.GetField("Effects").SetValue(choice, effects);
				choiceType.GetField("Exits").SetValue(choice, exits);
				choiceType.GetField("Key").SetValue(choice, "Switch:TEST#2");
				choiceType.GetField("Forced").SetValue(choice, 2);
				var mission = new StoryMission { Key = "MISSION_ID" };
				var plan = new StoryJump.Plan { Mission = mission, Script = "SCRIPT_ID" };
				plan.Choices["FORK_ID"] = 1;
				plan.Arms["CHECK_ID"] = 2;
				plan.Commands.Add(("METHOD_ID", new[] { "ARG_ID" }));
				int formats = 0;
				Func<List<string>> labels = () =>
				{
					formats++;
					return new List<string> { "「OPTION_ID」　（要 " + Short(T(condition)) + "）", "按游戏判断" };
				};
				int picked = (int)choose.Invoke(null, new object[] { plan, mission, "FORK_ID", "SCRIPT_ID", labels(), 0, true, labels });
				StoryJump.Fork fork = plan.Forks.Single();
				var options = fork.Options;
				foreach (int language in new[] { initial, 3, 5, 4, 2, 1, initial })
				{
					value.SetValue(setting, language);
					string actual = (string)displayEffects.GetValue(choice);
					string expected = effects + "\n" + string.Join("；", exits.Select(T));
					Assert(actual == expected && T(actual) == T(expected), "分支去处应按当前语言逐项翻译");
					Assert(actual.Contains("DIALOGUE；VALUE"), "去处文字内的分号不能拆开");
					refresh.Invoke(fork, null);
					Assert(fork.Options[0] == "「OPTION_ID」　（要 " + Short(T(condition)) + "）", "条件应重新翻译后截短，不能沿用旧语言");
					int before = formats;
					refresh.Invoke(fork, null);
					Assert(formats == before, "同一语言不应重复生成选项");
					Assert(ReferenceEquals(options, fork.Options) && fork.Options.Count == 2 && fork.Picked == picked && picked == 1 && fork.Key == "FORK_ID", "刷新显示不能替换选项容器或改变选中项");
					Assert(plan.Choices["FORK_ID"] == 1 && plan.Arms["CHECK_ID"] == 2 && plan.Commands.Single().Method == "METHOD_ID" && plan.Commands[0].Args.Single() == "ARG_ID", "刷新显示不能重算或改动执行计划");
					Assert((string)choiceType.GetField("Effects").GetValue(choice) == effects && exits.SequenceEqual((string[])choiceType.GetField("Exits").GetValue(choice)), "原始条件和去处应保留");
					Assert((string)choiceType.GetField("Key").GetValue(choice) == "Switch:TEST#2" && (int)choiceType.GetField("Forced").GetValue(choice) == 2, "事件编号与分支编号不变");
				}
				choiceType.GetField("Exits").SetValue(choice, null);
				Assert((string)displayEffects.GetValue(choice) == effects, "普通事件沿用原来的说明路径");
				choiceType.GetField("Exits").SetValue(choice, new string[0]);
				Assert((string)displayEffects.GetValue(choice) == effects, "没有去处时不增加空行");
				choiceType.GetField("Exits").SetValue(choice, exits);
				choiceType.GetField("Effects").SetValue(choice, "");
				Assert((string)displayEffects.GetValue(choice) == string.Join("；", exits.Select(T)), "没有条件时不增加空行");
				var plain = new StoryJump.Fork { Key = "PLAIN_ID", Picked = 1 };
				plain.Options.AddRange(new[] { "RAW_1", "RAW_2" });
				refresh.Invoke(plain, null);
				Assert(plain.Options.SequenceEqual(new[] { "RAW_1", "RAW_2" }) && plain.Picked == 1, "没有格式化回调的选项维持原状");
			}
		}
		finally
		{
			languageSetting.SetValue(null, savedSetting);
		}
	}

	/// <summary>随机事件名随界面语言刷新，包括未满足的事件；编号、权重和规则不受影响。</summary>
	public static void TestEventPickerLabelLanguages()
	{
		const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		Type config = Mod.GetType("LOMAssistant.Config.ConfigManager"), picker = Mod.GetType("LOMAssistant.EventPicker");
		FieldInfo language = config.GetField("UiLanguage", flags), rules = config.GetField("EventRules", flags);
		object savedLanguage = language.GetValue(null), savedRules = rules.GetValue(null);
		object setting = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(language.FieldType);
		object ruleSetting = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(rules.FieldType);
		FieldInfo value = language.FieldType.GetField("m_value", flags);
		FieldInfo ruleValue = rules.FieldType.GetField("m_value", flags);
		Type choiceType = picker.GetNestedType("Choice", flags);
		FieldInfo dataField = choiceType.GetField("Data"), branch = picker.GetField("_branchMode", flags);
		IList choices = (IList)picker.GetField("Choices", flags).GetValue(null);
		object[] savedChoices = choices.Cast<object>().ToArray();
		object savedBranch = branch.GetValue(null);
		object data = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(dataField.FieldType);
		FieldInfo note = dataField.FieldType.GetField("_devNote", flags);
		// 非零标记只供 Unity 的 null 判断；下面只读托管备注，不调用原生对象方法。
		FieldInfo native = typeof(UnityEngine.Object).GetField("m_CachedPtr", flags);
		MethodInfo getName = Mod.GetType("LOMAssistant.DataHelper").GetMethod("GetEventName"), label = picker.GetMethod("Label", flags);
		try
		{
			native.SetValue(data, new IntPtr(1));
			language.SetValue(null, setting);
			ruleValue.SetValue(ruleSetting, "");
			rules.SetValue(null, ruleSetting);
			branch.SetValue(null, false);
			foreach (string raw in new[] { "P_大失敗_002", "和小梅跑步", "小梅", "UNKNOWN_EVENT" })
			{
				note.SetValue(data, raw);
				foreach (int initial in Enumerable.Range(1, 5))
				{
					value.SetValue(setting, initial);
					object choice = Activator.CreateInstance(choiceType, true);
					string cached = LOMAssistant.TextConverter.ToSimplified((string)getName.Invoke(null, new object[] { data, true }));
					dataField.SetValue(choice, data);
					choiceType.GetField("Name").SetValue(choice, cached);
					choiceType.GetField("Key").SetValue(choice, "EVENT_ID");
					choiceType.GetField("Rate").SetValue(choice, 10);
					choices.Clear();
					choices.Add(choice);
					foreach (int current in Enumerable.Range(1, 5))
					{
						value.SetValue(setting, current);
						string name = (string)getName.Invoke(null, new object[] { data, true });
						foreach (bool unmet in new[] { false, true })
						{
							choiceType.GetField("Unmet").SetValue(choice, unmet);
							string actual = (string)label.Invoke(null, new object[] { 0, 10 });
							string expected = unmet ? name + "　<color=#73644F>条件没满足</color>" : "1. " + name + "（100%）";
							Assert(actual == expected, "事件名应按当前语言读取，不能沿用旧译名或把日语转成简体：" + raw);
							Assert((string)choiceType.GetField("Name").GetValue(choice) == cached && (string)choiceType.GetField("Key").GetValue(choice) == "EVENT_ID" && (int)choiceType.GetField("Rate").GetValue(choice) == 10, "取显示名不能改动缓存、编号或权重");
							Assert((string)ruleValue.GetValue(ruleSetting) == "", "显示不应写入事件规则");
						}
					}
				}
			}
		}
		finally
		{
			native.SetValue(data, IntPtr.Zero);
			choices.Clear();
			foreach (object choice in savedChoices) choices.Add(choice);
			branch.SetValue(null, savedBranch);
			language.SetValue(null, savedLanguage);
			rules.SetValue(null, savedRules);
		}
	}

	/// <summary>选项事件提示按当前语言重建，同语言沿用缓存，不清掉事件池和分支分析结果。</summary>
	public static void TestMapHintLanguages()
	{
		const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		FieldInfo language = Mod.GetType("LOMAssistant.Config.ConfigManager").GetField("UiLanguage", flags);
		object savedLanguage = language.GetValue(null);
		object setting = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(language.FieldType);
		FieldInfo value = language.FieldType.GetField("m_value", flags);
		Type hintType = Mod.GetType("LOMAssistant.PositionOptionHints"), map = Mod.GetType("LOMAssistant.MapEventHints");
		object hint = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(hintType);
		FieldInfo key = hintType.GetField("_key", flags), text = hintType.GetField("_text", flags), shownLanguage = hintType.GetField("_displayLanguage", flags);
		MethodInfo describe = hintType.GetMethod("Describe", flags);
		IDictionary groups = (IDictionary)map.GetField("GroupCache", flags).GetValue(null);
		var groupKey = ("", "");
		bool hadGroup = groups.Contains(groupKey);
		object savedGroup = groups[groupKey], empty = Activator.CreateInstance(groups.GetType().GetGenericArguments()[1]);
		Type playerType = Assembly.Load("Mortal.Core").GetType("Mortal.Core.PlayerStatManagerData");
		FieldInfo playerField = playerType.GetField("_instance", flags), native = typeof(UnityEngine.Object).GetField("m_CachedPtr", flags);
		object savedPlayer = playerField.GetValue(null), player = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(playerType);
		FieldInfo lastScript = map.GetField("LastScript", flags);
		object savedScript = lastScript.GetValue(null);
		try
		{
			// 只读托管字段；空事件组避免访问场景，非零标记仅供 Unity 的 null 判断。
			native.SetValue(player, new IntPtr(1));
			playerField.SetValue(null, player);
			lastScript.SetValue(null, null);
			groups[groupKey] = empty;
			language.SetValue(null, setting);
			foreach (int initial in Enumerable.Range(1, 5))
			{
				foreach (int current in Enumerable.Range(1, 5))
				{
					value.SetValue(setting, initial);
					string cached = LOMAssistant.TextConverter.ForDisplay("随机事件") + " CACHED_EVENT";
					key.SetValue(hint, "");
					text.SetValue(hint, cached);
					shownLanguage.SetValue(hint, LOMAssistant.TextConverter.DisplayLanguage);
					value.SetValue(setting, current);
					string actual = (string)describe.Invoke(hint, new object[] { null });
					Assert(actual == (initial == current ? cached : ""), "切换语言必须重建提示，同语言应复用缓存");
					Assert((string)shownLanguage.GetValue(hint) == LOMAssistant.TextConverter.DisplayLanguage, "提示缓存应记录当前语言");
					Assert(ReferenceEquals(groups[groupKey], empty), "切换语言不能清掉已分析的事件池");
					text.SetValue(hint, cached);
					key.SetValue(hint, "OLD_OPTION");
					Assert((string)describe.Invoke(hint, new object[] { null }) == "", "更换选项仍应重建提示");
				}
			}
		}
		finally
		{
			native.SetValue(player, IntPtr.Zero);
			playerField.SetValue(null, savedPlayer);
			lastScript.SetValue(null, savedScript);
			if (hadGroup) groups[groupKey] = savedGroup; else groups.Remove(groupKey);
			language.SetValue(null, savedLanguage);
		}
	}

	/// <summary>去处按剧本编号比较；同名、截短、排序和显示语言都不能改变分支判断。</summary>
	public static void TestBranchDestinationIdentity()
	{
		const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		FieldInfo seen = Mod.GetType("LOMAssistant.Config.ConfigManager").GetField("BranchPickerSeen", flags);
		object savedSeen = seen.GetValue(null);
		object setting = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(seen.FieldType);
		FieldInfo seenValue = seen.FieldType.GetField("m_value", flags);
		try
		{
			seen.SetValue(null, setting);
			foreach (bool enabled in new[] { false, true })
			{
				seenValue.SetValue(setting, enabled);
				Type picker = Mod.GetType("LOMAssistant.BranchPicker");
				Type type = picker.GetNestedType("Arm", BindingFlags.NonPublic);
				var exits = type.GetField("Exits");
				var raw = type.GetProperty("Exit");
				var key = picker.GetMethod("DestinationKey", BindingFlags.Static | BindingFlags.NonPublic);
				void SetDestinations(object arm, params StoryExit[] destinations)
				{
					var keys = (HashSet<string>)type.GetField("Destinations").GetValue(arm);
					keys.Clear();
					keys.UnionWith(destinations.Select(e => (string)key.Invoke(null, new object[] { e })).Where(k => k.Length > 0));
				}
				StoryExit Script(string id) => new StoryExit { Target = id };
				object Arm(int branch, params StoryExit[] destinations)
				{
					object arm = Activator.CreateInstance(type, true);
					type.GetField("Branch").SetValue(arm, branch);
					SetDestinations(arm, destinations);
					return arm;
				}
				object first = Arm(1, Script("ch8_4_11_1_1")), second = Arm(2, Script("ch8_4_11_1_2"));
				var arms = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type));
				arms.Add(first);
				arms.Add(second);
				var worth = picker.GetMethod("Worth");
				bool Worth(int predicted = 1) => (bool)worth.Invoke(null, new object[] { arms, predicted });
				const string sameTitle = "接到「애초에 당신을 한 번 낚아채 …」";
				exits.SetValue(first, new[] { sameTitle });
				exits.SetValue(second, new[] { sameTitle });
				Assert(Worth() && Worth(2), "官方韩文标题截短后相同，两个不同剧本仍应提示");
				string[] destinations = { "接到「DIALOGUE；VALUE」", "回到标题（结局）" };
				exits.SetValue(second, destinations);
				string original = string.Join("；", destinations);
				foreach (string language in new[] { "Korean", "Japanese", "English", "ChineseSimplified", "ChineseTraditional" })
				{
					string display = string.Join("；", ((string[])exits.GetValue(second)).Select(s => LOMAssistant.UiText.Translate(s, language)));
					Assert(display.Contains("DIALOGUE；VALUE"), language + "：去处内容中的分号不能当作列表边界");
					Assert((string)raw.GetValue(second) == original, language + "：显示不能改动原文去处");
					Assert(Worth(), language + "：显示不能影响分支判断");
					exits.SetValue(first, new[] { display });
					exits.SetValue(second, new[] { display });
					Assert(Worth(), language + "：显示完全相同也不能合并不同剧本");
					exits.SetValue(second, destinations);
				}
				SetDestinations(first, Script("A"), Script("B"), Script("C"));
				SetDestinations(second, Script("c"), Script("A"), Script("B"), Script("A"));
				Assert(!Worth(), "相同剧本集合的顺序、重复项和大小写不应影响判断");
				SetDestinations(second, Script("A"), Script("B"), Script("D"));
				Assert(Worth(), "第三个去处不同，即使显示只列前两个也应提示");
				SetDestinations(first, new StoryExit { ToMap = true });
				SetDestinations(second, new StoryExit { ToTitle = true });
				Assert(Worth(), "返回地图与标题应区分");
				SetDestinations(second, Script("map"));
				Assert(Worth(), "剧本编号不能与返回地图的类型混淆");
				SetDestinations(first, new StoryExit { ToTitle = true });
				SetDestinations(second, Script("title"));
				Assert(Worth(), "剧本编号不能与返回标题的类型混淆");
				SetDestinations(first, new StoryExit { ToMap = true, Target = "IGNORED" });
				SetDestinations(second, new StoryExit { ToMap = true });
				Assert(!Worth(), "返回地图优先于剧本编号，沿用原来的去处含义");
				SetDestinations(first, new StoryExit { ToTitle = true, ToMap = true, Target = "IGNORED" });
				SetDestinations(second, new StoryExit { ToTitle = true });
				Assert(!Worth(), "返回标题优先于地图和剧本编号");
				SetDestinations(first, new StoryExit());
				SetDestinations(second, new StoryExit());
				Assert(!Worth(), "没有去处也没有未读对白时仍不提示");
				SetDestinations(second, Script("A"));
				Assert(Worth() && !Worth(2), "无去处的另一支仍不单独触发提示");
				Assert(!Worth(0), "算不出当前分支且没有未读对白时仍不提示");
				type.GetField("Unread").SetValue(second, 1);
				Assert(Worth(0), "算不出当前分支时仍应提示未读对白");
				SetDestinations(first, Script("A"));
				Assert(Worth(), "去处相同时仍应提示未读对白");
				// 已读对白是否停下只看设置，不能改变目的地比较。
				type.GetField("Unread").SetValue(second, 0);
				type.GetField("Lines").SetValue(second, 1);
				Assert(Worth() == enabled, "同一去处的已读分支应按「已读分支也暂停」设置判断");
			}
		}
		finally
		{
			seen.SetValue(null, savedSeen);
		}
	}

	// ---------------- AI 陪玩 ----------------

	/// <summary>回答用错语言的判断：界面是英、韩、日时，中文回答要改写；本来就是界面语言、或玩家自己用中文问的不改。</summary>
	public static void TestWrongLanguage()
	{
		Func<string, string, string, bool> wrong = (a, q, lang) => LOMAssistant.Features.Ai.AiChat.WrongLanguage(a, q, lang);
		string zh = "（低头拨弄手套）这事我拿不准。我不懂那些大道理，只知道回来的路上，大家都像老了十岁。要我说，我不想再有人去送命了。";
		string en = "(She tugs at her gloves.) I'm not good at big decisions. If you choose to fight, I'll be right beside you.";
		string ko = "(장갑 끝을 만지작거린다) 그런 걸 갑자기 물으면 어떡해. 나 말 잘 못 하는 거 알면서. 네가 뭘 고르든 나는 따라갈 거야.";
		string ja = "（手袋の端をいじる）そんなこと急に聞かれても困るよ。あなたが何を選んでも、わたしはついていくから。";
		Assert(wrong(zh, "What should I choose here?", "English"), "英文界面、英文提问、中文回答，应该判成用错语言");
		Assert(!wrong(en, "What should I choose here?", "English"), "英文回答不该判错");
		Assert(!wrong(zh, "这里怎么选？", "English"), "玩家自己用中文问，中文回答不算错");
		Assert(wrong(zh, "여기서 뭘 고르면 좋을까?", "Korean"), "韩文界面、中文回答，应该判错");
		Assert(!wrong(ko, "여기서 뭘 고르면 좋을까?", "Korean"), "韩文回答不该判错");
		Assert(wrong(zh, "ここはどれを選べばいい？", "Japanese"), "日文界面、中文回答，应该判错");
		Assert(!wrong(ja, "ここはどれを選べばいい？", "Japanese"), "日文回答（有假名）不该判错");
		Assert(!wrong(zh, "这里怎么选？", "ChineseSimplified"), "简体界面从不判错");
		Assert(!wrong("「战他娘亲」和「退隐江湖」只差决策值。" + en, "What?", "English"), "「」里引的中文原文不算");
	}

	/// <summary>地点触发提示的识别与译表一致，切换语言后仍能识别之前追加的提示。</summary>
	public static void TestTriggerHintTranslations()
	{
		var hint = Mod.GetType("LOMAssistant.OptionInfoPatch").GetMethod("IsTriggerHint", BindingFlags.Static | BindingFlags.NonPublic);
		var remove = Mod.GetType("LOMAssistant.OptionInfoPatch").GetMethod("RemoveTriggerHint", BindingFlags.Static | BindingFlags.NonPublic);
		bool HasHint(string text) => (bool)hint.Invoke(null, new object[] { text });
		string Remove(string text) => (string)remove.Invoke(null, new object[] { text });
		foreach (string language in new[] { "Korean", "Japanese", "English", "ChineseSimplified", "ChineseTraditional" })
		{
			string text = LOMAssistant.UiText.Translate("可触发：MISSION_ID", language);
			Assert(HasHint("DIALOGUE_KEY(" + text + ")"), language + "：应识别实际生成的触发提示");
			foreach (string title in new[] { "MISSION_ID", "폐사당 (하)", "TITLE (part (2))" })
			{
				text = LOMAssistant.UiText.Translate("可触发：" + title, language);
				const string original = "DIALOGUE_KEY(original note)";
				Assert(Remove(original + "(" + text + ")") == original, language + "：任务名中的括号不能截断原对白或留下半条提示");
			}
		}
		Assert(HasHint("DIALOGUE_KEY(可觸發：MISSION_ID)"), "保留繁体提示识别");
		Assert(!HasHint("DIALOGUE_KEY") && !HasHint("DIALOGUE_KEY(MISSION_ID)"), "没有触发提示的对白不应被识别");
		Assert(Remove("DIALOGUE_KEY(可觸發：TITLE (下))") == "DIALOGUE_KEY", "繁体提示中的括号");
		Assert(Remove("DIALOGUE_KEY(note)") == "DIALOGUE_KEY(note)" && Remove(null) == null && Remove("") == "", "无提示时保留原文和空值");
	}

	/// <summary>详情先翻译各个去处再拼接；原来的 Describe 仍返回原文，不改变非界面调用。</summary>
	public static void TestStoryDescriptionFormatting()
	{
		Type scripts = Mod.GetType("LOMAssistant.Features.Story.StoryScripts");
		Type exitType = Mod.GetType("LOMAssistant.Features.Story.StoryExit");
		var raw = scripts.GetMethod("Describe", new[] { exitType });
		var formatted = scripts.GetMethod("Describe", BindingFlags.Static | BindingFlags.NonPublic, null, new[] { exitType, typeof(Func<string, string>) }, null);
		object exit = Activator.CreateInstance(exitType);
		exitType.GetField("ToMap").SetValue(exit, true);
		exitType.GetField("MainMission").SetValue(exit, "TEST_MAIN_123");
		const string original = "回到地图，主线推进到「TEST_MAIN_123」";
		Assert((string)raw.Invoke(null, new[] { exit }) == original, "原文说明的顺序、标点和编号不能改变");
		foreach (string language in new[] { "Korean", "Japanese", "English", "ChineseSimplified", "ChineseTraditional" })
		{
			Func<string, string> format = text => LOMAssistant.UiText.Translate(text, language);
			string actual = (string)formatted.Invoke(null, new object[] { exit, format });
			Assert(actual.Contains("TEST_MAIN_123"), language + "：主线编号应保留");
			if (language == "Korean")
			{
				Assert(actual == "지도로 돌아감，주요 이야기 「TEST_MAIN_123」(으)로 진행", "返回地图和推进主线都应翻译");
			}
			if (language.StartsWith("Chinese"))
			{
				Assert(actual == original, language + "：原文拼接不变");
			}
			Assert((string)raw.Invoke(null, new[] { exit }) == original, language + "：显示转换不能影响原文重载");
		}
	}

	/// <summary>流程图按原始路径合并：相同显示文案不能让不同分岔共用标识。</summary>
	public static void TestFlowPathIdentity()
	{
		var method = Mod.GetType("LOMAssistant.Features.Story.StoryReviewPanel").GetMethod("FlowPathKey", BindingFlags.Static | BindingFlags.NonPublic);
		var stepType = Mod.GetType("LOMAssistant.Features.Story.StoryStep");
		object Step(string kind, string key, int arm)
		{
			object step = Activator.CreateInstance(stepType);
			stepType.GetField("Kind").SetValue(step, kind);
			stepType.GetField("Key").SetValue(step, key);
			stepType.GetField("Arm").SetValue(step, arm);
			return step;
		}
		string Key(params object[] steps)
		{
			Array path = Array.CreateInstance(stepType, steps.Length);
			for (int i = 0; i < steps.Length; i++)
			{
				path.SetValue(steps[i], i);
			}
			return (string)method.Invoke(null, new object[] { path });
		}
		object a = Step("条件", "checkpoint_1", 1), b = Step("条件", "checkpoint_1", 2);
		Assert(Key(a) == Key(Step("条件", "checkpoint_1", 1)), "同一分岔的标识应一致");
		Assert(Key(a) != Key(b), "不同支不能合并");
		Assert(Key(a, b) != Key(b, a), "分岔顺序不能丢失");
		Assert(Key(a) != Key(Step("选项", "checkpoint_1", 1)), "不同分岔类型不能合并");
		Assert(Key(a) != Key(Step("条件", "checkpoint_2", 1)), "不同检查点不能合并");
		Assert(Key(Step("a:b", "c", 1)) != Key(Step("a", "b:c", 1)), "字段分隔符不能造成标识冲突");
		Assert(Key(Step("a", "b:1|c:d", 2)) != Key(Step("a", "b", 1), Step("c", "d", 2)), "路径分隔符不能造成标识冲突");
	}
}
