using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace LOMAssistant.Features.Ai;

/// <summary>对话里的一条：玩家说的、AI 说的（可能带工具调用）、工具查到的结果。</summary>
public sealed class ChatMessage
{
	public const string User = "user", Assistant = "assistant", Tool = "tool";

	public string Role = User;
	public string Text = "";
	/// <summary>AI 这一轮要调用的工具。</summary>
	public List<ToolCall> Calls = new List<ToolCall>();
	/// <summary>工具结果：对应哪一次调用。</summary>
	public string CallId = "";
	/// <summary>Claude 回复的原始内容块（含思考块和签名）：同一问的下一轮要原样传回，否则缓存失效、它也看不到自己刚才的思考。</summary>
	public JArray Raw;
	/// <summary>OpenAI 兼容接口返回的思考内容（DeepSeek、Kimi、通义的 reasoning_content）：同一问里带着工具调用的那几轮要原样传回，DeepSeek 不传会报 400。</summary>
	public string Reasoning = "";
}

public sealed class ToolCall
{
	public string Id = "", Name = "";
	public JObject Args = new JObject();
	/// <summary>调用上附带的服务商字段（Gemini 3 的思考签名在 extra_content 里），回传时原样带上。</summary>
	public JObject Extra;
}

/// <summary>
/// 一家服务商：大多数（DeepSeek、Kimi、通义、智谱、硅基流动、OpenRouter、OpenAI、Gemini、本地 Ollama）都兼容 OpenAI 的接口，
/// Claude 用它自己的接口。模型名只是建议，服务商出了新模型，玩家自己改。
/// </summary>
public sealed class LlmProvider
{
	public string Name = "", BaseUrl = "", Model = "", KeysPage = "";
	/// <summary>深度模式默认用的更强模型；空 = 和平时同一个模型。</summary>
	public string DeepModel = "";
	public bool Claude;
	/// <summary>本地模型不用 Key。</summary>
	public bool NoKey;
	/// <summary>地址要玩家自己填（自定义）。</summary>
	public bool Custom;

	public static readonly LlmProvider[] All =
	{
		new LlmProvider { Name = "DeepSeek", BaseUrl = "https://api.deepseek.com/v1", Model = "deepseek-flash", DeepModel = "deepseek-v4-pro", KeysPage = "https://platform.deepseek.com/api_keys" },
		new LlmProvider { Name = "Kimi（月之暗面）", BaseUrl = "https://api.moonshot.cn/v1", Model = "kimi-k3.1-pro", KeysPage = "https://platform.moonshot.cn/console/api-keys" },
		new LlmProvider { Name = "通义千问（阿里云百炼）", BaseUrl = "https://dashscope.aliyuncs.com/compatible-mode/v1", Model = "qwen3.8-flash", DeepModel = "qwen3.8-max", KeysPage = "https://bailian.console.aliyun.com/?apiKey=1" },
		new LlmProvider { Name = "智谱 GLM", BaseUrl = "https://open.bigmodel.cn/api/paas/v4", Model = "glm-5.3-flash", DeepModel = "glm-5.3", KeysPage = "https://open.bigmodel.cn/usercenter/apikeys" },
		new LlmProvider { Name = "硅基流动", BaseUrl = "https://api.siliconflow.cn/v1", Model = "deepseek-ai/DeepSeek-V4-Flash", DeepModel = "deepseek-ai/DeepSeek-V4-Pro-0813", KeysPage = "https://cloud.siliconflow.cn/account/ak" },
		new LlmProvider { Name = "OpenRouter", BaseUrl = "https://openrouter.ai/api/v1", Model = "deepseek/deepseek-v4.1-flash", KeysPage = "https://openrouter.ai/keys" },
		new LlmProvider { Name = "OpenAI", BaseUrl = "https://api.openai.com/v1", Model = "gpt-6-luna", DeepModel = "gpt-6-sol", KeysPage = "https://platform.openai.com/api-keys" },
		new LlmProvider { Name = "Claude（Anthropic）", BaseUrl = "https://api.anthropic.com/v1", Model = "claude-sonnet-5-5", DeepModel = "claude-opus-5-5", KeysPage = "https://console.anthropic.com/settings/keys", Claude = true },
		new LlmProvider { Name = "Gemini（Google）", BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai", Model = "gemini-3.6-flash", DeepModel = "gemini-3.8-flash", KeysPage = "https://aistudio.google.com/apikey" },
		new LlmProvider { Name = "本地模型（Ollama）", BaseUrl = "http://localhost:11434/v1", Model = "qwen3", NoKey = true },
		new LlmProvider { Name = "自定义（OpenAI 兼容）", BaseUrl = "", Model = "", Custom = true },
	};

	public static LlmProvider At(int index) => All[Math.Max(0, Math.Min(All.Length - 1, index))];
}

/// <summary>AI 一次回复：说的话、要调用的工具，或者出错的原因。</summary>
public sealed class LlmReply
{
	public string Text = "";
	public List<ToolCall> Calls = new List<ToolCall>();
	public string Error;
	/// <summary>这一轮用掉的 token（服务商有返回才有）；InputTokens 含缓存命中的 CachedTokens。</summary>
	public int InputTokens, OutputTokens, CachedTokens;
	/// <summary>服务商说的这一轮为什么停（end_turn、max_tokens、tool_use……），排查空回复用。</summary>
	public string StopReason = "";
	/// <summary>Claude 的原始内容块，见 ChatMessage.Raw。</summary>
	public JArray Raw;
	/// <summary>OpenAI 兼容接口的思考内容，见 ChatMessage.Reasoning。</summary>
	public string Reasoning = "";
}

/// <summary>
/// 发请求、解析回复。用 UnityWebRequest，跑在 Unity 主线程的协程里，查资料的工具（读游戏数据）可以直接在回调里执行。
/// 给了 onText 就流式接收（服务商一边写一边发，onText 拿到目前为止的全文）；流式用不了时退回一次拿整段。
/// 缓存：各家都按「请求开头完全相同」命中缓存，所以系统提示词每次都一样、会变的东西放在最后一条消息里；
/// Claude 要自己标缓存位置（系统提示词末尾、最后一条消息末尾）。
/// </summary>
public static class LlmClient
{
	private static UnityWebRequest _current;
	// 这个游戏里流式下载器用不了（被裁掉之类）时记下，之后都整段拿
	private static bool _noStream;

	/// <summary>停掉正在进行的请求。</summary>
	public static void Abort()
	{
		try
		{
			_current?.Abort();
		}
		catch
		{
		}
	}

	/// <summary>light：主动陪玩的一两句话。服务商忙时不重试，不跟着深度模式加思考强度，Claude 的系统提示词缓存留一小时。</summary>
	public static IEnumerator Complete(LlmProvider provider, string baseUrl, string key, string model, string system,
		List<ChatMessage> messages, bool useTools, int maxTokens, Action<LlmReply> done, bool noMoreCalls = false, Action<string> onText = null, bool light = false)
	{
		string url = (baseUrl ?? "").Trim().TrimEnd('/');
		if (url.Length == 0)
		{
			done(new LlmReply { Error = "没有填 API 地址" });
			yield break;
		}
		if ((model ?? "").Trim().Length == 0)
		{
			done(new LlmReply { Error = "没有填模型名" });
			yield break;
		}
		JObject body = provider.Claude ? ClaudeBody(model, system, messages, useTools, maxTokens, light) : OpenAiBody(model, system, messages, useTools, maxTokens, url, light);
		// 最后一轮：工具定义照给（历史里有工具调用，不给定义有的服务商会报错，DeepSeek 还会把调用格式当正文写出来），但不许再调用
		if (useTools && noMoreCalls)
		{
			body["tool_choice"] = provider.Claude ? (JToken)new JObject { ["type"] = "none" } : "none";
		}
		bool streaming = onText != null && !_noStream;
		if (streaming)
		{
			body["stream"] = true;
			// 流式时用量放在最后一块里，要明说才给（本地 Ollama 不一定认，不加）
			if (!provider.Claude && !provider.NoKey)
			{
				body["stream_options"] = new JObject { ["include_usage"] = true };
			}
		}
		byte[] payload = Encoding.UTF8.GetBytes(body.ToString(Formatting.None, new JsonConverter[0]));
		// 服务商一时忙不过来（429、5xx、Claude 的 529）且还没收到任何内容时，等一会儿自动再试，最多再试两次
		for (int attempt = 0; ; attempt++)
		{
			StreamHandler stream = null;
			if (streaming)
			{
				try
				{
					stream = new StreamHandler(provider.Claude);
					stream.OnText = onText;
				}
				catch (Exception ex)
				{
					_noStream = true;
					stream = null;
					FileLogger.Log("[AI] 流式下载器用不了，改成整段接收: " + ex.Message);
				}
			}
			var request = new UnityWebRequest(url + (provider.Claude ? "/messages" : "/chat/completions"), "POST")
			{
				uploadHandler = new UploadHandlerRaw(payload),
				downloadHandler = stream != null ? stream : new DownloadHandlerBuffer(),
				// 流式不设总时长（思考久的模型、长回答几分钟很正常），改成下面「多久没收到数据」；整段接收给 5 分钟
				timeout = stream != null ? 0 : 300
			};
			request.SetRequestHeader("Content-Type", "application/json");
			if (provider.Claude)
			{
				request.SetRequestHeader("x-api-key", key ?? "");
				request.SetRequestHeader("anthropic-version", "2023-06-01");
			}
			else if (!string.IsNullOrEmpty(key))
			{
				request.SetRequestHeader("Authorization", "Bearer " + key.Trim());
			}
			_current = request;
			bool stalled = false;
			using (request)
			{
				UnityWebRequestAsyncOperation op = request.SendWebRequest();
				float last = UnityEngine.Time.realtimeSinceStartup;
				long got = 0;
				while (!op.isDone)
				{
					long now = (long)request.downloadedBytes;
					if (now != got)
					{
						got = now;
						last = UnityEngine.Time.realtimeSinceStartup;
					}
					else if (stream != null && UnityEngine.Time.realtimeSinceStartup - last > StallSeconds)
					{
						stalled = true;
						request.Abort();
					}
					yield return null;
				}
				// 这次请求被停掉时，别的请求可能已经接着发出去了：只清自己的
				if (_current == request)
				{
					_current = null;
				}
				string text = stream != null ? stream.Raw.ToString() : request.downloadHandler?.text ?? "";
				if (stalled)
				{
					FileLogger.LogWarning("[AI] 服务商 " + StallSeconds + " 秒没有发来任何内容，停掉这次请求");
					done(new LlmReply { Error = "服务商很久没有回应，稍后再试，或换个快一点的模型" });
					yield break;
				}
				if (request.result != UnityWebRequest.Result.Success)
				{
					long code = request.responseCode;
					bool busy = code == 429 || code == 529 || (code >= 500 && code <= 504);
					// 主动陪玩的话错过就算了，不重试：免费额度被限流时不会连着再发
					if (busy && attempt < 2 && !light && (stream == null || stream.Events == 0))
					{
						FileLogger.LogWarning("[AI] 服务商忙（" + code + "），" + (attempt == 0 ? 3 : 8) + " 秒后自动重试");
						yield return new UnityEngine.WaitForSecondsRealtime(attempt == 0 ? 3f : 8f);
						continue;
					}
					done(new LlmReply { Error = Describe(request, text) });
					yield break;
				}
				try
				{
					if (stream != null && stream.Events > 0)
					{
						done(stream.Reply());
					}
					else
					{
						// 没开流式，或者服务商不理 stream 直接回了整段
						done(provider.Claude ? ParseClaude(JObject.Parse(text)) : ParseOpenAi(JObject.Parse(text)));
					}
				}
				catch (Exception ex)
				{
					FileLogger.LogWarning("[AI] 回复解析失败: " + ex.Message + "\n" + (text.Length > 500 ? text.Substring(0, 500) : text));
					done(new LlmReply { Error = "看不懂服务商的回复\n" + ex.Message });
				}
				yield break;
			}
		}
	}

	// 流式时多久没收到任何数据算卡住
	private const int StallSeconds = 300;

	/// <summary>
	/// 流式接收：服务商发来的是一行行「data: {...}」（Server-Sent Events），每来一块就拼起来，
	/// 正文每长一点就告诉 onText；工具调用的参数也是一段段来的，最后再拼成完整 JSON。
	/// Unity 在主线程上调 ReceiveData，所以回调里可以直接动界面的状态。
	/// </summary>
	private sealed class StreamHandler : DownloadHandlerScript
	{
		public readonly StringBuilder Raw = new StringBuilder();
		public Action<string> OnText;
		public int Events;

		private readonly bool _claude;
		private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
		private readonly StringBuilder _line = new StringBuilder();
		private readonly StringBuilder _text = new StringBuilder();
		private readonly StringBuilder _reasoning = new StringBuilder();
		private readonly Dictionary<int, JObject> _extras = new Dictionary<int, JObject>();
		private readonly SortedDictionary<int, (string Id, string Name, StringBuilder Args)> _calls = new SortedDictionary<int, (string, string, StringBuilder)>();
		private int _in, _out, _cached;
		private string _error;
		private string _stopReason = "";
		// Claude 的内容块按序号攒起来（思考块的文字和签名、正文），回传时原样用
		private readonly SortedDictionary<int, JObject> _blocks = new SortedDictionary<int, JObject>();

		public StreamHandler(bool claude) : base(new byte[16 * 1024])
		{
			_claude = claude;
		}

		protected override bool ReceiveData(byte[] data, int dataLength)
		{
			if (data == null || dataLength <= 0)
			{
				return true;
			}
			var chars = new char[_decoder.GetCharCount(data, 0, dataLength)];
			int n = _decoder.GetChars(data, 0, dataLength, chars, 0);
			int before = _text.Length;
			for (int i = 0; i < n; i++)
			{
				char c = chars[i];
				Raw.Append(c);
				if (c == '\n')
				{
					// 一行解析出错不能往外抛：Unity 会把整个请求判成「Unable to write data」断掉
					try
					{
						Line(_line.ToString().TrimEnd('\r'));
					}
					catch (Exception ex)
					{
						FileLogger.LogWarning("[AI] 流式数据有一行看不懂，跳过: " + ex.Message);
					}
					_line.Length = 0;
				}
				else
				{
					_line.Append(c);
				}
			}
			if (_text.Length != before)
			{
				try
				{
					OnText?.Invoke(_text.ToString());
				}
				catch
				{
				}
			}
			return true;
		}

		protected override void CompleteContent()
		{
			if (_line.Length > 0)
			{
				Line(_line.ToString().TrimEnd('\r'));
				_line.Length = 0;
			}
		}

		private void Line(string line)
		{
			if (!line.StartsWith("data:", StringComparison.Ordinal))
			{
				return;
			}
			string payload = line.Substring(5).Trim();
			if (payload.Length == 0 || payload == "[DONE]")
			{
				return;
			}
			JObject json;
			try
			{
				json = JObject.Parse(payload);
			}
			catch
			{
				return;
			}
			Events++;
			if (json["error"] != null)
			{
				_error = json["error"]?["message"]?.ToString() ?? json["error"].ToString();
				return;
			}
			if (_claude)
			{
				Claude(json);
			}
			else
			{
				OpenAi(json);
			}
		}

		private void OpenAi(JObject json)
		{
			// OpenAI 最后那块用量的 choices 是空数组，不能直接取 [0]
			JToken choice = json["choices"] is JArray choices && choices.Count > 0 ? choices[0] : null;
			JToken delta = choice?["delta"];
			if (delta != null)
			{
				if (delta["content"]?.Type == JTokenType.String)
				{
					_text.Append(delta["content"].ToString());
				}
				if (delta["reasoning_content"]?.Type == JTokenType.String)
				{
					_reasoning.Append(delta["reasoning_content"].ToString());
				}
				foreach (JToken call in delta["tool_calls"] as JArray ?? new JArray())
				{
					int index = call["index"]?.Value<int>() ?? _calls.Count;
					if (call["extra_content"] is JObject extra)
					{
						if (!_extras.TryGetValue(index, out JObject merged))
						{
							_extras[index] = merged = new JObject();
						}
						merged.Merge(extra);
					}
					if (!_calls.TryGetValue(index, out var c))
					{
						c = ("", "", new StringBuilder());
					}
					string id = call["id"]?.ToString();
					string name = call["function"]?["name"]?.ToString();
					c = (string.IsNullOrEmpty(id) ? c.Id : id, string.IsNullOrEmpty(name) ? c.Name : c.Name + name, c.Args);
					c.Args.Append(call["function"]?["arguments"]?.ToString() ?? "");
					_calls[index] = c;
				}
			}
			if (choice?["finish_reason"]?.Type == JTokenType.String)
			{
				_stopReason = choice["finish_reason"].ToString();
			}
			if (json["usage"] is JObject usage)
			{
				_in = usage["prompt_tokens"]?.Value<int>() ?? _in;
				_out = usage["completion_tokens"]?.Value<int>() ?? _out;
				_cached = CachedOf(usage);
			}
		}

		private void Claude(JObject json)
		{
			switch (json["type"]?.ToString())
			{
				case "message_start":
					if (json["message"]?["usage"] is JObject usage)
					{
						int read = usage["cache_read_input_tokens"]?.Value<int>() ?? 0;
						_in = (usage["input_tokens"]?.Value<int>() ?? 0) + read + (usage["cache_creation_input_tokens"]?.Value<int>() ?? 0);
						_cached = read;
					}
					break;
				case "content_block_start":
					if (json["content_block"] is JObject started)
					{
						_blocks[json["index"]?.Value<int>() ?? _blocks.Count] = (JObject)started.DeepClone();
					}
					if (json["content_block"]?["type"]?.ToString() == "tool_use")
					{
						_calls[json["index"]?.Value<int>() ?? _calls.Count] = (json["content_block"]["id"]?.ToString() ?? "", json["content_block"]["name"]?.ToString() ?? "", new StringBuilder());
					}
					break;
				case "content_block_delta":
					JToken delta = json["delta"];
					_blocks.TryGetValue(json["index"]?.Value<int>() ?? -1, out JObject block);
					if (delta?["type"]?.ToString() == "text_delta")
					{
						_text.Append(delta["text"]?.ToString() ?? "");
						if (block != null)
						{
							block["text"] = (block["text"]?.ToString() ?? "") + (delta["text"]?.ToString() ?? "");
						}
					}
					else if (delta?["type"]?.ToString() == "thinking_delta" && block != null)
					{
						block["thinking"] = (block["thinking"]?.ToString() ?? "") + (delta["thinking"]?.ToString() ?? "");
					}
					else if (delta?["type"]?.ToString() == "signature_delta" && block != null)
					{
						block["signature"] = (block["signature"]?.ToString() ?? "") + (delta["signature"]?.ToString() ?? "");
					}
					else if (delta?["type"]?.ToString() == "input_json_delta" && _calls.TryGetValue(json["index"]?.Value<int>() ?? -1, out var c))
					{
						c.Args.Append(delta["partial_json"]?.ToString() ?? "");
					}
					break;
				case "message_delta":
					_out = json["usage"]?["output_tokens"]?.Value<int>() ?? _out;
					_stopReason = json["delta"]?["stop_reason"]?.ToString() ?? _stopReason;
					break;
			}
		}

		public LlmReply Reply()
		{
			var reply = new LlmReply { Text = _text.ToString(), Reasoning = _reasoning.ToString(), InputTokens = _in, OutputTokens = _out, CachedTokens = _cached, Error = _error, StopReason = _stopReason };
			foreach (var kv in _calls)
			{
				var c = kv.Value;
				var call = new ToolCall { Id = c.Id.Length > 0 ? c.Id : Guid.NewGuid().ToString("N"), Name = c.Name, Args = ParseArgs(c.Args.ToString()), Extra = _extras.TryGetValue(kv.Key, out JObject e) ? e : null };
				reply.Calls.Add(call);
				if (_blocks.TryGetValue(kv.Key, out JObject b))
				{
					b["id"] = call.Id;
					b["input"] = call.Args;
				}
			}
			if (_claude && _blocks.Count > 0)
			{
				reply.Raw = new JArray(_blocks.Values);
			}
			return reply;
		}
	}

	private static JObject ParseArgs(string args)
	{
		try
		{
			return string.IsNullOrWhiteSpace(args) ? new JObject() : JObject.Parse(args);
		}
		catch
		{
			return new JObject();
		}
	}

	// 缓存命中的 token：OpenAI、通义、智谱、Gemini 放在 prompt_tokens_details.cached_tokens，DeepSeek 叫 prompt_cache_hit_tokens，Kimi 叫 cached_tokens
	private static int CachedOf(JObject usage) =>
		usage["prompt_tokens_details"]?["cached_tokens"]?.Value<int?>() ?? usage["prompt_cache_hit_tokens"]?.Value<int?>() ?? usage["cached_tokens"]?.Value<int?>() ?? 0;

	// 出错时给玩家看的原因：常见状态码说人话，后面附上服务商自己的说明
	private static string Describe(UnityWebRequest request, string body)
	{
		if (request.result == UnityWebRequest.Result.ConnectionError)
		{
			return "连不上服务商，检查网络和 API 地址\n" + request.error;
		}
		string detail = "";
		try
		{
			JToken json = JToken.Parse(body);
			detail = json["error"]?["message"]?.ToString() ?? json["error"]?.ToString() ?? json["message"]?.ToString() ?? "";
		}
		catch
		{
			detail = body.Length > 200 ? body.Substring(0, 200) : body;
		}
		long code = request.responseCode;
		string reason = code switch
		{
			400 => "请求不对（模型名写错，或这个模型不支持工具调用）",
			401 => "API Key 不对",
			403 => "没有权限（Key 不能用这个模型，或地区受限）",
			402 => "余额不足",
			404 => "地址或模型名不对",
			429 => "请求太频繁或额度用完了，稍后再试",
			_ when code >= 500 => "服务商那边出错了，稍后再试",
			_ => "请求失败",
		};
		// 原因单独一行（界面语言不是中文时查表翻译），服务商的原话放下一行
		return reason + "\n" + code + (detail.Length > 0 ? " " + detail.Trim() : "");
	}

	private static JObject OpenAiBody(string model, string system, List<ChatMessage> messages, bool useTools, int maxTokens, string url, bool light)
	{
		var list = new JArray { new JObject { ["role"] = "system", ["content"] = system } };
		foreach (ChatMessage m in messages)
		{
			if (m.Role == ChatMessage.Tool)
			{
				list.Add(new JObject { ["role"] = "tool", ["tool_call_id"] = m.CallId, ["content"] = m.Text });
			}
			else if (m.Role == ChatMessage.Assistant && m.Calls.Count > 0)
			{
				var assistant = new JObject
				{
					["role"] = "assistant",
					["content"] = m.Text.Length > 0 ? m.Text : null,
					["tool_calls"] = new JArray(m.Calls.Select(c =>
					{
						var call = new JObject
						{
							["id"] = c.Id,
							["type"] = "function",
							["function"] = new JObject { ["name"] = c.Name, ["arguments"] = c.Args.ToString(Formatting.None, new JsonConverter[0]) }
						};
						if (c.Extra != null)
						{
							call["extra_content"] = c.Extra.DeepClone();
						}
						return call;
					}))
				};
				// 思考内容只在服务商返回过时才带（OpenAI 官方不认这个字段，它也不会返回）
				if (m.Reasoning.Length > 0)
				{
					assistant["reasoning_content"] = m.Reasoning;
				}
				list.Add(assistant);
			}
			else
			{
				list.Add(new JObject { ["role"] = m.Role, ["content"] = m.Text });
			}
		}
		var body = new JObject { ["model"] = model.Trim(), ["messages"] = list };
		// OpenAI 官方的新模型（GPT-5 以后）不认 max_tokens，要用 max_completion_tokens；其他兼容接口还是 max_tokens
		body[url.IndexOf("api.openai.com", StringComparison.OrdinalIgnoreCase) >= 0 ? "max_completion_tokens" : "max_tokens"] = maxTokens;
		// OpenAI 的 GPT-6 Luna、Sol 在 Chat Completions 里只有推理强度为 none 时才能调用工具（官方文档写明；6.1 系列要用 Responses API 才能调工具）
		if (useTools && (model.IndexOf("luna", StringComparison.OrdinalIgnoreCase) >= 0 || model.IndexOf("gpt-6-sol", StringComparison.OrdinalIgnoreCase) >= 0))
		{
			body["reasoning_effort"] = "none";
		}
		// Gemini 3 系列的思考关不掉，不设时想得太久会超时：平时、极速 low，深度模式 medium（3.8-flash 不认 minimal，会报 400）
		if (url.IndexOf("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) >= 0 && model.IndexOf("gemini-3", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			body["reasoning_effort"] = AiChat.Deep && !light ? "medium" : "low";
		}
		// 极速模式下本地模型（Ollama）不思考：小模型想很久也不会更准，还常卡住
		if (AiChat.Fast && url.IndexOf(":11434", StringComparison.Ordinal) >= 0)
		{
			body["reasoning_effort"] = "none";
		}
		if (useTools)
		{
			body["tools"] = new JArray(AiChat.Tools.Select(t => new JObject
			{
				["type"] = "function",
				["function"] = new JObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters }
			}));
		}
		return body;
	}

	private static LlmReply ParseOpenAi(JObject json)
	{
		var reply = new LlmReply();
		JToken message = json["choices"] is JArray choices && choices.Count > 0 ? choices[0]["message"] : null;
		if (message == null)
		{
			reply.Error = "服务商没有返回内容";
			return reply;
		}
		reply.Text = message["content"]?.Type == JTokenType.String ? message["content"].ToString() : "";
		reply.Reasoning = message["reasoning_content"]?.Type == JTokenType.String ? message["reasoning_content"].ToString() : "";
		reply.StopReason = json["choices"][0]["finish_reason"]?.ToString() ?? "";
		foreach (JToken call in message["tool_calls"] as JArray ?? new JArray())
		{
			reply.Calls.Add(new ToolCall { Id = call["id"]?.ToString() ?? Guid.NewGuid().ToString("N"), Name = call["function"]?["name"]?.ToString() ?? "", Args = ParseArgs(call["function"]?["arguments"]?.ToString()), Extra = call["extra_content"] as JObject });
		}
		if (json["usage"] is JObject usage)
		{
			reply.InputTokens = usage["prompt_tokens"]?.Value<int>() ?? 0;
			reply.OutputTokens = usage["completion_tokens"]?.Value<int>() ?? 0;
			reply.CachedTokens = CachedOf(usage);
		}
		return reply;
	}

	private static JObject ClaudeBody(string model, string system, List<ChatMessage> messages, bool useTools, int maxTokens, bool light = false)
	{
		var list = new JArray();
		JArray pendingResults = null;
		foreach (ChatMessage m in messages)
		{
			// 连续的工具结果合成一条 user 消息
			if (m.Role == ChatMessage.Tool)
			{
				if (pendingResults == null)
				{
					pendingResults = new JArray();
					list.Add(new JObject { ["role"] = "user", ["content"] = pendingResults });
				}
				pendingResults.Add(new JObject { ["type"] = "tool_result", ["tool_use_id"] = m.CallId, ["content"] = m.Text });
				continue;
			}
			// 紧跟在工具结果后面的话（查满轮数时的「直接作答」）并进同一条 user 消息，不连发两条 user
			if (m.Role == ChatMessage.User && pendingResults != null)
			{
				pendingResults.Add(new JObject { ["type"] = "text", ["text"] = m.Text });
				pendingResults = null;
				continue;
			}
			pendingResults = null;
			// 同一问里 Claude 自己的回复：原样传回（思考块不能删也不能改，删了缓存就从这里失效）
			if (m.Role == ChatMessage.Assistant && m.Raw != null && m.Raw.Count > 0)
			{
				list.Add(new JObject { ["role"] = "assistant", ["content"] = m.Raw.DeepClone() });
				continue;
			}
			if (m.Role == ChatMessage.Assistant)
			{
				var content = new JArray();
				if (m.Text.Length > 0)
				{
					content.Add(new JObject { ["type"] = "text", ["text"] = m.Text });
				}
				foreach (ToolCall c in m.Calls)
				{
					content.Add(new JObject { ["type"] = "tool_use", ["id"] = c.Id, ["name"] = c.Name, ["input"] = c.Args });
				}
				list.Add(new JObject { ["role"] = "assistant", ["content"] = content });
			}
			else
			{
				list.Add(new JObject { ["role"] = "user", ["content"] = m.Text });
			}
		}
		// 缓存位置：系统提示词末尾（连同前面的工具定义一起缓存，每次都一样）、最后一条消息末尾（查资料的每一轮接着上一轮的缓存）
		var cache = new JObject { ["type"] = "ephemeral" };
		var body = new JObject
		{
			["model"] = model.Trim(),
			["max_tokens"] = maxTokens,
			["system"] = new JArray { new JObject { ["type"] = "text", ["text"] = system, ["cache_control"] = cache } },
			["messages"] = list
		};
		if (list.Count > 0 && list[list.Count - 1] is JObject lastMessage)
		{
			if (lastMessage["content"]?.Type == JTokenType.String)
			{
				lastMessage["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = lastMessage["content"] } };
			}
			if (lastMessage["content"] is JArray blocks && blocks.Count > 0 && blocks[blocks.Count - 1] is JObject lastBlock)
			{
				lastBlock["cache_control"] = cache.DeepClone();
			}
		}
		if (useTools)
		{
			body["tools"] = new JArray(AiChat.Tools.Select(t => new JObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = t.Parameters }));
		}
		if (light)
		{
			ClaudeLight(body);
		}
		return body;
	}

	/// <summary>
	/// 主动陪玩的一两句话：两句之间常隔五分钟以上，系统提示词的缓存改留一小时（写入贵一些，之后一小时里每次都按命中算）。
	/// 思考不动，照模型平时的样子开着：想过再说的话更好（实测一句 50 字的话连思考输出近 300 token）。
	/// </summary>
	private static void ClaudeLight(JObject body)
	{
		if (body["system"] is JArray blocks && blocks.Count > 0 && blocks[0] is JObject first)
		{
			// 一小时的缓存要排在五分钟的（最后一条消息上那个）前面，系统提示词正好在最前
			first["cache_control"] = new JObject { ["type"] = "ephemeral", ["ttl"] = "1h" };
		}
	}

	private static LlmReply ParseClaude(JObject json)
	{
		var reply = new LlmReply();
		var text = new StringBuilder();
		foreach (JToken block in json["content"] as JArray ?? new JArray())
		{
			string type = block["type"]?.ToString();
			if (type == "text")
			{
				text.Append(block["text"]);
			}
			else if (type == "tool_use")
			{
				reply.Calls.Add(new ToolCall { Id = block["id"]?.ToString() ?? "", Name = block["name"]?.ToString() ?? "", Args = block["input"] as JObject ?? new JObject() });
			}
		}
		reply.Text = text.ToString();
		reply.StopReason = json["stop_reason"]?.ToString() ?? "";
		reply.Raw = json["content"] is JArray content ? (JArray)content.DeepClone() : null;
		// Claude 的 input_tokens 不含缓存读写，合起来才是这一轮的输入
		int read = json["usage"]?["cache_read_input_tokens"]?.Value<int>() ?? 0;
		reply.InputTokens = (json["usage"]?["input_tokens"]?.Value<int>() ?? 0) + read + (json["usage"]?["cache_creation_input_tokens"]?.Value<int>() ?? 0);
		reply.OutputTokens = json["usage"]?["output_tokens"]?.Value<int>() ?? 0;
		reply.CachedTokens = read;
		return reply;
	}
}
