using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Extras;

public sealed partial class Gallery
{
	private bool _foeShowTaunts;
	private GUIStyle _foeTauntCard, _foeTauntHeading, _foeTauntLine;
	private Color _foeTauntRule;

	private static readonly (string Kind, string Label)[] FoeTauntGroups =
	{
		("NormalTalk", "普通嘴功"),
		("GoodTalk", "善良嘴功"),
		("BadTalk", "邪恶嘴功"),
		("StopTalk", "嘴功被打断"),
		("TalkAttacked", "被嘴攻时的回应"),
		("Sequential", "剧情顺序嘴功"),
		("StoryReply", "剧情嘴功回应"),
	};

	private void BuildFoeTauntStyles()
	{
		Color paper = PanelTheme.PaperLight;
		Color card = Color.Lerp(paper, PanelTheme.Paper, 0.5f);
		Color heading = Color.Lerp(paper, PanelTheme.PaperDeep, 0.45f);
		_foeTauntRule = Color.Lerp(card, PanelTheme.Edge, 0.34f);
		_foeTauntCard = new GUIStyle
		{
			border = new RectOffset(2, 2, 2, 2),
			padding = new RectOffset(1, 1, 1, 1),
			stretchWidth = true,
		};
		_foeTauntCard.normal.background = PanelTheme.Framed(card, _foeTauntRule);

		int inset = Mathf.RoundToInt(S(16f));
		int headingPad = Mathf.RoundToInt(S(10f));
		_foeTauntHeading = PanelTheme.Label(_scale, 15, Color.Lerp(PanelTheme.Ink, PanelTheme.InkMuted, 0.35f));
		_foeTauntHeading.fontStyle = FontStyle.Bold;
		_foeTauntHeading.padding = new RectOffset(inset, inset, headingPad, headingPad);
		_foeTauntHeading.margin = new RectOffset();
		_foeTauntHeading.normal.background = PanelTheme.Framed(heading, heading);
		_foeTauntHeading.stretchWidth = true;

		int linePad = Mathf.RoundToInt(S(9f));
		_foeTauntLine = PanelTheme.Label(_scale, 17, PanelTheme.Ink);
		_foeTauntLine.fontStyle = FontStyle.Normal;
		_foeTauntLine.richText = false;
		_foeTauntLine.padding = new RectOffset(inset, inset, linePad, linePad);
		_foeTauntLine.margin = new RectOffset();
		_foeTauntLine.stretchWidth = true;
	}

	private void DrawFoeTaunts(DuelEncounter encounter, float width)
	{
		var locale = LocalizationManager.Instance?.LocaleResolver;
		bool any = false;
		foreach (var (kind, label) in FoeTauntGroups)
		{
			if (!encounter.Taunts.TryGetValue(kind, out string[] keys) || keys.Length == 0)
				continue;
			if (any) GUILayout.Space(S(14f));
			any = true;
			GUILayout.BeginVertical(_foeTauntCard, GUILayout.MaxWidth(width));
			GUILayout.Label(TextConverter.ForDisplay(label), _foeTauntHeading);
			bool repaint = Event.current.type == EventType.Repaint;
			if (repaint)
			{
				Rect band = GUILayoutUtility.GetLastRect();
				Fill(new Rect(band.x, band.yMax - 1f, band.width, 1f), _foeTauntRule);
			}
			for (int i = 0; i < keys.Length; i++)
			{
				string key = keys[i];
				// 台词跟随游戏语言，界面栏目跟随 Mod 语言。
				string localeKey = "CombatTalking/" + key;
				string text = locale?.GetString(localeKey);
				if (string.IsNullOrWhiteSpace(text) || text == localeKey)
					text = string.Format(TextConverter.ForDisplay("游戏台词未找到（{0}）"), key);
				else
					text = RichText.Strip(text.Replace("\\n", "\n"));
				GUILayout.Label(text, _foeTauntLine);
				if (repaint && i < keys.Length - 1)
				{
					Rect row = GUILayoutUtility.GetLastRect();
					float inset = _foeTauntLine.padding.left;
					Fill(new Rect(row.x + inset, row.yMax - 1f, Mathf.Max(0f, row.width - 2f * inset), 1f), _foeTauntRule);
				}
			}
			GUILayout.EndVertical();
		}
		if (!any)
			GUILayout.Label(TextConverter.ForDisplay("此配置没有嘴功台词"), _foeWrapped);
	}
}
