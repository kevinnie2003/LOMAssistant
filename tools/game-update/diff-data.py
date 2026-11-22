"""对比重新生成的内置数据和仓库里提交过的版本，列出变了哪些。用法见 tools/game-update.sh。"""
import gzip
import json
import subprocess
import sys

FILES = {
    "决斗数据": "LOMAssistant.Features.Extras/gallery-duels.json.gz",
    "条件表": "LOMAssistant/conditions.json.gz",
}


def load(path, committed):
    if committed:
        raw = subprocess.run(["git", "show", "HEAD:" + path], capture_output=True, check=True).stdout
        return json.loads(gzip.decompress(raw))
    return json.loads(gzip.open(path).read())


def keyed(value):
    # 列表里的条目按 Id 对齐（新增一个敌人时后面的不会全算成变化）
    if isinstance(value, list) and value and all(isinstance(v, dict) and "Id" in v for v in value):
        return {v["Id"]: v for v in value}
    return value


def compare(name, path):
    old, new = load(path, True), load(path, False)
    if old == new:
        print(f"{name}：和提交过的一样")
        return
    print(f"{name}：")
    for key in sorted(set(old) | set(new)):
        o, n = keyed(old.get(key)), keyed(new.get(key))
        if o == n:
            continue
        if isinstance(o, dict) and isinstance(n, dict):
            added = sorted(set(n) - set(o))
            removed = sorted(set(o) - set(n))
            changed = sorted(k for k in set(o) & set(n) if o[k] != n[k])
            parts = []
            if added:
                parts.append(f"新增 {len(added)}（{'、'.join(map(str, added[:8]))}{'…' if len(added) > 8 else ''}）")
            if removed:
                parts.append(f"去掉 {len(removed)}（{'、'.join(map(str, removed[:8]))}{'…' if len(removed) > 8 else ''}）")
            if changed:
                parts.append(f"改了 {len(changed)}（{'、'.join(map(str, changed[:8]))}{'…' if len(changed) > 8 else ''}）")
            print(f"  {key}：{'；'.join(parts)}")
        else:
            print(f"  {key}：变了")


for name, path in FILES.items():
    if len(sys.argv) > 1 and name not in sys.argv[1:]:
        continue
    compare(name, path)
