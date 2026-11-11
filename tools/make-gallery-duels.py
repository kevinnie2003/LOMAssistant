"""Extract the duel gallery from the installed game, without starting a scene.

Usage: python -X utf8 tools/make-gallery-duels.py <game directory>
Requires UnityPy==1.25.4 and TypeTreeGeneratorAPI==0.0.10.
Only the catalog (not Unity assets) is embedded in the mod. Regenerate after
game updates; source fingerprints are retained for auditing.
"""
import gzip
import hashlib
import json
import pathlib
import re
import sys

import UnityPy
from UnityPy.helpers import TypeTreeHelper
from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator


def extract(game):
    root = pathlib.Path(game) / "Mortal_Data"
    # The accelerated reader on Windows decodes strings using the ANSI locale.
    # Use the Python reader to preserve UTF-8 Chinese names and paths.
    TypeTreeHelper.read_typetree_boost = None
    files = ["globalgamemanagers.assets", "resources.assets", "sharedassets0.assets", "sharedassets6.assets"]
    env = UnityPy.load(*(str(root / name) for name in files))
    gen = TypeTreeGenerator(next(f.unity_version for f in env.files.values() if hasattr(f, "unity_version")))
    gen.load_local_dll_folder(str(root / "Managed"))
    cache = {}

    def read(obj):
        identity = (obj.assets_file.name, obj.path_id)
        if identity not in cache:
            base = obj.read(check_read=False)
            script = base.m_Script.deref().read()
            node = gen.get_nodes_up(script.m_AssemblyName, script.m_Namespace + "." + script.m_ClassName)
            # Generated MonoBehaviour headers omit the enabled-byte padding.
            # Replace only those base nodes with Unity's own header definition.
            from UnityPy.files.ObjectReader import get_typetree_node
            header = get_typetree_node(114, obj.version)
            for i, child in enumerate(node.m_Children):
                match = next((n for n in header.m_Children if n.m_Name == child.m_Name), None)
                if match is not None:
                    node.m_Children[i] = match
            cache[identity] = (script.m_ClassName, obj.read_typetree(node))
        return cache[identity]

    def deref(obj, ptr):
        if not ptr or not ptr["m_PathID"]:
            return None
        from UnityPy.classes.PPtr import PPtr
        return PPtr(m_FileID=ptr["m_FileID"], m_PathID=ptr["m_PathID"], assetsfile=obj.assets_file).deref()

    def data(obj, ptr):
        target = deref(obj, ptr)
        return read(target)[1] if target else None

    objects = list(env.objects)
    combat = next(f for f in env.files.values() if getattr(f, "name", "") == "sharedassets6.assets")
    types = {}
    for obj in combat.objects.values():
        if obj.type.name == "MonoBehaviour":
            base = obj.read(check_read=False)
            script = base.m_Script.deref().read()
            types.setdefault(script.m_ClassName, []).append(obj)

    locale = {}
    for obj in objects:
        if obj.type.name != "TextAsset":
            continue
        text = obj.read()
        if text.m_Name not in ["Talent_zh-cn", "CombatCharacter_zh-cn", "CombatEffects_zh-cn", "CombatInfo_zh-cn"]:
            continue
        for line in text.m_Script.splitlines():
            key, sep, value = line.partition(" = ")
            if sep:
                locale[key.lstrip("\ufeff").strip()] = value.replace("\\n", "\n").strip()

    def effect(obj):
        d = read(obj)[1]
        key = d["_key"]
        return dict(Key=key, Name=locale.get("CombatSkill/Name/" + key, d["m_Name"]),
                    Description=locale.get("CombatSkill/Desc/" + key, d.get("_desc", "")),
                    Rounds=d["_rounds"])

    stats = []
    stat_ids = {}
    for obj in types["CombatStat"]:
        d = read(obj)[1]
        if not d["m_Name"].startswith("Enemy_"):
            continue
        avatar = data(obj, d["CombatAvatar"])
        folders = sorted({str(pathlib.PurePosixPath(avatar[k]).parent.name) for k in
                          ["NormalKey", "AttackKey", "HurtKey", "DefenceKey"] if avatar and avatar[k]})
        talents = []
        for item in d["TalentItems"]:
            talent = data(obj, item["Data"])
            if not talent:
                raise ValueError("Unresolved talent: " + d["m_Name"])
            key = talent["_id"]
            talents.append(dict(Id=key, Level=item["Level"], DisplayLevel=bool(talent["_displayLevel"]),
                                Key=talent["_combatSkillKey"], Priority=talent["_priority"],
                                Name=locale.get("PlayerTalent/Name/" + key, talent["m_Name"]),
                                Description=locale.get("PlayerTalent/Desc/" + key, "")))
        row = dict(Id=d["m_Name"], CharacterId=d["Name"],
                   Name=locale.get("CombatCharacter/Name/" + d["Name"], d["m_Name"][6:]),
                   Folders=folders, Note=d["_note"],
                   Six=[d[k] for k in ["Strength", "Stamina", "Dexterity", "Fist", "Sword", "MartialWeapon"]],
                   Health=d["DefaultHealth"], MaxHealth=d["MaxHealth"], Energy=d["DefaultStamina"],
                   MaxEnergy=d["MaxStamina"], Talking=d["Talking"], Skills=sorted(talents,key=lambda t:t["Priority"]))
        stats.append(row)
        stat_ids[(obj.assets_file.name, obj.path_id)] = row["Id"]

    def condition(obj):
        cls, d = read(obj)
        # Keep the developer's condition name: includes exact comparisons,
        # round numbers and story flags, rather than guessing a story outcome.
        text = d["m_Name"]
        if cls == "CombatEventConditionPlayerStat":
            flag = data(obj, d["_stat"]["_flag"])
            if flag and flag.get("_devNote"):
                text += "（" + flag["_devNote"].replace("\r", "").replace("\n", "；") + "）"
        return text

    def action(obj, trigger, phase):
        if obj is None:
            return None
        cls = obj.read(check_read=False).m_Script.deref().read().m_ClassName
        if cls not in ["CombatEventActionStateEffect", "CombatEventActionRemoveAllStateEffect",
                       "CombatEventActionModifyStat", "CombatEventActionCrazyMode",
                       "CombatEventActionRemoveAllSupport", "CombatEventActionModifyWeaponValue"]:
            return None
        d = read(obj)[1]
        row = dict(Trigger=trigger, Phase=phase, Action=d["m_Name"],
                   Target=d.get("_character", 0), Effect=None, Remove=False)
        if cls == "CombatEventActionStateEffect":
            row["Effect"] = effect(deref(obj, d["_effect"]))
            row["Remove"] = d["_type"] == 2
        return row

    levels = []
    for obj in types["CombatLevel"]:
        d = read(obj)[1]
        enemy = deref(obj, d["_enemyStat"])
        if not enemy:
            raise ValueError("Missing enemy: " + d["m_Name"])
        stat_id = stat_ids[(enemy.assets_file.name, enemy.path_id)]
        events = []
        for ptr in d["Events"]:
            ev = deref(obj, ptr)
            ed = read(ev)[1]
            for ap in ed["_initAction"]:
                row = action(deref(ev, ap), "", "Start")
                if row: events.append(row)
            for cp in ed["_conditionAction"]:
                ca = deref(ev, cp)
                cd = read(ca)[1]
                sep = " AND " if cd["_logic"] == 0 else " OR "
                trigger = sep.join(condition(deref(ca, p)) for p in cd["_conditions"])
                for field, phase in [("_actions", "Round"), ("_afterActions", "Action"), ("_afterResult", "Result")]:
                    for ap in cd[field]:
                        row = action(deref(ca, ap), trigger, phase)
                        if row: events.append(row)
        difficulty = re.search(r"難度\s*(\d+)", d["_desc"])
        levels.append(dict(Id=d["m_Name"], StatId=stat_id, Title=d["_desc"],
                           Difficulty=int(difficulty[1]) if difficulty else 0, Events=events))

    modes = {}
    for obj in types["CombatStateEffectScriptable"]:
        d = read(obj)[1]
        if d["_key"] in ["T001", "T002_1", "T002_2"]:
            modes[d["_key"]] = dict(Effect=effect(obj), Units=[dict(Type=read(deref(obj,p))[0],
                Data=read(deref(obj,p))[1]) for p in d["_units"]])
    sources = {name:hashlib.sha256((root/name).read_bytes()).hexdigest() for name in
               ["sharedassets6.assets", "globalgamemanagers.assets", "Managed/Mortal.Combat.dll"]}
    # Only embed the texture's location and fingerprint, never the game's artwork.
    # At runtime the gallery reads this small range from the user's installed game.
    ui = UnityPy.load(str(root / "sharedassets2.assets"))
    sprite = next(o.read() for o in ui.objects if o.type.name == "Sprite" and o.read().m_Name == "pic_stat_chart_6")
    texture = sprite.m_RD.texture.deref().read()
    stream = texture.m_StreamData
    assert texture.m_MipCount == 1 and sprite.m_RD.textureRect.x == 0 and sprite.m_RD.textureRect.y == 0
    with (root / pathlib.Path(stream.path).name).open("rb") as f:
        f.seek(stream.offset)
        texture_bytes = f.read(stream.size)
    radar = dict(Name=sprite.m_Name, File=pathlib.Path(stream.path).name, Offset=stream.offset, Size=stream.size,
                 Width=texture.m_Width, Height=texture.m_Height, Format=texture.m_TextureFormat,
                 Sha256=hashlib.sha256(texture_bytes).hexdigest())
    result = dict(Schema=1, Sources=sources, Stats=sorted(stats,key=lambda r:r["Id"]),
                  Levels=sorted(levels,key=lambda r:r["Id"]), Modes=modes, Radar=radar)
    raw = json.dumps(result,ensure_ascii=False,separators=(",", ":")).encode("utf-8")
    if b"\xef\xbf\xbd" in raw:
        raise ValueError("Invalid UTF-8 replacement characters in catalog")
    return result, raw


if __name__ == "__main__":
    result, raw = extract(sys.argv[1])
    target = pathlib.Path(__file__).resolve().parent.parent / "LOMAssistant.Features.Extras" / "gallery-duels.json.gz"
    target.write_bytes(gzip.compress(raw,mtime=0))
    print(f"{len(result['Stats'])} enemy profiles, {len(result['Levels'])} story encounters; {target.stat().st_size} bytes")
