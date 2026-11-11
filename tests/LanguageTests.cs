using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace LOMAssistant.Tests;

/// <summary>
/// 界面文字和多语言的测试：只用文字表和 tests/ci/GameStubs.cs，不需要游戏文件，GitHub 上也跑（tests/run-language-tests.sh）。
/// </summary>
public static partial class Tests
{
	public static void TestGalleryDuelUiTranslations()
	{
		string[] labels = { "面板与技能", "搜索剧情 / 配置", "角色技能", "无固有技能",
			"剧情状态变化", "未记录额外状态事件" };
		foreach (string language in new[] { LOMAssistant.UiText.Korean, LOMAssistant.UiText.Japanese, LOMAssistant.UiText.English })
		{
			foreach (string label in labels)
			{
				string translated = LOMAssistant.UiText.Translate(label, language);
				Assert(!string.IsNullOrWhiteSpace(translated) && translated != label, language + " missing duel label: " + label);
			}
			foreach (string format in new[] { "{0} 个剧情 / 状态", "关卡难度 {0}", "血量 {0}/{1} · 气力 {2}/{3} · 嘴力 {4}" })
			{
				string translated = LOMAssistant.UiText.Translate(format, language);
				Assert(Holes(translated) == Holes(format), language + " duel placeholders changed");
				string rendered = string.Format(translated, 17, 29, 31, 43, 59);
				Assert(rendered.Contains("17") && !rendered.Contains("{0}"), language + " duel values were not formatted");
			}
		}
		Assert(LOMAssistant.TextConverter.ToTraditional("面板与技能") == "面板與技能", "Traditional duel label conversion failed");
	}

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

	/// <summary>游戏备注压成一行后仍能查到完整名称，外层括号和未匹配的空白原样保留。</summary>
	public static void TestFlagNameWhitespace()
	{
		foreach (string language in new[] { "Korean", "Japanese", "English" })
		{
			foreach (string[] row in NameTable("UiFlagNames", language).Where(row => row[0].Contains(" ")))
			{
				string expected = LOMAssistant.UiText.Translate(row[0] + " (TEST_ID) 需=1", language);
				foreach (string space in new[] { "  ", "\r ", " \t " })
				{
					string source = row[0].Replace(" ", space) + " (TEST_ID) 需=1";
					Assert(LOMAssistant.UiText.Translate(source, language) == expected, language + "：备注空白导致漏译「" + row[0] + "」");
				}
			}
		}
		string name = "南宫家宴采纳结论 1:大师兄 2:二师兄 3:三师兄";
		string translated = "남궁세가 연회에서 채택한 결론 1:대사형 2:이사형 3:삼사형";
		Assert(LOMAssistant.UiText.Translate(name, "Korean") == translated, "无括号的旗标备注漏译");
		Assert(LOMAssistant.UiText.Translate("〔" + name + "〕", "Korean") == "〔" + translated + "〕", "旗标备注的外层括号丢失");
		foreach (string language in new[] { "Korean", "Japanese", "English", "ChineseSimplified", "ChineseTraditional" })
		{
			string source = "UNKNOWN_ID  \r\t 9";
			Assert(LOMAssistant.UiText.Translate(source, language) == source, "未匹配的文字不应被整理空白");
		}
	}

	/// <summary>条件按换行或富文本拆段后，段首的连接词仍复用各语言词条。</summary>
	public static void TestConditionConjunctionSegments()
	{
		foreach (string[] row in new[] { new[] { "Korean", "그리고", "또는" }, new[] { "Japanese", "AND", "OR" }, new[] { "English", "AND", "OR" } })
		{
			foreach (string[] word in new[] { new[] { "且", row[1] }, new[] { "或", row[2] } })
			{
				string source = "<b>FLAG_A</b>\n" + word[0] + " <b>FLAG_B</b>";
				string expected = "<b>FLAG_A</b>\n" + word[1] + " <b>FLAG_B</b>";
				Assert(LOMAssistant.UiText.Translate(source, row[0]) == expected, row[0] + "：段首连接词漏译");
				Assert(LOMAssistant.UiText.Translate(source, "ChineseSimplified") == source, "中文条件不应变化");
			}
			Assert(LOMAssistant.UiText.Translate("而且 UNKNOWN_ID", row[0]) == "而且 UNKNOWN_ID", "不应替换词语内部的连接词");
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
		// 复合结果中的否则条件应与单独翻译一致，富文本标签不能截断条件。
		foreach (string language in new[] { "Korean", "Japanese", "English" })
		{
			foreach (string clause in new[]
			{
				"否则（不满足：心上人=0）",
				"否则（不满足：心上人=2）",
				"否则（以下都不满足：心上人=0；心上人=2）",
				"否则（不满足：心上人（G00003）=0）",
				"否则（不满足：<color=#ffffff>心上人=0</color>）",
			})
			{
				string translated = LOMAssistant.UiText.Translate(clause, language);
				Assert(translated != clause && !translated.Contains("否则") && !translated.Contains("不满足"), language + ": " + translated);
				foreach (string prefix in new[] { "test\n", "test    ", "<size=20>test\n" })
				{
					string suffix = prefix.StartsWith("<size") ? "</size>" : "";
					string source = prefix + clause + suffix;
					Assert(LOMAssistant.UiText.Translate(source, language) == prefix + translated + suffix, language + ": " + source);
				}
			}
		}
		Assert(LOMAssistant.UiText.Translate("test:心上人=0", "Korean") == "test:마음에 둔 사람=0", "冒号后的条件名");
		Assert(LOMAssistant.UiText.Translate("test：心上人=2", "Korean") == "test：마음에 둔 사람=2", "全角冒号后的条件名");
		Assert(LOMAssistant.UiText.Translate("test\n否则（不满足：心上人=0）", "Korean") == "test\n그 외 (마음에 둔 사람=0 미충족)", "多行否则条件");
		Assert(LOMAssistant.UiText.Translate("<size=20>test\n否则（不满足：<color=#ffffff>心上人=0</color>）</size>", "Korean") == "<size=20>test\n그 외 (<color=#ffffff>마음에 둔 사람=0</color> 미충족)</size>", "保留富文本标签");
		foreach (string language in new[] { "ChineseSimplified", "ChineseTraditional" })
		{
			string source = "<size=20>test\n否则（不满足：<color=#ffffff>心上人=0</color>）</size>";
			Assert(LOMAssistant.UiText.Translate(source, language) == source, language + ": 复合原文");
		}
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
