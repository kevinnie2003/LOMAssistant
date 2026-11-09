using System;
using System.IO;
using System.Text;
using LOMAssistant.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace LOMAssistant.Features.Ai;

/// <summary>
/// 每家服务商各记各的：API Key，和玩家改过的地址、模型名（问答、深度模式、主动陪玩）。
/// 配置文件里的 AiApiKey、AiBaseUrl、AiModel、AiDeepModel、AiCompanionModel 始终是正在用的那一家的，别处照旧读它们；
/// 别家的存在 BepInEx\config\LOMAssistant_AI\providers.json，换服务商时把正在用的存进去、把要换的那家取出来。
/// 不放进 LOMAssistant.cfg：诊断包带这个配置文件、只隐去 AiApiKey 一行（安装器是固定的旧版，认不得新加的配置项）；
/// LOMAssistant_AI 文件夹不进诊断包。没改过的地址和模型名不存，换回来时用那时的默认值，Mod 换了推荐模型能跟上。
/// </summary>
internal static class AiProviderStore
{
	private static JObject _all;

	// 和对话存在同一个文件夹。读对话时会扫到这个文件：里面没有对话编号，当成空对话跳过
	private static string StorePath => Path.Combine(AiSessions.Folder, "providers.json");

	/// <summary>换到第 index 家：记下现在这家填的，再取出那家上次填的；没填过的用默认地址、默认模型，Key 空着。</summary>
	public static void Switch(int index)
	{
		LlmProvider from = AiChat.Provider, to = LlmProvider.At(index);
		JObject all = Load();
		JObject mine = Snapshot(from);
		if (mine.Count > 0)
		{
			all[from.Name] = mine;
		}
		else
		{
			all.Remove(from.Name);
		}
		Save(all);
		JObject saved = all[to.Name] as JObject;
		ConfigManager.AiProvider.Value = index;
		ConfigManager.AiApiKey.Value = Text(saved, "Key") ?? "";
		ConfigManager.AiBaseUrl.Value = Text(saved, "BaseUrl") ?? to.BaseUrl;
		ConfigManager.AiModel.Value = Text(saved, "Model") ?? to.Model;
		ConfigManager.AiDeepModel.Value = Text(saved, "DeepModel") ?? to.DeepModel;
		ConfigManager.AiCompanionModel.Value = Text(saved, "CompanionModel") ?? "";
	}

	// 现在这家填了什么。和默认一样的不记；Key 空着、别的也没改过，就不留这一家的记录
	private static JObject Snapshot(LlmProvider provider)
	{
		var o = new JObject();
		Keep(o, "Key", ConfigManager.AiApiKey.Value, "");
		Keep(o, "BaseUrl", ConfigManager.AiBaseUrl.Value, provider.BaseUrl);
		Keep(o, "Model", ConfigManager.AiModel.Value, provider.Model);
		Keep(o, "DeepModel", ConfigManager.AiDeepModel.Value, provider.DeepModel);
		Keep(o, "CompanionModel", ConfigManager.AiCompanionModel.Value, "");
		return o;
	}

	private static void Keep(JObject o, string name, string value, string usual)
	{
		value = (value ?? "").Trim();
		if (value != usual)
		{
			o[name] = value;
		}
	}

	// 没记过返回 null（用默认值）；记的是空字符串就返回空字符串（玩家特意清空的，比如深度模式不另选模型）
	private static string Text(JObject o, string name)
	{
		JToken token = o?[name];
		return token != null && token.Type == JTokenType.String ? (string)token : null;
	}

	private static JObject Load()
	{
		if (_all != null)
		{
			return _all;
		}
		_all = new JObject();
		try
		{
			if (File.Exists(StorePath))
			{
				_all = JObject.Parse(File.ReadAllText(StorePath, Encoding.UTF8));
			}
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[AI] 读不了各服务商的设置: " + ex.Message);
		}
		return _all;
	}

	// 存不了也照常切换：这次开游戏期间还记在内存里，换回来还在
	private static void Save(JObject all)
	{
		try
		{
			if (all.Count == 0 && !File.Exists(StorePath))
			{
				return;
			}
			Directory.CreateDirectory(AiSessions.Folder);
			string tmp = StorePath + ".tmp";
			File.WriteAllText(tmp, all.ToString(Formatting.Indented), new UTF8Encoding(false));
			if (File.Exists(StorePath))
			{
				File.Delete(StorePath);
			}
			File.Move(tmp, StorePath);
		}
		catch (Exception ex)
		{
			FileLogger.LogWarning("[AI] 存不了各服务商的设置: " + ex.Message);
		}
	}
}
