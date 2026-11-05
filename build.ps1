# Build script for CondensedProduction DSP mod.
# Uses the portable Roslyn compiler in xukongweiduta/.ref/roslyn and references the local game + BepInEx profile.
param(
    [switch]$SkipDeploy
)

$ErrorActionPreference = "Stop"
$root    = $PSScriptRoot
$tools   = "d:\trae\newcode\xukongweiduta\.ref"
$game    = "d:\steam\all\steamapps\common\Dyson Sphere Program"
$managed = Join-Path $game "DSPGAME_Data\Managed"
$profile = Join-Path $env:APPDATA "Thunderstore Mod Manager\DataFolder\DysonSphereProgram\profiles\Default\BepInEx"
$core    = Join-Path $profile "core"
$plugins = Join-Path $profile "plugins"
$csc     = Join-Path $tools "roslyn\tasks\net472\csc.exe"
$refdir  = Join-Path $tools "nslib\ref\netstandard2.1"

$src      = Join-Path $root "src"
$build    = Join-Path $root "build"
$dist     = Join-Path $root "dist"
$pkg      = Join-Path $build "package"
$livePlug = Join-Path $plugins "CondensedProduction"

$rsp = Join-Path $build "build.rsp"
$utf8Bom = New-Object System.Text.UTF8Encoding($true)
$lines = New-Object System.Collections.Generic.List[string]
function Add-Line([string]$text) {
    $lines.Add($text -replace '\\','/') | Out-Null
}
function Add-Path([string]$prefix, [string]$path) {
    $lines.Add($prefix + '"' + ($path -replace '\\','/') + '"') | Out-Null
}

$lines.Add("/target:library")
$lines.Add("/nostdlib+")
$lines.Add("/langversion:9.0")
$lines.Add("/optimize+")
$lines.Add("/debug-")
$lines.Add("/nologo")
$lines.Add("/warn:2")
$lines.Add("/utf8output")
Add-Path "/out:" (Join-Path $build "CondensedProduction.dll")

# netstandard 2.1 reference assemblies
Get-ChildItem $refdir -Filter *.dll | ForEach-Object {
    Add-Path "/r:" $_.FullName
}

# Game managed assemblies: only Unity/Assembly-CSharp
Get-ChildItem $managed -Filter *.dll |
    Where-Object { $_.Name -like "UnityEngine*" -or $_.Name -like "Assembly-*" -or $_.Name -eq "XGamingRuntime.dll" } |
    ForEach-Object { Add-Path "/r:" $_.FullName }

# BepInEx + modding libraries
foreach ($dll in @(
    (Join-Path $core "BepInEx.dll"),
    (Join-Path $core "0Harmony.dll"),
    (Join-Path $plugins "CommonAPI-DSPModSave\DSPModSave.dll")
)) {
    if (Test-Path $dll) { Add-Path "/r:" $dll }
    else { throw "Missing reference: $dll" }
}

Get-ChildItem $src -Recurse -Filter *.cs | ForEach-Object {
    Add-Path "" $_.FullName
}

New-Item -ItemType Directory -Force -Path $build | Out-Null
[System.IO.File]::WriteAllLines($rsp, $lines, $utf8Bom)

Write-Host "=== Compiling ==="
& $csc /noconfig "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Compilation failed ($LASTEXITCODE)" }

Write-Host "=== Generating icon ==="
$iconTool = Join-Path $build "MakeIcon.exe"
& $csc /nologo "/out:$iconTool" (Join-Path $root "tools\MakeIcon.cs")
if ($LASTEXITCODE -ne 0) { throw "Icon tool compilation failed" }
& $iconTool $pkg (Join-Path $dist "icon.png")
if ($LASTEXITCODE -ne 0) { throw "Icon generation failed" }

# Thunderstore 包结构：根目录直接放 manifest.json / README.md / icon.png / dll，
# 安装时由 Mod Manager 解压到 BepInEx/plugins/<PackageName>/，不能再套一层 BepInEx 目录。
Write-Host "=== Packaging ==="
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pkg | Out-Null
Copy-Item -Force (Join-Path $build "CondensedProduction.dll") $pkg
Copy-Item -Force (Join-Path $dist "manifest.json") $pkg
Copy-Item -Force (Join-Path $dist "README.md") $pkg
Copy-Item -Force (Join-Path $dist "icon.png") $pkg

Write-Host "=== Packaging upload zip ==="
$zip = Join-Path $dist "CondensedProduction.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $pkg "*") -DestinationPath $zip -Force
Write-Host "Upload zip: $zip"

if (-not $SkipDeploy) {
    Write-Host "=== Deploying to Thunderstore profile ==="
    New-Item -ItemType Directory -Force -Path $livePlug | Out-Null
    Copy-Item -Force (Join-Path $build "CondensedProduction.dll") $livePlug
    Write-Host "Deployed to: $livePlug"
}

Write-Host "BUILD OK"
