from pathlib import Path

# 1) Only an existing Raven portable install should make Run launch immediately.
app_page = Path('Raven/Views/AppPage.xaml.cs')
text = app_page.read_text(encoding='utf-8-sig')
old = '''        if (isRunAction &&\n            (PortableLaunchRegistry.Exists(productId) ||\n             (!isUnpackaged && IsPackagedInstalled(_currentProductInfo))))\n        {\n            await TryOpenCurrentAppAsync();\n            return;\n        }\n'''
new = '''        if (isRunAction && PortableLaunchRegistry.Exists(productId))\n        {\n            await TryOpenCurrentAppAsync();\n            return;\n        }\n'''
if old not in text:
    raise SystemExit('AppPage Run shortcut anchor not found')
app_page.write_text(text.replace(old, new, 1), encoding='utf-8')

# 2) Do not delete the root folder returned by the native folder picker.
launcher = Path('Raven/Helpers/PortableMsixLauncher.cs')
text = launcher.read_text(encoding='utf-8-sig')
old = '''        if (Directory.Exists(root))\n        {\n            try\n            {\n                Directory.Delete(root, recursive: true);\n            }\n            catch (Exception ex)\n            {\n                throw new IOException(\n                    $"The existing portable folder could not be replaced. Close the portable app if it is still running and try again. Folder: {root}",\n                    ex\n                );\n            }\n        }\n\n        Directory.CreateDirectory(appDir);\n        Directory.CreateDirectory(depsDir);\n'''
new = '''        Directory.CreateDirectory(root);\n\n        // Keep the user-selected root folder intact and only replace Raven-owned payload folders.\n        foreach (var ownedDirectory in new[] { appDir, depsDir })\n        {\n            if (!Directory.Exists(ownedDirectory))\n                continue;\n\n            try\n            {\n                Directory.Delete(ownedDirectory, recursive: true);\n            }\n            catch (Exception ex)\n            {\n                throw new IOException(\n                    $"The existing portable application files could not be replaced. Close the portable app if it is still running and try again. Folder: {ownedDirectory}",\n                    ex\n                );\n            }\n        }\n\n        Directory.CreateDirectory(appDir);\n        Directory.CreateDirectory(depsDir);\n'''
if old not in text:
    raise SystemExit('PortableMsixLauncher root cleanup anchor not found')
launcher.write_text(text.replace(old, new, 1), encoding='utf-8')

# 3) Release the single selected shell item too.
picker = Path('Raven/Helpers/NativeFilePicker.cs')
text = picker.read_text(encoding='utf-8-sig')
old = '''            dialog.GetResult(out var item);\n            item.GetDisplayName(SIGDN_FILESYSPATH, out var path);\n            return string.IsNullOrEmpty(path)\n                ? Array.Empty<string>()\n                : new[] { path };\n'''
new = '''            dialog.GetResult(out var item);\n            try\n            {\n                item.GetDisplayName(SIGDN_FILESYSPATH, out var path);\n                return string.IsNullOrEmpty(path)\n                    ? Array.Empty<string>()\n                    : new[] { path };\n            }\n            finally\n            {\n                Marshal.ReleaseComObject(item);\n            }\n'''
if old not in text:
    raise SystemExit('NativeFilePicker release anchor not found')
picker.write_text(text.replace(old, new, 1), encoding='utf-8')
