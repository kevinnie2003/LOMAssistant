#!/usr/bin/env bash
# Builds the one-click installer (Windows Forms, .NET Framework 4.5+, which ships with Windows)
# with Roslyn against the system's .NET Framework assemblies.
# Usage: DOTNET=/path/to/dotnet.exe ./Installer/build-installer.sh   -> Installer/bin/咖啡伴侣安装器 Installer.exe
set -euo pipefail
cd "$(dirname "$0")"
DOTNET="${DOTNET:-dotnet}"
SDK_DIR="$(dirname "$(command -v "$DOTNET" || echo "$DOTNET")")/sdk"
CSC="$(find "$SDK_DIR" -path '*Roslyn/bincore/csc.dll' | sort | tail -1)"
FW="${FW:-/c/Windows/Microsoft.NET/Framework64/v4.0.30319}"
refs=()
for n in mscorlib System System.Core System.Drawing System.Windows.Forms System.IO.Compression System.IO.Compression.FileSystem; do
	refs+=("-r:$FW/$n.dll")
done
mkdir -p bin obj
# 版本号跟 Mod 一致，写进 exe 的文件信息
VERSION="$(sed -n 's/.*AssemblyVersion("\([0-9.]*\)").*/\1/p' ../Properties/AssemblyInfo.cs | head -1)"
printf '[assembly: System.Reflection.AssemblyVersion("%s")]\n[assembly: System.Reflection.AssemblyFileVersion("%s")]\n[assembly: System.Reflection.AssemblyInformationalVersion("%s")]\n' \
	"$VERSION" "$VERSION" "${VERSION%.0}" > obj/InstallerVersion.cs
"$DOTNET" "$CSC" -nologo -noconfig -nostdlib+ -target:winexe -platform:anycpu -langversion:12 -optimize+ -deterministic \
	-win32manifest:app.manifest -out:"bin/咖啡伴侣安装器 Installer.exe" "${refs[@]}" \
	-resource:../CHANGELOG.md,Installer.CHANGELOG.md -resource:../CHANGELOG.en.md,Installer.CHANGELOG.en.md -resource:../CHANGELOG.ja.md,Installer.CHANGELOG.ja.md -resource:../CHANGELOG.ko.md,Installer.CHANGELOG.ko.md *.cs obj/InstallerVersion.cs ../LOMAssistant/UiTraditional.cs
echo "Built Installer/bin/咖啡伴侣安装器 Installer.exe"
