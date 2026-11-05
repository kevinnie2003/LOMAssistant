using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LOMAssistant.LuaParse;
using Mortal.Core;
using Mortal.Story;

namespace LOMAssistant;

public class PositionEventCache
{
	private static readonly Lazy<PositionEventCache> instance = new Lazy<PositionEventCache>(() => new PositionEventCache());

	private readonly Dictionary<string, List<PositionEventData>> positionEventDataCache;

	private readonly Dictionary<PositionEventData, string> eventKeyCache;

	private readonly Dictionary<PositionEventData, string> scriptPrefixCache;

	public static PositionEventCache Instance => instance.Value;

	private PositionEventCache()
	{
		positionEventDataCache = new Dictionary<string, List<PositionEventData>>();
		eventKeyCache = new Dictionary<PositionEventData, string>();
		scriptPrefixCache = new Dictionary<PositionEventData, string>();
	}

	public List<PositionEventData> GetPositionEvents(string position)
	{
		if (!positionEventDataCache.TryGetValue(position, out var value))
		{
			value = LoadPositionEvents(position, out bool found);
			// 事件表没拿到（不在剧情场景、也没在已载入的资源里找到）时不缓存，下次再找；
			// 以前在养成地图上悬停建筑会把空表缓存下来，回到剧情里随机事件自选就不弹了
			if (found)
			{
				positionEventDataCache[position] = value;
			}
		}
		return value;
	}

	public string GetPositionEventName(PositionEventData eventData)
	{
		if (!eventKeyCache.TryGetValue(eventData, out var value))
		{
			value = GenerateEventKey(eventData);
			eventKeyCache[eventData] = value;
		}
		return value;
	}

	public string GetPositionEventDesc(PositionEventData eventData)
	{
		return GenerateDynamicDesc(eventData) + "\n" + GenerateStaticDesc(eventData);
	}

	public int GetPositionEventRate(PositionEventData eventData)
	{
		return eventData.GetRate();
	}

	public List<PositionEventData> GetPositionEventsSortedByRate(string position)
	{
		return GetPositionEvents(position).OrderByDescending(GetPositionEventRate).ToList();
	}

	/// <summary>所有场所事件池里的事件（场所编号、事件）；事件表拿不到时是空的。</summary>
	public List<(string Pool, PositionEventData Data)> AllEvents()
	{
		var list = new List<(string, PositionEventData)>();
		PositionResultConfig config = Config();
		if (config?.List == null)
		{
			return list;
		}
		foreach (PositionResultData pool in config.List)
		{
			foreach (PositionEventData e in GetPositionEvents(pool.name))
			{
				list.Add((pool.name, e));
			}
		}
		return list;
	}

	/// <summary>场所编号的显示名（如「飞天殿_修炼」）。</summary>
	public static string PlaceName(string poolKey) => GetPositionDisplayName(poolKey);

	private static PositionResultConfig _config;

	/// <summary>场所事件表：剧情场景里从 CheckPointManager 拿并记住；养成地图上没有它，就用记住的或在已载入的资源里找。</summary>
	/// <summary>场所事件表拿得到没有：它挂在剧情场景上，开游戏后直接读档进养成地图、还没进过剧情时拿不到。</summary>
	public static bool Available => Config() != null;

	private static PositionResultConfig Config()
	{
		CheckPointManager checkPointManager = CheckPointManager.Instance;
		PositionResultConfig config = checkPointManager != null ? checkPointManager.GetFieldValue<PositionResultConfig>("_position") : null;
		if (config != null)
		{
			return _config = config;
		}
		if (_config == null)
		{
			_config = UnityEngine.Resources.FindObjectsOfTypeAll<PositionResultConfig>().FirstOrDefault();
		}
		return _config;
	}

	private List<PositionEventData> LoadPositionEvents(string position, out bool found)
	{
		List<PositionEventData> list = new List<PositionEventData>();
		PositionResultConfig config = Config();
		found = config != null && config.List != null;
		if (!found)
		{
			return list;
		}
		PositionResultData positionResultData = config.List.FirstOrDefault((PositionResultData p) => p.name == position);
		if (positionResultData == null)
		{
			return list;
		}
		string fieldValue = positionResultData.GetFieldValue<string>("_scriptPrefix");
		foreach (PositionEventData item in positionResultData.List)
		{
			scriptPrefixCache[item] = fieldValue;
			list.Add(item);
		}
		return list;
	}

	private string GenerateEventKey(PositionEventData eventData)
	{
		return GetPositionDisplayName(positionEventDataCache.FirstOrDefault((KeyValuePair<string, List<PositionEventData>> p) => p.Value.Contains(eventData)).Key) + "事件：" + DataHelper.GetEventName(eventData);
	}

	// 留学地图的场所事件池（Section_门派_场所…）：场所名取自留学地图菜单（section_0X_free_start_02 的选项和它们进的剧本）
	private static readonly Dictionary<string, string> SectionPlaces = new Dictionary<string, string>
	{
		{ "Section_01_00", "崆峒·窝居" },
		{ "Section_01_01", "崆峒·飞天殿" },
		{ "Section_01_02", "崆峒·夺魄森林" },
		{ "Section_01_03", "崆峒·铁拳巷" },
		{ "Section_01_04", "崆峒·玄功洞" },
		{ "Section_01_05", "崆峒·夺魄峰" },
		{ "Section_01_06", "崆峒·无色广场" },
		{ "Section_01_07", "崆峒·飞天殿（遇到小梅）" },
		{ "Section_01_08", "崆峒·鹤崖" },
		{ "Section_02_00", "青城·窝居" },
		{ "Section_02_01", "青城·常风观" },
		{ "Section_02_02", "青城·上清宫" },
		{ "Section_02_03", "青城·青城山镇" },
	};

	private static string GetPositionDisplayName(string positionKey)
	{
		if (positionKey != null && positionKey.Length >= 13 && SectionPlaces.TryGetValue(positionKey.Substring(0, 13), out string section))
		{
			return section + (positionKey.Length > 13 ? "（" + positionKey + "）" : "");
		}
		string[] array = positionKey.Split('_');
		if (array.Length == 0)
		{
			return positionKey;
		}
		string locationName = GetLocationName(array[0]);
		if (array.Length > 1)
		{
			List<string> list = new List<string>();
			for (int i = 1; i < array.Length; i++)
			{
				list.Add(GetTypeName(array[i]));
			}
			return locationName + "_" + string.Join("_", list);
		}
		return locationName;
	}

	private static string GetTypeName(string typeKey)
	{
		try
		{
			string text = LocalizationManager.Instance.LocaleResolver.GetString("Position/Type/" + typeKey);
			if (!string.IsNullOrEmpty(text))
			{
				return TextConverter.ToSimplified(text);
			}
		}
		catch
		{
		}
		string text2 = typeKey.ToLower();
		if (!(text2 == "work"))
		{
			if (text2 == "free")
			{
				return "自由";
			}
			return typeKey;
		}
		return "工作";
	}

	private static string GetLocationName(string shortKey)
	{
		try
		{
			string text = LocalizationManager.Instance.LocaleResolver.GetString("Position/Name/" + shortKey);
			if (!string.IsNullOrEmpty(text))
			{
				return TextConverter.ToSimplified(text);
			}
		}
		catch
		{
		}
		return shortKey.ToLower() switch
		{
			"kitchen" => "伙房", 
			"forge" => "锻造房", 
			"alchemy" => "炼丹房", 
			"doctor" => "医馆", 
			"center" => "中殿", 
			"backmountain" => "后山", 
			"door" => "门派大门", 
			"girlroom" => "女弟子房", 
			"girl" => "女弟子", 
			"mall" => "商铺", 
			"guard" => "守卫", 
			"study" => "书房", 
			"tree" => "树下", 
			"room" => "房间", 
			_ => shortKey, 
		};
	}

	/// <summary>事件结果的剧本名；没读过这个事件池返回 null。</summary>
	public string ResultScript(PositionEventData eventData)
	{
		return scriptPrefixCache.TryGetValue(eventData, out var prefix) ? prefix + "_" + eventData.ResultType : null;
	}

	/// <summary>随机事件选择窗里的说明：一定会有的变化；有条件分支（「后续」里按旗标、数值分开走的加减）的，再写一行「某些条件下」，和「此刻」页一样。</summary>
	public string GetEffects(PositionEventData eventData)
	{
		if (!scriptPrefixCache.TryGetValue(eventData, out var prefix))
		{
			return "";
		}
		List<LOMAssistant.LuaParse.Action> actions = LuaParser.Instance.ExtractActionsFromLua(prefix + "_" + eventData.ResultType);
		string certain = LuaParser.Description(actions.Where((LOMAssistant.LuaParse.Action a) => !a.IsConditional).ToList());
		List<string> branches = LuaParser.ConditionalLines(actions);
		if (branches.Count == 0)
		{
			return TextConverter.ToSimplifiedUnlessJapanese(certain);
		}
		var lines = new List<string>();
		if (!string.IsNullOrEmpty(certain))
		{
			lines.Add("一定触发：" + certain);
		}
		lines.AddRange(branches);
		return TextConverter.ToSimplifiedUnlessJapanese(string.Join("\n", lines.ToArray()));
	}

	private string GenerateStaticDesc(PositionEventData eventData)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("可能的结果：");
		if (!scriptPrefixCache.TryGetValue(eventData, out var value))
		{
			throw new InvalidOperationException("Script prefix not found for event data");
		}
		string luaScriptName = value + "_" + eventData.ResultType;
		List<LOMAssistant.LuaParse.Action> source = LuaParser.Instance.ExtractActionsFromLua(luaScriptName);
		List<LOMAssistant.LuaParse.Action> actions = source.Where((LOMAssistant.LuaParse.Action a) => !a.IsConditional).ToList();
		string text = LuaParser.Description(actions);
		if (!string.IsNullOrEmpty(text))
		{
			stringBuilder.AppendLine("    一定触发：" + text);
		}
		foreach (string line in LuaParser.ConditionalLines(source))
		{
			stringBuilder.AppendLine("    " + line);
		}
		return stringBuilder.ToString();
	}

	private string GenerateDynamicDesc(PositionEventData eventData)
	{
		StringBuilder stringBuilder = new StringBuilder();
		int fieldValue = eventData.GetFieldValue<int>("_defaultRate");
		int rate = eventData.GetRate();
		stringBuilder.AppendLine($"概率：{rate}（基础{fieldValue}）");
		bool fieldValue2 = eventData.GetFieldValue<bool>("_toggleCondition");
		ConditionResultItem fieldValue3 = eventData.GetFieldValue<ConditionResultItem>("_activeCondition");
		if (fieldValue2 && fieldValue3 != null)
		{
			stringBuilder.AppendLine("激活条件：" + DataHelper.ExtractConditions(fieldValue3, out var _));
		}
		stringBuilder.AppendLine("判定条件：");
		PositionEventRateItem[] fieldValue4 = eventData.GetFieldValue<PositionEventRateItem[]>("_eventRateItem");
		foreach (PositionEventRateItem positionEventRateItem in fieldValue4)
		{
			bool isInPeriod2;
			string text = DataHelper.ExtractConditions(positionEventRateItem.Condition, out isInPeriod2);
			string text2 = ((positionEventRateItem.Rate > 0) ? $"+{positionEventRateItem.Rate}" : positionEventRateItem.Rate.ToString());
			stringBuilder.AppendLine("    " + text + "：" + text2);
		}
		return stringBuilder.ToString();
	}
}
