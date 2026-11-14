using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LOMAssistant.Features.Extras;

public sealed partial class Gallery
{
	private DuelEncounter _foeBuffEncounter;
	private List<DuelBuffOption> _foeBuffOptions = new List<DuelBuffOption>();
	private readonly HashSet<string> _foeEnabledBuffs = new HashSet<string>(StringComparer.Ordinal);
	private float _foeBuffHealth;
	private bool _foeBuffSupport;
	private GUIStyle _foeBuffToggle;

	private void SelectFoeBuffs(DuelProfile profile)
	{
		if (ReferenceEquals(_foeBuffEncounter, _foeEncounter)) return;
		_foeBuffEncounter = _foeEncounter;
		_foeBuffOptions = GalleryDuelBuffPreview.Options(profile, _foeEncounter);
		ResetFoeBuffs(profile);
	}

	private void ResetFoeBuffs(DuelProfile profile)
	{
		_foeEnabledBuffs.Clear();
		_foeBuffHealth = profile.MaxHealth > 0 ? Mathf.Round(Mathf.Clamp(profile.Health * 100f / profile.MaxHealth, 0f, 100f)) : 100f;
		_foeBuffSupport = false;
	}

	private void FoeBuffStyles()
	{
		_foeBuffToggle = new GUIStyle(_track)
		{
			alignment = TextAnchor.MiddleLeft, wordWrap = true, fixedHeight = 0f,
			margin = new RectOffset(0, 0, Mathf.RoundToInt(S(3f)), Mathf.RoundToInt(S(5f))),
		};
		Color fill = Color.Lerp(PanelTheme.PaperDeep, Color.white, 0.12f);
		foreach (GUIStyleState state in new[] { _foeBuffToggle.onNormal, _foeBuffToggle.onHover, _foeBuffToggle.onActive, _foeBuffToggle.onFocused })
		{
			state.textColor = PanelTheme.Ink;
			state.background = PanelTheme.Framed(fill, PanelTheme.Edge);
		}
	}

	private void DrawFoeBuffOptions(DuelSkill skill)
	{
		foreach (DuelBuffOption option in _foeBuffOptions.Where(o => ReferenceEquals(o.Skill, skill)))
		{
			DuelBuff buff = GalleryDuelCatalog.Instance.Buffs[option.Key];
			string label = option.Triggered
				? string.Format(F("触发后：{0}"), DuelLocalized("CombatSkill/Name/" + option.Key, buff.Effect.Name))
				: skill != null ? F("启用增益") : DuelLocalized("CombatSkill/Name/" + option.Key, buff.Effect.Name);
			if (skill == null && option.OriginKey != option.Key)
			{
				DuelEffect origin = GalleryDuelCatalog.Instance.Buffs[option.OriginKey].Effect;
				label = DuelLocalized("CombatSkill/Name/" + origin.Key, origin.Name) + " · " + label;
			}
			bool on = _foeEnabledBuffs.Contains(option.Key);
			bool next = GUILayout.Toggle(on, (on ? "✓ " : "＋ ") + label, _foeBuffToggle);
			if (next != on)
			{
				if (next) _foeEnabledBuffs.Add(option.Key);
				else _foeEnabledBuffs.Remove(option.Key);
			}
			DuelBuff condition = option.Condition;
			string hint = condition?.Condition == "HigherEnergy" ? F("生效条件：气力高于对手")
				: condition?.Condition == "NoDebuff" ? F("触发条件：无负面状态")
				: condition?.Condition == "ComboRange" ? string.Format(F("触发条件：连击数大于 {0} 且不超过 {1}"), condition.MinCombo, condition.MaxCombo) : "";
			if (hint.Length > 0) GUILayout.Label(hint, _foeWrapped);
			if (!string.IsNullOrEmpty(option.StoryEvent?.Trigger))
			{
				string storyCondition = DuelCondition(option.StoryEvent.Trigger);
				if (storyCondition.Length > 0)
					GUILayout.Label(string.Format(F("剧情条件：{0}"), storyCondition), _foeWrapped);
				// 决斗临时旗标没有可读名称；提示额外剧情条件，避免误认为只需达到回合数。
				if (System.Text.RegularExpressions.Regex.IsMatch(option.StoryEvent.Trigger, @"旗[標标]_(TC|A)\d+_"))
					GUILayout.Label(F("需满足本场剧情的触发条件"), _foeWrapped);
			}
			if (skill == null)
				GUILayout.Label(DuelLocalized("CombatSkill/Desc/" + option.Key, buff.Effect.Description), _foeWrapped);
		}
	}

	private void DrawFoeBuffControls(DuelProfile profile)
	{
		if (_foeBuffOptions.Count == 0) return;
		GUILayout.Label(F("勾选增益，预览生效后的六维。"), _foeWrapped);
		if (_foeBuffOptions.Any(o => _foeEnabledBuffs.Contains(o.Key)
			&& GalleryDuelCatalog.Instance.Buffs[o.Key].Modifiers.Any(m => m.Kind == "Health")))
		{
			GUILayout.Label(string.Format(F("预览血量 {0}%"), Mathf.RoundToInt(_foeBuffHealth)), _foeWrapped);
			// 面板支持按住拖动滚动；滑条按下时让出拖动，避免横拖血量变成滚页面。
			Rect slider = GUILayoutUtility.GetRect(1f, S(24f), GUILayout.ExpandWidth(true));
			if (Event.current.type == EventType.MouseDown && slider.Contains(Event.current.mousePosition))
				_foeScrollDrag = false;
			_foeBuffHealth = Mathf.Round(GUI.HorizontalSlider(slider, _foeBuffHealth, 0f, 100f));
		}
		if (_foeBuffOptions.Any(o => _foeEnabledBuffs.Contains(o.Key)
			&& GalleryDuelCatalog.Instance.Buffs[o.Key].Modifiers.Any(m => m.Kind == "Support")))
			_foeBuffSupport = GUILayout.Toggle(_foeBuffSupport, (_foeBuffSupport ? "✓ " : "＋ ") + F("有同伴支援"), _foeBuffToggle);
		bool previous = GUI.enabled;
		GUI.enabled = previous && _foeEnabledBuffs.Count > 0;
		if (GUILayout.Button(F("恢复基础面板"), _track)) ResetFoeBuffs(profile);
		GUI.enabled = previous;
		GUILayout.Space(S(6f));
	}
}
