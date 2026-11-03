@echo off
cd /d "%~dp0"
echo Printing a sample receipt and kitchen ticket as PDF files...
Pos.Hub.exe --print-test --Printing:Receipt:Output=Pdf --Printing:Kitchen:Output=Pdf
echo.
echo The files are in the "data\prints" folder next to this file.
pause
