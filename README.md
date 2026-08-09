<p align="center">
  <img src="Raven/Assets/Raven.ico" alt="Raven Logo" width="128" height="128">
</p>

<h1 align="center">Raven – Portable Fork</h1>

<p align="center">
  <b>Raven with additional portable MSIX/AppX tools and a single-file x64 build</b>
</p>

> **Only use the portable package feature for packages you own or have a valid license to use.**

This repository is a fork of [mjishnu/Raven](https://github.com/mjishnu/Raven). Raven is a modern WinUI 3 / .NET 10 alternative Microsoft Store client for Windows. The original Raven project provides Store search, downloads, installation, updates, dependency handling and package export.

This fork keeps the original Raven functionality and adds a portable-package workflow and an optional single-EXE distribution build.

## Changes in this fork

### Portable MSIX / AppX launcher

The home page contains an **Open local package** action for local packages that you are licensed to use.

Supported input formats:

- `.msix`
- `.appx`
- `.msixbundle`
- `.appxbundle`

For a compatible desktop package Raven can:

1. unpack the package without registering it as a normal MSIX installation;
2. select the appropriate application package from an MSIX/AppX bundle;
3. inspect `AppxManifest.xml` and locate the application's executable;
4. fall back to EXE detection when the manifest does not provide a directly usable executable path;
5. extract supplied dependency packages when applicable;
6. start the detected executable from the extracted application directory.

Not every MSIX/AppX application is portable. UWP applications, applications that require package identity, Store licensing APIs, registered COM components, services, drivers, shell extensions or other deployment-time registration can still require a normal package installation.

### User PATH integration

When a portable desktop application is prepared, the directory containing its selected main executable can be added to the **current user's PATH**.

This changes only the user environment variable and does not require modifying the machine-wide PATH.

### Windows Start/Search integration

Raven creates a per-user Start menu shortcut under:

```text
%APPDATA%\Microsoft\Windows\Start Menu\Programs\Raven Portable Apps\
```

This allows compatible portable applications to appear in the Windows Start menu/search.

### Portable application storage

Extracted applications are stored below:

```text
%LOCALAPPDATA%\Raven\PortableApps\
```

### Single-file Raven build

This fork also includes a special **x64 OneFile build**.

The final distributable file is:

```text
Raven-Portable.exe
```

Raven itself is a WinUI 3 application and depends on native Windows App SDK files, resources and assets. Instead of discarding those required files, the OneFile launcher embeds the complete self-contained Raven payload inside one executable.

On first launch, the embedded application is extracted to a version-specific directory below:

```text
%LOCALAPPDATA%\Raven\OneFile\
```

The launcher then starts the extracted `Raven.exe`. Subsequent launches reuse the extracted payload for that build.

This gives you one file for distribution while retaining the complete WinUI runtime payload Raven requires.

## Build with GitHub Actions

A dedicated workflow is included:

```text
.github/workflows/build-onefile.yml
```

To build it yourself on GitHub:

1. Open the repository on GitHub.
2. Open **Actions**.
3. Select **Build Raven OneFile**.
4. Click **Run workflow**.
5. Select the `main` branch and start the workflow.
6. When the build completes, open the workflow run.
7. Download the artifact **Raven-Portable-x64**.

The artifact contains:

```text
Raven-Portable.exe
Raven-Portable.exe.sha256.txt
```

The workflow also runs automatically when relevant Raven/OneFile source files are pushed to `main`.

No code-signing certificate secrets are required for this OneFile workflow. The resulting executable is therefore unsigned unless you sign it separately after the build.

## Build locally

For local builds this fork includes:

```text
BUILD_ONEFILE.bat
```

Requirements:

- Windows 10/11 x64
- Git
- .NET 10 SDK
- Internet access to `nuget.org`

Clone the repository including its submodule:

```bat
git clone --recurse-submodules https://github.com/tarekwasfy01/Raven-Free-Installation-and-Appstart.git
cd Raven-Free-Installation-and-Appstart
```

Then run:

```text
BUILD_ONEFILE.bat
```

The final files are written to:

```text
OUTPUT\Raven-Portable.exe
OUTPUT\Raven-Portable.exe.sha256.txt
```

The build script explicitly restores packages from `nuget.org`, so a Visual Studio configuration that only has **Microsoft Visual Studio Offline Packages** enabled should not prevent the build.

## Original Raven features

The upstream project includes, among other features:

- Microsoft Store search and browsing
- app detail pages
- direct package downloads
- MSIX/AppX package export
- dependency resolution
- package installation and sideloading
- update checking
- delta downloads using block maps
- installation management
- light/dark themes
- structured logging
- WinUI 3 native Windows interface

For upstream documentation and development, see [mjishnu/Raven](https://github.com/mjishnu/Raven).

## Architecture

The main components relevant to this fork are:

```text
Raven/
├── Helpers/
│   └── PortableMsixLauncher.cs     # portable extraction, EXE launch, PATH and Start menu
├── Views/
│   ├── MainPage.xaml               # portable-package UI and license notice
│   └── MainPage.xaml.cs            # local package picker and launcher integration
└── Raven.csproj                    # main WinUI 3 application

Raven.OneFileLauncher/
├── Raven.OneFileLauncher.csproj
└── Program.cs                       # embeds/extracts the self-contained Raven payload

Raven.Updater/
└── ...                              # Raven update helper

StoreListings/
└── ...                              # upstream Store API submodule

.github/workflows/
└── build-onefile.yml                # GitHub Actions x64 OneFile build

BUILD_ONEFILE.bat                    # equivalent local OneFile build
```

## Notes about portability

Extracting an MSIX/AppX package does **not** automatically make every Windows application portable. A package may depend on installation-time registration or a valid package identity. This fork attempts to launch compatible desktop payloads but does not emulate all Windows package deployment features.

Use the portable feature only when you have the legal right and any required license to use the package/application.

## System requirements

For the normal Raven application, follow the upstream project's Windows requirements. The self-contained OneFile build includes the .NET and Windows App SDK runtime payload needed by this Raven build, but the operating system still needs to satisfy Raven's Windows version requirements.

The current Raven project targets:

```text
.NET 10
Windows 10/11
x64 for the OneFile build
```

## Credits

This is a fork. The main Raven application and its original functionality were created by the upstream Raven contributors.

- Original project: [mjishnu/Raven](https://github.com/mjishnu/Raven)
- Store API library: [StoreListings](https://github.com/mjishnu/StoreListings)

Please consider contributing improvements and fixes back to the appropriate upstream projects where applicable.

## License

Raven is licensed under the **Apache License 2.0**. See [LICENSE](LICENSE) for the complete license text.
