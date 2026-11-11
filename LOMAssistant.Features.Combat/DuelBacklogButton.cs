using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using Mortal.Combat;
using Mortal.Core;
using UnityEngine;
using UnityEngine.UI;

namespace LOMAssistant.Features.Combat;

/// <summary>
/// 决斗里打开决斗回想的按钮，摆在快进按钮正上方（图标和下面几个等距），跟着它们滑进滑出。
/// 复制决斗自带的「状态」菱形按钮（太极图标 icon_status，框和大小和旁边的一致），图标换成游戏回想按钮的 icon_log。
/// icon_log 在决斗场景里不一定载入：进养成地图时（那里的回想按钮用它）存一份 PNG，之后的启动直接从盘上读。
/// 两处都拿不到时（装好后还没进过地图就先打了决斗）画一个简单按钮。
/// </summary>
internal static class DuelBacklogButton
{
	private const string Icon = "icon_log", IconHover = "icon_log_hover";
	private const string Status = "icon_status", StatusHover = "icon_status_hover";
	private static readonly Dictionary<string, Sprite> Icons = new Dictionary<string, Sprite>();
	private static GameObject _button;
	private static Toggle _toggle;
	private static RectTransform _speed;
	private static Transform _speedIcon, _statusIcon, _icon;

	private static string CacheFile(string name) => Path.Combine(Path.GetDirectoryName(typeof(DuelBacklogButton).Assembly.Location) ?? ".", "StoryReview", name + ".png");

	/// <summary>养成地图建好时：回想图标已经载入，存到盘上（只存一次）。</summary>
	internal static void SaveIcons()
	{
		foreach (string name in new[] { Icon, IconHover })
		{
			string file = CacheFile(name);
			if (File.Exists(file))
			{
				continue;
			}
			Sprite sprite = Loaded(name);
			if (sprite == null)
			{
				continue;
			}
			Directory.CreateDirectory(Path.GetDirectoryName(file));
			File.WriteAllBytes(file, Readable(sprite).EncodeToPNG());
			FileLogger.Log("[决斗回想] 存下回想图标 " + name);
		}
	}

	private static Sprite Loaded(string name) => Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s != null && s.name == name);

	/// <summary>图集里的图不能直接读：画到临时 RenderTexture 上再读回来，只取这张图那一块。</summary>
	private static Texture2D Readable(Sprite sprite)
	{
		Texture source = sprite.texture;
		Rect r = sprite.textureRect;
		RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
		RenderTexture old = RenderTexture.active;
		Graphics.Blit(source, rt);
		RenderTexture.active = rt;
		var copy = new Texture2D((int)r.width, (int)r.height, TextureFormat.RGBA32, false);
		copy.ReadPixels(new Rect(r.x, source.height - r.y - r.height, r.width, r.height), 0, 0);
		copy.Apply();
		RenderTexture.active = old;
		RenderTexture.ReleaseTemporary(rt);
		return copy;
	}

	/// <summary>回想图标：场景里载入的那张；没有就读存下的 PNG。</summary>
	private static Sprite Find(string name, Sprite like)
	{
		if (Icons.TryGetValue(name, out Sprite cached) && cached != null)
		{
			return cached;
		}
		Sprite sprite = Loaded(name);
		if (sprite == null && File.Exists(CacheFile(name)))
		{
			var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
			if (tex.LoadImage(File.ReadAllBytes(CacheFile(name))))
			{
				tex.name = name;
				// 和被换掉的图同样的每单位像素，大小才和旁边按钮的图标一致
				sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), like != null ? like.pixelsPerUnit : 100f);
				sprite.name = name;
				Object.DontDestroyOnLoad(tex);
			}
		}
		if (sprite != null)
		{
			Icons[name] = sprite;
		}
		return sprite;
	}

	/// <summary>战斗界面建好时，复制「状态」按钮放到快进按钮上方，图标换成回想的。</summary>
	internal static void Create(CombatUIManager ui)
	{
		_button = null;
		_toggle = null;
		_speed = Traverse.Create(ui).Field("_speedButton").GetValue<MovePanel>()?.transform as RectTransform;
		var status = Traverse.Create(ui).Field("_statusButton").GetValue<MovePanel>()?.transform as RectTransform;
		if (_speed == null || status == null)
		{
			return;
		}
		GameObject copy = Object.Instantiate(status.gameObject, status.parent, false);
		copy.name = "LOMAssistant_DuelBacklog";
		// 位置由这里每帧跟着快进按钮摆，不用游戏的滑动
		foreach (MovePanel move in copy.GetComponentsInChildren<MovePanel>(true))
		{
			Object.DestroyImmediate(move);
		}
		int swapped = 0;
		foreach (Image image in copy.GetComponentsInChildren<Image>(true))
		{
			string name = image.sprite != null ? image.sprite.name : "";
			string to = name == Status ? Icon : name == StatusHover ? IconHover : null;
			Sprite icon = to != null ? Find(to, image.sprite) : null;
			if (icon != null)
			{
				image.sprite = icon;
				swapped++;
			}
			else if (to == null && name.StartsWith("T_", System.StringComparison.Ordinal))
			{
				// 手柄键位提示（状态按钮的是 X/A 键）：复制出来的按钮没有手柄键
				image.gameObject.SetActive(false);
			}
		}
		if (swapped == 0)
		{
			// 没有回想图标：不放一个看着像「状态」的按钮，改画简单按钮
			FileLogger.Log("[决斗回想] 没换上回想图标，改画简单按钮。状态按钮里的图：" + string.Join("；",
				copy.GetComponentsInChildren<Graphic>(true).Select(g => g.name + "=" + g.GetType().Name + ":" + ((g as Image)?.sprite != null ? ((Image)g).sprite.name : (g as RawImage)?.texture != null ? ((RawImage)g).texture.name : "-"))));
			Object.Destroy(copy);
			return;
		}
		_toggle = copy.GetComponentInChildren<Toggle>(true);
		if (_toggle != null)
		{
			// 复制出来的按钮只开决斗回想（游戏在代码里接的监听不会跟着复制）
			_toggle.onValueChanged = new Toggle.ToggleEvent();
			_toggle.SetIsOnWithoutNotify(false);
			_toggle.onValueChanged.AddListener(on =>
			{
				if (on != DuelDialogueHistoryPanel.IsOpen)
				{
					DuelDialogueHistoryPanel.Toggle();
				}
			});
		}
		_button = copy;
		_speedIcon = IconOf(_speed, "icon_2x");
		_statusIcon = IconOf(status, Status);
		_icon = IconOf(copy.transform, Icon);
		Follow(true);
	}

	/// <summary>每帧跟着快进按钮：位置、显示与否。</summary>
	internal static void Follow(bool enabled)
	{
		if (_button == null || _speed == null)
		{
			return;
		}
		bool show = enabled && _speed.gameObject.activeInHierarchy;
		if (_button.activeSelf != show)
		{
			_button.SetActive(show);
		}
		// 各按钮里图标相对外框的位置不一样，按图标对齐：回想图标放在「快进图标 +（快进图标 − 状态图标）」，
		// 和下面几个等距。每帧算，按钮滑进滑出时一起动。
		if (_speedIcon != null && _statusIcon != null && _icon != null)
		{
			Vector3 target = 2f * _speedIcon.position - _statusIcon.position;
			_button.transform.position += target - _icon.position;
		}
	}

	/// <summary>按钮里显示这张图标的那一层（不是悬停时换上的那张）。</summary>
	private static Transform IconOf(Transform button, string sprite) =>
		button.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.sprite != null && i.sprite.name == sprite)?.transform;

	/// <summary>回想关上时把按钮还原成没按下的样子。</summary>
	internal static void Reset()
	{
		_toggle?.SetIsOnWithoutNotify(false);
	}

	/// <summary>没有回想图标时：快进按钮正上方、同样大小的位置，画简单按钮用。</summary>
	internal static Rect? FallbackRect()
	{
		if (_button != null || _speed == null || !_speed.gameObject.activeInHierarchy)
		{
			return null;
		}
		Canvas canvas = _speed.GetComponentInParent<Canvas>()?.rootCanvas;
		if (canvas == null)
		{
			return null;
		}
		Camera cam = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
		var corners = new Vector3[4];
		_speed.GetWorldCorners(corners);
		Vector2 min = RectTransformUtility.WorldToScreenPoint(cam, corners[0]);
		Vector2 max = RectTransformUtility.WorldToScreenPoint(cam, corners[2]);
		float w = max.x - min.x, h = max.y - min.y;
		return new Rect(min.x, Screen.height - max.y - h - 8f, w, h);
	}
}
