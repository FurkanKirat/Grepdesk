#!/usr/bin/env python3
"""
Grepdesk Build & Packaging CLI Tool
Automates building, testing, publishing, and creating installers/packages
across target operating systems (defaults to current OS: Windows).
"""

import argparse
import os
import platform
import re
import shutil
import subprocess
import sys
import tarfile
import time
import zipfile
from pathlib import Path
from typing import Optional


ROOT_DIR = Path(__file__).resolve().parent
OUTPUT_DIR = ROOT_DIR / "Output"
PUBLISH_DIR = ROOT_DIR / "publish"


def print_header(title: str) -> None:
    print(f"\n{'=' * 60}")
    print(f"  {title}")
    print(f"{'=' * 60}\n")


def print_step(step: str) -> None:
    print(f"[*] {step}...")


def print_success(msg: str) -> None:
    print(f"[+] {msg}")


def print_error(msg: str) -> None:
    print(f"[-] ERROR: {msg}", file=sys.stderr)


def print_warning(msg: str) -> None:
    print(f"[!] WARNING: {msg}")


def run_command(cmd: list[str], cwd: Optional[Path] = None, check: bool = True) -> int:
    cmd_str = " ".join(f'"{c}"' if " " in c else c for c in cmd)
    print(f"    > {cmd_str}")
    result = subprocess.run(cmd, cwd=cwd or ROOT_DIR)
    if check and result.returncode != 0:
        print_error(f"Command failed with exit code {result.returncode}: {cmd_str}")
        sys.exit(result.returncode)
    return result.returncode


def get_version() -> str:
    props_path = ROOT_DIR / "Directory.Build.props"
    if props_path.is_file():
        text = props_path.read_text(encoding="utf-8")
        match = re.search(r"<Version>([^<]+)</Version>", text)
        if match:
            return match.group(1).strip()
    return "0.1.0"


def normalize_os(target_os: str) -> str:
    target = target_os.lower().strip()
    if target in ("current", "auto", "host"):
        sys_name = platform.system().lower()
        if sys_name == "windows":
            return "windows"
        elif sys_name == "linux":
            return "linux"
        elif sys_name == "darwin":
            return "macos"
        return sys_name
    if target in ("win", "windows", "win32", "win64", "win-x64"):
        return "windows"
    if target in ("linux", "linux-x64"):
        return "linux"
    if target in ("macos", "darwin", "osx", "osx-x64"):
        return "macos"
    return target


def get_rid(target_os: str) -> str:
    """Returns the .NET Runtime Identifier (RID) for the target OS."""
    if target_os == "windows":
        return "win-x64"
    elif target_os == "linux":
        return "linux-x64"
    elif target_os == "macos":
        return "osx-x64"
    return f"{target_os}-x64"


def find_inno_setup() -> Optional[Path]:
    which_iscc = shutil.which("iscc")
    if which_iscc:
        return Path(which_iscc)

    candidates = [
        Path(r"C:\Program Files\Inno Setup 7\ISCC.exe"),
        Path(r"C:\Program Files (x86)\Inno Setup 7\ISCC.exe"),
        Path(r"C:\Program Files\Inno Setup 6\ISCC.exe"),
        Path(r"C:\Program Files (x86)\Inno Setup 6\ISCC.exe"),
        Path(r"C:\Program Files\Inno Setup 5\ISCC.exe"),
        Path(r"C:\Program Files (x86)\Inno Setup 5\ISCC.exe"),
    ]
    local_app_data = os.environ.get("LOCALAPPDATA")
    if local_app_data:
        candidates.append(Path(local_app_data) / "Programs" / "Inno Setup 7" / "ISCC.exe")
        candidates.append(Path(local_app_data) / "Programs" / "Inno Setup 6" / "ISCC.exe")

    for candidate in candidates:
        if candidate.is_file():
            return candidate
    return None


def action_clean(distclean: bool = False) -> None:
    print_step("Cleaning build outputs")
    patterns = ["**/bin", "**/obj"]
    for pattern in patterns:
        for folder in ROOT_DIR.glob(pattern):
            if folder.is_dir():
                print(f"    Removing {folder.relative_to(ROOT_DIR)}")
                shutil.rmtree(folder, ignore_errors=True)

    if PUBLISH_DIR.is_dir():
        print(f"    Removing {PUBLISH_DIR.relative_to(ROOT_DIR)}")
        shutil.rmtree(PUBLISH_DIR, ignore_errors=True)

    if distclean and OUTPUT_DIR.is_dir():
        print(f"    Removing {OUTPUT_DIR.relative_to(ROOT_DIR)}")
        shutil.rmtree(OUTPUT_DIR, ignore_errors=True)

    print_success("Clean completed.")


def action_build(config: str) -> None:
    print_step(f"Building solution ({config})")
    run_command(["dotnet", "build", "-c", config])
    print_success("Build completed.")


def action_test(config: str) -> None:
    print_step(f"Running test suite ({config})")
    run_command(["dotnet", "test", "-c", config, "--no-build"])
    print_success("All tests passed.")


def action_publish(target_os: str, config: str) -> Path:
    rid = get_rid(target_os)
    print_step(f"Publishing Grepdesk.UI for {target_os.upper()} ({rid}, {config})")
    out_dir = PUBLISH_DIR / rid

    if target_os == "windows":
        # Uses win-x64.pubxml profile which outputs to publish/win-x64
        run_command([
            "dotnet", "publish", "Grepdesk.UI",
            "-c", config,
            "-p:PublishProfile=win-x64"
        ])
    elif target_os == "linux":
        out_dir.mkdir(parents=True, exist_ok=True)
        run_command([
            "dotnet", "publish", "Grepdesk.UI",
            "-c", config,
            "-r", rid,
            "--self-contained", "true",
            "-p:PublishReadyToRun=true",
            "-o", str(out_dir)
        ])
    elif target_os == "macos":
        out_dir.mkdir(parents=True, exist_ok=True)
        run_command([
            "dotnet", "publish", "Grepdesk.UI",
            "-c", config,
            "-r", rid,
            "--self-contained", "true",
            "-p:PublishReadyToRun=true",
            "-o", str(out_dir)
        ])
    else:
        print_error(f"Unsupported OS for publish: {target_os}")
        sys.exit(1)

    print_success(f"Publish output ready at: {out_dir}")
    return out_dir


def action_installer(target_os: str, version: str) -> Optional[Path]:
    if target_os != "windows":
        print_warning(
            f"Inno Setup installer is Windows-only; skipping installer generation for {target_os}."
        )
        return None

    print_step("Compiling Inno Setup installer (setup.iss)")
    iscc = find_inno_setup()
    if not iscc:
        print_error(
            "Inno Setup Compiler (ISCC.exe) was not found.\n"
            "    Please install Inno Setup 6 or 7, or add ISCC.exe to your PATH."
        )
        sys.exit(1)

    app_exe = PUBLISH_DIR / "win-x64" / "Grepdesk.UI.exe"
    if not app_exe.is_file():
        print_error(
            f"Published executable not found at '{app_exe}'.\n"
            "    Run the 'publish' action before creating the installer."
        )
        sys.exit(1)

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    run_command([str(iscc), "setup.iss"])

    expected_file = OUTPUT_DIR / f"Grepdesk-v{version}-win-x64-Setup.exe"
    if expected_file.is_file():
        print_success(f"Installer generated: {expected_file}")
        return expected_file
    else:
        print_success("Installer compilation completed (check Output/ folder).")
        return None


def action_portable(target_os: str, version: str) -> Path:
    rid = get_rid(target_os)
    print_step(f"Creating portable package for {target_os.upper()} ({rid})")
    source_dir = PUBLISH_DIR / rid

    if not source_dir.is_dir():
        print_error(
            f"Publish directory '{source_dir}' does not exist.\n"
            "    Run the 'publish' action first."
        )
        sys.exit(1)

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)

    if target_os == "windows":
        archive_path = OUTPUT_DIR / f"Grepdesk-v{version}-{rid}-portable.zip"
        print(f"    Compressing {source_dir} -> {archive_path.name}")
        with zipfile.ZipFile(archive_path, "w", zipfile.ZIP_DEFLATED) as zipf:
            for item in sorted(source_dir.rglob("*")):
                if item.is_file():
                    arcname = item.relative_to(source_dir)
                    zipf.write(item, arcname)
    else:
        archive_path = OUTPUT_DIR / f"Grepdesk-v{version}-{rid}-portable.tar.gz"
        print(f"    Compressing {source_dir} -> {archive_path.name}")
        with tarfile.open(archive_path, "w:gz") as tar:
            for item in sorted(source_dir.rglob("*")):
                arcname = item.relative_to(source_dir)
                tar.add(item, arcname=str(arcname))

    print_success(f"Portable archive created: {archive_path}")
    return archive_path


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Grepdesk Build & Packaging CLI Tool",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  python build.py                           # Full build, test, publish, installer & zip for current OS
  python build.py --action publish          # Publish self-contained binaries
  python build.py --action installer        # Compile setup.iss into Setup.exe
  python build.py --action portable         # Create portable .zip
  python build.py --os linux                # Build & package for Linux
  python build.py --clean                   # Clean build artifacts
        """
    )

    parser.add_argument(
        "action",
        nargs="?",
        default="all",
        choices=["all", "build", "test", "publish", "installer", "portable", "clean"],
        help="Action to perform (default: 'all')"
    )
    parser.add_argument(
        "--os", "-o",
        dest="target_os",
        default="current",
        help="Target operating system: 'current' (default), 'windows', 'linux', 'macos'"
    )
    parser.add_argument(
        "--config", "-c",
        default="Release",
        choices=["Release", "Debug"],
        help="Build configuration (default: Release)"
    )
    parser.add_argument(
        "--no-test",
        action="store_true",
        help="Skip tests during the build workflow"
    )
    parser.add_argument(
        "--no-installer",
        action="store_true",
        help="Skip creating the Inno Setup installer during 'all'"
    )
    parser.add_argument(
        "--distclean",
        action="store_true",
        help="When running 'clean', also purge the Output/ directory"
    )
    parser.add_argument(
        "--version", "-v",
        action="store_true",
        help="Show the current project version and exit"
    )

    args = parser.parse_args()

    version = get_version()
    if args.version:
        print(f"Grepdesk version: {version}")
        return

    target_os = normalize_os(args.target_os)
    host_os = platform.system().lower()

    print_header(f"Grepdesk CLI Build Tool (v{version})")
    print(f"Target OS:      {target_os.upper()} (Host: {host_os.upper()})")
    print(f"Configuration:  {args.config}")
    print(f"Action:         {args.action.upper()}")

    start_time = time.time()

    if args.action == "clean":
        action_clean(distclean=args.distclean)
        return

    if args.action in ("all", "build"):
        action_build(args.config)

    if args.action in ("all", "test"):
        if not args.no_test:
            action_test(args.config)
        else:
            print_step("Skipping tests (--no-test specified)")

    if args.action in ("all", "publish"):
        action_publish(target_os, args.config)

    if args.action in ("all", "installer"):
        if not args.no_installer:
            action_installer(target_os, version)
        else:
            print_step("Skipping installer generation (--no-installer specified)")

    if args.action in ("all", "portable"):
        action_portable(target_os, version)

    elapsed = time.time() - start_time
    print_header(f"Completed '{args.action}' in {elapsed:.2f}s")


if __name__ == "__main__":
    main()
