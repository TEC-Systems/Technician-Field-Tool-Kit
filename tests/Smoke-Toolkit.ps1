#requires -version 5.1
$ErrorActionPreference = 'Stop'
$env:LOCALAPPDATA = Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\smoke-appdata'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'TEC-Systems-FieldToolkit.ps1') -TestMode

function Assert-Toolkit {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

$names = @($tabs.TabPages | ForEach-Object { $_.Text })
Assert-Toolkit ($names.Count -eq 5) 'Expected exactly five visible tabs.'
Assert-Toolkit (($names -join ',') -eq 'Windows Troubleshooting,IP Shifter,Network Troubleshooting,IP Scanner,Feedback') 'Unexpected visible tabs.'
Assert-Toolkit ((ConvertFrom-ScannerIpNumber (ConvertTo-ScannerIpNumber '192.168.10.42')) -eq '192.168.10.42') 'IPv4 range conversion failed.'

$script:AdapterList = @([pscustomobject]@{
    Name = 'Test Ethernet'; Status = 'Connected'; IPText = '192.168.10.42'; MaskText = '255.255.255.0'
})
Refresh-ScannerAdapters
Assert-Toolkit ($script:txtScanStart.Text -eq '192.168.10.1') 'Scanner start range did not come from adapter subnet.'
Assert-Toolkit ($script:txtScanEnd.Text -eq '192.168.10.254') 'Scanner end range did not come from adapter subnet.'

$script:txtScanStart.Text = '127.0.0.1'
$script:txtScanEnd.Text = '127.0.0.1'
Start-LanScan
Assert-Toolkit ($null -ne $script:scanJob) 'Scanner did not start.'
$null = Wait-Job -Job $script:scanJob -Timeout 20
Complete-LanScan
Assert-Toolkit ($script:lvScan.Items.Count -eq 1) 'Scanner did not find the loopback host.'
Assert-Toolkit ($script:lvScan.Items[0].Text -eq '127.0.0.1') 'Scanner reported the wrong IP.'
Assert-Toolkit ($script:lvScan.Items[0].SubItems.Count -eq 5) 'Scanner columns are incomplete.'

$form.Show()
[System.Windows.Forms.Application]::DoEvents()
Assert-Toolkit $script:trayIcon.Visible 'Notification-area icon was not shown.'
$form.WindowState = [System.Windows.Forms.FormWindowState]::Minimized
[System.Windows.Forms.Application]::DoEvents()
Assert-Toolkit (-not $form.Visible) 'Minimizing did not hide the window.'
Assert-Toolkit $script:trayIcon.Visible 'Notification-area icon disappeared while minimized.'
Restore-ToolkitFromTray
[System.Windows.Forms.Application]::DoEvents()
Assert-Toolkit ($form.Visible -and $form.WindowState -eq [System.Windows.Forms.FormWindowState]::Normal) 'Tray restore failed.'
$form.Close()
Assert-Toolkit (-not $form.Visible -and $script:trayIcon.Visible) 'Closing the window did not keep the toolkit in the notification area.'
Restore-ToolkitFromTray
Exit-Toolkit
Assert-Toolkit (-not $script:trayIcon.Visible) 'Notification-area icon remained after Exit.'
$form.Dispose()
Write-Host 'Toolkit smoke tests passed.'
