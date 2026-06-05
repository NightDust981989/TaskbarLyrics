param(
    [string]$Configuration = "Release",
    [string]$Framework = "net8.0-windows10.0.22621.0",
    [switch]$Build
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repoRoot "TaskbarLyrics.sln"
$appExe = Join-Path $repoRoot "TaskbarLyrics.App\bin\$Configuration\$Framework\TaskbarLyrics.App.exe"
$processName = "TaskbarLyrics.App"

$processes = Get-Process -Name $processName -ErrorAction SilentlyContinue
foreach ($process in $processes) {
    try {
        if ($process.MainWindowHandle -ne 0) {
            [void]$process.CloseMainWindow()
            if ($process.WaitForExit(2500)) {
                continue
            }
        }

        Stop-Process -Id $process.Id -Force
        $process.WaitForExit(2500)
    }
    catch {
        Write-Warning "Failed to stop process $($process.Id): $($_.Exception.Message)"
    }
}

if ($Build) {
    dotnet build $solutionPath -c $Configuration --no-restore
}

if (-not (Test-Path -LiteralPath $appExe)) {
    throw "Application executable not found: $appExe. Run 'dotnet build TaskbarLyrics.sln -c $Configuration --no-restore' first, or call this script with -Build."
}

Start-Process -FilePath $appExe -WorkingDirectory (Split-Path -Parent $appExe) -WindowStyle Hidden
Write-Host "Restarted TaskbarLyrics from $appExe"
