using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

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
		foreach (MethodInfo test in typeof(Program).Assembly.GetTypes().Where(t => t.IsPublic && t.Name.EndsWith("Tests")).SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public).Where(m => m.Name.StartsWith("Test")).OrderBy(m => m.MetadataToken)))
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
