# Building Grepdesk

This guide explains how to build, test, publish, and package Grepdesk from source.

---

## Prerequisites

- **[.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)** or newer (configured in [`global.json`](global.json))
- **Windows 10 / 11 (64-bit)** for full Windows shell integration and testing
- **[Inno Setup 6 or 7](https://jrsoftware.org/isinfo.php)** (required to build the installer `.exe`)
- **[Python 3.8+](https://www.python.org/)** (optional, to use the `build.py` CLI tool)

---

## ⚡ Quick Packaging with the Build CLI Tool

Grepdesk includes an automated build CLI tool ([`build.py`](build.py) and [`build.ps1`](build.ps1)) that handles building, testing, publishing, and packaging for your target OS in a single command:

```powershell
# Full release package for the current OS (Windows: build, test, publish, installer & portable zip)
python build.py
# Or using PowerShell:
.\build.ps1
```

### CLI Commands & Options

```powershell
python build.py --help

# Run specific actions
python build.py build              # Build solution
python build.py test               # Run tests
python build.py publish            # Publish self-contained binaries
python build.py installer          # Compile setup.iss into Setup.exe (Windows)
python build.py portable           # Package publish folder into a portable archive
python build.py clean              # Clean bin, obj, and publish folders

# Target a specific OS
python build.py --os windows       # Target Windows (builds win-x64, installer, portable zip)
python build.py --os linux         # Target Linux (cross-publishes linux-x64 & creates tar.gz)
python build.py --os macos         # Target macOS (cross-publishes osx-x64 & creates tar.gz)

# Flags
python build.py --no-test          # Skip test execution
python build.py --no-installer     # Skip Inno Setup compilation
python build.py -c Debug           # Use Debug configuration (default: Release)
```

---

## 1. Development & Local Run

Clone the repository and open the project directory:

```powershell
git clone https://github.com/FurkanKirat/Grepdesk.git
cd Grepdesk
```

### Restore & Build
```powershell
dotnet build
```

### Run the App
```powershell
dotnet run --project Grepdesk.UI
```

### Run Tests
```powershell
dotnet test
```

---

## 2. Release & Packaging

Grepdesk is distributed in two formats:
1. **Installer** (`Grepdesk-vX.Y.Z-win-x64-Setup.exe`)
2. **Portable Archive** (`Grepdesk-vX.Y.Z-win-x64-portable.zip`)

### Step 2.1: Publish Self-Contained Binaries

Before compiling the installer or creating the zip package, publish the application using the `win-x64` profile:

```powershell
dotnet publish Grepdesk.UI -p:PublishProfile=win-x64
```

- Target directory: `publish\win-x64\`
- Self-contained: Includes the .NET runtime, so users do not need .NET installed.
- ReadyToRun enabled: Precompiles code for fast startup.

> [!NOTE]
> The version number is defined centrally in [`Directory.Build.props`](Directory.Build.props). The published executable embeds this version, and [`setup.iss`](setup.iss) reads it automatically.

---

### Step 2.2: Build the Installer (`setup.iss`)

Make sure you completed **Step 2.1** first; `setup.iss` requires `publish\win-x64\Grepdesk.UI.exe` to read the product version and package the application files.

#### Option A: Command Line (PowerShell)
If Inno Setup is added to your `PATH`:
```powershell
iscc setup.iss
```

Or invoke `ISCC.exe` directly (e.g., Inno Setup 7 default install path):
```powershell
& "C:\Program Files\Inno Setup 7\ISCC.exe" setup.iss
```

#### Option B: Inno Setup Compiler GUI
1. Right-click [`setup.iss`](setup.iss) and select **Compile**, or open it in the Inno Setup IDE.
2. Press <kbd>Ctrl</kbd> + <kbd>F9</kbd> (or click **Build** → **Compile**).

The compiled installer will be created in:
```text
Output\Grepdesk-vX.Y.Z-win-x64-Setup.exe
```

---

### Step 2.3: Build Portable Zip

To create the portable `.zip` release from the published folder:

```powershell
$version = (Get-Item "publish\win-x64\Grepdesk.UI.exe").VersionInfo.ProductVersion
Compress-Archive -Path "publish\win-x64\*" -DestinationPath "Output\Grepdesk-v$version-win-x64-portable.zip" -Force
```

The resulting zip will be saved in `Output\`.

---

## 3. Versioning & Release Checklist

Version numbers are defined in a single place: [`Directory.Build.props`](Directory.Build.props).

```xml
<PropertyGroup>
  <Version>0.2.0</Version>
</PropertyGroup>
```

When preparing a release:
1. Update `<Version>` in `Directory.Build.props`.
2. Update [`ROADMAP.md`](ROADMAP.md) / changelog.
3. Follow the steps in [`docs/RELEASE_CHECKLIST.md`](docs/RELEASE_CHECKLIST.md).

---

## 4. Benchmarks

To execute the benchmark suite against Explorer and other tools:
```powershell
dotnet run --project Grepdesk.Benchmarks -c Release
```

To run a timed job from the CLI:
```powershell
publish\win-x64\Grepdesk.UI.exe --extract-to --benchmark archive.zip
```
Benchmark metrics are appended to `%LOCALAPPDATA%\Grepdesk\benchmark.log`.

---

## 5. Linux Testing (via Docker)

To run the test suite in a Linux container on Windows:

```powershell
docker run --rm -v "${PWD}:/src:ro" mcr.microsoft.com/dotnet/sdk:10.0 bash -c `
  "cp -r /src /work && cd /work && find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + && dotnet test Grepdesk.Tests"
```
