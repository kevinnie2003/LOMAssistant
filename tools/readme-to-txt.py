"""把各语言 README 转成发布压缩包里的说明文件（「使用说明.txt」「使用說明.txt」等，纯文本，记事本打开好读）。

内容和 README 一致，只做格式转换：去掉图片和「开发」一节（玩家用不到），
标题加下划线，加粗、行内代码去掉标记，链接写成「文字（网址）」，表格转成两列对齐的行。
用法：python tools/readme-to-txt.py README.md 输出路径 [--no-installer]
--no-installer：给不带安装器的包（N 网版）用，「安装」一节里安装器那段换成去哪下载安装器，卸载写成删 Mod 文件夹。
"""
import re
import sys
import unicodedata

SKIP_SECTIONS = {'开发', '開發', 'Building'}
# 只是展示用的徽章，说明文件里不写
SKIP_BADGES = {'下载次数', '下載次數', 'Downloads', 'ダウンロード数', '다운로드 수',
               'Star', 'Stars', 'スター', '스타', '最近更新', 'Last update', '最終更新', '최근 업데이트'}


def width(text):
    return sum(2 if unicodedata.east_asian_width(c) in 'WF' else 1 for c in text)


# 链接、徽章的写法：中文、日语、韩语用全角括号和冒号，英语用半角（main 里按 README 的语言改）
LINK, COLON = '{0}（{1}）', '：'


def inline(text):
    text = re.sub(r'\[([^\]]+)\]\(([^)]+)\)', lambda m: m.group(1) if m.group(1) == m.group(2) else LINK.format(m.group(1), m.group(2)), text)
    text = re.sub(r'\*\*(.+?)\*\*', r'\1', text)
    # 书名等斜体（*Legend of Mortal*）去掉星号
    text = re.sub(r'(?<![*\w])\*([^*\n]+)\*(?![*\w])', r'\1', text)
    text = re.sub(r'`([^`]+)`', r'\1', text)
    return text


Q, A, NOTE = '问：', '答：', '注意：'

# 不带安装器的包：「安装」一节的标题，以及安装器那段、卸载那段换成的话（各语言手写）
INSTALL_SECTIONS = {'安装', '安裝', 'Install', '導入', '설치'}
UNINSTALL = ('**卸载**', '**卸載**', '**Uninstall**', '**アンインストール**', '**제거**')
RELEASES = 'https://github.com/kevinnie2003/LOMAssistant/releases/latest'
NO_INSTALLER = {
    'README.md': ('**这个压缩包不带安装器**（N 网版）。想用安装器，可以从 [GitHub](%s)下载 `LOMAssistant-Installer.exe`。',
                  '**卸载**：删掉游戏目录里的 `BepInEx\\plugins\\LOMAssistant` 文件夹。存档不受影响。'),
    'README.zh-Hant.md': ('**這個壓縮包不附安裝器**（N 網版）。想用安裝器，可以從 [GitHub](%s)下載 `LOMAssistant-Installer.exe`。',
                          '**卸載**：刪掉遊戲資料夾裡的 `BepInEx\\plugins\\LOMAssistant` 資料夾。存檔不受影響。'),
    'README.en.md': ('**This zip has no installer** (Nexus version). To use the installer, download `LOMAssistant-Installer.exe` from [GitHub](%s).',
                     '**Uninstall**: delete the `BepInEx\\plugins\\LOMAssistant` folder in the game folder. Saves are not touched.'),
    'README.ja.md': ('**この ZIP にはインストーラーが入っていません**（Nexus 版）。インストーラーを使う場合は [GitHub](%s)から `LOMAssistant-Installer.exe` をダウンロードしてください。',
                     '**アンインストール**：ゲームフォルダの `BepInEx\\plugins\\LOMAssistant` フォルダを削除します。セーブデータには影響しません。'),
    'README.ko.md': ('**이 압축 파일에는 설치 프로그램이 없습니다**(Nexus 버전). 설치 프로그램을 쓰려면 [GitHub](%s)에서 `LOMAssistant-Installer.exe`를 내려받으세요.',
                     '**제거**: 게임 폴더의 `BepInEx\\plugins\\LOMAssistant` 폴더를 지우세요. 저장 데이터에는 영향이 없습니다.'),
}


def without_installer(lines, readme):
    """「安装」一节里：写安装器的那段换成去哪下载安装器，卸载那段换成删 Mod 文件夹。"""
    installer, uninstall = NO_INSTALLER[readme]
    installer = installer % RELEASES
    out, inside = [], False
    for line in lines:
        heading = re.match(r'^## (.+)$', line)
        if heading:
            inside = heading.group(1).strip() in INSTALL_SECTIONS
        elif inside and 'Installer.exe' in line:
            line = installer
        elif inside and line.startswith(UNINSTALL):
            line = uninstall
        out.append(line)
    return out


def convert(lines):
    out, skipping, table = [], False, []

    def flush_table():
        rows = [r for r in table[2:]]  # 去掉表头和分隔行
        if rows:
            left = max(width(r[0]) for r in rows)
            for r in rows:
                out.append('  ' + r[0] + ' ' * (left - width(r[0]) + 2) + '　'.join(r[1:]))
        table.clear()

    for raw in lines:
        line = raw.rstrip()
        heading = re.match(r'^(#{1,3}) (.+)$', line)
        if heading:
            if table:
                flush_table()
            level, title = len(heading.group(1)), inline(heading.group(2))
            skipping = level == 2 and title in SKIP_SECTIONS
            if skipping:
                continue
            if level == 1:
                out += [title, '=' * width(title)]
            elif level == 2:
                out += ['\x00', title, '-' * width(title)]  # \x00：二级标题前空两行
            else:
                out += ['', '【' + title + '】']
            continue
        if skipping:
            continue
        stripped = line.strip()
        # 一排徽章（[![名字](图片)](链接)）：每个写成「名字：链接」一行
        badges = re.findall(r'\[!\[([^\]]*)\]\([^)]*\)\]\(([^)]+)\)', stripped)
        if badges and stripped.startswith('[!['):
            # 下载次数这类只是展示用的徽章，说明文件里不写
            out += [f'- {name}{COLON}{link}' for name, link in badges if name not in SKIP_BADGES]
            continue
        # 顶上切换 README 语言的一行，说明文件里不写
        if stripped.count('(README') >= 3:
            continue
        if stripped.startswith('![') or stripped.startswith('<img'):
            continue
        if stripped.startswith('|'):
            table.append([inline(c.strip()) for c in stripped.strip('|').split('|')])
            continue
        if table:
            flush_table()
        if stripped.startswith('>'):
            indent = line[:len(line) - len(line.lstrip())]
            out.append(indent + inline(stripped.lstrip('> ').replace('⚠️ ', NOTE)))
            continue
        # 常见问题「**问？** 答」拆成两行
        qa = re.match(r'^\*\*(.+[？?])\*\* (.+)$', line)
        if qa:
            out += [Q + inline(qa.group(1)), A + inline(qa.group(2))]
            continue
        text = inline(line)
        # 顶上一行「下载 · 网盘 · 视频 · 群」拆成一行一个
        if '　·　' in text:
            out += ['- ' + part.strip().replace('⬇ ', '').replace('▶ ', '') for part in text.split('　·　')]
            continue
        out.append(text)
    if table:
        flush_table()

    # 连续空行只留一行；二级标题前空两行
    result = []
    for line in out:
        if line == '\x00':
            while result and not result[-1].strip():
                result.pop()
            result += ['', '']
        elif line.strip() or (result and result[-1].strip()):
            result.append(line)
    return '\n'.join(result).strip() + '\n'


if __name__ == '__main__':
    src, dst = sys.argv[1], sys.argv[2]
    no_installer = '--no-installer' in sys.argv[3:]
    # 问答、注意的前缀按 README 的语言写
    if src.endswith('README.zh-Hant.md'):
        Q, A, NOTE = '問：', '答：', '注意：'
    elif src.endswith('README.md'):
        Q, A, NOTE = '问：', '答：', '注意：'
    elif src.endswith('README.ja.md'):
        Q, A, NOTE = 'Q：', 'A：', '注意：'
    elif src.endswith('README.ko.md'):
        Q, A, NOTE = 'Q: ', 'A: ', '주의: '
    else:
        Q, A, NOTE = 'Q: ', 'A: ', 'Note: '
        LINK, COLON = '{0} ({1})', ': '
    lines = open(src, encoding='utf-8').read().splitlines()
    if no_installer:
        import os
        lines = without_installer(lines, os.path.basename(src))
    text = convert(lines)
    with open(dst, 'w', encoding='utf-8-sig', newline='\r\n') as f:  # 带 BOM、CRLF：老记事本也不乱码
        f.write(text)
    print('written', dst)
