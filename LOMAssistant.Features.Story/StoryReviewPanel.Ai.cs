using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LOMAssistant.Config;
using LOMAssistant.Features.Ai;
using Mortal.Core;
using UnityEngine;

namespace LOMAssistant.Features.Story;

/// <summary>
/// 「AI」页：AI 攻略问答。没配好服务商时先显示设置（服务商、Key、地址、模型、测试连接），配好后显示对话。
/// AI 的回答本来就是界面语言写的，画的时候加 Verbatim 记号，不再查表翻译。
/// </summary>
public partial class StoryReviewPanel
{
	private const int AiTab = 7;

	private string _aiInput = "";
	private Vector2 _aiScroll, _aiSetupScroll;
	private bool _aiSetup;
	private string _aiTestStatus = "";
	// 设置页里正在改的主动陪玩 token 上限（数字输入框的文字）
	private string _aiCapText;
	private bool _aiTesting;
	private int _aiShownCount;



	/// <summary>AI 页热键（默认 F3）：打开 AI 页、光标放进输入框；已经在 AI 页就关掉窗口。</summary>
	public static void ToggleAi()
	{
		StoryReviewPanel panel = Instance;
		if (panel == null)
		{
			return;
		}
		if (panel._visible && panel._tab == AiTab && !panel._showChangelog)
		{
			panel._visible = false;
			return;
		}
		panel._visible = true;
		panel._tab = AiTab;
		panel._showChangelog = false;
		panel._aiHistory = false;
		panel._aiFocusInput = true;
		panel._aiTyping = true;
	}

	// 从画面上的小伙伴点过来时带着的那句话：先摆在对话末尾给玩家看，玩家真的追问了才记进这段对话；
	// 没追问就换人、离开这一页，它不留下（主动陪玩说过的话不进对话记录）
	private AiCompanion.Remark _aiCarry;
	private string _aiCarryPersona = "";
	private int _aiCarryFrame;

	/// <summary>
	/// 从画面上的小伙伴点过来：打开 AI 页、切到陪玩的这位。remark 是它刚说的那句话，先摆在对话末尾，
	/// 玩家追问时才连同当时的事一起记进对话、告诉 AI；question 不空就直接发问。
	/// </summary>
	internal static void OpenAiFor(string personaId, AiCompanion.Remark remark, string question)
	{
		StoryReviewPanel panel = Instance;
		if (panel == null)
		{
			return;
		}
		panel._visible = true;
		panel._tab = AiTab;
		panel._showChangelog = false;
		panel._aiHistory = false;
		panel._aiFocusInput = true;
		panel._aiTyping = true;
		if (AiChat.Busy)
		{
			return;
		}
		AiChat.PersonaId = personaId ?? AiPersona.AssistantId;
		panel._aiCarry = remark != null && remark.Text.Length > 0 ? remark : null;
		panel._aiCarryPersona = AiChat.PersonaId;
		panel._aiCarryFrame = Time.frameCount;
		panel._aiScroll.y = float.MaxValue;
		if (!string.IsNullOrEmpty(question) && AiChat.Configured)
		{
			panel.AskAi(TextConverter.ForDisplay(question));
		}
	}

	// 发问。从小伙伴那儿带过来的那句话这时才进对话：显示在问题前面，并连同当时的事告诉 AI
	private void AskAi(string question)
	{
		if (AiChat.Busy)
		{
			return;
		}
		if (_aiCarry != null && _aiCarryPersona == AiChat.PersonaId && (question ?? "").Trim().Length > 0)
		{
			AiSession session = AiChat.Session;
			session.Shown.Add(new AiChat.Entry { Kind = AiChat.Kind.Assistant, Text = _aiCarry.Text, Mood = _aiCarry.Mood });
			string note = AiCompanionPrompts.FollowUp(_aiCarry.Text, _aiCarry.Card);
			session.Note = session.Note.Length > 0 ? session.Note + "；" + note : note;
		}
		_aiCarry = null;
		AiChat.Ask(question);
	}

	private void DrawAi(Rect r)
	{
		// 带过来的那句话只在这次打开时摆着：离开过这一页、换了人就不再摆
		if (_aiCarry != null && ((Time.frameCount - _aiSeenFrame > 2 && Time.frameCount - _aiCarryFrame > 2) || _aiCarryPersona != AiChat.PersonaId))
		{
			_aiCarry = null;
		}
		_aiSeenFrame = Time.frameCount;
		// AI 查资料要用整理好的剧情数据：打开这一页就开始整理（和路线图一样）
		if (!StoryAtlas.Ready && !StoryAtlas.Building && UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "Title")
		{
			StoryAtlas.Start();
		}
		// 还没配好服务商就先进设置页，并留在那儿，等玩家自己点「开始提问」或「返回」
		// （以前只看配没配好：Key 刚填上第一个字就算配好了，设置页当场换成对话页）
		if (!AiChat.Configured)
		{
			_aiSetup = true;
		}
		if (_aiSetup)
		{
			DrawAiSetup(r);
		}
		else
		{
			DrawAiChat(r);
		}
	}

	// 点了哪家服务商：等下一次排版再换。换了以后「API Key」那一行可能多出来或消失，
	// 同一次事件里中途换，排版时和处理点击时的控件数对不上
	private int _aiPickedProvider = -1;

	/// <summary>
	/// AI 设置。左边一列选服务商，中间填连接要用的（Key、地址、模型）并测试连接，右边是主动陪玩和调试记录；
	/// 窗口窄、右边放不下时，右边那栏接在中间那栏下面。
	/// </summary>
	private void DrawAiSetup(Rect r)
	{
		AiStyles();
		if (_aiPickedProvider >= 0 && Event.current.type == EventType.Layout)
		{
			// 每家各记各的 Key、改过的地址和模型名：换过去是那家上次填的，没填过就是默认值、Key 空着
			AiProviderStore.Switch(_aiPickedProvider);
			_aiTestStatus = "";
			_aiPickedProvider = -1;
			// 输入框的控件编号可能跟着变了，别让焦点落到另一个框里
			GUIUtility.keyboardControl = 0;
		}
		// 顶上固定一行：左上角「返回」（和别的子页一样）、标题。还没配好服务商时没有对话页可回，按钮点不了；
		// 按钮一直在：按条件显示的话，填 Key 填到一半它冒出来，后面输入框的控件编号跟着变，焦点会跳走
		GUI.enabled = AiChat.Configured;
		if (GUI.Button(new Rect(r.x, r.y, S(110), S(36)), "返回", _button))
		{
			_aiSetup = false;
		}
		GUI.enabled = true;
		GUI.Label(new Rect(r.x + S(126), r.y, r.width - S(126), S(36)), "AI 设置", Middle(_heading));
		GUILayout.BeginArea(new Rect(r.x, r.y + S(46), r.width, r.height - S(46)));
		_aiSetupScroll = GUILayout.BeginScrollView(_aiSetupScroll);
		GUILayout.Label("需要自己的 API Key。Key 只存在这台电脑的配置文件里，只发给下面选的服务商；导出诊断时会隐藏。每次提问，问题和 AI 查到的游戏剧情会发给服务商，按服务商的价格计费。", _muted);
		GUILayout.Space(S(10));

		// 服务商那一列按最长的名字定宽（译文长短不一）；剩下的宽度够就分两栏
		float gap = S(20), listWidth = S(240);
		foreach (LlmProvider p in LlmProvider.All)
		{
			listWidth = Mathf.Max(listWidth, TextWidth(p.Name, _aiListOff, 0f));
		}
		listWidth = Mathf.Min(listWidth, S(340));
		float rest = r.width - S(30) - listWidth - gap;
		bool wide = rest >= S(1150);
		float mainWidth = wide ? Mathf.Min(S(820), rest * 0.58f) : Mathf.Min(S(900), rest);
		float sideWidth = wide ? Mathf.Min(S(620), rest - mainWidth - gap) : mainWidth;

		GUILayout.BeginHorizontal();
		GUILayout.BeginVertical(GUILayout.Width(listWidth));
		GUILayout.Label("服务商", _heading);
		int current = ConfigManager.AiProvider.Value;
		for (int i = 0; i < LlmProvider.All.Length; i++)
		{
			if (GUILayout.Button(LlmProvider.All[i].Name, i == current ? _aiListOn : _aiListOff, GUILayout.Width(listWidth), Ctl()) && i != current)
			{
				_aiPickedProvider = i;
			}
		}
		GUILayout.EndVertical();
		GUILayout.Space(gap);
		GUILayout.BeginVertical(GUILayout.Width(mainWidth));
		DrawAiConnection(mainWidth);
		if (!wide)
		{
			GUILayout.Space(S(8));
			DrawAiExtras(sideWidth);
		}
		GUILayout.EndVertical();
		if (wide)
		{
			GUILayout.Space(gap);
			GUILayout.BeginVertical(GUILayout.Width(sideWidth));
			DrawAiExtras(sideWidth);
			GUILayout.EndVertical();
		}
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
		GUILayout.EndScrollView();
		GUILayout.EndArea();
	}

	// 中间一栏：连上服务商要填的几项，填完就地测试、开始提问
	private void DrawAiConnection(float width)
	{
		LlmProvider provider = AiChat.Provider;
		GUILayout.Label("连接", _heading);
		GUILayout.BeginVertical(_card, GUILayout.Width(width));
		float name = AiNameWidth("API Key", "API 地址", "问答模型", "深度模式模型");
		// 选好服务商后多数人只用填 Key，放最上面；地址和模型名已经填好
		if (!provider.NoKey)
		{
			GUILayout.BeginHorizontal();
			AiName("API Key", name);
			Assign(ConfigManager.AiApiKey, GUILayout.PasswordField(ConfigManager.AiApiKey.Value, '•', 300, _field, Ctl()));
			if (provider.KeysPage.Length > 0 && GUILayout.Button("去申请 Key", _button, GUILayout.Width(TextWidth("去申请 Key", _button, S(120))), Ctl()))
			{
				Application.OpenURL(provider.KeysPage);
			}
			GUILayout.EndHorizontal();
			GUILayout.Space(S(4));
		}
		GUILayout.BeginHorizontal();
		AiName("API 地址", name);
		Assign(ConfigManager.AiBaseUrl, GUILayout.TextField(ConfigManager.AiBaseUrl.Value, 200, _field, Ctl()));
		GUILayout.EndHorizontal();
		GUILayout.Space(S(4));
		GUILayout.BeginHorizontal();
		AiName("问答模型", name);
		Assign(ConfigManager.AiModel, GUILayout.TextField(ConfigManager.AiModel.Value, 100, _field, Ctl()));
		GUILayout.EndHorizontal();
		GUILayout.Space(S(4));
		GUILayout.BeginHorizontal();
		AiName("深度模式模型", name);
		Assign(ConfigManager.AiDeepModel, AiHinted(GUILayout.TextField(ConfigManager.AiDeepModel.Value, 100, _field, Ctl()), "与问答模型相同"));
		GUILayout.EndHorizontal();
		GUILayout.Space(S(4));
		GUILayout.Label("模型名是建议值，服务商出了新模型可以自己改。要选支持工具调用（function calling）的模型。", _muted);
		GUILayout.Space(S(10));

		GUILayout.BeginHorizontal();
		GUI.enabled = !_aiTesting && AiChat.Configured;
		if (GUILayout.Button(_aiTesting ? "正在测试…" : "测试连接", _button, GUILayout.Width(TextWidth("测试连接", _button, S(160))), Ctl()))
		{
			_aiTesting = true;
			_aiTestStatus = "";
			StartCoroutine(AiChat.Test(error =>
			{
				_aiTesting = false;
				_aiTestStatus = error.Length == 0 ? "<color=#2F6B3A>连上了</color>" : "<color=#A3302A>" + error + "</color>";
			}));
		}
		if (GUILayout.Button("开始提问", _primary, GUILayout.Width(TextWidth("开始提问", _primary, S(160))), Ctl()))
		{
			_aiSetup = false;
		}
		GUI.enabled = true;
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
		// 测试结果单独一行，报错长了能折行；没有结果时这一行不占高度（控件一直在，免得结果出来的那一次事件里控件数变了）
		GUILayout.Label(_aiTestStatus, _rich, _aiTestStatus.Length > 0 ? GUILayout.ExpandHeight(false) : GUILayout.Height(0f));
		GUILayout.EndVertical();
		GUILayout.Space(S(4));
		GUILayout.Label("回答方式、剧透程度、女主角的攻略提示在对话页上方直接选。", _muted);
	}

	// 右边一栏（窄窗口时接在连接下面）：主动陪玩用的模型和用量上限、调试记录
	private void DrawAiExtras(float width)
	{
		GUILayout.Label("主动陪玩", _heading);
		GUILayout.BeginVertical(_card, GUILayout.Width(width));
		float name = AiNameWidth("模型", "token 上限");
		GUILayout.BeginHorizontal();
		AiName("模型", name);
		Assign(ConfigManager.AiCompanionModel, AiHinted(GUILayout.TextField(ConfigManager.AiCompanionModel.Value, 100, _field, Ctl()), "与问答模型相同"));
		GUILayout.EndHorizontal();
		GUILayout.Space(S(4));
		GUILayout.BeginHorizontal();
		AiName("token 上限", name);
		_aiCapText ??= ConfigManager.AiCompanionTokenCap.Value.ToString();
		string capText = GUILayout.TextField(_aiCapText, 9, _field, GUILayout.Width(S(160)), Ctl());
		if (capText != _aiCapText)
		{
			_aiCapText = Regex.Replace(capText, @"\D", "");
			ConfigManager.AiCompanionTokenCap.Value = int.TryParse(_aiCapText, out int capValue) ? capValue : 0;
		}
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
		GUILayout.Space(S(4));
		GUILayout.Label("上限按每次启动游戏计算，达到后本次游戏不再发言；填 0 为不限。", _muted);
		GUILayout.Label("开关、发言频率和陪玩人选在对话页上方直接选。", _muted);
		GUILayout.EndVertical();

		GUILayout.Space(S(8));
		GUILayout.Label("自定义人设", _heading);
		GUILayout.BeginVertical(_card, GUILayout.Width(width));
		GUILayout.Label("可添加自行编写的人设和图片，也可替换内置人设的图片。放置方法见文件夹内的说明文件。", _muted);
		GUILayout.Space(S(4));
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("打开文件夹", _button, GUILayout.Width(TextWidth("打开文件夹", _button, S(160))), Ctl()))
		{
			OpenAiPersonaFolder();
		}
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
		GUILayout.EndVertical();

		GUILayout.Space(S(8));
		GUILayout.Label("调试记录", _heading);
		GUILayout.BeginVertical(_card, GUILayout.Width(width));
		bool log = ConfigManager.AiDebugLog.Value;
		GUILayout.BeginHorizontal();
		if (GUILayout.Button("不记", log ? _button : _primary, GUILayout.Width(TextWidth("不记", _button, S(110))), Ctl()) && log)
		{
			ConfigManager.AiDebugLog.Value = false;
		}
		if (GUILayout.Button("记下发给 AI 的内容", log ? _primary : _button, GUILayout.Width(TextWidth("记下发给 AI 的内容", _button, S(110))), Ctl()) && !log)
		{
			ConfigManager.AiDebugLog.Value = true;
		}
		GUILayout.FlexibleSpace();
		GUILayout.EndHorizontal();
		GUILayout.Space(S(4));
		GUILayout.Label("写在 Mod 文件夹的 logs/ai_debug.txt，不含 API Key。", _muted);
		// 测试题：开发用，发布包里没有题目文件，玩家看不到；手动放了 config\LOMAssistant_AI\eval.json 才显示。很费 token，点两下才跑
		int evalCount = Time.frameCount % 60 == 0 || _aiEvalCount < 0 ? (_aiEvalCount = AiEval.Count) : _aiEvalCount;
		if (evalCount > 0)
		{
			GUILayout.Space(S(8));
			GUILayout.BeginHorizontal();
			GUI.enabled = AiChat.Configured && !AiChat.Busy && !AiEval.Running;
			bool asked = Time.realtimeSinceStartup - _aiEvalAsked < 3f;
			if (GUILayout.Button(asked ? "确定要跑吗？" : string.Format("跑测试题（{0} 题）", evalCount), asked ? _primary : _button, GUILayout.Width(S(220)), Ctl()))
			{
				if (asked)
				{
					_aiEvalAsked = -100f;
					AiEval.Start();
				}
				else
				{
					_aiEvalAsked = Time.realtimeSinceStartup;
				}
			}
			// 「打开结果」一直占着位置，没有结果时点不了
			GUI.enabled = AiEval.HasReport && !AiEval.Running;
			if (GUILayout.Button("打开结果", _button, GUILayout.Width(S(140)), Ctl()))
			{
				Application.OpenURL("file:///" + AiEval.ReportPath.Replace('\\', '/'));
			}
			GUI.enabled = true;
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			GUILayout.Label(asked ? "每题都要查资料，一次会用掉几十万 token" : AiEval.Status ?? "", _muted);
		}
		GUILayout.EndVertical();
	}

	// 一张卡片里各行的名字一样宽（按最长的那个量，译文长短不一），后面的输入框才对得齐
	private float AiNameWidth(params string[] names)
	{
		GUIStyle line = Middle(_label);
		float width = S(96);
		foreach (string name in names)
		{
			width = Mathf.Max(width, TextWidth(name, line, 0f) + S(6));
		}
		return width;
	}

	private void AiName(string text, float width)
	{
		GUILayout.Label(text, Middle(_label), GUILayout.Width(width), Ctl());
	}

	// 输入框空着时，在框里用淡墨字写上留空按什么算（接在刚画的输入框后面调用）
	private string AiHinted(string typed, string hint)
	{
		if (typed.Length == 0 && Event.current.type == EventType.Repaint)
		{
			Rect box = GUILayoutUtility.GetLastRect();
			GUI.Label(new Rect(box.x + _field.padding.left + 1f, box.y, box.width - _field.padding.horizontal, box.height), hint, Middle(_muted));
		}
		return typed;
	}

	private static void Assign(ConfigElement<string> element, string value)
	{
		if (value != element.Value)
		{
			element.Value = value;
		}
	}

	// 能选的人设：攻略助手 + 玩家见过的女主角（读过她的对白）+ 玩家自建的人设；每隔几秒重算一次
	private List<AiPersona> _aiPersonas = new List<AiPersona>(), _aiMet = new List<AiPersona>();
	private float _aiPersonasAt = -100f;

	private List<AiPersona> MetPersonas()
	{
		// 剧情数据还没整理好时见没见过算不出来：先只有自建的，一秒看一次
		if (Time.realtimeSinceStartup - _aiPersonasAt > (StoryAtlas.Ready ? 5f : 1f))
		{
			_aiPersonasAt = Time.realtimeSinceStartup;
			if (StoryAtlas.Ready)
			{
				_aiMet = AiPersona.All.Where(p => p.Met()).ToList();
			}
			_aiPersonas = _aiMet.Concat(AiCustom.Personas).ToList();
		}
		return _aiPersonas;
	}

	// 自定义人设的文件夹：建好、写上说明，再打开
	private void OpenAiPersonaFolder()
	{
		try
		{
			var shown = MetPersonas().ToList();
			if (AiCompanion.Persona != null)
			{
				shown.Add(AiCompanion.Persona);
			}
			string dir = AiCustom.Prepare(shown);
			System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
		}
		catch (System.Exception ex)
		{
			FileLogger.LogWarning("[AI] 打开自定义人设文件夹失败: " + ex.Message);
			Notice("打不开这个文件夹", true);
		}
	}

	// 立绘：Addressables 里 Images/Characters/文件夹/表情.png；没有这个表情就用默认的
	private static readonly Dictionary<string, UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<Sprite>> AiPortraits =
		new Dictionary<string, UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationHandle<Sprite>>();

	internal static Sprite AiPortrait(AiPersona p, string mood)
	{
		foreach (string m in new[] { mood, p.Moods[0] })
		{
			if (string.IsNullOrEmpty(m))
			{
				continue;
			}
			string path = "Assets/__Project/Images/Characters/" + p.Folder + "/" + m + ".png";
			try
			{
				if (!AiPortraits.TryGetValue(path, out var handle))
				{
					handle = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<Sprite>(path);
					AiPortraits[path] = handle;
				}
				if (!handle.IsDone)
				{
					return null;
				}
				if (handle.Status == UnityEngine.ResourceManagement.AsyncOperations.AsyncOperationStatus.Succeeded && handle.Result != null)
				{
					return handle.Result;
				}
			}
			catch (System.Exception ex)
			{
				FileLogger.Log("[AI] 读立绘失败 " + path + ": " + ex.Message);
			}
		}
		return null;
	}

	// 立绘的头在哪（0～1，左上为原点）：头顶 = 第一行不透明像素，头的中线 = 头顶往下约 70 像素那一行不透明部分的中点。
	// 在游戏实际载入的图上量（同一个人在解包资源里可能有好几套画布），量一次记住
	private static readonly Dictionary<Sprite, Vector2> HeadAnchors = new Dictionary<Sprite, Vector2>();

	internal static Vector2 HeadAnchor(Sprite sprite, string name)
	{
		if (HeadAnchors.TryGetValue(sprite, out Vector2 cached))
		{
			return cached;
		}
		var anchor = new Vector2(0.5f, 0f);
		RenderTexture previous = RenderTexture.active;
		RenderTexture rt = null;
		Texture2D read = null;
		try
		{
			// 立绘贴图不可读：缩小画到临时渲染纹理上再读回来
			Texture2D tex = sprite.texture;
			Rect tr = sprite.textureRect;
			int w = 200, h = Mathf.Max(1, Mathf.RoundToInt(w * tr.height / tr.width));
			rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
			Graphics.Blit(tex, rt, new Vector2(tr.width / tex.width, tr.height / tex.height), new Vector2(tr.x / tex.width, tr.y / tex.height));
			RenderTexture.active = rt;
			read = new Texture2D(w, h, TextureFormat.RGBA32, false);
			read.ReadPixels(new Rect(0, 0, w, h), 0, 0);
			Color32[] px = read.GetPixels32();
			// 读回来的 y 从下往上；从最上一行往下找第一行有不透明像素的
			int top = -1;
			for (int y = h - 1; y >= 0 && top < 0; y--)
			{
				for (int x = 0; x < w; x++)
				{
					if (px[y * w + x].a > 40)
					{
						top = y;
						break;
					}
				}
			}
			if (top >= 0)
			{
				int row = Mathf.Max(0, top - Mathf.RoundToInt(70f * w / tr.width));
				int minX = w, maxX = -1;
				for (int x = 0; x < w; x++)
				{
					if (px[row * w + x].a > 40)
					{
						minX = Mathf.Min(minX, x);
						maxX = Mathf.Max(maxX, x);
					}
				}
				anchor = new Vector2(maxX >= 0 ? (minX + maxX + 1) / 2f / w : 0.5f, 1f - (top + 1f) / h);
			}
			FileLogger.Log($"[AI] 立绘 {name}：{tr.width}x{tr.height}，头顶 {anchor.y:0.000}，头中线 {anchor.x:0.000}");
		}
		catch (System.Exception ex)
		{
			FileLogger.Log("[AI] 量立绘失败: " + ex.Message);
		}
		finally
		{
			RenderTexture.active = previous;
			if (rt != null)
			{
				RenderTexture.ReleaseTemporary(rt);
			}
			if (read != null)
			{
				Object.Destroy(read);
			}
		}
		return HeadAnchors[sprite] = anchor;
	}

	// 立绘：所有人按同一个像素比例画（游戏里的立绘都是同一个比例，只是画布留白不同），头顶对齐这一栏顶端、头居中。头约 280 像素宽，画成栏宽的一半
	private void DrawAiPortrait(Rect r, AiPersona p)
	{
		// 玩家自己放了图（给内置人设换图，或自建人设）：整张放进这一栏，靠上居中，不裁；小图最多放大到两倍
		Texture2D own = AiCustom.Image(p, AiChat.Mood, false);
		if (own != null)
		{
			if (Event.current.type == EventType.Repaint)
			{
				float fit = Mathf.Min(2f, Mathf.Min(r.width / own.width, (r.height - S(10)) / own.height));
				float w = Mathf.Round(own.width * fit), h = Mathf.Round(own.height * fit);
				GUI.DrawTexture(new Rect(Mathf.Round(r.x + (r.width - w) / 2f), Mathf.Round(r.y + S(10)), w, h), own);
			}
			return;
		}
		if (p.Custom)
		{
			// 没放图的自建人设：一方朱砂印，写名字的第一个字
			if (Event.current.type == EventType.Repaint)
			{
				GUI.Label(new Rect(Mathf.Round(r.x + (r.width - S(160)) / 2f), Mathf.Round(r.y + S(40)), S(160), S(160)), TextConverter.Verbatim + p.Initial, _aiBigSeal);
			}
			return;
		}
		Sprite sprite = AiPortrait(p, AiChat.Mood);
		if (sprite == null || Event.current.type != EventType.Repaint)
		{
			return;
		}
		Rect tex = sprite.textureRect;
		Vector2 head = HeadAnchor(sprite, p.Folder + "/" + (AiChat.Mood.Length > 0 ? AiChat.Mood : p.Moods[0]));
		float drawW = tex.width * (r.width * 0.5f / 280f), drawH = drawW * tex.height / tex.width;
		float x = r.width * 0.5f - head.x * drawW, y = S(10) - head.y * drawH;
		var uv = new Rect(tex.x / sprite.texture.width, tex.y / sprite.texture.height, tex.width / sprite.texture.width, tex.height / sprite.texture.height);
		GUI.BeginGroup(r);
		GUI.DrawTextureWithTexCoords(new Rect(x, y, drawW, drawH), sprite.texture, uv);
		GUI.EndGroup();
	}

	private bool _aiHistory;
	private bool _aiFocusInput;
	// 玩家点了输入框、还没点别处：这期间输入框一直拿着键盘焦点
	private bool _aiTyping;
	// 流式回答显示到多长了（变长就滚到底）
	private int _aiPartialShown;
	// 测试题有几题（隔一阵重读一次题目文件）；-1 = 还没读
	private int _aiEvalCount = -1;
	private float _aiEvalAsked = -100f;
	// 输入框的控件编号（画完输入框时记下），回车判断在画它之前，要靠它认
	private int _aiInputId;

	// 空对话里的预设问题（玩家最常问的几类）
	private static readonly string[] AssistantStarters =
	{
		"这里应该怎么选？", "下一段主线之前，做什么养成比较好？", "现在能触发哪些支线和小剧情？",
		"现在优先做什么？有哪些我没看过的事件可以触发？", "这个选项会改什么旗标，后面有什么呼应？",
	};
	private static readonly string[] HeroineStarters =
	{
		"这里应该怎么选？", "怎样才能和你走得更近？", "你最近在忙些什么？", "接下来有什么事件能增加你的好感？", "你觉得我这个人怎么样？",
	};
	// 自建人设没有好感、结缘可问
	private static readonly string[] CustomStarters = { "这里应该怎么选？", "你最近在忙些什么？", "你觉得我这个人怎么样？" };
	private float _aiRefocusLogged = -100f;
	// 自己画的光标：闪烁从光标最后一次移动算起，打字、移动时一直亮着
	private int _aiCaretIndex = -1;
	private float _aiCaretMoved;

	// Unity 自带的竖线光标时有时无，输入框真有键盘焦点时自己画一条（自带的在画输入框时设成透明）
	private void DrawAiCaret(Rect box)
	{
		if (_aiInputId == 0 || GUIUtility.keyboardControl != _aiInputId || !string.IsNullOrEmpty(Input.compositionString))
		{
			return;
		}
		var editor = GUIUtility.GetStateObject(typeof(TextEditor), _aiInputId) as TextEditor;
		if (editor == null || editor.cursorIndex != editor.selectIndex)
		{
			return;
		}
		int index = Mathf.Clamp(editor.cursorIndex, 0, _aiInput.Length);
		if (index != _aiCaretIndex)
		{
			_aiCaretIndex = index;
			_aiCaretMoved = Time.realtimeSinceStartup;
		}
		if (Mathf.Repeat(Time.realtimeSinceStartup - _aiCaretMoved, 1f) >= 0.5f)
		{
			return;
		}
		Vector2 at = _aiField.GetCursorPixelPosition(box, new GUIContent(_aiInput), index) - editor.scrollOffset;
		float height = _aiField.lineHeight;
		if (at.y < box.y || at.y + height > box.yMax)
		{
			return;
		}
		Fill(new Rect(at.x, at.y, Mathf.Max(1f, Mathf.Round(S(1))), height), Ink);
	}
	private int _aiSeenFrame;

	/// <summary>窗口关了、换到别的页、点了窗口外面：不再算正在输入（每次 OnGUI 开头调用）。</summary>
	private void AiTypingGuard()
	{
		Event e = Event.current;
		if (!_visible || Time.frameCount - _aiSeenFrame > 2 || (e.rawType == EventType.MouseDown && !_windowRect.Contains(e.mousePosition)))
		{
			_aiTyping = false;
		}
	}
	private float _aiDeleteAsked = -100f;
	// 刚导出的对话文件名，顶上那行提示几秒
	private float _aiExportedAt = -100f;
	private string _aiExportedName = "";

	private static Steamworks.Callback<Steamworks.GamepadTextInputDismissed_t> _keyboardDone;

	// 在 Steam Deck 上或 Steam 大屏幕模式里时，打开 Steam 的全屏键盘；打开了返回 true
	private bool SteamKeyboard(string existing)
	{
		try
		{
			if (!Steamworks.SteamAPI.IsSteamRunning() || !(Steamworks.SteamUtils.IsSteamRunningOnSteamDeck() || Steamworks.SteamUtils.IsSteamInBigPictureMode()))
			{
				return false;
			}
			_keyboardDone ??= Steamworks.Callback<Steamworks.GamepadTextInputDismissed_t>.Create(done =>
			{
				if (!done.m_bSubmitted)
				{
					return;
				}
				uint length = Steamworks.SteamUtils.GetEnteredGamepadTextLength();
				if (Steamworks.SteamUtils.GetEnteredGamepadTextInput(out string text, length + 1) && Instance != null)
				{
					Instance._aiInput = "";
					Instance.AskAi(text);
				}
			});
			return Steamworks.SteamUtils.ShowGamepadTextInput(Steamworks.EGamepadTextInputMode.k_EGamepadTextInputModeNormal,
				Steamworks.EGamepadTextInputLineMode.k_EGamepadTextInputLineModeMultipleLines, TextConverter.ForDisplay("问 AI"), 500, existing ?? "");
		}
		catch (System.Exception ex)
		{
			FileLogger.Log("[AI] 打不开 Steam 屏幕键盘: " + ex.Message);
			return false;
		}
	}
	private Vector2 _aiHistoryScroll;

	// 这位的全部对话：点一段切过去接着聊
	/// <summary>一排「名字 + 几个选项」：选中的标红，悬停看每项的说明；从 x 往右排，排完 x 移到末尾。返回点了哪项（没点就是 current）。</summary>
	private int ChoiceStrip(ref float x, float y, float h, string label, string[] choices, int current, string[] tips)
	{
		float lw = TextWidth(label, Middle(_muted), 0f);
		GUI.Label(new Rect(x, y, lw, h), label, Middle(_muted));
		x += lw + S(4);
		int picked = current;
		for (int i = 0; i < choices.Length; i++)
		{
			var b = new Rect(x, y, TextWidth(choices[i], _aiSmallButton, S(64)), h);
			if (GUI.Button(b, choices[i], i == current ? _primary : _aiSmallButton))
			{
				picked = i;
			}
			Hover(b, tips[i]);
			x = b.xMax + S(2);
		}
		return picked;
	}

	// 主动陪玩那一排：关、安静、正常、话多；陪玩的是谁（和上面聊天选的人设分开，点按钮换成当前这位）；用了多少 token
	private void DrawAiCompanionStrip(Rect r, AiPersona current)
	{
		float x = r.x;
		bool supported = AiCompanion.Supported;
		GUI.enabled = supported;
		int level = supported && ConfigManager.AiCompanion.Value ? AiCompanion.Level + 1 : 0;
		int picked = ChoiceStrip(ref x, r.y, r.height, "主动陪玩", new[] { "关", "安静", "正常", "话多" }, level, new[]
		{
			"不在游戏画面上显示",
			"选定的人设常驻游戏画面，仅在结局、战斗胜负、自身好感变化、限时剧情将到期时发言",
			"另在做出有后果的选择、掷骰、剧情段落结束时发言",
			"另在普通选择、数值变化时发言，token 用量约为「正常」的两倍",
		});
		GUI.enabled = true;
		x += S(24);
		if (!supported)
		{
			GUI.Label(new Rect(x, r.y, r.xMax - x, r.height), "需要游戏 1.0.6001 及以上版本", Middle(_muted));
			return;
		}
		if (picked != level)
		{
			if (picked == 0)
			{
				ConfigManager.AiCompanion.Value = false;
			}
			else
			{
				ConfigManager.AiCompanionLevel.Value = picked - 1;
				if (!ConfigManager.AiCompanion.Value)
				{
					ConfigManager.AiCompanion.Value = true;
					// 这句话写在这一排后面：窗口顶上那条提示夹在页签和版本号之间，放不下这么长
					_aiCompanionNoticeUntil = Time.realtimeSinceStartup + 10f;
				}
			}
		}
		AiPersona who = AiCompanion.Persona;
		string label = string.Format("陪玩人选：{0}", TextConverter.ForDisplay(who != null ? who.Name : "攻略助手"));
		float lw = TextWidth(label, Middle(_muted), 0f);
		GUI.Label(new Rect(x, r.y, lw, r.height), label, Middle(_muted));
		x += lw + S(4);
		if ((current?.Id ?? "") != (who?.Id ?? ""))
		{
			var swap = new Rect(x, r.y, TextWidth("换成当前人设", _aiSmallButton, S(64)), r.height);
			if (GUI.Button(swap, "换成当前人设", _aiSmallButton))
			{
				ConfigManager.AiCompanionPersona.Value = current?.Id ?? "";
			}
			Hover(swap, "由上方选中的人设陪玩");
			x = swap.xMax + S(12);
		}
		if (ConfigManager.AiCompanion.Value && Time.realtimeSinceStartup < _aiCompanionNoticeUntil)
		{
			GUI.Label(new Rect(x, r.y, r.xMax - x, r.height), "主动陪玩已开启：每次发言会将最近的对白和事件发送给服务商", Middle(_small));
		}
		else if (AiCompanion.TokensUsed > 0)
		{
			GUI.Label(new Rect(x, r.y, r.xMax - x, r.height), string.Format(AiCompanion.Capped ? "陪玩已用 {0} token（已达上限）" : "陪玩已用 {0} token", AiCompanion.TokensUsed), Middle(_muted));
		}
	}

	// 刚开启主动陪玩：这一排后面写一阵「会把对白发给服务商」
	private float _aiCompanionNoticeUntil = -100f;

	private void DrawAiHistory(Rect r)
	{
		List<AiSession> sessions = AiSessions.Of(AiChat.PersonaId);
		AiSession current = AiChat.Session;
		GUILayout.BeginArea(r);
		_aiHistoryScroll = GUILayout.BeginScrollView(_aiHistoryScroll);
		if (sessions.Count == 0)
		{
			GUILayout.Label("还没有保存的对话。", _muted);
		}
		foreach (AiSession s in sessions)
		{
			int asked = s.Shown.Count(e => e.Kind == AiChat.Kind.User);
			string label = s.Updated.ToString("MM-dd HH:mm") + "　" + TextConverter.Verbatim + Escape(s.Title) + "　" + string.Format("（{0} 问）", asked);
			if (GUILayout.Button(label, s == current ? _primary : _button, Ctl()) && !AiChat.Busy)
			{
				AiSessions.Select(s);
				_aiHistory = false;
			}
		}
		GUILayout.EndScrollView();
		GUILayout.EndArea();
	}

	// AI 页自己的样式：气泡、名牌、折叠的查资料行。跟着窗口缩放、深浅色重建
	private GUIStyle _aiUser, _aiBot, _aiCard, _aiName, _aiFold, _aiSmallButton, _aiPlate, _aiTabText, _aiTabTextOn, _aiField, _aiHint, _aiHintBox, _aiHintTitle, _aiHintText, _aiListOff, _aiListOn, _aiBigSeal;
	private float _aiStyleScale = -1f;
	private bool _aiStyleDark;
	private float _aiStyleOpacity = -1f;

	private void AiStyles()
	{
		if (_aiUser != null && _aiStyleScale == _scale && _aiStyleDark == _dark && _aiStyleOpacity == _opacity)
		{
			return;
		}
		_aiStyleScale = _scale;
		_aiStyleDark = _dark;
		_aiStyleOpacity = _opacity;
		Color Faded(Color c) => new Color(c.r, c.g, c.b, c.a * _opacity);
		GUIStyle Bubble(Color fill, Color edge)
		{
			var s = new GUIStyle(_rich) { wordWrap = true };
			s.normal.background = Framed(Faded(fill), edge);
			s.border = new RectOffset(2, 2, 2, 2);
			s.padding = new RectOffset((int)S(14), (int)S(14), (int)S(10), (int)S(10));
			s.margin = new RectOffset(0, 0, (int)S(6), (int)S(6));
			return s;
		}
		_aiUser = Bubble(PaperDeep, new Color(Edge.r, Edge.g, Edge.b, 0.6f));
		_aiBot = Bubble(PaperLight, new Color(Edge.r, Edge.g, Edge.b, 0.4f));
		_aiCard = Bubble(PaperLight, Cinnabar);
		// 攻略提示：比气泡淡一层的底、细边，字用淡墨色
		_aiHintBox = Bubble(Paper, new Color(Edge.r, Edge.g, Edge.b, 0.35f));
		_aiHintBox.margin = new RectOffset((int)S(18), 0, (int)S(2), (int)S(8));
		_aiHintTitle = new GUIStyle(_rich) { wordWrap = false, fontSize = Mathf.RoundToInt(_rich.fontSize * 0.9f) };
		_aiHintTitle.normal.textColor = InkMuted;
		_aiHintText = new GUIStyle(_rich) { wordWrap = true };
		_aiHintText.normal.textColor = InkMuted;
		_aiName = new GUIStyle(_rich) { fontStyle = FontStyle.Bold, wordWrap = false };
		_aiName.normal.textColor = Cinnabar;
		_aiName.margin = new RectOffset((int)S(4), 0, (int)S(8), 0);
		_aiFold = new GUIStyle(_muted) { wordWrap = false };
		_aiFold.hover.textColor = Ink;
		_aiFold.margin = new RectOffset((int)S(6), 0, (int)S(2), (int)S(2));
		_aiSmallButton = new GUIStyle(_button) { fontSize = Mathf.RoundToInt(15 * _scale) };
		// 设置页左边的服务商列表：一行一家，名字靠左对齐（长短不一，居中的话参差不齐）
		_aiListOff = new GUIStyle(_button) { alignment = TextAnchor.MiddleLeft };
		_aiListOff.padding = new RectOffset((int)S(14), (int)S(10), (int)S(4), (int)S(4));
		_aiListOn = new GUIStyle(_primary) { alignment = TextAnchor.MiddleLeft };
		_aiListOn.padding = new RectOffset((int)S(14), (int)S(10), (int)S(4), (int)S(4));
		// 人设按钮上的名字：只有字，不带按钮底（底由按钮自己画，名字压在头像右边）
		_aiTabText = new GUIStyle(_rich) { wordWrap = false, alignment = TextAnchor.MiddleLeft, fontSize = _button.fontSize };
		_aiTabText.normal.textColor = Ink;
		// 输入框：多行，从左上角开始写（设置页的单行输入框是竖直居中的，打的字会跑到框中间）；提示文字用同一个字号、同一个位置
		_aiField = new GUIStyle(_field) { alignment = TextAnchor.UpperLeft, wordWrap = true };
		_aiField.padding = new RectOffset((int)S(12), (int)S(12), (int)S(10), (int)S(10));
		_aiHint = new GUIStyle(_aiField);
		_aiHint.normal.background = null;
		_aiHint.normal.textColor = InkMuted;
		_aiTabTextOn = new GUIStyle(_aiTabText);
		_aiTabTextOn.normal.textColor = OnCinnabar;
		_aiPlate = new GUIStyle(_seal) { fontSize = Mathf.RoundToInt(20 * _scale) };
		_aiPlate.normal.background = Framed(Cinnabar, new Color(Cinnabar.r * 0.7f, Cinnabar.g * 0.7f, Cinnabar.b * 0.7f, 1f));
		_aiPlate.border = new RectOffset(2, 2, 2, 2);
		// 没放图的自建人设：左栏一方大朱砂印，写名字的第一个字
		_aiBigSeal = new GUIStyle(_aiPlate) { fontSize = Mathf.RoundToInt(84 * _scale), clipping = TextClipping.Clip };
	}

	// 气泡宽度跟着字走：最长一行有多宽就多宽，超过上限才折行
	private static float BubbleWidth(string text, GUIStyle style, float max)
	{
		var single = new GUIStyle(style) { wordWrap = false };
		return Mathf.Min(max, single.CalcSize(new GUIContent(text)).x + 2f);
	}

	// Unity 自带的折行只在空格处断：一整段中文算一个词，放不下就整段挪到下一行（「·」「1.」单独留一行），
	// 硬断时行尾的字还会露出半截。这里按字宽自己折行：中文每个字前后都能断，英文按单词断，「，。」」等不放行首
	private static readonly Dictionary<string, string> _wrapCache = new Dictionary<string, string>();
	private static readonly Dictionary<(int, int, bool, char), float> _charWidth = new Dictionary<(int, int, bool, char), float>();
	private static readonly GUIStyle _measure = new GUIStyle { wordWrap = false, richText = true };

	internal static string Wrap(string rich, GUIStyle style, float width)
	{
		if (width <= 0f || string.IsNullOrEmpty(rich))
		{
			return rich;
		}
		string key = style.fontSize + "|" + (int)width + "|" + rich;
		if (_wrapCache.TryGetValue(key, out string done))
		{
			return done;
		}
		if (_wrapCache.Count > 300)
		{
			_wrapCache.Clear();
		}
		_measure.font = style.font;
		_measure.fontSize = style.fontSize;
		_measure.fontStyle = style.fontStyle;
		var sb = new StringBuilder(rich.Length + 32);
		var open = new List<string>();
		string[] lines = rich.Split('\n');
		for (int li = 0; li < lines.Length; li++)
		{
			if (li > 0)
			{
				sb.Append('\n');
			}
			string line = lines[li];
			float x = 0f;
			int i = 0;
			while (i < line.Length)
			{
				// 一个单位：前面挂着的富文本标签 + 一个汉字，或一个英文单词，或一个空格；后面跟着的不能放行首的标点也算进来
				var unit = new StringBuilder();
				var tagsAfter = new List<string>(open);
				float w = 0f;
				int start = i;
				void TakeTags()
				{
					while (i < line.Length && line[i] == '<')
					{
						int end = line.IndexOf('>', i);
						if (end < 0)
						{
							break;
						}
						string tag = line.Substring(i, end - i + 1);
						unit.Append(tag);
						if (tag.StartsWith("</"))
						{
							if (tagsAfter.Count > 0)
							{
								tagsAfter.RemoveAt(tagsAfter.Count - 1);
							}
						}
						else
						{
							tagsAfter.Add(tag);
						}
						i = end + 1;
					}
				}
				float Take()
				{
					char c = line[i++];
					unit.Append(c);
					return CharWidth(c, tagsAfter.Exists(t => t == "<b>"));
				}
				TakeTags();
				if (i >= line.Length)
				{
					sb.Append(unit);
					open = tagsAfter;
					break;
				}
				char first = line[i];
				bool space = first == ' ';
				if (space || first > 0x2E7F)
				{
					w += Take();
				}
				else
				{
					while (i < line.Length && line[i] != ' ' && line[i] <= 0x2E7F && line[i] != '<')
					{
						w += Take();
					}
				}
				while (true)
				{
					int save = i;
					var saveTags = new List<string>(tagsAfter);
					int saveLen = unit.Length;
					TakeTags();
					if (i < line.Length && NoLineStart.IndexOf(line[i]) >= 0)
					{
						w += Take();
						continue;
					}
					i = save;
					tagsAfter = saveTags;
					unit.Length = saveLen;
					break;
				}
				if (x > 0f && x + w > width)
				{
					for (int k = open.Count - 1; k >= 0; k--)
					{
						sb.Append("</").Append(TagName(open[k])).Append('>');
					}
					sb.Append('\n');
					foreach (string t in open)
					{
						sb.Append(t);
					}
					x = 0f;
					if (space)
					{
						open = tagsAfter;
						continue;
					}
				}
				sb.Append(unit);
				x += w;
				open = tagsAfter;
				if (i == start)
				{
					sb.Append(line[i++]);
				}
			}
		}
		string result = sb.ToString();
		_wrapCache[key] = result;
		return result;
	}

	private static string TagName(string tag)
	{
		string name = tag.Substring(1, tag.Length - 2);
		int cut = name.IndexOfAny(new[] { '=', ' ' });
		return cut >= 0 ? name.Substring(0, cut) : name;
	}

	private static float CharWidth(char c, bool bold)
	{
		var key = (_measure.font != null ? _measure.font.GetInstanceID() : 0, _measure.fontSize, bold, c);
		if (_charWidth.TryGetValue(key, out float w))
		{
			return w;
		}
		string open = bold ? "<b>" : "", close = bold ? "</b>" : "";
		// 单个字量出来的宽度含首尾空白，夹在两个字中间量更准；空格单独量会是 0
		w = _measure.CalcSize(new GUIContent(open + "国" + c + "国" + close)).x - _measure.CalcSize(new GUIContent(open + "国国" + close)).x;
		_charWidth[key] = w = Mathf.Max(0f, w);
		return w;
	}

	private static void DrawSprite(Rect r, Sprite sprite)
	{
		if (sprite == null || Event.current.type != EventType.Repaint)
		{
			return;
		}
		Rect t = sprite.textureRect;
		GUI.DrawTextureWithTexCoords(r, sprite.texture, new Rect(t.x / sprite.texture.width, t.y / sprite.texture.height, t.width / sprite.texture.width, t.height / sprite.texture.height));
	}

	private static void DrawFace(Rect r, AiPersona p)
	{
		Sprite sprite = AiPortrait(p, p.Moods[0]);
		if (sprite == null || Event.current.type != EventType.Repaint)
		{
			return;
		}
		Rect t = sprite.textureRect;
		float w = sprite.texture.width, h = sprite.texture.height, side = p.IconCrop[2] * t.width;
		// IconCrop 从左上量，贴图坐标从左下量
		var uv = new Rect((t.x + p.IconCrop[0] * t.width) / w, (t.yMax - p.IconCrop[1] * t.height - side) / h, side / w, side / h);
		GUI.DrawTextureWithTexCoords(r, sprite.texture, uv);
	}

	// 展开了的「查了 N 处资料」（按那一组的第一条记）
	private readonly HashSet<AiChat.Entry> _aiOpenLookups = new HashSet<AiChat.Entry>();

	/// <summary>
	/// 一条（或连着的一组查资料）消息排好的样子：折好行的文字、气泡宽度、上次画出来的高度。
	/// 消息多了以后每次刷新都重新折行、量宽度会越来越卡，所以按消息记住；看不见的消息只按记下的高度留空。
	/// </summary>
	private sealed class AiRow
	{
		public string Key; // 宽度、字号、展开状态等，变了就重排
		public string Said, Reply, Hint;
		public float SaidWidth, ReplyWidth, HintWidth;
		public float Height = -1f;
	}

	private readonly Dictionary<AiChat.Entry, AiRow> _aiRows = new Dictionary<AiChat.Entry, AiRow>();
	private GUIStyle _aiRowStyle;
	private readonly List<(AiRow Row, string Key, float Height)> _aiMeasured = new List<(AiRow, string, float)>();

	private void DrawAiChat(Rect r)
	{
		AiStyles();
		float h = S(44);
		// 第一排：人设。攻略助手是朱砂印「攻」，女主角用游戏里的心上人头像
		var personas = MetPersonas();
		{
			int current = AiChat.PersonaId.Length == 0 ? 0 : personas.FindIndex(p => p.Id == AiChat.PersonaId) + 1;
			float x = r.x;
			// 英文名比中文长，一排放不下时缩小间隔、按比例收窄每格（宽度本来留了余量，名字不会被截）
			float[] widths = new float[personas.Count + 1];
			for (int i = 0; i <= personas.Count; i++)
			{
				widths[i] = TextWidth(i == 0 ? "攻略助手" : personas[i - 1].Name, _button, S(60)) + h - S(6);
			}
			float gap = S(6), need = widths.Sum() + gap * personas.Count;
			if (need > r.width)
			{
				gap = S(2);
				float fit = (r.width - gap * personas.Count) / widths.Sum();
				for (int i = 0; i < widths.Length; i++)
				{
					widths[i] *= Mathf.Min(1f, fit);
				}
			}
			for (int i = 0; i <= personas.Count; i++)
			{
				string label = i == 0 ? "攻略助手" : personas[i - 1].Name;
				float bw = widths[i];
				var cell = new Rect(x, r.y, bw, h);
				if (GUI.Button(cell, "", i == current ? _primary : _button) && !AiChat.Busy)
				{
					AiChat.PersonaId = i == 0 ? AiPersona.AssistantId : personas[i - 1].Id;
					_aiHistory = false;
				}
				var icon = new Rect(cell.x + S(5), cell.y + S(5), h - S(10), h - S(10));
				if (i == 0)
				{
					if (Event.current.type == EventType.Repaint)
					{
						Fill(icon, i == current ? new Color(1f, 1f, 1f, 0.25f) : Cinnabar);
					}
					GUI.Label(icon, "攻", _seal);
				}
				else if (personas[i - 1].Custom)
				{
					// 自建人设：头像那张图上裁一块；没放图就和攻略助手一样画朱砂印，写名字的第一个字
					if (AiCustom.Icon(personas[i - 1], out Texture own, out Rect ownUv))
					{
						if (Event.current.type == EventType.Repaint)
						{
							GUI.DrawTextureWithTexCoords(icon, own, ownUv);
						}
					}
					else
					{
						if (Event.current.type == EventType.Repaint)
						{
							Fill(icon, i == current ? new Color(1f, 1f, 1f, 0.25f) : Cinnabar);
						}
						GUI.Label(icon, TextConverter.Verbatim + personas[i - 1].Initial, _seal);
					}
				}
				else if (personas[i - 1].IconCrop != null)
				{
					// 大师兄没有游戏里的心上人头像：从立绘上裁出脸
					DrawFace(icon, personas[i - 1]);
				}
				else
				{
					DrawSprite(icon, LOMAssistant.Features.Extras.SaveBrowser.LoverIcon(personas[i - 1].Lover));
				}
				GUI.Label(new Rect(icon.xMax + S(6), cell.y, cell.xMax - icon.xMax - S(10), h), label, i == current ? _aiTabTextOn : _aiTabText);
				x += bw + gap;
			}
			r = new Rect(r.x, r.y + h + S(10), r.width, r.height - h - S(10));
		}

		// 左栏：女主角立绘和名牌；攻略助手时是一张用法说明
		AiPersona persona = AiPersona.Find(AiChat.PersonaId);
		// 选着的自建人设没了（文件夹删了、改名了）：回到攻略助手，免得名字对不上人
		if (persona == null && AiChat.PersonaId.Length > 0 && !AiChat.Busy)
		{
			AiChat.PersonaId = AiPersona.AssistantId;
		}
		float pw = S(320);
		var left = new Rect(r.x, r.y, pw, r.height);
		if (persona != null)
		{
			DrawAiPortrait(new Rect(left.x, left.y, left.width, left.height - S(56)), persona);
			var plate = new Rect(left.x + S(30), left.yMax - S(50), left.width - S(60), S(44));
			GUI.Label(plate, TextConverter.ForDisplay(persona.Name), _aiPlate);
		}
		else
		{
			GUILayout.BeginArea(left, _aiBot);
			GUILayout.Label("<b>攻略助手</b>", _rich);
			GUILayout.Space(S(6));
			GUILayout.Label("会自己查游戏里的剧情资料再回答：任务怎么触发、选项通向哪里、结局怎么达成、现在该做什么。", _muted);
			GUILayout.Space(S(6));
			GUILayout.Label("选上面的女主角或大师兄，就换成那个人用自己的口吻和你说话。", _muted);
			GUILayout.Space(S(6));
			GUILayout.Label("要改数值、好感时会先给你一张卡片，你确认了才改。", _muted);
			GUILayout.EndArea();
		}
		r = new Rect(r.x + pw + S(16), r.y, r.width - pw - S(16), r.height);

		// 右上：模型名和小按钮
		float bh = S(32), bwid = S(110);
		LlmProvider provider = AiChat.Provider;
		bool exported = Time.realtimeSinceStartup - _aiExportedAt < 5f;
		GUI.Label(new Rect(r.x, r.y, r.width - bwid * 5 - S(36), bh), exported ? string.Format("已导出到桌面：{0}", _aiExportedName) : provider.Name + " · " + AiChat.Model + (AiChat.TokensUsed > 0 ? "　" + string.Format("这次开游戏用了 {0} token", AiChat.TokensUsed) + (AiChat.CachedUsed > 0 ? "　" + string.Format("缓存命中 {0}", AiChat.CachedUsed) : "") : ""), Middle(_muted));
		float bx = r.xMax - bwid * 5 - S(24);
		GUI.enabled = !AiChat.Busy;
		if (GUI.Button(new Rect(bx, r.y, bwid, bh), "新对话", _aiSmallButton))
		{
			AiChat.NewSession();
			_aiHistory = false;
		}
		if (GUI.Button(new Rect(bx + bwid + S(6), r.y, bwid, bh), "历史对话", _aiHistory ? _primary : _aiSmallButton))
		{
			_aiHistory = !_aiHistory;
		}
		GUI.enabled = !AiChat.Busy && AiChat.Shown.Count > 0;
		if (GUI.Button(new Rect(bx + (bwid + S(6)) * 2, r.y, bwid, bh), "导出对话", _aiSmallButton))
		{
			try
			{
				AiPersona who = AiPersona.Find(AiChat.PersonaId);
				string path = AiSessions.Export(AiChat.Session, who != null ? TextConverter.ForDisplay(who.Name) : TextConverter.ForDisplay("攻略助手"));
				_aiExportedName = System.IO.Path.GetFileName(path);
				_aiExportedAt = Time.realtimeSinceStartup;
				GUIUtility.systemCopyBuffer = path;
			}
			catch (System.Exception ex)
			{
				FileLogger.LogWarning("[AI] 导出对话失败: " + ex.Message);
			}
		}
		bool confirming = Time.realtimeSinceStartup - _aiDeleteAsked < 3f;
		if (GUI.Button(new Rect(bx + (bwid + S(6)) * 3, r.y, bwid, bh), confirming ? "确定删除？" : "删除这段", confirming ? _primary : _aiSmallButton))
		{
			if (confirming)
			{
				AiChat.DeleteSession();
				_aiDeleteAsked = -100f;
			}
			else
			{
				_aiDeleteAsked = Time.realtimeSinceStartup;
			}
		}
		GUI.enabled = true;
		if (GUI.Button(new Rect(bx + (bwid + S(6)) * 4, r.y, bwid, bh), "AI 设置", _aiSmallButton))
		{
			_aiSetup = true;
		}
		r = new Rect(r.x, r.y + bh + S(8), r.width, r.height - bh - S(8));

		// 第二行：回答方式、剧透程度、（女主角）攻略提示，几个选项都摆出来，一眼看得到、点一下就换
		float sx = r.x;
		int mode = AiChat.Fast ? 0 : AiChat.Deep ? 2 : 1;
		int pickedMode = ChoiceStrip(ref sx, r.y, bh, "回答方式", new[] { "极速", "标准", "深度" }, mode, new[]
		{
			"少查几次资料、提示词更短、不让模型多想：免费额度和本地小模型也能较快回答，答案更粗略",
			"查好资料再回答，速度和准确都适中",
			"换用更强的模型，回答前核对资料：更准，也更贵、更慢",
		});
		if (pickedMode != mode && !AiChat.Busy)
		{
			ConfigManager.AiFastMode.Value = pickedMode == 0;
			ConfigManager.AiDeepMode.Value = pickedMode == 2;
		}
		sx += S(24);
		int spoiler = Mathf.Clamp(ConfigManager.AiSpoiler.Value, 0, 2);
		ConfigManager.AiSpoiler.Value = ChoiceStrip(ref sx, r.y, bh, "剧透程度", new[] { "全说", "只说走过的", "只给方向" }, spoiler, new[]
		{
			"想知道什么说什么",
			"只说当前和走过的",
			"只给方向提示",
		});
		if (persona != null)
		{
			sx += S(24);
			ConfigManager.AiHints.Value = ChoiceStrip(ref sx, r.y, bh, "攻略提示", new[] { "问到时给", "不给" }, ConfigManager.AiHints.Value ? 0 : 1, new[]
			{
				"问到攻略时给",
				"只聊天，不给",
			}) == 0;
		}
		r = new Rect(r.x, r.y + bh + S(8), r.width, r.height - bh - S(8));
		// 第三行：主动陪玩（常驻游戏画面的小伙伴）。开关和话多程度并成一排，后面是陪玩的人选
		DrawAiCompanionStrip(new Rect(r.x, r.y, r.width, bh), persona);
		r = new Rect(r.x, r.y + bh + S(8), r.width, r.height - bh - S(8));
		if (_aiHistory)
		{
			DrawAiHistory(r);
			return;
		}

		float inputHeight = S(76);
		var area = new Rect(r.x, r.y, r.width, r.height - inputHeight - S(10));
		// 新消息来了滚到最下面
		if (AiChat.Shown.Count != _aiShownCount)
		{
			_aiShownCount = AiChat.Shown.Count;
			_aiScroll.y = float.MaxValue;
		}
		DragScroll("ai", area, ref _aiScroll);
		GUILayout.BeginArea(area);
		_aiScroll = GUILayout.BeginScrollView(_aiScroll);
		float bubbleMax = area.width * 0.78f;
		if (!StoryAtlas.Ready)
		{
			GUILayout.Label(StoryAtlas.Status, _muted);
		}
		var shown = AiChat.Shown;
		// 消息多的时候只画看得见的那些：先按记下的高度算出总高，滚到底（float.MaxValue）时换算成实际位置
		string baseKey = (int)area.width + "|" + _aiBot.fontSize + "|" + TextConverter.DisplayLanguage + "|" + (persona != null ? persona.Name : "");
		if (_aiRows.Count > shown.Count + 200)
		{
			var keep = new HashSet<AiChat.Entry>(shown);
			foreach (AiChat.Entry gone in _aiRows.Keys.Where(k => !keep.Contains(k)).ToList())
			{
				_aiRows.Remove(gone);
			}
		}
		float total = 0f;
		foreach (AiRow known in shown.Select(e => _aiRows.TryGetValue(e, out AiRow row) ? row : null).Where(row => row != null && row.Height > 0f))
		{
			total += known.Height;
		}
		float top = Mathf.Min(_aiScroll.y, Mathf.Max(0f, total - area.height));
		float y = 0f;
		_aiMeasured.Clear();
		for (int i = 0; i < shown.Count; i++)
		{
			AiChat.Entry entry = shown[i];
			int end = i;
			if (entry.Kind == AiChat.Kind.Activity)
			{
				while (end + 1 < shown.Count && shown[end + 1].Kind == AiChat.Kind.Activity)
				{
					end++;
				}
			}
			bool busyTail = end == shown.Count - 1 && AiChat.Busy;
			string key = baseKey + "|" + (end - i) + "|" + _aiOpenLookups.Contains(entry) + "|" + busyTail + "|" + entry.State;
			if (!_aiRows.TryGetValue(entry, out AiRow cached))
			{
				_aiRows[entry] = cached = new AiRow();
			}
			if (cached.Key != key)
			{
				cached.Key = key;
				cached.Height = -1f;
				cached.Said = cached.Reply = cached.Hint = null;
			}
			// 记过高度、又离看得见的地方超过一屏的，只留空
			if (cached.Height > 0f && (y + cached.Height < top - area.height || y > top + area.height * 2f))
			{
				GUILayout.Space(cached.Height);
				y += cached.Height;
				i = end;
				continue;
			}
			// 不用 GUIStyle.none：那样里面第一条、最后一条的外边距会算到组外面，留空时高度对不上
			_aiRowStyle ??= new GUIStyle();
			GUILayout.BeginVertical(_aiRowStyle);
			switch (entry.Kind)
			{
				case AiChat.Kind.User:
					// 玩家的话靠右
					if (cached.Said == null)
					{
						cached.Said = Wrap(TextConverter.Verbatim + Escape(entry.Text), _aiUser, bubbleMax - _aiUser.padding.horizontal);
						cached.SaidWidth = BubbleWidth(cached.Said, _aiUser, bubbleMax);
					}
					GUILayout.BeginHorizontal();
					GUILayout.FlexibleSpace();
					GUILayout.Label(cached.Said, _aiUser, GUILayout.Width(cached.SaidWidth));
					GUILayout.EndHorizontal();
					break;
				case AiChat.Kind.Assistant:
					GUILayout.Label(persona != null ? TextConverter.ForDisplay(persona.Name) : "攻略助手", _aiName);
				{
					// 女主角说的话放气泡；「【攻略提示】」往后是旁白写给玩家的，单独一个淡色框
					float hintWidth = area.width - S(30) - _aiHintBox.margin.left - _aiHintBox.padding.horizontal;
					if (cached.Reply == null)
					{
						int cut = persona != null ? entry.Text.IndexOf("【攻略提示】", System.StringComparison.Ordinal) : -1;
						string words = cut >= 0 ? entry.Text.Substring(0, cut).Trim() : entry.Text;
						string hint = cut >= 0 ? entry.Text.Substring(cut + "【攻略提示】".Length).Trim() : "";
						cached.Reply = words.Length > 0 ? Wrap(TextConverter.Verbatim + RichFromMarkdown(words), _aiBot, area.width - S(30) - _aiBot.padding.horizontal) : "";
						cached.ReplyWidth = words.Length > 0 ? BubbleWidth(cached.Reply, _aiBot, area.width - S(30)) : 0f;
						cached.Hint = hint.Length > 0 ? Wrap(TextConverter.Verbatim + RichFromMarkdown(hint), _aiHintText, hintWidth - _aiHintText.padding.horizontal) : "";
					}
					if (cached.Reply.Length > 0)
					{
						GUILayout.BeginHorizontal();
						GUILayout.Label(cached.Reply, _aiBot, GUILayout.Width(cached.ReplyWidth));
						GUILayout.FlexibleSpace();
						GUILayout.EndHorizontal();
					}
					if (cached.Hint.Length > 0)
					{
						GUILayout.BeginVertical(_aiHintBox, GUILayout.MaxWidth(area.width - S(30)));
						GUILayout.Label("<b>攻略提示</b>", _aiHintTitle);
						GUILayout.Label(cached.Hint, _aiHintText, GUILayout.Width(hintWidth));
						GUILayout.EndVertical();
					}
					break;
				}
				case AiChat.Kind.Activity:
				{
					// 连着的查资料并成一行「查了 N 处资料」，点开看明细
					bool open = _aiOpenLookups.Contains(entry);
					bool last = busyTail;
					if (!last && GUILayout.Button(string.Format(open ? "▾ 查了 {0} 处资料" : "▸ 查了 {0} 处资料", end - i + 1), _aiFold))
					{
						if (open)
						{
							_aiOpenLookups.Remove(entry);
						}
						else
						{
							_aiOpenLookups.Add(entry);
						}
					}
					if (open || last)
					{
						for (int k = i; k <= end; k++)
						{
							GUILayout.Label("　· " + shown[k].Text, _muted);
						}
					}
					i = end;
					break;
				}
				case AiChat.Kind.Proposal:
					GUILayout.BeginVertical(_aiCard, GUILayout.MaxWidth(bubbleMax));
					GUILayout.Label("<b>修改提议</b>", _aiName);
					if (entry.Text.Length > 0)
					{
						GUILayout.Label(TextConverter.Verbatim + Escape(entry.Text), _muted);
					}
					foreach (AiChat.Change c in entry.Changes)
					{
						GUILayout.Label("　" + c.Name + "：" + c.From + " → <b>" + c.To + "</b>", _rich);
					}
					if (entry.State == AiChat.ProposalState.Pending)
					{
						GUILayout.Space(S(4));
						GUILayout.BeginHorizontal();
						if (GUILayout.Button("确认修改", _primary, GUILayout.Width(S(140)), Ctl()))
						{
							AiChat.Resolve(AiChat.Session, entry, true);
						}
						if (GUILayout.Button("不改", _button, GUILayout.Width(S(100)), Ctl()))
						{
							AiChat.Resolve(AiChat.Session, entry, false);
						}
						GUILayout.FlexibleSpace();
						GUILayout.EndHorizontal();
					}
					else
					{
						GUILayout.Label(entry.State == AiChat.ProposalState.Applied ? "<color=#2F6B3A>已修改</color>" : entry.State == AiChat.ProposalState.Expired ? "已失效（重启游戏前的提议，没有修改）" : "没有修改", _rich);
					}
					GUILayout.EndVertical();
					break;
				default:
					GUILayout.Label("<color=#A3302A>" + Escape(entry.Text) + "</color>", _rich);
					break;
			}
			GUILayout.EndVertical();
			// 画完量一下高度；整轮画完再记，免得同一次刷新里前后两遍排得不一样
			if (Event.current.type == EventType.Repaint)
			{
				_aiMeasured.Add((cached, key, GUILayoutUtility.GetLastRect().height));
			}
			y += cached.Height > 0f ? cached.Height : 0f;
			i = end;
		}
		foreach (var (row, rowKey, height) in _aiMeasured)
		{
			if (row.Key == rowKey && height > 0f)
			{
				row.Height = height;
			}
		}
		_aiMeasured.Clear();
		// 从画面上的小伙伴点过来时带着的那句话：摆在末尾当话头，还不算这段对话里的
		if (_aiCarry != null && !AiChat.Busy)
		{
			GUILayout.Label("刚才在游戏画面上说的", _muted);
			string carried = Wrap(TextConverter.Verbatim + RichFromMarkdown(_aiCarry.Text), _aiBot, area.width - S(30) - _aiBot.padding.horizontal);
			GUILayout.BeginHorizontal();
			GUILayout.Label(carried, _aiBot, GUILayout.Width(BubbleWidth(carried, _aiBot, area.width - S(30))));
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
		}
		if (AiChat.Busy && AiChat.Partial.Length > 0)
		{
			// 一边收一边显示
			GUILayout.Label(persona != null ? TextConverter.ForDisplay(persona.Name) : "攻略助手", _aiName);
			GUILayout.BeginHorizontal();
			string partial = Wrap(TextConverter.Verbatim + RichFromMarkdown(AiChat.Partial.Replace("【攻略提示】", "\n攻略提示\n")), _aiBot, area.width - S(30) - _aiBot.padding.horizontal);
			GUILayout.Label(partial, _aiBot, GUILayout.Width(BubbleWidth(partial, _aiBot, area.width - S(30))));
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			if (Event.current.type == EventType.Repaint && AiChat.Partial.Length != _aiPartialShown)
			{
				_aiPartialShown = AiChat.Partial.Length;
				_aiScroll.y = float.MaxValue;
			}
		}
		else if (AiChat.Busy)
		{
			// 等回答时点点点轮着动
			string dots = new string('·', 1 + (int)(Time.realtimeSinceStartup * 2.5f) % 3);
			GUILayout.Label(string.Format("{0} 正在想{1}", persona != null ? TextConverter.ForDisplay(persona.Name) : "AI", dots), _aiName);
		}
		// 还没说过话：给几个可以直接点的问题（按界面语言发出去）
		if (shown.Count == 0 && !AiChat.Busy)
		{
			GUILayout.Space(S(12));
			GUILayout.Label("可以这样问：", _muted);
			foreach (string q in persona == null ? AssistantStarters : persona.Custom ? CustomStarters : HeroineStarters)
			{
				GUILayout.BeginHorizontal();
				// 宽度按界面语言量：按中文原文量，英文、韩文会被截掉
				if (GUILayout.Button(q, _button, GUILayout.Width(TextWidth(q, _button, 0f) + _button.padding.horizontal), Ctl()))
				{
					AskAi(TextConverter.ForDisplay(q));
				}
				GUILayout.FlexibleSpace();
				GUILayout.EndHorizontal();
				GUILayout.Space(S(6));
			}
		}
		GUILayout.EndScrollView();
		GUILayout.EndArea();

		// 输入框：回车发送，Shift+回车换行；输入法还在选字时回车交给输入法
		var box = new Rect(r.x, r.yMax - inputHeight, r.width - S(150), inputHeight);
		Event e = Event.current;
		bool send = false;
		// Steam Deck、大屏幕模式：点输入框弹 Steam 的屏幕键盘（IMGUI 的输入框不会自己弹），提交了就直接发问
		if (e.type == EventType.MouseDown && box.Contains(e.mousePosition) && !AiChat.Busy && SteamKeyboard(_aiInput))
		{
			e.Use();
		}
		else if (e.type == EventType.MouseDown && e.button == 0)
		{
			// 自己记「正在输入」：点了输入框就开，点窗口里别处就关（点窗口外、发送、换页、关窗口在别处关）
			_aiTyping = box.Contains(e.mousePosition);
			if (_aiTyping)
			{
				GUI.FocusWindow(WindowId);
			}
		}
		GUI.SetNextControlName("AiInput");
		// 回车发送：Unity 把回车分成两个事件（按键、换行字符），两个都吞掉，不然清空后的输入框里会多一个空行
		if (e.type == EventType.KeyDown && !e.shift && _aiInputId != 0 && GUIUtility.keyboardControl == _aiInputId && string.IsNullOrEmpty(Input.compositionString)
			&& (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter || e.character == '\n' || e.character == '\r'))
		{
			send = e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter;
			e.Use();
		}
		// 自带的光标藏起来（透明），只留 DrawAiCaret 画的那条，不然两条闪烁节奏不同，忽粗忽细
		Color caret = GUI.skin.settings.cursorColor;
		GUI.skin.settings.cursorColor = Color.clear;
		_aiInput = GUI.TextArea(box, _aiInput, 500, _aiField);
		GUI.skin.settings.cursorColor = caret;
		if (GUI.GetNameOfFocusedControl() == "AiInput")
		{
			_aiInputId = GUIUtility.keyboardControl;
		}
		// 焦点名要在画完输入框之后才查得到（之前查总是空的，会每帧重抢焦点、把光标放回开头）。
		// 正在输入时焦点被别的东西拿走（第一下点击被窗口激活吃掉、别处清了键盘焦点……），每帧抢回来
		if (_aiTyping && e.type == EventType.Layout && GUI.GetNameOfFocusedControl() != "AiInput")
		{
			if (Time.realtimeSinceStartup - _aiRefocusLogged > 5f)
			{
				_aiRefocusLogged = Time.realtimeSinceStartup;
				FileLogger.Log("[AI] 输入框焦点被拿走，抢回来（原焦点：" + GUI.GetNameOfFocusedControl() + "，keyboardControl=" + GUIUtility.keyboardControl + "，hotControl=" + GUIUtility.hotControl + "）");
			}
			GUI.FocusWindow(WindowId);
			GUI.FocusControl("AiInput");
		}
		bool focused = _aiTyping || (GUI.GetNameOfFocusedControl() == "AiInput" && GUIUtility.keyboardControl != 0);
		if (e.type == EventType.Repaint)
		{
			if (focused)
			{
				Frame(box, Cinnabar, 2);
			}
			if (!focused && _aiInput.Length == 0)
			{
				GUI.Label(box, "点这里输入问题，回车发送，Shift+回车换行", _aiHint);
			}
			DrawAiCaret(box);
		}
		var button = new Rect(r.xMax - S(140), r.yMax - inputHeight, S(140), inputHeight);
		if (AiChat.Busy)
		{
			if (GUI.Button(button, "停止", _button))
			{
				AiChat.Stop();
			}
		}
		else if (GUI.Button(button, "发送", _primary) || send)
		{
			string question = _aiInput.Trim();
			if (question.Length > 0)
			{
				_aiInput = "";
				AskAi(question);
			}
		}
	}

	private static string Escape(string text) => (text ?? "").Replace("<", "‹").Replace(">", "›");

	private static readonly Regex Bold = new Regex(@"\*\*(.+?)\*\*");
	private static readonly Regex Heading = new Regex(@"^#{1,6}\s*(.+)$", RegexOptions.Multiline);

	// 模型常用 Markdown：粗体、标题换成富文本粗体，列表符号照留，其余尖括号换掉免得被当成标签
	internal static string RichFromMarkdown(string text)
	{
		string t = Escape(text).Replace("\r", "");
		t = Heading.Replace(t, "<b>$1</b>");
		t = Bold.Replace(t, "<b>$1</b>");
		t = Regex.Replace(t, @"^\s*[-*]\s+", "· ", RegexOptions.Multiline);
		return t.Replace("`", "");
	}

	// 一个界面物体下面所有显示着的字（普通文字和 TextMeshPro），去掉富文本、转简体，连成一句
	private static string TextsUnder(Component root)
	{
		var parts = root.GetComponentsInChildren<UnityEngine.UI.Text>().Select(t => t.text)
			.Concat(root.GetComponentsInChildren<TMPro.TMP_Text>().Select(t => t.text))
			.Select(t => TextConverter.ToSimplified(RichText.Strip(t ?? "")).Replace("\n", " ").Trim())
			.Where(t => t.Length > 0).Distinct().ToArray();
		return string.Join(" ", parts);
	}

	/// <summary>回想（1.0.6001 起的 StoryLogData）里最后一条对白的说话人；没写说话人就是旁白。</summary>
	private static string LastSpeaker()
	{
		try
		{
			// 旧版游戏没有这份回想，当旁白
			string speaker = StoryLogCompat.LastSpeaker();
			if (!string.IsNullOrEmpty(speaker))
			{
				string name = Mortal.Core.LocalizationManager.Instance.LocaleResolver.GetString("Character/" + speaker);
				name = TextConverter.ToSimplified(RichText.Strip(name ?? "")).Trim();
				return name.Length > 0 && !name.StartsWith("Character/") ? name : "旁白";
			}
		}
		catch
		{
		}
		return "旁白";
	}

	/// <summary>屏幕上正显示的对白、选项、掷骰：直接读游戏界面上的字。</summary>
	internal static string OnScreen()
	{
		var sb = new StringBuilder();
		try
		{
			foreach (Fungus.SayDialog say in Object.FindObjectsOfType<Fungus.SayDialog>())
			{
				if (!say.isActiveAndEnabled)
				{
					continue;
				}
				string line = string.Join("：", say.GetComponentsInChildren<UnityEngine.UI.Text>()
					.Where(t => t.name == "NameText" || t.name == "StoryText").OrderBy(t => t.name == "StoryText")
					.Select(t => TextConverter.ToSimplified(RichText.Strip(t.text ?? "")).Replace("\n", " ").Trim()).Where(t => t.Length > 0).ToArray());
				if (line.Length > 0)
				{
					// 对话框里没有名字（有的对话框不显示名字）时，用回想最后一条对白记下的说话人补上，
					// 不然扮演女主角时会把别人的台词当成自己说的
					if (!line.Contains("："))
					{
						line = LastSpeaker() + "：" + line;
					}
					sb.Append("对白：").Append(line).Append('\n');
				}
			}
			foreach (Fungus.MenuDialog menu in Object.FindObjectsOfType<Fungus.MenuDialog>())
			{
				if (!menu.isActiveAndEnabled)
				{
					continue;
				}
				var options = menu.GetComponentsInChildren<UnityEngine.UI.Button>().Select(TextsUnder).Where(t => t.Length > 0).ToList();
				if (options.Count > 0)
				{
					sb.Append("正在选的选项：").Append(string.Join(" / ", options.Select(o => "「" + o + "」").ToArray())).Append('\n');
				}
			}
			foreach (Mortal.Story.DiceMenuDialog dice in Object.FindObjectsOfType<Mortal.Story.DiceMenuDialog>())
			{
				if (!dice.isActiveAndEnabled)
				{
					continue;
				}
				string header = TextConverter.ToSimplified(RichText.Strip(HarmonyLib.Traverse.Create(dice).Field("_header").GetValue<string>() ?? "")).Trim();
				var lines = dice.GetComponentsInChildren<Mortal.Story.DiceOption>().Select(TextsUnder).Where(t => t.Length > 0).ToList();
				sb.Append("正在掷骰").Append(header.Length > 0 ? "「" + header + "」" : "").Append("，各档：").Append(string.Join(" / ", lines.Select(o => "「" + o + "」").ToArray())).Append('\n');
			}
		}
		catch (System.Exception ex)
		{
			FileLogger.LogWarning("[AI] 读取屏幕内容失败: " + ex.Message);
		}
		if (sb.Length > 0 && (OptionInfoPatch.LastScriptKey ?? "").Length > 0)
		{
			sb.Append("这段剧情的剧本：").Append(OptionInfoPatch.LastScriptKey).Append("（用 script 查它，按选项文字对上分岔）\n");
		}
		return sb.ToString();
	}

	/// <summary>
	/// AI 提议的修改：按数值页的行找（名字、编号），找到的变成一项「当前 → 新值」，确认后用数值页同一个 Set 改。
	/// 「好感·某人」「某人的好感」找好感那一组；心上人（愛人）可以直接给女主角的名字。找不到的写进 missing 交还给 AI。
	/// </summary>
	internal static List<AiChat.Change> AiResolveChanges(Newtonsoft.Json.Linq.JArray items, List<string> missing)
	{
		var result = new List<AiChat.Change>();
		PlayerStatManagerData player = PlayerStatManagerData.Instance;
		if (player == null || items == null)
		{
			missing.Add("玩家还没读档进游戏");
			return result;
		}
		var sections = BuildStatSections(player);
		var rows = sections.SelectMany(s => s.Item2.Select(row => (Section: s.Item1, Row: row))).Where(x => x.Row.Set != null && x.Row.Get != null).ToList();
		foreach (Newtonsoft.Json.Linq.JToken item in items)
		{
			string name = TextConverter.ToSimplified(item?["name"]?.ToString() ?? "").Trim();
			string raw = item?["value"]?.ToString().Trim() ?? "";
			if (name.Length == 0)
			{
				continue;
			}
			string Clean(string t) => AiKnowledge.Clean(t).Replace(" ", "");
			string key = Regex.Replace(name.Replace(" ", ""), @"^(?:旗标|设置)[·:：]", "");
			// 好感：「好感·小梅」「小梅好感」「小梅的好感」
			string favorOf = Regex.Match(key, @"^(?:好感[·:：]?(.+)|(.+?)的?好感(?:度)?)$") is Match fm && fm.Success ? (fm.Groups[1].Success && fm.Groups[1].Value.Length > 0 ? fm.Groups[1].Value : fm.Groups[2].Value) : null;
			(string Section, StatRow Row) hit = default;
			if (favorOf != null)
			{
				string who = AiKnowledge.FindHeroine(favorOf) is StoryRomance.Heroine h ? StoryRomance.Name(h) : favorOf;
				hit = rows.FirstOrDefault(x => x.Section == "好感" && (Clean(x.Row.Name) == who || Clean(x.Row.Name).Contains(who) || who.Contains(Clean(x.Row.Name))));
			}
			bool lover = key == "心上人" || key == "爱人" || key == "结缘对象";
			if (hit.Row == null && lover)
			{
				hit = rows.FirstOrDefault(x => x.Row.Id == "stat:" + (int)GameStatType.愛人);
			}
			if (hit.Row == null)
			{
				hit = rows.FirstOrDefault(x => x.Row.Id == name || Clean(x.Row.Name) == key);
			}
			if (hit.Row == null)
			{
				// 旗标：数值页里旗标行的名字是编号（G00006），说明里才是「小竹告白」这样的名字
				hit = rows.FirstOrDefault(x => x.Section == "旗标" && (x.Row.Id == "flag:" + key || Clean(x.Row.Note ?? "") == key));
			}
			if (hit.Row == null)
			{
				var near = rows.Where(x => Clean(x.Row.Name).Contains(key)).Take(2).ToList();
				if (near.Count != 1)
				{
					near = rows.Where(x => x.Section == "旗标" && Clean(x.Row.Note ?? "").Contains(key)).Take(2).ToList();
				}
				hit = near.Count == 1 ? near[0] : default;
			}
			if (hit.Row == null)
			{
				// 不是数值，就看是不是咖啡伴侣的设置项
				AiChat.Change setting = AiSettingChange(key, raw, out string problem);
				if (setting != null)
				{
					result.Add(setting);
				}
				else
				{
					missing.Add(name + (problem ?? "（数值页和设置页里都没有这一项）"));
				}
				continue;
			}
			int value;
			if (!int.TryParse(raw, out value))
			{
				// 心上人可以给名字
				if (hit.Row.Id == "stat:" + (int)GameStatType.愛人 && AiKnowledge.FindHeroine(raw) is StoryRomance.Heroine target)
				{
					value = target.Lover;
				}
				else
				{
					missing.Add(name + "（新值「" + raw + "」不是数字）");
					continue;
				}
			}
			StatRow row = hit.Row;
			string label = AiKnowledge.Clean(row.Name) + (hit.Section == "旗标" && !string.IsNullOrEmpty(row.Note) ? "「" + AiKnowledge.Clean(row.Note) + "」" : "");
			System.Func<int, string> show = row.Id == "stat:" + (int)GameStatType.愛人
				? v => v + (StoryRomance.All.FirstOrDefault(h => h.Lover == v) is StoryRomance.Heroine hh ? "（" + StoryRomance.Name(hh) + "）" : "")
				: v => v.ToString();
			result.Add(new AiChat.Change
			{
				Name = (hit.Section == "好感" ? "好感·" : "") + label,
				From = show(row.Get()),
				To = show(value),
				Apply = () => row.Set(value),
			});
		}
		return result;
	}

	// AI 不能碰的设置：接入 AI 本身的（Key、地址、模型），免得把自己改坏
	private static bool AiCanTouch(SettingRow row) => !row.Element.Name.StartsWith("Ai", System.StringComparison.Ordinal)
		&& (row.Element.ElementType == typeof(bool) || row.Element.ElementType == typeof(int) || row.Element.ElementType == typeof(float));

	private static IEnumerable<(string Section, SettingRow Row)> AiSettings() =>
		BuildSettingSections().SelectMany(s => s.Item2.Where(AiCanTouch).Select(r => (s.Item1, r)));

	private static string SettingValueText(SettingRow row, object value)
	{
		if (value is bool b)
		{
			return b ? "开" : "关";
		}
		if (row.Choices != null && value is int i && i >= 0 && i < row.Choices.Length)
		{
			return row.Choices[i];
		}
		return System.Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
	}

	/// <summary>给 AI 的 settings 工具：咖啡伴侣设置页的设置项（名字、当前值、可选值或范围、说明）。</summary>
	internal static string AiSettingsText(string query)
	{
		string q = TextConverter.ToSimplified(query ?? "").Trim();
		var sb = new StringBuilder();
		string section = null;
		foreach (var (title, row) in AiSettings())
		{
			string desc = Describe(row.Element.Description);
			if (q.Length > 0 && !(row.Name + title + desc).Contains(q))
			{
				continue;
			}
			if (title != section)
			{
				section = title;
				sb.Append("【").Append(title).Append("】\n");
			}
			sb.Append("- ").Append(row.Name).Append("：现在 ").Append(SettingValueText(row, row.Element.BoxedValue));
			if (row.Element.ElementType == typeof(bool))
			{
				sb.Append("（开 / 关）");
			}
			else if (row.Choices != null)
			{
				sb.Append("（可选：").Append(string.Join(" / ", row.Choices)).Append("）");
			}
			else if (row.Speeds != null)
			{
				sb.Append("（可选：").Append(string.Join(" / ", row.Speeds.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray())).Append("）");
			}
			else if (row.Max > row.Min)
			{
				sb.Append("（").Append(row.Min).Append(" 到 ").Append(row.Max).Append("）");
			}
			sb.Append(row.Restart ? "，改后重启游戏生效" : "").Append("。").Append(desc).Append('\n');
		}
		return sb.Length > 0 ? sb.ToString() : "没有「" + query + "」相关的设置项。";
	}

	// 设置项的修改提议：按设置页上的名字找，新值按类型读（开关、几选一、数字）
	private static AiChat.Change AiSettingChange(string key, string raw, out string problem)
	{
		problem = null;
		string Clean(string t) => TextConverter.ToSimplified(t ?? "").Replace(" ", "");
		var all = AiSettings().ToList();
		var hit = all.FirstOrDefault(x => Clean(x.Row.Name) == key || x.Row.Element.Name == key);
		if (hit.Row == null)
		{
			var near = all.Where(x => Clean(x.Row.Name).Contains(key)).Take(2).ToList();
			hit = near.Count == 1 ? near[0] : default;
		}
		if (hit.Row == null)
		{
			return null;
		}
		SettingRow row = hit.Row;
		IConfigElement element = row.Element;
		string v = Clean(raw).ToLowerInvariant();
		object value = null;
		int choice = -1;
		if (element.ElementType == typeof(bool))
		{
			if (v == "开" || v == "打开" || v == "开启" || v == "true" || v == "1" || v == "是" || v == "on")
			{
				value = true;
			}
			else if (v == "关" || v == "关闭" || v == "关掉" || v == "false" || v == "0" || v == "否" || v == "off")
			{
				value = false;
			}
		}
		else if (row.Choices != null)
		{
			choice = System.Array.FindIndex(row.Choices, c => Clean(c) == Clean(raw));
			if (choice < 0 && int.TryParse(v, out int n) && n >= 0 && n < row.Choices.Length)
			{
				choice = n;
			}
			if (choice >= 0)
			{
				value = choice;
			}
		}
		else if (float.TryParse(v.TrimEnd('x', '倍', '%'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
		{
			if (row.Max > row.Min)
			{
				f = Mathf.Clamp(f, row.Min, row.Max);
			}
			value = element.ElementType == typeof(int) ? (object)Mathf.RoundToInt(f) : f;
		}
		if (value == null)
		{
			problem = "（设置项「" + row.Name + "」的新值「" + raw + "」看不懂）";
			return null;
		}
		object newValue = value;
		return new AiChat.Change
		{
			Name = "设置·" + row.Name + (row.Restart ? "（重启游戏后生效）" : ""),
			From = SettingValueText(row, element.BoxedValue),
			To = SettingValueText(row, newValue),
			Apply = () =>
			{
				if (row.Choose != null && choice >= 0)
				{
					row.Choose(choice);
				}
				else
				{
					element.BoxedValue = newValue;
				}
			},
		};
	}

	// 养成地图在哪：地图标题（游戏左上角那行）和地图上能去的场所。唐门、崆峒等地图的场所不一样，AI 靠这个分清人在哪
	internal static string MapLocation()
	{
		var sb = new StringBuilder();
		try
		{
			Mortal.Free.FreeManager free = Mortal.Free.FreeManager.Instance;
			string title = free != null ? HarmonyLib.Traverse.Create(free).Field("_titleText").GetValue<UnityEngine.UI.Text>()?.text ?? "" : "";
			title = TextConverter.ToSimplified(RichText.Strip(title)).Replace("\n", " ").Trim();
			if (title.Length > 0)
			{
				sb.Append("地图标题：").Append(title).Append('\n');
			}
			var places = Object.FindObjectsOfType<Mortal.Free.PositionController>()
				.Select(p => TextConverter.ToSimplified(HarmonyLib.Traverse.Create(p).Field("_position").GetValue<PositionType>().ToString()))
				.Where(n => n.Length > 0 && n != "无").Distinct().ToList();
			if (places.Count > 0)
			{
				sb.Append("这张地图上的场所：").Append(string.Join("、", places.ToArray())).Append("（从场所判断玩家在唐门还是崆峒等地，不要只凭身份猜；任务触发的场所不在这里面，就是不在这张地图上，要先回到那里）\n");
			}
		}
		catch (System.Exception ex)
		{
			FileLogger.Log("[AI] 读地图失败: " + ex.Message);
		}
		return sb.ToString();
	}

	/// <summary>给 AI 的 player_state 工具：玩家现在的进度、数值、好感（旗标、书籍、物品太多，不放）。</summary>
	internal static string AiPlayerState()
	{
		PlayerStatManagerData player = PlayerStatManagerData.Instance;
		string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
		if (player == null || scene == "Title")
		{
			return "玩家在标题画面，还没读档进游戏。";
		}
		var sb = new StringBuilder();
		sb.Append("游戏日期：").Append(ReferenceEquals(player.GameTime, null) ? "不明" : StoryAtlas.DateText(player.GameTime)).Append('\n');
		sb.Append("现在在：").Append(scene == "Story" ? "剧情里" : scene == "Free" ? "养成地图上" : scene == "Combat" || scene == "Battle" ? "战斗中" : scene).Append('\n');
		if (scene == "Free")
		{
			sb.Append(MapLocation());
		}
		string story = player.CurrentStoryScript ?? "";
		if (story.Length > 0)
		{
			sb.Append("最近的剧本：").Append(story);
			if (StoryAtlas.Owner.TryGetValue(story, out StoryMission owner))
			{
				sb.Append("（任务 ").Append(owner.Key).Append("「").Append(AiKnowledge.Clean(owner.Title)).Append("」）");
			}
			sb.Append('\n');
		}
		if (DataHelper.GetMainMission(out MissionCheckData main, out string mainTitle, out string mainDesc))
		{
			MissionManagerData missions = MissionManagerData.Instance;
			bool ready = missions != null && missions.MainMissionStart && missions.MainCheckData == main && !string.IsNullOrEmpty(main.StartScript);
			sb.Append("主线：").Append(AiKnowledge.Clean(mainTitle).Replace("\n", " ")).Append(ready ? "（条件已满足，去 " + TextConverter.ToSimplified(main.Position.ToString()) + " 就会触发）" : "").Append("：").Append(AiKnowledge.Clean(mainDesc).Replace("\n", " ")).Append('\n');
		}
		var active = MissionManagerData.Instance?.ActiveSubMissions;
		var subs = DataHelper.GetMissions();
		if (subs.Count > 0)
		{
			sb.Append("进行中的支线：\n");
			foreach (var pair in subs)
			{
				bool ready = active != null && active.Values.Contains(pair.Key) && !string.IsNullOrEmpty(pair.Key.StartScript);
				string where = TextConverter.ToSimplified(pair.Key.Position.ToString());
				sb.Append("- ").Append(AiKnowledge.Clean(pair.Value.Item1).Replace("\n", " ")).Append(ready ? "（可以触发）" : "")
					.Append(where.Length > 0 && where != "无" ? "（在「" + where + "」触发）" : "").Append("：").Append(AiKnowledge.Clean(pair.Value.Item2).Replace("\n", " ")).Append('\n');
			}
		}
		string onScreen = OnScreen();
		if (onScreen.Length > 0)
		{
			sb.Append("【眼前】（屏幕上正显示的，玩家问「这里」「这个选项」「这次掷骰」指的就是它）\n").Append(onScreen);
		}
		sb.Append("感情：").Append(AiPersona.RelationText(null)).Append('\n');
		var skip = new HashSet<string> { "旗标", "书籍", "物品", "称号", "功法修炼" };
		foreach (var (title, rows) in BuildStatSections(player))
		{
			if (skip.Contains(title))
			{
				continue;
			}
			var parts = new List<string>();
			foreach (StatRow row in rows)
			{
				if (row.NoValue || row.Get == null)
				{
					continue;
				}
				int value = row.Get();
				// 好感、天赋只列有数的
				if ((title == "好感" || title == "天赋") && value == 0)
				{
					continue;
				}
				parts.Add(AiKnowledge.Clean(row.Name) + " " + value + (row.Of > 0 ? "/" + row.Of : ""));
			}
			if (parts.Count > 0)
			{
				sb.Append(title).Append("：").Append(string.Join("，", parts.ToArray())).Append('\n');
			}
		}
		return sb.ToString();
	}
}
