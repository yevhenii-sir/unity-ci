"""Set bundleVersion and AndroidBundleVersionCode in a Unity project's ProjectSettings.asset.

Version X.Y.Z maps to code X*1000000 + Y*1000 + Z (1.1.0 -> 1001000).
Without --version the current version is bumped by --bump (patch, minor or
major); --current only reports the current version. Writes version, code and
changed=true|false to $GITHUB_OUTPUT when running in GitHub Actions.
"""
import argparse
import os
import re
import sys
from pathlib import Path

VERSION_RE = re.compile(r"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})$")
BUNDLE_VERSION_RE = re.compile(r"^(  bundleVersion: )(.*)$", re.M)
VERSION_CODE_RE = re.compile(r"^(  AndroidBundleVersionCode: )(\d+)$", re.M)


def parse(version):
    match = VERSION_RE.match(version)
    if not match:
        sys.exit(f"Version '{version}' must be MAJOR.MINOR.PATCH, each 0-999 (e.g. 1.1.0)")
    return tuple(int(part) for part in match.groups())


def to_code(major, minor, patch):
    return major * 1000000 + minor * 1000 + patch


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", default=".", help="the Unity project folder")
    parser.add_argument("--version", default="", help="X.Y.Z; empty = bump the current version")
    parser.add_argument("--bump", default="patch", choices=["patch", "minor", "major"])
    parser.add_argument("--current", action="store_true", help="report the current version, change nothing")
    args = parser.parse_args()

    settings = Path(args.project) / "ProjectSettings" / "ProjectSettings.asset"
    text = settings.read_text(encoding="utf-8")
    current_version = BUNDLE_VERSION_RE.search(text).group(2).strip()
    current_code = int(VERSION_CODE_RE.search(text).group(2))

    major, minor, patch = parse(args.version.strip() or current_version)
    if args.current:
        pass
    elif not args.version.strip():
        if args.bump == "major":
            major, minor, patch = major + 1, 0, 0
        elif args.bump == "minor":
            minor, patch = minor + 1, 0
        else:
            patch += 1
    version = f"{major}.{minor}.{patch}"
    code = to_code(major, minor, patch)

    if code < current_code:
        sys.exit(f"Version {version} ({code}) is lower than current {current_version} ({current_code})")

    changed = version != current_version or code != current_code
    if changed:
        text = BUNDLE_VERSION_RE.sub(lambda m: m.group(1) + version, text, count=1)
        text = VERSION_CODE_RE.sub(lambda m: m.group(1) + str(code), text, count=1)
        settings.write_text(text, encoding="utf-8", newline="")

    print(f"Version {current_version} ({current_code}) -> {version} ({code}){'' if changed else ', unchanged'}")
    output = os.environ.get("GITHUB_OUTPUT")
    if output:
        with open(output, "a", encoding="utf-8") as stream:
            stream.write(f"version={version}\ncode={code}\nchanged={str(changed).lower()}\n")


if __name__ == "__main__":
    main()
