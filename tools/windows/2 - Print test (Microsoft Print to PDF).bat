@echo off
cd /d "%~dp0"
echo Printing a sample receipt and kitchen ticket through "Microsoft Print to PDF"...
Pos.Hub.exe --print-test --Printing:Receipt:Output=Windows --Printing:Kitchen:Output=Windows --Printing:Receipt:PrinterName="Microsoft Print to PDF" --Printing:Kitchen:PrinterName="Microsoft Print to PDF"
echo.
echo The files are in the "data\prints" folder next to this file.
pause
