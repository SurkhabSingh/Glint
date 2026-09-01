param(
    [string]$Subject = "CN=Glint Phase0 Development"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build .\src\Glint.Phase0.App\Glint.Phase0.App.csproj `
        --configuration Release --runtime win-x64
    if ($LASTEXITCODE -ne 0) {
        throw "MSIX build failed with exit code $LASTEXITCODE."
    }

    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if ($null -eq $certificate) {
        $certificate = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $Subject `
            -FriendlyName "Glint Phase 0 Development Signing" `
            -CertStoreLocation "Cert:\CurrentUser\My" `
            -KeyAlgorithm RSA `
            -KeyLength 2048 `
            -HashAlgorithm SHA256 `
            -KeyUsage DigitalSignature `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3") `
            -NotAfter (Get-Date).AddYears(2)
    }

    $package = Get-ChildItem .\artifacts\msix -Recurse -Filter *.msix |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $package) {
        throw "MSIX package was not generated."
    }

    $signTool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" `
            -Recurse `
            -Filter signtool.exe `
            -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $signTool) {
        throw "Windows SDK signtool.exe was not found."
    }

    & $signTool.FullName sign /fd SHA256 /sha1 $certificate.Thumbprint /s My $package.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "MSIX signing failed with exit code $LASTEXITCODE."
    }
    Write-Output $package.FullName
}
finally {
    Pop-Location
}
