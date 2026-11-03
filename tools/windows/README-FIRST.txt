POS Hub for Windows
===================

You need: the .NET 10 runtime (ASP.NET Core Runtime 10.0) installed.
Unzip this folder somewhere permanent, e.g. C:\PosHub (not inside a zip viewer).

Start hub.bat
  Runs the hub and opens the till screen. Keep the hub window open while trading.
  First time: the till screen asks for your name and a PIN (you become the owner).
  Then press "Load sample menu" to try it with a typical takeaway menu.

Open till (full screen).bat
  Opens the till screen full screen in Microsoft Edge, best on a touch screen.
  Alt+F4 leaves full screen.

Another PC or tablet as a till (later)
  It needs the shop certificate installed once (see the main README), then open
  https://<hub PC address>:5443 and enter the code from More > Add a till.

1 - Print test (PDF files).bat
  Saves a sample receipt and kitchen ticket as PDF files in data\prints.

2 - Print test (Microsoft Print to PDF).bat
  Prints the same samples through Windows to "Microsoft Print to PDF".

3 - Restore a backup (drag a backup onto this).bat
  Close the hub first. Drag a backup file from data\backups onto it.
  The data it replaces is kept in data\before-restore-..., never deleted.

Backups
  Made automatically into data\backups: at start-up if due, every hour when
  something changed, and straight after each Z report. Each one is checked after
  it is written. 48 hours of hourly backups are kept, then one a day for 30 days.
  For safety against a broken disk or a stolen PC, set "CopyFolder" in
  appsettings.json (section "Backup") to a USB stick or another drive,
  e.g.  "CopyFolder": "E:\\PosBackups"   (note the double backslash).
  The tills get a warning if a backup or its copy fails.

If Windows says "Windows protected your PC", click "More info" then "Run anyway"
(the program is not yet signed with a certificate).

If something fails, the window shows the reason: please copy it and send it back.
