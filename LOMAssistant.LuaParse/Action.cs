using System.Linq;
using System.Text.RegularExpressions;
using Mortal.Core;

namespace LOMAssistant.LuaParse;

public class Action
{
	public ActionType Type { get; set; }

	public bool IsConditional { get; set; }

	public int BranchIndex { get; set; }

	public string Content { get; set; }

	public string Description { get; private set; }

	public Action(ActionType type, string content, bool isConditional = false, int branchIndex = 0)
	{
		Type = type;
		Content = content;
		IsConditional = isConditional;
		BranchIndex = branchIndex;
		Description = ParseAction();
	}

	private string ParseAction()
	{
		return Type switch
		{
			ActionType.BattleScene => Content.Contains("Combat") ? "对决" : "混战", 
			ActionType.ModifyAction => ParseModifyAction(), 
			_ => string.Empty, 
		};
	}

	private string ParseModifyAction()
	{
		Match match = Regex.Match(Content, "statmodifymanager\\.(\\w+)\\(([^)]*)\\)");
		string value = match.Groups[1].Value;
		string value2 = match.Groups[2].Value;
		if (!match.Success)
		{
			return string.Empty;
		}
		return GetActionDescription(value, value2);
	}

	private string GetActionDescription(string actionType, string parameters)
	{
		string[] array = (from p in parameters.Split(',')
			select p.Trim().Trim('"')).ToArray();
		if (array.Length < 1)
		{
			return string.Empty;
		}
		string text = array[0];
		string text2;
		if (actionType != null)
		{
			switch (actionType.Length)
			{
			case 6:
				break;
			case 7:
				goto IL_00a4;
			case 9:
				goto IL_0131;
			case 10:
				goto IL_018e;
			case 19:
				goto IL_01dd;
			case 15:
				goto IL_0200;
			default:
				goto IL_0216;
			}
			if (actionType == "Player")
			{
				text2 = (DataHelper.TryParseEnum<GameStatType>(text, out var _) ? DataHelper.GetGameStatLocalizedName(text) : text);
				goto IL_0218;
			}
		}
		goto IL_0216;
		IL_018e:
		char c = actionType[0];
		if (c != 'A')
		{
			if (c != 'R' || !(actionType == "RemoveMisc"))
			{
				goto IL_0216;
			}
			text2 = DataHelper.GetItemName(GameItemType.雜物, text);
		}
		else
		{
			if (!(actionType == "AddSpecial"))
			{
				goto IL_0216;
			}
			text2 = DataHelper.GetItemName(GameItemType.貴重品, text);
		}
		goto IL_0218;
		IL_0218:
		text = text2;
		if (array.Length == 1)
		{
			if (actionType == "RemoveMisc")
			{
				return "丢弃" + text;
			}
			return string.Empty;
		}
		if (!int.TryParse(array[1], out var result2))
		{
			return string.Empty;
		}
		return GetDescription(actionType, text, result2);
		IL_01dd:
		if (!(actionType == "UpdateSetPlayerStat"))
		{
			goto IL_0216;
		}
		text2 = (DataHelper.TryParseEnum<GameStatType>(text, out var _) ? DataHelper.GetGameStatLocalizedName(text) : text);
		goto IL_0218;
		IL_00a4:
		char c2 = actionType[3];
		if (c2 != 'B')
		{
			if (c2 != 'F')
			{
				if (c2 != 'M' || !(actionType == "AddMisc"))
				{
					goto IL_0216;
				}
				text2 = DataHelper.GetItemName(GameItemType.雜物, text);
			}
			else if (actionType == "AddFlag")
			{
				text2 = DataHelper.GetFlagName(text);
			}
			else
			{
				if (!(actionType == "SetFlag"))
				{
					goto IL_0216;
				}
				text2 = DataHelper.GetFlagName(text);
			}
		}
		else
		{
			if (!(actionType == "AddBook"))
			{
				goto IL_0216;
			}
			text2 = DataHelper.GetItemName(GameItemType.書籍, text);
		}
		goto IL_0218;
		IL_0200:
		if (!(actionType == "ModifyEnemyTeam"))
		{
			goto IL_0216;
		}
		text2 = DataHelper.GetTeamName(text);
		goto IL_0218;
		IL_0216:
		text2 = text;
		goto IL_0218;
		IL_0131:
		char c3 = actionType[0];
		if (c3 != 'A')
		{
			if (c3 != 'C' || !(actionType == "Character"))
			{
				goto IL_0216;
			}
			text2 = (DataHelper.TryParseEnum<RelationshipStatType>(text, out var _) ? DataHelper.GetCharacterLocalizedName(text) : text);
		}
		else
		{
			if (!(actionType == "AddTalent"))
			{
				goto IL_0216;
			}
			text2 = DataHelper.GetTalentName(text);
		}
		goto IL_0218;
	}

	private string GetDescription(string actionType, string id, int value)
	{
		string text = ((value > 0) ? $"+{value}" : $"{value}");
		if (actionType != null)
		{
			switch (actionType.Length)
			{
			case 6:
				if (actionType == "Player")
				{
					return RichText.ColorText(id + " " + text);
				}
				break;
			case 7:
				switch (actionType[3])
				{
				case 'M':
					if (actionType == "AddMisc")
					{
						return $"{id} x{value}";
					}
					break;
				case 'F':
					if (actionType == "AddFlag")
					{
						if (!string.IsNullOrEmpty(id))
						{
							return $"{SimplifyFlagName(id)} {value}";
						}
						return string.Empty;
					}
					if (actionType == "SetFlag")
					{
						if (!string.IsNullOrEmpty(id))
						{
							return $"设置{SimplifyFlagName(id)}={value}";
						}
						return string.Empty;
					}
					break;
				case 'B':
					if (actionType == "AddBook")
					{
						return $"{id} x{value}";
					}
					break;
				}
				break;
			case 9:
				switch (actionType[0])
				{
				case 'C':
					if (actionType == "Character")
					{
						return RichText.ColorText(id + "好感 " + text);
					}
					break;
				case 'A':
					if (actionType == "AddTalent")
					{
						return $"天赋{id} lv{value}";
					}
					break;
				}
				break;
			case 10:
				if (actionType == "AddSpecial")
				{
					return $"{id} x{value}";
				}
				break;
			case 19:
				if (actionType == "UpdateSetPlayerStat")
				{
					return $"设置{id} {value}";
				}
				break;
			case 15:
				if (actionType == "ModifyEnemyTeam")
				{
					return RichText.ColorText(id + "向心力 " + text);
				}
				break;
			}
		}
		return string.Empty;
	}

	private static string SimplifyFlagName(string id)
	{
		if (string.IsNullOrEmpty(id))
		{
			return id;
		}
		try
		{
			string text = id;
			if (text.StartsWith("标记_") || text.StartsWith("旗标_") || text.StartsWith("標記_") || text.StartsWith("旗標_"))
			{
				text = text.Substring(3);
			}
			string flagName = DataHelper.GetFlagName(text);
			if (!string.IsNullOrEmpty(flagName))
			{
				return flagName + " (" + text + ")";
			}
		}
		catch
		{
		}
		return id.Replace("旗標_", "旗标_").Replace("標記_", "标记_").Replace("數值_", "数值_")
			.Replace("時間_", "时间_")
			.Replace("常數_", "常数_");
	}
}
