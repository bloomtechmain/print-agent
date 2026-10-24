# Builds the agent and its installer with the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK, Visual Studio or internet connection needed.  Run:  powershell -ExecutionPolicy Bypass -File build.ps1
#   dist\BloomPrintAgent.exe                  the agent itself (portable)
#   dist\BloomPrintAgent-Setup-<version>.exe  installer to give to customers (contains the agent)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '.NET Framework 4 compiler (csc.exe) not found.' }

$version = [regex]::Match((Get-Content (Join-Path $root 'shared\AppInfo.cs') -Raw), 'Version = "([^"]+)"').Groups[1].Value
$dist = Join-Path $root 'dist'
$agent = Join-Path $dist 'BloomPrintAgent.exe'
$setup = Join-Path $dist "BloomPrintAgent-Setup-$version.exe"
$icon = Join-Path $root 'assets\app.ico'
$shared = Join-Path $root 'shared\AppInfo.cs'
$agentSources = @(Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }) + $shared
$setupSources = @(Get-ChildItem (Join-Path $root 'installer') -Filter *.cs | ForEach-Object { $_.FullName }) + $shared
New-Item -ItemType Directory -Force $dist, (Join-Path $root 'assets') | Out-Null
Get-ChildItem $dist -Filter 'BloomPrintAgent-Setup-*.exe' | Remove-Item

function Compile([string]$out, [string]$refs, [string[]]$extra, [string[]]$sources) {
    & $csc /nologo /target:winexe /optimize+ /platform:anycpu $refs "/out:$out" @extra @sources
    if ($LASTEXITCODE -ne 0) { throw "Compiling $out failed." }
}

$agentRefs = '/r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Management.dll,System.Web.Extensions.dll'
# the icon is drawn by the app itself: build once without it, export it, then build again with it
if (-not (Test-Path $icon)) {
    Compile $agent $agentRefs @() $agentSources
    Start-Process -FilePath $agent -ArgumentList '--write-icon', "`"$icon`"" -Wait
}
Compile $agent $agentRefs @("/win32icon:$icon") $agentSources

$setupRefs = '/r:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Web.Extensions.dll,Microsoft.CSharp.dll'
Compile $setup $setupRefs @("/win32icon:$icon", "/win32manifest:$(Join-Path $root 'installer\app.manifest')", "/resource:$agent,BloomPrintAgent.exe") $setupSources

Write-Host "Built $agent ($([math]::Round((Get-Item $agent).Length / 1KB)) KB)"
Write-Host "Built $setup ($([math]::Round((Get-Item $setup).Length / 1KB)) KB)"
