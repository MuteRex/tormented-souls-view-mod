@echo off
rem Double-click to install TSViewMod. Pass "uninstall" to remove it.
if /i "%~1"=="uninstall" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Uninstall
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
)
pause
