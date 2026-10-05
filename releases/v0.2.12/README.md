# TEC Systems Field Toolkit 0.2.12

Fixes the **Release checksum is missing** error during in-app updates. The published checksum uses a line ending accepted by v0.2.10 and v0.2.11; this version's updater accepts both Windows and Unix line endings. It also retains the IP Scanner tab immediately next to IP Shifter.

**Update from the running toolkit:** Click **Check Updates**, accept v0.2.12, then click **Update** in Setup. The toolkit exits fully before Setup replaces it.

**Manual install:** Download `TEC-Systems-FieldToolkit-Install.zip`, extract it, and run `TEC-Systems-FieldToolkit-Setup.exe`. If the toolkit is running, right-click its TEC notification-area icon and choose **Exit Toolkit** first.

The `portable/` folder is for IT testing only; its EXE is not the installer. The installer is currently unsigned.
