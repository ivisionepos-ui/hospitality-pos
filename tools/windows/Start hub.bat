@echo off
title POS Hub
cd /d "%~dp0"
echo Starting the POS Hub. Keep this window open while the shop is trading.
echo A backup is made at start-up (if due), every hour when something changed, and after each Z report.
echo.
Pos.Hub.exe
echo.
echo The hub has stopped.
pause
