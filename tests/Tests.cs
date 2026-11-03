using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using LOMAssistant.Features.Story;

namespace LOMAssistant.Tests;

/// <summary>
/// 不开游戏就能跑的测试：直接载入编出来的 LOMAssistant.dll（和安装器），测纯逻辑和数据表。
/// 碰到游戏引擎的代码（Unity 的场景、游戏数据）这里跑不了，那些还得进游戏看。
/// 运行：DOTNET=... ./tests/run-tests.sh，每个 Test 开头的静态方法是一项，抛异常算失败，抛 Skip 算跳过。
/// </summary>
internal static class Program
{
	private static readonly string Repo = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
	private static string _game;

	private static int Main(string[] args)
	{
		Console.OutputEncoding = System.Text.Encoding.UTF8;
		_game = args.Length > 0 ? args[0] : "D:/Steam/steamapps/common/LegendOfMortal";
		// LOMAssistant.dll 引用了游戏和 BepInEx 的程序集：到游戏目录里找
		AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
		{
			string name = new AssemblyName(e.Name).Name + ".dll";
			foreach (string dir in new[] { Path.Combine(_game, "Mortal_Data", "Managed"), Path.Combine(_game, "BepInEx", "core"), Path.Combine(_game, "BepInEx", "plugins", "LOMAssistant") })
			{
				string path = Path.Combine(dir, name);
				if (File.Exists(path))
				{
					return Assembly.LoadFrom(path);
				}
			}
			return null;
		};
		int passed = 0, failed = 0, skipped = 0;
		foreach (MethodInfo test in new[] { typeof(Tests), typeof(AiTests) }.SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public).Where(m => m.Name.StartsWith("Test")).OrderBy(m => m.MetadataToken)))
		{
			try
			{
				test.Invoke(null, null);
				passed++;
				Console.WriteLine("通过  " + test.Name);
			}
			catch (TargetInvocationException e) when (e.InnerException is Skip skip)
			{
				skipped++;
				Console.WriteLine("跳过  " + test.Name + "：" + skip.Message);
			}
			catch (TargetInvocationException e)
			{
				failed++;
				Console.WriteLine("失败  " + test.Name + "：\n      " + (e.InnerException?.Message ?? e.Message).Replace("\n", "\n      "));
			}
		}
		Console.WriteLine();
		Console.WriteLine($"{passed} 项通过，{failed} 项失败，{skipped} 项跳过");
		return failed == 0 ? 0 : 1;
	}

	internal static string RepoPath(params string[] parts) => Path.Combine(new[] { Repo }.Concat(parts).ToArray());
	internal static string Game => _game;
}

internal sealed class Skip : Exception
{
	public Skip(string message) : base(message)
	{
	}
}

public static class Tests
{
	private static void Assert(bool ok, string message)
	{
		if (!ok)
		{
			throw new Exception(message);
		}
	}

	private static void Expect(List<string> problems)
	{
		if (problems.Count > 0)
		{
			throw new Exception(problems.Count + " 处：\n" + string.Join("\n", problems.Take(20).ToArray()) + (problems.Count > 20 ? "\n……" : ""));
		}
	}

	private static Assembly Mod => typeof(LOMAssistant.UiText).Assembly;

	private static object Static(Type type, string field) => type.GetField(field, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(null)
		?? throw new Exception(type.Name + "." + field + " 读不到");

	// 「{0}」「{1}」这类占位符：译文里少了或多了，string.Format 会报错或者漏显示
	private static string Holes(string s) => string.Join(",", Regex.Matches(s ?? "", @"\{\d+\}").Cast<Match>().Select(m => m.Value).Distinct().OrderBy(x => x).ToArray());

	// ---------------- 界面文字表 ----------------

	/// <summary>韩语表要接到实际显示路径：官方术语、组合条件、编号和富文本都保留。</summary>
	public static void TestKoreanUiTranslations()
	{
		var cases = new[]
		{
			new[] { "嘴力", "구력" },
			new[] { "嘴攻", "구공" },
			new[] { "备揍", "맞을 준비" },
			new[] { "绝招", "절초" },
			new[] { "银两", "은자" },
			new[] { "心上人", "마음에 둔 사람" },
			// 好感页的内部别名保留编号，韩语名称清楚区分各个条目。
			new[] { "主角", "조활" },
			new[] { "虞小梅_探险队长", "우소매_탐사대장" },
			new[] { "鹤手", "우소매의 학 손모양" },
			new[] { "叶云啾_1", "엽운추_1" },
			new[] { "叶云啾_2", "엽운추_2" },
			new[] { "好感 叶云啾_1", "호감도 엽운추_1" },
			new[] { "<b>叶云啾_2</b>", "<b>엽운추_2</b>" },
			new[] { "马富贵", "이부귀" },
			new[] { "马富贵在唐门", "이부귀가 당문에 머무름" },
			new[] { "认识马富贵", "이부귀와 구면" },
			// 游戏消息窗口也复用已有的旗标名称和通知句式。
			new[] { "【旗标】决斗泛用事件旗标1  1 → 0", "【플래그】결투 공통 이벤트 플래그1  1 → 0" },
			new[] { "【旗标】UNKNOWN_FLAG  -2 → 12", "【플래그】UNKNOWN_FLAG  -2 → 12" },
			new[] { "嘴力 需>=40 → 当前35", "구력 필요 >=40 → 현재 35" },
			new[] { "<b>嘴力 需>=40 → 当前35</b>", "<b>구력 필요 >=40 → 현재 35</b>" },
			new[] { "心上人 需=郁竹", "마음에 둔 사람 필요 =욱죽" },
			new[] { "好感 郁竹 · 好感 小梅", "호감도 욱죽 · 호감도 우소매" },
			new[] { "旗标M81012 = 1", "플래그 M81012 = 1" },
			new[] { "← 唐门会议", "← 당문회의" },
			new[] { "→ 唐门会议", "→ 당문회의" },
			new[] { "✓ 唐门会议", "✓ 당문회의" },
			new[] { "남궁심(南宮深)", "남궁심(南宮深)" },
			new[] { "강릉부(江陵府)", "강릉부(江陵府)" },
		};
		foreach (string[] row in cases)
		{
			string actual = LOMAssistant.UiText.Translate(row[0], LOMAssistant.UiText.Korean);
			Assert(actual == row[1], row[0] + "：预期「" + row[1] + "」，实际「" + actual + "」");
		}
	}

	/// <summary>名称表共用原文、韩语、日语、英语四列；未提供的译文用 null，不重复原文键。</summary>
	public static void TestUiNameTables()
	{
		foreach (string name in new[] { "UiConditionNames", "UiFlagNames", "UiQuestNames", "UiEventNames" })
		{
			var seen = new HashSet<string>();
			foreach (string[] row in (string[][])Static(Mod.GetType("LOMAssistant." + name), "Table"))
			{
				Assert(row.Length == 4 && !string.IsNullOrWhiteSpace(row[0]), name + "：名称表格式不对");
				Assert(seen.Add(row[0]), name + "：原文重复「" + row[0] + "」");
				foreach (string text in row.Skip(1).Where(text => text != null))
				{
					Assert(!string.IsNullOrWhiteSpace(text) && Holes(text) == Holes(row[0]), name + "：译文或占位符不对「" + row[0] + "」");
				}
			}
		}
	}

	// 旧测试仍按「原文、译文」对照，但数据来自共用表的当前语言列。
	private static string[][] NameTable(string name, string language)
	{
		int column = Array.IndexOf(new[] { "", "Korean", "Japanese", "English" }, language);
		return ((string[][])Static(Mod.GetType("LOMAssistant." + name), "Table"))
			.Where(row => column > 0 && column < row.Length && row[column] != null)
			.Select(row => new[] { row[0], row[column] }).ToArray();
	}

	/// <summary>补上任一语言列就能显示；切换语言不串用译文，缺译仍返回原来的回退结果。</summary>
	public static void TestUiNameColumns()
	{
		string[] languages = { "Korean", "Japanese", "English" };
		string[] fields = { "_korean", "_japanese", "_english" };
		var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
		string[] names = { "UiConditionNames", "UiFlagNames", "UiQuestNames", "UiEventNames" };
		var tables = names.Select(name => (string[][])Static(Mod.GetType("LOMAssistant." + name), "Table")).ToArray();
		var savedRows = tables.Select(table => table[table.Length - 1]).ToArray();
		var caches = fields.Select(field => typeof(LOMAssistant.UiText).GetField(field, flags)).ToArray();
		var savedCaches = caches.Select(field => field.GetValue(null)).ToArray();
		Type events = Mod.GetType("LOMAssistant.UiEventNames");
		FieldInfo eventCache = events.GetField("_names", flags);
		object savedEventCache = eventCache.GetValue(null);
		FieldInfo languageSetting = Mod.GetType("LOMAssistant.Config.ConfigManager").GetField("UiLanguage", flags);
		object savedSetting = languageSetting.GetValue(null);
		object setting = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(languageSetting.FieldType);
		FieldInfo value = languageSetting.FieldType.GetField("m_value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		string[] keys = { "测试条件", "测试旗标", "测试任务", "測試事件" };
		try
		{
			string[] fallbacks = keys.Take(3).Select(key => LOMAssistant.UiText.Translate(key, "English")).ToArray();
			languageSetting.SetValue(null, setting);
			for (int i = 0; i < tables.Length; i++)
			{
				tables[i][tables[i].Length - 1] = new[] { keys[i], "검사" + i, "テスト" + i, "Test" + i };
			}
			foreach (FieldInfo cache in caches)
			{
				cache.SetValue(null, null);
			}
			eventCache.SetValue(null, null);
			foreach (int i in new[] { 0, 1, 2, 1, 0 })
			{
				string Expected(int table) => tables[table][tables[table].Length - 1][i + 1];
				Assert(LOMAssistant.UiText.Translate(keys[0] + ">=40", languages[i]) == Expected(0) + ">=40", languages[i] + "：条件列未接入");
				Assert(LOMAssistant.UiText.Translate(keys[1] + " (TEST_FLAG) 需=1", languages[i]).StartsWith(Expected(1) + " (TEST_FLAG)"), languages[i] + "：旗标列未接入");
				Assert(LOMAssistant.UiText.Translate(keys[2], languages[i]) == Expected(2), languages[i] + "：任务列未接入");
				value.SetValue(setting, i + 3);
				Assert((string)events.GetMethod("Display").Invoke(null, new object[] { keys[3] }) == Expected(3), languages[i] + "：事件列或语言切换不对");
			}
			for (int i = 0; i < tables.Length; i++)
			{
				tables[i][tables[i].Length - 1][3] = null;
			}
			caches[2].SetValue(null, null);
			for (int i = 0; i < fallbacks.Length; i++)
			{
				Assert(LOMAssistant.UiText.Translate(keys[i], "English") == fallbacks[i], "英语缺译时应沿用原来的回退：" + keys[i]);
			}
			value.SetValue(setting, 5);
			Assert(events.GetMethod("Display").Invoke(null, new object[] { keys[3] }) == null, "事件缺译应返回 null");
			Assert((string)events.GetMethod("Display").Invoke(null, new object[] { "P_大失敗_002" }) == "P_Critical failure_002", "结果事件应保留前缀与编号");
			foreach (string language in new[] { "ChineseSimplified", "ChineseTraditional" })
			{
				Assert(LOMAssistant.UiText.Translate(keys[0] + ">=40", language) == keys[0] + ">=40", language + "：原文应保持不变");
			}
			value.SetValue(setting, 2);
			Assert(events.GetMethod("Display").Invoke(null, new object[] { keys[3] }) == null, "中文事件应沿用原来的显示路径");
		}
		finally
		{
			for (int i = 0; i < tables.Length; i++)
			{
				tables[i][tables[i].Length - 1] = savedRows[i];
			}
			for (int i = 0; i < caches.Length; i++)
			{
				caches[i].SetValue(null, savedCaches[i]);
			}
			eventCache.SetValue(null, savedEventCache);
			languageSetting.SetValue(null, savedSetting);
		}
	}

	/// <summary>比较条件与嵌套句式使用同一语言表，并保留符号、编号和富文本。</summary>
	public static void TestUiTextComposition()
	{
		var aliases = new[]
		{
			"攻略小师妹 (攻略人數)", "贵重品_连环火箭",
			"与飞石帮进入交战 (交戰門派)", "与崆峒派进入交战 (交戰門派_無飛石幫)",
			"攻略小师妹 (攻略人數_排除龍湘)", "攻略小梅 (攻略人數_梅竹菊)",
			"攻略小师妹 (攻略人數_三個高手_排除龍湘)", "条件_没有攻略龙湘",
			"攻略上官萤 (攻略人數_排除小師妹)", "攻略小师妹 (攻略人數_排除梅竹菊)",
			"攻略小师妹 (攻略人數_排除梅)", "天道 (六道總點數)", "条件_晚安且收留小梅"
		};
		foreach (string language in new[] { "Korean", "Japanese" })
		{
			var table = NameTable("UiConditionNames", language);
			foreach (string alias in aliases)
			{
				string[] row = table.FirstOrDefault(r => r[0] == alias);
				if (row == null)
				{
					Assert(language != "Korean", alias + "：缺少韩语条件名");
					continue;
				}
				foreach (string op in new[] { ">=", "<=", "!=", "=", ">", "<", "≥", "≤", "≠" })
				{
					foreach (string space in new[] { "", " ", "\t" })
					{
						string source = alias + space + op + space + "-40";
						string expected = row[1] + space + op + space + "-40";
						Assert(LOMAssistant.UiText.Translate(source, language) == expected, language + ": " + source);
						Assert(LOMAssistant.UiText.Translate("<b>" + source + "</b>", language) == "<b>" + expected + "</b>", language + "：富文本 " + source);
					}
				}
			}
		}
		foreach (string[] row in new[]
		{
			new[] { "Korean", "接到「接到「养成」」", "「「육성」(으)로 이어짐」(으)로 이어짐" },
			new[] { "English", "接到「接到「养成」」", "Leads to 「Leads to 「Training」」" },
			new[] { "Korean", "看剧情图：第2层", "스토리 흐름도 보기: 2단계" },
			new[] { "Japanese", "看剧情图：第2层", "ストーリー図を見る：第2層" },
			new[] { "English", "看剧情图：第2层", "Story map: Layer 2" },
			new[] { "Korean", "<b>看剧情图：第2层</b>", "<b>스토리 흐름도 보기: 2단계</b>" },
			new[] { "Korean", "看剧情图：남궁심(南宮深)", "스토리 흐름도 보기: 남궁심(南宮深)" },
			new[] { "English", "看剧情图：M12345", "Story map: M12345" },
			new[] { "Japanese", "看剧情图：unknown {9}", "ストーリー図を見る：unknown {9}" }
		})
		{
			Assert(LOMAssistant.UiText.Translate(row[1], row[0]) == row[2], row[0] + ": " + row[1]);
		}
		foreach (string language in new[] { "ChineseSimplified", "ChineseTraditional" })
		{
			foreach (string source in aliases.Select(a => a + " ≥ 40").Concat(new[] { "接到「接到「养成」」" }))
			{
				Assert(LOMAssistant.UiText.Translate(source, language) == source, language + "：应原样返回 " + source);
			}
		}
		string deep = string.Concat(Enumerable.Repeat("看剧情图：", 50)) + "M12345";
		foreach (string language in new[] { "Korean", "Japanese", "English" })
		{
			string result = LOMAssistant.UiText.Translate(deep, language);
			Assert(result.EndsWith("M12345") && result.Contains("看剧情图："), language + "：达到嵌套上限后应保留剩余文字");
			Assert(LOMAssistant.UiText.Translate(deep, language) == result, language + "：缓存结果发生变化");
		}
	}

	/// <summary>选中标记、条件提示和热键列表嵌套后仍按各自语言翻译，未知文字与分隔符保留。</summary>
	public static void TestNestedUiLists()
	{
		string J(string text) => LOMAssistant.UiText.Translate(text, "Japanese");
		Assert(J("崆峒·飞天殿（Section_01_01）") == "崆峒·" + J("飞天殿") + "（Section_01_01）", "相同日文句式也要翻译内部场所名并保留编号");
		Assert(J("【注意】还没有随时存档") == "【注意】" + J("还没有随时存档"), "日文提示前缀不变时仍翻译内部句子");
		foreach (string language in new[] { "Korean", "Japanese", "English" })
		{
			string T(string text) => LOMAssistant.UiText.Translate(text, language);
			foreach (string marker in new[] { "○ ", "● " })
			{
				const string condition = "贡献度 >=10";
				string source = marker + condition + "　（没读过 1 句）";
				string expected = marker + T(condition) + "　" + T("（没读过 1 句）");
				Assert(T(source) == expected, language + "：选中标记里的条件与未读提示应分别翻译");
				Assert(T("<b>" + source + "</b>") == "<b>" + expected + "</b>", language + "：保留选项的富文本");
				source = marker + "「DIALOGUE_01」　（要 " + condition + "）";
				Assert(T(source) == marker + "「DIALOGUE_01」　" + T("（要 " + condition + "）"), language + "：条件前缀不能漏译");
			}
			const string list = "数值页、设置页、UNKNOWN_KEY";
			string translated = T("数值页") + T("、") + T("设置页") + T("、") + "UNKNOWN_KEY";
			Assert(T(list) == translated, language + "：列表逐项翻译并保留未知名称");
			string notice = "检测到「Example Mod」的快捷键和本 Mod 撞了，本 Mod 的{0}热键已让给它（改成不绑定）。可以在设置页「热键」里另绑别的键。";
			string template = string.Format(notice, "LIST_VALUE");
			if (T(template) != template) // 没有整句译文的语言仍走原来的回退。
			{
				Assert(T(string.Format(notice, list)) == T(template).Replace("LIST_VALUE", translated), language + "：提示参数中的热键列表");
			}
			const string spaced = "FLAG_ID ≥ 1  〈VALUE…";
			Assert(T("○ 「DIALOGUE_01」　（要 " + spaced + "）") == "○ 「DIALOGUE_01」　" + T("（要 " + spaced + "）"), language + "：条件内部空格不能拆开外层句式");
			if (language != "English")
			{
				const string flag = "(解锁[崆峒探险队强袭六道峡]、[启程])";
				string name = NameTable("UiFlagNames", language).Single(r => r[0] == flag)[1];
				Assert(T("旗标TEST_ID〔" + flag + "〕 = 1").Contains(name), language + "：带顿号的完整旗标名优先于列表分段");
			}
		}
		foreach (string language in new[] { "ChineseSimplified", "ChineseTraditional" })
		{
			const string source = "● 贡献度 >=10　（没读过 1 句）、数值页、设置页";
			Assert(LOMAssistant.UiText.Translate(source, language) == source, language + "：保留中文原文");
		}
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

	/// <summary>流程图省略「需」并转成简体后，仍识别完整条件名和右侧变量。</summary>
	public static void TestCompactConditionTranslations()
	{
		foreach (string language in new[] { "Korean", "Japanese" })
		{
			var table = NameTable("UiConditionNames", language);
			foreach (string key in new[] { "攻略小师妹 (攻略人數_排除龍湘)", "攻略小师妹 (攻略人數_三個高手_排除龍湘)" })
			{
				string[] row = table.Single(r => r[0] == key);
				string simplified = LOMAssistant.TextConverter.ToSimplified(key);
				Assert(LOMAssistant.UiText.Translate(simplified + ">=40", language) == row[1] + ">=40", language + "：简体条件名别名");
			}
			foreach (string op in new[] { ">=", "<=", "!=", "=", ">", "<", "≥", "≤", "≠" })
			{
				foreach (string right in new[] { "好感度_三师兄", "随机值_0_40" })
				{
					string leftText = table.Single(r => r[0] == "好感度_二师兄")[1];
					string rightText = table.Single(r => r[0] == right)[1];
					string source = "好感度_二师兄" + op + right;
					string expected = leftText + op + rightText;
					Assert(LOMAssistant.UiText.Translate(source, language) == expected, language + ": " + source);
					Assert(LOMAssistant.UiText.Translate("<b>" + source + "</b>", language) == "<b>" + expected + "</b>", language + "：比较条件的富文本标签");
				}
			}
		}
		Assert(LOMAssistant.UiText.Translate("心上人!=自己", "Korean") == "마음에 둔 사람!=자신", "简略条件中的右侧名称");
		Assert(LOMAssistant.UiText.Translate("그 외 (공략 중인 인원 수(소사매·상… 미충족)", "Korean") == "그 외 (공략 중인 인원 수(소사매·상… 미충족)", "已翻译的缩略条件应保持原样");
		foreach (string language in new[] { "Korean", "Japanese", "English" })
		{
			string time = NameTable("UiConditionNames", language).Single(r => r[0] == "时间_目前时间")[1];
			Assert(LOMAssistant.UiText.Translate("时间_目前时间<=40", language) == time + "<=40", language + "：缓存条件带时间前缀时仍识别完整变量名");
		}
		foreach (string language in new[] { "Korean", "Japanese" })
		{
			string name = NameTable("UiConditionNames", language).Single(r => r[0] == "全武学点数")[1];
			string actual = LOMAssistant.UiText.Translate("否则（以下都不满足：全武学点数<200；全武学点数<120）", language);
			Assert(actual.Contains(name + "<200；" + name + "<120"), language + "：完整否则说明中的每条条件都应翻译，保留分号与数值");
			string item = NameTable("UiConditionNames", language).Single(r => r[0] == "贵重品_连环火箭")[1];
			Assert(LOMAssistant.UiText.Translate("全武学点数<200；贵重品_连环火箭>=1", language) == name + "<200；" + item + ">=1", language + "：分号后的完整条件名优先于其中的物品名");
		}
	}

	/// <summary>实际界面组合：任务编号、地点、选择、掷骰和否则分支。</summary>
	public static void TestUiDisplaySentences()
	{
		foreach (string[] row in new[]
		{
			new[] { "Korean", "M7001_02 · 围攻唐门 强制发生", "M7001_02 · 당문 포위 공격 (강제 발생)" },
			new[] { "Korean", "M12345 · 反间谍 - 限时", "M12345 · 방첩 - 시간 제한" },
			new[] { "Korean", "位置：无", "위치: 없음" },
			new[] { "Korean", "位置：正心堂", "위치: 정심당" },
			new[] { "Korean", "位置：校场", "위치: 연무장" },
			new[] { "Korean", "位置：炼丹房", "위치: 연단방" },
			new[] { "Korean", "位置：锻冶场", "위치: 대장간" },
			new[] { "Korean", "选「못 본 척한다」", "「못 본 척한다」 선택" },
			new[] { "Korean", "选「가서 살펴보자」（需 嘴力 需>=40）", "「가서 살펴보자」 선택 (필요: 구력 필요 >=40)" },
			new[] { "Korean", "掷骰「떨어져 죽었다」（<75）", "주사위 「떨어져 죽었다」 (<75)" },
			new[] { "Korean", "否则（不满足：贡献度>=0）", "그 외 (공헌도>=0 미충족)" },
			new[] { "Korean", "在支线「猴儿酒」（任意时间）", "곁가지 이야기「후아주」(언제든지)에서" },
			new[] { "Korean", "额外行动次数 1", "추가 행동 횟수 1" },
			new[] { "Korean", "额外行动次数 2", "추가 행동 횟수 2" },
			new[] { "Korean", "额外行动次数 3", "추가 행동 횟수 3" },
			new[] { "Japanese", "M7001_02 · 围攻唐门 强制发生", "M7001_02 · 唐門包囲戦（強制発生）" },
			new[] { "Japanese", "位置：炼丹房", "場所：錬丹房" },
			new[] { "English", "位置：炼丹房", "Place: Alchemy room" },
			new[] { "English", "掷骰「Keep this dialogue」（<75）", "Roll 「Keep this dialogue」 (<75)" },
			new[] { "English", "M7001_02 - unknown_key", "M7001_02 - unknown_key" },
			new[] { "Korean", "M7001_02 · 남궁심(南宮深)", "M7001_02 · 남궁심(南宮深)" },
		})
		{
			string actual = LOMAssistant.UiText.Translate(row[1], row[0]);
			Assert(actual == row[2], row[0] + ": " + row[1] + " => " + actual);
		}
		foreach (string language in new[] { "ChineseSimplified", "ChineseTraditional" })
		{
			foreach (string source in new[] { "M7001_02 - 围攻唐门 强制发生", "位置：正心堂", "选「原文」（需 >=40）" })
			{
				Assert(LOMAssistant.UiText.Translate(source, language) == source, language + ": " + source);
			}
		}
		// 任务名本身带分隔符时，仍应优先匹配完整名称。
		Assert(LOMAssistant.UiText.Translate("偷懒怪 - 大师兄", "Korean") == "게으름뱅이 - 대사형", "完整任务名应优先匹配");
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

	/// <summary>韩、日译文表：每行三列（简体、韩、日），简体原文不重复，译文的占位符和原文一样。</summary>
	public static void TestUiTextRows()
	{
		var problems = new List<string>();
		foreach (string field in new[] { "Table", "Terms" })
		{
			var rows = (string[][])Static(typeof(LOMAssistant.UiText), field);
			var seen = new Dictionary<string, string[]>();
			foreach (string[] row in rows)
			{
				if (row.Length != 3)
				{
					problems.Add($"{field} 一行不是三列：{row[0]}");
					continue;
				}
				// 同一句原文写了两遍：译文一样只是多余，译文不一样就有一处永远用不上
				if (seen.TryGetValue(row[0], out string[] before))
				{
					problems.Add($"{field} 原文重复{(before.SequenceEqual(row) ? "（两行一样）" : "（译文不一样）")}：{row[0]}");
				}
				seen[row[0]] = row;
				for (int i = 1; i < 3; i++)
				{
					if (row[i] != null && Holes(row[i]) != Holes(row[0]))
					{
						problems.Add($"{field} 占位符对不上（第 {i + 1} 列）：{row[0]}  →  {row[i]}");
					}
				}
			}
		}
		Expect(problems);
	}

	/// <summary>英文表：载入时不报重复键（字典初始化遇到重复键会让整个 Mod 加载失败），占位符和原文一样。</summary>
	public static void TestUiTextEnglish()
	{
		var problems = new List<string>();
		foreach (string field in new[] { "Terms", "Table", "Descriptions" })
		{
			var table = (Dictionary<string, string>)Static(typeof(LOMAssistant.UiTextEnglish), field);
			if (field == "Descriptions")
			{
				continue; // 键是配置名，不是原文
			}
			foreach (KeyValuePair<string, string> pair in table)
			{
				if (Holes(pair.Value) != Holes(pair.Key))
				{
					problems.Add($"{field} 占位符对不上：{pair.Key}  →  {pair.Value}");
				}
			}
		}
		Expect(problems);
	}

	/// <summary>安装器的文字表：载入不报错，每行四列（简体、英、日、韩），占位符一致。</summary>
	public static void TestInstallerText()
	{
		string exe = Program.RepoPath("Installer", "bin", "咖啡伴侣安装器 Installer.exe");
		if (!File.Exists(exe))
		{
			throw new Skip("没有编译好的安装器");
		}
		Type text = Assembly.LoadFrom(exe).GetTypes().First(t => t.Name == "InstallerText");
		var table = (IDictionary)Static(text, "Table");
		var problems = new List<string>();
		foreach (DictionaryEntry e in table)
		{
			string zh = (string)e.Key;
			var row = (string[])e.Value;
			foreach (string t in row.Where(t => t != null))
			{
				if (Holes(t) != Holes(zh))
				{
					problems.Add($"占位符对不上：{zh}  →  {t}");
				}
			}
		}
		Expect(problems);
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

	// ---------------- 发版检查 ----------------

	/// <summary>版本号一致：插件声明、程序集版本、四份更新内容最上面一条是同一个版本。</summary>
	public static void TestVersionsMatch()
	{
		string plugin = Regex.Match(File.ReadAllText(Program.RepoPath("LOMAssistant", "LOMAssistantPlugin.cs")), @"BepInPlugin\([^)]*""(\d+\.\d+\.\d+)""\)").Groups[1].Value;
		string assembly = Regex.Match(File.ReadAllText(Program.RepoPath("Properties", "AssemblyInfo.cs")), @"AssemblyVersion\(""(\d+\.\d+\.\d+)\.0""\)").Groups[1].Value;
		var problems = new List<string>();
		if (plugin != assembly)
		{
			problems.Add($"插件版本 {plugin}，程序集版本 {assembly}");
		}
		foreach (string log in new[] { "CHANGELOG.md", "CHANGELOG.en.md", "CHANGELOG.ja.md", "CHANGELOG.ko.md" })
		{
			string top = Regex.Match(File.ReadAllText(Program.RepoPath(log)), @"(?m)^## (\d+\.\d+\.\d+)").Groups[1].Value;
			if (top != plugin)
			{
				problems.Add($"{log} 最上面是 {top}，插件版本是 {plugin}");
			}
		}
		Expect(problems);
	}

	/// <summary>安装包里的加载器配置：开着，并且忽略 DOORSTOP_DISABLE（Steam 继承了这个变量时照样加载 Mod）。</summary>
	public static void TestPackagedDoorstopConfig()
	{
		string ini = Program.RepoPath("..", "release", "pkg", "doorstop_config.ini");
		if (!File.Exists(ini))
		{
			throw new Skip("没有 ../release/pkg");
		}
		string text = File.ReadAllText(ini);
		Assert(Regex.IsMatch(text, @"(?m)^enabled\s*=\s*true"), "enabled 不是 true");
		Assert(Regex.IsMatch(text, @"(?m)^ignore_disable_switch\s*=\s*true"), "ignore_disable_switch 不是 true");
		Assert(Regex.IsMatch(text, @"(?m)^target_assembly\s*=\s*BepInEx\\core\\BepInEx\.Unity\.Mono\.Preloader\.dll"), "target_assembly 不是 BepInEx 的预加载器");
	}
}
