#!/usr/bin/env bash
# 不需要游戏文件的多语言测试：把界面文字表和 tests/ci/GameStubs.cs 编成一个小程序，用 .NET Framework 跑。GitHub 上跑的就是这个。
# 用法：DOTNET=/path/to/dotnet.exe ./tests/run-language-tests.sh
set -euo pipefail
cd "$(dirname "$0")/.."

DOTNET="${DOTNET:-dotnet}"
SDK_DIR="$(dirname "$(command -v "$DOTNET" || echo "$DOTNET")")/sdk"
CSC="$(find "$SDK_DIR" -path '*Roslyn/bincore/csc.dll' | sort | tail -1)"
FW="${WINDIR:-C:/Windows}/Microsoft.NET/Framework64/v4.0.30319"

mkdir -p bin/language-tests
"$DOTNET" "$CSC" -nologo -noconfig -nostdlib+ -target:exe -platform:x64 -langversion:12 -optimize+ -nowarn:CS0649,CS0414 \
	-out:bin/language-tests/LOMAssistant.LanguageTests.exe \
	-r:"$FW/mscorlib.dll" -r:"$FW/System.dll" -r:"$FW/System.Core.dll" \
	LOMAssistant/UiText.cs LOMAssistant/UiTextEnglish.cs LOMAssistant/UiDescriptions.cs LOMAssistant/UiTraditional.cs \
	LOMAssistant/UiConditionNames.cs LOMAssistant/UiEventNames.cs LOMAssistant/UiFlagNames.cs LOMAssistant/UiQuestNames.cs \
	LOMAssistant/TextConverter.cs tests/ci/GameStubs.cs tests/Program.cs tests/LanguageTests.cs
./bin/language-tests/LOMAssistant.LanguageTests.exe
