# Builds CamRemote into .\dist. Needs the Sony SDK unpacked in vendor\CrSDK (see README).
# -Output and -SelfContained are for package-windows.ps1.
param([string]$Output = 'dist', [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

foreach ($tool in 'cmake', 'dotnet', 'python') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool not found. See 'Build it yourself' in README.md." }
}

$sdk = 'vendor\CrSDK\RemoteCli'
# Sony ships the sample (with the SDK binaries) as RemoteCli.zip inside the SDK; unpack it if that hasn't been done.
if (-not (Test-Path $sdk) -and (Test-Path 'vendor\CrSDK\RemoteCli.zip')) {
    Expand-Archive 'vendor\CrSDK\RemoteCli.zip' 'vendor\CrSDK\RemoteCli'
}
if (-not (Test-Path "$sdk\external\crsdk\Cr_Core.lib")) {
    throw "Sony SDK not found: expected $sdk\external\crsdk\Cr_Core.lib. See 'Build it yourself' in README.md."
}

python -I tools\gen_names.py "$sdk\app\CRSDK" app\CamRemote\SdkNames.g.cs
if ($LASTEXITCODE) { throw "Generating SdkNames.g.cs failed." }
cmake -S native\CamBridge -B build\bridge -A x64 | Out-Null
if ($LASTEXITCODE) { throw "CMake couldn't set up the C++ build. Is Visual Studio's 'Desktop development with C++' installed?" }
cmake --build build\bridge --config Release
if ($LASTEXITCODE) { throw "Building CamBridge failed." }
dotnet publish app\CamRemote\CamRemote.csproj -c Release -o $Output -p:SelfContained=$($SelfContained.IsPresent.ToString().ToLower())
if ($LASTEXITCODE) { throw "Building the app failed." }
Write-Host "Built $Output\CamRemote.exe"
