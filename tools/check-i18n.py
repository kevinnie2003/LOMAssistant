#!/usr/bin/env python3
"""Checks multi-language coverage from the source alone (no game files needed), for CI and for local use.

    python tools/check-i18n.py            exit 1 if anything is missing
    python tools/check-i18n.py --update   rewrite tools/i18n-gaps.txt with today's known gaps

Covers: every setting name has Korean, Japanese and English; every config option has a description in all
three; table rows have matching placeholders; every table row has every language, and Chinese text in the
UI files has a translation (known gaps live in tools/i18n-gaps.txt, so only new ones fail); the READMEs list
the same features; CHANGELOGs list the same versions; version numbers agree.
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
DISPLAY = re.compile(r'\b(?:Label|Button|Box|ForDisplay|Notice|Toast|Golden|CloneButton|CloneInto)\(\s*(?:[^"()]*,\s*)?(?<!\$)' + STRING)
GAPS = os.path.join('tools', 'i18n-gaps.txt')
UI_FILES = [os.path.join('LOMAssistant.Features.Story', 'StoryReviewPanel*.cs'), os.path.join('LOMAssistant.Features.Memo', 'MemoPanel.cs'),
            os.path.join('LOMAssistant.Features.Extras', 'SaveBrowser.cs'), os.path.join('LOMAssistant', 'EventPicker.cs'),
            os.path.join('LOMAssistant', 'MapEventHints.cs'), os.path.join('LOMAssistant', 'OptionInfoPatch.cs')]
LOGGING = re.compile(r'FileLogger\.|LogInfo\(|LogWarning\(|LogError\(|Debug\.Log|\bthrow\b')


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


def display_gaps():
    """What has no translation, as `language<TAB>text` lines."""
    keys = known_keys()
    en = known_english()
    gaps = set()

    def need(text):
        if text not in keys:
            gaps.add('Korean/Japanese\t' + text)
        if text not in en:
            gaps.add('English\t' + text)

    for path in glob.glob(os.path.join(ROOT, 'LOMAssistant*', '**', '*.cs'), recursive=True):
        with open(path, encoding='utf-8-sig') as f:
            for line in f:
                for text in DISPLAY.findall(line.split(' //')[0]):
                    if CJK.search(text):
                        need(text)
    for pattern in UI_FILES:
        for path in glob.glob(os.path.join(ROOT, pattern)):
            with open(path, encoding='utf-8-sig') as f:
                for line in f:
                    body = line.split(' //')[0]
                    if LOGGING.search(body) or body.lstrip().startswith('///'):
                        continue
                    for text in re.findall(r'(?<!\$)' + STRING, body):
                        if CJK.search(text):
                            need(text)
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
