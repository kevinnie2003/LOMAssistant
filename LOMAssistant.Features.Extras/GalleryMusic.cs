namespace LOMAssistant.Features.Extras;

/// <summary>
/// 鉴赏页的曲目表，按官方原声带第一辑、第二辑的顺序排。
/// 官方曲名没有写在游戏里：对照 Music.bnk 里每首曲子的时长和原声带曲目时长一一对上。
/// 胜利、失败的几秒短音效不在原声带里，不收录。第一辑官方英文名只有拼音。
/// </summary>
internal static class GalleryMusic
{
	internal static readonly (string Key, string Traditional, string Simplified, string English)[] Tracks =
	{
		("Theme1", "某人的傳說", "某人的传说", "Mou Ren De Chuan Shuo"),
		("Happy1", "春暖花開搓圓圓", "春暖花开搓圆圆", "Chun Nuan Hua Kai Cuo Yuan Yuan"),
		("Funny1", "得意須盡歡", "得意须尽欢", "De Yi Xu Jin Huan"),
		("Normal1", "山中好歲月", "山中好岁月", "Shan Zhong Hao Sui Yue"),
		("Combat1", "好戲開場", "好戏开场", "Hao Xi Kai Chang"),
		("Boss4", "君所願兮江湖行 最是輕狂不願醒", "君所愿兮江湖行 最是轻狂不愿醒", "Jun Suo Yuan Zhi Jiang Hu Xing Zui Shi Qing Kuang Bu Yuan Xing"),
		("Danger1", "戰令堂", "战令堂", "Zhan Ling Tang"),
		("Quirky1", "每天都歡似過年", "每天都欢似过年", "Mei Tian Dou Huan Si Guo Nian"),
		("Quirky2", "猜猜無猜心", "猜猜无猜心", "Cai Cai Wu Cai Xin"),
		("Battle1", "黃沙起雲飛揚", "黄沙起云飞扬", "Huang Sha Qi Yun Fei Yang"),
		("Gloomy1", "窗影燈深處", "窗影灯深处", "Chuang Ying Deng Shen Chu"),
		("Boss2", "晚鐘聲 千燈行", "晚钟声 千灯行", "Wan Zhong Sheng Qian Deng Xing"),
		("Boss3", "恐怖君臨", "恐怖君临", "Kong Bu Jun Lin"),
		("Danger2", "仙侶宮 哪吒令 龍遊淺水", "仙侣宫 哪吒令 龙游浅水", "Xian Lv Gong Ne Zha Ling Long You Qian Shui"),
		("Legend2", "歷劫知俠氣", "历劫知侠气", "Li Jie Zhi Xia Qi"),
		("Intro", "正心而後知己", "正心而后知己", "Zheng Xin Er Hou Zhi Ji"),
		("Boss1", "吾往矣", "吾往矣", "Wu Wang Yi"),
		("Normal2", "青山舊歌謠", "青山旧歌谣", "Qing Shan Jiu Ge Yao"),
		("Legend1", "子夜寄君書", "子夜寄君书", "Zi Ye Ji Jun Shu"),
		("Serious1", "唐門薪火 自古如今", "唐门薪火 自古如今", "Tang Men Xin Huo Zi Gu Ru Jin"),
		("Warm1", "餘生可慶", "余生可庆", "Yu Sheng Ke Qing"),
		("Sad1", "謝幕時分 雨花零落", "谢幕时分 雨花零落", "Xie Mu Shi Fen Yu Hua Ling Luo"),
		("Happy2", "青山新歌謠", "青山新歌谣", "A New Tune in the Old Mountains"),
		("Normal3", "雨後松林香", "雨后松林香", "The Pines After Rain"),
		("Normal4", "鼎沸煙火氣", "鼎沸烟火气", "Flutter-Tonguing Over the Boiling Streets"),
		("Mall1", "錦繡織歲，玄燭照心", "锦绣织岁，玄烛照心", "The Illuminated Heart Within, Embroidered Seasons Without"),
		("Mystery1", "方外執棋手", "方外执棋手", "The Hand Beyond the Board"),
		("Old1", "金光裂重雲，指天喚霹靂", "金光裂重云，指天唤霹雳", "Shot Through the Storm"),
		("Cool1", "龍捲瘋", "龙卷疯", "Tornador"),
		("Gloomy2", "鬼門開", "鬼门开", "Returning from the Netherworld"),
		("Danger3", "敕召乾坤英雄令", "敕召乾坤英雄令", "Gathering of the Brave"),
		("Dawn1", "一呼百諾西武林", "一呼百诺西武林", "United"),
		("Hopeless1", "傳說之下", "传说之下", "Unsung Below"),
		("Battle2", "最崎嶇的路", "最崎岖的路", "The Most Treacherous Path"),
		("Combat2", "千里不留行", "千里不留行", "Ever Onward"),
		("Serious3", "斬破曇天", "斩破昙天", "Cleaving the Sky"),
		("Normal5", "春以花佐酒，夏邀月同遊", "春以花佐酒，夏邀月同游", "In Spring We Sip with Blooms, in Summer We Stroll with the Moon"),
		("Hope1", "願言配德兮，執子協行", "愿言配德兮，执子协行", "To Have and to Hold, Through Every Joy and Storm"),
		("Warm2", "帳中夢守人", "帐中梦守人", "Dreamkeeper"),
	};

	/// <summary>简体界面写简体；韩文、日文没有官方译名，写原名。</summary>
	internal static string Title(string traditional, string simplified, string english)
	{
		string language = TextConverter.DisplayLanguage;
		return language == UiText.English ? english : language == "ChineseSimplified" ? simplified : traditional;
	}
}
