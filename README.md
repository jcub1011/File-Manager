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
