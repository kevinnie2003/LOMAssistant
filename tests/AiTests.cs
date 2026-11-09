using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using LOMAssistant.Config;
using LOMAssistant.Features.Ai;
using Newtonsoft.Json.Linq;

namespace LOMAssistant.Tests;

/// <summary>AI 陪玩、文字转换、配置、安装器的测试。要用配置的先 Configured() 一次：在临时文件里建一份全部默认值的配置。</summary>
public static class AiTests
{
	private static void Assert(bool ok, string message)
	{
		if (!ok)
		{
			throw new Exception(message);
		}
	}

	private static T Call<T>(Type type, string method, params object[] args) =>
		(T)type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(null, args);

	private static bool _configured;

	// 不开游戏时没有插件实例，配置写到临时文件里；每项都是默认值
	private static void Configured()
	{
		if (_configured)
		{
			return;
		}
		string path = Path.Combine(Path.GetTempPath(), "LOMAssistant.Tests.cfg");
		File.Delete(path);
		ConfigManager.TestConfig = new ConfigFile(path, true);
		ConfigManager.Init();
		_configured = true;
	}

	// 界面语言：1 简体，2 繁体，3 韩，4 日，5 英
	private static void Language(int ui)
	{
		Configured();
		ConfigManager.UiLanguage.Value = ui;
	}

	private static void Mode(bool fast, bool deep)
	{
		Configured();
		ConfigManager.AiFastMode.Value = fast;
		ConfigManager.AiDeepMode.Value = deep;
	}

	// ---------------- 提示词 ----------------

	/// <summary>每种界面语言都有自己的一份规则：语言要求写的是那种语言，剧透三档都有，五种语言互不相同。</summary>
	public static void TestPromptsPerLanguage()
	{
		var seen = new Dictionary<string, int>();
		foreach (int ui in new[] { 1, 2, 3, 4, 5 })
		{
			Language(ui);
			string rule = AiPrompts.LanguageRule;
			string assistant = AiPrompts.Assistant(AiPrompts.Spoiler(0));
			string fast = AiPrompts.FastAssistant(AiPrompts.Spoiler(0));
			Assert(assistant.Length > 500 && fast.Length > 100, "界面语言 " + ui + " 的攻略助手规则是空的或太短");
			Assert(assistant.Contains(rule) && fast.Contains(rule), "界面语言 " + ui + " 的规则里没有语言要求");
			for (int level = 0; level < 3; level++)
			{
				Assert(AiPrompts.Spoiler(level).Length > 0, "界面语言 " + ui + " 剧透第 " + level + " 档是空的");
			}
			Assert(!seen.ContainsKey(assistant), "界面语言 " + ui + " 和 " + (seen.TryGetValue(assistant, out int other) ? other : 0) + " 的规则一模一样");
			seen[assistant] = ui;
			bool ok = ui switch
			{
				1 => rule.Contains("简体中文"),
				2 => rule.Contains("繁體中文") && !assistant.Contains("赵活。") && assistant.Contains("趙活"),
				3 => rule.Any(c => c >= '가' && c <= '힣'),
				4 => rule.Any(c => c >= 'ぁ' && c <= 'ヿ'),
				_ => rule.Contains("English"),
			};
			Assert(ok, "界面语言 " + ui + " 的语言要求写的不是那种语言：" + rule);
		}
		Language(1);
	}

	// ---------------- 发给服务商的请求 ----------------

	private static JObject OpenAi(string model, string url, bool tools = true) => Call<JObject>(typeof(LlmClient), "OpenAiBody",
		model, "system", new List<ChatMessage> { new ChatMessage { Text = "问题" } }, tools, 2000, url, false);

	/// <summary>推理强度：Gemini 3 平时和极速 low、深度 medium（3.8-flash 不认 minimal）；OpenAI 的 Luna、Sol 调工具时 none；本地 Ollama 极速时 none；别家不设。</summary>
	public static void TestReasoningEffort()
	{
		const string gemini = "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions";
		Mode(false, false);
		Assert((string)OpenAi("gemini-3.8-flash", gemini)["reasoning_effort"] == "low", "Gemini 平时该是 low");
		Mode(true, false);
		Assert((string)OpenAi("gemini-3.8-flash", gemini)["reasoning_effort"] == "low", "Gemini 极速该是 low（不能是 minimal）");
		Mode(false, true);
		Assert((string)OpenAi("gemini-3.8-flash", gemini)["reasoning_effort"] == "medium", "Gemini 深度该是 medium");
		Mode(false, false);
		Assert((string)OpenAi("gpt-6-luna", "https://api.openai.com/v1/chat/completions")["reasoning_effort"] == "none", "Luna 调工具该是 none");
		Assert(OpenAi("gpt-6-luna", "https://api.openai.com/v1/chat/completions")["max_completion_tokens"] != null, "OpenAI 官方该用 max_completion_tokens");
		Assert(OpenAi("deepseek-flash", "https://api.deepseek.com/v1/chat/completions")["max_tokens"] != null, "别家该用 max_tokens");
		Assert(OpenAi("deepseek-flash", "https://api.deepseek.com/v1/chat/completions")["reasoning_effort"] == null, "DeepSeek 不该设推理强度");
		Assert(OpenAi("qwen3", "http://localhost:11434/v1/chat/completions")["reasoning_effort"] == null, "Ollama 平时不该设");
		Mode(true, false);
		Assert((string)OpenAi("qwen3", "http://localhost:11434/v1/chat/completions")["reasoning_effort"] == "none", "Ollama 极速该是 none");
		Mode(false, false);
	}

}
