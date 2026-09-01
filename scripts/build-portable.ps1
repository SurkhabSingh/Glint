param(
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$portableRoot = Join-Path $root "artifacts\portable"
$publishDirectory = Join-Path $portableRoot $Runtime
$archive = Join-Path $portableRoot "Glint.Phase0.App-$Runtime.zip"

$resolvedRoot = [System.IO.Path]::GetFullPath($root)
$resolvedPortableRoot = [System.IO.Path]::GetFullPath($portableRoot)
$resolvedPublishDirectory = [System.IO.Path]::GetFullPath($publishDirectory)
if (-not $resolvedPortableRoot.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not $resolvedPublishDirectory.StartsWith($resolvedPortableRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Portable output resolved outside the workspace."
}

Push-Location $root
try {
    if (Test-Path $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
    if (Test-Path $archive) {
        Remove-Item -LiteralPath $archive -Force
    }

    dotnet publish .\src\Glint.Phase0.App\Glint.Phase0.App.csproj `
        --configuration Release `
        --runtime $Runtime `
        --self-contained true `
        --output $publishDirectory `
        -p:WindowsPackageType=None `
        -p:GenerateAppxPackageOnBuild=false `
        -p:WindowsAppSDKSelfContained=true `
        -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) {
        throw "Portable publish failed with exit code $LASTEXITCODE."
    }

    Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archive
    Write-Output $archive
}
finally {
    Pop-Location
}
