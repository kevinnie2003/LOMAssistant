#!/usr/bin/env bash
# Builds LOMAssistant.dll with Roslyn directly (MSBuild hangs on this machine)
# against the game's own assemblies, and installs it into BepInEx/plugins.
# Usage: DOTNET=/path/to/dotnet.exe ./build.sh   (needs a .NET 8 SDK)
set -euo pipefail
cd "$(dirname "$0")"

GAME="${GAME:-D:/Steam/steamapps/common/LegendOfMortal}"
DOTNET="${DOTNET:-dotnet}"
SDK_DIR="$(dirname "$(command -v "$DOTNET" || echo "$DOTNET")")/sdk"
CSC="$(find "$SDK_DIR" -path '*Roslyn/bincore/csc.dll' | sort | tail -1)"
M="$GAME/Mortal_Data/Managed"
C="$GAME/BepInEx/core"
P="$GAME/BepInEx/plugins/LOMAssistant"

refs=()
for d in "$M"/*.dll "$C/BepInEx.Core.dll" "$C/BepInEx.Unity.Mono.dll" "$C/0Harmony.dll" "$C/Mono.Cecil.dll" "$P/Newtonsoft.Json.dll"; do
	refs+=("-r:$d")
done

mkdir -p bin
# BepInEx 5 插件兼容层（名叫 BepInEx 的小程序集），嵌进 Mod 里，见 LOMAssistant/BepInEx5Plugins.cs
"$DOTNET" "$CSC" -nologo -noconfig -nostdlib+ -target:library -langversion:12 -optimize+ -deterministic -out:bin/BepInEx.dll \
	-r:"$M/mscorlib.dll" -r:"$M/netstandard.dll" -r:"$M/System.dll" -r:"$M/System.Core.dll" -r:"$M/UnityEngine.CoreModule.dll" -r:"$C/BepInEx.Core.dll" \
	tools/bepinex5-shim/BepInEx5Shim.cs
"$DOTNET" "$CSC" -nologo -noconfig -nostdlib+ -target:library -langversion:12 -optimize+ -deterministic \
	-nowarn:CS1701,CS1702 -out:bin/LOMAssistant.dll "${refs[@]}" \
	-resource:LOMAssistant.Features.AnytimeSave/Replay.lua,LOMAssistant.AnytimeSave.Replay.lua \
	-resource:LOMAssistant.Features.AnytimeSave/contract.tsv.gz,LOMAssistant.AnytimeSave.contract.tsv.gz 	-resource:LOMAssistant.Features.AnytimeSave/contract-6000.tsv.gz,LOMAssistant.AnytimeSave.contract-6000.tsv.gz \
	-resource:LOMAssistant/conditions.json.gz,LOMAssistant.conditions.json.gz \
	-resource:CHANGELOG.md,LOMAssistant.CHANGELOG.md \
	-resource:CHANGELOG.ja.md,LOMAssistant.CHANGELOG.ja.md \
	-resource:CHANGELOG.ko.md,LOMAssistant.CHANGELOG.ko.md \
	-resource:bin/BepInEx.dll,LOMAssistant.BepInEx5Shim.dll \
	$(find . -name '*.cs' -not -path './bin/*' -not -path './Installer/*' -not -path './tools/*' -not -path './tests/*')

cp bin/LOMAssistant.dll "$P/LOMAssistant.dll"
echo "Installed to $P/LOMAssistant.dll"
