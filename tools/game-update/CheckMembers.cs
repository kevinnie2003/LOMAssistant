// 游戏更新后检查 Mod 按名字（字符串）找的游戏成员还在不在：编译只能查出直接写在代码里的引用，
// Harmony 的 [HarmonyPatch(typeof(X), "方法名")]、AccessTools、Traverse 的 Field("字段名") 这些字符串要另外查。
// 用法见 tools/game-update.sh。不参与 Mod 本体编译；用 .NET Framework 自带的旧版 csc 编译，只能用 C# 5 的写法。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;

internal static class CheckMembers
{
	private static int Main(string[] args)
	{
		Console.OutputEncoding = new UTF8Encoding(false);
		string managed = args[0], repo = args[1];

		// 游戏目录里所有程序集的类型（按不带命名空间的名字）和所有成员名
		var types = new Dictionary<string, List<TypeDefinition>>();
		var allNames = new HashSet<string>();
		var resolver = new DefaultAssemblyResolver();
		resolver.AddSearchDirectory(managed);
		foreach (string dll in Directory.GetFiles(managed, "*.dll"))
		{
			ModuleDefinition module;
			try
			{
				module = ModuleDefinition.ReadModule(dll, new ReaderParameters { AssemblyResolver = resolver });
			}
			catch
			{
				continue;
			}
			foreach (TypeDefinition t in All(module.Types))
			{
				List<TypeDefinition> list;
				if (!types.TryGetValue(t.Name, out list))
					types[t.Name] = list = new List<TypeDefinition>();
				list.Add(t);
				foreach (string n in Names(t))
					allNames.Add(n);
			}
		}

		string source = string.Join("\n", Directory.GetFiles(repo, "*.cs", SearchOption.AllDirectories)
			.Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
				&& !f.Contains(Path.DirectorySeparatorChar + "tools" + Path.DirectorySeparatorChar)
				&& !f.Contains(Path.DirectorySeparatorChar + "tests" + Path.DirectorySeparatorChar)
				&& !f.Contains(Path.DirectorySeparatorChar + "Installer" + Path.DirectorySeparatorChar)
				&& !Path.GetFileName(f).StartsWith("UiText"))
			.Select(File.ReadAllText).ToArray());

		var problems = new SortedSet<string>();
		int pairs = 0, fields = 0;
		// typeof(类型), "成员名"：只查游戏里有的类型（Mod 自己的类型、系统类型不管）
		foreach (Match m in Regex.Matches(source, "typeof\\((\\w+)\\)\\s*,\\s*\"(\\w+)\""))
		{
			string type = m.Groups[1].Value, member = m.Groups[2].Value;
			List<TypeDefinition> list;
			if (!types.TryGetValue(type, out list))
				continue;
			pairs++;
			if (!list.Any(t => HasMember(t, member)))
				problems.Add("[找不到成员] " + type + "." + member);
		}
		// Field("字段名") / Property("…") / Method("…")：不知道是哪个类型的，只查游戏里还有没有叫这个名字的成员
		foreach (Match m in Regex.Matches(source, "\\.(?:Field|Property|Method)(?:<[^>]*>)?\\(\"(\\w+)\""))
		{
			string member = m.Groups[1].Value;
			fields++;
			if (!allNames.Contains(member))
				problems.Add("[游戏里没有叫这个名字的成员] " + member);
		}
		Console.WriteLine("查了 " + pairs + " 处「类型 + 成员名」、" + fields + " 处按名字取的字段，有问题的 " + problems.Count + " 项" + (problems.Count == 0 ? "。" : "："));
		foreach (string p in problems)
			Console.WriteLine("  " + p);
		return problems.Count == 0 ? 0 : 1;
	}

	private static IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types)
	{
		foreach (TypeDefinition t in types)
		{
			yield return t;
			foreach (TypeDefinition n in All(t.NestedTypes))
				yield return n;
		}
	}

	private static IEnumerable<string> Names(TypeDefinition t)
	{
		return t.Fields.Select(f => f.Name).Concat(t.Methods.Select(x => x.Name)).Concat(t.Properties.Select(p => p.Name)).Concat(t.Events.Select(e => e.Name));
	}

	// 连同父类一起找（AccessTools、Harmony 也会往父类找）
	private static bool HasMember(TypeDefinition t, string member)
	{
		while (t != null)
		{
			if (Names(t).Contains(member))
				return true;
			try
			{
				t = t.BaseType == null ? null : t.BaseType.Resolve();
			}
			catch
			{
				return false;
			}
		}
		return false;
	}
}
