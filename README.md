# unity-ci

Release pipeline for Unity projects: GitHub Actions → RunPod (`unity-builder`) →
Google Play / App Store Connect, with iOS signed and uploaded on a self-hosted Mac.

- `.github/workflows/release.yml` — reusable workflow that projects call.
- `scripts/` — what it runs: version bump, RunPod request, iOS archive on the Mac.
- `builder/` — the RunPod service and its worker image (`.github/workflows/image.yml`).

This repo holds no secrets and has no runners attached; projects run the workflow on their
own runners with their own secrets.

## Adding a project

1. `.github/unity-ci.json`:

   ```json
   {
     "secrets_prefix": "NAME",
     "timeout_min": 110,
     "android": {
       "build_method": "Namespace.AndroidBuilder.BuildFromCommandLine",
       "build_args": ["-buildConfig", "{config}"],
       "play_script": "Tools/Build/upload_play.py"
     },
     "ios": {
       "build_method": "Namespace.IosBuilder.BuildFromCommandLine",
       "team_id": "XXXXXXXXXX"
     }
   }
   ```

   `{config}` becomes Release or Debug. The build method writes its output path to
   `output_file` (default `Builds/Android/last-build.txt`, `Builds/iOS/last-build.txt`).
   Without `play_script` nothing goes to Google Play; without `ios` the project is Android only.
2. `.github/workflows/release.yml`: a `workflow_dispatch` with the inputs it needs and

   ```yaml
   permissions:
     contents: write
   jobs:
     release:
       uses: yevhenii-sir/unity-ci/.github/workflows/release.yml@v1
       with:
         platform: ${{ inputs.platform }}
         config: ${{ inputs.config }}
       secrets: inherit
   ```

3. Repo secrets: `RUNPOD_API_KEY`; for iOS `ASC_KEY_ID`, `ASC_ISSUER_ID`, `ASC_KEY_P8`
   (App Store Connect API key, Admin) and `IOS_DIST_P12`, `IOS_DIST_P12_PASSWORD`
   (Apple Distribution identity, base64 .p12).
4. Android signing and Play: RunPod secrets `<prefix>-keystore-pass`, `<prefix>-keyalias-pass`,
   `<prefix>-play-service-account`, mapped in `builder/unity_builder.py` (SECRETS), then redeploy.
5. A Unity version without endpoints: build its worker images with the Worker image
   workflow and add endpoints in `builder/unity_builder.py`.

## Versions

Projects call `@v1`. Compatible changes move the `v1` tag; breaking ones get `v2`
(and the `unity_ci_ref` default in `release.yml`). To test a branch, call it with
`@branch` and `unity_ci_ref: branch`.

## Builder

```bash
rp flash deploy builder --python-version 3.10
```
