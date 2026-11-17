#!/usr/bin/env python3
"""Build a privacy-checked Desktop Todo release zip and its SHA-256 sidecar.

Usage:  python make-release.py
Output: dist/DesktopTodo-v<version>-win-x64.zip
        dist/DesktopTodo-v<version>-win-x64.zip.sha256

The app checks the .sha256 asset when present, so always upload BOTH files to
the GitHub Release. Version is read from DesktopTodo/DesktopTodo.csproj and must
match the release tag (v<version>).
"""
from __future__ import annotations

import hashlib
import os
import re
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
CSPROJ = ROOT / "DesktopTodo" / "DesktopTodo.csproj"
OUT_DIR = ROOT / "dist"
BUILD_DIR = ROOT / ".release-build"

REQUIRED = ("DesktopTodo.exe", "DesktopTodo.dll", "DesktopTodo.runtimeconfig.json")
EXCLUDE_NAMES = {"config.json", "config.json.bad", "error.log", "update.log"}

# Hosts that are expected to appear in binaries / docs. Anything else is reported.
ALLOWED_HOSTS = {
    "github.com", "api.github.com", "raw.githubusercontent.com",
    "dotnet.microsoft.com", "microsoft.com", "schemas.microsoft.com",
    "example.com", "w3.org", "localhost", "127.0.0.1",
}

PATTERNS = [
    ("明文密码字段", rb'(?i)"app_password"\s*:\s*"[^"]+"'),
    ("私钥", rb"BEGIN [A-Z ]*PRIVATE KEY"),
    ("API token", rb"(ghp_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16})"),
    ("手机号", rb"(?<!\d)1[3-9]\d{9}(?!\d)"),
    ("身份证", rb"(?<!\d)\d{17}[\dXx](?!\d)"),
    ("本机绝对路径", rb"[A-Za-z]:\\Users\\|[A-Za-z]:\\miniapp"),
]

URL_RE = re.compile(rb"https?://([A-Za-z0-9._-]+)")
PDB_PATH_RE = re.compile(rb"[A-Za-z]:\\[^\x00]{1,240}?\.pdb")
# .NET SDK 自带的 apphost 模板带有微软构建机的 pdb 路径，与用户数据无关。
SDK_BUILD_MARKERS = (b"a\\_work", b"corehost\\apphost", b"\\dotnet\\sdk\\")


def read_version() -> str:
    text = CSPROJ.read_text(encoding="utf-8")
    m = re.search(r"<Version>\s*([0-9]+(?:\.[0-9]+){1,3})\s*</Version>", text)
    if not m:
        raise SystemExit("csproj 中没有 <Version>，无法生成发行包")
    return m.group(1)


def find_sdk() -> str:
    user_sdk = Path(os.environ.get("USERPROFILE", "")) / ".dotnet" / "dotnet.exe"
    if user_sdk.is_file():
        return str(user_sdk)
    found = shutil.which("dotnet")
    return found or "dotnet"


def build() -> None:
    if BUILD_DIR.exists():
        shutil.rmtree(BUILD_DIR)
    BUILD_DIR.mkdir(parents=True)
    # 清掉增量产物：否则 publish 会复用之前带调试符号的 dll，
    # 把源码目录路径一起打进发行包。
    shutil.rmtree(ROOT / "DesktopTodo" / "obj", ignore_errors=True)
    shutil.rmtree(ROOT / "DesktopTodo" / "bin" / "Release", ignore_errors=True)
    cmd = [
        find_sdk(), "publish", str(CSPROJ), "-c", "Release",
        "-o", str(BUILD_DIR), "--self-contained", "false", "-r", "win-x64",
        "-p:DebugType=none", "-p:DebugSymbols=false", "-v", "minimal",
    ]
    print("构建:", " ".join(cmd))
    if subprocess.call(cmd) != 0:
        raise SystemExit("dotnet publish 失败，已停止打包")


def scan_views(data: bytes):
    """Yield byte views of data covering both ASCII and UTF-16LE encoding."""
    yield data
    # .NET 二进制中字符串以 UTF-16LE 存储，ASCII 正则无法命中。
    for offset in (0, 1):
        try:
            text = data[offset:].decode("utf-16le", errors="ignore")
            yield text.encode("utf-8", errors="ignore")
        except Exception:
            pass


def scan(name: str, data: bytes) -> list[str]:
    problems = []
    for view in scan_views(data):
        for label, pattern in PATTERNS:
            if re.search(pattern, view):
                problems.append(label)
        for m in PDB_PATH_RE.finditer(view):
            chunk = m.group(0)
            if any(marker in chunk for marker in SDK_BUILD_MARKERS):
                continue  # SDK 自带模板路径，不是用户机器
            problems.append("调试符号路径")
            break
        for host in URL_RE.findall(view):
            h = host.decode("ascii", "ignore").lower()
            if h in ALLOWED_HOSTS or h.endswith(".example.com"):
                continue
            if any(h == a or h.endswith("." + a) for a in ALLOWED_HOSTS):
                continue
            problems.append("未白名单域名: " + h)
    return sorted(set(problems))


def main() -> None:
    version = read_version()
    zip_name = f"DesktopTodo-v{version}-win-x64.zip"
    OUT_DIR.mkdir(exist_ok=True)
    zip_path = OUT_DIR / zip_name

    build()

    files = sorted(
        p for p in BUILD_DIR.iterdir()
        if p.is_file() and p.suffix.lower() != ".pdb" and p.name not in EXCLUDE_NAMES
    )
    names = {p.name for p in files}
    missing = [f for f in REQUIRED if f not in names]
    if missing:
        raise SystemExit("构建产物缺少必需文件: " + ", ".join(missing))

    extras = [ROOT / "README.md", ROOT / "DesktopTodo" / "config.example.json"]
    for e in extras:
        if not e.is_file():
            raise SystemExit("缺少文件: " + str(e))

    print("\n=== 打包前隐私检查 ===")
    failed = False
    # 先扫描全部文件，确认无问题后再写 zip。
    all_files = []
    for p in files:
        issues = scan(p.name, p.read_bytes())
        print(f"  {p.name:38s} {'OK' if not issues else '⚠ ' + ', '.join(issues)}")
        if issues:
            failed = True
        all_files.append(p)
    for e in extras:
        issues = scan(e.name, e.read_bytes())
        print(f"  {e.name:38s} {'OK' if not issues else '⚠ ' + ', '.join(issues)}")
        if issues:
            failed = True
        all_files.append(e)

    if failed:
        raise SystemExit("发现疑似隐私内容，未生成 zip，请检查后再发布")

    # 扫描通过后才写 zip（通过临时文件 + 原子改名）。
    tmp_zip = zip_path.with_suffix(".zip.tmp")
    with zipfile.ZipFile(tmp_zip, "w", zipfile.ZIP_DEFLATED) as z:
        for p in all_files:
            z.write(p, p.name)
    tmp_zip.rename(zip_path)

    with zipfile.ZipFile(zip_path) as z:
        contents = z.namelist()
    if "config.json" in contents or any(n.endswith(".pdb") for n in contents):
        zip_path.unlink(missing_ok=True)
        raise SystemExit("发行包内出现了 config.json 或 pdb，已删除")

    digest = hashlib.sha256(zip_path.read_bytes()).hexdigest()
    sidecar = OUT_DIR / (zip_name + ".sha256")
    sidecar.write_text(f"{digest}  {zip_name}\n", encoding="ascii")

    print("\n=== 完成 ===")
    print(f"  版本:     v{version}  (Release tag 必须是 v{version})")
    print(f"  zip:      {zip_path}  ({zip_path.stat().st_size} B)")
    print(f"  sha256:   {sidecar}")
    print(f"  SHA-256:  {digest}")
    print(f"  包内文件: {', '.join(contents)}")
    print("  上传 Release 时请同时上传 zip 与 .sha256 两个资产。")
    shutil.rmtree(BUILD_DIR, ignore_errors=True)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        sys.exit(1)
