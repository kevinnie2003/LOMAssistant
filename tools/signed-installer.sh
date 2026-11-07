#!/usr/bin/env bash
# 发版时取签名的安装器：在 GitHub Actions 上运行 Sign installer（编译 main 最新提交的安装器并交给 SignPath），
# 等你在 SignPath 网页上批准、签好后下载下来，覆盖 Installer/bin 里本地编译的那份，之后照常运行 tools/pack.py。
# 要先把版本号提交推送到 main。用法：./tools/signed-installer.sh
set -euo pipefail
cd "$(dirname "$0")/.."
WORKFLOW=sign-installer.yml
before="$(gh run list --workflow "$WORKFLOW" -L 1 --json databaseId -q '.[0].databaseId // 0')"
gh workflow run "$WORKFLOW" --ref main
echo "已启动，等待运行出现……"
run="$before"
for _ in $(seq 1 30); do
	sleep 2
	run="$(gh run list --workflow "$WORKFLOW" -L 1 --json databaseId -q '.[0].databaseId // 0')"
	[ "$run" != "$before" ] && break
done
[ "$run" != "$before" ] || { echo "没找到新的运行" >&2; exit 1; }
echo "运行 $run：编译好后请到 SignPath 网页批准签名"
gh run watch "$run" --exit-status
tmp="$(mktemp -d)"
gh run download "$run" -n installer-signed -D "$tmp"
mkdir -p Installer/bin
cp "$tmp/LOMAssistant-Installer.exe" "Installer/bin/咖啡伴侣安装器 Installer.exe"
rm -rf "$tmp"
powershell -NoProfile -Command "(Get-AuthenticodeSignature 'Installer/bin/咖啡伴侣安装器 Installer.exe') | Format-List Status, SignerCertificate"
echo "签名的安装器已放到 Installer/bin，可以运行 tools/pack.py 了"
