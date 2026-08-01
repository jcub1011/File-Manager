# File-Manager
An application that acts as an extension to the built-in file explorer, used to perform advanced and routine file management.

## Getting the source

This repository vendors [TreeDataGrid](https://github.com/wieslawsoltes/TreeDataGrid) as a git
submodule under `third_party/`, so clone with submodules:

```
git clone --recurse-submodules <repo-url>
```

If you already cloned without `--recurse-submodules`, initialize it before building:

```
git submodule update --init --recursive
```

(The UI project fails the build with this instruction if the submodule is missing.)

## Publishing

```
./publish.ps1
```

Publishes both `FileManager.UI` and `FileManager.Service` into `publish/` (gitignored) as native AOT,
self-contained `win-x64` executables with no `.pdb` files. The resulting folder needs no .NET runtime on
the target machine.

The two executables must stay in the same directory: `ServiceLauncher` resolves the engine as
`FileManager.Service.exe` beside the UI's own executable
(`src/FileManager.Contracts/IPC/ServiceLauncher.cs`) unless overridden by the Settings path or the
`FILEMANAGER_SERVICE_EXE` environment variable. The script asserts this before reporting success.

Native AOT links with `link.exe`, so the build machine needs the **Desktop development with C++**
workload from the Visual Studio Installer. The script imports the Visual Studio dev shell automatically
when the linker isn't already on `PATH`.

Useful switches: `-Configuration`, `-Runtime`, `-OutputPath`, `-KeepExisting` (skip the clean),
`-SkipToolchainCheck`.
