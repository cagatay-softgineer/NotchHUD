# Builds every release download into .\dist:
#   NotchHUD-<ver>-setup.exe     installer (self-contained, no .NET needed)
#   NotchHUD-<ver>-portable.exe  single self-contained exe
#   NotchHUD-<ver>-win-x64.zip   small zip, needs the .NET 10 Desktop Runtime
# The version comes from <Version> in NotchHUD.csproj.
# The built-in updater picks the download that matches how NotchHUD was installed,
# so every release must include all three with these names.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$version = ([xml](Get-Content NotchHUD.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in NotchHUD.csproj' }
$dist = Join-Path $PSScriptRoot 'dist'
$work = Join-Path $PSScriptRoot 'obj\release'
Remove-Item $dist, $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $dist, $work | Out-Null

function Invoke-Publish([string]$out, [string[]]$extra) {
    dotnet publish -c Release -r win-x64 -o $out @extra | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
}

Write-Host "== NotchHUD ${version}: zip (framework-dependent)"
Invoke-Publish "$work\zip" @('--self-contained', 'false')
Copy-Item LICENSE, README.md "$work\zip"
Compress-Archive -Path "$work\zip\*" -DestinationPath "$dist\NotchHUD-$version-win-x64.zip"

Write-Host "== portable (single file, self-contained)"
Invoke-Publish "$work\portable" @('--self-contained', 'true', '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true')
Copy-Item "$work\portable\NotchHUD.exe" "$dist\NotchHUD-$version-portable.exe"

Write-Host "== installer (self-contained)"
Invoke-Publish "$work\setup" @('--self-contained', 'true')
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }
& $iscc /Q "/DMyVersion=$version" "/DSourceDir=$work\setup" "/DOutputDir=$dist" installer\NotchHUD.iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

Get-ChildItem $dist | ForEach-Object { '{0,-36} {1,7:N1} MB' -f $_.Name, ($_.Length / 1MB) }
