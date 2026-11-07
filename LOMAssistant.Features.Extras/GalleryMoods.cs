using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LOMAssistant.Features.Extras;

/// <summary>
/// 鉴赏立绘页的差分名：游戏资源里是英文文件名（normal、nervous1、trainee3_normal……），按词拼成界面语言的名字。
/// 表里没有的词原样显示。每行：简体、韩语、日语、英语。
/// </summary>
internal static class GalleryMoods
{
	// 两个词合起来另有意思的，先查这里
	private static readonly Dictionary<string, string[]> Phrases = new Dictionary<string, string[]>
	{
		{ "close_eye", new[] { "闭眼", "눈 감음", "目を閉じる", "eyes closed" } },
		{ "close_eyes", new[] { "闭眼", "눈 감음", "目を閉じる", "eyes closed" } },
		{ "roll_eye", new[] { "翻白眼", "눈 흘김", "白目", "eye roll" } },
		{ "down_eye", new[] { "垂眼", "눈 내리깜", "伏し目", "eyes down" } },
		{ "no_weapon", new[] { "无兵器", "무기 없음", "武器なし", "unarmed" } },
		{ "one_hand", new[] { "单手", "한 손", "片手", "one hand" } },
		{ "body_lower", new[] { "下半身", "하반신", "下半身", "lower body" } },
		{ "body_upper", new[] { "上半身", "상반신", "上半身", "upper body" } },
		{ "cow_horse", new[] { "牛头马面", "우두마면", "牛頭馬頭", "Ox-Head and Horse-Face" } },
		{ "cow_man", new[] { "牛头", "우두", "牛頭", "Ox-Head" } },
		{ "horse_man", new[] { "马面", "마면", "馬頭", "Horse-Face" } },
		{ "grappling_hook", new[] { "飞爪", "갈고리", "鉤縄", "grappling hook" } },
		{ "hero_landing", new[] { "英雄落地", "히어로 착지", "ヒーロー着地", "hero landing" } },
		{ "north_star", new[] { "北斗", "북두", "北斗", "North Star" } },
		{ "cook_wine", new[] { "煮酒", "술 데우기", "酒の燗", "warming wine" } },
		{ "god_thunder", new[] { "神雷", "신뢰", "神雷", "divine thunder" } },
		{ "mega_player", new[] { "巨大主角", "거대 주인공", "巨大主人公", "giant protagonist" } },
		{ "doctor_flag", new[] { "大夫幌子", "의원 깃발", "医者の幟", "doctor's banner" } },
		{ "jewelry_box", new[] { "首饰盒", "보석함", "宝石箱", "jewelry box" } },
		{ "sword_box", new[] { "剑匣", "검갑", "剣箱", "sword case" } },
		{ "food_box", new[] { "食盒", "찬합", "重箱", "food box" } },
	};

	private static readonly Dictionary<string, string[]> Words = new Dictionary<string, string[]>
	{
		// 表情
		{ "normal", new[] { "普通", "보통", "普通", "normal" } },
		{ "nervous", new[] { "紧张", "긴장", "緊張", "nervous" } },
		{ "nervious", new[] { "紧张", "긴장", "緊張", "nervous" } },
		{ "laugh", new[] { "笑", "웃음", "笑い", "laugh" } },
		{ "angry", new[] { "生气", "화남", "怒り", "angry" } },
		{ "hurt", new[] { "受伤", "부상", "負傷", "hurt" } },
		{ "sad", new[] { "难过", "슬픔", "悲しみ", "sad" } },
		{ "shy", new[] { "害羞", "부끄러움", "照れ", "shy" } },
		{ "gloomy", new[] { "阴沉", "침울", "陰鬱", "gloomy" } },
		{ "cry", new[] { "哭", "울음", "泣き", "crying" } },
		{ "special", new[] { "特殊", "특수", "特殊", "special" } },
		{ "doubt", new[] { "疑惑", "의심", "疑い", "doubt" } },
		{ "drunk", new[] { "醉", "취함", "酔い", "drunk" } },
		{ "shock", new[] { "震惊", "충격", "驚き", "shock" } },
		{ "discouraged", new[] { "沮丧", "낙담", "落胆", "discouraged" } },
		{ "haggard", new[] { "憔悴", "초췌", "憔悴", "haggard" } },
		{ "crazy", new[] { "疯狂", "광기", "狂気", "crazy" } },
		{ "proud", new[] { "得意", "의기양양", "得意げ", "proud" } },
		{ "scary", new[] { "吓人", "무서움", "恐ろしい", "scary" } },
		{ "serious", new[] { "严肃", "진지", "真剣", "serious" } },
		{ "exciting", new[] { "兴奋", "흥분", "興奮", "excited" } },
		{ "sick", new[] { "病", "병", "病", "sick" } },
		{ "tired", new[] { "疲惫", "피곤", "疲れ", "tired" } },
		{ "annoyed", new[] { "不耐烦", "짜증", "苛立ち", "annoyed" } },
		{ "squint", new[] { "眯眼", "실눈", "細目", "squint" } },
		{ "droopy", new[] { "无精打采", "축 처짐", "しょんぼり", "droopy" } },
		{ "relax", new[] { "放松", "느긋함", "くつろぎ", "relaxed" } },
		{ "diffident", new[] { "怯", "주눅", "おずおず", "diffident" } },
		{ "hopeless", new[] { "绝望", "절망", "絶望", "hopeless" } },
		{ "determined", new[] { "坚定", "결연", "決意", "determined" } },
		{ "stupid", new[] { "呆", "멍함", "呆然", "dazed" } },
		{ "love", new[] { "心动", "설렘", "ときめき", "smitten" } },
		{ "endure", new[] { "忍耐", "인내", "我慢", "enduring" } },
		{ "revel", new[] { "狂欢", "들뜸", "浮かれ", "reveling" } },
		{ "pray", new[] { "祈祷", "기도", "祈り", "praying" } },
		{ "die", new[] { "死", "죽음", "死", "dead" } },
		{ "blood", new[] { "带血", "피", "血", "bloody" } },
		{ "norseblood", new[] { "鼻血", "코피", "鼻血", "nosebleed" } },
		{ "empty", new[] { "空", "빈", "空", "empty" } },
		{ "real", new[] { "真身", "진짜 모습", "正体", "true form" } },
		{ "transparent", new[] { "透明", "투명", "透明", "transparent" } },
		{ "soul", new[] { "魂", "혼", "魂", "soul" } },
		{ "shadow", new[] { "影子", "그림자", "影", "shadow" } },
		{ "white", new[] { "白", "흰색", "白", "white" } },
		{ "black", new[] { "黑", "검은색", "黒", "black" } },
		{ "red", new[] { "红", "붉은색", "赤", "red" } },
		{ "blue", new[] { "蓝", "푸른색", "青", "blue" } },
		{ "golden", new[] { "金", "금색", "金", "golden" } },
		{ "young", new[] { "年轻", "젊은 시절", "若い頃", "young" } },
		{ "old", new[] { "年老", "노년", "老年", "old" } },
		{ "full", new[] { "全副武装", "완전 무장", "完全武装", "fully armed" } },
		{ "half", new[] { "半", "반", "半分", "half" } },
		{ "long", new[] { "长", "긴", "長い", "long" } },
		{ "drawn", new[] { "消瘦", "수척", "やつれ", "drawn" } },
		{ "section", new[] { "局部", "부분", "部分", "part" } },
		{ "back", new[] { "背影", "뒷모습", "後ろ姿", "back" } },
		{ "left", new[] { "左", "왼쪽", "左", "left" } },
		{ "right", new[] { "右", "오른쪽", "右", "right" } },
		{ "eye", new[] { "眼", "눈", "目", "eye" } },
		{ "eyes", new[] { "眼", "눈", "目", "eyes" } },
		{ "face", new[] { "脸", "얼굴", "顔", "face" } },
		{ "head", new[] { "头", "머리", "頭", "head" } },
		{ "hand", new[] { "手", "손", "手", "hand" } },
		{ "fist", new[] { "拳", "주먹", "拳", "fist" } },
		{ "hat", new[] { "帽子", "모자", "帽子", "hat" } },
		{ "mask", new[] { "面具", "가면", "仮面", "mask" } },
		{ "powder", new[] { "粉", "가루", "粉", "powder" } },
		{ "mud", new[] { "泥", "진흙", "泥", "mud" } },
		// 动作
		{ "attack", new[] { "攻击", "공격", "攻撃", "attack" } },
		{ "kick", new[] { "踢", "발차기", "蹴り", "kick" } },
		{ "block", new[] { "格挡", "막기", "防御", "block" } },
		{ "kill", new[] { "杀", "살기", "殺気", "killing" } },
		{ "cast", new[] { "施法", "시전", "術", "casting" } },
		{ "caught", new[] { "被抓", "붙잡힘", "捕まり", "caught" } },
		{ "shake", new[] { "颤抖", "떨림", "震え", "shaking" } },
		{ "swim", new[] { "游泳", "수영", "水泳", "swimming" } },
		{ "suck", new[] { "吸", "빨기", "吸う", "sucking" } },
		{ "roll", new[] { "翻", "굴림", "転がり", "roll" } },
		{ "turn", new[] { "翻倒", "뒤집힘", "ひっくり返し", "overturned" } },
		{ "break", new[] { "破", "부서짐", "壊れ", "broken" } },
		{ "open", new[] { "打开", "열림", "開き", "open" } },
		{ "landing", new[] { "落地", "착지", "着地", "landing" } },
		{ "jojo", new[] { "JOJO", "JOJO", "ジョジョ", "JoJo" } },
		{ "oraora", new[] { "欧拉欧拉", "오라오라", "オラオラ", "ORA ORA" } },
		{ "gamer", new[] { "玩游戏", "게임", "ゲーム", "gaming" } },
		{ "home", new[] { "居家", "집", "家", "home" } },
		{ "backstage", new[] { "后台", "무대 뒤", "舞台裏", "backstage" } },
		// 人物
		{ "player", new[] { "主角", "주인공", "主人公", "protagonist" } },
		{ "brother", new[] { "师兄", "사형", "兄弟子", "senior brother" } },
		{ "master", new[] { "掌门", "장문인", "掌門", "sect leader" } },
		{ "trainee", new[] { "弟子", "제자", "弟子", "disciple" } },
		{ "traineegirl", new[] { "女弟子", "여제자", "女弟子", "female disciple" } },
		{ "daoshi", new[] { "道士", "도사", "道士", "Taoist" } },
		{ "monk", new[] { "和尚", "승려", "和尚", "monk" } },
		{ "scholar", new[] { "书生", "서생", "書生", "scholar" } },
		{ "merchant", new[] { "商人", "상인", "商人", "merchant" } },
		{ "shopman", new[] { "掌柜", "주인장", "店主", "shopkeeper" } },
		{ "vendor", new[] { "小贩", "행상", "物売り", "vendor" } },
		{ "soldier", new[] { "士兵", "병사", "兵士", "soldier" } },
		{ "guard", new[] { "捕快", "포졸", "捕り手", "constable" } },
		{ "ranger", new[] { "侠客", "협객", "侠客", "wanderer" } },
		{ "assassin", new[] { "刺客", "자객", "刺客", "assassin" } },
		{ "artist", new[] { "拳师", "권사", "拳法家", "boxer" } },
		{ "blacksmith", new[] { "铁匠", "대장장이", "鍛冶屋", "blacksmith" } },
		{ "woodman", new[] { "木人", "목인", "木人", "wooden dummy" } },
		{ "beggar", new[] { "乞丐", "거지", "乞食", "beggar" } },
		{ "badguy", new[] { "歹人", "악당", "悪党", "thug" } },
		{ "bandit", new[] { "山贼", "산적", "山賊", "bandit" } },
		{ "stone", new[] { "飞石帮众", "비석방 방도", "飛石幇の手下", "Flying Stone member" } },
		{ "fairy", new[] { "仙姑", "선고", "仙姑", "immortal maiden" } },
		{ "ghost", new[] { "鬼卒", "귀졸", "鬼卒", "ghost soldier" } },
		{ "hero", new[] { "英雄", "영웅", "英雄", "hero" } },
		{ "man", new[] { "男", "남자", "男", "man" } },
		{ "female", new[] { "女路人", "여자 행인", "女の通行人", "woman" } },
		{ "mom", new[] { "母亲", "어머니", "母", "mother" } },
		{ "boy", new[] { "少年", "소년", "少年", "boy" } },
		{ "girl", new[] { "女孩", "소녀", "少女", "girl" } },
		{ "student", new[] { "学童", "학동", "学童", "pupil" } },
		// 动物
		{ "horse", new[] { "马", "말", "馬", "horse" } },
		{ "cow", new[] { "牛", "소", "牛", "ox" } },
		{ "wolf", new[] { "狼", "늑대", "狼", "wolf" } },
		{ "monkey", new[] { "猴子", "원숭이", "猿", "monkey" } },
		{ "parrot", new[] { "鹦鹉", "앵무새", "オウム", "parrot" } },
		{ "chicken", new[] { "鸡", "닭", "鶏", "chicken" } },
		{ "pig", new[] { "猪", "돼지", "豚", "pig" } },
		{ "pigeon", new[] { "鸽子", "비둘기", "鳩", "pigeon" } },
		// 物品
		{ "sword", new[] { "剑", "검", "剣", "sword" } },
		{ "weapon", new[] { "兵器", "무기", "武器", "weapon" } },
		{ "dart", new[] { "镖", "표창", "鏢", "dart" } },
		{ "hammer", new[] { "锤", "망치", "槌", "hammer" } },
		{ "stick", new[] { "棍", "막대기", "棒", "stick" } },
		{ "drumstick", new[] { "鸡腿", "닭다리", "鶏もも", "drumstick" } },
		{ "umbrella", new[] { "伞", "우산", "傘", "umbrella" } },
		{ "fan", new[] { "扇子", "부채", "扇子", "fan" } },
		{ "letter", new[] { "信", "편지", "手紙", "letter" } },
		{ "map", new[] { "地图", "지도", "地図", "map" } },
		{ "bag", new[] { "袋子", "자루", "袋", "bag" } },
		{ "box", new[] { "盒子", "상자", "箱", "box" } },
		{ "bucket", new[] { "水桶", "물통", "桶", "bucket" } },
		{ "pot", new[] { "锅", "솥", "鍋", "pot" } },
		{ "table", new[] { "桌子", "탁자", "机", "table" } },
		{ "ladder", new[] { "梯子", "사다리", "はしご", "ladder" } },
		{ "tree", new[] { "树", "나무", "木", "tree" } },
		{ "branch", new[] { "树枝", "나뭇가지", "枝", "branch" } },
		{ "roof", new[] { "屋顶", "지붕", "屋根", "roof" } },
		{ "tent", new[] { "帐篷", "천막", "天幕", "tent" } },
		{ "sedan", new[] { "轿子", "가마", "駕籠", "sedan chair" } },
		{ "carriage", new[] { "马车", "마차", "馬車", "carriage" } },
		{ "checkerboard", new[] { "棋盘", "바둑판", "碁盤", "game board" } },
		{ "kite", new[] { "风筝", "연", "凧", "kite" } },
		{ "money", new[] { "钱", "돈", "銭", "money" } },
		{ "jewelry", new[] { "首饰", "장신구", "装飾品", "jewelry" } },
		{ "food", new[] { "食物", "음식", "食べ物", "food" } },
		{ "bun", new[] { "包子", "만두", "肉まん", "bun" } },
		{ "cake", new[] { "糕点", "떡", "菓子", "pastry" } },
		{ "soymilk", new[] { "豆浆", "두유", "豆乳", "soy milk" } },
		{ "peach", new[] { "桃子", "복숭아", "桃", "peach" } },
		{ "tanghulu", new[] { "糖葫芦", "탕후루", "サンザシ飴", "tanghulu" } },
		{ "wine", new[] { "酒", "술", "酒", "wine" } },
		{ "flag", new[] { "旗", "깃발", "旗", "flag" } },
		{ "fire", new[] { "火", "불", "火", "fire" } },
		{ "thunder", new[] { "雷", "번개", "雷", "thunder" } },
		{ "god", new[] { "神", "신", "神", "god" } },
		{ "star", new[] { "星", "별", "星", "star" } },
		{ "north", new[] { "北", "북", "北", "north" } },
		{ "body", new[] { "身体", "몸", "体", "body" } },
		{ "lower", new[] { "下", "아래", "下", "lower" } },
		{ "upper", new[] { "上", "위", "上", "upper" } },
		{ "down", new[] { "下", "아래", "下", "down" } },
		{ "no", new[] { "无", "없음", "なし", "no" } },
		{ "one", new[] { "一", "하나", "一", "one" } },
		{ "mega", new[] { "巨大", "거대", "巨大", "giant" } },
		{ "cook", new[] { "煮", "끓이기", "煮る", "cooking" } },
		{ "doctor", new[] { "大夫", "의원", "医者", "doctor" } },
	};

	private static readonly Regex Part = new Regex(@"^([A-Za-z]+?)(\d+%?)?$");

	/// <summary>差分的文件名换成界面语言的名字。</summary>
	internal static string Name(string mood)
	{
		string language = TextConverter.DisplayLanguage;
		int column = language == UiText.Korean ? 1 : language == UiText.Japanese ? 2 : language == UiText.English ? 3 : 0;
		string[] parts = Regex.Split(mood ?? "", @"[_\-\s]+").Where(p => p.Length > 0).ToArray();
		var shown = new List<string>();
		for (int i = 0; i < parts.Length; i++)
		{
			string word = parts[i];
			string number = "";
			Match m = Part.Match(word);
			if (m.Success)
			{
				word = m.Groups[1].Value;
				number = m.Groups[2].Value;
			}
			string key = word.ToLowerInvariant();
			if (i + 1 < parts.Length)
			{
				Match next = Part.Match(parts[i + 1]);
				string pair = key + "_" + (next.Success ? next.Groups[1].Value : parts[i + 1]).ToLowerInvariant();
				if (Phrases.TryGetValue(pair, out string[] phrase))
				{
					shown.Add(Pick(phrase, column) + Number(next.Success ? next.Groups[2].Value : ""));
					i++;
					continue;
				}
			}
			shown.Add(Words.TryGetValue(key, out string[] names) ? Pick(names, column) + Number(number) : parts[i]);
		}
		return shown.Count > 0 ? string.Join(" · ", shown.ToArray()) : mood;
	}

	private static string Number(string number) => number.Length > 0 ? " " + number : "";

	private static string Pick(string[] names, int column)
	{
		if (column > 0)
		{
			return names[column];
		}
		return TextConverter.DisplayLanguage == "ChineseTraditional" ? TextConverter.ToTraditional(names[0]) : names[0];
	}
}
