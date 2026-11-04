"""Shared Unity build service on RunPod Serverless.

One queue endpoint per Unity version, each on its own worker image. Deployed
endpoints keep the image cached on their workers, so a build does not wait for
the ~9 GB pull. Projects build through the hub's release workflow (see scripts/request_build.py):

    runpod.Queue(app="unity-builder", name="android_6000_3_8f1").remote(...)

Deploy:  rp flash deploy builder --python-version 3.10   (from the hub root)
Test:    rp flash dev <hub>/builder/unity_builder.py --once   (from a Unity project folder:
         builds its committed HEAD, no GitHub)

Adding a Unity version: build its images (android and ios GameCI tags) with the
"Worker image" workflow and add endpoints below. Adding a project: add its secrets to
SECRETS with its prefix.
"""
import os

import runpod
from runpod import App, Secret

IMAGE = "ghcr.io/yevhenii-sir/unity-runpod:ubuntu-{}"
# Printed first in every build log, to tell which deployed code a worker runs.
REVISION = "play-upload-1"

# Project secrets live in RunPod with a project prefix; a request names its prefix
# and the worker maps PREFIX_KEYSTORE_PASS to ANDROID_KEYSTORE_PASS and so on.
SECRETS = {
    # Dedicated CI Unity account (Personal, no 2FA); activated per build, returned after.
    "UNITY_EMAIL": Secret("unity-email"),
    "UNITY_PASSWORD": Secret("unity-password"),
    # MergeBalls
    "MERGEBALLS_KEYSTORE_PASS": Secret("mergeballs-keystore-pass"),
    "MERGEBALLS_KEYALIAS_PASS": Secret("mergeballs-keyalias-pass"),
    "MERGEBALLS_PLAY_SERVICE_ACCOUNT": Secret("mergeballs-play-service-account"),
}

COMMON = dict(
    # 5 GHz, 16 vCPU, 64 GB RAM. IL2CPP compiles C++ with one clang per core at 1-2 GB
    # each, so cores need about 4 GB of RAM apiece: 16 vCPU / 32 GB ran out of memory.
    # 32 vCPU / 128 GB built MergeBalls in 14.7 min (~$0.43); this size trades a few
    # minutes for half the price. No datacenter pin: 5 GHz CPUs only exist in EU-RO-1
    # and EUR-IS-1, and the SDK does not know EUR-IS-1 yet.
    cpu="cpu5g-16-64",
    workers=(0, 1),             # scales to zero; the image stays cached on the worker
    idle_timeout=60,
    container_disk_gb=60,
    execution_timeout_ms=2 * 60 * 60 * 1000,
    dependencies=["google-auth", "requests"],  # for projects' Play upload scripts
    env=SECRETS,
)

app = App("unity-builder")


def build(repo: str = "", tag: str = "", github_token: str = "",
          build_method: str = "", build_args: list = None, project: str = "",
          play_upload: dict = None, timeout_min: int = 90, source_tar_b64: str = "",
          cpu_limit: int = 0, prerelease: bool = False, build_target: str = "Android",
          output_file: str = "Builds/Android/last-build.txt", extra_env: dict = None,
          **unknown):
    """Get the source, activate Unity, build, then publish to GitHub Release / Google Play.

    output_file names the file in which the build method writes its output path. A file
    output (AAB, APK) is attached to the release with its siblings of the same name; a
    directory output (an exported Xcode project) is attached as a .tar.gz archive.

    Unknown arguments are ignored, so a client that sends a newer option does not crash
    a worker that still runs older code during a rollout.
    """
    import base64
    import io
    import json
    import os
    import subprocess
    import sys
    import tarfile
    import tempfile
    import time
    import urllib.error
    import urllib.request
    from pathlib import Path

    print(f"unity-builder revision {REVISION}", flush=True)
    if unknown:
        print(f"Ignoring unknown arguments: {', '.join(sorted(unknown))}", flush=True)
    work = Path(tempfile.mkdtemp(prefix="build-")) / "project"
    build_args = build_args or []

    # Project secrets: PREFIX_KEYSTORE_PASS -> ANDROID_KEYSTORE_PASS, etc.
    prefix = project.upper()
    env = dict(os.environ)
    for name in ("KEYSTORE_PASS", "KEYALIAS_PASS"):
        if f"{prefix}_{name}" in env:
            env[f"ANDROID_{name}"] = env[f"{prefix}_{name}"]
    env.update({key: str(value) for key, value in (extra_env or {}).items()})
    # The service account JSON may be stored base64-encoded (quotes do not survive shells).
    play_key = (env.get(f"{prefix}_PLAY_SERVICE_ACCOUNT") or env.get("PLAY_SERVICE_ACCOUNT", "")).strip()
    if play_key and not play_key.startswith("{"):
        play_key = base64.b64decode(play_key).decode()

    def run(cmd, secret=False, check=True):
        print("$", cmd[0], "..." if secret else " ".join(cmd[1:6]), flush=True)
        return subprocess.run(cmd, check=check, env=env)

    # cpu_limit pins Unity and its children (Bee, clang, Gradle) to that many cores, which
    # caps how many compilers run at once when memory is tight.
    pin = ["taskset", "-c", f"0-{cpu_limit - 1}"] if cpu_limit else []

    def unity(args, deadline=None):
        process = subprocess.Popen(
            [*pin, "unity-editor", "-batchmode", "-quit", "-nographics", "-logFile", "-", *args],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors="replace", env=env)
        for line in process.stdout:
            print(line, end="", flush=True)
            if deadline and time.time() > deadline:
                process.kill()
                raise RuntimeError(f"Unity timed out after {timeout_min} min")
        return process.wait()

    def github(method, url, data=None, headers=None):
        request = urllib.request.Request(url, data=data, method=method, headers={
            "Authorization": f"Bearer {github_token}",
            "Accept": "application/vnd.github+json",
            **(headers or {}),
        })
        try:
            with urllib.request.urlopen(request, timeout=1800) as response:
                body = response.read()
                return json.loads(body) if body else {}
        except urllib.error.HTTPError as e:
            if e.code == 404:
                return None
            raise RuntimeError(f"GitHub {method} {url} failed ({e.code}): {e.read().decode()[:500]}")

    # 1. Source: the archive sent by a local test (base64, queue jobs are plain JSON),
    # or a clone of the tag with the token in a header so it never lands in .git/config.
    if source_tar_b64:
        source_tar = base64.b64decode(source_tar_b64)
        work.mkdir(parents=True)
        with tarfile.open(fileobj=io.BytesIO(source_tar)) as archive:
            archive.extractall(work)
        print(f"Extracted local source ({len(source_tar) / 1048576:.1f} MB)", flush=True)
    else:
        auth = base64.b64encode(f"x-access-token:{github_token}".encode()).decode()
        run(["git", "-c", f"http.extraHeader=Authorization: Basic {auth}", "clone", "--depth", "1",
             "--branch", tag, f"https://github.com/{repo}.git", str(work)], secret=True)

    # 2. Unity license: Unity 6 activates through its Licensing Client, which falls back to
    # Personal when the UNITY_EMAIL account has no seats. Without credentials, use a .ulf
    # (stored base64-encoded so the multi-line XML survives).
    email, password = env.get("UNITY_EMAIL"), env.get("UNITY_PASSWORD")
    licensing_client = "/opt/unity/Editor/Data/Resources/Licensing/Client/Unity.Licensing.Client"
    credentials = ["--username", email, "--password", password]
    if email and password:
        if run([licensing_client, "--activate-all", "--include-personal", *credentials],
               secret=True, check=False).returncode != 0:
            raise RuntimeError("Unity license activation failed")
    elif env.get("UNITY_LICENSE"):
        license_text = env["UNITY_LICENSE"].strip()
        if not license_text.startswith("<"):
            license_text = base64.b64decode(license_text).decode()
        license_file = Path.home() / ".local/share/unity3d/Unity/Unity_lic.ulf"
        license_file.parent.mkdir(parents=True, exist_ok=True)
        license_file.write_text(license_text)
    else:
        print("No Unity credentials or license, activation will fail", flush=True)

    # 3. Build, with a hard deadline so a stuck editor cannot hold the worker.
    started = time.time()
    try:
        code = unity(["-projectPath", str(work), "-buildTarget", build_target,
                      "-executeMethod", build_method, *build_args],
                     deadline=started + timeout_min * 60)
    finally:
        if email and password:
            run([licensing_client, "--deactivate-all", *credentials], secret=True, check=False)
    if code != 0:
        raise RuntimeError(f"Unity build failed with exit code {code}")
    build_minutes = (time.time() - started) / 60

    output = Path((work / output_file).read_text().strip())
    if output.is_dir():
        archive = output.parent / f"{output.name}-{tag or 'local'}.tar.gz"
        with tarfile.open(archive, "w:gz", compresslevel=1) as stream:
            stream.add(output, arcname=output.name)
        output, files = archive, [archive]
    else:
        files = [f for f in output.parent.iterdir() if f.is_file() and f.name.startswith(output.stem)]
    size_mb = output.stat().st_size / 1048576
    print(f"Built {output.name} ({size_mb:.1f} MB) in {build_minutes:.1f} min", flush=True)

    # 4. Attach the build to the GitHub Release of the tag (the backup copy).
    release = None
    if github_token:
        api = f"https://api.github.com/repos/{repo}"
        release = github("GET", f"{api}/releases/tags/{tag}")
        if not release:
            try:
                release = github(
                    "POST", f"{api}/releases",
                    json.dumps({"tag_name": tag, "name": tag, "prerelease": prerelease}).encode(),
                    {"Content-Type": "application/json"})
            except RuntimeError:
                # Another platform's build of the same tag created it in the meantime.
                release = github("GET", f"{api}/releases/tags/{tag}")
                if not release:
                    raise
        existing = {asset["name"]: asset["id"] for asset in release.get("assets", [])}
        for file in files:
            if file.name in existing:
                github("DELETE", f"{api}/releases/assets/{existing[file.name]}")
            with file.open("rb") as stream:
                github("POST", f"https://uploads.github.com/repos/{repo}/releases/{release['id']}/assets?name={file.name}",
                       stream, {"Content-Type": "application/octet-stream", "Content-Length": str(file.stat().st_size)})
            print(f"Attached {file.name} to release {tag}", flush=True)

    # 5. Google Play: the built-in uploader, or a project's own script ("script"), which runs
    # on this worker's Python, whose path includes the deployed dependencies (google-auth, requests).
    if play_upload:
        if not play_key:
            raise RuntimeError(f"Play upload requested but {prefix}_PLAY_SERVICE_ACCOUNT is not set")
        if play_upload.get("script"):
            with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as key:
                key.write(play_key)
            env["PYTHONPATH"] = os.pathsep.join(path for path in sys.path if path)
            try:
                run([sys.executable, str(work / play_upload["script"]), str(output),
                     "--track", play_upload["track"], "--status", play_upload["status"], "--key", key.name])
            finally:
                os.remove(key.name)
        else:
            upload_to_play(output, android_package(work), play_upload["track"], play_upload["status"],
                           json.loads(play_key))

    return {
        "artifact": output.name,
        "size_mb": round(size_mb, 1),
        "build_minutes": round(build_minutes, 1),
        "release_url": release["html_url"] if release else None,
        "play": f"{play_upload['track']} ({play_upload['status']})" if play_upload else None,
    }


def android_package(project):
    """The Android application id from the project's ProjectSettings.asset."""
    import re
    text = (project / "ProjectSettings" / "ProjectSettings.asset").read_text(encoding="utf-8")
    return re.search(r"applicationIdentifier:\s*\n(?:\s+\w+:.*\n)*?\s+Android: (\S+)", text).group(1)


def upload_to_play(aab, package, track, status, service_account):
    """Upload an AAB to Google Play in one edit and assign it to the track; the edit is
    discarded if any step fails."""
    import json
    from google.auth.transport.requests import AuthorizedSession
    from google.oauth2 import service_account as accounts

    api = f"https://androidpublisher.googleapis.com/androidpublisher/v3/applications/{package}"
    upload_api = f"https://androidpublisher.googleapis.com/upload/androidpublisher/v3/applications/{package}"
    credentials = accounts.Credentials.from_service_account_info(
        service_account, scopes=["https://www.googleapis.com/auth/androidpublisher"])
    session = AuthorizedSession(credentials)

    def check(response, action):
        if response.ok:
            return response.json() if response.content else {}
        try:
            message = response.json()["error"]["message"]
        except (ValueError, KeyError):
            message = response.text
        raise RuntimeError(f"Play: {action} failed ({response.status_code}): {message}")

    size = aab.stat().st_size
    print(f"Uploading {aab.name} ({size / 1048576:.1f} MB) to Google Play {package}", flush=True)
    edit_id = check(session.post(f"{api}/edits"), "create edit")["id"]
    try:
        init = session.post(
            f"{upload_api}/edits/{edit_id}/bundles",
            params={"uploadType": "resumable", "ackBundleInstallationWarning": "true"},
            headers={"X-Upload-Content-Type": "application/octet-stream", "X-Upload-Content-Length": str(size)})
        check(init, "upload init")
        with aab.open("rb") as stream:
            bundle = check(session.put(init.headers["Location"], data=stream, timeout=1800, headers={
                "Content-Type": "application/octet-stream", "Content-Length": str(size)}), "bundle upload")
        version_code = bundle["versionCode"]
        if track:
            release = {"versionCodes": [str(version_code)], "status": status}
            check(session.put(f"{api}/edits/{edit_id}/tracks/{track}",
                              data=json.dumps({"track": track, "releases": [release]}),
                              headers={"Content-Type": "application/json"}), f"assign to {track}")
        check(session.post(f"{api}/edits/{edit_id}:commit"), "commit")
    except Exception:
        session.delete(f"{api}/edits/{edit_id}")
        raise
    print(f"Uploaded version code {version_code} to Play {track or 'bundle library'} ({status})", flush=True)


# One endpoint per Unity version and target module.
@app.queue(name="android_6000_3_8f1", image=IMAGE.format("6000.3.8f1-android-3"), **COMMON)
def android_6000_3_8f1(**request):
    return build(**request)


# Exports the Xcode project only; a Mac builds, signs and uploads it.
@app.queue(name="ios_6000_3_8f1", image=IMAGE.format("6000.3.8f1-ios-3"), **COMMON)
def ios_6000_3_8f1(**request):
    return build(**{"build_target": "iOS", **request})


@runpod.local_entrypoint
def main():
    """Dev test: build this repo's committed HEAD on the endpoint matching its Unity version."""
    import base64
    import subprocess

    archive = subprocess.run(["git", "archive", "--format=tar", "HEAD"],
                             capture_output=True, check=True).stdout
    result = android_6000_3_8f1.remote(
        build_method="CiBuilder.BuildFromCommandLine",
        source_tar_b64=base64.b64encode(archive).decode())
    print(result)
