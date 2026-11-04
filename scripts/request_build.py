"""Ask the shared unity-builder service on RunPod to build a project.

Env: RUNPOD_API_KEY, PROJECT_CONFIG (the project's .github/unity-ci.json), UNITY_VERSION,
GITHUB_REPOSITORY, RELEASE_TAG, GITHUB_TOKEN. Optional: PLATFORM (android or ios),
BUILD_CONFIG (Release or Debug, Android only), PLAY_UPLOAD=false, PLAY_TRACK (internal,
alpha, beta, production), PLAY_STATUS (draft or completed), IOS_BUILD_NUMBER,
RUNPOD_JOB_FILE.
`request_build.py --cancel JOB_ID` cancels a build job (PLATFORM picks the endpoint).

Android: the builder clones the tag and runs the project's build method. Release attaches
the build to the GitHub Release of the tag and, when the project has a play_script,
uploads the AAB to Google Play; Debug attaches the APK to a prerelease.
iOS: the builder attaches the exported Xcode project as Xcode-<tag>.tar.gz;
scripts/ios_archive.sh on the Mac builds and uploads it.
"""
import json
import os
import sys
from pathlib import Path

import runpod


def project():
    return json.loads(Path(os.environ["PROJECT_CONFIG"]).read_text(encoding="utf-8"))


def platform():
    return os.environ.get("PLATFORM") or "android"


def queue():
    unity = os.environ["UNITY_VERSION"].replace(".", "_")
    return runpod.Queue(app="unity-builder", name=f"{platform()}_{unity}")


def cancel(job_id):
    job = queue().job(job_id)
    job.cancel()
    print(f"Cancelled RunPod job {job_id}: {job.status()}")


def android_request(config):
    android = config["android"]
    build = os.environ.get("BUILD_CONFIG") or "Release"
    release = build == "Release"
    play_upload = None
    if release and android.get("play_script") and os.environ.get("PLAY_UPLOAD", "true") != "false":
        play_upload = {"script": android["play_script"],
                       "track": os.environ.get("PLAY_TRACK") or "internal",
                       "status": os.environ.get("PLAY_STATUS") or "draft"}
    target = f"Play {play_upload['track']} ({play_upload['status']})" if play_upload else "no Play upload"
    return f"{build}, {target}", dict(
        build_method=android["build_method"],
        build_args=[arg.replace("{config}", build) for arg in android.get("build_args", [])],
        output_file=android.get("output_file", "Builds/Android/last-build.txt"),
        play_upload=play_upload,
        prerelease=not release,
    )


def ios_request(config):
    ios = config["ios"]
    build_number = os.environ.get("IOS_BUILD_NUMBER", "")
    return f"Xcode project, build {build_number or 'from the project'}", dict(
        build_method=ios["build_method"],
        output_file=ios.get("output_file", "Builds/iOS/last-build.txt"),
        extra_env={"IOS_BUILD_NUMBER": build_number} if build_number else {},
    )


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "--cancel":
        return cancel(sys.argv[2])

    config = project()
    tag = os.environ["RELEASE_TAG"]
    timeout_min = config.get("timeout_min", 90)
    label, request = (ios_request if platform() == "ios" else android_request)(config)
    print(f"Building {tag} ({label}) on unity-builder/{queue().resource_name}", flush=True)
    # spawn + result: .remote() gives up waiting after 300 s while the build runs on.
    job = queue().spawn(
        repo=os.environ["GITHUB_REPOSITORY"],
        tag=tag,
        github_token=os.environ["GITHUB_TOKEN"],
        project=config.get("secrets_prefix", ""),
        timeout_min=timeout_min,
        **request,
    )
    print(f"RunPod job {job.id}", flush=True)
    # A cancelled run kills this script, so a later workflow step cancels the job by the
    # id saved here (a killed step may not publish its outputs, a file always survives).
    job_file = os.environ.get("RUNPOD_JOB_FILE")
    if job_file:
        Path(job_file).write_text(job.id, encoding="utf-8")
    try:
        result = job.result(timeout=(timeout_min + 15) * 60)
    except Exception as e:
        sys.exit(f"Build failed: {e}")
    for key, value in result.items():
        print(f"{key}: {value}")


if __name__ == "__main__":
    main()
