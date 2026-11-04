@echo off
rem Opens the till (or kitchen screen) full screen in Microsoft Edge. Alt+F4 leaves full screen.
rem On the hub PC it opens this PC's hub; on other PCs, the hub address saved by "Connect this PC to the hub".
cd /d "%~dp0"
set "HUB=http://localhost:5080"
if exist "hub-address.txt" set /p HUB=<"hub-address.txt"
start "" msedge --kiosk "%HUB%/" --edge-kiosk-type=fullscreen --no-first-run
