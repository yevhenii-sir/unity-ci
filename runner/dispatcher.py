#!/usr/bin/env python3
"""Self-hosted GitHub Actions runners on demand, for any number of repos.

Polls the watched repos for queued jobs whose labels this machine has, and for each one
starts an ephemeral just-in-time runner from a single runner install; the runner takes
one job and unregisters itself. Nothing idles between jobs.

Config: ~/.unity-ci-runner/config.json (chmod 600)
    token        fine-grained token: Administration read/write, Actions read, Contents read
    repos        ["owner/name", ...]; empty = the private repos with .github/unity-ci.json
    labels       labels of this machine (default self-hosted, macOS, ARM64)
    max_runners  jobs at once (default 2)
    poll_seconds (default 20), idle_minutes (default 10: an unused runner is removed)

Public repos are never served: anyone could run code on this machine through a pull request.

    dispatcher.py           run (the LaunchAgent does this)
    dispatcher.py --setup   download or update the runner, capture PATH for jobs
    dispatcher.py --check   validate the token and list the watched repos
"""
import json
import os
import platform
import shutil
import signal
import socket
import subprocess
import sys
import tarfile
import tempfile
import time
import urllib.error
from concurrent.futures import ThreadPoolExecutor
import urllib.request
from pathlib import Path

ROOT = Path(os.environ.get("UNITY_CI_RUNNER_HOME", Path.home() / ".unity-ci-runner"))
CONFIG = ROOT / "config.json"
STATE = ROOT / "runners.json"
RUNNER = ROOT / "runner"
LOGS = Path.home() / "Library" / "Logs" / "unity-ci-runner"
API = "https://api.github.com"
NAME_PREFIX = "jit-" + socket.gethostname().split(".")[0].lower()
REPOS_REFRESH = 600
PROJECT_CONFIG = ".github/unity-ci.json"
THREADS = 8
UPDATE_CHECK = 6 * 3600
DEFAULTS = {
    "repos": [],
    "labels": ["self-hosted", "macOS", "ARM64"],
    "max_runners": 2,
    "poll_seconds": 20,
    "idle_minutes": 10,
}


def log(message):
    print(time.strftime("%Y-%m-%d %H:%M:%S"), message, flush=True)


class GitHubError(Exception):
    def __init__(self, code, message):
        super().__init__(f"{code}: {message}")
        self.code = code


class GitHub:
    """REST client; cached GETs send If-None-Match, and a 304 does not count against the rate limit."""

    def __init__(self, token):
        self.token = token
        self.etags = {}
        self.cache = {}

    def request(self, method, path, body=None, cached=False):
        url = path if path.startswith("https://") else f"{API}/{path}"
        headers = {
            "Authorization": f"Bearer {self.token}",
            "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "unity-ci-runner",
        }
        if cached and url in self.etags:
            headers["If-None-Match"] = self.etags[url]
        data = None
        if body is not None:
            data = json.dumps(body).encode()
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(url, data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(request, timeout=30) as response:
                payload = response.read()
                result = json.loads(payload) if payload else {}
                if cached and response.headers.get("ETag"):
                    self.etags[url] = response.headers["ETag"]
                    self.cache[url] = result
                return result
        except urllib.error.HTTPError as e:
            if e.code == 304 and url in self.cache:
                return self.cache[url]
            raise GitHubError(e.code, e.read().decode(errors="replace")[:300])

    def pages(self, path):
        separator = "&" if "?" in path else "?"
        for page in range(1, 11):
            items = self.request("GET", f"{path}{separator}per_page=100&page={page}")
            yield from items
            if len(items) < 100:
                return


def load_config():
    if not CONFIG.exists():
        sys.exit(f"{CONFIG} not found; run install.sh")
    config = {**DEFAULTS, **json.loads(CONFIG.read_text(encoding="utf-8"))}
    if not config.get("token"):
        sys.exit(f"Set the token in {CONFIG}")
    return config


def is_project(github, repo):
    try:
        github.request("GET", f"repos/{repo}/contents/{PROJECT_CONFIG}", cached=True)
        return True
    except GitHubError as e:
        if e.code == 404:
            return False
        raise


def watched_repos(github, config):
    """Private, non-archived repos to serve."""
    if config["repos"]:
        candidates = []
        for name in config["repos"]:
            try:
                candidates.append(github.request("GET", f"repos/{name}"))
            except GitHubError as e:
                log(f"Skipping {name}: {e}")
    else:
        candidates = list(github.pages("user/repos?affiliation=owner,collaborator,organization_member"))
    repos = []
    for repo in candidates:
        if repo.get("archived"):
            continue
        if not repo.get("private"):
            if config["repos"]:
                log(f"Skipping {repo['full_name']}: public repos are never served")
            continue
        # Registering runners needs admin rights on the repo.
        if not repo.get("permissions", {}).get("admin"):
            if config["repos"]:
                log(f"Skipping {repo['full_name']}: the token has no admin rights on it")
            continue
        repos.append(repo["full_name"])
    if not config["repos"]:
        with ThreadPoolExecutor(THREADS) as pool:
            repos = [repo for repo, project in zip(repos, pool.map(lambda r: is_project(github, r), repos)) if project]
    return sorted(repos)


def queued_jobs(github, repo, labels):
    runs = github.request("GET", f"repos/{repo}/actions/runs?per_page=10", cached=True)
    count = 0
    for run in runs.get("workflow_runs", []):
        if run["status"] == "completed":
            continue
        jobs = github.request("GET", f"repos/{repo}/actions/runs/{run['id']}/jobs?filter=latest&per_page=100",
                              cached=True)
        for job in jobs.get("jobs", []):
            if job["status"] == "queued" and job["labels"] and {l.lower() for l in job["labels"]} <= labels:
                count += 1
    return count


def runner_version():
    try:
        output = subprocess.run([str(RUNNER / "bin" / "Runner.Listener"), "--version"],
                                capture_output=True, text=True, timeout=60).stdout
        return output.strip()
    except OSError:
        return ""


def latest_runner(github):
    release = github.request("GET", "repos/actions/runner/releases/latest")
    version = release["tag_name"].lstrip("v")
    arch = "arm64" if platform.machine() == "arm64" else "x64"
    asset = f"actions-runner-osx-{arch}-{version}.tar.gz"
    url = next(a["browser_download_url"] for a in release["assets"] if a["name"] == asset)
    return version, url


def install_runner(github, quiet=False):
    """Download the latest runner into RUNNER when it is missing or outdated."""
    version, url = latest_runner(github)
    current = runner_version()
    if current == version:
        if not quiet:
            log(f"Runner {version} is up to date")
        return
    log(f"Installing runner {version} (was {current or 'none'})")
    RUNNER.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as temp:
        archive = Path(temp) / "runner.tar.gz"
        with urllib.request.urlopen(url, timeout=600) as response, archive.open("wb") as stream:
            shutil.copyfileobj(response, stream)
        with tarfile.open(archive) as tar:
            tar.extractall(RUNNER)
    # Jobs get the PATH and env captured here (.path, .env), as with a configured runner;
    # a runner set up before on this Mac has them already.
    old = Path.home() / "actions-runner"
    if not (RUNNER / ".path").exists():
        if (old / ".path").exists():
            for name in (".path", ".env"):
                if (old / name).exists():
                    shutil.copy2(old / name, RUNNER / name)
        else:
            subprocess.run(["./env.sh"], cwd=RUNNER, check=True)
    log(f"Runner {runner_version()} installed in {RUNNER}")


class Dispatcher:
    def __init__(self, config):
        self.config = config
        self.github = GitHub(config["token"])
        self.labels = {label.lower() for label in config["labels"]}
        self.runners = self.load_state()
        self.repos = []
        self.repos_at = 0
        self.update_at = 0
        self.stopping = False

    def load_state(self):
        if not STATE.exists():
            return []
        runners = []
        for runner in json.loads(STATE.read_text(encoding="utf-8")):
            if pid_alive(runner["pid"]):
                runner["process"] = None
                runners.append(runner)
        return runners

    def save_state(self):
        STATE.write_text(json.dumps([{k: v for k, v in r.items() if k != "process"} for r in self.runners]),
                         encoding="utf-8")

    def refresh_repos(self):
        if time.time() - self.repos_at < REPOS_REFRESH:
            return
        repos = watched_repos(self.github, self.config)
        if repos != self.repos:
            log(f"Watching {len(repos)} repos: {', '.join(repos)}")
            for repo in set(repos) - set(self.repos):
                self.remove_stale(repo)
        self.repos, self.repos_at = repos, time.time()

    def remove_stale(self, repo):
        """Drop offline JIT runners of this machine left by a crash or a reboot."""
        known = {r["id"] for r in self.runners}
        try:
            for runner in self.github.request("GET", f"repos/{repo}/actions/runners?per_page=100")["runners"]:
                if runner["name"].startswith(NAME_PREFIX) and runner["id"] not in known and runner["status"] == "offline":
                    self.github.request("DELETE", f"repos/{repo}/actions/runners/{runner['id']}")
                    log(f"Removed stale runner {runner['name']} from {repo}")
        except GitHubError as e:
            log(f"Could not list runners of {repo}: {e}")

    def free_slot(self):
        used = {r["slot"] for r in self.runners}
        return next(slot for slot in range(1, self.config["max_runners"] + 1) if slot not in used)

    def spawn(self, repo):
        slot = self.free_slot()
        name = f"{NAME_PREFIX}-{slot}-{int(time.time())}"
        jit = self.github.request("POST", f"repos/{repo}/actions/runners/generate-jitconfig", {
            "name": name,
            "runner_group_id": 1,
            "labels": self.config["labels"],
            # Runners share the install but not the work folder.
            "work_folder": f"_work/{slot}",
        })
        LOGS.mkdir(parents=True, exist_ok=True)
        with (LOGS / f"{name}.log").open("w") as output:
            process = subprocess.Popen([str(RUNNER / "run.sh"), "--jitconfig", jit["encoded_jit_config"]],
                                       cwd=RUNNER, env=runner_env(), stdout=output, stderr=subprocess.STDOUT,
                                       start_new_session=True)
        self.runners.append({"name": name, "id": jit["runner"]["id"], "repo": repo, "slot": slot,
                             "pid": process.pid, "started": time.time(), "busy": False, "process": process})
        self.save_state()
        log(f"Started {name} for {repo}")

    def exited(self, runner):
        if runner["process"] is not None:
            return runner["process"].poll() is not None
        return not pid_alive(runner["pid"])

    def check_runners(self):
        changed = False
        for runner in list(self.runners):
            if self.exited(runner):
                self.runners.remove(runner)
                changed = True
                log(f"{runner['name']} finished")
                self.delete_registration(runner)
                continue
            if runner["busy"] or time.time() - runner["started"] < 60:
                continue
            try:
                info = self.github.request("GET", f"repos/{runner['repo']}/actions/runners/{runner['id']}")
            except GitHubError as e:
                if e.code != 404:
                    log(f"Could not check {runner['name']}: {e}")
                    continue
                info = {"busy": False}
            if info["busy"]:
                runner["busy"] = changed = True
            elif time.time() - runner["started"] > self.config["idle_minutes"] * 60:
                log(f"{runner['name']} got no job, removing it")
                stop(runner["pid"])
                if runner["process"] is not None:
                    try:
                        runner["process"].wait(timeout=30)
                    except subprocess.TimeoutExpired:
                        runner["process"].kill()
                self.delete_registration(runner)
                self.runners.remove(runner)
                changed = True
        if changed:
            self.save_state()

    def delete_registration(self, runner):
        try:
            self.github.request("DELETE", f"repos/{runner['repo']}/actions/runners/{runner['id']}")
        except GitHubError as e:
            if e.code != 404:
                log(f"Could not remove {runner['name']}: {e}")

    def poll(self, repo):
        try:
            return queued_jobs(self.github, repo, self.labels)
        except GitHubError as e:
            log(f"Could not poll {repo}: {e}")
            return 0

    def dispatch(self):
        if len(self.runners) >= self.config["max_runners"]:
            return
        with ThreadPoolExecutor(THREADS) as pool:
            queued = dict(zip(self.repos, pool.map(self.poll, self.repos)))
        for repo, count in queued.items():
            waiting = sum(1 for r in self.runners if r["repo"] == repo and not r["busy"])
            for _ in range(min(count - waiting, self.config["max_runners"] - len(self.runners))):
                self.spawn(repo)

    def maybe_update(self):
        if self.runners or time.time() - self.update_at < UPDATE_CHECK:
            return
        self.update_at = time.time()
        try:
            install_runner(self.github, quiet=True)
        except Exception as e:
            log(f"Runner update failed: {e}")

    def run(self):
        signal.signal(signal.SIGTERM, lambda *_: setattr(self, "stopping", True))
        log(f"Dispatcher started: {NAME_PREFIX}, labels {', '.join(self.config['labels'])}, "
            f"up to {self.config['max_runners']} runners")
        while not self.stopping:
            try:
                self.refresh_repos()
                self.check_runners()
                self.dispatch()
                self.maybe_update()
            except Exception as e:
                log(f"Error: {e}")
            for _ in range(self.config["poll_seconds"]):
                if self.stopping:
                    break
                time.sleep(1)
        # Running jobs keep going; the next start picks their runners up from the state file.
        log("Dispatcher stopped")


def runner_env():
    """launchd starts the dispatcher with a bare PATH; jobs get the one captured in .path, as
    runsvc.sh does for a runner service (the runner loads .env itself)."""
    env = dict(os.environ)
    path_file = RUNNER / ".path"
    if path_file.exists() and path_file.read_text(encoding="utf-8").strip():
        env["PATH"] = path_file.read_text(encoding="utf-8").strip()
    return env


def pid_alive(pid):
    try:
        os.kill(pid, 0)
        return True
    except OSError:
        return False


def stop(pid):
    try:
        os.killpg(pid, signal.SIGTERM)
    except OSError:
        pass


def main():
    config = load_config()
    if "--setup" in sys.argv:
        install_runner(GitHub(config["token"]))
    elif "--check" in sys.argv:
        github = GitHub(config["token"])
        log(f"Token of {github.request('GET', 'user')['login']}")
        labels = {label.lower() for label in config["labels"]}
        for repo in watched_repos(github, config):
            try:
                runners = github.request("GET", f"repos/{repo}/actions/runners?per_page=100")["total_count"]
                log(f"  {repo}: {queued_jobs(github, repo, labels)} queued jobs, {runners} registered runners")
            except GitHubError as e:
                log(f"  {repo}: {e}")
    else:
        Dispatcher(config).run()


if __name__ == "__main__":
    main()
