#!/usr/bin/env bash
# 游戏更新后跑一次：检查 Mod 和新版游戏还兼不兼容，重新生成内置的决斗数据和条件表，最后列出变了什么。
# 只读游戏目录；改的只有仓库里的两份内置数据（决斗数据、条件表），看过结果再决定提交。
# 用法：DOTNET=/path/to/dotnet.exe ./tools/game-update.sh
#   GAME      游戏目录，默认 D:/Steam/steamapps/common/LegendOfMortal
#   GAMEDATA  解包数据目录（放着 ann.py、dump2.py、dump3.py），默认仓库旁边的 ../gamedata；没有就跳过条件表
# 需要 Python 和 UnityPy==1.25.4、TypeTreeGeneratorAPI==0.0.10（和 make-gallery-duels.py 一样）。
set -uo pipefail
cd "$(dirname "$0")/.."
GAME="${GAME:-D:/Steam/steamapps/common/LegendOfMortal}"
GAMEDATA="${GAMEDATA:-../gamedata}"
M="$GAME/Mortal_Data/Managed"
failed=()

echo "== 游戏版本"
cat "$GAME/Mortal_Data/StreamingAssets/build_info" 2>/dev/null; echo

echo; echo "== 1/5 编译（游戏删改了 Mod 直接用到的类型、方法，这里会报错；编译通过会装进游戏）"
bash build.sh 2>&1 | grep -E " error |Installed" | sort -u
[ "${PIPESTATUS[0]}" -eq 0 ] || failed+=("编译")

echo; echo "== 2/5 按名字找的游戏成员（Harmony 补丁、反射用字符串写的方法名、字段名）"
CSC="$(ls /c/Windows/Microsoft.NET/Framework64/v4*/csc.exe | head -1)"
OUT=tools/game-update/bin
mkdir -p "$OUT"
cp "$GAME/BepInEx/core/Mono.Cecil.dll" "$OUT/"
"$CSC" -nologo -out:"$(cygpath -w "$OUT/check-members.exe")" -r:"$(cygpath -w "$OUT/Mono.Cecil.dll")" "$(cygpath -w tools/game-update/CheckMembers.cs)" >/dev/null \
	&& "$OUT/check-members.exe" "$M" "$(cygpath -w .)" || failed+=("按名字找的成员")

echo; echo "== 3/5 随时存档兼容契约"
./tools/anytime-contract/generate.sh - || failed+=("随时存档契约")

echo; echo "== 4/5 重新生成决斗数据"
python -I -X utf8 tools/make-gallery-duels.py "$GAME" && python -I -X utf8 tools/game-update/diff-data.py 决斗数据 || failed+=("决斗数据")

echo; echo "== 5/5 重新生成条件表"
if [ -f "$GAMEDATA/ann.py" ]; then
	# 旧的解包数据先留一份，目录名带上之前的游戏版本
	build="$(grep -o 'at [0-9/]*' "$GAME/Mortal_Data/StreamingAssets/build_info" | tr -d 'at /')"
	backup="$GAMEDATA/old_before_$build"
	[ -d "$backup" ] || { mkdir -p "$backup" && cp -r "$GAMEDATA/json2" "$GAMEDATA/json3" "$backup/"; }
	(cd "$GAMEDATA" && python -I -X utf8 dump2.py >/dev/null && python -I -X utf8 dump3.py >/dev/null) \
		&& python -I -X utf8 tools/make-conditions.py "$GAMEDATA" && python -I -X utf8 tools/game-update/diff-data.py 条件表 || failed+=("条件表")
else
	echo "没有 $GAMEDATA/ann.py，跳过（条件表只是进剧情场景前的备用，游戏里会现读）"
fi

echo
if [ ${#failed[@]} -eq 0 ]; then
	echo "全部通过。决斗数据、条件表有变化的话，看过上面的列表再提交。"
else
	echo "要处理的：${failed[*]}"
	exit 1
fi
