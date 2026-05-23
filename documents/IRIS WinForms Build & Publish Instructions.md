# IRIS WinForms Build & Publish Instructions

## Standard Release Build

From PowerShell:

```powershell id="c1g4vn"
cd C:\AI\iris
```

Build the WinForms application:

```powershell id="j2s8pk"
dotnet build .\WindowsFormsApp\WindowsFormsApp.csproj -c Release
```

Output location:

```text id="mx7a1t"
C:\AI\iris\WindowsFormsApp\bin\Release\net8.0-windows\
```

Run:

```text id="0q7xlf"
WindowsFormsApp.exe
```

---

# Self-Contained Publish (Recommended)

Creates a deployable executable bundle that includes the .NET runtime.

From PowerShell:

```powershell id="g9y2rm"
cd C:\AI\iris
```

Publish:

```powershell id="2x6bnk"
dotnet publish .\WindowsFormsApp\WindowsFormsApp.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true
```

Published output:

```text id="jw4v3k"
C:\AI\iris\WindowsFormsApp\bin\Release\net8.0-windows\win-x64\publish\
```

Run:

```text id="fh7m2p"
WindowsFormsApp.exe
```

---

# Optional Single-File Executable

Produces a mostly standalone single EXE.

```powershell id="a5n8zc"
dotnet publish .\WindowsFormsApp\WindowsFormsApp.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true
```

---

# Common Build Problems

## File Lock Error

If build fails with:

```text id="7z1mke"
file is being used by another process
```

Close the running IRIS executable first.

---

## Missing Runtime

If using normal `build` instead of `publish`, the target machine must have:

```text id="rv9s1x"
.NET 8 Desktop Runtime
```

installed.

---

# Recommended Workflow

```text id="n5t2wr"
1. Edit code
2. Test in VS Code / Debug
3. Close IRIS
4. Publish Release build
5. Launch EXE directly
6. Repeat
```

---

# Useful Future Enhancements

Later you can add:

* Installer packaging
* Auto-update system
* Portable mode
* Signed executable
* macOS Avalonia/Maui client
* iOS companion app
* Auto-publish scripts
* One-click deployment from VS Code
