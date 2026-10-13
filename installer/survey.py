#!/usr/bin/env python3
"""Run TSViewMod's unattended map survey, then put everything back.

  survey.py [--game DIR] [--compat DIR] [--appid URL]

1. Refuses to start if Tormented Souls is already running.
2. Installs the current build, backs up the saves and the Wine registry (the survey runs in a tiny window and the
   game's own scripts may autosave while it teleports around).
3. Pauses the ts-save-backup.path watcher so survey saves don't fill the backup history.
4. Writes the survey flag and launches the game through Steam, at low CPU priority; the mod continues the save,
   visits every room on the map, writes TSViewMod_survey_report.txt and quits by itself (about 5-10 minutes).
5. Restores the saves and registry, re-enables the watcher and prints the report.
"""
import argparse
import os
import shutil
import subprocess
import sys
import time
from datetime import datetime
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_GAME = Path("/mnt/media/Games/Tormented Souls")
DEFAULT_COMPAT = Path.home() / ".steam/debian-installation/steamapps/compatdata/4250254510"
DEFAULT_URL = "steam://rungameid/18254704120160059392"
SAVES_REL = "pfx/drive_c/users/steamuser/AppData/LocalLow/duale-abstractd/Tormented Souls Release/Saves"
EXE = "TormentedSouls.exe"
WATCHER = "ts-save-backup.path"


def game_pids() -> list[int]:
    pids = []
    for entry in Path("/proc").iterdir():
        if not entry.name.isdigit() or int(entry.name) == os.getpid():
            continue
        try:
            argv0 = (entry / "cmdline").read_bytes().split(b"\0")[0].decode(errors="replace")
        except OSError:
            continue
        if argv0.replace("\\", "/").endswith(EXE):
            pids.append(int(entry.name))
    return pids


def systemctl(*args: str) -> None:
    subprocess.run(["systemctl", "--user", *args], check=False, capture_output=True)


def main() -> None:
    p = argparse.ArgumentParser()
    p.add_argument("--game", type=Path, default=DEFAULT_GAME)
    p.add_argument("--compat", type=Path, default=DEFAULT_COMPAT)
    p.add_argument("--url", default=DEFAULT_URL)
    p.add_argument("--max-minutes", type=float, default=30)
    a = p.parse_args()

    if game_pids():
        sys.exit("Tormented Souls is running. Close it first (save your game!), then run this again.")

    data = a.game / "TormentedSouls_Data"
    saves = a.compat / SAVES_REL
    user_reg = a.compat / "pfx/user.reg"
    report = data / "TSViewMod_survey_report.txt"
    flag = data / "TSViewMod_survey.flag"

    subprocess.run([sys.executable, str(HERE / "install.py"), "install", "--game", str(a.game)], check=True)

    backup = Path.home() / "Games-backups" / f"TormentedSouls-survey_{datetime.now():%Y-%m-%d_%H%M%S}"
    backup.mkdir(parents=True)
    shutil.copytree(saves, backup / "Saves")
    shutil.copy2(user_reg, backup / "user.reg")
    print(f"backed up saves + registry to {backup}")

    systemctl("stop", WATCHER)
    report.unlink(missing_ok=True)
    flag.write_text("survey\n")
    try:
        print("launching the game for the survey (small window, muted, low priority)...")
        subprocess.Popen(["steam", a.url], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)

        deadline = time.time() + 180
        while not game_pids() and time.time() < deadline:
            time.sleep(2)
        if not game_pids():
            print("the game didn't start within 3 minutes")
        for pid in game_pids():
            try:
                os.setpriority(os.PRIO_PROCESS, pid, 15)
            except OSError:
                pass

        deadline = time.time() + a.max_minutes * 60
        last = ""
        while game_pids() and time.time() < deadline:
            time.sleep(5)
            if report.exists() and not last:
                last = "report written"
                print("survey finished, waiting for the game to close...")
        if game_pids():
            print(f"still running after {a.max_minutes:.0f} min; close the game window, then press Enter to restore")
            input()
            while game_pids():
                time.sleep(2)
    finally:
        flag.unlink(missing_ok=True)
        if game_pids():
            print("waiting for the game to close before restoring (close its window if needed)...")
            while game_pids():
                time.sleep(2)
        time.sleep(3)
        shutil.rmtree(saves)
        shutil.copytree(backup / "Saves", saves)
        shutil.copy2(backup / "user.reg", user_reg)
        systemctl("start", WATCHER)
        print("restored your saves and settings")

    print()
    print(report.read_text() if report.exists() else "no report was written; see Player.log ([TSViewMod] survey lines)")


if __name__ == "__main__":
    main()
