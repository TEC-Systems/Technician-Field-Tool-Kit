# TEC Systems Field Toolkit

Windows field toolkit for TEC Systems technicians. The installed app is native C# WinForms on .NET Framework; it does not launch or host PowerShell. It includes Windows and network checks, an IP shifter, IP scanner, feedback, and a technician log. The old PowerShell script remains in the repository for reference but is not installed.

## Install and run

- For a new laptop, download `TEC-Systems-FieldToolkit-Install.zip` from the latest GitHub release. Extract it, run `TEC-Systems-FieldToolkit-Setup.exe`, and click **Install**. Setup installs the native app per user, creates a Start menu shortcut, and can create a desktop shortcut. Portable EXEs in older `releases/` folders are for IT testing only. Technicians do not need PowerShell or a batch file to launch the installed toolkit.
- Double-click the **TEC Systems Field Toolkit** icon. The toolkit runs inside its own EXE process and requests Windows administrator rights when an IP change needs them.
- The original TEC emblem also appears in the Windows notification area, darkened for legibility. Minimize keeps the toolkit on the taskbar like a normal window; clicking X hides it in the tray. Single-click the tray icon to reopen it, or right-click for Open Toolkit, Check Updates, and Exit Toolkit. Only **Exit Toolkit** quits the toolkit. Windows may initially place the tray icon in the hidden-icons menu. Launching the toolkit again in the same Windows session reopens the existing instance instead of adding another tray icon.
- The installed version is shown in the toolkit header.
- The installer is currently unsigned, so Windows SmartScreen may show a warning. TEC Systems IT should sign the installer before broad deployment.

The toolkit checks GitHub Releases when it opens and every 30 minutes while running. A newer release leaves an **Update Available** button visible and shows a notification-area balloon; clicking the button or notification prompts the technician before downloading; the downloaded installer must match the published SHA-256 checksum before it runs. The **Check Updates** button checks on demand.

## Build and publish

Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\packaging\Build-Installer.ps1` on Windows to build the native app, icon, installer, and distributable ZIP in `dist`. PowerShell is used only by IT to build the release, not by the installed toolkit. The build runs the native launch/tray self-test and verifies the embedded installer payload.

To publish an update, change `version.txt` and commit the changes to `main`. From any signed-in PC or phone browser, open the repository's **Actions > Build installer > Run workflow**, select `main`, check **Publish release**, and run it. GitHub's Windows runner builds the installer and ZIP, attaches them with `SHA256SUMS.txt` to the versioned release, and makes them available to **Check Updates**. No technician laptop or IT PC needs to stay on. Pushing a matching version tag also publishes a release. Do not publish until the changes are ready for technicians.

Publishing a release makes it available for download; it does not forcibly replace a running installation. **Check Updates** downloads and verifies the update, exits the toolkit fully, updates the installed files in place, and reopens the toolkit without a separate install step. For a manually downloaded installer, click the notification-area arrow near the clock, right-click the TEC icon, choose **Exit Toolkit**, then run Setup. Closing the main window only hides the toolkit. Setup refuses to overwrite a running copy. Saved profiles and logs remain in the per-user data folder during an update.

The installer enables **Start with Windows** for the current Windows user. At sign-in the toolkit starts quietly in the notification area. Right-click the tray icon and uncheck **Start with Windows** to disable it; future updates preserve this choice. Opening the pinned app or shortcut restores the existing window, including when it was hidden with X. The application and installer include a Windows compatibility manifest.

## Saved data

Technician settings, IP profiles, saved RDP sites, and logs are saved per Windows user under `%LOCALAPPDATA%\TEC Systems\Field Toolkit`. Older BMS flow/link and screenshot files remain there but are no longer shown by this app. They are not stored in this repository or shared automatically between laptops. Back up that folder before replacing a laptop or Windows profile.

## Field diagnostics

- **Windows Troubleshooting** keeps all Open tools shortcuts visible by wrapping them to fit the available width. It opens `IPConfig /all` in a Command Prompt that stays open. Saved Remote Desktop sites are managed in the dedicated **RDP** tab. The Windows tools list still includes a shortcut to the standard Remote Desktop client. The old webpage field has been removed from this tab.
- **IP Shifter** hides Bluetooth adapters and lists other connected and disconnected adapters, shows their configured IPv4 addresses when Windows exposes them, and saves named adapter/IP/DNS profiles. Its full-width profile list adjusts column widths when the toolkit is resized. Before applying a static IP, it checks current and saved static IPv4 assignments on all local adapters, including hidden Bluetooth and disconnected adapters. An IP held by another adapter blocks the change and names that adapter; reapplying the selected adapter's own IP shows a warning in the confirmation. This checks local assignments, not every device on the network. Applying a change requires Windows administrator rights and confirmation; the toolkit reads the adapter again afterward and shows an IP assigned successfully message only when the requested IP and subnet mask are verified. A mismatch shows a verification warning.
- **IP Scanner** sits next to IP Shifter, selects a connected adapter, and fills its real IPv4 subnet. Large subnets default to a 254-address slice near the laptop. Technicians can enter any ascending range up to 1,024 addresses. It scans with ICMP and common TCP probes (22, 80, 443, 445, 3389), resolves hostnames through reverse DNS with a NetBIOS fallback, shows local ARP MACs and offline MAC-prefix owners when available, and exports CSV. Click a result column to sort it; click again to reverse the order. IP addresses and ping times sort numerically. A MAC-prefix owner may differ from the device brand. Devices that publish neither reverse DNS nor NetBIOS names will still have blank hostnames; silent hosts and filtered services may be absent.
- **Support bundle** exports the current toolkit log with network snapshots, a system summary and service list, or both into a ZIP. Review the package before sharing because it can contain sensitive machine and network details.
- **BMS Tools** currently contains **Launch YABE** and a work-in-progress note. YABE is checked in Program Files; technicians can locate Yabe.exe once if installed elsewhere. Previously saved BMS profiles remain in local settings for future use. Use the dedicated RDP tab for remote desktop connections.
- **Network Troubleshooting** includes a Telnet TCP-port check and a shortcut to the optional Windows Telnet Client. The check only proves a TCP connection can open; it does not verify login. Telnet is unencrypted, so use it only on approved networks.

## RDP connection manager

The **RDP** tab groups saved sites by folder. Select a site to edit its server/IP, username, domain, and resolution, then click **Save Site**. **New** clears the editor; **Delete** removes the selected profile after confirmation. Sites saved by earlier toolkit versions appear here automatically. Site names must be unique. A folder name groups connections like the connections tree in mRemoteNG.

Choose full screen, fit to screen (a window sized for the current monitor), or a fixed resolution. **Connect**, or double-click a saved site, opens a separate Windows Remote Desktop window using the current editor settings. Sessions are managed by Windows Remote Desktop rather than embedded inside the toolkit.

Passwords are optional. **Save password for this Windows user** encrypts the password with Windows DPAPI for the current account on this laptop. Passwords are never written as plain text to profiles, connection files, or logs. Leave the password field blank to retain an existing saved password; uncheck the option and save to remove it. Changing the server, username, or domain clears the retained password unless you enter a new one. Encrypted passwords do not transfer to another Windows user or laptop. An entered password can also be used for a single connection without saving it to the profile. Saved sites use a stable, per-site RDP file under the user's data folder. Windows display and resource preferences written to that file are retained across launches. Temporary connections and one-off passwords use disposable files removed when the launched Remote Desktop process exits; a crash may leave an encrypted file. Saved connection files are removed when the site is deleted. Windows/server policy may still prompt for credentials. For BMS sites using local accounts, leave **Domain** blank; supply it only when the site uses a domain account. Username can also use DOMAIN\user or user@domain format. Certificate trust choices are managed by Windows Remote Desktop; certificate changes and organizational policies can still require another prompt. The toolkit keeps server authentication enabled.

## Feedback

The Feedback tab displays `IT@tec-system.com`. Technicians can copy the address or open a pre-addressed Outlook Web draft in their browser. The technician writes and sends the email there. The toolkit does not send email or handle attachments. Existing screenshot files from older versions are left untouched.

For reliable unattended sending across mixed Outlook versions, TEC Systems IT would need to provide an authenticated company mail service or Microsoft Graph integration. No credentials are stored in this toolkit.

## Verification

The build also checks RDP profile persistence, folder grouping, resolution generation, password encryption/removal, second-process window activation, single-click tray restore, dark/light RDP tree colors, and the update indicator. The build runs `TEC-Systems-FieldToolkit.exe /self-test` to check native startup, visible tabs, tray close/restore/exit, and the absence of a PowerShell assembly reference. The setup also runs `/verify` against its embedded payload. Neither check sends feedback or changes adapter settings. `tests/Smoke-Toolkit.ps1` tests only the legacy script.

## Repository files

- `packaging/NativeToolkit.cs`: installed native technician GUI
- `packaging/ToolkitSetup.cs`: installer source
- `packaging/Build-Installer.ps1`: IT-side release build script
- `TEC-Systems-FieldToolkit.ps1`: legacy GUI, not installed
- `version.txt`: release version used by the installer and update check
- `assets/`: TEC Systems logo
