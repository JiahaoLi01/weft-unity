<#
.SYNOPSIS
Runs the plugin's native tests, then the package's PlayMode tests on each graphics API in batch mode, in a test project it makes.

.DESCRIPTION
The native tests (Build~/Native/<config>/weft_unity_tests.exe, built by Scripts~/BuildWeft.ps1) drive weft.dll through
stand-in Unity interfaces on a D3D11 and a D3D12 device; they need no Unity. Then each API (d3d11, d3d12, vulkan) is a run of the
Unity editor (-batchmode -runTests -force-<api>) on a test project the script makes, which takes the package from this
repository and tests it (testables). The project lies outside the repository, by default in the user's temp folder:
Unity's test framework loses its own scripts in a project whose path has a folder ending in '~' (as the package's
hidden folders do). Its Library stays there between runs. Results and logs go to Build~/Tests/<api>.xml and .log.
Build weft.dll first (Scripts~/BuildWeft.ps1).

.EXAMPLE
powershell -File Scripts~/RunTests.ps1 -Apis d3d11,d3d12
#>
param(
    [string]$UnityEditor = $(if ($env:UNITY_EDITOR_DIR) { $env:UNITY_EDITOR_DIR } else { "E:/Unity/Editor" }),
    # d3d11, d3d12, vulkan: a list, or one string of them with commas (as powershell -File passes it).
    [string[]]$Apis = @("d3d11", "d3d12", "vulkan"),
    [string]$Filter = "",
    [ValidateSet("Release", "RelWithDebInfo")]
    [string]$Config = "Release",
    # Only the native tests.
    [switch]$NativeOnly,
    [string]$ProjectDir = (Join-Path ([IO.Path]::GetTempPath()) "weft-unity-tests")
)
$ErrorActionPreference = "Stop"
$Apis = @($Apis | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($api in $Apis) {
    if ($api -notin @("d3d11", "d3d12", "vulkan")) { throw "no graphics API $api (d3d11, d3d12, vulkan)" }
}
$package = Split-Path -Parent $PSScriptRoot
$unity = Join-Path $UnityEditor "Unity.exe"
if (-not (Test-Path $unity)) { throw "no Unity.exe in $UnityEditor" }
if (-not (Test-Path (Join-Path $package "Runtime/Plugins/x86_64/weft.dll"))) { throw "build weft.dll first (Scripts~/BuildWeft.ps1)" }
$native = Join-Path $package "Build~/Native/$Config/weft_unity_tests.exe"
if (-not (Test-Path $native)) { throw "no native tests at $native (Scripts~/BuildWeft.ps1 builds them)" }
foreach ($api in @("d3d11", "d3d12")) {
    Write-Host "Weft: native tests on $api"
    & $native --api $api
    if ($LASTEXITCODE) { throw "the native tests failed on $api ($LASTEXITCODE)" }
}
if ($NativeOnly) { return }

# The test project: the package from this repository, the test framework, and image encoding for the tests' PNGs.
$project = $ProjectDir
if ($project -match '~') { throw "the test project's path has a '~' ($project)" }
New-Item -ItemType Directory -Force (Join-Path $project "Assets"), (Join-Path $project "Packages") | Out-Null
$manifest = [ordered]@{
    dependencies = [ordered]@{
        "com.jiahaoli.weft" = "file:" + $package.Replace('\', '/')
        "com.unity.test-framework" = "1.4.5"
        "com.unity.modules.imageconversion" = "1.0.0"
    }
    testables = @("com.jiahaoli.weft")
}
[IO.File]::WriteAllText((Join-Path $project "Packages/manifest.json"), ($manifest | ConvertTo-Json -Depth 4), (New-Object Text.UTF8Encoding $false))
# Scenes the test runner leaves behind when a run is ended under it.
Remove-Item (Join-Path $project "Assets/InitTestScene*") -ErrorAction SilentlyContinue
$out = Join-Path $package "Build~/Tests"
New-Item -ItemType Directory -Force $out | Out-Null

$failed = @()
foreach ($api in $Apis) {
    $results = Join-Path $out "$api.xml"
    $log = Join-Path $out "$api.log"
    Remove-Item $results, $log -ErrorAction SilentlyContinue
    $arguments = @("-batchmode", "-projectPath", "`"$project`"", "-runTests", "-testPlatform", "PlayMode",
        "-testResults", "`"$results`"", "-logFile", "`"$log`"", "-force-$api")
    if ($Filter) { $arguments += @("-testFilter", $Filter) }
    Write-Host "Weft: PlayMode tests on $api"
    $process = Start-Process -FilePath $unity -ArgumentList $arguments -WorkingDirectory $package -Wait -PassThru -NoNewWindow
    if (-not (Test-Path $results)) {
        Write-Host "  no results (exit $($process.ExitCode)); see $log"
        $failed += $api
        continue
    }
    [xml]$xml = Get-Content -Raw -Encoding UTF8 $results
    $run = $xml.'test-run'
    Write-Host "  $($run.result): $($run.passed) passed, $($run.failed) failed, $($run.skipped) skipped"
    foreach ($case in $xml.SelectNodes("//test-case[@result='Failed']")) {
        Write-Host "  FAILED $($case.fullname)"
        Write-Host "    $($case.failure.message.InnerText.Trim())"
    }
    if ($run.result -notlike "Passed*") { $failed += $api }
}
if ($failed.Count) { throw "tests failed on $($failed -join ', ')" }
Write-Host "Weft: all tests passed on $($Apis -join ', ')"
