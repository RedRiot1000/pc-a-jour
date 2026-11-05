# Publie une nouvelle version : .\release.ps1 1.0.1 "Ce qui change"
# L'appli de tes amis la télécharge toute seule au prochain lancement.
param([Parameter(Mandatory = $true)][string]$Version, [string]$Notes = "")
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $here
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version attendue au format 1.2.3" }

$vfile = Join-Path $here 'src\Version.cs'
$v = [IO.File]::ReadAllText($vfile)
$v = $v -replace 'AssemblyVersion\("[^"]*"\)', "AssemblyVersion(`"$Version`")" `
        -replace 'AssemblyFileVersion\("[^"]*"\)', "AssemblyFileVersion(`"$Version`")" `
        -replace 'Version = "[^"]*"', "Version = `"$Version`""
[IO.File]::WriteAllText($vfile, $v, (New-Object Text.UTF8Encoding $false))

& (Join-Path $here 'build.ps1')

git add -A
if ($Notes -eq "") { $Notes = "Version $Version" }
git commit -m "Version $Version" -m $Notes | Out-Null
git tag "v$Version"
git push origin HEAD --tags
Write-Host "Envoyé : GitHub compile et publie v$Version dans 1 à 2 minutes (onglet Actions du dépôt)."
