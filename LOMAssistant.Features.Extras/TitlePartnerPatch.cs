using HarmonyLib;
using LOMAssistant.Config;
using Mortal.Core;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 标题画面人物自选：游戏按结局记一个编号（SaveSystem.TitlePartner），标题画面按它显示结缘的人。
/// 设置页「标题画面人物」选了某个人时，标题画面读这个编号时换成她（不改全局存档里记的值，选回「跟随结局」就恢复）。
/// </summary>
public static class TitlePartnerPatch
{
	/// <summary>设置里的选项（下标）对应的编号：-1 跟随结局，0 不显示；标题画面能显示的只有这几个。</summary>
	internal static readonly int[] Values = { -1, 0, 1, 2, 3, 5, 6, 8, 20, 0 };

	private const int ShowAll = 9; // 「全部显示」：编号按不显示算，再由下面把人物都打开

	// 游戏把这些人物都摆在同一处：小梅（画面里连着吊床和主角）和大师兄留在原位，其他人分在主角两边，脚都落在山坡上
	private static readonly (string Field, float X, float Y)[] Layout =
	{
		("_titleGirl3", 0f, 0f),
		("_titleBrother1", 0f, 0f),
		("_titleGirl5", 196f, 34f),
		("_titleGirl2", 55f, -51f),
		("_titleSister", -144f, -58f),
		("_titleGirl6", 11f, -94f),
		("_titleGirl8", -218f, -82f),
	};

	internal static readonly string[] Names = { "跟随结局", "不显示", "小师妹", "龙湘", "叶云裳", "小兰", "小梅", "小竹", "大师兄", "全部显示" };

	[HarmonyPostfix]
	[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.TitlePartner), MethodType.Getter)]
	public static void TitlePartner_Postfix(ref int __result)
	{
		int choice = ConfigManager.TitlePartnerChoice.Value;
		if (choice > 0 && choice < Values.Length)
		{
			__result = Values[choice];
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(TitleManager), "DisplayPartner")]
	public static void DisplayPartner_Postfix(TitleManager __instance)
	{
		if (ConfigManager.TitlePartnerChoice.Value != ShowAll)
		{
			return;
		}
		var fields = Traverse.Create(__instance);
		foreach (var (name, x, y) in Layout)
		{
			UnityEngine.GameObject figure = fields.Field(name).GetValue<UnityEngine.GameObject>();
			if (figure != null)
			{
				figure.SetActive(true);
				if (figure.transform is UnityEngine.RectTransform rect)
				{
					rect.anchoredPosition += new UnityEngine.Vector2(x, y);
				}
			}
		}
		// 龙湘的图左边画着主角的手（原本主角站在那里），全部显示时主角在别处，裁掉
		if (fields.Field("_titleGirl8").GetValue<UnityEngine.GameObject>()?.GetComponent<UnityEngine.UI.Image>() is UnityEngine.UI.Image longxiang)
		{
			longxiang.type = UnityEngine.UI.Image.Type.Filled;
			longxiang.fillMethod = UnityEngine.UI.Image.FillMethod.Horizontal;
			longxiang.fillOrigin = (int)UnityEngine.UI.Image.OriginHorizontal.Right;
			longxiang.fillAmount = 0.7f;
		}
		// 和游戏显示小梅时一样，主角由她的画面代替
		fields.Field("_titlePlayer").GetValue<UnityEngine.GameObject>()?.SetActive(false);
	}
}
