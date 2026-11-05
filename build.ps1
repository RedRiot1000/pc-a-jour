# Compile PCaJour.exe (WPF) avec le compilateur C# fourni avec Windows (.NET Framework 4.x)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$dist = Join-Path $here 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

# Si l'appli est ouverte, Windows interdit de l'écraser mais autorise de la renommer
$exe = "$dist\PCaJour.exe"
if (Test-Path $exe) {
    Remove-Item "$exe.old" -ErrorAction SilentlyContinue
    if (-not (Test-Path "$exe.old")) { Move-Item $exe "$exe.old" }
}

$src = Get-ChildItem (Join-Path $here 'src') -Filter *.cs | ForEach-Object { $_.FullName }
$cscArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001',
    "/out:$exe", "/win32manifest:$here\src\app.manifest", "/win32icon:$here\src\app.ico",
    "/resource:$here\src\Theme.xaml,PCaJour.Theme.xaml",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Xml.dll', '/r:System.Xaml.dll', '/r:System.Management.dll',
    '/r:System.Web.Extensions.dll', '/r:Microsoft.CSharp.dll',
    "/r:$fw\WPF\WindowsBase.dll", "/r:$fw\WPF\PresentationCore.dll", "/r:$fw\WPF\PresentationFramework.dll")

& "$fw\csc.exe" @cscArgs @src
if ($LASTEXITCODE -ne 0) { throw "Compilation impossible" }

Copy-Item (Join-Path $here 'LISEZ-MOI.txt') $dist -Force
$zip = Join-Path $here 'PCaJour.zip'
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $exe, "$dist\LISEZ-MOI.txt" -DestinationPath $zip
Write-Host "OK : $exe et $zip"
