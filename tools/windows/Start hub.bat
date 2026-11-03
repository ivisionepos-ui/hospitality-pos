@echo off
title POS Hub
cd /d "%~dp0"
echo Starting the POS Hub. Keep this window open while the shop is trading.
echo The till screen opens in a few seconds (or open http://localhost:5080 in Edge or Chrome).
echo A backup is made at start-up (if due), every hour when something changed, and after each Z report.
echo.
start "" cmd /c "timeout /t 5 /nobreak >nul & start http://localhost:5080/"
Pos.Hub.exe
echo.
echo The hub has stopped.
pause
