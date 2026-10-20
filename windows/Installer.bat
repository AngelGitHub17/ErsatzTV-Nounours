@echo off
rem Double-cliquez sur ce fichier pour installer (ou mettre a jour) ErsatzTV edition Nounours.
rem Options : Installer.bat -SansDemarrageAuto   |   Installer.bat -Desinstaller
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer.ps1" %*
echo.
pause
