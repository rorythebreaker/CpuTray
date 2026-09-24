# Сборка CpuTray.exe штатным компилятором .NET Framework 4 (ставить ничего не нужно).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
New-Item -ItemType Directory -Force "$root\dist" | Out-Null
$res = Get-ChildItem "$root\native" -File | ForEach-Object { "/resource:$($_.FullName),$($_.Name)" }
& $csc /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 `
    /win32manifest:"$root\src\app.manifest" /win32icon:"$root\src\app.ico" `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    @res /out:"$root\dist\CpuTray.exe" "$root\src\CpuTray.cs"
if ($LASTEXITCODE -ne 0) { throw "Сборка не удалась" }
Write-Host "Готово: $root\dist\CpuTray.exe"
