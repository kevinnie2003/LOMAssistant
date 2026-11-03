using System.Collections;
using LOMAssistant.Config;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 启动加速：标题画面「启程」等按钮慢慢浮现的开场动画（Title 场景里的 TitlePlay 时间轴，11 秒）加快播放。
/// 开头的 Logo 是 Unity 启动画面，放的时候游戏还没开始跑脚本（Mod 叫停也要等它放完才轮到），Mod 跳不过。
/// 只改播放速度，动画、音乐和按钮最后的样子都不变。
/// </summary>
public static class FastStartup
{
	private const float TitleSpeed = 6f;

	public static void Install(MonoBehaviour host)
	{
		if (!ConfigManager.EnableFastStartup.Value)
		{
			return;
		}
		// 插件加载得晚、标题已经在放了，也加快
		Scene title = SceneManager.GetSceneByName("Title");
		if (title.isLoaded)
		{
			host.StartCoroutine(SpeedTitle(title));
		}
		SceneManager.sceneLoaded += (scene, mode) =>
		{
			if (scene.name == "Title")
			{
				host.StartCoroutine(SpeedTitle(scene));
			}
		};
	}

	private static IEnumerator SpeedTitle(Scene scene)
	{
		// 时间轴在场景加载后才开始播，等它的播放图建好
		for (int frame = 0; frame < 30; frame++)
		{
			yield return null;
			if (!scene.isLoaded)
			{
				yield break;
			}
			bool found = false;
			foreach (GameObject root in scene.GetRootGameObjects())
			{
				foreach (PlayableDirector director in root.GetComponentsInChildren<PlayableDirector>(true))
				{
					if (director.state != PlayState.Playing || !director.playableGraph.IsValid() || director.playableGraph.GetRootPlayableCount() == 0)
					{
						continue;
					}
					director.playableGraph.GetRootPlayable(0).SetSpeed(TitleSpeed);
					FileLogger.Log($"[开游戏提速] 标题开场动画 {director.name} 加快 {TitleSpeed} 倍（原长 {director.duration:0.0} 秒）");
					found = true;
				}
			}
			if (found)
			{
				yield break;
			}
		}
	}
}
