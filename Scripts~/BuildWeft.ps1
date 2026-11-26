<#
.SYNOPSIS
Builds weft and the plugin's native code into Dev (Runtime/Plugins/x86_64/weft.dll) and Release (Runtime/Plugins~/Release/x86_64/weft.dll).

.DESCRIPTION
Weft (https://github.com/JiahaoLi01/weft) is built from a checkout of its own (by default the folder `weft` next to
this repository, or WEFT_SOURCE_DIR) with CMake and Visual Studio 2022, against Unity's native plugin headers (by
default those of E:/Unity/Editor, or UNITY_EDITOR_DIR); the Vulkan SDK's DXC adds the Vulkan backend. Both DLLs export
the same C ABI:

  Runtime/Plugins/x86_64/weft.dll           Dev build (WEFT_DEVTOOLS=ON): Editor, PlayMode and Development players.
  Runtime/Plugins~/Release/x86_64/weft.dll  Release build (WEFT_DEVTOOLS=OFF): swapped in for Release players
                                            automatically by Editor/WeftBuildProcessor.cs (no DevTools, no WS2_32.dll).

The shader effect compiler scripts (compile_effect.cmake, check_effect.cmake, scene.hlsl) are copied to Tools~/Effects/.
Run it again after the weft checkout changes, with Unity editors that loaded the old DLL closed.

.EXAMPLE
powershell -File Scripts~/BuildWeft.ps1 -WeftSource F:/Projects/weft -UnityEditor E:/Unity/Editor
#>
param(
    [string]$WeftSource = $env:WEFT_SOURCE_DIR,
    [string]$UnityEditor = $(if ($env:UNITY_EDITOR_DIR) { $env:UNITY_EDITOR_DIR } else { "E:/Unity/Editor" }),
    [ValidateSet("Release", "RelWithDebInfo")]
    [string]$Config = "Release",
    [switch]$SkipRelease
)
# Native tools write warnings to stderr; their exit codes say whether they failed.
$ErrorActionPreference = "Continue"
$package = Split-Path -Parent $PSScriptRoot
if (-not $WeftSource) {
    $WeftSource = Join-Path (Split-Path -Parent $package) "weft"
}
$WeftSource = (Resolve-Path $WeftSource -ErrorAction Stop).Path
if (-not (Test-Path (Join-Path $WeftSource "include/weft/weft.h"))) {
    throw "$WeftSource is not a weft checkout"
}
$pluginApi = Join-Path $UnityEditor "Data/PluginAPI"
if (-not (Test-Path (Join-Path $pluginApi "IUnityGraphics.h"))) {
    throw "$UnityEditor is not a Unity editor folder (no Data/PluginAPI/IUnityGraphics.h)"
}

# Dev build (WEFT_DEVTOOLS=ON) + native test runner.
$buildDev = Join-Path $package "Build~/Native"
Write-Host "Weft: building $WeftSource ($Config, Dev: WEFT_DEVTOOLS=ON) in $buildDev"
cmake -S (Join-Path $package "Native~") -B $buildDev -G "Visual Studio 17 2022" -A x64 "-DWEFT_SOURCE_DIR=$WeftSource" `
    "-DUNITY_PLUGIN_API_DIR=$pluginApi" "-DWEFT_DEVTOOLS=ON"
if ($LASTEXITCODE) { throw "cmake configure failed (Dev)" }
cmake --build $buildDev --config $Config --target weft_unity weft_unity_tests --parallel
if ($LASTEXITCODE) { throw "cmake build failed (Dev)" }

# The Dev DLL and the .meta files that import it into the editor and Windows players (fixed GUIDs, so a rebuild is the
# same asset). Editor/WeftBuildProcessor.cs replaces it with the Release DLL in non-Development player builds.
$plugins = Join-Path $package "Runtime/Plugins"
$folder = Join-Path $plugins "x86_64"
New-Item -ItemType Directory -Force $folder | Out-Null
Copy-Item (Join-Path $buildDev "$Config/weft.dll") $folder -Force

if (-not $SkipRelease) {
    $buildRelease = Join-Path $package "Build~/NativeRelease"
    Write-Host "Weft: building $WeftSource ($Config, Release: WEFT_DEVTOOLS=OFF) in $buildRelease"
    cmake -S (Join-Path $package "Native~") -B $buildRelease -G "Visual Studio 17 2022" -A x64 "-DWEFT_SOURCE_DIR=$WeftSource" `
        "-DUNITY_PLUGIN_API_DIR=$pluginApi" "-DWEFT_DEVTOOLS=OFF" "-DWEFT_UNITY_TESTS=OFF"
    if ($LASTEXITCODE) { throw "cmake configure failed (Release)" }
    cmake --build $buildRelease --config $Config --target weft_unity --parallel
    if ($LASTEXITCODE) { throw "cmake build failed (Release)" }
    $releaseFolder = Join-Path $package "Runtime/Plugins~/Release/x86_64"
    New-Item -ItemType Directory -Force $releaseFolder | Out-Null
    Copy-Item (Join-Path $buildRelease "$Config/weft.dll") $releaseFolder -Force
}

# The effect compiler with weft's shell: a game compiles its UI shader effects (-weft-effect) offline without a weft checkout.
$effects = Join-Path $package "Tools~/Effects"
New-Item -ItemType Directory -Force $effects | Out-Null
Copy-Item (Join-Path $WeftSource "cmake/compile_effect.cmake"), (Join-Path $WeftSource "cmake/check_effect.cmake"),
    (Join-Path $WeftSource "src/render/shaders/scene.hlsl") $effects -Force
function Write-Meta([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText($Path, $Text.Replace("`r`n", "`n"), (New-Object System.Text.UTF8Encoding $false))
}
$folderMeta = @"
fileFormatVersion: 2
guid: {0}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:

"@
Write-Meta "$plugins.meta" ($folderMeta -f "6f1d3a4c9b2e4f708a5d1c3e7b9f2a41")
Write-Meta "$folder.meta" ($folderMeta -f "2b8e5f0d7c3a4e19b6f4d2a8c1e7b3f5")
Write-Meta (Join-Path $folder "weft.dll.meta") @"
fileFormatVersion: 2
guid: 9c4f2e7a1b3d4c58a6e0f9d2b7c5a3e1
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 1
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      : Any
    second:
      enabled: 0
      settings:
        Exclude Editor: 0
        Exclude Linux64: 1
        Exclude OSXUniversal: 1
        Exclude Win: 1
        Exclude Win64: 0
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: x86_64
        DefaultValueInitialized: true
        OS: Windows
  - first:
      Standalone: Linux64
    second:
      enabled: 0
      settings:
        CPU: None
  - first:
      Standalone: OSXUniversal
    second:
      enabled: 0
      settings:
        CPU: None
  - first:
      Standalone: Win
    second:
      enabled: 0
      settings:
        CPU: None
  - first:
      Standalone: Win64
    second:
      enabled: 1
      settings:
        CPU: x86_64
  userData:
  assetBundleName:
  assetBundleVariant:

"@
$commit = (git -C $WeftSource rev-parse HEAD).Trim()
$branch = (git -C $WeftSource rev-parse --abbrev-ref HEAD).Trim()
Write-Host "Weft: $branch $commit -> $folder (Dev)$(if (-not $SkipRelease) { ', Runtime/Plugins~/Release/x86_64 (Release)' }), Tools~/Effects"
