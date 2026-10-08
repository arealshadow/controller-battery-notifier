# ---------------------------------------------------------------------------
# Builds the Windows installer (Setup.exe) for Controller Battery Notifier.
#
#   1. dotnet publish  -> installer\publish\   (self-contained, win-x64, single file)
#   2. ISCC.exe        -> installer\output\    ("Controller Battery Notifier-Setup-1.0.0.exe")
#
# Requires: .NET 9 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup)
# ---------------------------------------------------------------------------
$ErrorActionPreference = 'Stop'
$root     = Split-Path -Parent $PSScriptRoot
$proj     = Join-Path $root 'src\ControllerBatteryNotifier\ControllerBatteryNotifier.csproj'
$publish  = Join-Path $PSScriptRoot 'publish'
$script   = Join-Path $PSScriptRoot 'ControllerBatteryNotifier.iss'

# --- Locate ISCC.exe (winget per-user, then classic Program Files locations) ---
$isscCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles    'Inno Setup 6\ISCC.exe')
)
$issc = $isscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $issc) { throw "ISCC.exe not found. Install Inno Setup 6: winget install JRSoftware.InnoSetup" }

# --- 1. Publish self-contained single-file win-x64 build ---
Write-Host "==> Publishing self-contained win-x64 build..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# --- 2. Compile the Inno Setup script (run from the script's own directory so its
#      relative publish\ path resolves correctly) ---
Write-Host "==> Compiling installer with $issc" -ForegroundColor Cyan
Push-Location $PSScriptRoot
try { & $issc $script } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

# --- 3. Report ---
$setup = Get-ChildItem (Join-Path $PSScriptRoot 'output') -Filter '*.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host ""
Write-Host "DONE: $($setup.FullName)" -ForegroundColor Green
Write-Host ("      {0:N1} MB" -f ($setup.Length / 1MB))