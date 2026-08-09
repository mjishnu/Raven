# Pull request summary

This fork adds an optional portable workflow for locally available MSIX/AppX packages, together with reproducible single-file build tooling.

## Main changes

- Add a local package picker for `.msix`, `.appx`, `.msixbundle`, and `.appxbundle` files.
- Extract compatible desktop packages without registering them as normal MSIX installations.
- Detect and launch the main executable when unpackaged execution is supported.
- Add the detected executable folder to the current user's PATH.
- Create a per-user Start menu shortcut so compatible portable apps can be found through Windows Start/Search.
- Add a self-contained x64 OneFile launcher that embeds the complete Raven payload and extracts it into `%LOCALAPPDATA%\Raven\OneFile\`.
- Add `BUILD_ONEFILE.bat` for local reproducible builds.
- Add GitHub Actions workflows for OneFile builds and releases.
- Add prominent licensing and liability notices advising users to use the feature only with applications they own or are licensed to use.

## Licensing notice

The portable functionality is intended for legitimate use with packages the user owns or is licensed to use. The README and release notes explicitly advise against downloading, installing, extracting, or running paid applications without a valid license.

## Notes

Not every MSIX/AppX application can run unpackaged. Applications that depend on package identity, Store licensing APIs, deployment-time registration, COM registration, services, drivers, shell extensions, or other package-specific infrastructure may still require normal installation.
