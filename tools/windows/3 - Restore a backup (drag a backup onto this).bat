@echo off
cd /d "%~dp0"
if "%~1"=="" (
  echo To restore, drag a backup file ^(hub-backup-...zip^) onto this file.
  echo Backups are in the "data\backups" folder, and in your copy folder if you set one.
  pause
  exit /b 1
)
echo This puts the hub's data back to how it was in this backup:
echo   %~nx1
echo.
echo Orders taken after the backup was made will not be in the hub any more.
echo The current data is NOT deleted: it is moved to a "before-restore" folder in "data".
echo Close the hub window before you continue.
echo.
choice /m "Restore this backup now"
if errorlevel 2 exit /b 1
Pos.Hub.exe --restore "%~1"
echo.
pause
