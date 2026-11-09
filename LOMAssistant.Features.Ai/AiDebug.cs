using System;
using System.IO;
using LOMAssistant.Config;

namespace LOMAssistant.Features.Ai;

/// <summary>
/// 调试记录：设置里打开后，把每个问题发给 AI 的系统提示词、现状、每次查资料的参数和结果、每轮用量写进
/// logs/ai_debug.txt，方便核对「是资料里没有，还是 AI 没用上」。不含 API Key。超过 5 MB 换新文件。
/// </summary>
public static class AiDebug
{
	private const long MaxBytes = 5 * 1024 * 1024;

	private static string File => Path.Combine(Path.GetDirectoryName(typeof(AiDebug).Assembly.Location) ?? ".", "logs", "ai_debug.txt");

	public static bool On => ConfigManager.AiDebugLog.Value;

	/// <summary>model：这次请求真正用的模型；不给就是问答现在用的那个（主动陪玩可以另设模型，也不跟着深度模式换）。</summary>
	public static void Turn(string who, string system, string question, string model = null)
	{
		Write("\n\n==================== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " · " + who + " · " + (model ?? AiChat.Model) + " ====================\n"
			+ "【系统提示词】（" + system.Length + " 字）\n" + system + "\n\n【这次发的问题】\n" + question + "\n");
	}

	public static void Tool(string name, string args, string result)
	{
		Write("\n--- 查资料 " + name + " " + args + "（结果 " + result.Length + " 字）\n" + result + "\n");
	}

	public static void Reply(LlmReply reply)
	{
		Write("\n--- 回复：输入 " + reply.InputTokens + "（缓存命中 " + reply.CachedTokens + "），输出 " + reply.OutputTokens
			+ (reply.Calls.Count > 0 ? "，要查 " + reply.Calls.Count + " 处" : "") + (reply.StopReason.Length > 0 ? "，停止原因 " + reply.StopReason : "") + "\n" + reply.Text + "\n");
	}

	private static void Write(string text)
	{
		if (!On)
		{
			return;
		}
		try
		{
			string path = File;
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			if (System.IO.File.Exists(path) && new FileInfo(path).Length > MaxBytes)
			{
				string old = Path.ChangeExtension(path, ".old.txt");
				System.IO.File.Delete(old);
				System.IO.File.Move(path, old);
			}
			System.IO.File.AppendAllText(path, text);
		}
		catch (Exception ex)
		{
			FileLogger.Log("[AI] 写调试记录失败: " + ex.Message);
		}
	}
}
