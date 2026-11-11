using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.UI.Extensions;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 使用游戏同一个 UIPolygon 组件、底图、颜色和 0–100 / 100–200 两层规则。
/// 在独立画布上渲染到纹理，交给 IMGUI 裁剪，滚动时不会盖住其他控件。
/// 不加载/激活决斗场景，不克隆带游戏行为的面板，不分发原游戏美术资源。
/// </summary>
internal sealed class GalleryDuelRadar : IDisposable
{
	private GameObject _root;
	private Camera _camera;
	private UIPolygon _normal, _overflow;
	private RenderTexture _target;
	private Texture2D _ownedTexture;
	private Sprite _ownedSprite;
	private int[] _values;
	private int _createdFrame;
	private bool _failed;
	internal string Error { get; private set; }
	/// <summary>画上了游戏的六维图底图（深色）；读不到时只有六边形，背景透明。</summary>
	internal bool HasBackground { get; private set; }

	// 游戏从上方逆时针：体力、轻功、刀剑、暗器、拳掌、内力。
	internal static readonly int[] Order = { 0, 2, 4, 5, 3, 1 };

	internal Texture Render(int[] six)
	{
		if (_failed) return null;
		try
		{
			if (_root == null) Create();
			// UIPolygon.Update 用 RectTransform 初始化尺寸。等原组件自己的 Update 执行。
			if (Time.frameCount <= _createdFrame) return null;
			if (_values == null || !_values.SequenceEqual(six))
			{
				_normal.VerticesDistances = Distances(six, 0);
				_overflow.VerticesDistances = Distances(six, 100);
				_normal.SetAllDirty();
				_overflow.SetAllDirty();
				Canvas.ForceUpdateCanvases();
				_camera.Render();
				_values = (int[])six.Clone();
			}
			return _target;
		}
		catch (Exception e)
		{
			_failed = true;
			Error = e.Message;
			FileLogger.LogWarning("[鉴赏] 游戏六维图读取失败: " + e);
			Dispose();
			return null;
		}
	}

	internal static float[] Distances(int[] six, int offset)
	{
		var values = new float[7];
		for (int i = 0; i < 6; i++) values[i] = Math.Max(0f, Math.Min(1f, (six[Order[i]] - offset) / 100f));
		if (values.Any(v => v > 0f))
			for (int i = 0; i < 6; i++) values[i] = Math.Max(0.02f, values[i]);
		values[6] = values[0];
		return values;
	}

	private void Create()
	{
		Sprite background = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s.name == "pic_stat_chart_6");
		if (background == null)
		{
			// 游戏更新后资源位置或内容变了：不画底图，只画两层六边形，不让整张图失效
			try
			{
				background = LoadGameBackground();
			}
			catch (Exception e)
			{
				FileLogger.LogWarning("[鉴赏] 六维图底图读不到，只画六边形: " + e.Message);
			}
		}
		HasBackground = background != null;
		_root = new GameObject("LOMAssistant_GalleryRadar") { hideFlags = HideFlags.HideAndDontSave, layer = 31 };
		_root.transform.position = new Vector3(100000f, 100000f, 0f);
		GameObject canvasObject = Child("Canvas", _root.transform, typeof(RectTransform), typeof(Canvas));
		Canvas canvas = canvasObject.GetComponent<Canvas>();
		canvas.renderMode = RenderMode.WorldSpace;
		((RectTransform)canvas.transform).sizeDelta = new Vector2(440f, 500f);
		Image image = Child("pic_stat_chart_6", canvas.transform, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image)).GetComponent<Image>();
		image.sprite = background;
		image.enabled = background != null;
		image.raycastTarget = false;
		image.rectTransform.sizeDelta = new Vector2(440f, 500f);
		_normal = Polygon(canvas.transform, "RadarValue", new Color(0.8470588f, 0.8352941f, 0.8352941f, 1f));
		_overflow = Polygon(canvas.transform, "RadarValue2", new Color(0.2311321f, 0.6376427f, 1f, 1f));
		_target = new RenderTexture(880, 1000, 16, RenderTextureFormat.ARGB32)
		{
			hideFlags = HideFlags.HideAndDontSave, antiAliasing = 4, name = "GalleryDuelRadar",
		};
		_target.Create();
		_camera = Child("Camera", _root.transform, typeof(Camera)).GetComponent<Camera>();
		_camera.transform.localPosition = new Vector3(0f, 0f, -10f);
		_camera.orthographic = true;
		_camera.orthographicSize = 250f;
		_camera.aspect = 440f / 500f;
		_camera.nearClipPlane = 0.1f;
		_camera.farClipPlane = 20f;
		_camera.cullingMask = 1 << 31;
		_camera.clearFlags = CameraClearFlags.SolidColor;
		_camera.backgroundColor = Color.clear;
		_camera.targetTexture = _target;
		_camera.enabled = false;
		canvas.worldCamera = _camera;
		_createdFrame = Time.frameCount;
	}

	private static GameObject Child(string name, Transform parent, params Type[] components)
	{
		var child = new GameObject(name, components) { hideFlags = HideFlags.HideAndDontSave, layer = 31 };
		child.transform.SetParent(parent, false);
		return child;
	}

	private static UIPolygon Polygon(Transform parent, string name, Color color)
	{
		var polygon = Child(name, parent, typeof(RectTransform), typeof(CanvasRenderer), typeof(UIPolygon)).GetComponent<UIPolygon>();
		polygon.rectTransform.sizeDelta = new Vector2(220f, 220f);
		polygon.fill = true;
		polygon.thickness = 1f;
		polygon.sides = 6;
		polygon.rotation = 270f;
		polygon.VerticesDistances = new float[7];
		polygon.color = color;
		polygon.raycastTarget = false;
		return polygon;
	}

	private Sprite LoadGameBackground()
	{
		_ownedTexture = LoadGameTexture(GalleryDuelCatalog.Instance.Radar);
		_ownedSprite = Sprite.Create(_ownedTexture, new Rect(0, 0, _ownedTexture.width, _ownedTexture.height), new Vector2(0.5f, 0.5f));
		return _ownedSprite;
	}

	/// <summary>只读取已校验的游戏纹理；六维图和四维滑条共用，不把游戏图片打包进 Mod。</summary>
	internal static Texture2D LoadGameTexture(DuelRadarResource source)
	{
		if (source == null || Path.GetFileName(source.File) != source.File || source.Size <= 0 || source.Size > 16 * 1024 * 1024)
			throw new InvalidDataException("Invalid radar resource metadata");
		byte[] bytes;
		using (var reader = new BinaryReader(File.OpenRead(Path.Combine(Application.dataPath, source.File))))
		{
			if (source.Offset < 0 || source.Offset > reader.BaseStream.Length - source.Size)
				throw new InvalidDataException("Radar resource is outside the game file");
			reader.BaseStream.Position = source.Offset;
			bytes = reader.ReadBytes(source.Size);
		}
		using (SHA256 hash = SHA256.Create())
			if (BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant() != source.Sha256)
				throw new InvalidDataException("Game radar resource changed; regenerate gallery catalog");
		var texture = new Texture2D(source.Width, source.Height, (TextureFormat)source.Format, false)
		{
			name = source.Name, hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear,
			wrapMode = TextureWrapMode.Clamp,
		};
		try
		{
			texture.LoadRawTextureData(bytes);
			texture.Apply(false, true);
			return texture;
		}
		catch
		{
			UnityEngine.Object.Destroy(texture);
			throw;
		}
	}

	public void Dispose()
	{
		if (_camera != null) _camera.targetTexture = null;
		if (_root != null) UnityEngine.Object.Destroy(_root);
		if (_target != null) { _target.Release(); UnityEngine.Object.Destroy(_target); }
		if (_ownedSprite != null) UnityEngine.Object.Destroy(_ownedSprite);
		if (_ownedTexture != null) UnityEngine.Object.Destroy(_ownedTexture);
		_root = null;
		_target = null;
		_ownedSprite = null;
		_ownedTexture = null;
		_values = null;
	}
}
