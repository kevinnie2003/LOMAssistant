#!/usr/bin/env python3
"""Checks multi-language coverage from the source alone (no game files needed), for CI and for local use.

    python tools/check-i18n.py            exit 1 if anything is missing
    python tools/check-i18n.py --update   rewrite tools/i18n-gaps.txt with today's known gaps

Covers: every setting name has Korean, Japanese and English; every config option has a description in all
three; table rows have matching placeholders; every table row has every language, and every Chinese string
in the code has a translation, interpolated ones included (known gaps live in tools/i18n-gaps.txt, so only
new ones fail); the READMEs list the same features; CHANGELOGs list the same versions; version numbers agree.

Text that never reaches the screen: end the line with `// i18n-ignore`.
"""
import glob
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CJK = re.compile(r'[一-鿿]')
STRING = r'"((?:[^"\\]|\\.)*)"'
STRING_OR_NULL = r'(?:' + STRING + r'|null)'
ROW = re.compile(r'new\[\]\s*\{\s*' + STRING + r'\s*,\s*' + STRING_OR_NULL + r'\s*,\s*' + STRING_OR_NULL + r'\s*\}')
ENTRY = re.compile(r'\[' + STRING + r'\]\s*=\s*' + STRING)
SETTING = re.compile(r'ConfigElement<[^>]+>\("(\w+)"')
ROW_CALL = re.compile(r'\bRow\(ConfigManager\.\w+,\s*' + STRING)
DESCRIPTION = re.compile(r'\{\s*"(\w+)",\s*new\[\]\s*\{\s*' + STRING + r'\s*,\s*' + STRING + r'\s*\}\s*\}')
HOLE = re.compile(r'\{\d+\}')
GAPS = os.path.join('tools', 'i18n-gaps.txt')
# 不显示给玩家：日志、自测、诊断；文字表本身；AI 提示词（自带四语言，由 AiTests 查）
SKIP_FILES = re.compile(r'^(?:Ui(?:Text|TextEnglish|Descriptions|Traditional|ConditionNames|EventNames|FlagNames|QuestNames)\.cs'
                        r'|Pinyin.*|SelfTest.*|Diagnostics.*|FileLogger\.cs|BattleLogPatch\.cs'
                        r'|Ai(?:Personas|Prompts|Knowledge|Items|Analysis|Eval|Debug).*)$')
NOT_SHOWN = re.compile(r'FileLogger\.|LogInfo\(|LogWarning\(|LogError\(|Debug\.Log|\bthrow\b|ConfigElement<|i18n-ignore|new Regex\(|Console\.Write')
# 这些位置的中文是用来比较游戏数据的，不显示
COMPARED = re.compile(r'(?:Contains|StartsWith|EndsWith|IndexOf|LastIndexOf|Replace|Split|TrimEnd|TrimStart|Trim|Regex\.\w+|IsMatch|Match)\(\s*(?:[^"()]*,\s*)?$|(?:==|!=|\bcase|\bis)\s*$')


def read(*parts):
    with open(os.path.join(ROOT, *parts), encoding='utf-8-sig') as f:
        return f.read()


def holes(text):
    return sorted(set(HOLE.findall(text or '')))


def section(text, name):
    """The body of a `name = new ...` initializer, up to the closing brace at the same indent."""
    start = re.search(r'\b' + name + r'\s*=\s*(?:new|\n)', text)
    if not start:
        raise SystemExit('table not found: ' + name)
    end = text.find('\n\t};', start.end())
    return text[start.end():end if end >= 0 else len(text)]


def tables():
    text = read('LOMAssistant', 'UiText.cs')
    return {name: ROW.findall(section(text, name)) for name in ('Terms', 'Table')}


def english():
    text = read('LOMAssistant', 'UiTextEnglish.cs')
    return {name: ENTRY.findall(section(text, name)) for name in ('Terms', 'Table', 'Descriptions')}


def check_tables(problems):
    rows = tables()
    for name, table in rows.items():
        seen = set()
        for key, korean, japanese in table:
            if key in seen:
                problems.append(f'UiText.{name}: duplicate key {key}')
            seen.add(key)
            for label, value in (('Korean', korean), ('Japanese', japanese)):
                if value and holes(value) != holes(key):
                    problems.append(f'UiText.{name}: {label} placeholders differ: {key} -> {value}')
    en = english()
    for name in ('Terms', 'Table'):
        seen = set()
        for key, value in en[name]:
            if key in seen:
                problems.append(f'UiTextEnglish.{name}: duplicate key {key}')
            seen.add(key)
            if holes(value) != holes(key):
                problems.append(f'UiTextEnglish.{name}: placeholders differ: {key} -> {value}')


def known_keys():
    keys = {}
    for table in tables().values():
        for key, korean, japanese in table:
            keys[key] = (korean, japanese)
    return keys


def known_english():
    en = english()
    return {key for name in ('Terms', 'Table') for key, _ in en[name]}


def check_settings(problems):
    keys = known_keys()
    en = known_english()
    code = read('LOMAssistant.Features.Story', 'StoryReviewPanel.Settings.cs')
    for name in ROW_CALL.findall(code):
        if name not in keys or not keys[name][0]:
            problems.append(f'setting name has no Korean: {name}')
        if name not in keys or not keys[name][1]:
            problems.append(f'setting name has no Japanese: {name}')
        if name not in en:
            problems.append(f'setting name has no English: {name}')

    shown = re.findall(r'\bRow\(ConfigManager\.(\w+),', code)
    en_descriptions = {key for key, _ in english()['Descriptions']}
    descriptions = {key: (ko, ja) for key, ko, ja in DESCRIPTION.findall(read('LOMAssistant', 'UiDescriptions.cs'))}
    settings = set(SETTING.findall(read('LOMAssistant.Config', 'ConfigManager.cs')))
    for setting in shown:
        if setting not in settings:
            continue
        if setting not in en_descriptions:
            problems.append(f'setting has no English description: {setting}')
        if setting not in descriptions:
            problems.append(f'setting has no Korean/Japanese description: {setting}')


def check_docs(problems):
    readmes = ['README.md', 'README.zh-Hant.md', 'README.en.md', 'README.ja.md', 'README.ko.md']

    # 功能那几节五种语言要一致；安装、开发、致谢各语言写法不同，不比
    def shape(name):
        lines = read(name).splitlines()
        heads = [i for i, l in enumerate(lines) if l.startswith('## ')]
        lines = lines[heads[2]:heads[3]]
        return (sum(l.startswith('#') for l in lines), sum(l.startswith('<img') or '![' in l for l in lines),
                sum(l.lstrip().startswith('- ') for l in lines))

    base = shape(readmes[0])
    for name in readmes[1:]:
        now = shape(name)
        if now != base:
            problems.append(f'{name}: feature section has headings/images/bullets {now}, README.md has {base}')

    versions = {}
    for name in ('CHANGELOG.md', 'CHANGELOG.en.md', 'CHANGELOG.ja.md', 'CHANGELOG.ko.md'):
        versions[name] = re.findall(r'(?m)^## (\d+\.\d+\.\d+)', read(name))
    base = versions['CHANGELOG.md']
    for name, found in versions.items():
        if found[:len(base)] != base and base[:len(found)] != found:
            problems.append(f'{name}: version list differs from CHANGELOG.md, first difference near {next((b for a, b in zip(found, base) if a != b), "the end")}')

    plugin = re.search(r'BepInPlugin\([^)]*"(\d+\.\d+\.\d+)"\)', read('LOMAssistant', 'LOMAssistantPlugin.cs')).group(1)
    assembly = re.search(r'AssemblyVersion\("(\d+\.\d+\.\d+)\.0"\)', read('Properties', 'AssemblyInfo.cs')).group(1)
    if plugin != assembly:
        problems.append(f'plugin version {plugin}, assembly version {assembly}')


# 和 UiText.Translate 一样拆开：富文本标签、换行、「 · 」、全角空格
SPLIT = re.compile(r'</?(?:b|i|size|color|material|quad)(?:[= ][^<>]*)?>|\\n|\\r| · |　·　|　+| → ')
NOT_TEXT = re.compile(r'^[A-Za-z]+\d*_|^\||\\[SdwWs]|\(\?|^\^')
PLAIN = ' \t\u3000'


def parts_of(text):
    return [part.strip(PLAIN) for part in SPLIT.split(text) if CJK.search(part)]


def matcher(keys):
    """Is this text covered: whole, or part by part, by exact rows or {0} patterns."""
    exact = set(keys)
    patterns = [re.compile('^' + re.sub(r'\\\{\d\\\}', '(.+?)', re.escape(key)) + '$') for key in keys if '{0}' in key]

    def covered(text):
        return text in exact or all(part in exact or any(p.match(part) for p in patterns) for part in parts_of(text))
    return covered


def read_string(line, i, interpolated, verbatim, found):
    """Reads the literal opening at line[i]; holes in an interpolated one become {0}, {1}, ... as in the tables."""
    j = i + 1
    text = []
    holes = 0
    while j < len(line):
        c = line[j]
        if verbatim and line[j:j + 2] == '""':
            text.append('\\"')
            j += 2
        elif not verbatim and c == '\\':
            text.append(line[j:j + 2])
            j += 2
        elif c == '"':
            return j + 1, ''.join(text)
        elif interpolated and line[j:j + 2] in ('{{', '}}'):
            text.append(c)
            j += 2
        elif interpolated and c == '{':
            depth = 1
            j += 1
            while j < len(line) and depth:
                if line[j] == '"':
                    start = j
                    j, inner = read_string(line, j, False, False, found)
                    found.append((line[:start], inner))
                    continue
                depth += {'{': 1, '}': -1}.get(line[j], 0)
                j += 1
            text.append('{%d}' % holes)
            holes += 1
        else:
            text.append(c)
            j += 1
    return j, ''.join(text)


def literals_in(line):
    """Every string literal on a line, as (code before it, text)."""
    found = []
    i = 0
    while i < len(line):
        if line[i:i + 2] == '//':
            break
        char = re.match(r"'(?:\\.|[^'\\])'", line[i:])
        if char:
            i += char.end()
        elif line[i] == '"':
            start = i
            prefix = line[max(0, i - 2):i]
            i, text = read_string(line, i, '$' in prefix, '@' in prefix, found)
            found.append((line[:start].rstrip('$@'), text))
        else:
            i += 1
    return found


def display_gaps():
    """What has no translation, as `language<TAB>text` lines."""
    keys = known_keys()
    en = known_english()
    korean_ok = matcher([key for key, row in keys.items() if row[0]])
    japanese_ok = matcher([key for key, row in keys.items() if row[1]])
    english_ok = matcher(en)
    gaps = set()
    for path in glob.glob(os.path.join(ROOT, 'LOMAssistant*', '**', '*.cs'), recursive=True):
        if SKIP_FILES.match(os.path.basename(path)):
            continue
        with open(path, encoding='utf-8-sig') as f:
            for line in f:
                if NOT_SHOWN.search(line) or line.lstrip().startswith('//'):
                    continue
                for before, text in literals_in(line):
                    if not CJK.search(text) or COMPARED.search(before) or NOT_TEXT.search(text):
                        continue
                    if not korean_ok(text):
                        gaps.add('Korean\t' + text)
                    if not japanese_ok(text):
                        gaps.add('Japanese\t' + text)
                    if not english_ok(text):
                        gaps.add('English\t' + text)
    for key, korean, japanese in tables()['Table']:
        if not korean:
            gaps.add('Korean\t' + key)
        if not japanese:
            gaps.add('Japanese\t' + key)
        if key not in en:
            gaps.add('English\t' + key)
    return gaps


def check_display(problems, update):
    path = os.path.join(ROOT, GAPS)
    gaps = display_gaps()
    if update:
        with open(path, 'w', encoding='utf-8', newline='\n') as f:
            f.write(''.join(line + '\n' for line in sorted(gaps)))
        return
    with open(path, encoding='utf-8') as f:
        known = set(f.read().splitlines())
    for line in sorted(gaps - known):
        problems.append('no translation: ' + line.replace('\t', ': '))


def main():
    problems = []
    check_tables(problems)
    check_settings(problems)
    check_docs(problems)
    check_display(problems, '--update' in sys.argv)
    for problem in problems:
        print(problem)
    print(f'{len(problems)} problems')
    return 1 if problems else 0


if __name__ == '__main__':
    sys.exit(main())
