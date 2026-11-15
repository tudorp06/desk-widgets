# Compiles every widget source in a folder the way the app does when it installs one
# (Plugins.Compile in src\Plugins.cs): the C# compiler that ships with Windows, the same
# references, against a built DeskWidgets.exe. Nothing is run; a widget either compiles or the
# first errors are printed. Used on pull requests (.github\workflows\widgets.yml). Usage:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\compile-widgets.ps1 <folder> [-App bin\DeskWidgets.exe]
param([Parameter(Mandatory = $true)][string]$Folder, [string]$App = 'bin\DeskWidgets.exe')

$root = Split-Path -Parent $PSScriptRoot
if (-not [IO.Path]::IsPathRooted($App)) { $App = Join-Path $root $App }
if (-not (Test-Path -LiteralPath $App)) { Write-Host "$App is not built. Run build.bat first."; exit 2 }

$fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path (Join-Path $fw 'csc.exe'))) { $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$csc = Join-Path $fw 'csc.exe'

$sources = @(Get-ChildItem -LiteralPath $Folder -Filter *.cs -File -ErrorAction SilentlyContinue)
if ($sources.Count -eq 0) { Write-Host 'No widget source to compile.'; exit 0 }

$out = Join-Path ([IO.Path]::GetTempPath()) ('widget-check-' + [Guid]::NewGuid().ToString('N') + '.dll')
$failed = 0
foreach ($s in $sources) {
    # /noconfig: only the references the app gives a widget, not the compiler's default list.
    $result = & $csc /nologo /noconfig /target:library /optimize+ /codepage:65001 /utf8output "/out:$out" `
        /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
        /r:System.Xml.dll /r:System.Xml.Linq.dll /r:System.Web.Extensions.dll /r:System.Security.dll `
        "/r:$App" $s.FullName
    if ($LASTEXITCODE -eq 0) { Write-Host ("ok      {0}" -f $s.BaseName) }
    else {
        $failed++
        Write-Host ("FAILED  {0}" -f $s.BaseName)
        $result | Where-Object { $_ -match 'error' } | Select-Object -First 5 | ForEach-Object { Write-Host ("        " + $_) }
    }
    Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
}
if ($failed -gt 0) { Write-Host "$failed widget(s) do not compile with this version of Desk Widgets."; exit 1 }
Write-Host ("{0} widget(s) compile." -f $sources.Count)
exit 0
