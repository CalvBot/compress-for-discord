# Builds the app and its installer: dist\CompressForDiscord-<version>.msi
# Needs the .NET 9 SDK (WiX comes from NuGet automatically).
#
#   .\build.ps1                   version from CompressForDiscord.csproj
#   .\build.ps1 -Version 1.2.0    override the version (the release workflow passes the tag)
#   .\build.ps1 -Install          build, then install the MSI for the current user

param([string]$Version, [switch]$Install)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

if (-not $Version) { $Version = ([xml](Get-Content "$root\CompressForDiscord.csproj")).Project.PropertyGroup.Version | Select-Object -First 1 }
$Version = $Version.TrimStart("v")

$publish = "$root\bin\publish"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish "$root\CompressForDiscord.csproj" -c Release -o $publish -p:Version=$Version --nologo -v q
if ($LASTEXITCODE) { throw "App build failed (exit $LASTEXITCODE)" }

dotnet build "$root\installer\Installer.wixproj" -c Release -p:Version=$Version -p:PublishDir=$publish --nologo -v q
if ($LASTEXITCODE) { throw "Installer build failed (exit $LASTEXITCODE)" }

New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$msi = "$root\dist\CompressForDiscord-$Version.msi"
Copy-Item "$root\installer\bin\Release\CompressForDiscord.msi" $msi -Force
Write-Host "Built $msi" -ForegroundColor Green

# REINSTALLMODE=amus: replace files even when the version number is unchanged (local test builds)
if ($Install) {
    $p = Start-Process msiexec -ArgumentList "/i `"$msi`" /passive REINSTALLMODE=amus" -Wait -PassThru
    if ($p.ExitCode) { throw "Install failed (msiexec exit $($p.ExitCode))" }
    Write-Host "Installed: right-click a video > Compress for Discord, or open it from the Start menu."
}
