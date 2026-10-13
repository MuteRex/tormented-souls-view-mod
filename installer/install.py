#!/usr/bin/env python3
"""Install or remove TSViewMod in a Tormented Souls (Unity Mono) install.

  install.py install   [--game DIR] [--dll PATH]
  install.py uninstall [--game DIR]

Install copies TSViewMod.dll into <game>/TormentedSouls_Data/Managed and registers it in
ScriptingAssemblies.json + RuntimeInitializeOnLoads.json so Unity calls TSViewMod.Loader.Init.
The two JSON files are backed up once as *.tsviewmod-orig before the first change.
"""
import argparse
import json
import shutil
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_GAME = Path("/mnt/media/Games/Tormented Souls")
# Release zip: DLL and map cache sit next to this script. Repo: build output and data/.
DLL_CANDIDATES = [HERE / "TSViewMod.dll", HERE.parent / "TSViewMod/bin/Release/net472/TSViewMod.dll"]
CACHE_CANDIDATES = [HERE / "TSViewMod_mapcache.txt", HERE.parent / "data/TSViewMod_mapcache.txt"]
ASSEMBLY = "TSViewMod"
ENTRY = {
    "assemblyName": ASSEMBLY,
    "nameSpace": "TSViewMod",
    "className": "Loader",
    "methodName": "Init",
    "loadTypes": 0,
    "isUnityClass": False,
}


def data_dir(game: Path) -> Path:
    d = game / "TormentedSouls_Data"
    if not (d / "Managed" / "Assembly-CSharp.dll").exists():
        raise SystemExit(f"not a Tormented Souls install: {game}\npass the game folder with --game \"/path/to/Tormented Souls\"")
    return d


def backup_once(path: Path) -> None:
    orig = path.with_name(path.name + ".tsviewmod-orig")
    if not orig.exists():
        shutil.copy2(path, orig)


def write_json(path: Path, data) -> None:
    tmp = path.with_name(path.name + ".tmp")
    tmp.write_text(json.dumps(data, separators=(",", ":")), encoding="utf-8")
    tmp.replace(path)


def install(game: Path, dll: Path | None) -> None:
    d = data_dir(game)
    dll = dll or next((c for c in DLL_CANDIDATES if c.exists()), None)
    if dll is None or not dll.exists():
        raise SystemExit("TSViewMod.dll not found: build the mod first or pass --dll")
    shutil.copy2(dll, d / "Managed" / dll.name)

    # Pre-computed map fits, so the map dot works in every room straight away. Never overwrites the player's own.
    cache = next((c for c in CACHE_CANDIDATES if c.exists()), None)
    if cache is not None and not (d / cache.name).exists():
        shutil.copy2(cache, d / cache.name)

    sa_path = d / "ScriptingAssemblies.json"
    sa = json.loads(sa_path.read_text(encoding="utf-8"))
    if f"{ASSEMBLY}.dll" not in sa["names"]:
        backup_once(sa_path)
        sa["names"].append(f"{ASSEMBLY}.dll")
        sa["types"].append(16)
        write_json(sa_path, sa)

    ri_path = d / "RuntimeInitializeOnLoads.json"
    ri = json.loads(ri_path.read_text(encoding="utf-8"))
    if not any(e.get("assemblyName") == ASSEMBLY for e in ri["root"]):
        backup_once(ri_path)
        ri["root"].append(ENTRY)
        write_json(ri_path, ri)
    print(f"installed {dll.name} into {d / 'Managed'}")


def uninstall(game: Path) -> None:
    d = data_dir(game)
    sa_path = d / "ScriptingAssemblies.json"
    sa = json.loads(sa_path.read_text(encoding="utf-8"))
    pairs = [(n, t) for n, t in zip(sa["names"], sa["types"]) if n != f"{ASSEMBLY}.dll"]
    sa["names"], sa["types"] = [p[0] for p in pairs], [p[1] for p in pairs]
    write_json(sa_path, sa)

    ri_path = d / "RuntimeInitializeOnLoads.json"
    ri = json.loads(ri_path.read_text(encoding="utf-8"))
    ri["root"] = [e for e in ri["root"] if e.get("assemblyName") != ASSEMBLY]
    write_json(ri_path, ri)

    (d / "Managed" / f"{ASSEMBLY}.dll").unlink(missing_ok=True)
    print("uninstalled (TSViewMod.cfg left in place)")


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("action", choices=["install", "uninstall"])
    p.add_argument("--game", type=Path, default=DEFAULT_GAME)
    p.add_argument("--dll", type=Path)
    a = p.parse_args()
    if a.action == "install":
        install(a.game, a.dll)
    else:
        uninstall(a.game)


if __name__ == "__main__":
    main()
