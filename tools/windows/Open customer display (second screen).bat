@echo off
rem Opens the customer display full screen on this PC's SECOND screen (the one facing the customer).
rem The first time it shows "Register this device": choose "Customer display" and type the 6-digit code
rem from More > Add a till or screen on the owner's till. After that it opens straight to the display.
rem It uses its own Edge profile, so the till on the first screen and the display are separate devices.
cd /d "%~dp0"
set "HUB=http://localhost:5080"
if exist "hub-address.txt" set /p HUB=<"hub-address.txt"
set "POS="
for /f "usebackq delims=" %%P in (`powershell -NoProfile -Command "Add-Type -AssemblyName System.Windows.Forms; $s = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1; if ($s) { '{0},{1}' -f $s.Bounds.X, $s.Bounds.Y }"`) do set "POS=%%P"
if not defined POS (
  echo Only one screen was found.
  echo Plug in the customer display screen, then in Windows Settings ^> Display choose "Extend these displays".
  pause
  exit /b 1
)
start "" msedge --kiosk "%HUB%/" --edge-kiosk-type=fullscreen --no-first-run --window-position=%POS% --user-data-dir="%LOCALAPPDATA%\PosCustomerDisplay"
