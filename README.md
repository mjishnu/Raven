<p align="center">
  <img src="Raven/Assets/Raven.ico" alt="Raven Logo" width="128" height="128">
</p>

<h1 align="center">Raven</h1>

<p align="center">
  <b>A free, open-source alternative Microsoft Store client for Windows</b>
</p>

<p align="center">
  <a href="https://github.com/mjishnu/Raven/releases"><img src="https://img.shields.io/github/v/release/mjishnu/Raven?style=flat-square&color=blue" alt="GitHub Release"></a>
  <a href="https://github.com/mjishnu/Raven/blob/main/LICENSE"><img src="https://img.shields.io/github/license/mjishnu/Raven?style=flat-square&color=green" alt="License"></a>
  <a href="https://github.com/mjishnu/Raven/stargazers"><img src="https://img.shields.io/github/stars/mjishnu/Raven?style=flat-square" alt="Stars"></a>
  <a href="https://github.com/mjishnu/Raven/issues"><img src="https://img.shields.io/github/issues/mjishnu/Raven?style=flat-square" alt="Issues"></a>
  <a href="https://discord.gg/9eeN2Wve4T"><img src="https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fdiscord.com%2Fapi%2Fv10%2Finvites%2F9eeN2Wve4T%3Fwith_counts%3Dtrue&query=%24.approximate_member_count&label=Discord&logo=discord&logoColor=white&color=5865F2&style=flat-square&suffix=%20members&cacheSeconds=3600" alt="Discord"></a>
</p>

---

Raven is a modern, native Windows application that serves as a fully-featured alternative to the Microsoft Store. It can do everything the official store does — **search, download, install, and update apps** — while also adding powerful capabilities like **sideloading external UWP/MSIX packages**, **exporting Store apps for offline use**, and **bandwidth-saving delta downloads**.

Built with **WinUI 3** and **.NET 10**, Raven delivers a clean, fluent UI that feels right at home on Windows 10 and 11.

<img width="996" height="543" alt="raven" src="https://github.com/user-attachments/assets/40229b36-df5e-419d-aea7-ca374f6e2108" />

## ✨ Features

### 🔍 Search & Browse
- **Store Search** — search the Microsoft Store catalog directly from the title bar with real-time auto-suggest (including app icons and titles).
- **Advanced Search** — filter and query apps with more granular control over results.
- **App Details** — full app detail pages with descriptions, screenshots, version info, and dependency listings.
- **Market & Language Selection** — browse the Store as it appears in any region/language combination.

### ⬇️ Downloads & Export
- **Download Store Apps** — download any app package directly from Microsoft's CDN for offline installation or archival.
- **Delta Downloads** — save bandwidth with intelligent block-level delta downloads. Only changed blocks are fetched using BlockMap diffing, drastically reducing download sizes for updates.
- **Export Packages** — download `.appx`, `.msix`, `.appxbundle`, and `.msixbundle` files for external use, backup, or redistribution to other machines.
- **Download Manager** — a full download queue with progress tracking, pause/resume, and status animations.

### 📦 Install & Sideload
- **Install Store Apps** — install downloaded packages directly, just like the Microsoft Store.
- **Sideload External Packages** — install `.appx`, `.msix`, `.appxbundle`, or `.msixbundle` files from anywhere — not just the Store. Drag-and-drop or browse to select.
- **Dependency Resolution** — automatic detection and installation of required framework dependencies.
- **Force Install** — option to forcibly reinstall or downgrade packages when a newer version is already present.

### 🔄 Updates
- **Update Checking** — scans all installed Store-signed packaged apps and compares them against the latest available versions.
- **Delta Updates** — applies block-level differential updates to minimize download sizes.
- **Batch & Individual Updates** — update all apps at once or pick and choose which ones to update.
- **Version Comparison** — intelligently determines the latest available version per architecture and OS build.

### ⚙️ General
- **Unpackaged Deployment** — runs as a standalone `.exe` without requiring MSIX packaging or Windows App Installer.
- **Theme Support** — light, dark, and system-default themes with seamless switching.
- **Structured Logging** — separate log files for runtime events, installations, and crashes via Serilog.
- **Localization-Ready** — UI strings use a resource-based localization system (`x:Uid`).
- **Self-Update Check** — checks GitHub for newer releases of Raven itself.

##  🛑 System requirements

- **Windows 10** version 1809+
- [**.NET 10**](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- [**Windows App SDK Runtime 2.1**](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads#windows-app-sdk-20)

## 🌐 How to Run

- Statisfy all [system requirements](#-system-requirements) (excluding runtimes if running self contained version)
- Download the latest version of Raven from [releases](https://github.com/mjishnu/Raven/releases) according to your system architecture.
- Extract the contents of the zip and run `raven.exe`.
- If you encounter a false antivirus positive, download `raven_cert.zip` from [releases](https://github.com/mjishnu/Raven/releases), extract the contents and install `raven.cer` or run `install_raven_cert.bat`.
- Enjoy!

## 🏗️ Architecture

Raven follows the **MVVM pattern** and uses dependency injection via `Microsoft.Extensions.Hosting`.

```
Raven.sln
├── Raven/                    # WinUI 3 Application (UI layer)
│   ├── Views/                # XAML pages: Shell, Search, App Details,
│   │                         #   Downloads, Installations, Updates, Settings
│   ├── ViewModels/           # MVVM view models (CommunityToolkit.Mvvm)
│   ├── Services/             # App-level services: navigation, downloads,
│   │                         #   package installation, update checking
│   ├── Helpers/              # Utilities: delta downloads, BlockMap parsing,
│   │                         #   download URL resolution, version comparison
│   ├── Models/               # Data models: AppInfo, DownloadItem, UpdateItem
│   ├── Contracts/            # Service interfaces
│   ├── Layouts/              # Custom WinUI layouts (VirtualGridLayout)
│   ├── Styles/               # XAML resource dictionaries
│   └── Strings/              # Localized string resources (en-us)
│
├── Raven.Updater/            # Self-update helper executable
│   └── Program.cs            # Copies update payload and relaunches Raven
│
└── StoreListings/            # Git submodule — Microsoft Store API wrapper
    └── StoreListings.Library/
        ├── StoreEdgeFDProduct.cs   # Store product queries
        ├── DCATPackage.cs          # Dependency catalog lookups
        └── FE3Handler.cs           # Package download link resolution
```

### Key Dependencies

| Package | Purpose |
|---|---|
| [Microsoft.WindowsAppSDK](https://github.com/microsoft/WindowsAppSDK) | WinUI 3 framework |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM source generators & helpers |
| [CommunityToolkit.WinUI](https://github.com/CommunityToolkit/Windows) | WinUI media controls & effects |
| [Downloader](https://github.com/bezzad/Downloader) | Multi-part file download engine |
| [WinUIEx](https://github.com/dotMorten/WinUIEx) | Window management extensions |
| [Serilog](https://serilog.net/) | Structured logging |
| [StoreListings](https://github.com/mjishnu/StoreListings) | Microsoft Store API wrapper (submodule) |

## 📋 Build Prerequisites

- **.NET 10 SDK**
- **Visual Studio 2026** with the following workloads:
  - .NET Desktop Development
  - Windows App SDK / WinUI Development
  - Windows 10 SDK (26100)

## 🚀 Getting Started

### 1. Clone the Repository

```bash
git clone --recurse-submodules https://github.com/mjishnu/Raven.git
cd Raven
```

> If you've already cloned without `--recurse-submodules`, initialize the submodule manually:
> ```bash
> git submodule update --init --recursive
> ```

### 2. Build & Run

**From Visual Studio:**
1. Open `Raven.sln`
2. Set `Raven` as the startup project
3. Select your target platform (`x64`, `x86`, or `arm64`)
4. Press **F5** to build and run

**From the command line:**
```bash
dotnet build Raven.sln -c Debug -p:Platform=x64
dotnet run --project Raven -c Debug
```

### Supported Platforms

| Architecture | Status |
|---|---|
| x64 | ✅ Supported |
| x86 | ✅ Supported |
| ARM64 | ✅ Supported |

## 🤝 Contributing

Contributions are welcome! Here's how you can help:

1. **Fork** the repository
2. **Create a branch** for your feature or fix (`git checkout -b feature/my-feature`)
3. **Commit** your changes (`git commit -m "Add my feature"`)
4. **Push** to your branch (`git push origin feature/my-feature`)
5. **Open a Pull Request**

### Code Style

- Use `x:Uid`-based localized strings in XAML — no hardcoded text
- Follow existing MVVM patterns and DI conventions

## ⭐ Acknowledgements

- [StoreListings](https://github.com/dongle-the-gadget/StoreListings) for the Microsoft Store API wrapper.
- [Alt App Installer](https://github.com/mjishnu/alt-app-installer) the predecessor to this project.

## 📜 License

This project is licensed under the **Apache License 2.0** — see the [LICENSE](LICENSE) file for details.
