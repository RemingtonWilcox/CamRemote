# Builds dist\CamRemote-Windows-Setup.exe, an installer that needs nothing else on the PC.
# Needs the build tools, Sony's SDK in vendor\CrSDK, and Inno Setup (winget install JRSoftware.InnoSetup).
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup not found. Install it with: winget install JRSoftware.InnoSetup" }

$publish = 'build\win-publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
& .\build.ps1 -Output $publish -SelfContained

# Sony's libraries need Microsoft's C++ runtime, which a fresh PC may not have.
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath
$crt = Get-ChildItem "$vs\VC\Redist\MSVC\*\x64\*.CRT" -Directory | Sort-Object FullName | Select-Object -Last 1
if (-not $crt) { throw "Microsoft's C++ runtime wasn't found under Visual Studio's Redist folder." }
Copy-Item "$($crt.FullName)\msvcp140.dll", "$($crt.FullName)\vcruntime140.dll", "$($crt.FullName)\vcruntime140_1.dll" $publish

python -I tools\make_notices.py "$publish\THIRD-PARTY-NOTICES.txt"
if ($LASTEXITCODE) { throw "Making the notices failed." }

& $iscc /Q tools\windows\CamRemote.iss
if ($LASTEXITCODE) { throw "Building the installer failed." }
Copy-Item vendor\CrSDK\SourceCodeOfOpenSourceSoftware\libusb.zip dist\libusb-source.zip
Write-Host "Built dist\CamRemote-Windows-Setup.exe"
