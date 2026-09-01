param(
    [string]$OutputPath = ".\artifacts\compatibility\live-targets.json"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$cli = Join-Path $root "src\Glint.Phase0.Cli\bin\Debug\net9.0-windows10.0.22621.0\Glint.Phase0.Cli.exe"
$wpf = Join-Path $root "tools\compatibility\Glint.Phase0.WpfTarget\bin\Debug\net9.0-windows10.0.22621.0\Glint.Phase0.WpfTarget.exe"
$win32 = Join-Path $root "tools\compatibility\Glint.Phase0.Win32Target\bin\Debug\net9.0-windows10.0.22621.0\Glint.Phase0.Win32Target.exe"
$winui = Join-Path $root "src\Glint.Phase0.App\bin\Debug\net9.0-windows10.0.22621.0\win-x64\Glint.Phase0.App.exe"
$scratch = Join-Path $root "artifacts\compatibility\scratch"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class GlintWindowTestNative
{
    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr window, int command);
}
"@

function Wait-MainWindow {
    param([System.Diagnostics.Process]$Process)

    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "Target process $($Process.ProcessName) exited before exposing a window."
        }
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return $Process.MainWindowHandle
        }
        Start-Sleep -Milliseconds 100
    }

    throw "Target process $($Process.ProcessName) did not expose a window."
}

function Invoke-GlintCli {
    param(
        [string]$Command,
        [IntPtr]$Handle,
        [switch]$SoftwareDevice,
        [switch]$KnownSafe,
        [switch]$DoNotActivate
    )

    New-Item -ItemType Directory -Path $scratch -Force | Out-Null
    $id = [Guid]::NewGuid().ToString("N")
    $stdout = Join-Path $scratch "$id.stdout.json"
    $stderr = Join-Path $scratch "$id.stderr.txt"
    $arguments = @($Command, "--handle", $Handle.ToInt64().ToString())
    if ($SoftwareDevice) {
        $arguments += "--software-device"
    }
    if ($KnownSafe) {
        $arguments += "--compatibility-known-safe"
    }

    if (-not $DoNotActivate) {
        [GlintWindowTestNative]::ShowWindow($Handle, 9) | Out-Null
        [GlintWindowTestNative]::SetForegroundWindow($Handle) | Out-Null
        Start-Sleep -Milliseconds 300
    }
    $process = Start-Process `
        -FilePath $cli `
        -ArgumentList $arguments `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -PassThru `
        -Wait
    $errorText = Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue
    if ($process.ExitCode -ne 0) {
        throw "Glint CLI exited $($process.ExitCode): $errorText"
    }

    return Get-Content -LiteralPath $stdout -Raw | ConvertFrom-Json
}

function Stop-TestProcess {
    param([System.Diagnostics.Process]$Process)

    if (-not $Process.HasExited) {
        $Process.CloseMainWindow() | Out-Null
        if (-not $Process.WaitForExit(2000)) {
            $Process.Kill($true)
        }
    }
}

function Test-WpfMode {
    param(
        [string]$Name,
        [string[]]$Arguments,
        [string]$Command = "probe",
        [switch]$SoftwareDevice,
        [switch]$KnownSafe,
        [switch]$DoNotActivate
    )

    $startParameters = @{
        FilePath = $wpf
        PassThru = $true
    }
    if ($Arguments.Count -gt 0) {
        $startParameters.ArgumentList = $Arguments
    }
    $target = Start-Process @startParameters
    try {
        $handle = Wait-MainWindow $target
        Start-Sleep -Milliseconds 300
        $result = Invoke-GlintCli `
            $Command `
            $handle `
            -SoftwareDevice:$SoftwareDevice `
            -KnownSafe:$KnownSafe `
            -DoNotActivate:$DoNotActivate
        return [ordered]@{ target = $Name; status = "completed"; result = $result }
    }
    catch {
        return [ordered]@{ target = $Name; status = "failed"; error = $_.Exception.Message }
    }
    finally {
        Stop-TestProcess $target
    }
}

$resolvedRoot = [System.IO.Path]::GetFullPath($root)
$resolvedOutput = [System.IO.Path]::GetFullPath((Join-Path $root $OutputPath))
if (-not $resolvedOutput.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Compatibility output must remain inside the workspace."
}

Push-Location $root
try {
    dotnet build .\Glint.Windows.Phase0.sln --configuration Debug
    if ($LASTEXITCODE -ne 0) { throw "Solution build failed." }
    dotnet build .\tools\compatibility\Glint.Phase0.WpfTarget `
        --configuration Debug
    if ($LASTEXITCODE -ne 0) { throw "WPF compatibility target build failed." }
    dotnet build .\tools\compatibility\Glint.Phase0.Win32Target `
        --configuration Debug
    if ($LASTEXITCODE -ne 0) { throw "Win32 compatibility target build failed." }

    $results = @(
        Test-WpfMode "WPF hardware D3D11" @() "capture" -KnownSafe
        Test-WpfMode "WPF WARP software D3D11" @() "capture" -SoftwareDevice -KnownSafe
        Test-WpfMode "WPF display protected" @("--protected") -DoNotActivate
        Test-WpfMode "WPF minimized" @("--minimized") -DoNotActivate
    )

    $win32Target = Start-Process -FilePath $win32 -PassThru
    try {
        $handle = Wait-MainWindow $win32Target
        $results += [ordered]@{
            target = "Win32"
            status = "completed"
            result = Invoke-GlintCli "capture" $handle -KnownSafe
        }
    }
    catch {
        $results += [ordered]@{
            target = "Win32"
            status = "failed"
            error = $_.Exception.Message
        }
    }
    finally {
        Stop-TestProcess $win32Target
    }

    $winuiTarget = Start-Process -FilePath $winui -PassThru
    try {
        $handle = Wait-MainWindow $winuiTarget
        $results += [ordered]@{
            target = "WinUI 3"
            status = "completed"
            result = Invoke-GlintCli "capture" $handle -KnownSafe
        }
    }
    catch {
        $results += [ordered]@{
            target = "WinUI 3"
            status = "failed"
            error = $_.Exception.Message
        }
    }
    finally {
        Stop-TestProcess $winuiTarget
    }

    $calculatorBefore = @(Get-Process CalculatorApp -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Id)
    $calculator = $null
    try {
        Start-Process "calculator:"
        for ($attempt = 0; $attempt -lt 100; $attempt++) {
            $candidates = @(Get-Process CalculatorApp -ErrorAction SilentlyContinue |
                Where-Object { $_.MainWindowHandle -ne [IntPtr]::Zero })
            $calculator = $candidates |
                Where-Object { $calculatorBefore -notcontains $_.Id } |
                Select-Object -First 1
            if ($null -eq $calculator) {
                $calculator = $candidates | Select-Object -First 1
            }
            if ($null -ne $calculator) { break }
            Start-Sleep -Milliseconds 100
        }
        if ($null -eq $calculator) {
            throw "Windows Calculator did not expose a window."
        }

        $handle = $calculator.MainWindowHandle
        $probe = Invoke-GlintCli "probe" $handle -DoNotActivate
        try {
            $capture = Invoke-GlintCli "capture" $handle -KnownSafe -DoNotActivate
            $results += [ordered]@{
                target = "UWP/system Calculator"
                status = "completed"
                probe = $probe
                result = $capture
            }
        }
        catch {
            $results += [ordered]@{
                target = "UWP/system Calculator"
                status = "partial"
                probe = $probe
                error = $_.Exception.Message
            }
        }
    }
    catch {
        $results += [ordered]@{
            target = "UWP/system Calculator"
            status = "skipped"
            error = $_.Exception.Message
        }
    }
    finally {
        if ($null -ne $calculator -and $calculatorBefore -notcontains $calculator.Id) {
            Stop-TestProcess $calculator
        }
    }

    $outputDirectory = Split-Path -Parent $resolvedOutput
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    [ordered]@{
        collectedAtUtc = [DateTimeOffset]::UtcNow
        results = $results
    } |
        ConvertTo-Json -Depth 10 |
        Set-Content -LiteralPath $resolvedOutput -Encoding UTF8
    Write-Output $resolvedOutput
}
finally {
    Pop-Location
}
