POS Hub for Windows
===================

Nothing else needs installing: everything the hub needs is in this folder.
Unzip this folder somewhere permanent, e.g. C:\PosHub (not inside a zip viewer).

Start hub.bat
  Runs the hub and opens the till screen. Keep the hub window open while trading.
  First time: the till screen asks for your name and a PIN (you become the owner).
  Then press "Load sample menu" to try it with a typical takeaway menu.

Open till (full screen).bat
  Opens the till screen full screen in Microsoft Edge, best on a touch screen.
  Alt+F4 leaves full screen.

ONE PC IS THE HUB. Run "Start hub" only on that PC.

Allow tills to connect (hub PC, once).bat
  Run once on the hub PC (Windows asks for permission). Lets the other tills and
  kitchen screens on the shop network reach the hub.

Another Windows PC as a till or kitchen screen
  1. Copy this whole folder to that PC (do NOT run "Start hub" there).
  2. On the hub's till: More > Add a till or screen. It shows the hub address
     (e.g. https://192.168.1.20:5443), a check code and a 6-digit code.
  3. On the other PC double-click "Connect this PC to the hub (other tills)",
     type the address, compare the check code, and click Yes to install the
     shop certificate.
  4. The till opens: choose Till or Kitchen screen, enter the 6-digit code.
  5. From then on use "Open till (full screen)" on that PC.
  Give the hub PC a fixed address in the router, so the address never changes.

Tablet or Android box: install the shop certificate (see the main README), then
open the hub address in the browser.

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
