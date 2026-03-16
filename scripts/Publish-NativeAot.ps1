param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "WhisperSTT.App\WhisperSTT.App.csproj"

$msvcRoot = "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools\MSVC"
$msvcVersion = Get-ChildItem $msvcRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
if ($null -eq $msvcVersion) {
    throw "MSVC tools were not found under $msvcRoot."
}

$windowsKitRoot = "C:\Program Files (x86)\Windows Kits\10"
$sdkVersion = Get-ChildItem (Join-Path $windowsKitRoot "Lib") -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object Name -Descending |
    Select-Object -First 1
if ($null -eq $sdkVersion) {
    throw "Windows SDK libraries were not found under $windowsKitRoot."
}

$toolPaths = @(
    (Join-Path $msvcVersion.FullName "bin\Hostx64\x64"),
    (Join-Path $windowsKitRoot "bin\$($sdkVersion.Name)\x64")
)

$libPaths = @(
    (Join-Path $msvcVersion.FullName "lib\x64"),
    (Join-Path $sdkVersion.FullName "ucrt\x64"),
    (Join-Path $sdkVersion.FullName "um\x64")
)

$env:Path = ($toolPaths -join ";") + ";" + $env:Path
$env:LIB = $libPaths -join ";"
$env:LIBPATH = $env:LIB

dotnet publish $projectPath -c $Configuration -r $RuntimeIdentifier --self-contained true -p:PublishAot=true -p:PublishSingleFile=false
