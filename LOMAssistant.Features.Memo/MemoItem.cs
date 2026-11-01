using System;

namespace LOMAssistant.Features.Memo;

/// <summary>备忘录盯的是哪一类游戏数据。存进文件时写数字，加新类别往后加，别改已有的顺序。</summary>
public enum MemoKind
{
	/// <summary>角色数值（体力、内力、银两……）。</summary>
	Stat = 0,
	/// <summary>某个人的好感。</summary>
	Favor = 1,
	/// <summary>功法（武学书）的修炼进度。</summary>
	Martial = 2,
	/// <summary>装备锻造的开发进度。</summary>
	Forge = 3,
	/// <summary>丹药（毒药）炼制的开发进度。</summary>
	Alchemy = 4,
	/// <summary>天赋等级。</summary>
	Talent = 5,
	/// <summary>门派设施等级。</summary>
	Facility = 6,
	/// <summary>物品、书籍的数量。</summary>
	Item = 7,
	/// <summary>旗标的值。</summary>
	Flag = 8,
}

/// <summary>条件怎么算满足。</summary>
public enum MemoOp
{
	/// <summary>到了 / 超过目标值。</summary>
	AtLeast = 0,
	/// <summary>不超过目标值（如「心理卫生别掉到 30 以下」写成 不超过 30 的反向盯法）。</summary>
	AtMost = 1,
	/// <summary>正好等于目标值（旗标常用）。</summary>
	Equal = 2,
}

/// <summary>
/// 一条备忘录：在某个时间前把某个条件做到。条件是「盯哪个数据（Kind+Key）+ 怎么比（Op）+ 目标值（Value）」，
/// 截止是绝对的旬数（和 StoryAtlas.Rounds 一致，第1年1月上旬是 0），-1 是不限时。
/// 数据存在 BepInEx\plugins\LOMAssistant\StoryReview\memo.json（见 MemoStore），跟着 Mod 走、不写进游戏存档，
/// 所以换存档、重开一局时旧的备忘录还在，日期对不上的自己删。
/// </summary>
[Serializable]
public sealed class MemoItem
{
	/// <summary>唯一编号，改名、改条件都不变。</summary>
	public string id = Guid.NewGuid().ToString("N");

	/// <summary>自己写的标题；空着就用条件本身当标题。</summary>
	public string title = "";

	public MemoKind kind;

	/// <summary>盯的那个数据在目录里的编号（见 MemoTarget.Key）。</summary>
	public string key = "";

	public MemoOp op;

	/// <summary>目标值。</summary>
	public int value;

	/// <summary>截止：第几旬（绝对，见 StoryAtlas.Rounds）；-1 不限时。</summary>
	public int deadline = -1;

	/// <summary>补充的话，显示在条目下面。</summary>
	public string note = "";

	/// <summary>手动勾掉（条件不方便自动盯、或者已经办完了）。</summary>
	public bool done;

	/// <summary>不弹提示（条目照样在窗口里盯着）。</summary>
	public bool muted;

	/// <summary>建的时候（Unix 秒），只用来排序和排查。</summary>
	public long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
