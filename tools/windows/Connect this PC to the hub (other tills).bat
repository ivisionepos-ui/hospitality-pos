@echo off
setlocal
cd /d "%~dp0"
title Connect this PC to the POS hub
echo This PC will be a till or kitchen screen using the hub on another PC.
echo Do NOT run "Start hub" on this PC.
echo.
echo On the hub PC's till open: More - Add a till or screen. It shows the address, e.g. https://192.168.1.20:5443
echo.
set "HUB="
set /p HUB=Type the hub address exactly as shown there: 
if "%HUB%"=="" goto :eof
if "%HUB:~-1%"=="/" set "HUB=%HUB:~0,-1%"
if /i not "%HUB:~0,8%"=="https://" set "HUB=https://%HUB%"

echo.
echo Getting the shop certificate from the hub...
set "CRT=%TEMP%\pos-hub-ca.crt"
curl.exe -k -s -f --max-time 10 -o "%CRT%" "%HUB%/ca.crt"
if errorlevel 1 (
  echo.
  echo FAILED: cannot reach %HUB%
  echo Check that: 1. the hub is running on the hub PC   2. both PCs are on the same network
  echo           3. "Allow tills to connect (hub PC, once)" was run on the hub PC   4. the address is typed right
  pause
  exit /b 1
)

for /f "usebackq delims=" %%C in (`powershell -NoProfile -Command "$c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 '%CRT%'; $h = -join ([System.Security.Cryptography.SHA256]::Create().ComputeHash($c.RawData) | ForEach-Object { $_.ToString('X2') }); $h.Substring(0,4)+'-'+$h.Substring(4,4)+'-'+$h.Substring(8,4)+'-'+$h.Substring(12,4)"`) do set "CODE=%%C"
echo.
echo Certificate check code: %CODE%
echo It must be the SAME as the check code on the hub's "Add a till or screen" screen.
choice /m "Is it the same"
if errorlevel 2 (
  echo Not installed. Make sure you typed the hub's address, then try again.
  pause
  exit /b 1
)

echo.
echo Installing the shop certificate for this Windows user (Windows asks you to confirm: click Yes)...
certutil -user -addstore Root "%CRT%" >nul
if errorlevel 1 (
  echo FAILED: the certificate was not installed.
  pause
  exit /b 1
)

> "hub-address.txt" echo %HUB%
echo.
echo Done. This PC now uses the hub at %HUB%
echo Use "Open till (full screen)" from now on. Opening the till now...
start "" msedge "%HUB%/"
echo On the till screen choose Till or Kitchen screen and enter the code from the hub.
pause
