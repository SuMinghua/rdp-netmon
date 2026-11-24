$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { Write-Host "ERROR: 未找到 .NET Framework 编译器 csc.exe"; exit 1 }

$out = Join-Path $root "RdpNetMonitor.exe"
$src = @((Join-Path $root "RdpMonitorCore.cs"), (Join-Path $root "RdpNetMonitor.cs"))
$refs = @("/r:System.dll", "/r:System.Core.dll", "/r:System.Drawing.dll", "/r:System.Windows.Forms.dll")

$args = @("/nologo", "/target:winexe", "/platform:anycpu", "/optimize+", "/out:$out") + $refs + $src
& $csc @args
if ($LASTEXITCODE -ne 0) { Write-Host "ERROR: 编译失败 (退出码 $LASTEXITCODE)"; exit 1 }
Write-Host "构建成功: $out"
