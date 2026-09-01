param(
    [string]$OutputPath = ".\artifacts\compatibility\machine-report.json"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
    $resolvedRoot = [System.IO.Path]::GetFullPath($root)
    if (-not $resolvedOutput.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Compatibility output must remain inside the workspace."
    }

    $outputDirectory = Split-Path -Parent $resolvedOutput
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

    $runtime = dotnet run --project .\src\Glint.Phase0.Cli `
        --configuration Debug --no-build -- compatibility |
        ConvertFrom-Json
    $os = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
    $cpu = Get-CimInstance Win32_Processor |
        Select-Object Name, Architecture, NumberOfCores, NumberOfLogicalProcessors
    $gpu = Get-CimInstance Win32_VideoController |
        Select-Object Name, DriverVersion, AdapterRAM, VideoProcessor, Status
    $system = Get-CimInstance Win32_ComputerSystem |
        Select-Object Manufacturer, Model, SystemType, TotalPhysicalMemory

    [ordered]@{
        collectedAtUtc = [DateTimeOffset]::UtcNow
        displayVersion = $os.DisplayVersion
        edition = $os.EditionID
        runtime = $runtime
        cpu = $cpu
        gpu = @($gpu)
        system = $system
    } |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $resolvedOutput -Encoding UTF8

    Write-Output $resolvedOutput
}
finally {
    Pop-Location
}
