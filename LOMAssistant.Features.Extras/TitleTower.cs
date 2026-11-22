using LOMAssistant.Config;
using Mortal.Core;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 练功塔：标题画面多一个按钮，运行试玩版的练功塔模式（剧本 act1 起）。做法和游戏原来的 TitleManager.StartActGame 一样（1.0.6001.2 起游戏删掉了这个方法）。
/// 正式版游戏里还有这些剧本、文本和关卡，只是没有入口。存档、自动存档和生死簿都和正常游玩一样。
/// </summary>
internal static class TitleTower
{
	internal static void AddButton(TitleManager title)
	{
		if (ConfigManager.TitleTower.Value)
		{
			Gallery.CreateTitleButton(title, "TitleTower", "练功塔", Start);
		}
	}

	private static void Start()
	{
		SaveSystem.Instance.SetSlot("act1");
		SaveSystem.Instance.NewActGameData();
		SceneController.Instance.LoadStory();
	}
}
