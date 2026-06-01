# Rebuilding "Audibly Dev"

This is a personal-dev guide for rebuilding the `Audibly Dev` MSIX you have installed
from this worktree, alongside the Microsoft Store version of Audibly.

It is **not** meant to be committed upstream — it documents an uncommitted dev workflow.

## What "Audibly Dev" is

A second copy of Audibly that installs side-by-side with the Store version. It uses a
different package `Identity Name` (`Audibly.Dev` vs `38488StewartRyan.24898061B3F0E`)
so Windows treats it as a separate app with its own isolated app-data folder.

- **Store install data:** `%LocalAppData%\Packages\38488StewartRyan.24898061B3F0E_8hz582d7yec5r\LocalState\Audibly.db`
- **Dev install data:** `%LocalAppData%\Packages\Audibly.Dev_8hz582d7yec5r\LocalState\Audibly.db`

The two libraries are completely independent.

---

## Prerequisites (one-time)

1. **Windows 11** with **Developer Mode** enabled
   (Settings → Privacy & security → For developers → Developer Mode: On).

2. **.NET SDK 8.0+** installed (`dotnet --version` should print 8.x or higher).

3. **Windows App SDK 1.7** workload — installed via Visual Studio installer
   (".NET desktop development" + "Windows App SDK" components) or via
   the standalone runtime.

4. **NuGet feeds** — the project depends on Labs packages
   (`CommunityToolkit.Labs.WinUI.MarqueeText`, `MarkdownTextBlock`) that are NOT on
   nuget.org. The build command below passes the Azure DevOps feed via `--source`,
   so no global `NuGet.config` change is required.

5. **A self-signed code signing certificate** in your `Cert:\CurrentUser\My` store.
   See the one-time setup below.

---

## One-time setup

### 1. Create a self-signed code signing certificate

The cert's subject must match the package's `Publisher` field
(`CN=680AB335-56C7-4E87-81DE-D27B78AC46A3`).

Run in PowerShell:

```powershell
New-SelfSignedCertificate `
    -Type Custom `
    -Subject 'CN=680AB335-56C7-4E87-81DE-D27B78AC46A3' `
    -KeyUsage DigitalSignature `
    -FriendlyName 'Audibly Dev' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
```

Note the **Thumbprint** it prints — you'll pass it to MSBuild below.

If you ever need to look it up again:

```powershell
Get-ChildItem Cert:\CurrentUser\My |
    Where-Object Subject -eq 'CN=680AB335-56C7-4E87-81DE-D27B78AC46A3' |
    Select-Object Thumbprint, FriendlyName
```

### 2. Patch `Audibly.App/Package.appxmanifest` (uncommitted)

Apply these changes to make the build produce a side-by-side `Audibly.Dev` package
instead of overwriting the Store identity:

```diff
     <Identity
-            Name="38488StewartRyan.24898061B3F0E"
+            Name="Audibly.Dev"
             Publisher="CN=680AB335-56C7-4E87-81DE-D27B78AC46A3"
             Version="2.2.9.0"/>

-    <mp:PhoneIdentity PhoneProductId="a4e10036-a97d-4e2e-8144-0a20e6bcb775"
+    <mp:PhoneIdentity PhoneProductId="b5f11147-ba8e-4f3f-9255-1b31f7cd5786"
                       PhonePublisherId="00000000-0000-0000-0000-000000000000"/>

     <Properties>
-        <DisplayName>Audibly — Audiobook Player</DisplayName>
+        <DisplayName>Audibly Dev</DisplayName>

             <uap:VisualElements
-                    DisplayName="Audibly — Audiobook Player"
+                    DisplayName="Audibly Dev"
                     ...>
                 <uap:DefaultTile ...
-                                 ShortName="Audibly"/>
+                                 ShortName="Audibly Dev"/>
```

Leave these changes **uncommitted** so they don't end up in branches you might push
to a fork or open as a PR.

---

## Build

From the worktree root (`...\.claude\worktrees\thirsty-gauss-3c0d2b\`), replacing
`<THUMBPRINT>` with the certificate thumbprint from the setup step:

```powershell
dotnet build Audibly.App/Audibly.App.csproj `
    -c Debug `
    -p:Platform=x64 `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxPackageSigningEnabled=true `
    -p:PackageCertificateThumbprint=<THUMBPRINT> `
    -p:AppxBundle=Never `
    --source 'https://api.nuget.org/v3/index.json' `
    --source 'https://pkgs.dev.azure.com/dotnet/CommunityToolkit/_packaging/CommunityToolkit-Labs/nuget/v3/index.json'
```

On success the signed MSIX lands at:

```
Audibly.App\bin\x64\Debug\net8.0-windows10.0.19041.0\AppPackages\Audibly.App_2.2.9.0_x64_Debug_Test\Audibly.App_2.2.9.0_x64_Debug.msix
```

The build is incremental — code changes only need this same command run again. The
manifest patch from setup stays in place between builds.

---

## Install / reinstall / uninstall

The MSIX has the same `2.2.9.0` version on every build. Windows refuses to install
the same version over itself when contents differ, so the reliable loop is:
**uninstall the old, install the new.**

> **⚠️ Back up `LocalState` BEFORE every uninstall.**
>
> `Remove-AppxPackage` deletes `%LocalAppData%\Packages\Audibly.Dev_8hz582d7yec5r\`
> as part of uninstalling, which wipes `Audibly.db` (your library, watched folders,
> listening progress, AND every bookmark — both Musicolet-imported and manually
> added). Cloud backups like Backblaze typically exclude `AppData\Local` by default,
> so there is no safety net once this folder is gone.

```powershell
# 1. Back up the LocalState folder (DB + covers + cached metadata) somewhere safe.
$src = "$env:LocalAppData\Packages\Audibly.Dev_8hz582d7yec5r\LocalState"
$dst = "$env:UserProfile\Audibly.Dev_LocalState_Backup_$(Get-Date -Format yyyyMMdd_HHmmss)"
if (Test-Path $src) {
    Copy-Item -Path $src -Destination $dst -Recurse
    Write-Host "Backed up to $dst" -ForegroundColor Green
} else {
    Write-Host "No existing LocalState — first install or already uninstalled." -ForegroundColor Yellow
}

# 2. Uninstall existing dev build (Store install is untouched).
Get-AppxPackage -Name 'Audibly.Dev' | Remove-AppxPackage

# 3. Install the freshly built MSIX (and register the dev cert in TrustedPeople on first run).
#    Replace <worktree-name> with the worktree you just built from.
powershell -ExecutionPolicy Bypass -File `
  "C:\softwareDevelopment\audibly\Audibly\.claude\worktrees\<worktree-name>\Audibly.App\bin\x64\Debug\net8.0-windows10.0.19041.0\AppPackages\Audibly.App_2.2.9.0_x64_Debug_Test\Install.ps1"
```

`Install.ps1` prompts for UAC the first time it runs to add the cert to the local
`TrustedPeople` store; subsequent installs skip that step.

### Restoring from a backup

To roll back to a backed-up `LocalState` (e.g. a code change introduced a DB
corruption bug, or you just want yesterday's state):

```powershell
# Stop the app if it's running, then uninstall the current build.
Get-AppxPackage -Name 'Audibly.Dev' | Remove-AppxPackage

# Restore the backed-up LocalState (the parent dir is recreated by the install,
# but writing it here too is harmless).
$src = "$env:UserProfile\Audibly.Dev_LocalState_Backup_<timestamp>"
$dst = "$env:LocalAppData\Packages\Audibly.Dev_8hz582d7yec5r\LocalState"
New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
Copy-Item -Path "$src\*" -Destination $dst -Recurse -Force

# Reinstall.
powershell -ExecutionPolicy Bypass -File '<path-to>\Install.ps1'
```

EF Core runs any newly-added migrations against the restored DB on first launch,
so a backup taken before a schema-changing commit is still usable afterwards.

To completely remove the dev build (cert plus app):

```powershell
Get-AppxPackage -Name 'Audibly.Dev' | Remove-AppxPackage
# Optional: remove the cert from your user store
Get-ChildItem Cert:\CurrentUser\My |
    Where-Object Subject -eq 'CN=680AB335-56C7-4E87-81DE-D27B78AC46A3' |
    Remove-Item
```

---

## Common pitfalls

| Symptom                                                                                                 | Cause                                                                                                                                                                                |
|---------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `NU1101: Unable to find package CommunityToolkit.Labs.WinUI.MarqueeText`                                | The Labs feed isn't being passed. Use the `--source` flags shown in the build command.                                                                                               |
| `APPX0101: A signing key is required`                                                                   | `PackageCertificateThumbprint` is missing or wrong. Re-check the cert exists in `Cert:\CurrentUser\My` and the thumbprint matches.                                                   |
| `0x80073CF9 ... Install failed`<br/>"package has the same identity as an already-installed package..."  | Same-version reinstall blocked. Run the `Remove-AppxPackage` line first.                                                                                                             |
| `0x80073CF9 ... Deployment Add operation with target volume C: on Package 38488StewartRyan...`          | The manifest still has the Store identity — the patch from setup wasn't applied. The dev build can't overlay the Store install.                                                     |
| `Add-AppxPackage : ... Rejecting a request to register from AppxBundleManifest.xml`                    | Don't use `Add-AppxPackage -Register` against the build output — it expects packaged layout. Use `Install.ps1` instead.                                                              |
| Bookmarks/library missing after reinstall                                                               | Expected — uninstalling clears `%LocalAppData%\Packages\Audibly.Dev_8hz582d7yec5r\`. To preserve data across reinstalls, copy that folder somewhere before uninstalling and restore. |

---

## Resetting Audibly Dev's library without uninstalling

If you just want to wipe the dev library but keep the install:

```powershell
Remove-Item "$env:LocalAppData\Packages\Audibly.Dev_8hz582d7yec5r\LocalState\Audibly.db"
```

The next launch creates a fresh empty DB via EF Core migrations.
