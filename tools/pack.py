"""打发布包：把 release/pkg 打成 LOMAssistant.zip、release/咖啡伴侣 vX.Y.Z.zip 和不带安装器的 LOMAssistant-NoInstaller.zip。

用法（仓库根目录）：python -I -X utf8 tools/pack.py 2.8.3
事先要编译好 Mod（./build.sh）和安装器（./Installer/build-installer.sh），并把 bin/LOMAssistant.dll 复制到
release/pkg/BepInEx/plugins/LOMAssistant/。release/ 和仓库在同一个上级文件夹里。
"""
import os
import shutil
import subprocess
import sys
import zipfile

repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
base = os.path.dirname(repo)
version = sys.argv[1]
root = os.path.join(base, 'release', 'pkg')

INSTALLER = '咖啡伴侣安装器 Installer.exe'
# 说明文件：每次从各语言 README 生成，内容和 README 一致
MANUALS = [
    ('README.md', '使用说明.txt'),
    ('README.zh-Hant.md', '使用說明.txt'),
    ('README.ja.md', '説明書（日本語）.txt'),
    ('README.ko.md', '설명서 (한국어).txt'),
    ('README.en.md', 'Readme (English).txt'),
]

# 旧文件名的安装器、说明不要留在包里
for old in ['咖啡伴侣安装器.exe']:
    if os.path.exists(os.path.join(root, old)):
        os.remove(os.path.join(root, old))

# 安装器用仓库里最新编译的（Installer/build-installer.sh 的输出）
shutil.copyfile(os.path.join(repo, 'Installer', 'bin', INSTALLER), os.path.join(root, INSTALLER))
for readme, manual in MANUALS:
    subprocess.run([sys.executable, '-I', '-X', 'utf8', os.path.join(repo, 'tools', 'readme-to-txt.py'),
                    os.path.join(repo, readme), os.path.join(root, manual)], check=True)

out = os.path.join(base, 'LOMAssistant.zip')
with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
    for top in ['.doorstop_version', 'changelog.txt', 'doorstop_config.ini', 'winhttp.dll'] + [m for _, m in MANUALS] + [INSTALLER]:
        z.write(os.path.join(root, top), top)
    for d, dirs, files in os.walk(os.path.join(root, 'BepInEx')):
        dirs.sort()
        rel = os.path.relpath(d, root).replace(os.sep, '/')
        if not files and not dirs:
            z.writestr(rel + '/', '')
        for f in sorted(files):
            z.write(os.path.join(d, f), rel + '/' + f)
shutil.copyfile(out, os.path.join(base, 'release', '咖啡伴侣 v' + version + '.zip'))

# 不带安装器 exe 的包：只传 Nexus（Nexus 会把含 exe 的上传自动隔离），其余和完整包一样
nexus = os.path.join(base, 'LOMAssistant-NoInstaller.zip')
with zipfile.ZipFile(out) as src, zipfile.ZipFile(nexus, 'w', zipfile.ZIP_DEFLATED) as dst:
    for item in src.infolist():
        if not item.filename.endswith('.exe'):
            dst.writestr(item, src.read(item.filename))
print('nexus zip', os.path.getsize(nexus), 'bytes')

# release 文件夹只留这一版的压缩包（网盘上的旧包不动）
for f in os.listdir(os.path.join(base, 'release')):
    if f.endswith('.zip') and f != '咖啡伴侣 v' + version + '.zip':
        os.remove(os.path.join(base, 'release', f))
        print('removed old', f)

# GitHub 上单独提供的安装器（英文文件名）
shutil.copyfile(os.path.join(root, INSTALLER), os.path.join(base, 'LOMAssistant-Installer.exe'))

z = zipfile.ZipFile(out)
print(len(z.infolist()), 'entries')
for i in z.infolist():
    if i.filename.endswith(('LOMAssistant.dll', '.txt', '.exe')):
        print(i.filename, i.file_size, i.date_time)
