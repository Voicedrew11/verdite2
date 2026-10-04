#!/usr/bin/env python3
"""Run tools/verdite-core/scripts/inspect_disc.py with this game's config/verdite.json."""
import os, runpy, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
os.environ.setdefault("VERDITE_GAME_ROOT", str(ROOT))
CORE = ROOT / "tools" / "verdite-core" / "scripts"
sys.path.insert(0, str(CORE))
runpy.run_path(str(CORE / "inspect_disc.py"), run_name="__main__")
