@echo off
rem Run ONCE on each till PC that has its own USB receipt printer (not on the hub PC).
rem Shares that printer on the shop network as "TillPrinter", so the hub can print this till's receipts
rem and open its cash drawer. Only computers on this shop network can reach it (Windows file and printer sharing).
net session >nul 2>&1
if %errorlevel% neq 0 (
  echo Windows will ask for permission to share the printer. Click Yes.
  powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
echo Printers installed on this PC:
echo.
powershell -NoProfile -Command "$i=0; Get-Printer | Where-Object { $_.Type -eq 'Local' } | ForEach-Object { $i++; Write-Host ('  ' + $i + '. ' + $_.Name) }"
echo.
set /p CHOICE=Type the number of the receipt printer and press Enter: 
powershell -NoProfile -Command "$list=@(Get-Printer | Where-Object { $_.Type -eq 'Local' }); $n=[int]'%CHOICE%'; if ($n -lt 1 -or $n -gt $list.Count) { Write-Host 'That number is not in the list. Nothing was changed.'; exit 1 }; $p=$list[$n-1]; Set-Printer -Name $p.Name -Shared $true -ShareName 'TillPrinter'; Enable-NetFirewallRule -DisplayGroup 'File and Printer Sharing' -ErrorAction SilentlyContinue; Write-Host ''; Write-Host ('Done. ' + $p.Name + ' is shared.')"
if errorlevel 1 goto end
echo.
echo On the hub's till: More - Printers - Tills with their own printer - this till:
echo   Connected by: USB printer on a till PC
echo   Windows printer name:  \\%COMPUTERNAME%\TillPrinter
echo Then press "Test this till's printer" and "Test this till's drawer".
echo.
echo The hub PC must be able to see this PC: both on the same network, with network discovery on.
:end
echo.
pause
