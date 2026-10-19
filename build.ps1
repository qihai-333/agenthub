$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$src    = Join-Path $root 'src'
$outDir = Join-Path $root 'bin'
$fw     = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc    = Join-Path $fw 'csc.exe'

if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$files = Get-ChildItem -LiteralPath $src -Filter *.cs | ForEach-Object { $_.FullName }

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/codepage:65001'
    ('/out:' + (Join-Path $outDir 'AgentHub.exe'))
    ('/win32manifest:' + (Join-Path $src 'app.manifest'))
    ('/win32icon:' + (Join-Path $root 'icon.ico'))
    '/r:System.dll'
    '/r:System.Core.dll'
    '/r:System.Drawing.dll'
    '/r:System.Windows.Forms.dll'
    '/r:System.Security.dll'
    ('/r:' + (Join-Path $fw 'System.Web.Extensions.dll'))
) + $files

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }

if (Test-Path (Join-Path $root 'agents.json')) {
    Copy-Item -LiteralPath (Join-Path $root 'agents.json') -Destination (Join-Path $outDir 'agents.json') -Force
}
Write-Output ("OK -> " + (Join-Path $outDir 'AgentHub.exe'))
