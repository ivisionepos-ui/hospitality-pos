@echo off
rem Opens the till screen full screen in Microsoft Edge, for a touch screen.
rem To leave full screen: Alt+F4. The hub must be running ("Start hub").
start "" msedge --kiosk "http://localhost:5080/" --edge-kiosk-type=fullscreen --no-first-run
