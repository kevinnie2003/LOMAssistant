using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LOMAssistant.Config;
using UnityEngine;

namespace LOMAssistant;

/// <summary>
/// 检测和其他 Mod 的冲突：等所有 Mod 都加载完，看本 Mod 改过的游戏方法有没有被别的 Mod 也改了
/// （例如旧版骰子大师、多功能 Mod 会让选项提示失效）。不靠文件名猜，只报真正撞到的：
/// 两边真会打架的才报（对方会拦下原方法、两边都改返回值、两边都改写同一段代码），能一起用的只记日志。
/// 结果写进日志，并显示在 F8 面板顶部。
/// </summary>
public static class ConflictDetector
{
	/// <summary>给 F8 面板显示的提示，一条一个冲突的 Mod。空表示没有冲突。</summary>
	public static readonly List<string> Warnings = new List<string>();

	public static bool Checked { get; private set; }

	// 已经自动打开设置页提示过的冲突，一行一条
	private static string ShownMark => Path.Combine(Path.GetDirectoryName(typeof(ConflictDetector).Assembly.Location) ?? ".", "StoryReview", "compat-warnings");

	/// <summary>有没有还没自动打开设置页提示过的冲突。</summary>
	public static bool HasNewWarning
	{
		get
		{
			if (Warnings.Count == 0)
			{
				return false;
			}
			if (_shown == null)
			{
				try
				{
					_shown = new HashSet<string>(File.Exists(ShownMark) ? File.ReadAllLines(ShownMark) : new string[0]);
				}
				catch (Exception e)
				{
					FileLogger.LogWarning("[冲突检测] 读取已提示记录失败: " + e.Message);
					_shown = new HashSet<string>();
				}
			}
			return Warnings.Any(warning => !_shown.Contains(warning));
		}
	}

	private static HashSet<string> _shown;

	/// <summary>记下这些冲突已经提示过，下次启动不再自动打开设置页。</summary>
	public static void RememberWarnings()
	{
		try
		{
			List<string> added = Warnings.Where(warning => _shown.Add(warning)).ToList();
			Directory.CreateDirectory(Path.GetDirectoryName(ShownMark));
			File.AppendAllLines(ShownMark, added);
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[冲突检测] 保存已提示记录失败: " + e.Message);
		}
	}

	public static IEnumerator CheckLater(Harmony ours)
	{
		// 其他 Mod 可能在 Awake 之后才打补丁，等几秒再查
		yield return new WaitForSeconds(5f);
		StoryWorkbenchCompat.Apply();
		JapanesePatchCompat.Apply();
		HotkeyCompat.Apply(ours.Id);
		Check(ours);
	}

	public static void Check(Harmony ours)
	{
		Warnings.Clear();
		Warnings.AddRange(BepInEx5Plugins.Notices);
		Warnings.AddRange(JapanesePatchCompat.Notices);
		Warnings.AddRange(StoryWorkbenchCompat.Notices);
		Warnings.AddRange(HotkeyCompat.Notices);
		try
		{
			Dictionary<string, string> pluginNames = LoadedPluginNames();
			string others = string.Join("，", pluginNames.Where(p => p.Key != ours.Id).Select(p => $"{p.Value}（{p.Key}）").ToArray());
			FileLogger.Log("[冲突检测] 同时加载的其他 BepInEx 插件：" + (others.Length > 0 ? others : "无"));

			// owner -> 和本 Mod 撞到的方法
			var clashes = new Dictionary<string, List<string>>();
			var owners = new Dictionary<string, Assembly>();
			foreach (MethodBase method in ours.GetPatchedMethods())
			{
				Patches info = Harmony.GetPatchInfo(method);
				if (info == null)
				{
					continue;
				}
				foreach (string owner in info.Owners)
				{
					// 剧情随行：已经做了兼容处理，不算冲突
					if (owner == ours.Id || owner == StoryWorkbenchCompat.Id)
					{
						continue;
					}
					// 翻译补丁（日语、英文，见 IsTranslationPatch）：不算冲突
					Assembly assembly = OwnerAssembly(info, owner);
					if (assembly != null && IsTranslationPatch(assembly.GetName().Name))
					{
						FileLogger.Log($"[冲突检测] 翻译补丁（{assembly.GetName().Name}）也改了 {method.DeclaringType?.Name}.{method.Name}，已兼容，不提示");
						continue;
					}
					// 剧情 Mod 的加载器：只有 Mod 带了对应内容才接管，其余放行，本 Mod 也读它的内容（见 ModScripts）
					if (assembly != null && IsStoryModLoader(assembly))
					{
						FileLogger.Log($"[冲突检测] 剧情 Mod（{assembly.GetName().Name}）也改了 {method.DeclaringType?.Name}.{method.Name}，已兼容，不提示");
						continue;
					}
					if (assembly != null && !owners.ContainsKey(owner))
					{
						owners[owner] = assembly;
					}
					// 只是在前后各加各的（不拦原方法、不改结果、不改写代码）：能一起用，只记日志
					if (!Intrudes(info, owner, ours.Id))
					{
						FileLogger.Log($"[冲突检测] {owner} 也改了 {method.DeclaringType?.Name}.{method.Name}，只是各加各的，能一起用，不提示");
						continue;
					}
					if (!clashes.TryGetValue(owner, out List<string> list))
					{
						clashes[owner] = list = new List<string>();
					}
					list.Add(method.DeclaringType?.Name + "." + method.Name);
				}
			}

			foreach (KeyValuePair<string, List<string>> clash in clashes)
			{
				// Harmony 实例没起名字（harmony-auto-…）时，用补丁所在的程序集找出是哪个插件
				string name = pluginNames.TryGetValue(clash.Key, out string n) ? n : owners.TryGetValue(clash.Key, out Assembly asm) ? PluginName(asm, pluginNames) : clash.Key;
				string methods = string.Join("、", clash.Value.Distinct().ToArray());
				string message = $"检测到冲突的 Mod：{name}（{clash.Key}），和本 Mod 同时修改了 {clash.Value.Distinct().Count()} 处游戏代码：{methods}。本 Mod 的部分功能可能不生效（如骰子、选项显示）；出现问题建议删掉旧版骰子大师、多功能 Mod、活侠传助手等同类 Mod 后重新覆盖安装。";
				LOMAssistantPlugin.LogWarning(message);
				FileLogger.LogWarning("[冲突检测] " + message);
				Warnings.Add($"{name} 和本 Mod 改了同样的游戏代码（{clash.Value.Distinct().Count()} 处），部分功能可能不生效；出现问题建议删掉它");
			}
			if (clashes.Count == 0)
			{
				FileLogger.Log("[冲突检测] 没有发现和本 Mod 冲突的 Mod。");
			}
		}
		catch (Exception e)
		{
			FileLogger.LogWarning("[冲突检测] 检测失败：" + e.Message);
		}
		Checked = true;
	}

	// 本 Mod 能让出去的热键和它在设置页「热键」里的叫法；开本窗口的键不让（让了就打不开窗口改回来）
	private static (ConfigElement<KeyCode> Key, string Name)[] Hotkeys => new[]
	{
		(ConfigManager.DebugPanelKey, "数值页"),
		(ConfigManager.PanelKey, "设置页"),
		(ConfigManager.SaveNoteKey, "存档备注"),
		(ConfigManager.HideUIKey, "隐藏界面看 CG"),
		(ConfigManager.SkipReadKey, "一键快进已读对话"),
		(ConfigManager.AnytimePanelKey, "随时存档页"),
		(ConfigManager.AnytimeSaveKey, "随时存档：存新档"),
		(ConfigManager.AnytimeLoadKey, "随时存档：读取最新存档"),
		(ConfigManager.AnytimeRollbackKey, "回退到判定前"),
	};

	/// <summary>别的 Mod 占着改不了的键：本 Mod 绑在这些键上的热键改成不绑定，返回让出去的热键名。</summary>
	internal static List<string> YieldHotkeys(params KeyCode[] taken)
	{
		var names = new List<string>();
		foreach (var (key, name) in Hotkeys)
		{
			if (key != null && Array.IndexOf(taken, key.Value) >= 0)
			{
				key.Value = KeyCode.None;
				names.Add(name);
			}
		}
		return names;
	}

	/// <summary>
	/// owner 和本 Mod（ours）在这个方法上的补丁会不会打架，只认这几种：
	/// - owner 的前置补丁会拦下原方法（返回 bool，而且代码里真有返回 false 的地方）：原方法和本 Mod 靠它的改动都不走了；
	/// - 两边都改返回值（ref __result，或本 Mod 拦下原方法自己给结果）：后改的把先改的盖掉；
	/// - 两边都改写这段代码（transpiler）。
	/// 只在前后加东西的、只有一边改返回值的、前置补丁一定放行的，两边各改各的，能一起用。
	/// </summary>
	private static bool Intrudes(Patches info, string owner, string ours)
	{
		bool ChangesResult(Patch p) => p.PatchMethod.GetParameters().Any(x => x.Name == "__result" && x.ParameterType.IsByRef);
		bool Skips(Patch p) => p.PatchMethod.ReturnType == typeof(bool) && MayReturnFalse(p.PatchMethod);
		var theirs = info.Prefixes.Concat(info.Postfixes).Where(p => p.owner == owner).ToList();
		var mine = info.Prefixes.Concat(info.Postfixes).Where(p => p.owner == ours).ToList();
		return info.Prefixes.Any(p => p.owner == owner && Skips(p))
			|| (theirs.Any(ChangesResult) && mine.Any(p => ChangesResult(p) || (info.Prefixes.Contains(p) && Skips(p))))
			|| (info.Transpilers.Any(p => p.owner == owner) && info.Transpilers.Any(p => p.owner == ours));
	}

	/// <summary>返回 bool 的补丁代码里有没有「false」（IL 的 ldc.i4.0）；一句都没有就是一定放行。读不出时当作有。</summary>
	private static bool MayReturnFalse(MethodInfo method)
	{
		try
		{
			byte[] il = method.GetMethodBody()?.GetILAsByteArray();
			return il == null || Array.IndexOf(il, (byte)0x16) >= 0;
		}
		catch
		{
			return true;
		}
	}

	// 某个 Harmony 实例（owner）打在这个方法上的补丁所在的程序集
	private static Assembly OwnerAssembly(Patches info, string owner)
	{
		foreach (Patch patch in info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers))
		{
			if (patch.owner == owner && patch.PatchMethod?.DeclaringType != null)
			{
				return patch.PatchMethod.DeclaringType.Assembly;
			}
		}
		return null;
	}

	// 程序集对应的插件名（插件列表里实例在这个程序集里的那个），找不到就用程序集名
	private static string PluginName(Assembly assembly, Dictionary<string, string> pluginNames)
	{
		try
		{
			Type chainloader = AccessTools.TypeByName("BepInEx.Unity.Mono.Bootstrap.UnityChainloader");
			object loader = chainloader != null ? AccessTools.Property(chainloader, "Instance")?.GetValue(null, null) : null;
			if (loader != null && AccessTools.Property(loader.GetType(), "Plugins")?.GetValue(loader, null) is IDictionary plugins)
			{
				foreach (DictionaryEntry entry in plugins)
				{
					object instance = AccessTools.Property(entry.Value.GetType(), "Instance")?.GetValue(entry.Value, null);
					if (instance != null && instance.GetType().Assembly == assembly && pluginNames.TryGetValue((string)entry.Key, out string name))
					{
						return name;
					}
				}
			}
		}
		catch
		{
		}
		return assembly.GetName().Name;
	}

	// GUID -> 插件名。用反射读 BepInEx 的插件列表，读不到就返回空。
	internal static Dictionary<string, string> LoadedPluginNames()
	{
		var result = new Dictionary<string, string>();
		try
		{
			Type chainloader = AccessTools.TypeByName("BepInEx.Unity.Mono.Bootstrap.UnityChainloader");
			object instance = chainloader != null ? AccessTools.Property(chainloader, "Instance")?.GetValue(null, null) : null;
			if (instance == null)
			{
				return result;
			}
			if (AccessTools.Property(instance.GetType(), "Plugins")?.GetValue(instance, null) is IDictionary plugins)
			{
				foreach (DictionaryEntry entry in plugins)
				{
					object metadata = AccessTools.Property(entry.Value.GetType(), "Metadata")?.GetValue(entry.Value, null);
					string name = metadata != null ? AccessTools.Property(metadata.GetType(), "Name")?.GetValue(metadata, null) as string : null;
					result[(string)entry.Key] = name ?? (string)entry.Key;
				}
			}
		}
		catch
		{
		}
		return result;
	}
	// 翻译补丁：日语补丁（LOM_JP_…）、英文补丁（LLM 版 FanslationStudio.LegendOfMortal…，XUnity.AutoTranslator），
	// 和本 Mod 一起装玩家测试过，只是同一处游戏代码各改各的（翻译、字体），不算冲突
	private static bool IsStoryModLoader(Assembly assembly) => assembly.GetType("Mortal.HookCustomStory") != null
		|| assembly.GetName().Name.StartsWith("HahuranGongdong", StringComparison.OrdinalIgnoreCase);

	private static bool IsTranslationPatch(string name) => name.StartsWith("LOM_JP_", StringComparison.OrdinalIgnoreCase)
		|| name.StartsWith("FanslationStudio.LegendOfMortal", StringComparison.OrdinalIgnoreCase)
		|| name.StartsWith("XUnity.AutoTranslator", StringComparison.OrdinalIgnoreCase);
}
