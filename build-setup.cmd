@echo off
rem Double-click: rebuilds dist\ResourceLens-Setup.exe from the current source (same as build-setup.ps1).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-setup.ps1"
if errorlevel 1 (echo. & echo BUILD FAILED) else (echo. & echo Done: dist\ResourceLens-Setup.exe)
pause
