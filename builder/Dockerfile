# GameCI Unity editor + the RunPod SDK worker runtime.
# The SDK launcher only pip-installs runpod-sdk-runtime when it is missing, and that
# package is not on PyPI, so it is baked in here from its GitHub release.
# Python stays the image's 3.10, so local and CI Python must be 3.10 too.
ARG UNITY_IMAGE=unityci/editor:ubuntu-6000.3.8f1-android-3
FROM ${UNITY_IMAGE}

ARG RUNPOD_VERSION=2.1.0
ARG RUNTIME_REF=runpod-sdk-runtime-v0.2.0

RUN apt-get update \
 && apt-get install -y --no-install-recommends python3-venv \
 && rm -rf /var/lib/apt/lists/*

# A venv first on PATH, so the launcher's "python3" is this one and not the system Python.
RUN python3 -m venv /opt/venv \
 && /opt/venv/bin/pip install --no-cache-dir --upgrade pip \
 && /opt/venv/bin/pip install --no-cache-dir "runpod==${RUNPOD_VERSION}" \
    "runpod-sdk-runtime @ git+https://github.com/runpod-workers/sdk-runtime@${RUNTIME_REF}" \
 && /opt/venv/bin/python -c "import runpod_sdk_runtime, runpod"
ENV PATH=/opt/venv/bin:$PATH
