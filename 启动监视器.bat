@echo off
cd /d "%~dp0"
if not exist RdpNetMonitor.exe (
  echo Building RdpNetMonitor...
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
  if errorlevel 1 (
    echo Build failed. See message above.
    pause
    exit /b 1
  )
)
start "" "%~dp0RdpNetMonitor.exe"
