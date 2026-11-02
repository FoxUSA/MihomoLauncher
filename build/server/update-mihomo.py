#!/usr/bin/env python3
"""Keeps the byfox.dev mirror of the mihomo engine current.

The launcher downloads the engine from GitHub; where GitHub's release CDN is blocked (RU) it
falls back to https://byfox.dev/data/mihomolauncher/mihomo-windows-amd64.exe. This script
(run daily from cron) refreshes that file from the latest stable GitHub release.

The version of the exe already on the server is read from the binary itself: a Go build
stores the mihomo version right after the Go version ("go1.26.8\\0...\\0v1.19.32\\0"). If it
equals the latest release tag, nothing is downloaded. The new file is verified the same way
and swapped in atomically, so nginx never serves a half-written exe.

Lives on the server next to the exe it maintains: /var/www/byfox.dev/data/mihomolauncher/.
usage:  python3 update-mihomo.py [target_dir]     (default: the folder this script is in)
cron:   17 4 * * * root /usr/bin/python3 /var/www/byfox.dev/data/mihomolauncher/update-mihomo.py >> /var/log/mihomo-mirror.log 2>&1
"""
import io, json, os, re, sys, time, urllib.request, zipfile

TARGET_DIR = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
EXE_NAME = "mihomo-windows-amd64.exe"
API = "https://api.github.com/repos/MetaCubeX/mihomo/releases/latest"
# Same asset the launcher picks (Services\GitHubMihomo.cs): the universal amd64 build.
ASSET_RE = re.compile(r"^mihomo-windows-amd64-v\d+\.\d+\.\d+\.zip$")
VERSION_RE = re.compile(rb"go1\.\d+(?:\.\d+)?\x00+(v\d+\.\d+\.\d+)\x00")


def log(msg):
    print(time.strftime("%Y-%m-%d %H:%M:%S"), msg, flush=True)


def fetch(url, timeout=120):
    req = urllib.request.Request(url, headers={"User-Agent": "byfox-mihomo-mirror"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read()


def version_of(data):
    m = VERSION_RE.search(data)
    return m.group(1).decode() if m else ""


def main():
    rel = json.loads(fetch(API, timeout=30))
    tag = rel.get("tag_name", "")
    asset = next((a for a in rel.get("assets", []) if ASSET_RE.match(a.get("name", ""))), None)
    if not tag or not asset:
        log("ERROR: no windows-amd64 zip in the latest release (tag=%r)" % tag)
        return 1

    dest = os.path.join(TARGET_DIR, EXE_NAME)
    have = ""
    if os.path.exists(dest):
        with open(dest, "rb") as f:
            have = version_of(f.read())
    if have == tag:
        log("up to date: %s" % tag)
        return 0

    log("updating %s -> %s (%s)" % (have or "none", tag, asset["name"]))
    with zipfile.ZipFile(io.BytesIO(fetch(asset["browser_download_url"]))) as z:
        exe = z.read(EXE_NAME)
    got = version_of(exe)
    if got != tag:
        log("ERROR: downloaded exe reports %r, expected %s - left the old file alone" % (got, tag))
        return 1

    tmp = dest + ".tmp"
    with open(tmp, "wb") as f:
        f.write(exe)
    os.chmod(tmp, 0o644)
    os.replace(tmp, dest)
    log("done: %s (%d bytes)" % (tag, len(exe)))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as e:
        log("ERROR: %s" % e)
        sys.exit(1)
