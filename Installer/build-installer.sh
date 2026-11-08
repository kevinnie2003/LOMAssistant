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
# 安装器自己的版本号，和 Mod 版本无关：只在安装器代码改了时才加。
# 安装器不带 Mod 版本号、不带更新内容（都从压缩包里读），同样的代码编出来一个字节都不变，
# 每次发版放出去的是同一个文件，杀毒软件和 SmartScreen 按文件记的信誉才能一直累积。
INSTALLER_VERSION=1.0.0
printf '[assembly: System.Reflection.AssemblyVersion("%s.0")]\n[assembly: System.Reflection.AssemblyFileVersion("%s.0")]\n[assembly: System.Reflection.AssemblyInformationalVersion("%s")]\n' \
	"$INSTALLER_VERSION" "$INSTALLER_VERSION" "$INSTALLER_VERSION" > obj/InstallerVersion.cs
"$DOTNET" "$CSC" -nologo -noconfig -nostdlib+ -target:winexe -platform:anycpu -langversion:12 -optimize+ -deterministic \
	-win32manifest:app.manifest -out:"bin/咖啡伴侣安装器 Installer.exe" "${refs[@]}" \
	*.cs obj/InstallerVersion.cs ../LOMAssistant/UiTraditional.cs
echo "Built Installer/bin/咖啡伴侣安装器 Installer.exe"
