"""Check a project's .github/unity-ci.json for the release workflow.

Usage: project.py CONFIG_FILE PLATFORM BUILD. Fails when the project has no settings for
the requested platform and writes team_id to $GITHUB_OUTPUT.
"""
import json
import os
import sys
from pathlib import Path

path, platform, build = sys.argv[1:4]
project = json.loads(Path(path).read_text(encoding="utf-8"))

if build == "Debug" and platform != "android":
    sys.exit("Debug builds are Android only")
platforms = ["android", "ios"] if platform == "both" else [platform]
missing = [p for p in platforms if p not in project]
if missing:
    sys.exit(f"{path} has no {' or '.join(missing)} settings")

team_id = project.get("ios", {}).get("team_id", "")
with open(os.environ.get("GITHUB_OUTPUT", os.devnull), "a", encoding="utf-8") as stream:
    stream.write(f"team_id={team_id}\n")
