@echo off
rem Run ONCE on the hub PC. Lets other tills and kitchen screens on the shop network reach the hub
rem (Windows firewall rule for port 5443, only from this PC's own local network).
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Windows will ask for permission to change the firewall. Click Yes.
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
netsh advfirewall firewall delete rule name="POS Hub (tills)" >nul 2>&1
netsh advfirewall firewall add rule name="POS Hub (tills)" dir=in action=allow protocol=TCP localport=5443 remoteip=localsubnet profile=any
if errorlevel 1 (
  echo.
  echo FAILED: the firewall rule could not be added.
) else (
  echo.
  echo Done. Other tills and kitchen screens on this network can now reach the hub.
  echo On the hub's till: More - Add a till or screen shows the address to use.
)
echo.
pause
