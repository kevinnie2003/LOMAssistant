using System;
using System.Collections.Generic;
using System.Linq;
using LOMAssistant.Config;
using LOMAssistant.Features.Story;
using Mortal.Battle;
using Mortal.Combat;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Combat;

/// <summary>
/// 战斗失败死亡提示（设置「战斗失败死亡提示」）：这场输了会走到生死簿的死亡时，整场战斗在屏幕上方写出死亡名称和收录情况。
/// 游戏有两条路：决斗关卡直接标了输了就死；或者打完接下一个剧本，剧本开头按输赢判断后跳到死亡画面。
/// 第二条路开打时下一个剧本已经定好（CurrentStoryScript），读它的分岔即可；输了之后还要过别的分岔的写「可能」。
/// </summary>
internal sealed class FightDeathHint : MonoBehaviour
{
	private static FightDeathHint _instance;

	private UnityEngine.Object _fight;
	private string _text;
	private GUIStyle _style;
	private float _scale = -1f;

	public static void Ensure(GameObject host)
	{
		if (_instance == null)
		{
			_instance = host.GetComponent<FightDeathHint>() ?? host.AddComponent<FightDeathHint>();
		}
	}

	private void Update()
	{
		CombatManager duel = CombatManager.Instance;
		GameLevelManager battle = duel == null ? GameLevelManager.Instance : null;
		UnityEngine.Object fight = duel != null ? duel : battle;
		if (fight == null || !ConfigManager.FightDeathHint.Value || LevelTestMode.IsActive)
		{
			_fight = null;
			_text = null;
			return;
		}
		// 决斗的关卡是开场协程里读进来的，读到以后才算开打
		if (fight == _fight || (duel != null && duel.CombatLevel == null))
		{
			return;
		}
		_fight = fight;
		try
		{
			_text = Describe(duel != null ? duel.CombatLevel : null);
			FileLogger.Log($"[战斗失败死亡提示] 关卡 {PlayerStatManagerData.Instance.CurrentSceneKey}，下一个剧本 {PlayerStatManagerData.Instance.CurrentStoryScript}：{_text ?? "无"}");
		}
		catch (Exception e)
		{
			_text = null;
			FileLogger.LogWarning("战斗失败死亡提示失败: " + e.Message);
		}
	}

	/// <summary>level 是决斗关卡，战役为 null。不会死时返回 null。</summary>
	private static string Describe(CombatLevel level)
	{
		if (level != null && level.DeadEnd)
		{
			return level.DeadLibrary != null ? string.Format(TextConverter.ForDisplay("输了会死：{0}"), Name(level.DeadLibrary)) : TextConverter.ForDisplay("输了会死");
		}
		PlayerStatManagerData stat = PlayerStatManagerData.Instance;
		StoryScript next = stat.CurrentNextScene == "Story" ? StoryScripts.Get(stat.CurrentStoryScript) : null;
		if (next == null)
		{
			return null;
		}
		string kind = level != null ? "Combat_Result_" : "Battle_Result_";
		var ids = new List<string>();
		bool certain = false;
		foreach (var (path, death, id) in next.Endings)
		{
			List<StoryStep> results = path.Where(s => s.Kind == StoryStep.Branch && s.Key.StartsWith(kind)).ToList();
			if (!death || results.Count == 0 || !results.All(IsLoss))
			{
				continue;
			}
			if (!ids.Contains(id))
			{
				ids.Add(id);
			}
			certain |= results.Count == path.Count;
		}
		if (ids.Count == 0)
		{
			return null;
		}
		string names = string.Join("、", ids.Select(id => Name(LibrarySystem.Instance.Dead.Get(id))).Where(n => n != null).ToArray());
		if (names.Length == 0)
		{
			return TextConverter.ForDisplay("输了会死");
		}
		return string.Format(TextConverter.ForDisplay(certain && ids.Count == 1 ? "输了会死：{0}" : "输了可能会死：{0}"), names);
	}

	/// <summary>这一支是不是输了。旗标开打时清零，赢了才写 1，所以「等于 0」的第一支是输，「等于 1」的第一支是赢。</summary>
	private static bool IsLoss(StoryStep step)
	{
		switch (step.Key)
		{
			case "Combat_Result_01":
			case "Battle_Result_000":
				return step.Arm == 1;
			case "Combat_Result_02":
			case "Battle_Result_001":
				return step.Arm != 1;
			default:
				return false;
		}
	}

	private static string Name(LibraryItemData item)
	{
		if (item == null)
		{
			return null;
		}
		return TextConverter.ForDisplay(StoryAtlas.LibraryText(item.GetTitleKey(), item.name)) + TextConverter.ForDisplay(item.Count > 0 ? "（生死簿已收录）" : "（生死簿未收录）");
	}

	private void OnGUI()
	{
		if (string.IsNullOrEmpty(_text) || UiHider.Active)
		{
			return;
		}
		GameLevelManager battle = GameLevelManager.Instance;
		float scale = PanelTheme.UiScale;
		if (scale <= 0f || (battle != null && battle.IsGameOver))
		{
			return;
		}
		if (!Mathf.Approximately(scale, _scale) || _style == null)
		{
			_scale = scale;
			_style = PanelTheme.Toast(scale, 18);
			_style.alignment = TextAnchor.MiddleCenter;
			_style.wordWrap = false;
		}
		Vector2 size = _style.CalcSize(new GUIContent(_text));
		float width = Mathf.Min(size.x, Screen.width * 0.9f);
		GUI.Box(new Rect((Screen.width - width) / 2f, Screen.height * 0.003f, width, size.y), _text, PanelTheme.Sync(_style));
	}
}
