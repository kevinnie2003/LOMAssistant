using System;
using System.Collections.Generic;
using System.Linq;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Extras;

/// <summary>与 CombatCharacterStatusUI.SetSliderValue 使用同一个分档函数；仅查询定义，不写主角属性。</summary>
internal static class GalleryDuelPersonality
{
	internal static readonly GameStatType[] Types = { GameStatType.性情, GameStatType.處世, GameStatType.道德, GameStatType.修養 };
	internal static readonly string[] Keys = { "disposition", "behaviour", "karma", "training" };

	internal static int Level(int value, int maximum = 100, int count = 5)
	{
		if (maximum <= 0 || count <= 0 || maximum < count) throw new ArgumentOutOfRangeException(nameof(maximum));
		// 原版 0–19、20–39、40–59、60–79、80–100；100 仍属第五档。
		return Math.Min(count - 1, GameStatUtils.GetGameStatLevel(Math.Max(0, Math.Min(value, maximum)), maximum, count));
	}

	internal static float Position(int value, int maximum = 100) => maximum > 0 ? Math.Max(0f, Math.Min(1f, value / (float)maximum)) : 0f;

	internal static string RatingKey(int index, int value, GameStat definition = null)
	{
		if (definition != null && definition.LevelLength > 0 && definition.Max >= definition.LevelLength)
			return definition.GetLevelText(Level(value, definition.Max, definition.LevelLength));
		return "StatLevel/" + Keys[index] + (Level(value) + 1).ToString("00");
	}
}

public sealed partial class Gallery
{
	private Sprite _foePersonalityBar, _foePersonalityPointer;
	private readonly List<UnityEngine.Object> _foePersonalityOwned = new List<UnityEngine.Object>();
	private bool _foePersonalityLoaded;

	private void EnsureFoePersonalityResources()
	{
		if (_foePersonalityLoaded) return;
		_foePersonalityLoaded = true;
		try
		{
			var catalog = GalleryDuelCatalog.Instance;
			_foePersonalityBar = FoePersonalitySprite(catalog.PersonalityBar);
			_foePersonalityPointer = FoePersonalitySprite(catalog.PersonalityPointer);
		}
		catch (Exception e)
		{
			// 资源有变化时继续显示数值及评价，不每帧重试。
			FileLogger.LogWarning("[鉴赏] 四维滑条资源读不到: " + e.Message);
		}
	}

	private Sprite FoePersonalitySprite(DuelRadarResource source)
	{
		if (source == null) throw new InvalidOperationException("Missing personality texture metadata");
		Sprite existing = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s.name == source.Name);
		if (existing != null) return existing;
		Texture2D texture = GalleryDuelRadar.LoadGameTexture(source);
		_foePersonalityOwned.Add(texture);
		Sprite sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
		sprite.hideFlags = HideFlags.HideAndDontSave;
		_foePersonalityOwned.Add(sprite);
		return sprite;
	}

	private void CloseFoePersonality()
	{
		for (int i = _foePersonalityOwned.Count - 1; i >= 0; i--)
			if (_foePersonalityOwned[i] != null) Destroy(_foePersonalityOwned[i]);
		_foePersonalityOwned.Clear();
		_foePersonalityBar = _foePersonalityPointer = null;
		_foePersonalityLoaded = false;
	}

	private static string FoePersonalityText(string key)
	{
		GalleryDuelCatalog.Instance.PersonalityLabels.TryGetValue(key, out string fallback);
		string value = LocalizationManager.Instance?.LocaleResolver?.GetString(key);
		if (string.IsNullOrEmpty(value) || value == key) value = fallback ?? key;
		return TextConverter.ForDisplay(TextConverter.ToSimplifiedUnlessJapanese(value));
	}

	private void DrawFoePersonality(DuelProfile profile, float width)
	{
		GUILayout.Space(S(8f));
		GUILayout.Label(TextConverter.ForDisplay("角色四维"), _foeStatsHeading);
		var names = new string[4];
		var ratings = new string[4];
		var maxima = new int[4];
		float nameWidth = 0f, ratingWidth = 0f;
		for (int i = 0; i < 4; i++)
		{
			GameStat definition = PlayerStatManagerData.Instance?.Stats?.Get(GalleryDuelPersonality.Types[i]);
			names[i] = FoePersonalityText("PlayerStat/" + GalleryDuelPersonality.Keys[i]);
			ratings[i] = FoePersonalityText(GalleryDuelPersonality.RatingKey(i, profile.Four[i], definition));
			maxima[i] = definition != null && definition.Max > 0 ? definition.Max : 100;
			nameWidth = Mathf.Max(nameWidth, _foeWrapped.CalcSize(new GUIContent(names[i] + "  " + profile.Four[i])).x);
			ratingWidth = Mathf.Max(ratingWidth, _foeWrapped.CalcSize(new GUIContent(ratings[i])).x);
		}
		// 宽度不够时滑条另起一行，长译名和评价仍完整可见。
		bool stacked = width - nameWidth - ratingWidth - S(38f) < S(100f);
		for (int i = 0; i < 4; i++)
		{
			Rect row = GUILayoutUtility.GetRect(width, S(stacked ? 76f : 46f));
			if (Event.current.type != EventType.Repaint) continue;
			EnsureFoePersonalityResources();
			float textY = stacked ? row.y : row.y + S(12f);
			GUI.Label(new Rect(row.x, textY, nameWidth + S(4f), S(28f)), names[i] + "  " + profile.Four[i], _foeWrapped);
			GUI.Label(new Rect(row.xMax - ratingWidth, textY, ratingWidth, S(28f)), ratings[i], _foeWrapped);
			float left = stacked ? row.x + S(8f) : row.x + nameWidth + S(18f);
			float right = stacked ? row.xMax - S(8f) : row.xMax - ratingWidth - S(18f);
			float barY = stacked ? row.y + S(57f) : row.y + S(29f);
			if (_foePersonalityBar != null && _foePersonalityPointer != null)
			{
				// 原版决斗 Slider：140×6 底条，16×36 指针，中心在条上方 10.6 像素。
				DrawFoePersonalitySprite(_foePersonalityBar, new Rect(left, barY - S(3f), right - left, S(6f)));
				float x = Mathf.Lerp(left, right, GalleryDuelPersonality.Position(profile.Four[i], maxima[i]));
				DrawFoePersonalitySprite(_foePersonalityPointer, new Rect(x - S(8f), barY - S(28.6f), S(16f), S(36f)));
			}
		}
	}

	private static void DrawFoePersonalitySprite(Sprite sprite, Rect rect)
	{
		Rect part = sprite.textureRect;
		GUI.DrawTextureWithTexCoords(rect, sprite.texture, new Rect(part.x / sprite.texture.width, part.y / sprite.texture.height,
			part.width / sprite.texture.width, part.height / sprite.texture.height));
	}
}
