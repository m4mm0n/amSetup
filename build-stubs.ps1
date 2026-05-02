# SPDX-License-Identifier: GPL-3.0-or-later

param(
    [string[]]$Rids = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"),
    [switch]$NoAot,
    [string]$IconPath = ""
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src/amSetup/amSetup.csproj"

foreach ($rid in $Rids) {
    $out = Join-Path $PSScriptRoot "artifacts/stubs/$rid"
    $iconArgs = @()
    if (-not [string]::IsNullOrWhiteSpace($IconPath)) {
        $iconArgs += "-p:ApplicationIcon=$IconPath"
    }

    if ($NoAot) {
        dotnet publish $project -c Release -r $rid --self-contained true `
            -p:PublishSingleFile=true `
            -p:PublishTrimmed=true `
            -p:EnableCompressionInSingleFile=true `
            -p:DebugType=None `
            -p:DebugSymbols=false `
            -o $out `
            @iconArgs
    }
    else {
        dotnet publish $project -c Release -r $rid --self-contained true `
            -p:PublishAot=true `
            -p:StripSymbols=true `
            -p:IlcOptimizationPreference=Size `
            -p:DebugType=None `
            -p:DebugSymbols=false `
            -o $out `
            @iconArgs
    }

    Get-ChildItem -LiteralPath $out -Filter *.pdb -File -ErrorAction SilentlyContinue | Remove-Item -Force
    $assets = Join-Path $out "assets"
    if (Test-Path -LiteralPath $assets) {
        Remove-Item -LiteralPath $assets -Recurse -Force
    }
}
