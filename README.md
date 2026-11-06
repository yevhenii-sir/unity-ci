# unity-ci

Release pipeline for Unity projects: GitHub Actions → RunPod (`unity-builder`) →
Google Play / App Store Connect, with iOS signed and uploaded on a self-hosted Mac.

- `unity/` — Unity package `com.yevhenii.unity-ci`: Android and iOS builders, iOS
  postprocessing, and the Build > CI Setup page that writes the project's workflow.
- `.github/workflows/release.yml` — reusable workflow that projects call.
- `scripts/` — what it runs: version bump, RunPod request, iOS archive on the Mac.
- `runner/` — on-demand self-hosted runners for the Mac (one install for all projects).
- `builder/` — the RunPod service and its worker image (`.github/workflows/image.yml`).

This repo holds no secrets and has no runners attached; projects run the workflow on their
own runners with their own secrets.

## Adding a project

1. `Packages/manifest.json`:

   ```json
   "com.yevhenii.unity-ci": "https://github.com/yevhenii-sir/unity-ci.git?path=/unity#v1"
   ```

2. Build > CI Setup: fill in the settings (secrets prefix, Release defines, folders to
   exclude from Release builds, iOS options) and press Write GitHub workflow files. Commit
   `ProjectSettings/UnityCiSettings.asset`, `.github/workflows/release.yml` and
   `.github/unity-ci.json`. Android builds use the `Android Debug` / `Android Release` build
   profiles when the project has them, otherwise the scenes from Build Settings.
3. Repo secrets: `RUNPOD_API_KEY`; for iOS `ASC_KEY_ID`, `ASC_ISSUER_ID`, `ASC_KEY_P8`
   (App Store Connect API key, Admin) and `IOS_DIST_P12`, `IOS_DIST_P12_PASSWORD`
   (Apple Distribution identity, base64 .p12).
4. RunPod secrets `<prefix>-keystore-pass`, `<prefix>-keyalias-pass`,
   `<prefix>-play-service-account`, mapped in `builder/unity_builder.py` (SECRETS), then redeploy.
5. Nothing for the Mac: the runner dispatcher picks the repo up by its `.github/unity-ci.json`
   (within 10 minutes).
6. A Unity version without endpoints: build its worker images with the Worker image
   workflow and add endpoints in `builder/unity_builder.py`.

Google Play needs the app created and its first AAB uploaded by hand; App Store Connect
needs the app record.

## Mac runners

`runner/dispatcher.py` runs on the Mac as a LaunchAgent. It watches the private repos that
have `.github/unity-ci.json` (or the `repos` listed in its config), and for each queued
`self-hosted, macOS` job starts an ephemeral just-in-time runner from one shared runner
install; the runner takes the job and unregisters itself, so nothing idles between jobs.
Up to `max_runners` jobs run at once, each in its own work folder. Public repos are never
served.

Install or update (asks for a fine-grained token on the first run: repository access to the
projects, Administration read/write, Actions read, Contents read):

```bash
curl -fsSL https://raw.githubusercontent.com/yevhenii-sir/unity-ci/v1/runner/install.sh | bash
```

Config `~/.unity-ci-runner/config.json`, log `~/Library/Logs/unity-ci-runner/dispatcher.log`
(one log per runner next to it). `python3 ~/.unity-ci-runner/dispatcher.py --check` lists
the watched repos.

## Versions

Projects use `@v1` (workflow) and `#v1` (package). Compatible changes move the `v1` tag;
breaking ones get `v2` (and the `unity_ci_ref` default in `release.yml`). Unity pins the
package to a commit in `Packages/packages-lock.json`: update it from the Package Manager.
To test a branch, call the workflow with `@branch` and `unity_ci_ref: branch`.

## Builder

```bash
rp flash deploy builder --python-version 3.10
```
