#requires -version 3.0
<#
    TEC Systems Field Toolkit
    PowerShell 5.1 compatible WinForms toolkit for field technicians.

    Managed by TEC Systems IT.
#>
param([switch]$TestMode)

# -------------------------------
# Initialization
# -------------------------------
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

[System.Windows.Forms.Application]::EnableVisualStyles()

trap {
    $startupMessage = $_.Exception.Message
    $startupLine = ''
    try {
        if ($_.InvocationInfo -and $_.InvocationInfo.Line) {
            $startupLine = $_.InvocationInfo.Line.Trim()
        }
    }
    catch {
    }

    try {
        $logLine = "[{0}] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $startupMessage
        if ($startupLine) {
            $logLine = "{0}`r`nLine: {1}`r`n" -f $logLine, $startupLine
        }
        Add-Content -Path (Join-Path -Path $env:TEMP -ChildPath 'TEC_FieldToolkit_StartupError.log') -Value $logLine
    }
    catch {
    }

    try {
        $display = "TEC Systems Field Toolkit could not start.`r`n`r`n{0}" -f $startupMessage
        if ($startupLine) {
            $display = "{0}`r`n`r`nLine: {1}" -f $display, $startupLine
        }
        [System.Windows.Forms.MessageBox]::Show(
            $display,
            'Toolkit Startup Error',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        ) | Out-Null
    }
    catch {
    }

    exit 1
}

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}
catch {
}

$script:AppName = 'TEC Systems Field Toolkit'
$script:ManagedBy = 'Managed by TEC Systems IT'
$script:AppFolder = Join-Path -Path $env:LOCALAPPDATA -ChildPath 'TEC Systems\Field Toolkit'
$script:ConfigFile = Join-Path -Path $script:AppFolder -ChildPath 'config.json'
$script:BmsFlowFile = Join-Path -Path $script:AppFolder -ChildPath 'bms-troubleshooting-flows-v2.json'
$script:BmsCommandsFile = Join-Path -Path $script:AppFolder -ChildPath 'bms-commands-v1.json'
$script:LinksFile = Join-Path -Path $script:AppFolder -ChildPath 'important-links-docs-v1.json'
$script:ScreenshotFolder = Join-Path -Path $script:AppFolder -ChildPath 'Screenshots'
$script:LogFile = Join-Path -Path $script:AppFolder -ChildPath ("TEC_FieldToolkit_{0:yyyyMMdd_HHmmss}.log" -f (Get-Date))
$script:LogoPath = Join-Path -Path $PSScriptRoot -ChildPath 'assets\TEC Systems Full Logo Cobalt RGB.png'
$script:ToolkitVersion = '0.0.0'
$versionPath = Join-Path -Path $PSScriptRoot -ChildPath 'version.txt'
if (Test-Path -LiteralPath $versionPath) { $script:ToolkitVersion = (Get-Content -LiteralPath $versionPath -First 1).Trim() }
$script:Config = $null
$script:AdapterList = @()
$script:BmsFlows = @()
$script:BmsCommands = @()
$script:ImportantLinks = @()
$script:CurrentBmsCategory = $null
$script:CurrentBmsStepId = $null
$script:CurrentBmsCommandName = $null
$script:IsAdminMode = $false
$script:PreserveBmsRunResult = $false
$script:BmsStepHistory = New-Object System.Collections.Generic.List[string]
$script:AdminMaxFailedAttempts = 5
$script:AdminLockoutMinutes = 10
$script:ThemeColors = $null
$script:trayIcon = $null
$script:trayMenu = $null
$script:ExitRequested = $false

if (-not (Test-Path -Path $script:AppFolder)) {
    New-Item -Path $script:AppFolder -ItemType Directory -Force | Out-Null
}
if (-not (Test-Path -Path $script:ScreenshotFolder)) {
    New-Item -Path $script:ScreenshotFolder -ItemType Directory -Force | Out-Null
}

# -------------------------------
# Configuration
# -------------------------------
function New-DefaultConfig {
    return [pscustomobject][ordered]@{
        SiteProfiles = @(
            [pscustomobject][ordered]@{
                Name = 'Sample Site'
                Adapter = ''
                IPAddress = '192.168.1.50'
                SubnetMask = '255.255.255.0'
                Gateway = '192.168.1.1'
                Dns1 = '8.8.8.8'
                Dns2 = '1.1.1.1'
            }
        )
        Notes = ''
        AdminPasswordHash = ''
        AdminPasswordHashProtected = ''
        AdminFailedAttempts = 0
        AdminLockoutUntilUtc = ''
        DarkMode = $false
        OnlineTroubleshootingUrl = 'http://127.0.0.1:8787/troubleshoot'
        OpenAIModel = 'gpt-5'
    }
}

function Protect-LocalSecret {
    param(
        [string]$PlainText,
        [string]$Purpose = 'FieldToolkitSecret'
    )

    if ([string]::IsNullOrWhiteSpace($PlainText)) {
        return ''
    }

    try {
        $plainBytes = [System.Text.Encoding]::UTF8.GetBytes($PlainText)
        $entropy = [System.Text.Encoding]::UTF8.GetBytes(('TEC Systems Field Toolkit|{0}' -f $Purpose))
        $protectedBytes = [System.Security.Cryptography.ProtectedData]::Protect(
            $plainBytes,
            $entropy,
            [System.Security.Cryptography.DataProtectionScope]::CurrentUser
        )
        return [Convert]::ToBase64String($protectedBytes)
    }
    catch {
        return ''
    }
}

function Unprotect-LocalSecret {
    param(
        [string]$ProtectedText,
        [string]$Purpose = 'FieldToolkitSecret'
    )

    if ([string]::IsNullOrWhiteSpace($ProtectedText)) {
        return ''
    }

    try {
        $protectedBytes = [Convert]::FromBase64String($ProtectedText)
        $entropy = [System.Text.Encoding]::UTF8.GetBytes(('TEC Systems Field Toolkit|{0}' -f $Purpose))
        $plainBytes = [System.Security.Cryptography.ProtectedData]::Unprotect(
            $protectedBytes,
            $entropy,
            [System.Security.Cryptography.DataProtectionScope]::CurrentUser
        )
        return [System.Text.Encoding]::UTF8.GetString($plainBytes)
    }
    catch {
        return ''
    }
}

function Save-Config {
    try {
        $script:Config | ConvertTo-Json -Depth 6 | Set-Content -Path $script:ConfigFile -Encoding UTF8
    }
    catch {
        Add-Log -Area 'Config' -Level 'ERROR' -Message ('Could not save config: {0}' -f $_.Exception.Message)
    }
}

function Load-Config {
    if (Test-Path -Path $script:ConfigFile) {
        try {
            $script:Config = Get-Content -Path $script:ConfigFile -Raw | ConvertFrom-Json
        }
        catch {
            $script:Config = New-DefaultConfig
        }
    }
    else {
        $script:Config = New-DefaultConfig
        Save-Config
    }

    if (-not $script:Config.PSObject.Properties['SiteProfiles']) {
        $script:Config | Add-Member -NotePropertyName SiteProfiles -NotePropertyValue (New-DefaultConfig).SiteProfiles
    }
    if (-not $script:Config.PSObject.Properties['Notes']) {
        $script:Config | Add-Member -NotePropertyName Notes -NotePropertyValue ''
    }
    if (-not $script:Config.PSObject.Properties['AdminPasswordHash']) {
        $script:Config | Add-Member -NotePropertyName AdminPasswordHash -NotePropertyValue ''
    }
    if (-not $script:Config.PSObject.Properties['AdminPasswordHashProtected']) {
        $script:Config | Add-Member -NotePropertyName AdminPasswordHashProtected -NotePropertyValue ''
    }
    if (-not $script:Config.PSObject.Properties['AdminFailedAttempts']) {
        $script:Config | Add-Member -NotePropertyName AdminFailedAttempts -NotePropertyValue 0
    }
    if (-not $script:Config.PSObject.Properties['AdminLockoutUntilUtc']) {
        $script:Config | Add-Member -NotePropertyName AdminLockoutUntilUtc -NotePropertyValue ''
    }
    if (-not $script:Config.PSObject.Properties['DarkMode']) {
        $script:Config | Add-Member -NotePropertyName DarkMode -NotePropertyValue $false
    }
    if (-not $script:Config.PSObject.Properties['OnlineTroubleshootingUrl']) {
        $script:Config | Add-Member -NotePropertyName OnlineTroubleshootingUrl -NotePropertyValue 'http://127.0.0.1:8787/troubleshoot'
    }
    if (-not $script:Config.PSObject.Properties['OpenAIModel']) {
        $script:Config | Add-Member -NotePropertyName OpenAIModel -NotePropertyValue 'gpt-5'
    }

    $needsConfigSave = $false
    $legacyAdminHash = [string]$script:Config.AdminPasswordHash
    $protectedAdminHash = [string]$script:Config.AdminPasswordHashProtected

    if (-not [string]::IsNullOrWhiteSpace($legacyAdminHash) -and [string]::IsNullOrWhiteSpace($protectedAdminHash)) {
        $migratedHash = Protect-LocalSecret -PlainText $legacyAdminHash -Purpose 'AdminPasswordHash'
        if (-not [string]::IsNullOrWhiteSpace($migratedHash)) {
            $script:Config.AdminPasswordHashProtected = $migratedHash
            $script:Config.AdminPasswordHash = ''
            $needsConfigSave = $true
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($legacyAdminHash)) {
        $script:Config.AdminPasswordHash = ''
        $needsConfigSave = $true
    }

    if ($script:Config.PSObject.Properties['OpenAIApiKeyProtected']) {
        [void]$script:Config.PSObject.Properties.Remove('OpenAIApiKeyProtected')
        $needsConfigSave = $true
    }

    if ($needsConfigSave) {
        Save-Config
    }
}

Load-Config

# -------------------------------
# Core Helpers
# -------------------------------
function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Restart-ToolkitElevated {
    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        throw 'Could not determine the toolkit script path for elevation.'
    }

    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList ('-NoLogo -NoProfile -ExecutionPolicy Bypass -STA -File "{0}"' -f $scriptPath) | Out-Null
}

function Ensure-ToolkitAdministrator {
    param([string]$Feature = 'run this action')

    if (Test-IsAdministrator) {
        return $true
    }

    if (Confirm-Action -Title 'Administrator Required' -Message ("{0} requires administrator rights. Relaunch the toolkit as administrator now?" -f $Feature)) {
        Restart-ToolkitElevated
        Add-Log -Area 'Admin' -Level 'INFO' -Message ('Relaunch requested for: {0}' -f $Feature)
    }
    else {
        Add-Log -Area 'Admin' -Level 'WARN' -Message ('Skipped elevation for: {0}' -f $Feature)
    }

    return $false
}

function Add-Log {
    param(
        [string]$Area,
        [ValidateSet('INFO', 'OK', 'WARN', 'ERROR')]
        [string]$Level = 'INFO',
        [string]$Message
    )

    $timeText = Get-Date -Format 'HH:mm:ss'
    $line = '[{0}] [{1}] [{2}] {3}' -f $timeText, $Level, $Area, $Message
    Add-Content -Path $script:LogFile -Value $line

    if ((Get-Variable -Name lvLog -Scope Script -ErrorAction SilentlyContinue) -and
        $script:lvLog -and
        -not $script:lvLog.IsDisposed) {
        $item = New-Object System.Windows.Forms.ListViewItem($timeText)
        [void]$item.SubItems.Add($Level)
        [void]$item.SubItems.Add($Area)
        [void]$item.SubItems.Add($Message)
        [void]$script:lvLog.Items.Add($item)
        $item.EnsureVisible()
    }
}

function Set-MainStatus {
    param(
        [string]$Text,
        [System.Drawing.Color]$Color = [System.Drawing.Color]::FromArgb(80, 90, 105)
    )

    if ((Get-Variable -Name lblStatus -Scope Script -ErrorAction SilentlyContinue) -and $script:lblStatus) {
        $script:lblStatus.Text = $Text
        $script:lblStatus.ForeColor = $Color
    }
}

function Schedule-StatusReset {
    param([int]$Milliseconds = 3500)

    if ((Get-Variable -Name statusResetTimer -Scope Script -ErrorAction SilentlyContinue) -and $script:statusResetTimer) {
        $script:statusResetTimer.Stop()
        $script:statusResetTimer.Interval = [Math]::Max(1000, $Milliseconds)
        $script:statusResetTimer.Start()
    }
}

function Invoke-UiAction {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    try {
        Set-MainStatus -Text ("Running: {0}" -f $Name) -Color ([System.Drawing.Color]::FromArgb(25, 95, 170))
        if ((Get-Variable -Name progress -Scope Script -ErrorAction SilentlyContinue) -and $script:progress) {
            $script:progress.Style = 'Marquee'
            $script:progress.MarqueeAnimationSpeed = 25
        }
        [System.Windows.Forms.Application]::DoEvents()
        Add-Log -Area $Name -Level 'INFO' -Message 'Started.'
        & $Action
        Add-Log -Area $Name -Level 'OK' -Message 'Completed.'
        if ((Get-Variable -Name statusResetTimer -Scope Script -ErrorAction SilentlyContinue) -and $script:statusResetTimer) {
            $script:statusResetTimer.Stop()
        }
        Set-MainStatus -Text 'Ready' -Color ([System.Drawing.Color]::FromArgb(45, 130, 80))
    }
    catch {
        Add-Log -Area $Name -Level 'ERROR' -Message $_.Exception.Message
        Set-MainStatus -Text 'Error: check log' -Color ([System.Drawing.Color]::FromArgb(190, 55, 55))
        Schedule-StatusReset -Milliseconds 5000
    }
    finally {
        if ((Get-Variable -Name progress -Scope Script -ErrorAction SilentlyContinue) -and $script:progress) {
            $script:progress.Style = 'Blocks'
            $script:progress.MarqueeAnimationSpeed = 0
            $script:progress.Value = 0
        }
    }
}

function Confirm-Action {
    param(
        [string]$Title,
        [string]$Message
    )

    $result = [System.Windows.Forms.MessageBox]::Show(
        $Message,
        $Title,
        [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Warning
    )

    return ($result -eq [System.Windows.Forms.DialogResult]::Yes)
}

function Get-PasswordHash {
    param([string]$Password)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Password)
        return (($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') }) -join '')
    }
    finally {
        $sha.Dispose()
    }
}

function Show-PasswordDialog {
    param(
        [string]$Title,
        [string]$Prompt,
        [switch]$ConfirmPassword
    )

    $dialog = New-Object System.Windows.Forms.Form
    $dialog.Text = $Title
    $dialog.StartPosition = 'CenterParent'
    $dialog.Size = New-Object System.Drawing.Size(360, $(if ($ConfirmPassword) { 230 } else { 180 }))
    $dialog.FormBorderStyle = 'FixedDialog'
    $dialog.MaximizeBox = $false
    $dialog.MinimizeBox = $false
    $dialog.TopMost = $true

    $label = New-Object System.Windows.Forms.Label
    $label.Text = $Prompt
    $label.Location = New-Object System.Drawing.Point(16, 16)
    $label.Size = New-Object System.Drawing.Size(310, 32)
    $dialog.Controls.Add($label)

    $txtPassword = New-Object System.Windows.Forms.TextBox
    $txtPassword.Location = New-Object System.Drawing.Point(16, 56)
    $txtPassword.Size = New-Object System.Drawing.Size(310, 24)
    $txtPassword.UseSystemPasswordChar = $true
    $dialog.Controls.Add($txtPassword)

    $txtConfirm = $null
    if ($ConfirmPassword) {
        $labelConfirm = New-Object System.Windows.Forms.Label
        $labelConfirm.Text = 'Confirm password'
        $labelConfirm.Location = New-Object System.Drawing.Point(16, 92)
        $labelConfirm.Size = New-Object System.Drawing.Size(200, 20)
        $dialog.Controls.Add($labelConfirm)

        $txtConfirm = New-Object System.Windows.Forms.TextBox
        $txtConfirm.Location = New-Object System.Drawing.Point(16, 116)
        $txtConfirm.Size = New-Object System.Drawing.Size(310, 24)
        $txtConfirm.UseSystemPasswordChar = $true
        $dialog.Controls.Add($txtConfirm)
    }

    $btnOk = New-Object System.Windows.Forms.Button
    $btnOk.Text = 'OK'
    $btnOk.Location = New-Object System.Drawing.Point(170, $(if ($ConfirmPassword) { 150 } else { 92 }))
    $btnOk.Size = New-Object System.Drawing.Size(75, 28)
    $btnOk.DialogResult = [System.Windows.Forms.DialogResult]::OK
    $dialog.Controls.Add($btnOk)

    $btnCancel = New-Object System.Windows.Forms.Button
    $btnCancel.Text = 'Cancel'
    $btnCancel.Location = New-Object System.Drawing.Point(251, $(if ($ConfirmPassword) { 150 } else { 92 }))
    $btnCancel.Size = New-Object System.Drawing.Size(75, 28)
    $btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
    $dialog.Controls.Add($btnCancel)

    $dialog.AcceptButton = $btnOk
    $dialog.CancelButton = $btnCancel

    $result = $dialog.ShowDialog()
    if ($result -ne [System.Windows.Forms.DialogResult]::OK) {
        $dialog.Dispose()
        return $null
    }

    $payload = [pscustomobject]@{
        Password = $txtPassword.Text
        ConfirmPassword = if ($txtConfirm) { $txtConfirm.Text } else { '' }
    }
    $dialog.Dispose()
    return $payload
}

function Test-AdminPasswordPolicy {
    param([string]$Password)

    if ([string]::IsNullOrWhiteSpace($Password)) {
        return 'Password cannot be blank.'
    }
    if ($Password.Length -lt 8) {
        return 'Use at least 8 characters for the admin password.'
    }

    return $null
}

function Get-AdminPasswordRecord {
    $protectedHash = if ($script:Config.PSObject.Properties['AdminPasswordHashProtected']) {
        [string]$script:Config.AdminPasswordHashProtected
    }
    else {
        ''
    }
    $legacyHash = if ($script:Config.PSObject.Properties['AdminPasswordHash']) {
        [string]$script:Config.AdminPasswordHash
    }
    else {
        ''
    }

    $hasStoredValue = (-not [string]::IsNullOrWhiteSpace($protectedHash)) -or (-not [string]::IsNullOrWhiteSpace($legacyHash))
    $hash = ''
    $isReadable = $true
    $storage = 'NotConfigured'

    if (-not [string]::IsNullOrWhiteSpace($protectedHash)) {
        $storage = 'Protected'
        $hash = Unprotect-LocalSecret -ProtectedText $protectedHash -Purpose 'AdminPasswordHash'
        if ([string]::IsNullOrWhiteSpace($hash)) {
            $isReadable = $false
        }
    }
    elseif (-not [string]::IsNullOrWhiteSpace($legacyHash)) {
        $storage = 'Legacy'
        $hash = $legacyHash
    }

    return [pscustomobject]@{
        Hash = $hash
        HasStoredValue = $hasStoredValue
        IsReadable = $isReadable
        Storage = $storage
    }
}

function Set-StoredAdminPassword {
    param([string]$Password)

    $policyMessage = Test-AdminPasswordPolicy -Password $Password
    if ($policyMessage) {
        throw $policyMessage
    }

    $hash = Get-PasswordHash -Password $Password
    $protectedHash = Protect-LocalSecret -PlainText $hash -Purpose 'AdminPasswordHash'
    if ([string]::IsNullOrWhiteSpace($protectedHash)) {
        throw 'Windows could not protect the admin password on this profile.'
    }

    $script:Config.AdminPasswordHashProtected = $protectedHash
    $script:Config.AdminPasswordHash = ''
    $script:Config.AdminFailedAttempts = 0
    $script:Config.AdminLockoutUntilUtc = ''
    Save-Config
}

function Get-AdminLockoutStatus {
    $lockoutValue = if ($script:Config.PSObject.Properties['AdminLockoutUntilUtc']) {
        [string]$script:Config.AdminLockoutUntilUtc
    }
    else {
        ''
    }

    if ([string]::IsNullOrWhiteSpace($lockoutValue)) {
        return [pscustomobject]@{
            IsLocked = $false
            Remaining = [TimeSpan]::Zero
            UntilUtc = $null
        }
    }

    try {
        $untilUtc = [datetime]::Parse($lockoutValue, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::RoundtripKind)
    }
    catch {
        $script:Config.AdminLockoutUntilUtc = ''
        Save-Config
        return [pscustomobject]@{
            IsLocked = $false
            Remaining = [TimeSpan]::Zero
            UntilUtc = $null
        }
    }

    $remaining = $untilUtc - (Get-Date).ToUniversalTime()
    if ($remaining.TotalSeconds -le 0) {
        $script:Config.AdminLockoutUntilUtc = ''
        Save-Config
        return [pscustomobject]@{
            IsLocked = $false
            Remaining = [TimeSpan]::Zero
            UntilUtc = $null
        }
    }

    return [pscustomobject]@{
        IsLocked = $true
        Remaining = $remaining
        UntilUtc = $untilUtc
    }
}

function Format-AdminLockoutRemaining {
    param([TimeSpan]$Remaining)

    if ($Remaining.TotalHours -ge 1) {
        return '{0:D2}:{1:D2}:{2:D2}' -f [math]::Floor($Remaining.TotalHours), $Remaining.Minutes, $Remaining.Seconds
    }

    return '{0:D2}:{1:D2}' -f [math]::Max(0, [math]::Floor($Remaining.TotalMinutes)), $Remaining.Seconds
}

function Clear-AdminSecurityBackoff {
    $changed = $false

    if ([int]$script:Config.AdminFailedAttempts -ne 0) {
        $script:Config.AdminFailedAttempts = 0
        $changed = $true
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$script:Config.AdminLockoutUntilUtc)) {
        $script:Config.AdminLockoutUntilUtc = ''
        $changed = $true
    }

    if ($changed) {
        Save-Config
    }
}

function Register-FailedAdminAttempt {
    $failedAttempts = 0
    if ($script:Config.PSObject.Properties['AdminFailedAttempts']) {
        $failedAttempts = [int]$script:Config.AdminFailedAttempts
    }

    $failedAttempts++
    $script:Config.AdminFailedAttempts = $failedAttempts
    $remainingBeforeLockout = [math]::Max(0, $script:AdminMaxFailedAttempts - $failedAttempts)
    $lockoutApplied = $false
    $lockoutStatus = $null

    if ($failedAttempts -ge $script:AdminMaxFailedAttempts) {
        $script:Config.AdminFailedAttempts = 0
        $script:Config.AdminLockoutUntilUtc = (Get-Date).ToUniversalTime().AddMinutes($script:AdminLockoutMinutes).ToString('o')
        $lockoutApplied = $true
        $lockoutStatus = Get-AdminLockoutStatus
    }

    Save-Config

    return [pscustomobject]@{
        FailedAttempts = $failedAttempts
        RemainingBeforeLockout = $remainingBeforeLockout
        LockoutApplied = $lockoutApplied
        LockoutStatus = $lockoutStatus
    }
}

function Get-AdminFailureDelayMilliseconds {
    $failedAttempts = 0
    if ($script:Config.PSObject.Properties['AdminFailedAttempts']) {
        $failedAttempts = [int]$script:Config.AdminFailedAttempts
    }

    return [Math]::Min(4000, (800 + ($failedAttempts * 600)))
}

function Get-AdminEditorStatusText {
    if ($script:IsAdminMode) {
        return 'Editor: Unlocked'
    }

    $lockoutStatus = Get-AdminLockoutStatus
    if ($lockoutStatus.IsLocked) {
        return 'Editor: Cooldown'
    }

    return 'Editor: Locked'
}

function Get-AdminButtonText {
    if ($script:IsAdminMode) {
        return 'Lock Editor'
    }

    $lockoutStatus = Get-AdminLockoutStatus
    if ($lockoutStatus.IsLocked) {
        return ('Locked {0}' -f (Format-AdminLockoutRemaining -Remaining $lockoutStatus.Remaining))
    }

    return 'Unlock Editor'
}

function Update-HeaderToolbarLayout {
    $searchX = 658
    if ($script:lblHeaderSearch) { $script:lblHeaderSearch.Location = New-Object System.Drawing.Point($searchX, 54) }
    if ($script:txtHeaderSearch) { $script:txtHeaderSearch.Location = New-Object System.Drawing.Point($searchX, 74) }
    if ($script:btnHeaderSearch) { $script:btnHeaderSearch.Location = New-Object System.Drawing.Point(($searchX + 134), 70) }
    if ($script:btnThemeToggle) { $script:btnThemeToggle.Location = New-Object System.Drawing.Point(($searchX + 204), 70) }
    return
    $hasPasswordButton = (
        (Get-Variable -Name btnChangeAdminPassword -Scope Script -ErrorAction SilentlyContinue) -and
        $script:btnChangeAdminPassword -and
        $script:btnChangeAdminPassword.Visible
    )

    if ((Get-Variable -Name lblAdminEditor -Scope Script -ErrorAction SilentlyContinue) -and $script:lblAdminEditor) {
        $script:lblAdminEditor.Location = New-Object System.Drawing.Point(342, 78)
        $script:lblAdminEditor.Size = New-Object System.Drawing.Size(104, 22)
    }

    if ((Get-Variable -Name btnAdminMode -Scope Script -ErrorAction SilentlyContinue) -and $script:btnAdminMode) {
        $script:btnAdminMode.Location = New-Object System.Drawing.Point(452, 70)
        $script:btnAdminMode.Size = New-Object System.Drawing.Size(120, 30)
    }

    if ($hasPasswordButton) {
        $passwordX = 582
        $searchX = 748
    }
    else {
        $passwordX = 582
        $searchX = 658
    }

    if ((Get-Variable -Name btnChangeAdminPassword -Scope Script -ErrorAction SilentlyContinue) -and $script:btnChangeAdminPassword) {
        $script:btnChangeAdminPassword.Location = New-Object System.Drawing.Point($passwordX, 70)
        $script:btnChangeAdminPassword.Size = New-Object System.Drawing.Size(136, 30)
    }

    if ((Get-Variable -Name lblHeaderSearch -Scope Script -ErrorAction SilentlyContinue) -and $script:lblHeaderSearch) {
        $script:lblHeaderSearch.Location = New-Object System.Drawing.Point($searchX, 54)
        $script:lblHeaderSearch.Size = New-Object System.Drawing.Size(110, 18)
    }

    if ((Get-Variable -Name txtHeaderSearch -Scope Script -ErrorAction SilentlyContinue) -and $script:txtHeaderSearch) {
        $script:txtHeaderSearch.Location = New-Object System.Drawing.Point($searchX, 74)
        $script:txtHeaderSearch.Size = New-Object System.Drawing.Size(126, 24)
    }

    if ((Get-Variable -Name btnHeaderSearch -Scope Script -ErrorAction SilentlyContinue) -and $script:btnHeaderSearch) {
        $script:btnHeaderSearch.Location = New-Object System.Drawing.Point(($searchX + 134), 70)
        $script:btnHeaderSearch.Size = New-Object System.Drawing.Size(62, 30)
    }

    if ((Get-Variable -Name btnThemeToggle -Scope Script -ErrorAction SilentlyContinue) -and $script:btnThemeToggle) {
        $script:btnThemeToggle.Location = New-Object System.Drawing.Point(($searchX + 204), 70)
        $script:btnThemeToggle.Size = New-Object System.Drawing.Size(86, 30)
    }
}

function Change-AdminPassword {
    if (-not (Assert-AdminMode -Feature 'change the admin password')) {
        return
    }

    $record = Get-AdminPasswordRecord
    if (-not $record.HasStoredValue) {
        [System.Windows.Forms.MessageBox]::Show(
            'Set an admin password first by unlocking the editor once.',
            'Admin Password',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information
        ) | Out-Null
        return
    }

    if (-not $record.IsReadable) {
        [System.Windows.Forms.MessageBox]::Show(
            'The stored admin password could not be read on this Windows profile. Create a fresh password from this device profile instead.',
            'Admin Password',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        ) | Out-Null
        Add-Log -Area 'Admin Mode' -Level 'ERROR' -Message 'Stored admin password could not be read for password change.'
        return
    }

    $currentPassword = Show-PasswordDialog -Title 'Verify Current Password' -Prompt 'Enter the current admin password.'
    if (-not $currentPassword) {
        return
    }

    if ((Get-PasswordHash -Password $currentPassword.Password) -ne $record.Hash) {
        Start-Sleep -Milliseconds 1200
        [System.Windows.Forms.MessageBox]::Show(
            'The current admin password is not correct.',
            'Admin Password',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        ) | Out-Null
        Add-Log -Area 'Admin Mode' -Level 'WARN' -Message 'Password change blocked because the current password was incorrect.'
        return
    }

    $newPassword = Show-PasswordDialog -Title 'Change Admin Password' -Prompt 'Enter the new admin password.' -ConfirmPassword
    if (-not $newPassword) {
        return
    }

    if ($newPassword.Password -ne $newPassword.ConfirmPassword) {
        [System.Windows.Forms.MessageBox]::Show(
            'The new passwords did not match.',
            'Admin Password',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }

    if ($newPassword.Password -eq $currentPassword.Password) {
        [System.Windows.Forms.MessageBox]::Show(
            'Pick a different password than the current one.',
            'Admin Password',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information
        ) | Out-Null
        return
    }

    try {
        Set-StoredAdminPassword -Password $newPassword.Password
        Update-AdminModeUi
        Add-Log -Area 'Admin Mode' -Level 'OK' -Message 'Admin password changed.'
        [System.Windows.Forms.MessageBox]::Show(
            'Admin password updated for this Windows profile.',
            'Admin Password',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information
        ) | Out-Null
    }
    catch {
        [System.Windows.Forms.MessageBox]::Show(
            $_.Exception.Message,
            'Admin Password',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        ) | Out-Null
        Add-Log -Area 'Admin Mode' -Level 'ERROR' -Message ('Could not change admin password: {0}' -f $_.Exception.Message)
    }
}

function Set-EditorTextBoxState {
    param(
        $Control,
        [bool]$Editable
    )

    if (-not $Control) {
        return
    }

    if ($Control -is [System.Windows.Forms.TextBoxBase]) {
        $Control.ReadOnly = -not $Editable
        $Control.BackColor = [System.Drawing.Color]::White
    }
    else {
        $Control.Enabled = $Editable
    }
}

function Update-AdminModeUi {
    $isEnabled = $script:IsAdminMode
    $editorTextBoxNames = @(
        'txtBmsCategory', 'txtBmsStepName', 'txtBmsPrompt', 'txtBmsButtonText', 'txtBmsButtonNotes',
        'txtLinkCategory', 'txtLinkTitle', 'txtLinkTarget', 'txtLinkNotes',
        'txtBmsCommandName', 'txtBmsCommandCategory', 'txtBmsCommandDescription', 'txtBmsCommandText'
    )
    $editorControlNames = @(
        'lstBmsButtons', 'lstBmsSteps', 'cboBmsNextStep',
        'cboBmsCommandType', 'chkBmsCommandRequiresAdmin', 'chkBmsCommandConfirm', 'chkBmsCommandKeepWindowOpen'
    )
    $editorButtonNames = @(
        'btnNewBmsTopic', 'btnDeleteBmsTopic', 'btnSaveBmsTopic',
        'btnNewBmsStep', 'btnDeleteBmsStep', 'btnSaveBmsStep', 'btnSetBmsStartStep',
        'btnNewBmsButton', 'btnSaveBmsButton', 'btnDeleteBmsButton',
        'btnNewLink', 'btnSaveLink', 'btnDeleteLink',
        'btnNewBmsCommand', 'btnSaveBmsCommand', 'btnDeleteBmsCommand'
    )

    foreach ($name in $editorTextBoxNames) {
        if ((Get-Variable -Name $name -Scope Script -ErrorAction SilentlyContinue) -and (Get-Variable -Name $name -Scope Script).Value) {
            Set-EditorTextBoxState -Control (Get-Variable -Name $name -Scope Script).Value -Editable $isEnabled
        }
    }

    foreach ($name in $editorControlNames + $editorButtonNames) {
        if ((Get-Variable -Name $name -Scope Script -ErrorAction SilentlyContinue) -and (Get-Variable -Name $name -Scope Script).Value) {
            (Get-Variable -Name $name -Scope Script).Value.Enabled = $isEnabled
        }
    }

    if ((Get-Variable -Name lblAdminEditor -Scope Script -ErrorAction SilentlyContinue) -and $script:lblAdminEditor) {
        $script:lblAdminEditor.Text = Get-AdminEditorStatusText
        $script:lblAdminEditor.ForeColor = if ($script:IsAdminMode) {
            [System.Drawing.Color]::FromArgb(45, 130, 80)
        }
        elseif ((Get-AdminLockoutStatus).IsLocked) {
            [System.Drawing.Color]::FromArgb(185, 70, 60)
        }
        else {
            [System.Drawing.Color]::FromArgb(190, 120, 45)
        }
    }

    if ((Get-Variable -Name btnAdminMode -Scope Script -ErrorAction SilentlyContinue) -and $script:btnAdminMode) {
        $script:btnAdminMode.Text = Get-AdminButtonText
    }

    if ((Get-Variable -Name btnChangeAdminPassword -Scope Script -ErrorAction SilentlyContinue) -and $script:btnChangeAdminPassword) {
        $script:btnChangeAdminPassword.Enabled = $script:IsAdminMode
        $script:btnChangeAdminPassword.Visible = $script:IsAdminMode
    }

    Update-HeaderToolbarLayout
    $script:lblAdminEditor.Visible = $false
    $script:btnAdminMode.Visible = $false
    $script:btnChangeAdminPassword.Visible = $false

    if ((Get-Variable -Name tvBmsFlowOutline -Scope Script -ErrorAction SilentlyContinue) -and $script:tvBmsFlowOutline) {
        $script:tvBmsFlowOutline.Visible = $script:IsAdminMode
    }
    if ((Get-Variable -Name lblBmsOutlineTitle -Scope Script -ErrorAction SilentlyContinue) -and $script:lblBmsOutlineTitle) {
        $script:lblBmsOutlineTitle.Visible = $script:IsAdminMode
    }
    if ((Get-Variable -Name lstBmsTopics -Scope Script -ErrorAction SilentlyContinue) -and $script:lstBmsTopics) {
        if ($script:IsAdminMode) {
            $script:lstBmsTopics.Size = New-Object System.Drawing.Size(180, 184)
        }
        else {
            $script:lstBmsTopics.Size = New-Object System.Drawing.Size(180, 418)
        }
    }

    if ((Get-Variable -Name tabsBmsModes -Scope Script -ErrorAction SilentlyContinue) -and
        (Get-Variable -Name tabBmsBuilder -Scope Script -ErrorAction SilentlyContinue) -and
        (Get-Variable -Name tabBmsTroubleshoot -Scope Script -ErrorAction SilentlyContinue) -and
        $script:tabsBmsModes -and $script:tabBmsBuilder -and $script:tabBmsTroubleshoot) {
        $builderPresent = $script:tabsBmsModes.TabPages.Contains($script:tabBmsBuilder)
        if ($script:IsAdminMode -and -not $builderPresent) {
            [void]$script:tabsBmsModes.TabPages.Add($script:tabBmsBuilder)
        }
        elseif (-not $script:IsAdminMode -and $builderPresent) {
            if ($script:tabsBmsModes.SelectedTab -eq $script:tabBmsBuilder) {
                $script:tabsBmsModes.SelectedTab = $script:tabBmsTroubleshoot
            }
            $script:tabsBmsModes.TabPages.Remove($script:tabBmsBuilder)
        }
    }

    if ((Get-Variable -Name tabsBmsCommandModes -Scope Script -ErrorAction SilentlyContinue) -and
        (Get-Variable -Name tabBmsCommandEditor -Scope Script -ErrorAction SilentlyContinue) -and
        (Get-Variable -Name tabBmsCommandRun -Scope Script -ErrorAction SilentlyContinue) -and
        $script:tabsBmsCommandModes -and $script:tabBmsCommandEditor -and $script:tabBmsCommandRun) {
        $editorPresent = $script:tabsBmsCommandModes.TabPages.Contains($script:tabBmsCommandEditor)
        if ($script:IsAdminMode -and -not $editorPresent) {
            [void]$script:tabsBmsCommandModes.TabPages.Add($script:tabBmsCommandEditor)
        }
        elseif (-not $script:IsAdminMode -and $editorPresent) {
            if ($script:tabsBmsCommandModes.SelectedTab -eq $script:tabBmsCommandEditor) {
                $script:tabsBmsCommandModes.SelectedTab = $script:tabBmsCommandRun
            }
            $script:tabsBmsCommandModes.TabPages.Remove($script:tabBmsCommandEditor)
        }
    }

    Update-AiSettingsUi
}

function Assert-AdminMode {
    param([string]$Feature = 'edit this section')

    if ($script:IsAdminMode) {
        return $true
    }

    [System.Windows.Forms.MessageBox]::Show(
        ("Unlock Admin Mode to {0}." -f $Feature),
        'Admin Mode',
        [System.Windows.Forms.MessageBoxButtons]::OK,
        [System.Windows.Forms.MessageBoxIcon]::Information
    ) | Out-Null
    return $false
}

function Toggle-AdminMode {
    if ($script:IsAdminMode) {
        $script:IsAdminMode = $false
        Update-AdminModeUi
        Add-Log -Area 'Admin Mode' -Level 'INFO' -Message 'Editor locked.'
        return
    }

    $lockoutStatus = Get-AdminLockoutStatus
    if ($lockoutStatus.IsLocked) {
        $remainingText = Format-AdminLockoutRemaining -Remaining $lockoutStatus.Remaining
        [System.Windows.Forms.MessageBox]::Show(
            ("Too many incorrect password attempts. Wait {0} before trying again." -f $remainingText),
            'Admin Mode',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        Update-AdminModeUi
        Add-Log -Area 'Admin Mode' -Level 'WARN' -Message ('Unlock attempt blocked during cooldown ({0} remaining).' -f $remainingText)
        return
    }

    $passwordRecord = Get-AdminPasswordRecord
    if ($passwordRecord.HasStoredValue -and -not $passwordRecord.IsReadable) {
        [System.Windows.Forms.MessageBox]::Show(
            'The stored admin password cannot be read on this Windows profile. Reset the toolkit password from the original profile or recreate the local config on this laptop.',
            'Admin Mode',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        ) | Out-Null
        Add-Log -Area 'Admin Mode' -Level 'ERROR' -Message 'Stored admin password could not be read on this Windows profile.'
        return
    }

    if (-not $passwordRecord.HasStoredValue) {
        $passwordSetup = Show-PasswordDialog -Title 'Set Admin Password' -Prompt 'Create a password for BMS, links, and commands editing.' -ConfirmPassword
        if (-not $passwordSetup) {
            return
        }
        if ($passwordSetup.Password -ne $passwordSetup.ConfirmPassword) {
            [System.Windows.Forms.MessageBox]::Show(
                'The passwords did not match.',
                'Admin Mode',
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Warning
            ) | Out-Null
            return
        }

        try {
            Set-StoredAdminPassword -Password $passwordSetup.Password
            $script:IsAdminMode = $true
            Update-AdminModeUi
            Add-Log -Area 'Admin Mode' -Level 'OK' -Message 'Admin password created and editor unlocked.'
        }
        catch {
            [System.Windows.Forms.MessageBox]::Show(
                $_.Exception.Message,
                'Admin Mode',
                [System.Windows.Forms.MessageBoxButtons]::OK,
                [System.Windows.Forms.MessageBoxIcon]::Error
            ) | Out-Null
            Add-Log -Area 'Admin Mode' -Level 'ERROR' -Message ('Could not create admin password: {0}' -f $_.Exception.Message)
        }
        return
    }

    $passwordEntry = Show-PasswordDialog -Title 'Enter Admin Password' -Prompt 'Enter the admin password to unlock editing.'
    if (-not $passwordEntry) {
        return
    }

    if ((Get-PasswordHash -Password $passwordEntry.Password) -eq $passwordRecord.Hash) {
        Clear-AdminSecurityBackoff
        $script:IsAdminMode = $true
        Update-AdminModeUi
        Add-Log -Area 'Admin Mode' -Level 'OK' -Message 'Editor unlocked.'
    }
    else {
        $attemptState = Register-FailedAdminAttempt
        Start-Sleep -Milliseconds (Get-AdminFailureDelayMilliseconds)
        $message = if ($attemptState.LockoutApplied -and $attemptState.LockoutStatus) {
            'Too many incorrect attempts. Editor unlock is on cooldown for 10 minutes.'
        }
        else {
            'The admin password is not correct.'
        }
        [System.Windows.Forms.MessageBox]::Show(
            $message,
            'Admin Mode',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error
        ) | Out-Null
        Update-AdminModeUi
        if ($attemptState.LockoutApplied -and $attemptState.LockoutStatus) {
            Add-Log -Area 'Admin Mode' -Level 'WARN' -Message ('Too many incorrect admin passwords. Cooldown started for {0} minutes.' -f $script:AdminLockoutMinutes)
        }
        else {
            Add-Log -Area 'Admin Mode' -Level 'WARN' -Message ('Incorrect admin password. {0} attempt(s) remaining before cooldown.' -f $attemptState.RemainingBeforeLockout)
        }
    }
}

function Select-BmsFlow {
    Select-BmsCategory
}

function Test-InternetConnection {
    $ping = New-Object System.Net.NetworkInformation.Ping
    foreach ($target in @('1.1.1.1', '8.8.8.8')) {
        try {
            $reply = $ping.Send($target, 1000)
            if ($reply.Status -eq [System.Net.NetworkInformation.IPStatus]::Success) {
                return $true
            }
        }
        catch {
        }
    }

    try {
        [System.Net.Dns]::GetHostAddresses('microsoft.com') | Out-Null
        return $true
    }
    catch {
        return $false
    }
}

function Update-InternetStatus {
    if (-not ((Get-Variable -Name lblInternet -Scope Script -ErrorAction SilentlyContinue) -and $script:lblInternet)) {
        return
    }

    $script:lblInternet.Text = 'Internet: Checking'
    $script:lblInternet.ForeColor = [System.Drawing.Color]::FromArgb(95, 105, 120)
    [System.Windows.Forms.Application]::DoEvents()

    if (Test-InternetConnection) {
        $script:lblInternet.Text = 'Internet: Online | {0}' -f (Get-NetworkTrafficText)
        $script:lblInternet.ForeColor = [System.Drawing.Color]::FromArgb(45, 130, 80)
        if ($script:trayIcon) { $script:trayIcon.Text = 'TEC Systems Field Toolkit - Internet: Online' }
    }
    else {
        $script:lblInternet.Text = 'Internet: Offline'
        $script:lblInternet.ForeColor = [System.Drawing.Color]::FromArgb(190, 55, 55)
        if ($script:trayIcon) { $script:trayIcon.Text = 'TEC Systems Field Toolkit - Internet: Offline' }
    }
}

function Format-Speed {
    param([double]$BytesPerSecond)

    $bitsPerSecond = $BytesPerSecond * 8
    if ($bitsPerSecond -ge 1000000) {
        return ('{0:N1} Mbps' -f ($bitsPerSecond / 1000000))
    }
    elseif ($bitsPerSecond -ge 1000) {
        return ('{0:N0} Kbps' -f ($bitsPerSecond / 1000))
    }
    else {
        return ('{0:N0} bps' -f $bitsPerSecond)
    }
}

function Get-NetworkTrafficText {
    try {
        $interfaces = Get-WmiObject -Class Win32_PerfFormattedData_Tcpip_NetworkInterface -ErrorAction Stop |
            Where-Object { $_.Name -notmatch 'Loopback|isatap|Teredo' }

        $down = 0
        $up = 0
        foreach ($interface in $interfaces) {
            $down += [double]$interface.BytesReceivedPersec
            $up += [double]$interface.BytesSentPersec
        }

        return ('Down {0} / Up {1}' -f (Format-Speed -BytesPerSecond $down), (Format-Speed -BytesPerSecond $up))
    }
    catch {
        return 'Down n/a / Up n/a'
    }
}

function Convert-MaskToPrefixLength {
    param([string]$SubnetMask)

    $octets = $SubnetMask.Split('.')
    if ($octets.Count -ne 4) {
        throw 'Subnet mask must be in format 255.255.255.0.'
    }

    $binary = ''
    foreach ($octet in $octets) {
        $number = [int]$octet
        if ($number -lt 0 -or $number -gt 255) {
            throw 'Subnet mask octets must be between 0 and 255.'
        }
        $binary += [Convert]::ToString($number, 2).PadLeft(8, '0')
    }

    if ($binary -notmatch '^1*0*$') {
        throw 'Subnet mask is not valid.'
    }

    return @($binary.ToCharArray() | Where-Object { $_ -eq '1' }).Count
}

function Test-IPv4AddressText {
    param(
        [string]$Value,
        [string]$FieldName
    )

    $address = $null
    if (-not [System.Net.IPAddress]::TryParse($Value, [ref]$address) -or $address.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
        throw ("{0} must be a valid IPv4 address." -f $FieldName)
    }
}

function Invoke-Netsh {
    param(
        [string[]]$Arguments,
        [string]$Area = 'netsh'
    )

    $output = @(& netsh.exe @Arguments 2>&1)
    $exitCode = $LASTEXITCODE

    foreach ($line in $output) {
        if (-not [string]::IsNullOrWhiteSpace([string]$line)) {
            Add-Log -Area $Area -Level 'INFO' -Message ([string]$line)
        }
    }

    if ($exitCode -ne 0) {
        throw ("netsh failed with exit code {0}. Command: netsh {1}" -f $exitCode, ($Arguments -join ' '))
    }

    return $output
}

function Get-NetshAdapterConfig {
    param([string]$AdapterName)

    $result = [pscustomobject]@{
        IPAddress = ''
        SubnetMask = ''
        Gateway = ''
        Dns = ''
    }

    try {
        $output = @(& netsh.exe interface ipv4 show config name="$AdapterName" 2>&1)
        $dnsItems = @()

        foreach ($rawLine in $output) {
            $line = ([string]$rawLine).Trim()

            if ($line -match '^IP Address:\s+(.+)$') {
                $result.IPAddress = $matches[1].Trim()
            }
            elseif ($line -match '^Subnet Prefix:\s+.+\(mask\s+([0-9\.]+)\)') {
                $result.SubnetMask = $matches[1].Trim()
            }
            elseif ($line -match '^Default Gateway:\s+(.+)$') {
                $value = $matches[1].Trim()
                if ($value -and $value -notmatch '^(none|)$') {
                    $result.Gateway = $value
                }
            }
            elseif ($line -match '^DNS servers configured.*:\s+(.+)$') {
                $value = $matches[1].Trim()
                if ($value -and $value -notmatch '^(none|)$') {
                    $dnsItems += $value
                }
            }
            elseif ($line -match '^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$') {
                $dnsItems += $line
            }
        }

        $result.Dns = (@($dnsItems) | Select-Object -Unique) -join ', '
    }
    catch {
    }

    return $result
}

# -------------------------------
# IP Shifter
# -------------------------------
function Get-ToolkitAdapterInventory {
    $statusMap = @{
        0 = 'Disconnected'
        1 = 'Connecting'
        2 = 'Connected'
        3 = 'Disconnecting'
        4 = 'Hardware not present'
        5 = 'Hardware disabled'
        6 = 'Hardware malfunction'
        7 = 'Media disconnected'
        8 = 'Authenticating'
        9 = 'Authentication succeeded'
        10 = 'Authentication failed'
        11 = 'Invalid address'
        12 = 'Credentials required'
    }

    $inventory = @()

    try {
        $wmiAdapters = @(Get-WmiObject -Class Win32_NetworkAdapter -ErrorAction Stop |
            Where-Object { $_.NetConnectionID })

        foreach ($adapter in $wmiAdapters) {
            $statusText = 'Unknown'
            if ($null -ne $adapter.NetConnectionStatus -and $statusMap.ContainsKey([int]$adapter.NetConnectionStatus)) {
                $statusText = $statusMap[[int]$adapter.NetConnectionStatus]
            }

            $inventory += [pscustomobject]@{
                Name = [string]$adapter.NetConnectionID
                Description = [string]$adapter.Description
                DeviceId = [int]$adapter.DeviceID
                InterfaceIndex = [int]$adapter.InterfaceIndex
                Guid = [string]$adapter.GUID
                StatusText = $statusText
            }
        }
    }
    catch {
    }

    if (@($inventory).Count -gt 0) {
        return @($inventory | Sort-Object -Property Name)
    }

    if (Get-Command -Name Get-NetAdapter -ErrorAction SilentlyContinue) {
        try {
            $netAdapters = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop | Where-Object {
                $_.Name -and ($_.HardwareInterface -or $_.Status -in @('Up', 'Disconnected', 'Disabled', 'Not Present'))
            })

            foreach ($adapter in $netAdapters) {
                $statusText = switch ([string]$adapter.Status) {
                    'Up' { 'Connected' }
                    'Disconnected' { 'Disconnected' }
                    'Disabled' { 'Hardware disabled' }
                    'Not Present' { 'Hardware not present' }
                    default { [string]$adapter.Status }
                }

                $inventory += [pscustomobject]@{
                    Name = [string]$adapter.Name
                    Description = [string]$adapter.InterfaceDescription
                    DeviceId = [int]$adapter.ifIndex
                    InterfaceIndex = [int]$adapter.ifIndex
                    Guid = [string]$adapter.InterfaceGuid
                    StatusText = $statusText
                }
            }
        }
        catch {
        }
    }

    return @($inventory | Sort-Object -Property Name)
}

function Get-ToolkitAdapterAddress {
    param($Adapter)

    $result = [ordered]@{ IPText = 'No IP'; MaskText = ''; GatewayText = ''; DnsText = '' }
    $config = Get-WmiObject -Class Win32_NetworkAdapterConfiguration -Filter ('Index={0}' -f $Adapter.DeviceId) -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($config) {
        $addresses = @($config.IPAddress | Where-Object { $_ -match '^\d+\.' })
        if ($addresses.Count -gt 0) { $result.IPText = $addresses -join ', ' }
        $masks = @($config.IPSubnet | Where-Object { $_ -match '^\d+\.' })
        $result.MaskText = $masks -join ', '
        $result.GatewayText = @($config.DefaultIPGateway) -join ', '
        $result.DnsText = @($config.DNSServerSearchOrder) -join ', '
    }

    # Disconnected adapters can lose their live WMI address while keeping a static registry configuration.
    if ($result.IPText -eq 'No IP' -and $Adapter.Guid) {
        $path = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{0}' -f $Adapter.Guid
        $stored = Get-ItemProperty -LiteralPath $path -ErrorAction SilentlyContinue
        if ($stored) {
            $staticIp = @()
            if ($stored.PSObject.Properties['IPAddress']) {
                $staticIp = @($stored.IPAddress | Where-Object { $_ -and $_ -ne '0.0.0.0' })
            }
            if ($staticIp.Count -gt 0) {
                $result.IPText = $staticIp -join ', '
                if ($stored.PSObject.Properties['SubnetMask']) {
                    $result.MaskText = @($stored.SubnetMask) -join ', '
                }
            }
            if ($stored.PSObject.Properties['DefaultGateway']) {
                $result.GatewayText = @($stored.DefaultGateway) -join ', '
            }
            if ($stored.PSObject.Properties['NameServer']) {
                $result.DnsText = @($stored.NameServer) -join ', '
            }
        }
    }

    if ($result.IPText -eq 'No IP') {
        $netshConfig = Get-NetshAdapterConfig -AdapterName $Adapter.Name
        if ($netshConfig.IPAddress) { $result.IPText = $netshConfig.IPAddress }
        if ($netshConfig.SubnetMask) { $result.MaskText = $netshConfig.SubnetMask }
        if ($netshConfig.Gateway) { $result.GatewayText = $netshConfig.Gateway }
        if ($netshConfig.Dns) { $result.DnsText = $netshConfig.Dns }
    }

    return [pscustomobject]$result
}

function Refresh-Adapters {
    $script:AdapterList = @(Get-ToolkitAdapterInventory | ForEach-Object {
            $adapterName = [string]$_.Name
            $statusText = [string]$_.StatusText
            $deviceId = [int]$_.DeviceId
            $interfaceIndex = [int]$_.InterfaceIndex
            try {
                $address = Get-ToolkitAdapterAddress -Adapter $_
            }
            catch {
                Add-Log -Area 'IP Shifter' -Level 'WARN' -Message ('Could not read {0} configuration: {1}' -f $adapterName, $_.Exception.Message)
                $address = [pscustomobject]@{ IPText = 'Unavailable'; MaskText = ''; GatewayText = ''; DnsText = '' }
            }

            [pscustomobject]@{
                Name = $adapterName
                Description = [string]$_.Description
                Index = $interfaceIndex
                DeviceId = $deviceId
                Status = $statusText
                IPText = $address.IPText
                MaskText = $address.MaskText
                GatewayText = $address.GatewayText
                DnsText = $address.DnsText
            }
        })

    if ((Get-Variable -Name cboAdapter -Scope Script -ErrorAction SilentlyContinue) -and $script:cboAdapter) {
        $previous = if ($script:cboAdapter.SelectedItem) { Get-SelectedAdapterName } else { '' }
        $script:cboAdapter.Items.Clear()
        foreach ($adapter in $script:AdapterList) {
            $displayIp = $adapter.IPText
            if ($adapter.MaskText) {
                $displayIp = '{0} / {1}' -f $adapter.IPText, $adapter.MaskText
            }
            [void]$script:cboAdapter.Items.Add(('{0} [{1}] - IP: {2}' -f $adapter.Name, $adapter.Status, $displayIp))
        }

        $selected = -1
        for ($i = 0; $i -lt $script:AdapterList.Count; $i++) {
            if ($script:AdapterList[$i].Name -eq $previous) { $selected = $i; break }
        }
        if ($selected -ge 0) {
            $script:cboAdapter.SelectedIndex = $selected
        }
        elseif ($script:cboAdapter.Items.Count -gt 0) {
            $script:cboAdapter.SelectedIndex = 0
        }
    }
    if ((Get-Variable -Name cboScanAdapter -Scope Script -ErrorAction SilentlyContinue) -and $script:cboScanAdapter) {
        Refresh-ScannerAdapters
    }
}

function Get-SelectedAdapterName {
    if (-not $script:cboAdapter.SelectedItem) {
        throw 'Select a network adapter.'
    }

    return [string]$script:AdapterList[$script:cboAdapter.SelectedIndex].Name
}

function Get-IpProfileValue {
    param($Profile, [string]$Name)

    if (-not $Profile) { return '' }
    if ($Profile.PSObject.Properties[$Name]) { return [string]$Profile.$Name }
    if ($Name -eq 'IPAddress' -and $Profile.PSObject.Properties['IP']) { return [string]$Profile.IP }
    if ($Name -eq 'SubnetMask' -and $Profile.PSObject.Properties['Mask']) { return [string]$Profile.Mask }
    return ''
}

function Refresh-Profiles {
    if (-not ((Get-Variable -Name lvProfiles -Scope Script -ErrorAction SilentlyContinue) -and $script:lvProfiles)) {
        return
    }

    $script:lvProfiles.Items.Clear()
    foreach ($profile in @($script:Config.SiteProfiles)) {
        if (-not $profile) { continue }
        $item = New-Object System.Windows.Forms.ListViewItem((Get-IpProfileValue -Profile $profile -Name 'Name'))
        [void]$item.SubItems.Add((Get-IpProfileValue -Profile $profile -Name 'Adapter'))
        [void]$item.SubItems.Add((Get-IpProfileValue -Profile $profile -Name 'IPAddress'))
        [void]$item.SubItems.Add((Get-IpProfileValue -Profile $profile -Name 'SubnetMask'))
        [void]$item.SubItems.Add((Get-IpProfileValue -Profile $profile -Name 'Gateway'))
        [void]$script:lvProfiles.Items.Add($item)
    }
}

function Load-SelectedProfile {
    if ($script:lvProfiles.SelectedItems.Count -eq 0) {
        Add-Log -Area 'IP Shifter' -Level 'WARN' -Message 'Select a saved site profile first.'
        return
    }

    $profileName = $script:lvProfiles.SelectedItems[0].Text
    $profile = $script:Config.SiteProfiles | Where-Object { $_.Name -eq $profileName } | Select-Object -First 1
    if (-not $profile) {
        Add-Log -Area 'IP Shifter' -Level 'WARN' -Message 'Profile was not found.'
        return
    }

    $script:txtProfileName.Text = Get-IpProfileValue -Profile $profile -Name 'Name'
    $script:txtIpAddress.Text = Get-IpProfileValue -Profile $profile -Name 'IPAddress'
    $script:txtSubnetMask.Text = Get-IpProfileValue -Profile $profile -Name 'SubnetMask'
    $script:txtGateway.Text = Get-IpProfileValue -Profile $profile -Name 'Gateway'
    $script:txtDns1.Text = Get-IpProfileValue -Profile $profile -Name 'Dns1'
    $script:txtDns2.Text = Get-IpProfileValue -Profile $profile -Name 'Dns2'
    for ($i = 0; $i -lt $script:AdapterList.Count; $i++) {
        if ($script:AdapterList[$i].Name -eq (Get-IpProfileValue -Profile $profile -Name 'Adapter')) { $script:cboAdapter.SelectedIndex = $i; break }
    }
    Add-Log -Area 'IP Shifter' -Level 'OK' -Message ("Loaded profile {0}" -f (Get-IpProfileValue -Profile $profile -Name 'Name'))
}

function Save-IpProfile {
    Invoke-UiAction -Name 'Save IP Profile' -Action {
        $name = $script:txtProfileName.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($name)) {
            throw 'Profile name is required.'
        }

        Test-IPv4AddressText -Value $script:txtIpAddress.Text.Trim() -FieldName 'IP address'
        [void](Convert-MaskToPrefixLength -SubnetMask $script:txtSubnetMask.Text.Trim())
        foreach ($field in @(@('Gateway', $script:txtGateway.Text), @('DNS 1', $script:txtDns1.Text), @('DNS 2', $script:txtDns2.Text))) {
            if ($field[1].Trim()) { Test-IPv4AddressText -Value $field[1].Trim() -FieldName $field[0] }
        }

        $profile = [pscustomobject]@{
            Name = $name
            Adapter = Get-SelectedAdapterName
            IPAddress = $script:txtIpAddress.Text.Trim()
            SubnetMask = $script:txtSubnetMask.Text.Trim()
            Gateway = $script:txtGateway.Text.Trim()
            Dns1 = $script:txtDns1.Text.Trim()
            Dns2 = $script:txtDns2.Text.Trim()
        }

        $profiles = @($script:Config.SiteProfiles | Where-Object { $_.Name -ne $name })
        $profiles += $profile
        $script:Config.SiteProfiles = $profiles
        Save-Config
        Refresh-Profiles
        Add-Log -Area 'IP Shifter' -Level 'OK' -Message ("Saved profile {0}" -f $name)
    }
}

function Save-PendingNetworkDraft {
    param([string]$Action)

    $draft = [pscustomobject]@{
        Action = $Action
        Adapter = Get-SelectedAdapterName
        ProfileName = $script:txtProfileName.Text
        IPAddress = $script:txtIpAddress.Text
        SubnetMask = $script:txtSubnetMask.Text
        Gateway = $script:txtGateway.Text
        Dns1 = $script:txtDns1.Text
        Dns2 = $script:txtDns2.Text
    }
    $script:Config | Add-Member -NotePropertyName PendingNetworkDraft -NotePropertyValue $draft -Force
    Save-Config
}

function Restore-PendingNetworkDraft {
    if (-not $script:Config.PSObject.Properties['PendingNetworkDraft'] -or -not $script:Config.PendingNetworkDraft) { return }
    $draft = $script:Config.PendingNetworkDraft
    for ($i = 0; $i -lt $script:AdapterList.Count; $i++) {
        if ($script:AdapterList[$i].Name -eq $draft.Adapter) { $script:cboAdapter.SelectedIndex = $i; break }
    }
    $script:txtProfileName.Text = [string]$draft.ProfileName
    $script:txtIpAddress.Text = [string]$draft.IPAddress
    $script:txtSubnetMask.Text = [string]$draft.SubnetMask
    $script:txtGateway.Text = [string]$draft.Gateway
    $script:txtDns1.Text = [string]$draft.Dns1
    $script:txtDns2.Text = [string]$draft.Dns2
    $script:Config.PendingNetworkDraft = $null
    Save-Config
    $script:tabs.SelectedTab = $script:tabIp
    Add-Log -Area 'IP Shifter' -Level 'INFO' -Message ("Restored {0} draft for {1}; review and apply it." -f $draft.Action, $draft.Adapter)
}

function Remove-IpProfile {
    Invoke-UiAction -Name 'Delete IP Profile' -Action {
        if ($script:lvProfiles.SelectedItems.Count -eq 0) { throw 'Select a saved profile first.' }
        $name = $script:lvProfiles.SelectedItems[0].Text
        if (-not (Confirm-Action -Title 'Delete Profile' -Message ("Delete saved profile '{0}'? This does not change the adapter." -f $name))) { return }
        $script:Config.SiteProfiles = @($script:Config.SiteProfiles | Where-Object { $_.Name -ne $name })
        Save-Config
        Refresh-Profiles
        Add-Log -Area 'IP Shifter' -Level 'OK' -Message ("Deleted saved profile {0}" -f $name)
    }
}

function Show-AdapterDetails {
    Invoke-UiAction -Name 'Adapter Details' -Action {
        $adapterName = Get-SelectedAdapterName
        $adapter = @($script:AdapterList | Where-Object { $_.Name -eq $adapterName } | Select-Object -First 1)
        if (-not $adapter) {
            throw 'Selected adapter was not found.'
        }

        $config = Get-WmiObject -Class Win32_NetworkAdapterConfiguration |
            Where-Object { $_.Index -eq [int]$adapter.DeviceId -or $_.InterfaceIndex -eq $adapter.Index } |
            Select-Object -First 1

        Add-Log -Area 'Adapter' -Level 'INFO' -Message ("Adapter: {0}" -f $adapter.Name)
        Add-Log -Area 'Adapter' -Level 'INFO' -Message ("Status: {0}" -f $adapter.Status)
        Add-Log -Area 'Adapter' -Level 'INFO' -Message ("Description: {0}" -f $adapter.Description)
        Add-Log -Area 'Adapter' -Level 'INFO' -Message ("Configured IP: {0}" -f $adapter.IPText)
        if ($adapter.MaskText) { Add-Log -Area 'Adapter' -Level 'INFO' -Message ("Configured Mask: {0}" -f $adapter.MaskText) }
        if ($adapter.GatewayText) { Add-Log -Area 'Adapter' -Level 'INFO' -Message ("Configured Gateway: {0}" -f $adapter.GatewayText) }
        if ($adapter.DnsText) { Add-Log -Area 'Adapter' -Level 'INFO' -Message ("Configured DNS: {0}" -f $adapter.DnsText) }

        if ($config) {
            Add-Log -Area 'Adapter' -Level 'INFO' -Message ("MAC: {0}" -f $config.MACAddress)
            Add-Log -Area 'Adapter' -Level 'INFO' -Message ("DHCP Enabled: {0}" -f $config.DHCPEnabled)
        }
        else {
            Add-Log -Area 'Adapter' -Level 'WARN' -Message 'No IP configuration object found for selected adapter.'
        }
    }
}

function Apply-StaticIp {
    Invoke-UiAction -Name 'Apply Static IP' -Action {
        if (-not (Test-IsAdministrator)) {
            Save-PendingNetworkDraft -Action 'Static IP'
            [void](Ensure-ToolkitAdministrator -Feature 'Changing adapter IP settings')
            return
        }

        $adapter = Get-SelectedAdapterName
        $ip = $script:txtIpAddress.Text.Trim()
        $mask = $script:txtSubnetMask.Text.Trim()
        $gateway = $script:txtGateway.Text.Trim()
        $dns1 = $script:txtDns1.Text.Trim()
        $dns2 = $script:txtDns2.Text.Trim()

        Test-IPv4AddressText -Value $ip -FieldName 'IP address'
        [void](Convert-MaskToPrefixLength -SubnetMask $mask)
        if (-not [string]::IsNullOrWhiteSpace($gateway)) {
            Test-IPv4AddressText -Value $gateway -FieldName 'Gateway'
        }
        if (-not [string]::IsNullOrWhiteSpace($dns1)) {
            Test-IPv4AddressText -Value $dns1 -FieldName 'DNS 1'
        }
        if (-not [string]::IsNullOrWhiteSpace($dns2)) {
            Test-IPv4AddressText -Value $dns2 -FieldName 'DNS 2'
        }

        $message = "Apply this static IP to adapter '$adapter'?" + [Environment]::NewLine +
            "IP: $ip" + [Environment]::NewLine +
            "Mask: $mask" + [Environment]::NewLine +
            "Gateway: $(if ($gateway) { $gateway } else { 'None' })"

        if (-not (Confirm-Action -Title 'Confirm IP Change' -Message $message)) {
            Add-Log -Area 'IP Shifter' -Level 'WARN' -Message 'Static IP change cancelled.'
            return
        }

        $gatewayValue = if ([string]::IsNullOrWhiteSpace($gateway)) { 'none' } else { $gateway }
        $addressArgs = @(
            'interface', 'ipv4', 'set', 'address',
            ('name={0}' -f $adapter),
            'source=static',
            ('address={0}' -f $ip),
            ('mask={0}' -f $mask),
            ('gateway={0}' -f $gatewayValue)
        )
        if ($gatewayValue -ne 'none') {
            $addressArgs += 'gwmetric=1'
        }
        Invoke-Netsh -Arguments $addressArgs -Area 'IP Shifter' | Out-Null

        if (-not [string]::IsNullOrWhiteSpace($dns1)) {
            Invoke-Netsh -Arguments @(
                'interface', 'ipv4', 'set', 'dnsservers',
                ('name={0}' -f $adapter),
                'source=static',
                ('address={0}' -f $dns1),
                'register=primary'
            ) -Area 'IP Shifter' | Out-Null
        }
        if (-not [string]::IsNullOrWhiteSpace($dns2)) {
            Invoke-Netsh -Arguments @(
                'interface', 'ipv4', 'add', 'dnsserver',
                ('name={0}' -f $adapter),
                ('address={0}' -f $dns2),
                'index=2'
            ) -Area 'IP Shifter' | Out-Null
        }

        Start-Sleep -Milliseconds 500
        Refresh-Adapters
        $applied = @($script:AdapterList | Where-Object { $_.Name -eq $adapter } | Select-Object -First 1)
        if (-not $applied -or @($applied.IPText -split ',\s*') -notcontains $ip) {
            throw 'Windows did not report the requested IP after applying it. Check adapter details and the log.'
        }
        Add-Log -Area 'IP Shifter' -Level 'OK' -Message ("Applied static IP {0} to {1}" -f $ip, $adapter)
    }
}

function Apply-Dhcp {
    Invoke-UiAction -Name 'Set DHCP' -Action {
        if (-not (Test-IsAdministrator)) {
            Save-PendingNetworkDraft -Action 'DHCP'
            [void](Ensure-ToolkitAdministrator -Feature 'Changing adapter IP settings')
            return
        }

        $adapter = Get-SelectedAdapterName
        if (-not (Confirm-Action -Title 'Confirm DHCP Change' -Message ("Set adapter '{0}' back to DHCP for IP and DNS?" -f $adapter))) {
            Add-Log -Area 'IP Shifter' -Level 'WARN' -Message 'DHCP change cancelled.'
            return
        }

        Invoke-Netsh -Arguments @(
            'interface', 'ipv4', 'set', 'address',
            ('name={0}' -f $adapter),
            'source=dhcp'
        ) -Area 'IP Shifter' | Out-Null

        Invoke-Netsh -Arguments @(
            'interface', 'ipv4', 'set', 'dnsservers',
            ('name={0}' -f $adapter),
            'source=dhcp'
        ) -Area 'IP Shifter' | Out-Null

        Start-Sleep -Milliseconds 500
        Refresh-Adapters
        Add-Log -Area 'IP Shifter' -Level 'OK' -Message ("Set {0} to DHCP" -f $adapter)
    }
}

# -------------------------------
# Troubleshooting Actions
# -------------------------------
function Show-SystemSummary {
    Invoke-UiAction -Name 'System Summary' -Action {
        $computer = Get-WmiObject -Class Win32_ComputerSystem
        $os = Get-WmiObject -Class Win32_OperatingSystem
        $bios = Get-WmiObject -Class Win32_BIOS
        $uptime = (Get-Date) - $os.ConvertToDateTime($os.LastBootUpTime)

        Add-Log -Area 'System' -Level 'INFO' -Message ('Computer Name: {0}' -f $env:COMPUTERNAME)
        Add-Log -Area 'System' -Level 'INFO' -Message ('Current User: {0}' -f [Environment]::UserName)
        Add-Log -Area 'System' -Level 'INFO' -Message ('Manufacturer: {0}' -f $computer.Manufacturer)
        Add-Log -Area 'System' -Level 'INFO' -Message ('Model: {0}' -f $computer.Model)
        Add-Log -Area 'System' -Level 'INFO' -Message ('Serial Number: {0}' -f $bios.SerialNumber)
        Add-Log -Area 'System' -Level 'INFO' -Message ('OS: {0}' -f $os.Caption)
        Add-Log -Area 'System' -Level 'INFO' -Message ('RAM: {0:N1} GB' -f ($computer.TotalPhysicalMemory / 1GB))
        Add-Log -Area 'System' -Level 'INFO' -Message ('Uptime: {0} days, {1} hours' -f [int]$uptime.TotalDays, $uptime.Hours)
    }
}

function Show-NetworkSummary {
    Invoke-UiAction -Name 'Network Summary' -Action {
        $configs = Get-WmiObject -Class Win32_NetworkAdapterConfiguration | Where-Object { $_.IPEnabled -eq $true }
        if (-not $configs) {
            Add-Log -Area 'Network' -Level 'WARN' -Message 'No active IP-enabled adapters found.'
            return
        }

        foreach ($config in $configs) {
            Add-Log -Area 'Network' -Level 'INFO' -Message ('Adapter: {0}' -f $config.Description)
            Add-Log -Area 'Network' -Level 'INFO' -Message ('IP Address: {0}' -f (@($config.IPAddress | Where-Object { $_ -match '^\d+\.' }) -join ', '))
            Add-Log -Area 'Network' -Level 'INFO' -Message ('Gateway: {0}' -f (@($config.DefaultIPGateway) -join ', '))
            Add-Log -Area 'Network' -Level 'INFO' -Message ('DNS Servers: {0}' -f (@($config.DNSServerSearchOrder) -join ', '))
        }
    }
}

function Show-IpConfigAll {
    Invoke-UiAction -Name 'IPConfig All' -Action {
        $output = @(& ipconfig.exe /all 2>&1)
        foreach ($line in $output) {
            if (-not [string]::IsNullOrWhiteSpace([string]$line)) {
                Add-Log -Area 'IPConfig' -Level 'INFO' -Message ([string]$line)
            }
        }
    }
}

function Show-RouteTable {
    Invoke-UiAction -Name 'Route Table' -Action {
        $output = @(& route.exe print 2>&1)
        foreach ($line in $output) {
            if (-not [string]::IsNullOrWhiteSpace([string]$line)) {
                Add-Log -Area 'Routes' -Level 'INFO' -Message ([string]$line)
            }
        }
    }
}

function Show-ArpCache {
    Invoke-UiAction -Name 'ARP Cache' -Action {
        $output = @(& arp.exe -a 2>&1)
        foreach ($line in $output) {
            if (-not [string]::IsNullOrWhiteSpace([string]$line)) {
                Add-Log -Area 'ARP' -Level 'INFO' -Message ([string]$line)
            }
        }
    }
}

function Show-DnsCacheEntries {
    Invoke-UiAction -Name 'DNS Cache' -Action {
        $output = @(& ipconfig.exe /displaydns 2>&1)
        $lines = @($output | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
        if ($lines.Count -eq 0) {
            Add-Log -Area 'DNS Cache' -Level 'WARN' -Message 'No DNS cache output was returned.'
            return
        }

        foreach ($line in ($lines | Select-Object -First 80)) {
            Add-Log -Area 'DNS Cache' -Level 'INFO' -Message ([string]$line)
        }

        if ($lines.Count -gt 80) {
            Add-Log -Area 'DNS Cache' -Level 'INFO' -Message ("Output truncated. Showing first 80 lines of {0}." -f $lines.Count)
        }
    }
}

function Show-NetstatSummary {
    Invoke-UiAction -Name 'Netstat' -Action {
        $output = @(& netstat.exe -ano -p tcp 2>&1)
        $lines = @($output | Where-Object { $_ -match 'LISTENING|ESTABLISHED' })
        if ($lines.Count -eq 0) {
            Add-Log -Area 'Netstat' -Level 'WARN' -Message 'No active LISTENING or ESTABLISHED TCP entries were found.'
            return
        }

        foreach ($line in ($lines | Select-Object -First 60)) {
            Add-Log -Area 'Netstat' -Level 'INFO' -Message ([string]$line)
        }

        if ($lines.Count -gt 60) {
            Add-Log -Area 'Netstat' -Level 'INFO' -Message ("Output truncated. Showing first 60 lines of {0}." -f $lines.Count)
        }
    }
}

function Show-FirewallProfiles {
    Invoke-UiAction -Name 'Firewall Profiles' -Action {
        if (Get-Command -Name Get-NetFirewallProfile -ErrorAction SilentlyContinue) {
            $profiles = @(Get-NetFirewallProfile -ErrorAction Stop)
            foreach ($profile in $profiles) {
                Add-Log -Area 'Firewall' -Level 'INFO' -Message ('{0}: Enabled={1}, DefaultInbound={2}, DefaultOutbound={3}' -f $profile.Name, $profile.Enabled, $profile.DefaultInboundAction, $profile.DefaultOutboundAction)
            }
        }
        else {
            $output = @(& netsh.exe advfirewall show allprofiles 2>&1)
            foreach ($line in $output) {
                if (-not [string]::IsNullOrWhiteSpace([string]$line)) {
                    Add-Log -Area 'Firewall' -Level 'INFO' -Message ([string]$line)
                }
            }
        }
    }
}

function Show-WindowsUpdateStatus {
    Invoke-UiAction -Name 'Windows Update Status' -Action {
        foreach ($serviceName in @('wuauserv', 'BITS', 'UsoSvc')) {
            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($service) {
                Add-Log -Area 'Updates' -Level 'INFO' -Message ('{0}: {1}' -f $service.Name, $service.Status)
            }
        }

        $hotfixes = @(Get-HotFix -ErrorAction SilentlyContinue | Sort-Object -Property InstalledOn -Descending | Select-Object -First 5)
        if ($hotfixes.Count -eq 0) {
            Add-Log -Area 'Updates' -Level 'WARN' -Message 'No recent installed hotfixes were returned.'
            return
        }

        foreach ($fix in $hotfixes) {
            Add-Log -Area 'Updates' -Level 'INFO' -Message ('{0} installed on {1:yyyy-MM-dd}' -f $fix.HotFixID, $fix.InstalledOn)
        }
    }
}

function Test-CommonPorts {
    Invoke-UiAction -Name 'Port Test' -Action {
        $target = $script:txtPingTarget.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($target)) {
            throw 'Enter a host or IP first.'
        }

        $ports = @(80, 443, 3389, 50000)
        foreach ($port in $ports) {
            $ok = $false
            if (Get-Command -Name Test-NetConnection -ErrorAction SilentlyContinue) {
                try {
                    $result = Test-NetConnection -ComputerName $target -Port $port -InformationLevel Quiet -WarningAction SilentlyContinue
                    $ok = [bool]$result
                }
                catch {
                    $ok = $false
                }
            }
            else {
                try {
                    $client = New-Object System.Net.Sockets.TcpClient
                    $iar = $client.BeginConnect($target, $port, $null, $null)
                    $ok = $iar.AsyncWaitHandle.WaitOne(1500, $false)
                    $client.Close()
                }
                catch {
                    $ok = $false
                }
            }

            Add-Log -Area 'Ports' -Level $(if ($ok) { 'OK' } else { 'WARN' }) -Message ('{0}:{1} {2}' -f $target, $port, $(if ($ok) { 'reachable' } else { 'not reachable' }))
        }
    }
}

function Get-PrimaryGatewayAddress {
    try {
        $configs = @(Get-WmiObject -Class Win32_NetworkAdapterConfiguration -ErrorAction Stop | Where-Object {
            $_.IPEnabled -eq $true -and $_.DefaultIPGateway -and @($_.DefaultIPGateway).Count -gt 0
        })
        foreach ($config in $configs) {
            $gateway = @($config.DefaultIPGateway | Where-Object { $_ -match '^\d+\.' } | Select-Object -First 1)
            if ($gateway.Count -gt 0 -and $gateway[0]) {
                return [string]$gateway[0]
            }
        }
    }
    catch {
    }

    return ''
}

function Start-CommandTerminal {
    param(
        [string]$Title,
        [string]$CommandLine,
        [string]$Area = 'Tools',
        [string]$SuccessMessage = ''
    )

    Invoke-UiAction -Name $Title -Action {
        if ([string]::IsNullOrWhiteSpace($CommandLine)) {
            throw 'No command line was provided.'
        }

        $cmdText = 'title "{0}" & {1}' -f $Title, $CommandLine
        Start-Process -FilePath 'cmd.exe' -ArgumentList ('/k {0}' -f $cmdText)
        if ([string]::IsNullOrWhiteSpace($SuccessMessage)) {
            $SuccessMessage = ("Opened terminal for {0}" -f $Title)
        }
        Add-Log -Area $Area -Level 'OK' -Message $SuccessMessage
    }
}

function Get-NetworkDeviceTarget {
    $target = ''
    if ((Get-Variable -Name txtNetworkDeviceTarget -Scope Script -ErrorAction SilentlyContinue) -and $script:txtNetworkDeviceTarget) {
        $target = $script:txtNetworkDeviceTarget.Text.Trim()
    }
    if ([string]::IsNullOrWhiteSpace($target) -and
        (Get-Variable -Name txtPingTarget -Scope Script -ErrorAction SilentlyContinue) -and $script:txtPingTarget) {
        $target = $script:txtPingTarget.Text.Trim()
    }
    return $target
}

function Get-NetworkSwitchTarget {
    $target = ''
    if ((Get-Variable -Name txtSwitchTarget -Scope Script -ErrorAction SilentlyContinue) -and $script:txtSwitchTarget) {
        $target = $script:txtSwitchTarget.Text.Trim()
    }
    return $target
}

function Start-NetworkDevicePing {
    $target = Get-NetworkDeviceTarget
    if ([string]::IsNullOrWhiteSpace($target)) {
        throw 'Enter a device host or IP first.'
    }
    if ($target -match '[\s&|<>^"]') {
        throw 'Device target cannot contain spaces or command characters.'
    }

    $pingSwitch = '-n 4'
    $pingModeText = '4 ping replies'
    if ((Get-Variable -Name chkNetworkContinuousPing -Scope Script -ErrorAction SilentlyContinue) -and
        $script:chkNetworkContinuousPing -and
        $script:chkNetworkContinuousPing.Checked) {
        $pingSwitch = '-t'
        $pingModeText = 'continuous ping'
    }

    Start-CommandTerminal -Title ('TEC Device Ping - {0}' -f $target) -CommandLine ('echo Device ping to {0} ({1}) & echo. & ping {2} {0}' -f $target, $pingModeText, $pingSwitch) -Area 'Network' -SuccessMessage ("Opened device ping terminal for {0} ({1})" -f $target, $pingModeText)
}

function Start-GatewayPing {
    $gateway = Get-PrimaryGatewayAddress
    if ([string]::IsNullOrWhiteSpace($gateway)) {
        throw 'No default gateway was found on an active adapter.'
    }

    Start-CommandTerminal -Title ('TEC Gateway Ping - {0}' -f $gateway) -CommandLine ('echo Gateway ping to {0} & echo. & ping -n 4 {0}' -f $gateway) -Area 'Network' -SuccessMessage ("Opened gateway ping terminal for {0}" -f $gateway)
}

function Start-TracertToDevice {
    $target = Get-NetworkDeviceTarget
    if ([string]::IsNullOrWhiteSpace($target)) {
        throw 'Enter a device host or IP first.'
    }

    Start-CommandTerminal -Title ('TEC Tracert - {0}' -f $target) -CommandLine ('echo Trace route to {0} & echo. & tracert {0}' -f $target) -Area 'Network' -SuccessMessage ("Opened tracert for {0}" -f $target)
}

function Start-PathPingToDevice {
    $target = Get-NetworkDeviceTarget
    if ([string]::IsNullOrWhiteSpace($target)) {
        throw 'Enter a device host or IP first.'
    }

    Start-CommandTerminal -Title ('TEC PathPing - {0}' -f $target) -CommandLine ('echo PathPing to {0} & echo This can take a few minutes. & echo. & pathping {0}' -f $target) -Area 'Network' -SuccessMessage ("Opened pathping for {0}" -f $target)
}

function Test-SwitchAccessPorts {
    Invoke-UiAction -Name 'Switch Port Test' -Action {
        $target = Get-NetworkSwitchTarget
        if ([string]::IsNullOrWhiteSpace($target)) {
            throw 'Enter a switch host or IP first.'
        }

        $ports = @(80, 443, 22, 23, 161)
        foreach ($port in $ports) {
            $ok = $false
            if (Get-Command -Name Test-NetConnection -ErrorAction SilentlyContinue) {
                try {
                    $result = Test-NetConnection -ComputerName $target -Port $port -InformationLevel Quiet -WarningAction SilentlyContinue
                    $ok = [bool]$result
                }
                catch {
                    $ok = $false
                }
            }
            else {
                try {
                    $client = New-Object System.Net.Sockets.TcpClient
                    $iar = $client.BeginConnect($target, $port, $null, $null)
                    $ok = $iar.AsyncWaitHandle.WaitOne(1500, $false)
                    $client.Close()
                }
                catch {
                    $ok = $false
                }
            }

            Add-Log -Area 'Switch' -Level $(if ($ok) { 'OK' } else { 'WARN' }) -Message ('{0}:{1} {2}' -f $target, $port, $(if ($ok) { 'reachable' } else { 'not reachable' }))
        }
    }
}

function Open-SwitchWebUi {
    Invoke-UiAction -Name 'Switch Web UI' -Action {
        $target = Get-NetworkSwitchTarget
        if ([string]::IsNullOrWhiteSpace($target)) {
            throw 'Enter a switch host or IP first.'
        }

        $scheme = 'http://'
        if ((Get-Variable -Name cboSwitchScheme -Scope Script -ErrorAction SilentlyContinue) -and $script:cboSwitchScheme -and $script:cboSwitchScheme.SelectedItem) {
            $selected = [string]$script:cboSwitchScheme.SelectedItem
            if ($selected -match '^https?://') {
                $scheme = $selected
            }
        }

        $url = '{0}{1}' -f $scheme, $target
        Start-Process -FilePath $url
        Add-Log -Area 'Switch' -Level 'OK' -Message ("Opened switch web UI: {0}" -f $url)
    }
}

function Open-SwitchSshTerminal {
    Invoke-UiAction -Name 'Switch SSH' -Action {
        $target = Get-NetworkSwitchTarget
        if ([string]::IsNullOrWhiteSpace($target)) {
            throw 'Enter a switch host or IP first.'
        }
        if (-not (Get-Command -Name ssh.exe -ErrorAction SilentlyContinue)) {
            throw 'ssh.exe was not found on this laptop.'
        }

        Start-CommandTerminal -Title ('TEC SSH - {0}' -f $target) -CommandLine ('ssh {0}' -f $target) -Area 'Switch' -SuccessMessage ("Opened SSH terminal for {0}" -f $target)
    }
}

function Open-SwitchTelnetSession {
    Invoke-UiAction -Name 'Switch Telnet' -Action {
        $target = Get-NetworkSwitchTarget
        if ([string]::IsNullOrWhiteSpace($target)) {
            throw 'Enter a switch host or IP first.'
        }
        if (-not (Get-Command -Name telnet.exe -ErrorAction SilentlyContinue)) {
            throw 'telnet.exe is not installed on this laptop.'
        }

        Start-CommandTerminal -Title ('TEC Telnet - {0}' -f $target) -CommandLine ('telnet {0}' -f $target) -Area 'Switch' -SuccessMessage ("Opened Telnet session for {0}" -f $target)
    }
}

function Show-NetworkBaseline {
    Invoke-UiAction -Name 'Network Baseline' -Action {
        Add-Log -Area 'Network Baseline' -Level 'INFO' -Message ('Hostname: {0}' -f $env:COMPUTERNAME)
        Add-Log -Area 'Network Baseline' -Level 'INFO' -Message ('Gateway: {0}' -f $(if (Get-PrimaryGatewayAddress) { Get-PrimaryGatewayAddress } else { 'Not found' }))
        Show-AdapterDetails
        Show-NetworkSummary
        Show-ArpCache
        Show-RouteTable
        Show-DnsCacheEntries
    }
}

function Show-NetworkLinkStatus {
    Invoke-UiAction -Name 'Link Status' -Action {
        if (Get-Command -Name Get-NetAdapter -ErrorAction SilentlyContinue) {
            $adapters = @(Get-NetAdapter -ErrorAction Stop | Sort-Object -Property Status, Name)
            foreach ($adapter in $adapters) {
                Add-Log -Area 'Link' -Level 'INFO' -Message ('{0}: Status={1}, LinkSpeed={2}, Mac={3}' -f $adapter.Name, $adapter.Status, $adapter.LinkSpeed, $adapter.MacAddress)
            }
        }
        else {
            $adapters = @(Get-WmiObject -Class Win32_NetworkAdapter -ErrorAction Stop | Where-Object { $_.PhysicalAdapter -eq $true })
            foreach ($adapter in $adapters) {
                Add-Log -Area 'Link' -Level 'INFO' -Message ('{0}: NetConnectionStatus={1}, Speed={2}' -f $adapter.NetConnectionID, $adapter.NetConnectionStatus, $adapter.Speed)
            }
        }
    }
}

function Show-NetworkNeighborTable {
    Invoke-UiAction -Name 'Neighbors' -Action {
        if (Get-Command -Name Get-NetNeighbor -ErrorAction SilentlyContinue) {
            $neighbors = @(Get-NetNeighbor -ErrorAction Stop | Where-Object { $_.IPAddress -match '^\d+\.' } | Select-Object -First 50)
            if ($neighbors.Count -eq 0) {
                Add-Log -Area 'Neighbors' -Level 'WARN' -Message 'No IPv4 neighbors were found.'
                return
            }
            foreach ($neighbor in $neighbors) {
                Add-Log -Area 'Neighbors' -Level 'INFO' -Message ('{0} | MAC {1} | Interface {2} | State {3}' -f $neighbor.IPAddress, $neighbor.LinkLayerAddress, $neighbor.InterfaceAlias, $neighbor.State)
            }
        }
        else {
            Show-ArpCache
        }
    }
}

function Show-NetworkPlaybook {
    param([string]$Topic)

    if (-not ((Get-Variable -Name txtNetworkPlaybook -Scope Script -ErrorAction SilentlyContinue) -and $script:txtNetworkPlaybook)) {
        return
    }

    $topicText = switch ($Topic) {
        'managed-switch' {
@"
Managed switch field steps
1. Confirm laptop IP profile, gateway, and VLAN/site details first.
2. Ping the switch IP. If that fails, check cable, link lights, subnet, and gateway path.
3. Test common switch access ports: 80, 443, 22, 23, and 161.
4. Open the switch web UI or SSH if reachable.
5. Confirm the expected port, VLAN, trunk/access mode, PoE state, and any shutdown/err-disable condition.
6. If the device on the far side still fails, compare the switch port status with the device IP and MAC visibility.
"@
        }
        'unmanaged-switch' {
@"
Unmanaged switch field steps
1. Start with physical checks: power, link lights, cable quality, and correct uplink/downlink path.
2. Confirm the laptop and device are in the same expected IP range.
3. Ping the default gateway and target device.
4. Use ARP/Neighbors to see whether the device shows up at all on the segment.
5. Swap ports or patch leads if there is no link or intermittent link.
6. If one device works and another does not, isolate the bad cable/device before assuming the switch is bad.
"@
        }
        'mstp-ip' {
@"
MS/TP and IP field steps
1. Confirm whether the fault is on the IP side, the MS/TP side, or both.
2. For IP: verify VLAN, subnet, gateway, duplicate IP risk, and switch reachability.
3. For MS/TP: confirm controller power, polarity, termination, baud, MAC addressing, and segment wiring.
4. If there is an IP-to-MS/TP router or gateway, verify that it is reachable and powered before chasing field devices.
5. Use saved BMS flows and documentation to match the site design before changing settings.
6. Document exactly where communication stops: laptop to switch, switch to gateway, gateway to server, or router to field bus.
"@
        }
        'no-link' {
@"
No link light / no carrier
1. Check both ends for link lights.
2. Reseat or replace the patch cable.
3. Try another known-good port.
4. Review adapter link status on the laptop.
5. If the switch is managed, confirm the port is enabled and not err-disabled.
6. If PoE is involved, confirm the device is actually powered.
"@
        }
        'wrong-vlan' {
@"
Wrong VLAN / wrong subnet symptoms
1. Confirm the intended site IP profile and gateway.
2. If the gateway does not answer, suspect VLAN or local switch port placement first.
3. Compare the working device subnet with the failing device subnet.
4. On a managed switch, confirm access VLAN or trunk membership.
5. Review ARP/neighbor entries for unexpected adjacent addresses.
6. If the server is reachable only from another site profile, stop and verify the expected network design before changing IPs.
"@
        }
        'intermittent' {
@"
Intermittent network issue
1. Run continuous ping to the gateway and target at the same time if possible.
2. Watch for packet loss patterns: local only, target only, or both.
3. Check cable strain, damaged ports, bad couplers, and power-related resets.
4. Review switch port counters and error status when available.
5. Capture the exact time of drops so Event Viewer and BMS logs can be compared.
6. If the issue follows a cable or port move, treat the physical path as the primary suspect first.
"@
        }
        default {
@"
Network playbooks
- Managed switch
- Unmanaged switch
- MS/TP and IP
- No link light
- Wrong VLAN / subnet
- Intermittent network

Pick one of the playbook buttons to load the field steps.
"@
        }
    }

    $script:txtNetworkPlaybook.Text = $topicText
}

function Start-PingTerminal {
    Invoke-UiAction -Name 'Ping Terminal' -Action {
        $target = $script:txtPingTarget.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($target)) {
            throw 'Enter a host or IP to ping.'
        }
        if ($target -match '[\s&|<>^"]') {
            throw 'Ping target cannot contain spaces or command characters.'
        }

        $title = 'TEC Ping - {0}' -f $target
        $pingSwitch = '-n 4'
        $pingModeText = '4 ping replies'
        if ((Get-Variable -Name chkContinuousPing -Scope Script -ErrorAction SilentlyContinue) -and
            $script:chkContinuousPing -and
            $script:chkContinuousPing.Checked) {
            $pingSwitch = '-t'
            $pingModeText = 'continuous ping'
        }

        $command = 'title "{0}" & echo Ping to {1} ({2}) & echo This window stays open. Continuous ping can be stopped with CTRL+C. & echo. & ping {3} {1}' -f $title, $target, $pingModeText, $pingSwitch
        Start-Process -FilePath 'cmd.exe' -ArgumentList ('/k {0}' -f $command)
        Add-Log -Area 'Ping' -Level 'OK' -Message ("Opened ping terminal for {0} ({1})" -f $target, $pingModeText)
    }
}

function Run-DnsLookup {
    Invoke-UiAction -Name 'DNS Lookup' -Action {
        $target = $script:txtPingTarget.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($target)) {
            throw 'Enter a host or IP first.'
        }

        $resolved = $false
        if (Get-Command -Name Resolve-DnsName -ErrorAction SilentlyContinue) {
            try {
                $records = @(Resolve-DnsName -Name $target -ErrorAction Stop)
                foreach ($record in $records) {
                    if ($record.IPAddress) {
                        Add-Log -Area 'DNS' -Level 'OK' -Message ("{0} -> {1}" -f $target, $record.IPAddress)
                        $resolved = $true
                    }
                    elseif ($record.NameHost) {
                        Add-Log -Area 'DNS' -Level 'OK' -Message ("{0} -> {1}" -f $target, $record.NameHost)
                        $resolved = $true
                    }
                }
            }
            catch {
                Add-Log -Area 'DNS' -Level 'WARN' -Message ('Resolve-DnsName failed: {0}' -f $_.Exception.Message)
            }
        }

        if (-not $resolved) {
            Add-Log -Area 'DNS' -Level 'INFO' -Message 'Trying nslookup fallback.'
            $output = @(& nslookup.exe $target 2>&1)
            foreach ($line in $output) {
                if ($line) {
                    Add-Log -Area 'DNS' -Level 'INFO' -Message ([string]$line)
                }
            }
        }
    }
}

function Flush-DnsCache {
    Invoke-UiAction -Name 'Flush DNS' -Action {
        if (-not (Confirm-Action -Title 'Flush DNS Cache' -Message 'Flush the local DNS resolver cache on this laptop?')) {
            Add-Log -Area 'DNS' -Level 'WARN' -Message 'Flush DNS cancelled.'
            return
        }

        $output = @(& ipconfig.exe /flushdns 2>&1)
        foreach ($line in $output) {
            if (-not [string]::IsNullOrWhiteSpace([string]$line)) {
                Add-Log -Area 'DNS' -Level 'INFO' -Message ([string]$line)
            }
        }
    }
}

function Show-HostnameLookup {
    Invoke-UiAction -Name 'Hostname Lookup' -Action {
        $target = $script:txtPingTarget.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($target)) {
            throw 'Enter a host or IP first.'
        }

        $entry = [System.Net.Dns]::GetHostEntry($target)
        Add-Log -Area 'Hostname' -Level 'OK' -Message ('Input: {0}' -f $target)
        Add-Log -Area 'Hostname' -Level 'OK' -Message ('Host Name: {0}' -f $entry.HostName)
        foreach ($address in $entry.AddressList) {
            Add-Log -Area 'Hostname' -Level 'INFO' -Message ('Address: {0}' -f $address.IPAddressToString)
        }
    }
}

function Show-DiskSummary {
    Invoke-UiAction -Name 'Disk Summary' -Action {
        $drives = Get-WmiObject -Class Win32_LogicalDisk -Filter "DriveType = 3"
        foreach ($drive in $drives) {
            $freePercent = if ($drive.Size -gt 0) { ($drive.FreeSpace / $drive.Size) * 100 } else { 0 }
            $level = if ($freePercent -lt 15) { 'WARN' } else { 'OK' }
            Add-Log -Area 'Disk' -Level $level -Message ('Drive {0}: Free {1:N1} GB / {2:N1} GB ({3:N0}%)' -f $drive.DeviceID, ($drive.FreeSpace / 1GB), ($drive.Size / 1GB), $freePercent)
        }
    }
}

function Show-ServiceQuickCheck {
    Invoke-UiAction -Name 'Service Quick Check' -Action {
        foreach ($serviceName in @('Winmgmt', 'EventLog', 'Spooler', 'wuauserv', 'BITS')) {
            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($null -eq $service) {
                Add-Log -Area 'Services' -Level 'WARN' -Message ("{0} was not found." -f $serviceName)
                continue
            }

            $level = if ($service.Status -eq 'Running') { 'OK' } else { 'WARN' }
            Add-Log -Area 'Services' -Level $level -Message ('{0}: {1}' -f $service.Name, $service.Status)
        }
    }
}

function Show-RecentEventErrors {
    Invoke-UiAction -Name 'Recent Event Errors' -Action {
        $startTime = (Get-Date).AddDays(-1)
        try {
            $events = Get-WinEvent -FilterHashtable @{ LogName = 'System'; Level = 1, 2; StartTime = $startTime } -MaxEvents 15 -ErrorAction Stop
        }
        catch {
            $events = Get-EventLog -LogName System -EntryType Error -After $startTime -Newest 15 -ErrorAction Stop
        }

        if (-not $events) {
            Add-Log -Area 'Events' -Level 'OK' -Message 'No System critical/error events found in the last 24 hours.'
            return
        }

        foreach ($event in $events) {
            $eventTime = if ($event.PSObject.Properties['TimeCreated']) { $event.TimeCreated } else { $event.TimeGenerated }
            $eventId = if ($event.PSObject.Properties['Id']) { $event.Id } elseif ($event.PSObject.Properties['EventID']) { $event.EventID } else { $event.InstanceId }
            $message = ($event.Message -replace '\s+', ' ')
            if ($message.Length -gt 140) { $message = $message.Substring(0, 140) + '...' }
            Add-Log -Area 'Events' -Level 'WARN' -Message ('{0:yyyy-MM-dd HH:mm} | ID {1} | {2}' -f $eventTime, $eventId, $message)
        }
    }
}

function Show-ProblemDevices {
    Invoke-UiAction -Name 'Problem Devices' -Action {
        $devices = Get-WmiObject -Class Win32_PnPEntity | Where-Object { $_.ConfigManagerErrorCode -ne 0 }
        if (-not $devices) {
            Add-Log -Area 'Devices' -Level 'OK' -Message 'No Device Manager problem devices found.'
            return
        }

        foreach ($device in $devices) {
            Add-Log -Area 'Devices' -Level 'WARN' -Message ('{0} | Code {1}' -f $device.Name, $device.ConfigManagerErrorCode)
        }
    }
}

function Open-ServicesConsole {
    Invoke-UiAction -Name 'Services Console' -Action {
        Start-Process -FilePath 'services.msc'
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened Services.'
    }
}

function Open-EventViewer {
    Invoke-UiAction -Name 'Event Viewer' -Action {
        Start-Process -FilePath 'eventvwr.msc'
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened Event Viewer.'
    }
}

function Open-DeviceManager {
    Invoke-UiAction -Name 'Device Manager' -Action {
        Start-Process -FilePath 'devmgmt.msc'
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened Device Manager.'
    }
}

function Open-ComputerManagement {
    Invoke-UiAction -Name 'Computer Management' -Action {
        Start-Process -FilePath 'compmgmt.msc'
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened Computer Management.'
    }
}

function Open-LocalUsersConsole {
    Invoke-UiAction -Name 'Local Users' -Action {
        Start-Process -FilePath 'lusrmgr.msc'
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened Local Users and Groups.'
    }
}

function Open-ProgramsAndFeatures {
    Invoke-UiAction -Name 'Programs and Features' -Action {
        Start-Process -FilePath 'appwiz.cpl'
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened Programs and Features.'
    }
}

function Open-SystemProperties {
    Invoke-UiAction -Name 'System Properties' -Action {
        Start-Process -FilePath 'sysdm.cpl'
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened System Properties.'
    }
}

function Open-TaskManager {
    Invoke-UiAction -Name 'Task Manager' -Action {
        Start-Process -FilePath 'taskmgr.exe'
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened Task Manager.'
    }
}

function Open-CommandPrompt {
    Invoke-UiAction -Name 'Command Prompt' -Action {
        Start-Process -FilePath 'cmd.exe'
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened Command Prompt.'
    }
}

function Open-PowerShellConsole {
    Invoke-UiAction -Name 'PowerShell Console' -Action {
        Start-Process -FilePath 'powershell.exe'
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened PowerShell.'
    }
}

function Open-TaskScheduler {
    Invoke-UiAction -Name 'Task Scheduler' -Action {
        Start-Process -FilePath 'taskschd.msc'
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened Task Scheduler.'
    }
}

function Open-FirewallConsole {
    Invoke-UiAction -Name 'Firewall Console' -Action {
        Start-Process -FilePath 'wf.msc'
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened Windows Defender Firewall.'
    }
}

function Open-CredentialManager {
    Invoke-UiAction -Name 'Credential Manager' -Action {
        Start-Process -FilePath 'control.exe' -ArgumentList '/name Microsoft.CredentialManager'
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened Credential Manager.'
    }
}

function Open-SharedFolders {
    Invoke-UiAction -Name 'Shared Folders' -Action {
        Start-Process -FilePath 'fsmgmt.msc'
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened Shared Folders.'
    }
}

function Open-WindowsUpdateSettings {
    Invoke-UiAction -Name 'Windows Update' -Action {
        try {
            Start-Process -FilePath 'ms-settings:windowsupdate'
        }
        catch {
            Start-Process -FilePath 'control.exe' -ArgumentList '/name Microsoft.WindowsUpdate'
        }
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened Windows Update.'
    }
}

function Open-RemoteDesktopSettings {
    Invoke-UiAction -Name 'Remote Desktop Settings' -Action {
        Start-Process -FilePath 'SystemPropertiesRemote.exe'
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened Remote settings.'
    }
}

function Open-DevicesAndPrinters {
    Invoke-UiAction -Name 'Devices and Printers' -Action {
        Start-Process -FilePath 'control.exe' -ArgumentList '/name Microsoft.DevicesAndPrinters'
        Add-Log -Area 'Tools' -Level 'OK' -Message 'Opened Devices and Printers.'
    }
}

function Open-WebPage {
    Invoke-UiAction -Name 'Open Webpage' -Action {
        $url = $script:txtWebUrl.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($url)) {
            throw 'Enter a webpage URL.'
        }
        if ($url -notmatch '^https?://') {
            $url = 'http://' + $url
        }
        Start-Process -FilePath $url
        Add-Log -Area 'Web' -Level 'OK' -Message ("Opened {0}" -f $url)
    }
}

function Open-Rdp {
    Invoke-UiAction -Name 'Open RDP' -Action {
        $target = $script:txtRdpTarget.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($target)) {
            throw 'Enter an RDP target.'
        }
        Start-Process -FilePath 'mstsc.exe' -ArgumentList ('/v:{0}' -f $target)
        Add-Log -Area 'RDP' -Level 'OK' -Message ("Opened RDP to {0}" -f $target)
    }
}

function Open-NetworkSettings {
    Invoke-UiAction -Name 'Network Settings' -Action {
        Start-Process -FilePath 'ncpa.cpl'
        Add-Log -Area 'Network Settings' -Level 'OK' -Message 'Opened Network Connections.'
    }
}

function Save-TechnicianNotes {
    if (-not ((Get-Variable -Name txtNotes -Scope Script -ErrorAction SilentlyContinue) -and $script:txtNotes)) {
        return
    }

    $script:Config.Notes = $script:txtNotes.Text
    Save-Config
    Add-Log -Area 'Notes' -Level 'OK' -Message 'Saved notes.'
}

function Capture-Screenshot {
    Invoke-UiAction -Name 'Screenshot' -Action {
        $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bitmap = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
            $path = Join-Path -Path $script:ScreenshotFolder -ChildPath ("Screenshot_{0:yyyyMMdd_HHmmss}.png" -f (Get-Date))
            $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
            if ((Get-Variable -Name txtFeedbackScreenshot -Scope Script -ErrorAction SilentlyContinue) -and $script:txtFeedbackScreenshot) {
                $script:txtFeedbackScreenshot.Text = $path
            }
            if ((Get-Variable -Name txtScreenshotPath -Scope Script -ErrorAction SilentlyContinue) -and $script:txtScreenshotPath) {
                $script:txtScreenshotPath.Text = $path
            }
            Add-Log -Area 'Screenshot' -Level 'OK' -Message ("Saved screenshot: {0}" -f $path)
        }
        finally {
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
}

function New-DefaultBmsFlows {
    return @()
}

function New-DefaultImportantLinks {
    return @()
}

function New-DefaultBmsCommands {
    return @()
}

function Remove-LegacyBundledBmsFlowSample {
    $sampleCategoryName = 'Network Troubleshooting Sample'
    $beforeCount = @($script:BmsFlows).Count
    $script:BmsFlows = @($script:BmsFlows | Where-Object {
        $_.Name -ne $sampleCategoryName -or $_.StartStepId -ne 'start' -or
        -not (@($_.Steps | Where-Object { $_.Id -eq 'ping_test' }).Count -gt 0)
    })
    if (@($script:BmsFlows).Count -ne $beforeCount) {
        Save-BmsFlows
        Add-Log -Area 'BMS Flow' -Level 'INFO' -Message ('Removed legacy sample topic: {0}' -f $sampleCategoryName)
    }
}

function New-BmsStepObject {
    param(
        [string]$Name = 'New Step',
        [string]$Prompt = 'Enter the next troubleshooting question here.'
    )

    return [pscustomobject]@{
        Id = ([guid]::NewGuid().Guid)
        Name = $Name
        Prompt = $Prompt
        Buttons = @()
    }
}

function Normalize-BmsButton {
    param($Button)

    $text = ''
    $notes = ''
    $nextStepId = ''

    if ($Button -and $Button.PSObject.Properties['Text']) { $text = [string]$Button.Text }
    if ($Button -and $Button.PSObject.Properties['Notes']) { $notes = [string]$Button.Notes }
    elseif ($Button -and $Button.PSObject.Properties['Result']) { $notes = [string]$Button.Result }
    if ($Button -and $Button.PSObject.Properties['NextStepId']) { $nextStepId = [string]$Button.NextStepId }

    return [pscustomobject]@{
        Text = $text
        Notes = $notes
        NextStepId = $nextStepId
    }
}

function Normalize-BmsStep {
    param($Step)

    $id = if ($Step -and $Step.PSObject.Properties['Id'] -and $Step.Id) { [string]$Step.Id } else { ([guid]::NewGuid().Guid) }
    $name = if ($Step -and $Step.PSObject.Properties['Name'] -and $Step.Name) { [string]$Step.Name } else { 'New Step' }
    $prompt = if ($Step -and $Step.PSObject.Properties['Prompt']) { [string]$Step.Prompt } else { '' }
    $buttons = @()

    foreach ($button in @($Step.Buttons)) {
        $buttons += Normalize-BmsButton -Button $button
    }

    return [pscustomobject]@{
        Id = $id
        Name = $name
        Prompt = $prompt
        Buttons = $buttons
    }
}

function Normalize-BmsCategory {
    param($Category)

    $name = if ($Category -and $Category.PSObject.Properties['Name']) { [string]$Category.Name } elseif ($Category -and $Category.PSObject.Properties['Category']) { [string]$Category.Category } else { 'New BMS Topic' }
    $startStepId = if ($Category -and $Category.PSObject.Properties['StartStepId']) { [string]$Category.StartStepId } else { '' }
    $steps = @()

    if ($Category -and $Category.PSObject.Properties['Steps']) {
        foreach ($step in @($Category.Steps)) {
            $steps += Normalize-BmsStep -Step $step
        }
    }
    elseif ($Category -and $Category.PSObject.Properties['Prompt']) {
        # Migrate the old single-step shape into the new model.
        $migratedStep = Normalize-BmsStep -Step ([pscustomobject]@{
            Name = 'Start'
            Prompt = [string]$Category.Prompt
            Buttons = @($Category.Buttons)
        })
        $steps += $migratedStep
        $startStepId = $migratedStep.Id
    }

    if ($steps.Count -eq 0) {
        $newStep = New-BmsStepObject -Name 'Start' -Prompt 'Enter the first troubleshooting question here.'
        $steps += $newStep
        $startStepId = $newStep.Id
    }

    if (-not $startStepId -or -not (@($steps | Where-Object { $_.Id -eq $startStepId }).Count)) {
        $startStepId = $steps[0].Id
    }

    return [pscustomobject]@{
        Name = $name
        StartStepId = $startStepId
        Steps = $steps
    }
}

function Normalize-LinkItem {
    param($Item)

    $category = if ($Item -and $Item.PSObject.Properties['Category']) { [string]$Item.Category } else { 'General' }
    $title = if ($Item -and $Item.PSObject.Properties['Title']) { [string]$Item.Title } else { '' }
    $target = if ($Item -and $Item.PSObject.Properties['Target']) { [string]$Item.Target } elseif ($Item -and $Item.PSObject.Properties['Url']) { [string]$Item.Url } else { '' }
    $notes = if ($Item -and $Item.PSObject.Properties['Notes']) { [string]$Item.Notes } else { '' }

    return [pscustomobject]@{
        Category = $category
        Title = $title
        Target = $target
        Notes = $notes
    }
}

function Normalize-BmsCommand {
    param($Item)

    $name = if ($Item -and $Item.PSObject.Properties['Name']) { [string]$Item.Name } else { 'New Command' }
    $category = if ($Item -and $Item.PSObject.Properties['Category']) { [string]$Item.Category } else { 'General' }
    $description = if ($Item -and $Item.PSObject.Properties['Description']) { [string]$Item.Description } else { '' }
    $commandType = if ($Item -and $Item.PSObject.Properties['CommandType']) { [string]$Item.CommandType } else { 'PowerShell' }
    $commandText = if ($Item -and $Item.PSObject.Properties['CommandText']) { [string]$Item.CommandText } else { '' }
    $requiresAdmin = $false
    $confirmBeforeRun = $true
    $keepWindowOpen = $true

    if ($Item -and $Item.PSObject.Properties['RequiresAdmin']) {
        $requiresAdmin = [bool]$Item.RequiresAdmin
    }
    if ($Item -and $Item.PSObject.Properties['ConfirmBeforeRun']) {
        $confirmBeforeRun = [bool]$Item.ConfirmBeforeRun
    }
    if ($Item -and $Item.PSObject.Properties['KeepWindowOpen']) {
        $keepWindowOpen = [bool]$Item.KeepWindowOpen
    }

    return [pscustomobject]@{
        Name = $name
        Category = $category
        Description = $description
        CommandType = $commandType
        CommandText = $commandText
        RequiresAdmin = $requiresAdmin
        ConfirmBeforeRun = $confirmBeforeRun
        KeepWindowOpen = $keepWindowOpen
    }
}

function Save-BmsFlows {
    try {
        $script:BmsFlows | ConvertTo-Json -Depth 10 | Set-Content -Path $script:BmsFlowFile -Encoding UTF8
        Add-Log -Area 'BMS Flow' -Level 'OK' -Message 'Saved BMS troubleshooting flow data.'
    }
    catch {
        Add-Log -Area 'BMS Flow' -Level 'ERROR' -Message ('Could not save BMS flow data: {0}' -f $_.Exception.Message)
    }
}

function Load-BmsFlows {
    if (Test-Path -Path $script:BmsFlowFile) {
        try {
            $loaded = @(Get-Content -Path $script:BmsFlowFile -Raw | ConvertFrom-Json)
            $script:BmsFlows = @()
            foreach ($category in $loaded) {
                $script:BmsFlows += Normalize-BmsCategory -Category $category
            }
        }
        catch {
            $script:BmsFlows = @(New-DefaultBmsFlows)
            Save-BmsFlows
        }
    }
    else {
        $script:BmsFlows = @(New-DefaultBmsFlows)
        Save-BmsFlows
    }
}

function Save-ImportantLinks {
    try {
        $script:ImportantLinks | ConvertTo-Json -Depth 6 | Set-Content -Path $script:LinksFile -Encoding UTF8
        Add-Log -Area 'Links' -Level 'OK' -Message 'Saved Important Links.'
    }
    catch {
        Add-Log -Area 'Links' -Level 'ERROR' -Message ('Could not save links: {0}' -f $_.Exception.Message)
    }
}

function Save-BmsCommands {
    try {
        $script:BmsCommands | ConvertTo-Json -Depth 6 | Set-Content -Path $script:BmsCommandsFile -Encoding UTF8
        Add-Log -Area 'BMS Commands' -Level 'OK' -Message 'Saved BMS command definitions.'
    }
    catch {
        Add-Log -Area 'BMS Commands' -Level 'ERROR' -Message ('Could not save BMS commands: {0}' -f $_.Exception.Message)
    }
}

function Load-BmsCommands {
    if (Test-Path -Path $script:BmsCommandsFile) {
        try {
            $loaded = @(Get-Content -Path $script:BmsCommandsFile -Raw | ConvertFrom-Json)
            $script:BmsCommands = @()
            foreach ($item in $loaded) {
                $script:BmsCommands += Normalize-BmsCommand -Item $item
            }
        }
        catch {
            $script:BmsCommands = @(New-DefaultBmsCommands)
            Save-BmsCommands
        }
    }
    else {
        $script:BmsCommands = @(New-DefaultBmsCommands)
        Save-BmsCommands
    }
}

function Load-ImportantLinks {
    if (Test-Path -Path $script:LinksFile) {
        try {
            $loaded = @(Get-Content -Path $script:LinksFile -Raw | ConvertFrom-Json)
            $script:ImportantLinks = @()
            foreach ($item in $loaded) {
                $script:ImportantLinks += Normalize-LinkItem -Item $item
            }
        }
        catch {
            $script:ImportantLinks = @(New-DefaultImportantLinks)
            Save-ImportantLinks
        }
    }
    else {
        $script:ImportantLinks = @(New-DefaultImportantLinks)
        Save-ImportantLinks
    }
}

function Get-BmsCategoryByName {
    param([string]$Name)

    return ($script:BmsFlows | Where-Object { $_.Name -eq $Name } | Select-Object -First 1)
}

function Get-SelectedBmsCategory {
    if (-not ((Get-Variable -Name lstBmsTopics -Scope Script -ErrorAction SilentlyContinue) -and $script:lstBmsTopics)) {
        return $null
    }
    if ($script:lstBmsTopics.SelectedItem -eq $null) {
        return $null
    }

    return Get-BmsCategoryByName -Name ([string]$script:lstBmsTopics.SelectedItem)
}

function Refresh-BmsCategoryList {
    if (-not ((Get-Variable -Name lstBmsTopics -Scope Script -ErrorAction SilentlyContinue) -and $script:lstBmsTopics)) {
        return
    }

    $previous = $script:lstBmsTopics.SelectedItem
    $script:lstBmsTopics.Items.Clear()
    foreach ($category in @($script:BmsFlows | Sort-Object -Property Name)) {
        [void]$script:lstBmsTopics.Items.Add($category.Name)
    }

    if ($previous -and $script:lstBmsTopics.Items.Contains($previous)) {
        $script:lstBmsTopics.SelectedItem = $previous
    }
    elseif ($script:lstBmsTopics.Items.Count -gt 0) {
        $script:lstBmsTopics.SelectedIndex = 0
    }
    else {
        $script:CurrentBmsCategory = $null
        $script:CurrentBmsStepId = $null
        if ((Get-Variable -Name tvBmsFlowOutline -Scope Script -ErrorAction SilentlyContinue) -and $script:tvBmsFlowOutline) {
            $script:tvBmsFlowOutline.Nodes.Clear()
        }
    }
}

function Refresh-BmsFlowOutline {
    if (-not ((Get-Variable -Name tvBmsFlowOutline -Scope Script -ErrorAction SilentlyContinue) -and $script:tvBmsFlowOutline)) {
        return
    }

    $script:tvBmsFlowOutline.BeginUpdate()
    $script:tvBmsFlowOutline.Nodes.Clear()

    $category = Get-SelectedBmsCategory
    if (-not $category) {
        $script:tvBmsFlowOutline.EndUpdate()
        return
    }

    $root = New-Object System.Windows.Forms.TreeNode($category.Name)
    $root.NodeFont = $fontSection

    foreach ($step in @($category.Steps)) {
        $stepLabel = if ($category.StartStepId -eq $step.Id) { '{0} [Start]' -f $step.Name } else { $step.Name }
        $stepNode = New-Object System.Windows.Forms.TreeNode($stepLabel)
        $stepNode.Tag = $step.Id
        foreach ($button in @($step.Buttons)) {
            $targetText = ''
            if ($button.NextStepId) {
                $nextStep = Get-BmsStepById -Category $category -StepId $button.NextStepId
                if ($nextStep) {
                    $targetText = ' -> {0}' -f $nextStep.Name
                }
            }
            $buttonNode = New-Object System.Windows.Forms.TreeNode(('{0}{1}' -f $button.Text, $targetText))
            $buttonNode.Tag = $step.Id
            [void]$stepNode.Nodes.Add($buttonNode)
        }
        [void]$root.Nodes.Add($stepNode)
    }

    [void]$script:tvBmsFlowOutline.Nodes.Add($root)
    $root.Expand()
    foreach ($stepNode in $root.Nodes) {
        $stepNode.Expand()
    }
    $script:tvBmsFlowOutline.EndUpdate()
}

function Select-BmsStepFromOutline {
    param([string]$StepId)

    if ([string]::IsNullOrWhiteSpace($StepId)) {
        return
    }

    $category = Get-SelectedBmsCategory
    $step = Get-BmsStepById -Category $category -StepId $StepId
    if (-not $step) {
        return
    }

    $script:CurrentBmsStepId = $step.Id
    if ((Get-Variable -Name lstBmsSteps -Scope Script -ErrorAction SilentlyContinue) -and
        $script:lstBmsSteps -and
        $script:lstBmsSteps.Items.Contains($step.Name)) {
        $script:lstBmsSteps.SelectedItem = $step.Name
    }
    else {
        Select-BmsStep
    }
}

function Get-BmsStepById {
    param(
        $Category,
        [string]$StepId
    )

    if (-not $Category -or -not $StepId) {
        return $null
    }

    return ($Category.Steps | Where-Object { $_.Id -eq $StepId } | Select-Object -First 1)
}

function Get-BmsStepByName {
    param(
        $Category,
        [string]$StepName
    )

    if (-not $Category -or -not $StepName) {
        return $null
    }

    return ($Category.Steps | Where-Object { $_.Name -eq $StepName } | Select-Object -First 1)
}

function Get-SelectedBmsStep {
    $category = Get-SelectedBmsCategory
    if (-not $category) {
        return $null
    }
    if ($script:CurrentBmsStepId) {
        $selectedStep = Get-BmsStepById -Category $category -StepId $script:CurrentBmsStepId
        if ($selectedStep) {
            return $selectedStep
        }
    }
    if ((Get-Variable -Name lstBmsSteps -Scope Script -ErrorAction SilentlyContinue) -and
        $script:lstBmsSteps -and
        $script:lstBmsSteps.SelectedItem -ne $null) {
        return Get-BmsStepByName -Category $category -StepName ([string]$script:lstBmsSteps.SelectedItem)
    }

    return $null
}

function Refresh-BmsStepList {
    if (-not ((Get-Variable -Name lstBmsSteps -Scope Script -ErrorAction SilentlyContinue) -and $script:lstBmsSteps)) {
        return
    }

    $category = Get-SelectedBmsCategory
    $previous = $script:lstBmsSteps.SelectedItem
    $script:lstBmsSteps.Items.Clear()

    if (-not $category) {
        return
    }

    foreach ($step in @($category.Steps)) {
        [void]$script:lstBmsSteps.Items.Add($step.Name)
    }

    if ($previous -and $script:lstBmsSteps.Items.Contains($previous)) {
        $script:lstBmsSteps.SelectedItem = $previous
    }
    else {
        $selectedStep = Get-BmsStepById -Category $category -StepId $category.StartStepId
        if ($selectedStep) {
            $script:lstBmsSteps.SelectedItem = $selectedStep.Name
        }
        elseif ($script:lstBmsSteps.Items.Count -gt 0) {
            $script:lstBmsSteps.SelectedIndex = 0
        }
    }
}

function Refresh-BmsStepTargetOptions {
    if (-not ((Get-Variable -Name cboBmsNextStep -Scope Script -ErrorAction SilentlyContinue) -and $script:cboBmsNextStep)) {
        return
    }

    $category = Get-SelectedBmsCategory
    $currentChoice = $script:cboBmsNextStep.SelectedItem
    $script:cboBmsNextStep.Items.Clear()
    [void]$script:cboBmsNextStep.Items.Add('(No next step)')

    if ($category) {
        foreach ($step in @($category.Steps)) {
            [void]$script:cboBmsNextStep.Items.Add($step.Name)
        }
    }

    if ($currentChoice -and $script:cboBmsNextStep.Items.Contains($currentChoice)) {
        $script:cboBmsNextStep.SelectedItem = $currentChoice
    }
    else {
        $script:cboBmsNextStep.SelectedIndex = 0
    }
}

function Clear-BmsButtonEditor {
    if ((Get-Variable -Name lstBmsButtons -Scope Script -ErrorAction SilentlyContinue) -and $script:lstBmsButtons) {
        $script:lstBmsButtons.ClearSelected()
    }
    $script:txtBmsButtonText.Text = ''
    $script:txtBmsButtonNotes.Text = ''
    Refresh-BmsStepTargetOptions
    if ($script:cboBmsNextStep.Items.Count -gt 0) {
        $script:cboBmsNextStep.SelectedIndex = 0
    }
}

function Render-BmsButtons {
    if (-not ((Get-Variable -Name pnlBmsButtons -Scope Script -ErrorAction SilentlyContinue) -and $script:pnlBmsButtons)) {
        return
    }

    $script:pnlBmsButtons.Controls.Clear()
    $step = Get-SelectedBmsStep
    if (-not $step) {
        return
    }

    if (@($step.Buttons).Count -eq 0) {
        $infoLabel = New-Object System.Windows.Forms.Label
        $infoLabel.Text = 'This step does not have any more choices. Use Back or Restart Flow to move somewhere else in the path.'
        $infoLabel.Location = New-Object System.Drawing.Point(0, 0)
        $infoLabel.Size = New-Object System.Drawing.Size([Math]::Max(200, $script:pnlBmsButtons.ClientSize.Width - 18), 54)
        $infoLabel.ForeColor = $colorMuted
        $infoLabel | Add-Member -NotePropertyName ThemeTone -NotePropertyValue 'Muted' -Force
        $script:pnlBmsButtons.Controls.Add($infoLabel)
        return
    }

    $buttonWidth = [Math]::Max(200, [Math]::Min(220, $script:pnlBmsButtons.ClientSize.Width - 24))
    foreach ($choice in @($step.Buttons)) {
        $button = New-Button -Text $choice.Text -OnClick { Show-BmsChoice -Choice $this.Tag } -X 0 -Y 0 -Width $buttonWidth -Height 38
        $button.Tag = $choice
        $button.Margin = New-Object System.Windows.Forms.Padding(0, 0, 8, 8)
        if ($script:toolTip) {
            $tipParts = @()
            if (-not [string]::IsNullOrWhiteSpace([string]$choice.Notes)) {
                $tipParts += [string]$choice.Notes
            }
            if (-not [string]::IsNullOrWhiteSpace([string]$choice.NextStepId)) {
                $category = Get-SelectedBmsCategory
                $nextStep = Get-BmsStepById -Category $category -StepId ([string]$choice.NextStepId)
                if ($nextStep) {
                    $tipParts += ('Next step: {0}' -f $nextStep.Name)
                }
            }
            if (@($tipParts).Count -gt 0) {
                $script:toolTip.SetToolTip($button, ($tipParts -join [Environment]::NewLine + [Environment]::NewLine))
            }
        }
        $script:pnlBmsButtons.Controls.Add($button)
    }
}

function Select-BmsCategory {
    $category = Get-SelectedBmsCategory
    if (-not $category) {
        return
    }

    $script:BmsStepHistory.Clear()
    $script:CurrentBmsCategory = $category.Name
    $script:CurrentBmsStepId = $category.StartStepId
    $script:txtBmsCategory.Text = $category.Name
    Refresh-BmsStepList
    Refresh-BmsStepTargetOptions
    Refresh-BmsFlowOutline
    if ((Get-Variable -Name lblBmsActiveCategory -Scope Script -ErrorAction SilentlyContinue) -and $script:lblBmsActiveCategory) {
        $script:lblBmsActiveCategory.Text = ('Category: {0}' -f $category.Name)
    }
    if ($script:lstBmsSteps.SelectedItem -ne $null) {
        Select-BmsStep
    }
    Add-Log -Area 'BMS Flow' -Level 'INFO' -Message ("Loaded category: {0}" -f $category.Name)
}

function Select-BmsStep {
    $category = Get-SelectedBmsCategory
    $step = Get-SelectedBmsStep
    if (-not $category -or -not $step) {
        return
    }

    $script:CurrentBmsCategory = $category.Name
    $script:CurrentBmsStepId = $step.Id
    $script:txtBmsCategory.Text = $category.Name
    $script:txtBmsStepName.Text = $step.Name
    $script:txtBmsPrompt.Text = $step.Prompt
    if ((Get-Variable -Name lblBmsActiveStep -Scope Script -ErrorAction SilentlyContinue) -and $script:lblBmsActiveStep) {
        $script:lblBmsActiveStep.Text = ('Step: {0}' -f $step.Name)
    }
    if ((Get-Variable -Name txtBmsRunPrompt -Scope Script -ErrorAction SilentlyContinue) -and $script:txtBmsRunPrompt) {
        $script:txtBmsRunPrompt.Text = $step.Prompt
    }
    $script:lstBmsButtons.Items.Clear()
    foreach ($choice in @($step.Buttons)) {
        [void]$script:lstBmsButtons.Items.Add($choice.Text)
    }

    Clear-BmsButtonEditor
    if (-not $script:PreserveBmsRunResult -and (Get-Variable -Name txtBmsResult -Scope Script -ErrorAction SilentlyContinue) -and $script:txtBmsResult) {
        if (@($step.Buttons).Count -eq 0) {
            $script:txtBmsResult.Text = $step.Prompt
        }
        else {
            $script:txtBmsResult.Text = ''
        }
    }
    Render-BmsButtons
    Refresh-BmsFlowOutline
    Add-Log -Area 'BMS Flow' -Level 'INFO' -Message ("Loaded step: {0}" -f $step.Name)
}

function Show-BmsChoice {
    param($Choice)

    if (-not $Choice) {
        return
    }

    $notes = [string]$Choice.Notes
    $nextStepId = [string]$Choice.NextStepId
    if ($nextStepId) {
        $category = Get-SelectedBmsCategory
        $nextStep = Get-BmsStepById -Category $category -StepId $nextStepId
        if ($nextStep) {
            if ($script:CurrentBmsStepId) {
                [void]$script:BmsStepHistory.Add([string]$script:CurrentBmsStepId)
            }
            $script:PreserveBmsRunResult = $true
            $script:CurrentBmsStepId = $nextStep.Id
            $script:lstBmsSteps.SelectedItem = $nextStep.Name
        }
    }

    $script:txtBmsResult.Text = $notes
    $script:PreserveBmsRunResult = $false
    Add-Log -Area 'BMS Flow' -Level 'INFO' -Message ("Selected step button: {0}" -f $Choice.Text)
}

function Go-BackBmsStep {
    if ($script:BmsStepHistory.Count -eq 0) {
        return
    }

    $previousStepId = $script:BmsStepHistory[$script:BmsStepHistory.Count - 1]
    $script:BmsStepHistory.RemoveAt($script:BmsStepHistory.Count - 1)
    $category = Get-SelectedBmsCategory
    $previousStep = Get-BmsStepById -Category $category -StepId $previousStepId
    if (-not $previousStep) {
        return
    }

    $script:txtBmsResult.Text = ''
    $script:CurrentBmsStepId = $previousStep.Id
    if ($script:lstBmsSteps.Items.Contains($previousStep.Name)) {
        $script:lstBmsSteps.SelectedItem = $previousStep.Name
    }
}

function Load-BmsButtonForEdit {
    $step = Get-SelectedBmsStep
    if (-not $step -or $script:lstBmsButtons.SelectedItem -eq $null) {
        return
    }

    $buttonText = [string]$script:lstBmsButtons.SelectedItem
    $choice = ($step.Buttons | Where-Object { $_.Text -eq $buttonText } | Select-Object -First 1)
    if ($choice) {
        $script:txtBmsButtonText.Text = $choice.Text
        $script:txtBmsButtonNotes.Text = $choice.Notes
        Refresh-BmsStepTargetOptions

        $targetName = '(No next step)'
        if ($choice.NextStepId) {
            $category = Get-SelectedBmsCategory
            $targetStep = Get-BmsStepById -Category $category -StepId $choice.NextStepId
            if ($targetStep) {
                $targetName = $targetStep.Name
            }
        }

        if ($script:cboBmsNextStep.Items.Contains($targetName)) {
            $script:cboBmsNextStep.SelectedItem = $targetName
        }
    }
}

function Save-BmsCategory {
    if (-not (Assert-AdminMode -Feature 'create or edit BMS categories')) {
        return
    }

    $oldCategory = $script:CurrentBmsCategory
    $categoryName = $script:txtBmsCategory.Text.Trim()
    if ([string]::IsNullOrWhiteSpace($categoryName)) {
        [System.Windows.Forms.MessageBox]::Show(
            'Category name is required.',
            'BMS Flow',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }

    $duplicate = ($script:BmsFlows | Where-Object { $_.Name -eq $categoryName -and $_.Name -ne $oldCategory } | Select-Object -First 1)
    if ($duplicate) {
        [System.Windows.Forms.MessageBox]::Show(
            'A BMS topic with that name already exists. Use a different name.',
            'BMS Flow',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }

    $category = ($script:BmsFlows | Where-Object { $_.Name -eq $oldCategory } | Select-Object -First 1)
    if (-not $category) {
        $startStep = New-BmsStepObject -Name 'Start' -Prompt 'Enter the first troubleshooting question here.'
        $category = [pscustomobject]@{
            Name = $categoryName
            StartStepId = $startStep.Id
            Steps = @($startStep)
        }
        $script:BmsFlows += $category
    }
    else {
        $category.Name = $categoryName
    }

    $script:CurrentBmsCategory = $categoryName
    Save-BmsFlows
    Refresh-BmsCategoryList
    $script:lstBmsTopics.SelectedItem = $categoryName
}

function New-BmsCategory {
    if (-not (Assert-AdminMode -Feature 'create BMS categories')) {
        return
    }

    $newName = 'New BMS Topic'
    $counter = 1
    while (@($script:BmsFlows | Where-Object { $_.Name -eq $newName }).Count -gt 0) {
        $counter++
        $newName = 'New BMS Topic {0}' -f $counter
    }

    $startStep = New-BmsStepObject -Name 'Start' -Prompt 'Enter the first troubleshooting question here.'
    $script:BmsFlows += [pscustomobject]@{
        Name = $newName
        StartStepId = $startStep.Id
        Steps = @($startStep)
    }

    Save-BmsFlows
    Refresh-BmsCategoryList
    $script:lstBmsTopics.SelectedItem = $newName
}

function Delete-BmsCategory {
    if (-not (Assert-AdminMode -Feature 'delete BMS categories')) {
        return
    }

    $category = Get-SelectedBmsCategory
    if (-not $category) {
        return
    }

    if (-not (Confirm-Action -Title 'Delete BMS Category' -Message ("Delete BMS troubleshooting category '{0}'?" -f $category.Name))) {
        return
    }

    $script:BmsFlows = @($script:BmsFlows | Where-Object { $_.Name -ne $category.Name })
    $script:CurrentBmsCategory = $null
    $script:CurrentBmsStepId = $null
    Save-BmsFlows
    Refresh-BmsCategoryList
    if ($script:txtBmsCategory) { $script:txtBmsCategory.Text = '' }
    if ($script:txtBmsStepName) { $script:txtBmsStepName.Text = '' }
    if ($script:txtBmsPrompt) { $script:txtBmsPrompt.Text = '' }
    if ($script:txtBmsResult) { $script:txtBmsResult.Text = '' }
    if ($script:lstBmsButtons) { $script:lstBmsButtons.Items.Clear() }
    if ($script:lstBmsSteps) { $script:lstBmsSteps.Items.Clear() }
    if ($script:pnlBmsButtons) { $script:pnlBmsButtons.Controls.Clear() }
    if ((Get-Variable -Name txtBmsRunPrompt -Scope Script -ErrorAction SilentlyContinue) -and $script:txtBmsRunPrompt) { $script:txtBmsRunPrompt.Text = '' }
    if ((Get-Variable -Name tvBmsFlowOutline -Scope Script -ErrorAction SilentlyContinue) -and $script:tvBmsFlowOutline) { $script:tvBmsFlowOutline.Nodes.Clear() }
}

function Save-BmsStep {
    if (-not (Assert-AdminMode -Feature 'create or edit BMS steps')) {
        return
    }

    $category = Get-SelectedBmsCategory
    if (-not $category) {
        return
    }

    $stepName = $script:txtBmsStepName.Text.Trim()
    $prompt = $script:txtBmsPrompt.Text.Trim()
    if ([string]::IsNullOrWhiteSpace($stepName)) {
        [System.Windows.Forms.MessageBox]::Show(
            'Step name is required.',
            'BMS Flow',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }

    $duplicateStep = ($category.Steps | Where-Object { $_.Name -eq $stepName -and $_.Id -ne $script:CurrentBmsStepId } | Select-Object -First 1)
    if ($duplicateStep) {
        [System.Windows.Forms.MessageBox]::Show(
            'A step with that name already exists in this topic.',
            'BMS Flow',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }

    $step = Get-BmsStepById -Category $category -StepId $script:CurrentBmsStepId
    if (-not $step) {
        $step = New-BmsStepObject -Name $stepName -Prompt $prompt
        $category.Steps = @($category.Steps) + @($step)
        if (-not $category.StartStepId) {
            $category.StartStepId = $step.Id
        }
    }
    else {
        $step.Name = $stepName
        $step.Prompt = $prompt
    }

    $script:CurrentBmsStepId = $step.Id
    Save-BmsFlows
    Refresh-BmsStepList
    Refresh-BmsStepTargetOptions
    $script:lstBmsSteps.SelectedItem = $step.Name
}

function New-BmsStep {
    if (-not (Assert-AdminMode -Feature 'create BMS steps')) {
        return
    }

    $category = Get-SelectedBmsCategory
    if (-not $category) {
        return
    }

    $newName = 'New Step'
    $counter = 1
    while (@($category.Steps | Where-Object { $_.Name -eq $newName }).Count -gt 0) {
        $counter++
        $newName = 'New Step {0}' -f $counter
    }

    $newStep = New-BmsStepObject -Name $newName -Prompt 'Enter the next troubleshooting question here.'
    $category.Steps = @($category.Steps) + @($newStep)
    if (-not $category.StartStepId) {
        $category.StartStepId = $newStep.Id
    }

    $script:CurrentBmsStepId = $newStep.Id
    Save-BmsFlows
    Refresh-BmsStepList
    $script:lstBmsSteps.SelectedItem = $newName
}

function Delete-BmsStep {
    if (-not (Assert-AdminMode -Feature 'delete BMS steps')) {
        return
    }

    $category = Get-SelectedBmsCategory
    $step = Get-SelectedBmsStep
    if (-not $category -or -not $step) {
        return
    }

    if (@($category.Steps).Count -le 1) {
        [System.Windows.Forms.MessageBox]::Show(
            'Each BMS topic needs at least one step.',
            'BMS Flow',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }

    if (-not (Confirm-Action -Title 'Delete BMS Step' -Message ("Delete step '{0}'?" -f $step.Name))) {
        return
    }

    $category.Steps = @($category.Steps | Where-Object { $_.Id -ne $step.Id })
    foreach ($existingStep in @($category.Steps)) {
        foreach ($button in @($existingStep.Buttons)) {
            if ($button.NextStepId -eq $step.Id) {
                $button.NextStepId = ''
            }
        }
    }

    if ($category.StartStepId -eq $step.Id) {
        $category.StartStepId = $category.Steps[0].Id
    }

    $script:CurrentBmsStepId = $category.StartStepId
    Save-BmsFlows
    Refresh-BmsStepList
}

function Set-BmsStartStep {
    if (-not (Assert-AdminMode -Feature 'set the start step')) {
        return
    }

    $category = Get-SelectedBmsCategory
    $step = Get-SelectedBmsStep
    if (-not $category -or -not $step) {
        return
    }

    $category.StartStepId = $step.Id
    $script:CurrentBmsStepId = $step.Id
    Save-BmsFlows
    Refresh-BmsStepList
    Refresh-BmsFlowOutline
    Add-Log -Area 'BMS Flow' -Level 'OK' -Message ("Start step set to: {0}" -f $step.Name)
}

function New-BmsButton {
    if (-not (Assert-AdminMode -Feature 'create BMS buttons')) {
        return
    }

    Clear-BmsButtonEditor
    $script:txtBmsButtonText.Focus()
}

function Save-BmsButton {
    if (-not (Assert-AdminMode -Feature 'create or edit BMS buttons')) {
        return
    }

    $category = Get-SelectedBmsCategory
    $step = Get-SelectedBmsStep
    if (-not $category -or -not $step) {
        return
    }

    $buttonText = $script:txtBmsButtonText.Text.Trim()
    $buttonNotes = $script:txtBmsButtonNotes.Text.Trim()
    if ([string]::IsNullOrWhiteSpace($buttonNotes) -and $script:txtBmsResult) {
        $buttonNotes = $script:txtBmsResult.Text.Trim()
    }
    if ([string]::IsNullOrWhiteSpace($buttonText)) {
        [System.Windows.Forms.MessageBox]::Show(
            'Button text is required.',
            'BMS Flow',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }

    $selectedButtonName = $null
    if ($script:lstBmsButtons.SelectedItem -ne $null) {
        $selectedButtonName = [string]$script:lstBmsButtons.SelectedItem
    }

    $buttons = @($step.Buttons)
    $duplicateButton = ($buttons | Where-Object { $_.Text -eq $buttonText -and $_.Text -ne $selectedButtonName } | Select-Object -First 1)
    if ($duplicateButton) {
        [System.Windows.Forms.MessageBox]::Show(
            'A button with that text already exists in this step.',
            'BMS Flow',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }

    $existing = $null
    if ($selectedButtonName) {
        $existing = ($buttons | Where-Object { $_.Text -eq $selectedButtonName } | Select-Object -First 1)
    }

    if (-not $existing -and $buttons.Count -ge 10) {
        [System.Windows.Forms.MessageBox]::Show(
            'Each step supports up to 10 buttons.',
            'BMS Flow',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }

    $nextStepId = ''
    if ($script:cboBmsNextStep.SelectedItem -and [string]$script:cboBmsNextStep.SelectedItem -ne '(No next step)') {
        $nextStep = Get-BmsStepByName -Category $category -StepName ([string]$script:cboBmsNextStep.SelectedItem)
        if ($nextStep) {
            $nextStepId = $nextStep.Id
        }
    }

    if ($existing) {
        $existing.Text = $buttonText
        $existing.Notes = $buttonNotes
        $existing.NextStepId = $nextStepId
    }
    else {
        $buttons += [pscustomobject]@{
            Text = $buttonText
            Notes = $buttonNotes
            NextStepId = $nextStepId
        }
        $step.Buttons = $buttons
    }

    Save-BmsFlows
    Select-BmsStep
    Refresh-BmsFlowOutline
    $script:lstBmsButtons.SelectedItem = $buttonText
}

function Delete-BmsButton {
    if (-not (Assert-AdminMode -Feature 'delete BMS buttons')) {
        return
    }

    $step = Get-SelectedBmsStep
    if (-not $step -or $script:lstBmsButtons.SelectedItem -eq $null) {
        return
    }

    $buttonText = [string]$script:lstBmsButtons.SelectedItem
    $step.Buttons = @($step.Buttons | Where-Object { $_.Text -ne $buttonText })
    Save-BmsFlows
    Select-BmsStep
    Refresh-BmsFlowOutline
}

function Reset-BmsToStartStep {
    $category = Get-SelectedBmsCategory
    if (-not $category) {
        return
    }

    $startStep = Get-BmsStepById -Category $category -StepId $category.StartStepId
    if (-not $startStep) {
        return
    }

    $script:BmsStepHistory.Clear()
    $script:txtBmsResult.Text = ''
    $script:CurrentBmsStepId = $startStep.Id
    if ($script:lstBmsSteps.Items.Contains($startStep.Name)) {
        $script:lstBmsSteps.SelectedItem = $startStep.Name
    }
}

function Refresh-LinkList {
    if (-not ((Get-Variable -Name lstImportantLinks -Scope Script -ErrorAction SilentlyContinue) -and $script:lstImportantLinks)) {
        return
    }

    $previous = $script:lstImportantLinks.SelectedItem
    $script:lstImportantLinks.Items.Clear()
    foreach ($item in @($script:ImportantLinks)) {
        [void]$script:lstImportantLinks.Items.Add(('[{0}] {1}' -f $item.Category, $item.Title))
    }

    if ($previous -and $script:lstImportantLinks.Items.Contains($previous)) {
        $script:lstImportantLinks.SelectedItem = $previous
    }
    elseif ($script:lstImportantLinks.Items.Count -gt 0) {
        $script:lstImportantLinks.SelectedIndex = 0
    }
}

function Get-SelectedLinkItem {
    if (-not ((Get-Variable -Name lstImportantLinks -Scope Script -ErrorAction SilentlyContinue) -and $script:lstImportantLinks)) {
        return $null
    }
    if ($script:lstImportantLinks.SelectedIndex -lt 0) {
        return $null
    }
    if ($script:lstImportantLinks.SelectedIndex -ge $script:ImportantLinks.Count) {
        return $null
    }

    return $script:ImportantLinks[$script:lstImportantLinks.SelectedIndex]
}

function Load-LinkItem {
    $item = Get-SelectedLinkItem
    if (-not $item) {
        return
    }

    $script:txtLinkCategory.Text = $item.Category
    $script:txtLinkTitle.Text = $item.Title
    $script:txtLinkTarget.Text = $item.Target
    $script:txtLinkNotes.Text = $item.Notes
}

function Save-LinkItem {
    if (-not (Assert-AdminMode -Feature 'create or edit important links')) {
        return
    }

    $category = $script:txtLinkCategory.Text.Trim()
    $title = $script:txtLinkTitle.Text.Trim()
    $target = $script:txtLinkTarget.Text.Trim()
    $notes = $script:txtLinkNotes.Text.Trim()

    if ([string]::IsNullOrWhiteSpace($title)) {
        [System.Windows.Forms.MessageBox]::Show(
            'Link title is required.',
            'Important Links',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }

    if ([string]::IsNullOrWhiteSpace($category)) {
        $category = 'General'
    }

    $item = Get-SelectedLinkItem
    if ($item) {
        $item.Category = $category
        $item.Title = $title
        $item.Target = $target
        $item.Notes = $notes
    }
    else {
        $script:ImportantLinks += [pscustomobject]@{
            Category = $category
            Title = $title
            Target = $target
            Notes = $notes
        }
    }

    Save-ImportantLinks
    Refresh-LinkList
}

function New-LinkItem {
    if (-not (Assert-AdminMode -Feature 'create important links')) {
        return
    }

    $script:lstImportantLinks.ClearSelected()
    $script:txtLinkCategory.Text = 'General'
    $script:txtLinkTitle.Text = ''
    $script:txtLinkTarget.Text = ''
    $script:txtLinkNotes.Text = ''
}

function Delete-LinkItem {
    if (-not (Assert-AdminMode -Feature 'delete important links')) {
        return
    }

    $index = $script:lstImportantLinks.SelectedIndex
    if ($index -lt 0) {
        return
    }

    $script:ImportantLinks = @(
        for ($i = 0; $i -lt $script:ImportantLinks.Count; $i++) {
            if ($i -ne $index) {
                $script:ImportantLinks[$i]
            }
        }
    )

    Save-ImportantLinks
    Refresh-LinkList
    New-LinkItem
}

function Open-LinkItem {
    $item = Get-SelectedLinkItem
    if (-not $item) {
        return
    }
    if ([string]::IsNullOrWhiteSpace($item.Target)) {
        Add-Log -Area 'Links' -Level 'WARN' -Message 'Selected link has no target to open.'
        return
    }

    Start-Process -FilePath $item.Target
    Add-Log -Area 'Links' -Level 'OK' -Message ("Opened link: {0}" -f $item.Title)
}

function Convert-TextToEncodedCommand {
    param([string]$Text)

    return [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($Text))
}

function Get-BmsCommandByName {
    param([string]$Name)

    return ($script:BmsCommands | Where-Object { $_.Name -eq $Name } | Select-Object -First 1)
}

function Get-SelectedBmsCommandCategory {
    if ((Get-Variable -Name cboBmsCommandCategoryFilter -Scope Script -ErrorAction SilentlyContinue) -and
        $script:cboBmsCommandCategoryFilter -and
        $script:cboBmsCommandCategoryFilter.SelectedItem) {
        return [string]$script:cboBmsCommandCategoryFilter.SelectedItem
    }

    return 'All Categories'
}

function Get-BmsCommandsForSelectedCategory {
    $category = Get-SelectedBmsCommandCategory
    if ([string]::IsNullOrWhiteSpace($category) -or $category -eq 'All Categories') {
        return @($script:BmsCommands | Sort-Object -Property Category, Name)
    }

    return @($script:BmsCommands | Where-Object { $_.Category -eq $category } | Sort-Object -Property Name)
}

function Refresh-BmsCommandCategoryFilter {
    if (-not ((Get-Variable -Name cboBmsCommandCategoryFilter -Scope Script -ErrorAction SilentlyContinue) -and $script:cboBmsCommandCategoryFilter)) {
        return
    }

    $previous = if ($script:cboBmsCommandCategoryFilter.SelectedItem) { [string]$script:cboBmsCommandCategoryFilter.SelectedItem } else { 'All Categories' }
    $script:cboBmsCommandCategoryFilter.Items.Clear()
    [void]$script:cboBmsCommandCategoryFilter.Items.Add('All Categories')

    foreach ($category in @($script:BmsCommands | Select-Object -ExpandProperty Category -Unique | Sort-Object)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$category)) {
            [void]$script:cboBmsCommandCategoryFilter.Items.Add([string]$category)
        }
    }

    if ($script:cboBmsCommandCategoryFilter.Items.Contains($previous)) {
        $script:cboBmsCommandCategoryFilter.SelectedItem = $previous
    }
    else {
        $script:cboBmsCommandCategoryFilter.SelectedIndex = 0
    }
}

function Refresh-BmsCommandList {
    if (-not ((Get-Variable -Name lstBmsCommandItems -Scope Script -ErrorAction SilentlyContinue) -and $script:lstBmsCommandItems)) {
        return
    }

    $previous = if ($script:lstBmsCommandItems.SelectedItem) { [string]$script:lstBmsCommandItems.SelectedItem } else { $script:CurrentBmsCommandName }
    $script:lstBmsCommandItems.Items.Clear()

    foreach ($item in @(Get-BmsCommandsForSelectedCategory)) {
        [void]$script:lstBmsCommandItems.Items.Add($item.Name)
    }

    if ($previous -and $script:lstBmsCommandItems.Items.Contains($previous)) {
        $script:lstBmsCommandItems.SelectedItem = $previous
    }
    elseif ($script:lstBmsCommandItems.Items.Count -gt 0) {
        $script:lstBmsCommandItems.SelectedIndex = 0
    }
    else {
        $script:CurrentBmsCommandName = $null
        if ((Get-Variable -Name txtBmsCommandDescriptionView -Scope Script -ErrorAction SilentlyContinue) -and $script:txtBmsCommandDescriptionView) {
            $script:txtBmsCommandDescriptionView.Text = ''
        }
        if ((Get-Variable -Name txtBmsCommandPreview -Scope Script -ErrorAction SilentlyContinue) -and $script:txtBmsCommandPreview) {
            $script:txtBmsCommandPreview.Text = ''
        }
    }
}

function Clear-BmsCommandEditor {
    if ((Get-Variable -Name txtBmsCommandName -Scope Script -ErrorAction SilentlyContinue) -and $script:txtBmsCommandName) {
        $script:txtBmsCommandName.Text = ''
    }
    if ((Get-Variable -Name txtBmsCommandCategory -Scope Script -ErrorAction SilentlyContinue) -and $script:txtBmsCommandCategory) {
        $script:txtBmsCommandCategory.Text = 'General'
    }
    if ((Get-Variable -Name txtBmsCommandDescription -Scope Script -ErrorAction SilentlyContinue) -and $script:txtBmsCommandDescription) {
        $script:txtBmsCommandDescription.Text = ''
    }
    if ((Get-Variable -Name cboBmsCommandType -Scope Script -ErrorAction SilentlyContinue) -and $script:cboBmsCommandType) {
        $script:cboBmsCommandType.SelectedItem = 'PowerShell'
    }
    if ((Get-Variable -Name txtBmsCommandText -Scope Script -ErrorAction SilentlyContinue) -and $script:txtBmsCommandText) {
        $script:txtBmsCommandText.Text = ''
    }
    if ((Get-Variable -Name chkBmsCommandRequiresAdmin -Scope Script -ErrorAction SilentlyContinue) -and $script:chkBmsCommandRequiresAdmin) {
        $script:chkBmsCommandRequiresAdmin.Checked = $false
    }
    if ((Get-Variable -Name chkBmsCommandConfirm -Scope Script -ErrorAction SilentlyContinue) -and $script:chkBmsCommandConfirm) {
        $script:chkBmsCommandConfirm.Checked = $true
    }
    if ((Get-Variable -Name chkBmsCommandKeepWindowOpen -Scope Script -ErrorAction SilentlyContinue) -and $script:chkBmsCommandKeepWindowOpen) {
        $script:chkBmsCommandKeepWindowOpen.Checked = $true
    }
}

function Select-BmsCommandByName {
    param([string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) {
        return
    }

    $command = Get-BmsCommandByName -Name $Name
    if (-not $command) {
        return
    }

    $script:CurrentBmsCommandName = $command.Name

    if ((Get-Variable -Name lstBmsCommandItems -Scope Script -ErrorAction SilentlyContinue) -and
        $script:lstBmsCommandItems -and
        $script:lstBmsCommandItems.Items.Contains($command.Name) -and
        $script:lstBmsCommandItems.SelectedItem -ne $command.Name) {
        $script:lstBmsCommandItems.SelectedItem = $command.Name
    }

    if ((Get-Variable -Name lblBmsCommandName -Scope Script -ErrorAction SilentlyContinue) -and $script:lblBmsCommandName) {
        $script:lblBmsCommandName.Text = ('Command: {0}' -f $command.Name)
    }
    if ((Get-Variable -Name lblBmsCommandMeta -Scope Script -ErrorAction SilentlyContinue) -and $script:lblBmsCommandMeta) {
        $script:lblBmsCommandMeta.Text = ('Category: {0} | Type: {1} | Admin: {2} | Confirm: {3}' -f $command.Category, $command.CommandType, $(if ($command.RequiresAdmin) { 'Yes' } else { 'No' }), $(if ($command.ConfirmBeforeRun) { 'Yes' } else { 'No' }))
    }
    if ((Get-Variable -Name txtBmsCommandDescriptionView -Scope Script -ErrorAction SilentlyContinue) -and $script:txtBmsCommandDescriptionView) {
        $script:txtBmsCommandDescriptionView.Text = $command.Description
    }
    if ((Get-Variable -Name txtBmsCommandPreview -Scope Script -ErrorAction SilentlyContinue) -and $script:txtBmsCommandPreview) {
        $script:txtBmsCommandPreview.Text = $command.CommandText
    }
    if ((Get-Variable -Name txtBmsCommandName -Scope Script -ErrorAction SilentlyContinue) -and $script:txtBmsCommandName) {
        $script:txtBmsCommandName.Text = $command.Name
    }
    if ((Get-Variable -Name txtBmsCommandCategory -Scope Script -ErrorAction SilentlyContinue) -and $script:txtBmsCommandCategory) {
        $script:txtBmsCommandCategory.Text = $command.Category
    }
    if ((Get-Variable -Name txtBmsCommandDescription -Scope Script -ErrorAction SilentlyContinue) -and $script:txtBmsCommandDescription) {
        $script:txtBmsCommandDescription.Text = $command.Description
    }
    if ((Get-Variable -Name cboBmsCommandType -Scope Script -ErrorAction SilentlyContinue) -and $script:cboBmsCommandType) {
        if ($script:cboBmsCommandType.Items.Contains($command.CommandType)) {
            $script:cboBmsCommandType.SelectedItem = $command.CommandType
        }
    }
    if ((Get-Variable -Name txtBmsCommandText -Scope Script -ErrorAction SilentlyContinue) -and $script:txtBmsCommandText) {
        $script:txtBmsCommandText.Text = $command.CommandText
    }
    if ((Get-Variable -Name chkBmsCommandRequiresAdmin -Scope Script -ErrorAction SilentlyContinue) -and $script:chkBmsCommandRequiresAdmin) {
        $script:chkBmsCommandRequiresAdmin.Checked = [bool]$command.RequiresAdmin
    }
    if ((Get-Variable -Name chkBmsCommandConfirm -Scope Script -ErrorAction SilentlyContinue) -and $script:chkBmsCommandConfirm) {
        $script:chkBmsCommandConfirm.Checked = [bool]$command.ConfirmBeforeRun
    }
    if ((Get-Variable -Name chkBmsCommandKeepWindowOpen -Scope Script -ErrorAction SilentlyContinue) -and $script:chkBmsCommandKeepWindowOpen) {
        $script:chkBmsCommandKeepWindowOpen.Checked = [bool]$command.KeepWindowOpen
    }
}

function Select-BmsCommandItem {
    if (-not ((Get-Variable -Name lstBmsCommandItems -Scope Script -ErrorAction SilentlyContinue) -and $script:lstBmsCommandItems)) {
        return
    }
    if ($script:lstBmsCommandItems.SelectedItem -eq $null) {
        return
    }

    Select-BmsCommandByName -Name ([string]$script:lstBmsCommandItems.SelectedItem)
}

function Refresh-BmsCommandButtons {
    if (-not ((Get-Variable -Name pnlBmsCommandButtons -Scope Script -ErrorAction SilentlyContinue) -and $script:pnlBmsCommandButtons)) {
        return
    }

    $script:pnlBmsCommandButtons.Controls.Clear()
    foreach ($item in @(Get-BmsCommandsForSelectedCategory)) {
        $button = New-Button -Text $item.Name -OnClick { Select-BmsCommandByName -Name ([string]$this.Tag) } -X 0 -Y 0 -Width 180 -Height 34
        $button.Tag = $item.Name
        $button.Margin = New-Object System.Windows.Forms.Padding(0, 0, 8, 8)
        $script:pnlBmsCommandButtons.Controls.Add($button)
    }
}

function Refresh-BmsCommandsUi {
    Refresh-BmsCommandCategoryFilter
    Refresh-BmsCommandList
    Refresh-BmsCommandButtons
}

function Invoke-BmsCommandItem {
    param($Command)

    if (-not $Command) {
        throw 'No BMS command is selected.'
    }

    $commandName = [string]$Command.Name
    $commandType = [string]$Command.CommandType
    $commandText = [string]$Command.CommandText

    if ([string]::IsNullOrWhiteSpace($commandText)) {
        throw ("Command '{0}' does not have any command text yet." -f $commandName)
    }

    if ($Command.ConfirmBeforeRun) {
        $message = "Run BMS command '$commandName'?" + [Environment]::NewLine +
            "Type: $commandType" + [Environment]::NewLine +
            "Admin required: $(if ($Command.RequiresAdmin) { 'Yes' } else { 'No' })"
        if (-not (Confirm-Action -Title 'Run BMS Command' -Message $message)) {
            Add-Log -Area 'BMS Commands' -Level 'WARN' -Message ("Cancelled command: {0}" -f $commandName)
            return
        }
    }

    switch ($commandType.ToLowerInvariant()) {
        'powershell' {
            $args = @('-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass')
            if ($Command.KeepWindowOpen) {
                $args += '-NoExit'
            }
            $args += @('-EncodedCommand', (Convert-TextToEncodedCommand -Text $commandText))

            if ($Command.RequiresAdmin -and -not (Test-IsAdministrator)) {
                Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $args
            }
            else {
                Start-Process -FilePath 'powershell.exe' -ArgumentList $args
            }
        }
        'cmd' {
            $mode = if ($Command.KeepWindowOpen) { '/k' } else { '/c' }
            $args = @($mode, $commandText)
            if ($Command.RequiresAdmin -and -not (Test-IsAdministrator)) {
                Start-Process -FilePath 'cmd.exe' -Verb RunAs -ArgumentList $args
            }
            else {
                Start-Process -FilePath 'cmd.exe' -ArgumentList $args
            }
        }
        'open' {
            if ($Command.RequiresAdmin -and -not (Test-IsAdministrator)) {
                Start-Process -FilePath $commandText -Verb RunAs
            }
            else {
                Start-Process -FilePath $commandText
            }
        }
        default {
            throw ("Unsupported command type: {0}" -f $commandType)
        }
    }

    Add-Log -Area 'BMS Commands' -Level 'OK' -Message ("Ran command: {0}" -f $commandName)
}

function Run-SelectedBmsCommand {
    Invoke-UiAction -Name 'Run BMS Command' -Action {
        $command = Get-BmsCommandByName -Name $script:CurrentBmsCommandName
        Invoke-BmsCommandItem -Command $command
    }
}

function New-BmsCommandItem {
    if (-not (Assert-AdminMode -Feature 'create BMS commands')) {
        return
    }

    $newName = 'New Command'
    $counter = 1
    while (@($script:BmsCommands | Where-Object { $_.Name -eq $newName }).Count -gt 0) {
        $counter++
        $newName = 'New Command {0}' -f $counter
    }

    $script:BmsCommands += [pscustomobject]@{
        Name = $newName
        Category = 'General'
        Description = ''
        CommandType = 'PowerShell'
        CommandText = ''
        RequiresAdmin = $false
        ConfirmBeforeRun = $true
        KeepWindowOpen = $true
    }

    Save-BmsCommands
    Refresh-BmsCommandsUi
    Select-BmsCommandByName -Name $newName
}

function Save-BmsCommandItem {
    if (-not (Assert-AdminMode -Feature 'create or edit BMS commands')) {
        return
    }

    $oldName = $script:CurrentBmsCommandName
    $name = $script:txtBmsCommandName.Text.Trim()
    $category = $script:txtBmsCommandCategory.Text.Trim()
    $description = $script:txtBmsCommandDescription.Text.Trim()
    $commandType = if ($script:cboBmsCommandType.SelectedItem) { [string]$script:cboBmsCommandType.SelectedItem } else { 'PowerShell' }
    $commandText = $script:txtBmsCommandText.Text.Trim()
    $requiresAdmin = [bool]$script:chkBmsCommandRequiresAdmin.Checked
    $confirmBeforeRun = [bool]$script:chkBmsCommandConfirm.Checked
    $keepWindowOpen = [bool]$script:chkBmsCommandKeepWindowOpen.Checked

    if ([string]::IsNullOrWhiteSpace($name)) {
        [System.Windows.Forms.MessageBox]::Show(
            'Command name is required.',
            'BMS Commands',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }
    if ([string]::IsNullOrWhiteSpace($category)) {
        $category = 'General'
    }
    if ([string]::IsNullOrWhiteSpace($commandText)) {
        [System.Windows.Forms.MessageBox]::Show(
            'Command text is required.',
            'BMS Commands',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }

    $duplicate = ($script:BmsCommands | Where-Object { $_.Name -eq $name -and $_.Name -ne $oldName } | Select-Object -First 1)
    if ($duplicate) {
        [System.Windows.Forms.MessageBox]::Show(
            'A command with that name already exists.',
            'BMS Commands',
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Warning
        ) | Out-Null
        return
    }

    $item = Get-BmsCommandByName -Name $oldName
    if (-not $item) {
        $item = [pscustomobject]@{
            Name = $name
            Category = $category
            Description = $description
            CommandType = $commandType
            CommandText = $commandText
            RequiresAdmin = $requiresAdmin
            ConfirmBeforeRun = $confirmBeforeRun
            KeepWindowOpen = $keepWindowOpen
        }
        $script:BmsCommands += $item
    }
    else {
        $item.Name = $name
        $item.Category = $category
        $item.Description = $description
        $item.CommandType = $commandType
        $item.CommandText = $commandText
        $item.RequiresAdmin = $requiresAdmin
        $item.ConfirmBeforeRun = $confirmBeforeRun
        $item.KeepWindowOpen = $keepWindowOpen
    }

    $script:CurrentBmsCommandName = $name
    Save-BmsCommands
    Refresh-BmsCommandsUi
    Select-BmsCommandByName -Name $name
}

function Delete-BmsCommandItem {
    if (-not (Assert-AdminMode -Feature 'delete BMS commands')) {
        return
    }

    $item = Get-BmsCommandByName -Name $script:CurrentBmsCommandName
    if (-not $item) {
        return
    }

    if (-not (Confirm-Action -Title 'Delete BMS Command' -Message ("Delete BMS command '{0}'?" -f $item.Name))) {
        return
    }

    $script:BmsCommands = @($script:BmsCommands | Where-Object { $_.Name -ne $item.Name })
    $script:CurrentBmsCommandName = $null
    Save-BmsCommands
    Clear-BmsCommandEditor
    Refresh-BmsCommandsUi
}

function Protect-ToolkitSecret {
    param([string]$PlainText)

    if ([string]::IsNullOrWhiteSpace($PlainText)) {
        return ''
    }

    $secure = ConvertTo-SecureString -String $PlainText -AsPlainText -Force
    return ($secure | ConvertFrom-SecureString)
}

function Unprotect-ToolkitSecret {
    param([string]$CipherText)

    if ([string]::IsNullOrWhiteSpace($CipherText)) {
        return ''
    }

    try {
        $secure = ConvertTo-SecureString -String $CipherText
        $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try {
            return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
        }
        finally {
            if ($ptr -ne [IntPtr]::Zero) {
                [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr)
            }
        }
    }
    catch {
        return ''
    }
}

function Get-OpenAiApiKey {
    return ''
}

function Update-AiSettingsUi {
    if ((Get-Variable -Name lblAiKeyStatus -Scope Script -ErrorAction SilentlyContinue) -and $script:lblAiKeyStatus) {
        $script:lblAiKeyStatus.Text = 'Ask the assistant for online troubleshooting, or search saved BMS flows, important links, and recent log activity.'
        $script:lblAiKeyStatus.ForeColor = if ((Get-Variable -Name ThemeColors -Scope Script -ErrorAction SilentlyContinue) -and $script:ThemeColors -and $script:ThemeColors.Muted) { $script:ThemeColors.Muted } else { [System.Drawing.Color]::FromArgb(80, 90, 105) }
    }

    if ((Get-Variable -Name lblAiAdminHint -Scope Script -ErrorAction SilentlyContinue) -and $script:lblAiAdminHint) {
        $script:lblAiAdminHint.Visible = $true
    }
}

function Save-AiSettings {
    Add-Log -Area 'AI Assistant' -Level 'INFO' -Message 'AI settings UI is disabled in this build.'
}

function Clear-StoredAiKey {
    if ($script:Config.PSObject.Properties['OpenAIApiKeyProtected']) {
        [void]$script:Config.PSObject.Properties.Remove('OpenAIApiKeyProtected')
    }
    Save-Config
    Update-AiSettingsUi
    Add-Log -Area 'AI Assistant' -Level 'WARN' -Message 'Removed the locally stored AI key value from the loaded config.'
}

function Export-ToolkitLog {
    Invoke-UiAction -Name 'Export Log' -Action {
        $dialog = New-Object System.Windows.Forms.SaveFileDialog
        $dialog.Title = 'Export TEC Systems Field Toolkit Log'
        $dialog.Filter = 'Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*'
        $dialog.FileName = ('TEC_FieldToolkit_Log_{0:yyyyMMdd_HHmmss}.log' -f (Get-Date))
        if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
            return
        }

        Copy-Item -Path $script:LogFile -Destination $dialog.FileName -Force
        Add-Log -Area 'Log' -Level 'OK' -Message ("Exported log to {0}" -f $dialog.FileName)
    }
}

function Check-ToolkitUpdates {
    param([switch]$Silent, $Release)

    try {
        $headers = @{ 'User-Agent' = 'TEC-Systems-Field-Toolkit' }
        $release = if ($Release) { $Release } else { Invoke-RestMethod -Uri 'https://api.github.com/repos/TEC-Systems/Technician-Field-Tool-Kit/releases/latest' -Headers $headers -TimeoutSec 8 -ErrorAction Stop }
        $latestText = [string]$release.tag_name -replace '^v', ''
        $latest = [version]$latestText
        $current = [version]$script:ToolkitVersion
        if ($latest -le $current) {
            if (-not $Silent) { [System.Windows.Forms.MessageBox]::Show('This toolkit is up to date.', 'Toolkit Updates') | Out-Null }
            return
        }
        $setup = @($release.assets | Where-Object { $_.name -eq 'TEC-Systems-FieldToolkit-Setup.exe' } | Select-Object -First 1)
        $sums = @($release.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' } | Select-Object -First 1)
        if (-not $setup -or -not $sums) { throw 'The newest release does not contain a verified installer yet.' }
        $message = "Version $latestText is available (installed: $($script:ToolkitVersion))."
        $notes = [string]$release.body
        if ($notes) {
            if ($notes.Length -gt 900) { $notes = $notes.Substring(0, 900) + '...' }
            $message += "`r`n`r`nWhat's new:`r`n$notes"
        }
        $message += "`r`n`r`nDownload and install it now?"
        if ([System.Windows.Forms.MessageBox]::Show($message, 'Toolkit Update Available', 'YesNo', 'Information') -ne 'Yes') { return }
        $folder = Join-Path -Path $env:TEMP -ChildPath ('TEC-FieldToolkit-Update-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        $setupPath = Join-Path -Path $folder -ChildPath 'TEC-Systems-FieldToolkit-Setup.exe'
        $sumsPath = Join-Path -Path $folder -ChildPath 'SHA256SUMS.txt'
        Invoke-WebRequest -Uri $setup.browser_download_url -Headers $headers -OutFile $setupPath -UseBasicParsing -TimeoutSec 120 -ErrorAction Stop
        Invoke-WebRequest -Uri $sums.browser_download_url -Headers $headers -OutFile $sumsPath -UseBasicParsing -TimeoutSec 30 -ErrorAction Stop
        $expected = @((Get-Content -LiteralPath $sumsPath) | Where-Object { $_ -match '^([a-fA-F0-9]{64})\s+TEC-Systems-FieldToolkit-Setup\.exe$' } | Select-Object -First 1)
        if (-not $expected) { throw 'The release checksum file does not list the installer.' }
        $expectedHash = ([regex]::Match($expected[0], '^[a-fA-F0-9]{64}')).Value
        $actualHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedHash) { throw 'Installer checksum mismatch. The update was not started.' }
        Add-Log -Area 'Updates' -Level 'OK' -Message ("Verified version {0} installer." -f $latestText)
        Start-Process -FilePath $setupPath | Out-Null
        Exit-Toolkit
    }
    catch {
        $message = $_.Exception.Message
        if ($message -match '404|Not Found') { $message = 'No published installer release is available yet.' }
        if (-not $Silent) {
            Add-Log -Area 'Updates' -Level 'WARN' -Message $message
            [System.Windows.Forms.MessageBox]::Show($message, 'Toolkit Updates') | Out-Null
        }
    }
}

function Start-ToolkitUpdateCheck {
    if ($script:updateJob) { return }
    $script:updateJob = Start-Job -ScriptBlock {
        try {
            [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
            Invoke-RestMethod -Uri 'https://api.github.com/repos/TEC-Systems/Technician-Field-Tool-Kit/releases/latest' -Headers @{ 'User-Agent' = 'TEC-Systems-Field-Toolkit' } -TimeoutSec 8 -ErrorAction Stop
        }
        catch { }
    }
    $script:updateTimer.Start()
}

function Complete-ToolkitUpdateCheck {
    if (-not $script:updateJob -or $script:updateJob.State -eq 'Running') { return }
    $script:updateTimer.Stop()
    try {
        $release = @(Receive-Job -Job $script:updateJob -ErrorAction SilentlyContinue | Select-Object -First 1)
        if ($release) { Check-ToolkitUpdates -Silent -Release $release[0] }
    }
    finally {
        Remove-Job -Job $script:updateJob -Force -ErrorAction SilentlyContinue
        $script:updateJob = $null
    }
}

function Add-ToolkitZipText {
    param($Archive, [string]$Name, [string]$Content)

    $entry = $Archive.CreateEntry($Name)
    $stream = $entry.Open()
    try {
        $writer = New-Object System.IO.StreamWriter($stream, (New-Object System.Text.UTF8Encoding($false)))
        try { $writer.Write($Content) } finally { $writer.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Export-ToolkitSupportBundle {
    Invoke-UiAction -Name 'Export Support Bundle' -Action {
        $kind = [string]$script:cboBundleType.SelectedItem
        if (-not (Confirm-Action -Title 'Export Diagnostic Data' -Message 'This package may contain computer names, IP addresses, usernames, and Windows event details. Review it before sharing. Continue?')) { return }
        $dialog = New-Object System.Windows.Forms.SaveFileDialog
        $dialog.Title = 'Save technician support bundle'
        $dialog.Filter = 'ZIP package (*.zip)|*.zip'
        $dialog.FileName = ('TEC_FieldToolkit_{0}_{1:yyyyMMdd_HHmmss}.zip' -f $kind, (Get-Date))
        if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return }

        Add-Type -AssemblyName System.IO.Compression
        $file = [System.IO.File]::Open($dialog.FileName, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
        try {
            $archive = New-Object System.IO.Compression.ZipArchive($file, [System.IO.Compression.ZipArchiveMode]::Create, $true)
            try {
                Add-ToolkitZipText -Archive $archive -Name 'README.txt' -Content ("TEC Systems Field Toolkit support bundle`r`nComputer: {0}`r`nCreated: {1:u}`r`nType: {2}`r`nReview contents before sharing.`r`n" -f $env:COMPUTERNAME, (Get-Date), $kind)
                Add-ToolkitZipText -Archive $archive -Name 'toolkit.log' -Content ([System.IO.File]::ReadAllText($script:LogFile))
                if ($kind -in @('Network', 'Both')) {
                    foreach ($command in @(
                        @{ Name = 'ipconfig-all.txt'; File = 'ipconfig.exe'; Args = @('/all') },
                        @{ Name = 'route-print.txt'; File = 'route.exe'; Args = @('print') },
                        @{ Name = 'arp-cache.txt'; File = 'arp.exe'; Args = @('-a') },
                        @{ Name = 'netsh-ipv4.txt'; File = 'netsh.exe'; Args = @('interface', 'ipv4', 'show', 'config') }
                    )) {
                        try {
                            $commandArgs = $command.Args
                            $lines = & $command.File @commandArgs 2>&1 | Out-String
                            Add-ToolkitZipText -Archive $archive -Name $command.Name -Content $lines
                        }
                        catch { Add-ToolkitZipText -Archive $archive -Name $command.Name -Content $_.Exception.Message }
                    }
                }
                if ($kind -in @('Windows', 'Both')) {
                    foreach ($eventLog in @('System', 'Application')) {
                        try {
                            $events = Get-WinEvent -LogName $eventLog -MaxEvents 150 -ErrorAction Stop |
                                Select-Object TimeCreated, Id, LevelDisplayName, ProviderName, Message |
                                ConvertTo-Csv -NoTypeInformation
                            Add-ToolkitZipText -Archive $archive -Name ("events-{0}.csv" -f $eventLog.ToLowerInvariant()) -Content ($events -join "`r`n")
                        }
                        catch { Add-ToolkitZipText -Archive $archive -Name ("events-{0}.txt" -f $eventLog.ToLowerInvariant()) -Content $_.Exception.Message }
                    }
                    try {
                        $system = Get-WmiObject Win32_OperatingSystem -ErrorAction Stop | Format-List Caption, Version, BuildNumber, LastBootUpTime | Out-String
                        Add-ToolkitZipText -Archive $archive -Name 'windows-summary.txt' -Content $system
                    }
                    catch { Add-ToolkitZipText -Archive $archive -Name 'windows-summary.txt' -Content $_.Exception.Message }
                }
            }
            finally { $archive.Dispose() }
        }
        finally { $file.Dispose() }
        Add-Log -Area 'Support Bundle' -Level 'OK' -Message ("Exported {0} package: {1}" -f $kind, $dialog.FileName)
    }
}

function Clear-VisibleLog {
    if (-not (Confirm-Action -Title 'Clear Visible Log' -Message 'Clear the on-screen technician log view? The saved log file will remain on disk.')) {
        return
    }

    $script:lvLog.Items.Clear()
    Add-Log -Area 'Log' -Level 'INFO' -Message 'Visible log cleared.'
}

function Refresh-ThemePalette {
    param([bool]$DarkMode)

    if ($DarkMode) {
        $script:ThemeColors = @{
            IsDark = $true
            Background = [System.Drawing.Color]::FromArgb(8, 14, 26)
            Panel = [System.Drawing.Color]::FromArgb(15, 24, 42)
            Surface = [System.Drawing.Color]::FromArgb(23, 35, 61)
            SurfaceAlt = [System.Drawing.Color]::FromArgb(19, 30, 52)
            Header = [System.Drawing.Color]::FromArgb(12, 22, 40)
            Footer = [System.Drawing.Color]::FromArgb(10, 18, 33)
            ReadOnly = [System.Drawing.Color]::FromArgb(12, 20, 36)
            Text = [System.Drawing.Color]::FromArgb(237, 243, 255)
            Muted = [System.Drawing.Color]::FromArgb(151, 166, 194)
            Accent = [System.Drawing.Color]::FromArgb(79, 152, 255)
            AccentHover = [System.Drawing.Color]::FromArgb(57, 134, 244)
            AccentPressed = [System.Drawing.Color]::FromArgb(43, 114, 216)
            Success = [System.Drawing.Color]::FromArgb(31, 154, 116)
            SuccessHover = [System.Drawing.Color]::FromArgb(39, 174, 132)
            SuccessPressed = [System.Drawing.Color]::FromArgb(24, 132, 98)
            Warning = [System.Drawing.Color]::FromArgb(212, 132, 52)
            WarningHover = [System.Drawing.Color]::FromArgb(227, 149, 69)
            WarningPressed = [System.Drawing.Color]::FromArgb(186, 112, 36)
            Secondary = [System.Drawing.Color]::FromArgb(55, 71, 102)
            SecondaryHover = [System.Drawing.Color]::FromArgb(69, 88, 122)
            SecondaryPressed = [System.Drawing.Color]::FromArgb(46, 60, 86)
            Danger = [System.Drawing.Color]::FromArgb(154, 78, 96)
            DangerHover = [System.Drawing.Color]::FromArgb(176, 92, 112)
            DangerPressed = [System.Drawing.Color]::FromArgb(132, 64, 82)
            Border = [System.Drawing.Color]::FromArgb(44, 60, 88)
        }
    }
    else {
        $script:ThemeColors = @{
            IsDark = $false
            Background = [System.Drawing.Color]::FromArgb(241, 245, 249)
            Panel = [System.Drawing.Color]::White
            Surface = [System.Drawing.Color]::White
            SurfaceAlt = [System.Drawing.Color]::White
            Header = [System.Drawing.Color]::White
            Footer = [System.Drawing.Color]::FromArgb(241, 245, 249)
            Text = [System.Drawing.Color]::FromArgb(33, 43, 54)
            Muted = [System.Drawing.Color]::FromArgb(92, 105, 120)
            Accent = [System.Drawing.Color]::FromArgb(17, 64, 255)
            AccentHover = [System.Drawing.Color]::FromArgb(12, 47, 190)
            AccentPressed = [System.Drawing.Color]::FromArgb(8, 36, 150)
            Success = [System.Drawing.Color]::FromArgb(45, 130, 80)
            SuccessHover = [System.Drawing.Color]::FromArgb(35, 102, 62)
            SuccessPressed = [System.Drawing.Color]::FromArgb(26, 78, 48)
            Warning = [System.Drawing.Color]::FromArgb(185, 95, 35)
            WarningHover = [System.Drawing.Color]::FromArgb(150, 70, 25)
            WarningPressed = [System.Drawing.Color]::FromArgb(125, 56, 18)
            Secondary = [System.Drawing.Color]::FromArgb(80, 100, 125)
            SecondaryHover = [System.Drawing.Color]::FromArgb(60, 78, 98)
            SecondaryPressed = [System.Drawing.Color]::FromArgb(50, 67, 83)
            Danger = [System.Drawing.Color]::FromArgb(95, 105, 120)
            DangerHover = [System.Drawing.Color]::FromArgb(70, 80, 95)
            DangerPressed = [System.Drawing.Color]::FromArgb(58, 67, 80)
            ReadOnly = [System.Drawing.Color]::White
            Border = [System.Drawing.Color]::FromArgb(210, 218, 229)
        }
    }
}

function Get-ButtonThemeRole {
    param(
        [string]$Text,
        [System.Drawing.Color]$BackColor
    )

    $label = ([string]$Text).ToLowerInvariant()
    if ($label -match 'delete|clear|remove') {
        return 'Danger'
    }

    switch ($BackColor.ToArgb()) {
        ([System.Drawing.Color]::FromArgb(45, 130, 80).ToArgb()) { return 'Success' }
        ([System.Drawing.Color]::FromArgb(185, 95, 35).ToArgb()) { return 'Warning' }
        ([System.Drawing.Color]::FromArgb(80, 100, 125).ToArgb()) { return 'Secondary' }
        ([System.Drawing.Color]::FromArgb(95, 105, 120).ToArgb()) { return 'Danger' }
        default { return 'Accent' }
    }
}

function Get-ButtonThemeStyle {
    param(
        [string]$Role,
        $Button
    )

    $theme = $script:ThemeColors
    $isDark = [bool]$theme.IsDark

    if (-not $isDark) {
        $baseBackColor = if ($Button.PSObject.Properties['BaseBackColor']) { [System.Drawing.Color]$Button.BaseBackColor } else { $theme.Accent }
        $baseHoverColor = if ($Button.PSObject.Properties['BaseHoverColor']) { [System.Drawing.Color]$Button.BaseHoverColor } else { $theme.AccentHover }
        return [pscustomobject]@{
            BackColor = $baseBackColor
            HoverColor = $baseHoverColor
            PressedColor = $baseHoverColor
            ForeColor = [System.Drawing.Color]::White
            BorderColor = $baseBackColor
            BorderSize = 0
        }
    }

    switch ($Role) {
        'Success' {
            return [pscustomobject]@{
                BackColor = $theme.Success
                HoverColor = $theme.SuccessHover
                PressedColor = $theme.SuccessPressed
                ForeColor = [System.Drawing.Color]::White
                BorderColor = $theme.Border
                BorderSize = 1
            }
        }
        'Warning' {
            return [pscustomobject]@{
                BackColor = $theme.Warning
                HoverColor = $theme.WarningHover
                PressedColor = $theme.WarningPressed
                ForeColor = [System.Drawing.Color]::White
                BorderColor = $theme.Border
                BorderSize = 1
            }
        }
        'Secondary' {
            return [pscustomobject]@{
                BackColor = $theme.Secondary
                HoverColor = $theme.SecondaryHover
                PressedColor = $theme.SecondaryPressed
                ForeColor = [System.Drawing.Color]::White
                BorderColor = $theme.Border
                BorderSize = 1
            }
        }
        'Danger' {
            return [pscustomobject]@{
                BackColor = $theme.Danger
                HoverColor = $theme.DangerHover
                PressedColor = $theme.DangerPressed
                ForeColor = [System.Drawing.Color]::White
                BorderColor = $theme.Border
                BorderSize = 1
            }
        }
        default {
            return [pscustomobject]@{
                BackColor = $theme.Accent
                HoverColor = $theme.AccentHover
                PressedColor = $theme.AccentPressed
                ForeColor = [System.Drawing.Color]::White
                BorderColor = $theme.Border
                BorderSize = 1
            }
        }
    }
}

function Apply-ThemeToButton {
    param($Button)

    if (-not $Button) {
        return
    }

    $role = if ($Button.PSObject.Properties['ThemeRole']) { [string]$Button.ThemeRole } else { 'Accent' }
    $style = Get-ButtonThemeStyle -Role $role -Button $Button

    if ($Button.PSObject.Properties['CurrentBackColor']) {
        $Button.CurrentBackColor = $style.BackColor
    }
    else {
        $Button | Add-Member -NotePropertyName CurrentBackColor -NotePropertyValue $style.BackColor -Force
    }

    if ($Button.PSObject.Properties['CurrentHoverColor']) {
        $Button.CurrentHoverColor = $style.HoverColor
    }
    else {
        $Button | Add-Member -NotePropertyName CurrentHoverColor -NotePropertyValue $style.HoverColor -Force
    }

    $Button.BackColor = $style.BackColor
    $Button.ForeColor = $style.ForeColor
    $Button.FlatAppearance.BorderSize = $style.BorderSize
    $Button.FlatAppearance.BorderColor = $style.BorderColor
    $Button.FlatAppearance.MouseOverBackColor = $style.HoverColor
    $Button.FlatAppearance.MouseDownBackColor = $style.PressedColor
}

function Apply-ThemeToControl {
    param($Control)

    if (-not $Control) {
        return
    }

    $theme = $script:ThemeColors
    switch ($Control.GetType().FullName) {
        'System.Windows.Forms.Form' {
            $Control.BackColor = $theme.Background
            $Control.ForeColor = $theme.Text
        }
        'System.Windows.Forms.TabPage' {
            $Control.BackColor = $theme.Panel
            $Control.ForeColor = $theme.Text
        }
        'System.Windows.Forms.Panel' {
            $Control.BackColor = $theme.Panel
            $Control.ForeColor = $theme.Text
        }
        'System.Windows.Forms.SplitterPanel' {
            $Control.BackColor = $theme.Panel
            $Control.ForeColor = $theme.Text
        }
        'System.Windows.Forms.GroupBox' {
            $Control.BackColor = $theme.Panel
            $Control.ForeColor = $theme.Text
        }
        'System.Windows.Forms.FlowLayoutPanel' {
            $Control.BackColor = $theme.Panel
            $Control.ForeColor = $theme.Text
        }
        'System.Windows.Forms.Button' {
            Apply-ThemeToButton -Button $Control
        }
        'System.Windows.Forms.Label' {
            $tone = if ($Control.PSObject.Properties['ThemeTone']) { [string]$Control.ThemeTone } else { 'Normal' }
            $Control.ForeColor = if ($tone -eq 'Muted') { $theme.Muted } else { $theme.Text }
            if ($Control.Parent) {
                $Control.BackColor = $Control.Parent.BackColor
            }
        }
        'System.Windows.Forms.CheckBox' {
            $Control.ForeColor = $theme.Text
            if ($Control.Parent) {
                $Control.BackColor = $Control.Parent.BackColor
            }
        }
        'System.Windows.Forms.TextBox' {
            $Control.ForeColor = $theme.Text
            $Control.BackColor = if ($Control.ReadOnly) { $theme.ReadOnly } else { $theme.Surface }
            $Control.BorderStyle = 'FixedSingle'
        }
        'System.Windows.Forms.ListBox' {
            $Control.BackColor = $theme.Surface
            $Control.ForeColor = $theme.Text
            $Control.BorderStyle = 'FixedSingle'
        }
        'System.Windows.Forms.TreeView' {
            $Control.BackColor = $theme.Surface
            $Control.ForeColor = $theme.Text
            $Control.LineColor = $theme.Muted
        }
        'System.Windows.Forms.ListView' {
            $Control.BackColor = $theme.Surface
            $Control.ForeColor = $theme.Text
        }
        'System.Windows.Forms.ComboBox' {
            $Control.BackColor = $theme.Surface
            $Control.ForeColor = $theme.Text
            $Control.FlatStyle = 'Flat'
        }
        'System.Windows.Forms.TabControl' {
            $Control.BackColor = $theme.Panel
            $Control.ForeColor = $theme.Text
        }
        'System.Windows.Forms.SplitContainer' {
            $Control.BackColor = $theme.Background
        }
        default {
            if ($Control -is [System.Windows.Forms.TextBoxBase]) {
                $Control.ForeColor = $theme.Text
                $Control.BackColor = if ($Control.ReadOnly) { $theme.ReadOnly } else { $theme.Surface }
            }
        }
    }

    foreach ($child in $Control.Controls) {
        Apply-ThemeToControl -Control $child
    }
}

function Apply-Theme {
    param([bool]$DarkMode)

    Refresh-ThemePalette -DarkMode $DarkMode
    $script:Config.DarkMode = $DarkMode

    if ((Get-Variable -Name form -Scope Script -ErrorAction SilentlyContinue) -and $script:form) {
        Apply-ThemeToControl -Control $script:form
    }

    if ((Get-Variable -Name header -Scope Script -ErrorAction SilentlyContinue) -and $script:header) {
        $script:header.BackColor = $script:ThemeColors.Header
    }
    if ((Get-Variable -Name footer -Scope Script -ErrorAction SilentlyContinue) -and $script:footer) {
        $script:footer.BackColor = $script:ThemeColors.Footer
    }
    if ((Get-Variable -Name leftPanel -Scope Script -ErrorAction SilentlyContinue) -and $script:leftPanel) {
        $script:leftPanel.BackColor = $script:ThemeColors.Panel
    }
    if ((Get-Variable -Name rightPanel -Scope Script -ErrorAction SilentlyContinue) -and $script:rightPanel) {
        $script:rightPanel.BackColor = $script:ThemeColors.SurfaceAlt
    }
    if ((Get-Variable -Name pnlLogActions -Scope Script -ErrorAction SilentlyContinue) -and $script:pnlLogActions) {
        $script:pnlLogActions.BackColor = $script:ThemeColors.SurfaceAlt
    }

    if ((Get-Variable -Name lblTitle -Scope Script -ErrorAction SilentlyContinue) -and $script:lblTitle) {
        $script:lblTitle.ForeColor = $script:ThemeColors.Text
    }
    if ((Get-Variable -Name lblManaged -Scope Script -ErrorAction SilentlyContinue) -and $script:lblManaged) {
        $script:lblManaged.ForeColor = $script:ThemeColors.Muted
    }
    if ((Get-Variable -Name lblHost -Scope Script -ErrorAction SilentlyContinue) -and $script:lblHost) {
        $script:lblHost.ForeColor = $script:ThemeColors.Text
    }
    if ((Get-Variable -Name lblHeaderSearch -Scope Script -ErrorAction SilentlyContinue) -and $script:lblHeaderSearch) {
        $script:lblHeaderSearch.ForeColor = $script:ThemeColors.Muted
    }
    if ((Get-Variable -Name lblLogTitle -Scope Script -ErrorAction SilentlyContinue) -and $script:lblLogTitle) {
        $script:lblLogTitle.ForeColor = $script:ThemeColors.Text
    }
    if ((Get-Variable -Name btnThemeToggle -Scope Script -ErrorAction SilentlyContinue) -and $script:btnThemeToggle) {
        $script:btnThemeToggle.Text = if ($DarkMode) { 'Light Mode' } else { 'Dark Mode' }
    }

    Save-Config
}

function Toggle-DarkMode {
    Apply-Theme -DarkMode (-not [bool]$script:Config.DarkMode)
}

function Build-OpenAiInputText {
    param([string]$Prompt)

    $context = Get-BmsAiContextText
    $recentLog = Get-RecentToolkitLogText

    return @"
You are TEC Systems Field Toolkit AI mode for field technicians.

Instructions:
- Answer the technician's question directly.
- Use web search to find current information when needed.
- Be practical, concise, and action-oriented.
- Prefer numbered troubleshooting steps.
- If there is uncertainty, say what to verify next.
- End with a short section called "Likely next move".
- Include a short "Sources" section with links or domains consulted when useful.

Toolkit context:
$context

Recent toolkit log:
$recentLog

Technician question:
$Prompt
"@
}

function Get-WebExceptionResponseText {
    param($Exception)

    try {
        if (-not $Exception.Response) {
            return ''
        }

        $stream = $Exception.Response.GetResponseStream()
        if (-not $stream) {
            return ''
        }

        $reader = New-Object System.IO.StreamReader($stream)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
            $stream.Dispose()
        }
    }
    catch {
        return ''
    }
}

function Get-WebExceptionStatusCode {
    param($Exception)

    try {
        if ($Exception.Response -and $Exception.Response.StatusCode) {
            return [int]$Exception.Response.StatusCode
        }
    }
    catch {
    }

    return $null
}

function Get-WebExceptionRetryDelaySeconds {
    param($Exception)

    try {
        if ($Exception.Response -and $Exception.Response.Headers) {
            $retryAfter = $Exception.Response.Headers['Retry-After']
            if ($retryAfter -and $retryAfter -match '^\d+$') {
                return [int]$retryAfter
            }
        }
    }
    catch {
    }

    return $null
}

function Get-ResponseOutputText {
    param($Response)

    if ($Response.PSObject.Properties['output_text'] -and $Response.output_text) {
        return [string]$Response.output_text
    }

    $parts = @()
    if ($Response.PSObject.Properties['output']) {
        foreach ($item in @($Response.output)) {
            if ($item.PSObject.Properties['content']) {
                foreach ($content in @($item.content)) {
                    if ($content.PSObject.Properties['text'] -and $content.text) {
                        $parts += [string]$content.text
                    }
                }
            }
        }
    }

    return ($parts -join [Environment]::NewLine)
}

function Invoke-OpenAiWebSearch {
    param([string]$Prompt)

    $apiKey = Get-OpenAiApiKey
    if ([string]::IsNullOrWhiteSpace($apiKey)) {
        throw 'Online AI mode is not active in this build.'
    }
    if (-not (Test-InternetConnection)) {
        throw 'AI mode needs an internet connection for live web search.'
    }

    $model = if ([string]::IsNullOrWhiteSpace($script:Config.OpenAIModel)) { 'gpt-5' } else { $script:Config.OpenAIModel }
    $body = @{
        model = $model
        reasoning = @{ effort = 'low' }
        tools = @(
            @{
                type = 'web_search'
                user_location = @{
                    type = 'approximate'
                    country = 'US'
                    region = 'New York'
                    timezone = 'America/New_York'
                }
            }
        )
        tool_choice = 'auto'
        input = (Build-OpenAiInputText -Prompt $Prompt)
    }

    $json = $body | ConvertTo-Json -Depth 10
    $headers = @{
        Authorization = ('Bearer {0}' -f $apiKey)
        'Content-Type' = 'application/json'
    }

    $attempt = 0
    $maxAttempts = 3
    $fallbackDelays = @(2, 5, 10)

    while ($attempt -lt $maxAttempts) {
        $attempt++
        try {
            return Invoke-RestMethod -Method Post -Uri 'https://api.openai.com/v1/responses' -Headers $headers -Body $json -TimeoutSec 90 -ErrorAction Stop
        }
        catch {
            $statusCode = Get-WebExceptionStatusCode -Exception $_.Exception
            $rawResponse = Get-WebExceptionResponseText -Exception $_.Exception
            $friendlyMessage = $_.Exception.Message

            if ($rawResponse) {
                try {
                    $errorJson = $rawResponse | ConvertFrom-Json
                    if ($errorJson.error.message) {
                        $friendlyMessage = $errorJson.error.message
                    }
                }
                catch {
                    $friendlyMessage = $rawResponse
                }
            }

            if ($statusCode -eq 429 -and $attempt -lt $maxAttempts) {
                $delay = Get-WebExceptionRetryDelaySeconds -Exception $_.Exception
                if (-not $delay) {
                    $delay = $fallbackDelays[$attempt - 1]
                }

                Add-Log -Area 'AI Assistant' -Level 'WARN' -Message ("OpenAI rate limit hit. Waiting {0} seconds before retry {1} of {2}." -f $delay, ($attempt + 1), $maxAttempts)
                Start-Sleep -Seconds $delay
                continue
            }

            if ($statusCode -eq 429) {
                throw 'OpenAI API rate limit reached for this account or project (HTTP 429). Wait a minute and try again, or check your API usage/billing limits.'
            }
            elseif ($statusCode -eq 401) {
                throw 'OpenAI API rejected the configured credentials (HTTP 401).'
            }
            elseif ($statusCode -eq 403) {
                throw 'OpenAI API access was denied (HTTP 403). Check the connected service permissions and model access.'
            }
            elseif ($friendlyMessage -match 'model') {
                throw ("OpenAI API error: {0} The configured model may not be available to the connected service." -f $friendlyMessage)
            }
            else {
                throw ("OpenAI API error: {0}" -f $friendlyMessage)
            }
        }
    }
}

function Add-AiConversationMessage {
    param(
        [string]$Role,
        [string]$Text
    )

    if (-not ((Get-Variable -Name txtAiConversation -Scope Script -ErrorAction SilentlyContinue) -and $script:txtAiConversation)) {
        return
    }

    $stamp = Get-Date -Format 'HH:mm'
    $entry = '{0} {1}{2}{3}{2}{2}' -f $stamp, $Role, [Environment]::NewLine, $Text
    $script:txtAiConversation.AppendText($entry)
}

function Invoke-ToolkitKnowledgeSearch {
    param(
        [string]$Prompt,
        [switch]$FocusFirstBmsMatch
    )

    $query = $Prompt.Trim()
    if ([string]::IsNullOrWhiteSpace($query)) {
        return 'Enter a keyword, hostname, issue, or phrase to search the toolkit knowledge.'
    }

    $queryLower = $query.ToLowerInvariant()
    $lines = @()
    $firstBmsMatch = $null

    $lines += ('Search query: {0}' -f $query)
    $lines += ''

    $bmsMatches = @()
    foreach ($category in @($script:BmsFlows)) {
        if ($category.Name.ToLowerInvariant().Contains($queryLower)) {
            $bmsMatches += ('Topic: {0}' -f $category.Name)
            if (-not $firstBmsMatch -and $category.StartStepId) {
                $firstBmsMatch = [pscustomobject]@{ Category = $category.Name; StepId = $category.StartStepId }
            }
        }

        foreach ($step in @($category.Steps)) {
            $stepMatch = $false
            if (($step.Name -and $step.Name.ToLowerInvariant().Contains($queryLower)) -or
                ($step.Prompt -and $step.Prompt.ToLowerInvariant().Contains($queryLower))) {
                $stepMatch = $true
            }

            foreach ($choice in @($step.Buttons)) {
                if (($choice.Text -and $choice.Text.ToLowerInvariant().Contains($queryLower)) -or
                    ($choice.Notes -and $choice.Notes.ToLowerInvariant().Contains($queryLower))) {
                    $stepMatch = $true
                }
            }

            if ($stepMatch) {
                $bmsMatches += ('Topic: {0} | Step: {1}' -f $category.Name, $step.Name)
                if (-not $firstBmsMatch) {
                    $firstBmsMatch = [pscustomobject]@{ Category = $category.Name; StepId = $step.Id }
                }
            }
        }
    }

    if (@($bmsMatches).Count -gt 0) {
        $lines += 'BMS matches'
        $lines += ($bmsMatches | Select-Object -Unique | ForEach-Object { '- ' + $_ })
        $lines += ''
    }

    $linkMatches = @()
    foreach ($link in @($script:ImportantLinks)) {
        if (($link.Category -and $link.Category.ToLowerInvariant().Contains($queryLower)) -or
            ($link.Title -and $link.Title.ToLowerInvariant().Contains($queryLower)) -or
            ($link.Target -and $link.Target.ToLowerInvariant().Contains($queryLower)) -or
            ($link.Notes -and $link.Notes.ToLowerInvariant().Contains($queryLower))) {
            $linkMatches += ('{0} [{1}] - {2}' -f $link.Title, $link.Category, $link.Target)
        }
    }

    if (@($linkMatches).Count -gt 0) {
        $lines += 'Important links'
        $lines += ($linkMatches | Select-Object -Unique | ForEach-Object { '- ' + $_ })
        $lines += ''
    }

    $logMatches = @()
    if ((Get-Variable -Name lvLog -Scope Script -ErrorAction SilentlyContinue) -and $script:lvLog) {
        foreach ($item in @($script:lvLog.Items)) {
            $line = ('[{0}] [{1}] [{2}] {3}' -f $item.Text, $item.SubItems[1].Text, $item.SubItems[2].Text, $item.SubItems[3].Text)
            if ($line.ToLowerInvariant().Contains($queryLower)) {
                $logMatches += $line
            }
        }
    }

    if (@($logMatches).Count -gt 0) {
        $lines += 'Recent log matches'
        $lines += ($logMatches | Select-Object -Last 6 | ForEach-Object { '- ' + $_ })
        $lines += ''
    }

    $bulletLines = @($lines | Where-Object { $_ -match '^-' })
    if (@($bulletLines).Count -eq 0) {
        $lines += 'No direct matches were found in saved flows, important links, or the current log.'
        $lines += 'Try a shorter keyword, server name, site name, or issue phrase.'
    }
    else {
        $lines += 'Likely next move'
        if ($firstBmsMatch) {
            $lines += ('- Open BMS topic "{0}" and continue from the matched step.' -f $firstBmsMatch.Category)
        }
        elseif (@($linkMatches).Count -gt 0) {
            $lines += '- Open the matching link or document and confirm the latest site standard.'
        }
        else {
            $lines += '- Review the matching log entries, then continue troubleshooting from the closest matching step.'
        }
    }

    if ($FocusFirstBmsMatch -and $firstBmsMatch -and $script:lstBmsTopics) {
        $script:tabsMain.SelectedTab = $script:tabBms
        $script:lstBmsTopics.SelectedItem = $firstBmsMatch.Category
        Select-BmsStepFromOutline -StepId $firstBmsMatch.StepId
    }

    return ($lines -join [Environment]::NewLine)
}

function Open-AiGoogleSearch {
    param([string]$Query)

    Invoke-UiAction -Name 'Open Web Search' -Action {
        $searchText = $Query
        if ([string]::IsNullOrWhiteSpace($searchText) -and
            (Get-Variable -Name txtAiPrompt -Scope Script -ErrorAction SilentlyContinue) -and
            $script:txtAiPrompt) {
            $searchText = $script:txtAiPrompt.Text.Trim()
        }

        if ([string]::IsNullOrWhiteSpace($searchText)) {
            Add-Log -Area 'Open Web Search' -Level 'WARN' -Message 'Enter a question or keyword before opening web search.'
            Set-MainStatus -Text 'Enter a search term first' -Color ([System.Drawing.Color]::FromArgb(190, 120, 45))
            Schedule-StatusReset
            return
        }
        if (-not (Test-InternetConnection)) {
            Add-Log -Area 'Open Web Search' -Level 'WARN' -Message 'Web search needs an internet connection.'
            Set-MainStatus -Text 'Internet connection needed' -Color ([System.Drawing.Color]::FromArgb(190, 120, 45))
            Schedule-StatusReset
            return
        }

        $encoded = [System.Uri]::EscapeDataString($searchText)
        $url = 'https://www.google.com/search?q={0}' -f $encoded
        Start-Process -FilePath $url
        Add-Log -Area 'AI Assistant' -Level 'OK' -Message ("Opened web search for: {0}" -f $searchText)
    }
}

function Get-BmsAiContextText {
    $category = Get-SelectedBmsCategory
    $step = Get-SelectedBmsStep
    $lines = @()
    $lines += ('Hostname: {0}' -f $env:COMPUTERNAME)
    $lines += ('Internet: {0}' -f $(if ($script:lblInternet) { $script:lblInternet.Text } else { 'Unknown' }))

    if ($category) {
        $lines += ('BMS Category: {0}' -f $category.Name)
    }
    if ($step) {
        $lines += ('Current Step: {0}' -f $step.Name)
        $lines += ('Prompt: {0}' -f $step.Prompt)
    }

    if ((Get-Variable -Name cboAdapter -Scope Script -ErrorAction SilentlyContinue) -and $script:cboAdapter -and $script:cboAdapter.SelectedItem) {
        $lines += ('Adapter: {0}' -f [string]$script:cboAdapter.SelectedItem)
    }

    return ($lines -join [Environment]::NewLine)
}

function Get-RecentToolkitLogText {
    $lines = @()
    if ((Get-Variable -Name lvLog -Scope Script -ErrorAction SilentlyContinue) -and $script:lvLog -and $script:lvLog.Items.Count -gt 0) {
        $startIndex = [Math]::Max(0, $script:lvLog.Items.Count - 8)
        for ($i = $startIndex; $i -lt $script:lvLog.Items.Count; $i++) {
            $item = $script:lvLog.Items[$i]
            $lines += ('[{0}] [{1}] [{2}] {3}' -f $item.Text, $item.SubItems[1].Text, $item.SubItems[2].Text, $item.SubItems[3].Text)
        }
    }

    return ($lines -join [Environment]::NewLine)
}

function Get-OnlineTroubleshootingUrl {
    if (-not [string]::IsNullOrWhiteSpace($env:TEC_FIELD_TOOLKIT_BACKEND_URL)) {
        return $env:TEC_FIELD_TOOLKIT_BACKEND_URL.Trim()
    }
    if ($script:Config -and $script:Config.PSObject.Properties['OnlineTroubleshootingUrl'] -and
        -not [string]::IsNullOrWhiteSpace([string]$script:Config.OnlineTroubleshootingUrl)) {
        return ([string]$script:Config.OnlineTroubleshootingUrl).Trim()
    }

    return 'http://127.0.0.1:8787/troubleshoot'
}

function Get-OnlineTroubleshootingHealthUrl {
    $backendUrl = Get-OnlineTroubleshootingUrl
    if ([string]::IsNullOrWhiteSpace($backendUrl)) {
        return ''
    }

    $trimmed = $backendUrl.Trim()
    if ($trimmed -match '/troubleshoot/?$') {
        return ($trimmed -replace '/troubleshoot/?$', '/health')
    }

    return ($trimmed.TrimEnd('/') + '/health')
}

function Refresh-OnlineTroubleshootingStatus {
    if (-not ((Get-Variable -Name lblAiAdminHint -Scope Script -ErrorAction SilentlyContinue) -and $script:lblAiAdminHint)) {
        return
    }

    $script:lblAiAdminHint.Text = 'Service: Checking online troubleshooting backend...'
    $script:lblAiAdminHint.ForeColor = if ($script:ThemeColors -and $script:ThemeColors.Muted) { $script:ThemeColors.Muted } else { $colorMuted }
    [System.Windows.Forms.Application]::DoEvents()

    try {
        $healthUrl = Get-OnlineTroubleshootingHealthUrl
        if ([string]::IsNullOrWhiteSpace($healthUrl)) {
            throw 'No backend health URL is configured.'
        }

        $health = Invoke-RestMethod -Method Get -Uri $healthUrl -TimeoutSec 2 -ErrorAction Stop
        if ($health -and $health.ok) {
            $modelText = if ($health.PSObject.Properties['model'] -and $health.model) { [string]$health.model } else { 'Unknown model' }
            $script:lblAiAdminHint.Text = ('Service: Online | Model: {0}' -f $modelText)
            $script:lblAiAdminHint.ForeColor = [System.Drawing.Color]::FromArgb(45, 130, 80)
            return
        }

        throw 'Health check returned an unexpected response.'
    }
    catch {
        $script:lblAiAdminHint.Text = 'Service: Offline | local toolkit guidance is still available'
        $script:lblAiAdminHint.ForeColor = [System.Drawing.Color]::FromArgb(190, 120, 45)
    }
}

function Invoke-OnlineTroubleshootingBackend {
    param([string]$Prompt)

    $backendUrl = Get-OnlineTroubleshootingUrl
    if ([string]::IsNullOrWhiteSpace($backendUrl)) {
        throw 'No online troubleshooting service URL is configured.'
    }

    $payload = [ordered]@{
        issue = $Prompt
        hostname = $env:COMPUTERNAME
        internet_status = if ($script:lblInternet) { $script:lblInternet.Text } else { 'Unknown' }
        bms_context = Get-BmsAiContextText
        recent_log = Get-RecentToolkitLogText
        adapter = if ((Get-Variable -Name cboAdapter -Scope Script -ErrorAction SilentlyContinue) -and $script:cboAdapter -and $script:cboAdapter.SelectedItem) { [string]$script:cboAdapter.SelectedItem } else { '' }
    }

    $json = $payload | ConvertTo-Json -Depth 6
    return Invoke-RestMethod -Method Post -Uri $backendUrl -ContentType 'application/json' -Body $json -TimeoutSec 90 -ErrorAction Stop
}

function Format-OnlineTroubleshootingResponse {
    param($Response)

    if (-not $Response) {
        return 'The online troubleshooting service returned an empty response.'
    }

    $lines = @()
    if ($Response.answer) {
        $lines += [string]$Response.answer
    }
    elseif ($Response.output_text) {
        $lines += [string]$Response.output_text
    }

    $sources = @()
    if ($Response.sources) {
        foreach ($source in @($Response.sources)) {
            if ($source -is [string]) {
                $sources += $source
            }
            elseif ($source.PSObject.Properties['url']) {
                $title = if ($source.PSObject.Properties['title'] -and $source.title) { [string]$source.title } else { [string]$source.url }
                $sources += ('{0} - {1}' -f $title, [string]$source.url)
            }
        }
    }

    $sources = @($sources | Select-Object -Unique)
    if ($sources.Count -gt 0) {
        $lines += ''
        $lines += 'Sources'
        $lines += ($sources | ForEach-Object { '- ' + $_ })
    }

    return ($lines -join [Environment]::NewLine)
}

function Build-AiCopilotResponse {
    param([string]$Prompt)

    $promptLower = $Prompt.ToLowerInvariant()
    $response = @()
    $response += 'Here is a practical next-step view based on the toolkit context.'
    $response += ''

    if ($promptLower -match 'ping|icmp|reachable|not ping|server down') {
        $target = ''
        if ((Get-Variable -Name txtPingTarget -Scope Script -ErrorAction SilentlyContinue) -and $script:txtPingTarget) {
            $target = $script:txtPingTarget.Text.Trim()
        }

        $response += 'Focus: Server not pingable'
        $response += '1. Confirm the technician laptop is on the expected site adapter/profile.'
        $response += '2. Open Network Settings and verify the active adapter, static IP, subnet mask, and gateway.'
        $response += '3. Ping the local gateway first. If gateway fails, stay on the laptop/site network problem.'
        $response += '4. If gateway works but the server fails, check whether the server is on the same subnet or routed path.'
        $response += '5. Try DNS Lookup only if the target is a hostname. If name resolution fails, test with raw IP.'
        $response += '6. If the server still does not answer, try RDP/Web access to confirm whether only ICMP is blocked.'
        if ($target) {
            $response += ('Current ping target in toolkit: {0}' -f $target)
        }
    }
    elseif ($promptLower -match 'dns|name resolv|lookup|hostname') {
        $response += 'Focus: DNS / hostname issue'
        $response += '1. Confirm the target is spelled correctly and reachable from the correct site profile.'
        $response += '2. Compare ping by hostname versus ping by IP.'
        $response += '3. Review DNS 1 / DNS 2 on the selected adapter.'
        $response += '4. If hostname fails but IP works, document the failing name and current DNS servers.'
        $response += '5. Use Important Links for site-specific naming standards or server sheets.'
    }
    elseif ($promptLower -match 'ip|adapter|network|subnet|gateway|dhcp') {
        $response += 'Focus: Adapter / IP profile'
        $response += '1. Confirm the correct adapter is selected, even if the cable is disconnected.'
        $response += '2. Review assigned IP, subnet mask, gateway, and DNS values before changing anything.'
        $response += '3. Load the saved site profile if one exists, then apply static IP from the admin launcher.'
        $response += '4. After change, re-open adapter details and confirm Windows accepted the values.'
        $response += '5. If communication still fails, compare the working site profile against the target device network.'
    }
    elseif ($promptLower -match 'rdp|remote desktop|mstsc') {
        $response += 'Focus: Remote Desktop'
        $response += '1. Confirm the target is reachable by IP or hostname first.'
        $response += '2. If ping works but RDP fails, test whether the server is powered on and Remote Desktop is enabled.'
        $response += '3. Open Remote Desktop settings on the laptop or target documentation to confirm the expected server.'
        $response += '4. Check that firewall policy and user permissions allow RDP access.'
        $response += '5. If the site uses jump hosts or VPN routing, confirm that path before assuming the server is down.'
    }
    elseif ($promptLower -match 'printer|print|spooler') {
        $response += 'Focus: Printing'
        $response += '1. Check whether the issue is one printer, one user, or the whole site.'
        $response += '2. Open Devices and Printers and confirm the expected printer is present and online.'
        $response += '3. Review the Print Spooler service and restart it only if the queue is stuck and you understand the impact.'
        $response += '4. Confirm network reachability to the printer IP if it is a network printer.'
        $response += '5. Document the printer model, IP, and error state before escalating.'
    }
    elseif ($promptLower -match 'user|account|login|credential|password') {
        $response += 'Focus: User / account access'
        $response += '1. Confirm whether the problem is local Windows access, BMS access, RDP access, or a website login.'
        $response += '2. Open Local Users, Credential Manager, or the relevant BMS documentation depending on the access type.'
        $response += '3. Verify the username spelling, group membership, and whether the account is disabled or locked.'
        $response += '4. For BMS users, follow the documented site flow and capture exactly where the add-user process fails.'
        $response += '5. Record what was tested so the next technician does not repeat the same access steps.'
    }
    elseif ($promptLower -match 'service|services|stopped|start') {
        $response += 'Focus: Windows services'
        $response += '1. Confirm which service matters to the actual symptom before changing anything.'
        $response += '2. Use Service Check for quick health, then open Services for the detailed startup type and dependency view.'
        $response += '3. Review Event Viewer around the same time for service start failures or dependency errors.'
        $response += '4. If you restart a service, capture the before and after state in the technician log.'
        $response += '5. If the service stops again immediately, move to logs and dependencies rather than repeated restarts.'
    }
    elseif ($promptLower -match 'update|patch|windows update|kb') {
        $response += 'Focus: Windows Update'
        $response += '1. Check Update Status to confirm service state and recent hotfix history.'
        $response += '2. Open Windows Update and confirm whether the device is actively pending, failed, or fully updated.'
        $response += '3. If update services are stopped, note which ones are affected before changing them.'
        $response += '4. Capture the KB number or error code if a specific update is failing.'
        $response += '5. Review free disk space and recent event errors before assuming the issue is only update-related.'
    }
    elseif ($promptLower -match 'bms|bacnet|ebi|point|lonworks|alarm|station') {
        $response += 'Focus: BMS troubleshooting'
        $response += '1. Start with the structured BMS flow on the Troubleshoot tab.'
        $response += '2. Capture the exact symptom: not pingable, web not loading, point server unavailable, alarms not sending, or user access issue.'
        $response += '3. Confirm laptop network profile first, then verify server reachability, then application-specific checks.'
        $response += '4. If the issue does not fit the current flow, use Admin Mode later to add the missing branch so the next technician benefits.'
        $response += '5. Record the failed step and current server or network state in the technician log before escalation.'
    }
    elseif ($promptLower -match 'log|summary|what happened') {
        $response += 'Focus: Current toolkit activity'
        $logText = Get-RecentToolkitLogText
        if ([string]::IsNullOrWhiteSpace($logText)) {
            $response += 'There are no recent toolkit log entries yet.'
        }
        else {
            $response += $logText
        }
    }
    else {
        $response += 'Suggested support path'
        $response += '1. Define the symptom in one sentence.'
        $response += '2. Confirm the correct network/IP profile.'
        $response += '3. Test reachability: gateway, target IP, target hostname, web, then RDP if relevant.'
        $response += '4. Use the BMS flow for the structured branch and the technician log to capture evidence.'
        $response += '5. If needed, convert the final resolution into a reusable BMS flow or Important Link entry.'
    }

    $response += ''
    $response += 'Current context'
    $response += Get-BmsAiContextText
    return ($response -join [Environment]::NewLine)
}

function Ask-AiCopilot {
    Invoke-UiAction -Name 'AI Assistant' -Action {
        $prompt = $script:txtAiPrompt.Text.Trim()
        if ([string]::IsNullOrWhiteSpace($prompt)) {
            Add-Log -Area 'AI Assistant' -Level 'WARN' -Message 'Enter a troubleshooting question before asking the assistant.'
            Set-MainStatus -Text 'Enter a troubleshooting question' -Color ([System.Drawing.Color]::FromArgb(190, 120, 45))
            Schedule-StatusReset
            return
        }

        Add-AiConversationMessage -Role 'Technician' -Text $prompt

        try {
            $onlineResponse = Invoke-OnlineTroubleshootingBackend -Prompt $prompt
            if ($onlineResponse.PSObject.Properties['ok'] -and -not [bool]$onlineResponse.ok) {
                throw ([string]$onlineResponse.error)
            }

            $reply = Format-OnlineTroubleshootingResponse -Response $onlineResponse
            if ([string]::IsNullOrWhiteSpace($reply)) {
                throw 'The online troubleshooting service returned an empty answer.'
            }

            Add-AiConversationMessage -Role 'AI Assistant' -Text $reply
            Add-Log -Area 'AI Assistant' -Level 'OK' -Message 'Returned online troubleshooting guidance from backend service.'
            Refresh-OnlineTroubleshootingStatus
        }
        catch {
            $guidance = Build-AiCopilotResponse -Prompt $prompt
            $knowledge = Invoke-ToolkitKnowledgeSearch -Prompt $prompt
            $reply = @(
                'Online troubleshooting is currently unavailable, so the toolkit switched to local guidance.'
                ''
                $guidance
                ''
                'Saved toolkit information'
                $knowledge
            ) -join [Environment]::NewLine
            Add-AiConversationMessage -Role 'AI Assistant' -Text $reply
            Add-Log -Area 'AI Assistant' -Level 'WARN' -Message ('Online troubleshooting fallback used: {0}' -f $_.Exception.Message)
            Refresh-OnlineTroubleshootingStatus
        }

        $script:txtAiPrompt.Clear()
    }
}

function Ask-AiQuick {
    param([string]$Prompt)

    if (-not ((Get-Variable -Name txtAiPrompt -Scope Script -ErrorAction SilentlyContinue) -and $script:txtAiPrompt)) {
        return
    }

    $script:txtAiPrompt.Text = $Prompt
    Ask-AiCopilot
}

# -------------------------------
# UI Helpers
# -------------------------------
$fontMain = New-Object System.Drawing.Font('Segoe UI', 9)
$fontHeader = New-Object System.Drawing.Font('Segoe UI Semibold', 16)
$fontSection = New-Object System.Drawing.Font('Segoe UI Semibold', 10)
$fontButton = New-Object System.Drawing.Font('Segoe UI Semibold', 9)
$colorBackground = [System.Drawing.Color]::FromArgb(241, 245, 249)
$colorPanel = [System.Drawing.Color]::White
$colorPrimary = [System.Drawing.Color]::FromArgb(17, 64, 255)
$colorPrimaryDark = [System.Drawing.Color]::FromArgb(12, 47, 190)
$colorText = [System.Drawing.Color]::FromArgb(33, 43, 54)
$colorMuted = [System.Drawing.Color]::FromArgb(92, 105, 120)

function New-Button {
    param(
        [string]$Text,
        [scriptblock]$OnClick,
        [int]$X,
        [int]$Y,
        [int]$Width = 130,
        [int]$Height = 34,
        [System.Drawing.Color]$BackColor = $colorPrimary,
        [System.Drawing.Color]$HoverColor = $colorPrimaryDark
    )

    $button = New-Object System.Windows.Forms.Button
    $button.Text = $Text
    $button.Location = New-Object System.Drawing.Point($X, $Y)
    $button.Size = New-Object System.Drawing.Size($Width, $Height)
    $button.FlatStyle = 'Flat'
    $button.FlatAppearance.BorderSize = 0
    $button.BackColor = $BackColor
    $button.ForeColor = [System.Drawing.Color]::White
    $button.Font = $fontButton
    $button.Cursor = [System.Windows.Forms.Cursors]::Hand
    $button | Add-Member -NotePropertyName ThemeRole -NotePropertyValue (Get-ButtonThemeRole -Text $Text -BackColor $BackColor) -Force
    $button | Add-Member -NotePropertyName BaseBackColor -NotePropertyValue $BackColor -Force
    $button | Add-Member -NotePropertyName BaseHoverColor -NotePropertyValue $HoverColor -Force
    $button | Add-Member -NotePropertyName CurrentBackColor -NotePropertyValue $BackColor -Force
    $button | Add-Member -NotePropertyName CurrentHoverColor -NotePropertyValue $HoverColor -Force
    $button.Add_Click($OnClick)
    $button.Add_MouseEnter({
        if ($this.PSObject.Properties['CurrentHoverColor']) {
            $this.BackColor = [System.Drawing.Color]$this.CurrentHoverColor
        }
    })
    $button.Add_MouseLeave({
        if ($this.PSObject.Properties['CurrentBackColor']) {
            $this.BackColor = [System.Drawing.Color]$this.CurrentBackColor
        }
    })

    if ((Get-Variable -Name ThemeColors -Scope Script -ErrorAction SilentlyContinue) -and $script:ThemeColors) {
        Apply-ThemeToButton -Button $button
    }

    return $button
}

function New-Label {
    param([string]$Text, [int]$X, [int]$Y, [int]$Width = 120)

    $label = New-Object System.Windows.Forms.Label
    $label.Text = $Text
    $label.Location = New-Object System.Drawing.Point($X, $Y)
    $label.Size = New-Object System.Drawing.Size($Width, 22)
    $label.ForeColor = $colorMuted
    $label | Add-Member -NotePropertyName ThemeTone -NotePropertyValue 'Muted' -Force
    return $label
}

function Get-AllChildControls {
    param($Root)

    $all = @()
    if (-not $Root) {
        return $all
    }

    foreach ($child in @($Root.Controls)) {
        $all += $child
        $all += Get-AllChildControls -Root $child
    }

    return $all
}

function Apply-ToolTipsByButtonText {
    param(
        $Root,
        [hashtable]$Descriptions
    )

    if (-not $Root -or -not $Descriptions -or -not $script:toolTip) {
        return
    }

    foreach ($control in @(Get-AllChildControls -Root $Root)) {
        if ($control -is [System.Windows.Forms.Button]) {
            $label = [string]$control.Text
            if ($Descriptions.ContainsKey($label)) {
                $script:toolTip.SetToolTip($control, [string]$Descriptions[$label])
            }
        }
    }
}

function Apply-TroubleshootingButtonToolTips {
    if (-not $script:toolTip) {
        return
    }

    $common = @{
        'DNS Lookup' = 'Run a DNS name lookup to confirm whether a hostname resolves and which DNS server is answering.'
        'ARP Cache' = 'Show the ARP table so you can see recent IP-to-MAC mappings on the local network.'
        'Route Table' = 'Display local routing entries to confirm the system knows where traffic is supposed to go.'
        'DNS Cache' = 'Review the local resolver cache to see what Windows recently resolved and stored.'
        'Netstat' = 'List active connections and listening ports so you can confirm services, sessions, and listeners.'
        'Firewall' = 'Summarize firewall state and profiles to check whether Windows filtering may be blocking traffic.'
        'Update Status' = 'Check Windows Update and related services for common update or patching issues.'
        'Adapter Details' = 'Show NIC status, configured IP, mask, gateway, DNS, MAC address, and DHCP state.'
        'Open Webpage' = 'Open the current target in a browser to verify HTTP or HTTPS access quickly.'
    }

    $windowsDescriptions = @{
        'Ping Terminal' = 'Open a live terminal ping against the current target. Use this when you want to watch replies and packet loss in real time.'
        'System Summary' = 'Collect quick host details like model, OS, BIOS, uptime, and core system information.'
        'Network Summary' = 'Log a compact network overview, including active adapters, IP information, and reachability context.'
        'IPConfig /all' = 'Run the full Windows IP configuration output to inspect adapters, DHCP, DNS, suffixes, and leases.'
        'Disk Summary' = 'Review drive usage and basic storage health so you can rule out disk-related issues.'
        'Service Check' = 'Check important Windows services to confirm whether key services are stopped, missing, or unhealthy.'
        'Event Errors' = 'Pull recent Windows event log errors to spot system or application problems quickly.'
        'Problem Devices' = 'List hardware devices that Windows reports as having driver or Device Manager issues.'
        'Flush DNS' = 'Clear the local DNS resolver cache before testing name resolution again.'
        'Network Settings' = 'Open Windows Network Connections (`ncpa.cpl`) so you can inspect or change adapter settings.'
        'Open RDP' = 'Launch Remote Desktop using the current RDP target.'
        'Services' = 'Open the Services console to inspect service state, startup type, dependencies, and recovery options.'
        'Event Viewer' = 'Open Event Viewer for a deeper look at system, application, and custom log errors.'
        'Device Manager' = 'Open Device Manager to inspect adapters, drivers, hidden devices, and hardware problem codes.'
        'Computer Mgmt' = 'Open Computer Management for Event Viewer, Shared Folders, Services, and Device Manager in one place.'
        'Local Users' = 'Open Local Users and Groups so you can review or manage local accounts and group membership.'
        'Programs' = 'Open Programs and Features to review installed software or remove problem applications.'
        'System Props' = 'Open classic system properties for computer name, domain, hardware, performance, and remote settings.'
        'Task Manager' = 'Open Task Manager for process, performance, startup, and user-session troubleshooting.'
        'Command Prompt' = 'Open a CMD console for quick command-line diagnostics and vendor utilities.'
        'PowerShell' = 'Open a PowerShell console for script-based diagnostics and administrative checks.'
        'Task Scheduler' = 'Open Task Scheduler to review scheduled jobs, triggers, failures, and service account context.'
        'Firewall Console' = 'Open the advanced firewall console for rule-level inspection, profiles, and logging.'
        'Credential Mgr' = 'Open Credential Manager to inspect saved Windows, web, or RDP credentials.'
        'Shared Folders' = 'Open the Shared Folders console to review active shares, sessions, and open files.'
        'Windows Update' = 'Open Windows Update settings to review pending updates, failures, and restart requirements.'
        'Remote Desktop' = 'Open Remote Desktop system settings to confirm whether this laptop allows inbound RDP.'
        'Printers' = 'Open Devices and Printers to inspect printers, drivers, default devices, and print queues.'
    }

    $networkDescriptions = @{
        'Ping Device' = 'Ping the entered device or host to confirm basic Layer 3 reachability.'
        'Ping Gateway' = 'Ping the default gateway to verify the local subnet and upstream network path.'
        'Tracert' = 'Trace the path to the destination hop by hop to find where traffic stops or changes path.'
        'PathPing' = 'Combine route tracing and packet-loss testing for a deeper path-quality check.'
        'Ping Switch' = 'Ping the switch management IP to confirm management-plane connectivity.'
        'Switch Ports' = 'Test common management ports like HTTP, HTTPS, SSH, and SNMP to see what the switch is exposing.'
        'Open Web UI' = 'Open the switch management page in a browser using the selected HTTP or HTTPS scheme.'
        'SSH Terminal' = 'Launch an SSH session to the switch if OpenSSH is available on the laptop.'
        'Baseline Snapshot' = 'Capture a quick network baseline so you have a before-state for troubleshooting notes.'
        'Link Status' = 'Log adapter media state and connection details to confirm whether the laptop actually has link.'
        'Neighbors' = 'Show neighbor information to help identify devices discovered on the local network.'
        'Network Summary' = 'Log a compact overview of current adapters, IP addresses, gateways, and DNS.'
        'IPConfig /all' = 'Show full Windows network configuration, including DHCP leases and DNS suffixes.'
        'Flush DNS' = 'Clear the Windows DNS resolver cache before another name-resolution test.'
        'Network Settings' = 'Open Network Connections to inspect or change adapter configuration.'
        'Inspect Selected Adapter' = 'Show local NIC VLAN settings; the actual switch-port VLAN still needs switch-side confirmation.'
        'Export ZIP' = 'Package selected network or Windows diagnostics and toolkit logs for review before sharing.'
        'Managed Switch' = 'Load a playbook for switch models that expose VLANs, trunks, management IPs, and configurable ports.'
        'Unmanaged Switch' = 'Load a playbook focused on simple switches where link, cable, power, and upstream path are the main checks.'
        'MS/TP and IP' = 'Load a field checklist for mixed MS/TP and IP troubleshooting, including controllers, trunks, addressing, and gateways.'
        'No Link Light' = 'Walk through a fast physical-layer checklist when there is no carrier or no visible link.'
        'Wrong VLAN / IP' = 'Use a guided check for subnet mismatches, VLAN assignment problems, or wrong addressing.'
        'Intermittent' = 'Load a playbook for issues that come and go, including packet loss, spanning tree, power, and upstream instability.'
    }

    foreach ($key in $common.Keys) {
        $windowsDescriptions[$key] = $common[$key]
        $networkDescriptions[$key] = $common[$key]
    }

    if ((Get-Variable -Name tabTrouble -Scope Script -ErrorAction SilentlyContinue) -and $script:tabTrouble) {
        Apply-ToolTipsByButtonText -Root $script:tabTrouble -Descriptions $windowsDescriptions
    }
    if ((Get-Variable -Name tabNetwork -Scope Script -ErrorAction SilentlyContinue) -and $script:tabNetwork) {
        Apply-ToolTipsByButtonText -Root $script:tabNetwork -Descriptions $networkDescriptions
    }
    if ((Get-Variable -Name tabIp -Scope Script -ErrorAction SilentlyContinue) -and $script:tabIp) {
        Apply-ToolTipsByButtonText -Root $script:tabIp -Descriptions @{
            'Refresh' = 'Refresh all network adapters, including disconnected interfaces and stored IPv4 configurations.'
            'Details' = 'Write the selected adapter status, IP configuration, MAC, and DHCP state to the technician log.'
            'Load Profile' = 'Fill the IP fields and select the adapter saved in the chosen profile.'
            'Save Profile' = 'Save or update this named adapter, IP, gateway, and DNS profile for later use.'
            'Delete Profile' = 'Delete the selected saved profile without changing Windows adapter settings.'
            'Apply Static IP' = 'Confirm and apply the entered static IPv4 settings. Windows administrator rights are required.'
            'Set DHCP' = 'Confirm and return the selected adapter to automatic IPv4 and DNS settings.'
        }
    }
    if ((Get-Variable -Name tabScan -Scope Script -ErrorAction SilentlyContinue) -and $script:tabScan) {
        Apply-ToolTipsByButtonText -Root $script:tabScan -Descriptions @{
            'Scan' = 'Scan the selected IPv4 range using ping and common TCP service probes. Limited to 1,024 addresses.'
            'Stop' = 'Stop the current scan without changing the network.'
            'Export CSV' = 'Save found IPs, hostnames, ping times, MAC addresses where visible, and open common ports.'
        }
    }
}

function New-TextBox {
    param([int]$X, [int]$Y, [int]$Width = 180, [string]$Text = '')

    $textBox = New-Object System.Windows.Forms.TextBox
    $textBox.Location = New-Object System.Drawing.Point($X, $Y)
    $textBox.Size = New-Object System.Drawing.Size($Width, 24)
    $textBox.Text = $Text
    $textBox.BorderStyle = 'FixedSingle'
    return $textBox
}

# -------------------------------
function ConvertTo-ScannerIpNumber {
    param([string]$Address)
    if ($Address -notmatch '^\d{1,3}(\.\d{1,3}){3}$') { throw 'Enter a full dotted IPv4 address.' }
    Test-IPv4AddressText -Value $Address -FieldName 'Scan address'
    $bytes = [System.Net.IPAddress]::Parse($Address).GetAddressBytes()
    return ([uint64]$bytes[0] * 16777216 + [uint64]$bytes[1] * 65536 + [uint64]$bytes[2] * 256 + [uint64]$bytes[3])
}

function ConvertFrom-ScannerIpNumber {
    param([uint64]$Number)
    return ('{0}.{1}.{2}.{3}' -f (($Number -shr 24) -band 255), (($Number -shr 16) -band 255), (($Number -shr 8) -band 255), ($Number -band 255))
}

function Select-ScannerAdapter {
    if ($script:cboScanAdapter.SelectedIndex -lt 0) { return }
    $adapter = $script:AdapterList[$script:cboScanAdapter.SelectedIndex]
    if ($adapter.Status -ne 'Connected' -or $adapter.IPText -eq 'No IP') {
        $script:lblScanScope.Text = 'This adapter is not connected. Select a connected adapter or enter a range manually.'
        return
    }
    try {
        $ip = ($adapter.IPText -split ',')[0].Trim()
        $mask = ($adapter.MaskText -split ',')[0].Trim()
        $ipNumber = ConvertTo-ScannerIpNumber $ip
        $maskNumber = ConvertTo-ScannerIpNumber $mask
        $network = $ipNumber -band $maskNumber
        $broadcast = $network -bor ([uint64]4294967295 -bxor $maskNumber)
        $first = $network + 1
        $last = $broadcast - 1
        if ($last -lt $first) { $first = $ipNumber; $last = $ipNumber }
        if (($last - $first + 1) -gt 254) {
            $first = [Math]::Max($first, $ipNumber - 126)
            $last = [Math]::Min($last, $first + 253)
            $script:lblScanScope.Text = ('{0}: large subnet; showing 254 addresses near this laptop. Edit the range to scan another slice.' -f $adapter.Name)
        }
        else { $script:lblScanScope.Text = ('{0}: connected | {1} / {2}' -f $adapter.Name, $ip, $mask) }
        $script:txtScanStart.Text = ConvertFrom-ScannerIpNumber $first
        $script:txtScanEnd.Text = ConvertFrom-ScannerIpNumber $last
    }
    catch {
        $script:lblScanScope.Text = 'Could not determine a range from this adapter. Enter a start and end IP manually.'
        Add-Log -Area 'IP Scanner' -Level 'WARN' -Message $_.Exception.Message
    }
}

function Refresh-ScannerAdapters {
    if (-not $script:cboScanAdapter) { return }
    $previous = $script:cboScanAdapter.SelectedIndex
    $script:cboScanAdapter.Items.Clear()
    foreach ($adapter in $script:AdapterList) {
        [void]$script:cboScanAdapter.Items.Add(('{0} [{1}] - {2}' -f $adapter.Name, $adapter.Status, $adapter.IPText))
    }
    if ($script:cboScanAdapter.Items.Count -gt 0) {
        if ($previous -ge 0 -and $previous -lt $script:cboScanAdapter.Items.Count) { $script:cboScanAdapter.SelectedIndex = $previous }
        else {
            $connected = @($script:AdapterList | Where-Object { $_.Status -eq 'Connected' -and $_.IPText -match '^\d+\.' } | Select-Object -First 1)
            $script:cboScanAdapter.SelectedIndex = if ($connected.Count -gt 0) { [array]::IndexOf($script:AdapterList, $connected[0]) } else { 0 }
        }
    }
}

function Start-LanScan {
    Invoke-UiAction -Name 'IP Scanner' -Action {
        $first = ConvertTo-ScannerIpNumber $script:txtScanStart.Text.Trim()
        $last = ConvertTo-ScannerIpNumber $script:txtScanEnd.Text.Trim()
        if ($last -lt $first) { throw 'The end address must be after the start address.' }
        $count = [int64]($last - $first + 1)
        if ($count -gt 1024) { throw 'Enter no more than 1,024 IP addresses per scan.' }
        if ($script:scanJob -and $script:scanJob.State -eq 'Running') { throw 'A scan is already running.' }
        $script:lvScan.Items.Clear()
        $script:scanProcessed = 0
        $script:scanTotal = $count
        $script:scanJob = Start-Job -ArgumentList $first, $last -ScriptBlock {
            param([uint64]$rangeStart, [uint64]$rangeEnd)
            $ports = @(80, 443, 445, 3389, 22)
            for ($batch = $rangeStart; $batch -le $rangeEnd; $batch += 16) {
                $hosts = @()
                for ($number = $batch; $number -le [Math]::Min($batch + 15, $rangeEnd); $number++) {
                    $ip = '{0}.{1}.{2}.{3}' -f (($number -shr 24) -band 255), (($number -shr 16) -band 255), (($number -shr 8) -band 255), ($number -band 255)
                    $ping = New-Object System.Net.NetworkInformation.Ping
                    $clients = @()
                    foreach ($port in $ports) {
                        $client = New-Object System.Net.Sockets.TcpClient
                        try { $clients += [pscustomobject]@{ Port = $port; Client = $client; Task = $client.BeginConnect($ip, $port, $null, $null) } }
                        catch { $client.Dispose() }
                    }
                    $hosts += [pscustomobject]@{ IP = $ip; Ping = $ping; PingTask = $ping.SendPingAsync($ip, 450); Clients = $clients }
                }
                foreach ($hostItem in $hosts) {
                    $reply = ''
                    $openPorts = @()
                    try {
                        if ($hostItem.PingTask.Wait(500) -and $hostItem.PingTask.Result.Status -eq 'Success') {
                            $reply = ('{0} ms' -f $hostItem.PingTask.Result.RoundtripTime)
                        }
                    }
                    catch { }
                    finally { $hostItem.Ping.Dispose() }
                    foreach ($probe in $hostItem.Clients) {
                        try {
                            if ($probe.Task.AsyncWaitHandle.WaitOne(250)) {
                                $probe.Client.EndConnect($probe.Task)
                                if ($probe.Client.Connected) { $openPorts += $probe.Port }
                            }
                        }
                        catch { }
                        finally { $probe.Client.Close() }
                    }
                    if ($reply -or $openPorts.Count -gt 0) {
                        $name = ''
                        $mac = ''
                        try {
                            $dnsTask = [System.Net.Dns]::GetHostEntryAsync($hostItem.IP)
                            if ($dnsTask.Wait(350)) { $name = $dnsTask.Result.HostName }
                        }
                        catch { }
                        try {
                            $arpLines = @(& arp.exe -a $hostItem.IP 2>$null)
                            foreach ($line in $arpLines) {
                                if ($line -match ('^\s*' + [regex]::Escape($hostItem.IP) + '\s+([0-9a-fA-F-]{17})\s+')) { $mac = $matches[1]; break }
                            }
                        }
                        catch { }
                        [pscustomobject]@{ Kind = 'Host'; IP = $hostItem.IP; HostName = $name; Ping = $reply; Mac = $mac; Ports = ($openPorts -join ', ') }
                    }
                }
                [pscustomobject]@{ Kind = 'Progress'; Scanned = [int]([Math]::Min($batch + 15, $rangeEnd) - $rangeStart + 1) }
            }
        }
        $script:lblScanStatus.Text = 'Scanning...'
        $script:btnScan.Enabled = $false
        $script:scanTimer.Start()
        Add-Log -Area 'IP Scanner' -Level 'INFO' -Message ('Scanning {0}-{1} ({2} addresses).' -f $script:txtScanStart.Text, $script:txtScanEnd.Text, $count)
    }
}

function Complete-LanScan {
    if (-not $script:scanJob) { return }
    $finished = $script:scanJob.State -ne 'Running'
    try {
        $results = @(Receive-Job -Job $script:scanJob -ErrorAction Stop)
        foreach ($result in $results) {
            if ($result.Kind -eq 'Progress') {
                $script:scanProcessed = [int]$result.Scanned
            }
            elseif ($result.Kind -eq 'Host') {
                $row = New-Object System.Windows.Forms.ListViewItem([string]$result.IP)
                [void]$row.SubItems.Add([string]$result.HostName)
                [void]$row.SubItems.Add([string]$result.Ping)
                [void]$row.SubItems.Add([string]$result.Mac)
                [void]$row.SubItems.Add([string]$result.Ports)
                [void]$script:lvScan.Items.Add($row)
            }
        }
        if ($finished) {
            $script:lblScanStatus.Text = ('{0} devices found' -f $script:lvScan.Items.Count)
            Add-Log -Area 'IP Scanner' -Level 'OK' -Message ('{0} devices found. ICMP and common TCP services were checked.' -f $script:lvScan.Items.Count)
        }
        else {
            $script:lblScanStatus.Text = ('Scanning {0}/{1} | {2} devices' -f $script:scanProcessed, $script:scanTotal, $script:lvScan.Items.Count)
        }
    }
    catch {
        $finished = $true
        $script:lblScanStatus.Text = 'Scan failed'
        Add-Log -Area 'IP Scanner' -Level 'ERROR' -Message $_.Exception.Message
    }
    finally {
        if ($finished) {
            $script:scanTimer.Stop()
            Remove-Job -Job $script:scanJob -Force -ErrorAction SilentlyContinue
            $script:scanJob = $null
            $script:btnScan.Enabled = $true
        }
    }
}

function Stop-LanScan {
    if ($script:scanJob) {
        Stop-Job -Job $script:scanJob -ErrorAction SilentlyContinue
        $script:scanTimer.Stop()
        Remove-Job -Job $script:scanJob -Force -ErrorAction SilentlyContinue
        $script:scanJob = $null
        $script:btnScan.Enabled = $true
        $script:lblScanStatus.Text = 'Scan stopped'
    }
}

function Export-LanScan {
    if ($script:lvScan.Items.Count -eq 0) { throw 'There are no scan results to export.' }
    $dialog = New-Object System.Windows.Forms.SaveFileDialog
    $dialog.Filter = 'CSV files (*.csv)|*.csv'
    $dialog.FileName = ('IP_Scan_{0:yyyyMMdd_HHmmss}.csv' -f (Get-Date))
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return }
    $rows = foreach ($item in $script:lvScan.Items) {
        [pscustomobject]@{ IPAddress = $item.Text; HostName = $item.SubItems[1].Text; Ping = $item.SubItems[2].Text; MacAddress = $item.SubItems[3].Text; OpenPorts = $item.SubItems[4].Text }
    }
    $rows | Export-Csv -LiteralPath $dialog.FileName -NoTypeInformation -Encoding UTF8
    Add-Log -Area 'IP Scanner' -Level 'OK' -Message ("Exported scan results to {0}" -f $dialog.FileName)
}

function Show-VlanClues {
    Invoke-UiAction -Name 'VLAN Clues' -Action {
        $name = Get-SelectedAdapterName
        $adapter = @($script:AdapterList | Where-Object { $_.Name -eq $name } | Select-Object -First 1)
        $lines = @("Adapter: $name", "Link: $($adapter.Status)", "Address: $($adapter.IPText)")
        try {
            $properties = @(Get-NetAdapterAdvancedProperty -Name $name -ErrorAction Stop |
                Where-Object { $_.RegistryKeyword -match 'vlan' -or $_.DisplayName -match 'vlan' })
            foreach ($property in $properties) {
                $lines += ('NIC setting: {0} = {1}' -f $property.DisplayName, $property.DisplayValue)
            }
        }
        catch { $lines += 'NIC VLAN setting: unavailable' }
        $lines += 'Switch access VLAN: unconfirmed from this laptop.'
        $lines += 'Check managed switch port configuration, LLDP/CDP inventory, or your network team for the actual port VLAN.'
        $script:txtVlanClues.Text = $lines -join [Environment]::NewLine
        Add-Log -Area 'VLAN' -Level 'INFO' -Message ('Displayed VLAN clues for {0}; switch port VLAN remains unconfirmed.' -f $name)
    }
}

function Search-ToolkitLog {
    param([string]$Query)
    $hits = @($script:lvLog.Items | Where-Object {
        $_.Text -like "*$Query*" -or @($_.SubItems | Where-Object { $_.Text -like "*$Query*" }).Count -gt 0
    })
    if ($hits.Count -eq 0) {
        [System.Windows.Forms.MessageBox]::Show('No matching entries in the current technician log.', 'Toolkit Search') | Out-Null
        return
    }
    foreach ($item in @($script:lvLog.SelectedItems)) { $item.Selected = $false }
    $hits[0].Selected = $true
    $hits[0].EnsureVisible()
    $script:lvLog.Focus()
    Set-MainStatus -Text ('{0} log matches' -f $hits.Count) -Color ([System.Drawing.Color]::FromArgb(45, 130, 80))
}

function Restore-ToolkitFromTray {
    if (-not $script:form.Visible) { $script:form.Show() }
    $script:form.WindowState = [System.Windows.Forms.FormWindowState]::Normal
    $script:form.Activate()
}

function Exit-Toolkit {
    $script:ExitRequested = $true
    $script:form.Close()
}

function Select-FeedbackScreenshot {
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    $dialog.Filter = 'Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg'
    $dialog.Title = 'Choose a screenshot for feedback'
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $script:txtFeedbackScreenshot.Text = $dialog.FileName
    }
}

function Submit-ToolkitFeedback {
    Invoke-UiAction -Name 'Feedback' -Action {
        $message = $script:txtFeedback.Text.Trim()
        if ($message.Length -lt 10) { throw 'Describe your feedback in at least 10 characters.' }
        $screenshot = $script:txtFeedbackScreenshot.Text.Trim()
        if ($screenshot) {
            if (-not (Test-Path -LiteralPath $screenshot -PathType Leaf)) { throw 'The selected screenshot file was not found.' }
            if ([System.IO.Path]::GetExtension($screenshot).ToLowerInvariant() -notin @('.png', '.jpg', '.jpeg')) { throw 'Choose a PNG or JPEG screenshot.' }
            if ((Get-Item -LiteralPath $screenshot).Length -gt 15MB) { throw 'The screenshot exceeds the 15 MB attachment limit.' }
        }
        if (-not (Test-InternetConnection)) { throw 'Connect to the internet before sending feedback.' }
        $category = [string]$script:cboFeedbackCategory.SelectedItem
        $subject = 'TEC Systems Field Toolkit Feedback - ' + $category
        $body = "Category: $category`r`nComputer: $env:COMPUTERNAME`r`nToolkit version: $script:ToolkitVersion`r`n`r`n$message"
        $confirmation = 'Send this feedback to IT@tec-system.com?'
        if ($screenshot) { $confirmation += "`r`n`r`nScreenshot: $screenshot" }
        if ([System.Windows.Forms.MessageBox]::Show($confirmation, 'Confirm Feedback', 'YesNo', 'Question') -ne 'Yes') { return }

        try {
            $outlook = New-Object -ComObject Outlook.Application -ErrorAction Stop
            $mail = $outlook.CreateItem(0)
            $mail.To = 'IT@tec-system.com'
            $mail.Subject = $subject
            $mail.Body = $body
            if ($screenshot) { [void]$mail.Attachments.Add($screenshot) }
            $mail.Send()
            Add-Log -Area 'Feedback' -Level 'OK' -Message 'Feedback submitted to classic Outlook. Delivery depends on Outlook connectivity and account settings.'
            [System.Windows.Forms.MessageBox]::Show('Feedback was submitted to classic Outlook. Check Outbox/Sent Items to confirm delivery.', 'Feedback', 'OK', 'Information') | Out-Null
            $script:txtFeedback.Clear()
            $script:txtFeedbackScreenshot.Clear()
            return
        }
        catch {
            Add-Log -Area 'Feedback' -Level 'WARN' -Message ('Classic Outlook automation unavailable: {0}' -f $_.Exception.Message)
        }

        $uri = 'mailto:IT@tec-system.com?subject=' + [uri]::EscapeDataString($subject) + '&body=' + [uri]::EscapeDataString($body)
        Start-Process $uri -ErrorAction Stop
        $notice = 'A mail draft was opened. Review it and click Send in your mail app.'
        if ($screenshot) { $notice += "`r`n`r`nNew Outlook cannot attach this file through local automation. Attach it manually before sending:`r`n$screenshot" }
        [System.Windows.Forms.MessageBox]::Show($notice, 'Finish Sending Feedback', 'OK', 'Information') | Out-Null
        Add-Log -Area 'Feedback' -Level 'INFO' -Message 'Opened default mail draft; technician must send it.'
    }
}

# Build Form
# -------------------------------
$form = New-Object System.Windows.Forms.Form
$script:form = $form
$form.Text = $script:AppName
$packagedIconPath = Join-Path -Path $PSScriptRoot -ChildPath 'assets\TEC Systems Field Toolkit.ico'
$launcherIconPath = Join-Path -Path $PSScriptRoot -ChildPath 'TEC-Systems-FieldToolkit.exe'
if (Test-Path -LiteralPath $packagedIconPath) {
    $form.Icon = New-Object System.Drawing.Icon($packagedIconPath)
}
elseif (Test-Path -LiteralPath $launcherIconPath) {
    $form.Icon = [System.Drawing.Icon]::ExtractAssociatedIcon($launcherIconPath)
}
$form.StartPosition = 'CenterScreen'
$form.Size = New-Object System.Drawing.Size(1280, 780)
$form.MinimumSize = New-Object System.Drawing.Size(1120, 680)
$form.BackColor = $colorBackground
$form.Font = $fontMain

$script:trayMenu = New-Object System.Windows.Forms.ContextMenuStrip
$openTrayItem = New-Object System.Windows.Forms.ToolStripMenuItem('Open Toolkit')
$openTrayItem.Add_Click({ Restore-ToolkitFromTray })
[void]$script:trayMenu.Items.Add($openTrayItem)
$updateTrayItem = New-Object System.Windows.Forms.ToolStripMenuItem('Check Updates')
$updateTrayItem.Add_Click({ Restore-ToolkitFromTray; Check-ToolkitUpdates })
[void]$script:trayMenu.Items.Add($updateTrayItem)
[void]$script:trayMenu.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
$exitTrayItem = New-Object System.Windows.Forms.ToolStripMenuItem('Exit')
$exitTrayItem.Add_Click({ Exit-Toolkit })
[void]$script:trayMenu.Items.Add($exitTrayItem)
$script:trayIcon = New-Object System.Windows.Forms.NotifyIcon
$script:trayIcon.Icon = $form.Icon
$script:trayIcon.Text = $script:AppName
$script:trayIcon.ContextMenuStrip = $script:trayMenu
$script:trayIcon.Add_DoubleClick({ Restore-ToolkitFromTray })
$form.Add_Resize({
    if ($script:form.WindowState -eq [System.Windows.Forms.FormWindowState]::Minimized) {
        $script:form.Hide()
    }
})

$script:toolTip = New-Object System.Windows.Forms.ToolTip
$script:toolTip.AutoPopDelay = 12000
$script:toolTip.InitialDelay = 400
$script:toolTip.ReshowDelay = 200
$script:toolTip.ShowAlways = $true

$header = New-Object System.Windows.Forms.Panel
$header.Location = New-Object System.Drawing.Point(0, 0)
$header.Size = New-Object System.Drawing.Size(1280, 104)
$header.Anchor = 'Top, Left, Right'
$header.BackColor = [System.Drawing.Color]::White
$form.Controls.Add($header)

if (Test-Path -Path $script:LogoPath) {
    $logo = New-Object System.Windows.Forms.PictureBox
    $logo.Location = New-Object System.Drawing.Point(18, 16)
    $logo.Size = New-Object System.Drawing.Size(300, 64)
    $logo.SizeMode = 'Zoom'
    $logo.Image = [System.Drawing.Image]::FromFile($script:LogoPath)
    $header.Controls.Add($logo)
}

$lblTitle = New-Object System.Windows.Forms.Label
$lblTitle.Text = $script:AppName
$lblTitle.Location = New-Object System.Drawing.Point(340, 20)
$lblTitle.Size = New-Object System.Drawing.Size(300, 30)
$lblTitle.Font = $fontHeader
$lblTitle.ForeColor = $colorText
$header.Controls.Add($lblTitle)

$lblManaged = New-Object System.Windows.Forms.Label
$lblManaged.Text = $script:ManagedBy
$lblManaged.Location = New-Object System.Drawing.Point(342, 54)
$lblManaged.Size = New-Object System.Drawing.Size(260, 22)
$lblManaged.ForeColor = $colorMuted
$lblManaged | Add-Member -NotePropertyName ThemeTone -NotePropertyValue 'Muted' -Force
$header.Controls.Add($lblManaged)

$lblHost = New-Object System.Windows.Forms.Label
$lblHost.Text = ('Hostname: {0}' -f $env:COMPUTERNAME)
$lblHost.Location = New-Object System.Drawing.Point(1090, 16)
$lblHost.Size = New-Object System.Drawing.Size(150, 22)
$lblHost.Anchor = 'Top, Right'
$lblHost.TextAlign = 'MiddleRight'
$lblHost.ForeColor = $colorText
$header.Controls.Add($lblHost)

$lblAdmin = New-Object System.Windows.Forms.Label
$lblAdmin.Text = if (Test-IsAdministrator) { 'Mode: Administrator' } else { 'Mode: Standard User' }
$lblAdmin.Location = New-Object System.Drawing.Point(1030, 68)
$lblAdmin.Size = New-Object System.Drawing.Size(210, 22)
$lblAdmin.Anchor = 'Top, Right'
$lblAdmin.TextAlign = 'MiddleRight'
$lblAdmin.ForeColor = if (Test-IsAdministrator) { [System.Drawing.Color]::FromArgb(45, 130, 80) } else { [System.Drawing.Color]::FromArgb(190, 120, 45) }
$header.Controls.Add($lblAdmin)

$script:lblAdminEditor = New-Object System.Windows.Forms.Label
$script:lblAdminEditor.Text = 'Editor: Locked'
$script:lblAdminEditor.Location = New-Object System.Drawing.Point(342, 78)
$script:lblAdminEditor.Size = New-Object System.Drawing.Size(104, 22)
$script:lblAdminEditor.Anchor = 'Top, Left'
$script:lblAdminEditor.ForeColor = [System.Drawing.Color]::FromArgb(190, 120, 45)
$header.Controls.Add($script:lblAdminEditor)
$script:lblAdminEditor.Visible = $false

$script:btnAdminMode = New-Button -Text 'Unlock Editor' -OnClick { Toggle-AdminMode } -X 452 -Y 70 -Width 120 -Height 30 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))
$script:btnAdminMode.Anchor = 'Top, Left'
$header.Controls.Add($script:btnAdminMode)
$script:btnAdminMode.Visible = $false

$script:btnChangeAdminPassword = New-Button -Text 'Change Password' -OnClick { Change-AdminPassword } -X 582 -Y 70 -Width 136 -Height 30 -BackColor ([System.Drawing.Color]::FromArgb(45, 130, 80)) -HoverColor ([System.Drawing.Color]::FromArgb(35, 102, 62))
$script:btnChangeAdminPassword.Anchor = 'Top, Left'
$script:btnChangeAdminPassword.Enabled = $false
$script:btnChangeAdminPassword.Visible = $false
$header.Controls.Add($script:btnChangeAdminPassword)
$script:btnChangeAdminPassword.Visible = $false

$script:lblHeaderSearch = New-Object System.Windows.Forms.Label
$script:lblHeaderSearch.Text = 'Search log'
$script:lblHeaderSearch.Location = New-Object System.Drawing.Point(658, 54)
$script:lblHeaderSearch.Size = New-Object System.Drawing.Size(110, 18)
$script:lblHeaderSearch.ForeColor = $colorMuted
$script:lblHeaderSearch | Add-Member -NotePropertyName ThemeTone -NotePropertyValue 'Muted' -Force
$header.Controls.Add($script:lblHeaderSearch)

$script:txtHeaderSearch = New-Object System.Windows.Forms.TextBox
$script:txtHeaderSearch.Location = New-Object System.Drawing.Point(658, 74)
$script:txtHeaderSearch.Size = New-Object System.Drawing.Size(126, 24)
$script:txtHeaderSearch.Anchor = 'Top, Left'
$script:txtHeaderSearch.BorderStyle = 'FixedSingle'
$header.Controls.Add($script:txtHeaderSearch)
$script:txtHeaderSearch.Add_KeyDown({
    if ($_.KeyCode -eq [System.Windows.Forms.Keys]::Enter) {
        $script:btnHeaderSearch.PerformClick()
        $_.SuppressKeyPress = $true
    }
})

$script:btnHeaderSearch = New-Button -Text 'Search' -OnClick {
    $query = $script:txtHeaderSearch.Text.Trim()
    if (-not [string]::IsNullOrWhiteSpace($query)) {
        Search-ToolkitLog -Query $query
    }
} -X 792 -Y 70 -Width 62 -Height 30 -BackColor ([System.Drawing.Color]::FromArgb(45, 130, 80)) -HoverColor ([System.Drawing.Color]::FromArgb(35, 102, 62))
$script:btnHeaderSearch.Anchor = 'Top, Left'
$header.Controls.Add($script:btnHeaderSearch)

$script:btnThemeToggle = New-Button -Text 'Dark Mode' -OnClick { Toggle-DarkMode } -X 862 -Y 70 -Width 86 -Height 30 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))
$script:btnThemeToggle.Anchor = 'Top, Left'
$header.Controls.Add($script:btnThemeToggle)
Update-HeaderToolbarLayout

$script:splitMain = New-Object System.Windows.Forms.SplitContainer
$script:splitMain.Location = New-Object System.Drawing.Point(18, 122)
$script:splitMain.Size = New-Object System.Drawing.Size(1226, 560)
$script:splitMain.Anchor = 'Top, Bottom, Left, Right'
$script:splitMain.FixedPanel = 'None'
$script:splitMain.IsSplitterFixed = $false
$script:splitMain.Orientation = 'Vertical'
$script:splitMain.SplitterDistance = 860
$script:splitMain.Panel1MinSize = 700
$script:splitMain.Panel2MinSize = 260
$form.Controls.Add($script:splitMain)

$script:leftPanel = $script:splitMain.Panel1
$script:leftPanel.BackColor = $colorPanel
$script:leftPanel.Padding = New-Object System.Windows.Forms.Padding(0)

$script:rightPanel = $script:splitMain.Panel2
$script:rightPanel.BackColor = $colorPanel
$script:rightPanel.Padding = New-Object System.Windows.Forms.Padding(0)

$tabs = New-Object System.Windows.Forms.TabControl
$script:tabs = $tabs
$tabs.Dock = 'Fill'
$script:leftPanel.Controls.Add($tabs)
$script:tabsMain = $tabs

# Troubleshooting tab
$tabTrouble = New-Object System.Windows.Forms.TabPage
$tabTrouble.Text = 'Windows Troubleshooting'
$tabTrouble.BackColor = $colorPanel
$tabTrouble.AutoScroll = $true
$script:tabTrouble = $tabTrouble
$tabs.TabPages.Add($tabTrouble)

$tabTrouble.Controls.Add((New-Label -Text 'Host / IP' -X 18 -Y 18 -Width 120))
$script:txtPingTarget = New-TextBox -X 18 -Y 42 -Width 280 -Text '8.8.8.8'
$tabTrouble.Controls.Add($script:txtPingTarget)
$tabTrouble.Controls.Add((New-Button -Text 'Ping Terminal' -OnClick { Start-PingTerminal } -X 314 -Y 38 -Width 132))
$tabTrouble.Controls.Add((New-Button -Text 'DNS Lookup' -OnClick { Run-DnsLookup } -X 458 -Y 38 -Width 118))
$tabTrouble.Controls.Add((New-Button -Text 'ARP Cache' -OnClick { Show-ArpCache } -X 588 -Y 38 -Width 118))

$script:chkContinuousPing = New-Object System.Windows.Forms.CheckBox
$script:chkContinuousPing.Text = 'Continuous ping'
$script:chkContinuousPing.Location = New-Object System.Drawing.Point(18, 74)
$script:chkContinuousPing.Size = New-Object System.Drawing.Size(160, 24)
$script:chkContinuousPing.ForeColor = $colorText
$tabTrouble.Controls.Add($script:chkContinuousPing)

$tabTrouble.Controls.Add((New-Label -Text 'Windows checks' -X 18 -Y 106 -Width 160))
$tabTrouble.Controls.Add((New-Button -Text 'System Summary' -OnClick { Show-SystemSummary } -X 18 -Y 130 -Width 132))
$tabTrouble.Controls.Add((New-Button -Text 'Disk Summary' -OnClick { Show-DiskSummary } -X 162 -Y 130 -Width 132))
$tabTrouble.Controls.Add((New-Button -Text 'Service Check' -OnClick { Show-ServiceQuickCheck } -X 306 -Y 130 -Width 132))
$tabTrouble.Controls.Add((New-Button -Text 'Event Errors' -OnClick { Show-RecentEventErrors } -X 450 -Y 130 -Width 132))
$tabTrouble.Controls.Add((New-Button -Text 'Problem Devices' -OnClick { Show-ProblemDevices } -X 594 -Y 130 -Width 112))
$tabTrouble.Controls.Add((New-Button -Text 'Update Status' -OnClick { Show-WindowsUpdateStatus } -X 18 -Y 170 -Width 132))

$tabTrouble.Controls.Add((New-Label -Text 'Open tools' -X 18 -Y 260 -Width 160))
$script:pnlTroubleTools = New-Object System.Windows.Forms.FlowLayoutPanel
$script:pnlTroubleTools.Location = New-Object System.Drawing.Point(18, 284)
$script:pnlTroubleTools.Size = New-Object System.Drawing.Size(688, 120)
$script:pnlTroubleTools.AutoScroll = $true
$script:pnlTroubleTools.WrapContents = $true
$script:pnlTroubleTools.Anchor = 'Top, Left, Right'
$tabTrouble.Controls.Add($script:pnlTroubleTools)

foreach ($toolSpec in @(
    @{ Text = 'Services'; Action = { Open-ServicesConsole } },
    @{ Text = 'Event Viewer'; Action = { Open-EventViewer } },
    @{ Text = 'Device Manager'; Action = { Open-DeviceManager } },
    @{ Text = 'Computer Mgmt'; Action = { Open-ComputerManagement } },
    @{ Text = 'Local Users'; Action = { Open-LocalUsersConsole } },
    @{ Text = 'Programs'; Action = { Open-ProgramsAndFeatures } },
    @{ Text = 'System Props'; Action = { Open-SystemProperties } },
    @{ Text = 'Task Manager'; Action = { Open-TaskManager } },
    @{ Text = 'Command Prompt'; Action = { Open-CommandPrompt } },
    @{ Text = 'PowerShell'; Action = { Open-PowerShellConsole } },
    @{ Text = 'Task Scheduler'; Action = { Open-TaskScheduler } },
    @{ Text = 'Firewall Console'; Action = { Open-FirewallConsole } },
    @{ Text = 'Credential Mgr'; Action = { Open-CredentialManager } },
    @{ Text = 'Shared Folders'; Action = { Open-SharedFolders } },
    @{ Text = 'Windows Update'; Action = { Open-WindowsUpdateSettings } },
    @{ Text = 'Remote Desktop'; Action = { Open-RemoteDesktopSettings } },
    @{ Text = 'Printers'; Action = { Open-DevicesAndPrinters } }
)) {
    $toolButton = New-Button -Text $toolSpec.Text -OnClick $toolSpec.Action -X 0 -Y 0 -Width 126 -Height 32 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))
    $toolButton.Margin = New-Object System.Windows.Forms.Padding(0, 0, 8, 8)
    $script:pnlTroubleTools.Controls.Add($toolButton)
}

$tabTrouble.Controls.Add((New-Label -Text 'Web URL' -X 18 -Y 418 -Width 120))
$script:txtWebUrl = New-TextBox -X 18 -Y 442 -Width 420 -Text 'http://'
$tabTrouble.Controls.Add($script:txtWebUrl)
$tabTrouble.Controls.Add((New-Button -Text 'Open Webpage' -OnClick { Open-WebPage } -X 456 -Y 438 -Width 150))

$tabTrouble.Controls.Add((New-Label -Text 'RDP Target' -X 18 -Y 480 -Width 120))
$script:txtRdpTarget = New-TextBox -X 18 -Y 504 -Width 420 -Text ''
$tabTrouble.Controls.Add($script:txtRdpTarget)
$tabTrouble.Controls.Add((New-Button -Text 'Open RDP' -OnClick { Open-Rdp } -X 456 -Y 500 -Width 150))

# Network troubleshooting tab
$tabNetwork = New-Object System.Windows.Forms.TabPage
$tabNetwork.Text = 'Network Troubleshooting'
$tabNetwork.BackColor = $colorPanel
$tabNetwork.AutoScroll = $true
$script:tabNetwork = $tabNetwork
$tabs.TabPages.Add($tabNetwork)

$grpNetDevice = New-Object System.Windows.Forms.GroupBox
$grpNetDevice.Text = 'Device Checks'
$grpNetDevice.Location = New-Object System.Drawing.Point(18, 18)
$grpNetDevice.Size = New-Object System.Drawing.Size(340, 230)
$grpNetDevice.ForeColor = $colorText
$tabNetwork.Controls.Add($grpNetDevice)

$grpNetDevice.Controls.Add((New-Label -Text 'Device host / IP' -X 14 -Y 28 -Width 140))
$script:txtNetworkDeviceTarget = New-TextBox -X 14 -Y 52 -Width 300 -Text '192.168.1.10'
$grpNetDevice.Controls.Add($script:txtNetworkDeviceTarget)

$script:chkNetworkContinuousPing = New-Object System.Windows.Forms.CheckBox
$script:chkNetworkContinuousPing.Text = 'Continuous ping'
$script:chkNetworkContinuousPing.Location = New-Object System.Drawing.Point(14, 82)
$script:chkNetworkContinuousPing.Size = New-Object System.Drawing.Size(140, 24)
$script:chkNetworkContinuousPing.ForeColor = $colorText
$grpNetDevice.Controls.Add($script:chkNetworkContinuousPing)

$grpNetDevice.Controls.Add((New-Button -Text 'Ping Device' -OnClick { Start-NetworkDevicePing } -X 14 -Y 114 -Width 142))
$grpNetDevice.Controls.Add((New-Button -Text 'Ping Gateway' -OnClick { Start-GatewayPing } -X 172 -Y 114 -Width 142 -BackColor ([System.Drawing.Color]::FromArgb(45, 130, 80)) -HoverColor ([System.Drawing.Color]::FromArgb(35, 102, 62))))
$grpNetDevice.Controls.Add((New-Button -Text 'Tracert' -OnClick { Start-TracertToDevice } -X 14 -Y 154 -Width 142 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))
$grpNetDevice.Controls.Add((New-Button -Text 'PathPing' -OnClick { Start-PathPingToDevice } -X 172 -Y 154 -Width 142 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))
$grpNetDevice.Controls.Add((New-Button -Text 'DNS Lookup' -OnClick {
    if ($script:txtNetworkDeviceTarget.Text.Trim()) { $script:txtPingTarget.Text = $script:txtNetworkDeviceTarget.Text.Trim() }
    Run-DnsLookup
} -X 14 -Y 194 -Width 142))
$grpNetDevice.Controls.Add((New-Button -Text 'Open Webpage' -OnClick {
    if ($script:txtNetworkDeviceTarget.Text.Trim()) { $script:txtWebUrl.Text = $script:txtNetworkDeviceTarget.Text.Trim() }
    Open-WebPage
} -X 172 -Y 194 -Width 142))

$grpSwitch = New-Object System.Windows.Forms.GroupBox
$grpSwitch.Text = 'Switch Access'
$grpSwitch.Location = New-Object System.Drawing.Point(376, 18)
$grpSwitch.Size = New-Object System.Drawing.Size(340, 230)
$grpSwitch.ForeColor = $colorText
$tabNetwork.Controls.Add($grpSwitch)

$grpSwitch.Controls.Add((New-Label -Text 'Switch host / IP' -X 14 -Y 28 -Width 140))
$script:txtSwitchTarget = New-TextBox -X 14 -Y 52 -Width 220 -Text '192.168.1.2'
$grpSwitch.Controls.Add($script:txtSwitchTarget)
$grpSwitch.Controls.Add((New-Label -Text 'Web' -X 246 -Y 28 -Width 48))
$script:cboSwitchScheme = New-Object System.Windows.Forms.ComboBox
$script:cboSwitchScheme.Location = New-Object System.Drawing.Point(246, 52)
$script:cboSwitchScheme.Size = New-Object System.Drawing.Size(70, 24)
$script:cboSwitchScheme.DropDownStyle = 'DropDownList'
[void]$script:cboSwitchScheme.Items.Add('http://')
[void]$script:cboSwitchScheme.Items.Add('https://')
$script:cboSwitchScheme.SelectedIndex = 0
$grpSwitch.Controls.Add($script:cboSwitchScheme)

$grpSwitch.Controls.Add((New-Label -Text 'Port / VLAN notes' -X 14 -Y 82 -Width 140))
$script:txtSwitchPortNotes = New-TextBox -X 14 -Y 106 -Width 300 -Text ''
$grpSwitch.Controls.Add($script:txtSwitchPortNotes)

$grpSwitch.Controls.Add((New-Button -Text 'Ping Switch' -OnClick {
    if ($script:txtSwitchTarget.Text.Trim()) { $script:txtNetworkDeviceTarget.Text = $script:txtSwitchTarget.Text.Trim() }
    Start-NetworkDevicePing
} -X 14 -Y 146 -Width 142))
$grpSwitch.Controls.Add((New-Button -Text 'Switch Ports' -OnClick { Test-SwitchAccessPorts } -X 172 -Y 146 -Width 142 -BackColor ([System.Drawing.Color]::FromArgb(45, 130, 80)) -HoverColor ([System.Drawing.Color]::FromArgb(35, 102, 62))))
$grpSwitch.Controls.Add((New-Button -Text 'Open Web UI' -OnClick { Open-SwitchWebUi } -X 14 -Y 186 -Width 142))
$grpSwitch.Controls.Add((New-Button -Text 'SSH Terminal' -OnClick { Open-SwitchSshTerminal } -X 172 -Y 186 -Width 142 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))

$grpCapture = New-Object System.Windows.Forms.GroupBox
$grpCapture.Text = 'Quick Capture'
$grpCapture.Location = New-Object System.Drawing.Point(18, 262)
$grpCapture.Size = New-Object System.Drawing.Size(340, 328)
$grpCapture.ForeColor = $colorText
$tabNetwork.Controls.Add($grpCapture)

$grpCapture.Controls.Add((New-Button -Text 'Baseline Snapshot' -OnClick { Show-NetworkBaseline } -X 14 -Y 32 -Width 146))
$grpCapture.Controls.Add((New-Button -Text 'Link Status' -OnClick { Show-NetworkLinkStatus } -X 174 -Y 32 -Width 146 -BackColor ([System.Drawing.Color]::FromArgb(45, 130, 80)) -HoverColor ([System.Drawing.Color]::FromArgb(35, 102, 62))))
$grpCapture.Controls.Add((New-Button -Text 'Adapter Details' -OnClick { Show-AdapterDetails } -X 14 -Y 72 -Width 146 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))
$grpCapture.Controls.Add((New-Button -Text 'Neighbors' -OnClick { Show-NetworkNeighborTable } -X 174 -Y 72 -Width 146 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))
$grpCapture.Controls.Add((New-Button -Text 'ARP Cache' -OnClick { Show-ArpCache } -X 14 -Y 112 -Width 146 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))
$grpCapture.Controls.Add((New-Button -Text 'Route Table' -OnClick { Show-RouteTable } -X 174 -Y 112 -Width 146 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))
$grpCapture.Controls.Add((New-Button -Text 'DNS Cache' -OnClick { Show-DnsCacheEntries } -X 14 -Y 152 -Width 146 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))
$grpCapture.Controls.Add((New-Button -Text 'Netstat' -OnClick { Show-NetstatSummary } -X 174 -Y 152 -Width 146 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))
$grpCapture.Controls.Add((New-Button -Text 'Firewall' -OnClick { Show-FirewallProfiles } -X 14 -Y 192 -Width 146 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))
$grpCapture.Controls.Add((New-Button -Text 'Network Summary' -OnClick { Show-NetworkSummary } -X 174 -Y 192 -Width 146))
$grpCapture.Controls.Add((New-Button -Text 'IPConfig /all' -OnClick { Show-IpConfigAll } -X 14 -Y 232 -Width 146))
$grpCapture.Controls.Add((New-Button -Text 'Network Settings' -OnClick { Open-NetworkSettings } -X 174 -Y 232 -Width 146))
$grpCapture.Controls.Add((New-Button -Text 'Flush DNS' -OnClick { Flush-DnsCache } -X 14 -Y 272 -Width 146 -BackColor ([System.Drawing.Color]::FromArgb(185, 95, 35)) -HoverColor ([System.Drawing.Color]::FromArgb(150, 70, 25))))

$grpPlaybook = New-Object System.Windows.Forms.GroupBox
$grpPlaybook.Text = 'Field Playbooks'
$grpPlaybook.Location = New-Object System.Drawing.Point(376, 262)
$grpPlaybook.Size = New-Object System.Drawing.Size(340, 248)
$grpPlaybook.ForeColor = $colorText
$tabNetwork.Controls.Add($grpPlaybook)

$grpPlaybook.Controls.Add((New-Button -Text 'Managed Switch' -OnClick { Show-NetworkPlaybook -Topic 'managed-switch' } -X 14 -Y 28 -Width 148))
$grpPlaybook.Controls.Add((New-Button -Text 'Unmanaged Switch' -OnClick { Show-NetworkPlaybook -Topic 'unmanaged-switch' } -X 174 -Y 28 -Width 148))
$grpPlaybook.Controls.Add((New-Button -Text 'MS/TP and IP' -OnClick { Show-NetworkPlaybook -Topic 'mstp-ip' } -X 14 -Y 66 -Width 148 -BackColor ([System.Drawing.Color]::FromArgb(45, 130, 80)) -HoverColor ([System.Drawing.Color]::FromArgb(35, 102, 62))))
$grpPlaybook.Controls.Add((New-Button -Text 'No Link Light' -OnClick { Show-NetworkPlaybook -Topic 'no-link' } -X 174 -Y 66 -Width 148 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))
$grpPlaybook.Controls.Add((New-Button -Text 'Wrong VLAN / IP' -OnClick { Show-NetworkPlaybook -Topic 'wrong-vlan' } -X 14 -Y 104 -Width 148 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))
$grpPlaybook.Controls.Add((New-Button -Text 'Intermittent' -OnClick { Show-NetworkPlaybook -Topic 'intermittent' } -X 174 -Y 104 -Width 148 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))

$script:txtNetworkPlaybook = New-Object System.Windows.Forms.TextBox
$script:txtNetworkPlaybook.Location = New-Object System.Drawing.Point(14, 144)
$script:txtNetworkPlaybook.Size = New-Object System.Drawing.Size(308, 88)
$script:txtNetworkPlaybook.Multiline = $true
$script:txtNetworkPlaybook.ScrollBars = 'Vertical'
$script:txtNetworkPlaybook.Font = $fontMain
$script:txtNetworkPlaybook.ReadOnly = $true
$script:txtNetworkPlaybook.BackColor = [System.Drawing.Color]::White
$grpPlaybook.Controls.Add($script:txtNetworkPlaybook)
Show-NetworkPlaybook -Topic ''

# BMS troubleshooting tab
$tabBms = New-Object System.Windows.Forms.TabPage
$tabBms.Text = 'BMS Troubleshooting'
$tabBms.BackColor = $colorPanel
$script:tabBms = $tabBms
$tabs.TabPages.Add($tabBms)

$script:splitBms = New-Object System.Windows.Forms.SplitContainer
$script:splitBms.Location = New-Object System.Drawing.Point(8, 8)
$script:splitBms.Size = New-Object System.Drawing.Size(708, 486)
$script:splitBms.Anchor = 'Top, Bottom, Left, Right'
$script:splitBms.SplitterDistance = 200
$script:splitBms.Panel1MinSize = 180
$script:splitBms.Panel2MinSize = 420
$tabBms.Controls.Add($script:splitBms)

$script:splitBms.Panel1.Controls.Add((New-Label -Text 'BMS categories' -X 10 -Y 12 -Width 160))
$script:lstBmsTopics = New-Object System.Windows.Forms.ListBox
$script:lstBmsTopics.Location = New-Object System.Drawing.Point(10, 38)
$script:lstBmsTopics.Size = New-Object System.Drawing.Size(180, 184)
$script:lstBmsTopics.Font = $fontMain
$script:lstBmsTopics.Add_SelectedIndexChanged({ Select-BmsCategory })
$script:splitBms.Panel1.Controls.Add($script:lstBmsTopics)

$script:lblBmsOutlineTitle = New-Label -Text 'Decision outline' -X 10 -Y 236 -Width 160
$script:splitBms.Panel1.Controls.Add($script:lblBmsOutlineTitle)
$script:tvBmsFlowOutline = New-Object System.Windows.Forms.TreeView
$script:tvBmsFlowOutline.Location = New-Object System.Drawing.Point(10, 262)
$script:tvBmsFlowOutline.Size = New-Object System.Drawing.Size(180, 194)
$script:tvBmsFlowOutline.Anchor = 'Top, Bottom, Left, Right'
$script:tvBmsFlowOutline.HideSelection = $false
$script:tvBmsFlowOutline.Add_NodeMouseClick({
    if ($_.Node -and $_.Node.Tag) {
        Select-BmsStepFromOutline -StepId ([string]$_.Node.Tag)
    }
})
$script:splitBms.Panel1.Controls.Add($script:tvBmsFlowOutline)

$script:tabsBmsModes = New-Object System.Windows.Forms.TabControl
$script:tabsBmsModes.Dock = 'Fill'
$script:splitBms.Panel2.Controls.Add($script:tabsBmsModes)

$script:tabBmsTroubleshoot = New-Object System.Windows.Forms.TabPage
$script:tabBmsTroubleshoot.Text = 'Troubleshoot'
$script:tabBmsTroubleshoot.BackColor = $colorPanel
[void]$script:tabsBmsModes.TabPages.Add($script:tabBmsTroubleshoot)

$script:lblBmsActiveCategory = New-Object System.Windows.Forms.Label
$script:lblBmsActiveCategory.Text = 'Category:'
$script:lblBmsActiveCategory.Location = New-Object System.Drawing.Point(14, 14)
$script:lblBmsActiveCategory.Size = New-Object System.Drawing.Size(460, 22)
$script:lblBmsActiveCategory.ForeColor = $colorText
$script:lblBmsActiveCategory.Font = $fontSection
$script:tabBmsTroubleshoot.Controls.Add($script:lblBmsActiveCategory)

$script:lblBmsActiveStep = New-Object System.Windows.Forms.Label
$script:lblBmsActiveStep.Text = 'Step:'
$script:lblBmsActiveStep.Location = New-Object System.Drawing.Point(14, 40)
$script:lblBmsActiveStep.Size = New-Object System.Drawing.Size(460, 22)
$script:lblBmsActiveStep.ForeColor = $colorMuted
$script:lblBmsActiveStep | Add-Member -NotePropertyName ThemeTone -NotePropertyValue 'Muted' -Force
$script:tabBmsTroubleshoot.Controls.Add($script:lblBmsActiveStep)

$script:txtBmsRunPrompt = New-Object System.Windows.Forms.TextBox
$script:txtBmsRunPrompt.Location = New-Object System.Drawing.Point(14, 72)
$script:txtBmsRunPrompt.Size = New-Object System.Drawing.Size(458, 84)
$script:txtBmsRunPrompt.Multiline = $true
$script:txtBmsRunPrompt.ScrollBars = 'Vertical'
$script:txtBmsRunPrompt.Font = $fontMain
$script:txtBmsRunPrompt.ReadOnly = $true
$script:txtBmsRunPrompt.BackColor = [System.Drawing.Color]::White
$script:tabBmsTroubleshoot.Controls.Add($script:txtBmsRunPrompt)

$script:btnBmsResetToStart = New-Button -Text 'Restart Flow' -OnClick { Reset-BmsToStartStep } -X 14 -Y 166 -Width 120 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))
$script:tabBmsTroubleshoot.Controls.Add($script:btnBmsResetToStart)
$script:btnBmsBack = New-Button -Text 'Back' -OnClick { Go-BackBmsStep } -X 144 -Y 166 -Width 90 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))
$script:tabBmsTroubleshoot.Controls.Add($script:btnBmsBack)

$script:tabBmsTroubleshoot.Controls.Add((New-Label -Text 'Choose the next action' -X 14 -Y 208 -Width 180))
$script:pnlBmsButtons = New-Object System.Windows.Forms.FlowLayoutPanel
$script:pnlBmsButtons.Location = New-Object System.Drawing.Point(14, 234)
$script:pnlBmsButtons.Size = New-Object System.Drawing.Size(458, 142)
$script:pnlBmsButtons.Anchor = 'Top, Bottom, Left, Right'
$script:pnlBmsButtons.AutoScroll = $true
$script:pnlBmsButtons.WrapContents = $true
$script:tabBmsTroubleshoot.Controls.Add($script:pnlBmsButtons)

$script:tabBmsTroubleshoot.Controls.Add((New-Label -Text 'Guidance / resolution' -X 14 -Y 390 -Width 180))
$script:txtBmsResult = New-Object System.Windows.Forms.TextBox
$script:txtBmsResult.Location = New-Object System.Drawing.Point(14, 416)
$script:txtBmsResult.Size = New-Object System.Drawing.Size(458, 66)
$script:txtBmsResult.Anchor = 'Top, Bottom, Left, Right'
$script:txtBmsResult.Multiline = $true
$script:txtBmsResult.ScrollBars = 'Vertical'
$script:txtBmsResult.Font = $fontMain
$script:txtBmsResult.ReadOnly = $true
$script:txtBmsResult.BackColor = [System.Drawing.Color]::White
$script:tabBmsTroubleshoot.Controls.Add($script:txtBmsResult)

$script:tabBmsBuilder = New-Object System.Windows.Forms.TabPage
$script:tabBmsBuilder.Text = 'Decision Editor'
$script:tabBmsBuilder.BackColor = $colorPanel

$script:pnlBmsBuilder = New-Object System.Windows.Forms.Panel
$script:pnlBmsBuilder.Dock = 'Fill'
$script:pnlBmsBuilder.AutoScroll = $true
$script:tabBmsBuilder.Controls.Add($script:pnlBmsBuilder)

$script:pnlBmsBuilder.Controls.Add((New-Label -Text 'Decision editor' -X 14 -Y 10 -Width 160))
$script:lblBmsBuilderIntro = New-Object System.Windows.Forms.Label
$script:lblBmsBuilderIntro.Text = 'Build the technician path as topic > step > choice. Keep each step short and let the choice notes hold the resolution details.'
$script:lblBmsBuilderIntro.Location = New-Object System.Drawing.Point(14, 30)
$script:lblBmsBuilderIntro.Size = New-Object System.Drawing.Size(470, 36)
$script:lblBmsBuilderIntro.ForeColor = $colorMuted
$script:lblBmsBuilderIntro | Add-Member -NotePropertyName ThemeTone -NotePropertyValue 'Muted' -Force
$script:pnlBmsBuilder.Controls.Add($script:lblBmsBuilderIntro)

$script:pnlBmsBuilder.Controls.Add((New-Label -Text 'Topic name' -X 14 -Y 78 -Width 160))
$script:txtBmsCategory = New-TextBox -X 14 -Y 40 -Width 210 -Text ''
$script:txtBmsCategory.Location = New-Object System.Drawing.Point(14, 102)
$script:pnlBmsBuilder.Controls.Add($script:txtBmsCategory)
$script:btnNewBmsTopic = New-Button -Text 'New Topic' -OnClick { New-BmsCategory } -X 236 -Y 98 -Width 80
$script:pnlBmsBuilder.Controls.Add($script:btnNewBmsTopic)
$script:btnSaveBmsTopic = New-Button -Text 'Save Topic' -OnClick { Save-BmsCategory } -X 322 -Y 98 -Width 80
$script:pnlBmsBuilder.Controls.Add($script:btnSaveBmsTopic)
$script:btnDeleteBmsTopic = New-Button -Text 'Delete Topic' -OnClick { Delete-BmsCategory } -X 408 -Y 98 -Width 80 -BackColor ([System.Drawing.Color]::FromArgb(95, 105, 120)) -HoverColor ([System.Drawing.Color]::FromArgb(70, 80, 95))
$script:pnlBmsBuilder.Controls.Add($script:btnDeleteBmsTopic)
$script:pnlBmsBuilder.Controls.Add((New-Label -Text 'Steps in this topic' -X 14 -Y 148 -Width 180))
$script:lstBmsSteps = New-Object System.Windows.Forms.ListBox
$script:lstBmsSteps.Location = New-Object System.Drawing.Point(14, 172)
$script:lstBmsSteps.Size = New-Object System.Drawing.Size(180, 186)
$script:lstBmsSteps.Font = $fontMain
$script:lstBmsSteps.Add_SelectedIndexChanged({ Select-BmsStep })
$script:pnlBmsBuilder.Controls.Add($script:lstBmsSteps)

$script:btnNewBmsStep = New-Button -Text 'New Step' -OnClick { New-BmsStep } -X 14 -Y 370 -Width 84
$script:pnlBmsBuilder.Controls.Add($script:btnNewBmsStep)
$script:btnSaveBmsStep = New-Button -Text 'Save Step' -OnClick { Save-BmsStep } -X 106 -Y 370 -Width 88
$script:pnlBmsBuilder.Controls.Add($script:btnSaveBmsStep)
$script:btnDeleteBmsStep = New-Button -Text 'Delete' -OnClick { Delete-BmsStep } -X 14 -Y 408 -Width 84 -BackColor ([System.Drawing.Color]::FromArgb(95, 105, 120)) -HoverColor ([System.Drawing.Color]::FromArgb(70, 80, 95))
$script:pnlBmsBuilder.Controls.Add($script:btnDeleteBmsStep)
$script:btnSetBmsStartStep = New-Button -Text 'Set as Start Step' -OnClick { Set-BmsStartStep } -X 106 -Y 408 -Width 140 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))
$script:pnlBmsBuilder.Controls.Add($script:btnSetBmsStartStep)

$script:pnlBmsBuilder.Controls.Add((New-Label -Text 'Step name' -X 220 -Y 148 -Width 140))
$script:txtBmsStepName = New-TextBox -X 220 -Y 172 -Width 268 -Text ''
$script:pnlBmsBuilder.Controls.Add($script:txtBmsStepName)

$script:pnlBmsBuilder.Controls.Add((New-Label -Text 'Question shown to technicians' -X 220 -Y 210 -Width 220))
$script:txtBmsPrompt = New-Object System.Windows.Forms.TextBox
$script:txtBmsPrompt.Location = New-Object System.Drawing.Point(220, 234)
$script:txtBmsPrompt.Size = New-Object System.Drawing.Size(268, 94)
$script:txtBmsPrompt.Multiline = $true
$script:txtBmsPrompt.ScrollBars = 'Vertical'
$script:txtBmsPrompt.Font = $fontMain
$script:pnlBmsBuilder.Controls.Add($script:txtBmsPrompt)

$script:pnlBmsBuilder.Controls.Add((New-Label -Text 'Choices in selected step' -X 14 -Y 472 -Width 180))
$script:lstBmsButtons = New-Object System.Windows.Forms.ListBox
$script:lstBmsButtons.Location = New-Object System.Drawing.Point(14, 496)
$script:lstBmsButtons.Size = New-Object System.Drawing.Size(180, 146)
$script:lstBmsButtons.Font = $fontMain
$script:lstBmsButtons.Add_SelectedIndexChanged({ Load-BmsButtonForEdit })
$script:pnlBmsBuilder.Controls.Add($script:lstBmsButtons)

$script:btnNewBmsButton = New-Button -Text 'New Choice' -OnClick { New-BmsButton } -X 14 -Y 654 -Width 84
$script:pnlBmsBuilder.Controls.Add($script:btnNewBmsButton)
$script:btnSaveBmsButton = New-Button -Text 'Save Choice' -OnClick { Save-BmsButton } -X 106 -Y 654 -Width 88
$script:pnlBmsBuilder.Controls.Add($script:btnSaveBmsButton)
$script:btnDeleteBmsButton = New-Button -Text 'Delete' -OnClick { Delete-BmsButton } -X 14 -Y 692 -Width 84 -BackColor ([System.Drawing.Color]::FromArgb(95, 105, 120)) -HoverColor ([System.Drawing.Color]::FromArgb(70, 80, 95))
$script:pnlBmsBuilder.Controls.Add($script:btnDeleteBmsButton)

$script:pnlBmsBuilder.Controls.Add((New-Label -Text 'Choice button text' -X 220 -Y 472 -Width 140))
$script:txtBmsButtonText = New-TextBox -X 220 -Y 496 -Width 268 -Text ''
$script:pnlBmsBuilder.Controls.Add($script:txtBmsButtonText)

$script:pnlBmsBuilder.Controls.Add((New-Label -Text 'Go to next step' -X 220 -Y 534 -Width 180))
$script:cboBmsNextStep = New-Object System.Windows.Forms.ComboBox
$script:cboBmsNextStep.Location = New-Object System.Drawing.Point(220, 558)
$script:cboBmsNextStep.Size = New-Object System.Drawing.Size(268, 24)
$script:cboBmsNextStep.DropDownStyle = 'DropDownList'
$script:pnlBmsBuilder.Controls.Add($script:cboBmsNextStep)

$script:pnlBmsBuilder.Controls.Add((New-Label -Text 'What the technician should do next' -X 220 -Y 596 -Width 220))
$script:txtBmsButtonNotes = New-Object System.Windows.Forms.TextBox
$script:txtBmsButtonNotes.Location = New-Object System.Drawing.Point(220, 620)
$script:txtBmsButtonNotes.Size = New-Object System.Drawing.Size(268, 94)
$script:txtBmsButtonNotes.Multiline = $true
$script:txtBmsButtonNotes.ScrollBars = 'Vertical'
$script:txtBmsButtonNotes.Font = $fontMain
$script:pnlBmsBuilder.Controls.Add($script:txtBmsButtonNotes)

# BMS commands tab
$tabBmsCommands = New-Object System.Windows.Forms.TabPage
$tabBmsCommands.Text = 'BMS Commands'
$tabBmsCommands.BackColor = $colorPanel
$tabs.TabPages.Add($tabBmsCommands)

$script:splitBmsCommands = New-Object System.Windows.Forms.SplitContainer
$script:splitBmsCommands.Location = New-Object System.Drawing.Point(8, 8)
$script:splitBmsCommands.Size = New-Object System.Drawing.Size(708, 486)
$script:splitBmsCommands.Anchor = 'Top, Bottom, Left, Right'
$script:splitBmsCommands.SplitterDistance = 210
$script:splitBmsCommands.Panel1MinSize = 190
$script:splitBmsCommands.Panel2MinSize = 440
$tabBmsCommands.Controls.Add($script:splitBmsCommands)

$script:splitBmsCommands.Panel1.Controls.Add((New-Label -Text 'Command category' -X 10 -Y 12 -Width 150))
$script:cboBmsCommandCategoryFilter = New-Object System.Windows.Forms.ComboBox
$script:cboBmsCommandCategoryFilter.Location = New-Object System.Drawing.Point(10, 38)
$script:cboBmsCommandCategoryFilter.Size = New-Object System.Drawing.Size(186, 24)
$script:cboBmsCommandCategoryFilter.DropDownStyle = 'DropDownList'
$script:cboBmsCommandCategoryFilter.Add_SelectedIndexChanged({
    Refresh-BmsCommandList
    Refresh-BmsCommandButtons
})
$script:splitBmsCommands.Panel1.Controls.Add($script:cboBmsCommandCategoryFilter)

$script:splitBmsCommands.Panel1.Controls.Add((New-Label -Text 'Saved commands' -X 10 -Y 76 -Width 150))
$script:lstBmsCommandItems = New-Object System.Windows.Forms.ListBox
$script:lstBmsCommandItems.Location = New-Object System.Drawing.Point(10, 102)
$script:lstBmsCommandItems.Size = New-Object System.Drawing.Size(186, 322)
$script:lstBmsCommandItems.Font = $fontMain
$script:lstBmsCommandItems.Add_SelectedIndexChanged({ Select-BmsCommandItem })
$script:splitBmsCommands.Panel1.Controls.Add($script:lstBmsCommandItems)

$script:splitBmsCommands.Panel1.Controls.Add((New-Button -Text 'Refresh' -OnClick { Refresh-BmsCommandsUi } -X 10 -Y 436 -Width 88 -Height 30 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))
$script:splitBmsCommands.Panel1.Controls.Add((New-Button -Text 'Run Selected' -OnClick { Run-SelectedBmsCommand } -X 108 -Y 436 -Width 88 -Height 30 -BackColor ([System.Drawing.Color]::FromArgb(45, 130, 80)) -HoverColor ([System.Drawing.Color]::FromArgb(35, 102, 62))))

$script:tabsBmsCommandModes = New-Object System.Windows.Forms.TabControl
$script:tabsBmsCommandModes.Dock = 'Fill'
$script:splitBmsCommands.Panel2.Controls.Add($script:tabsBmsCommandModes)

$script:tabBmsCommandRun = New-Object System.Windows.Forms.TabPage
$script:tabBmsCommandRun.Text = 'Run Commands'
$script:tabBmsCommandRun.BackColor = $colorPanel
[void]$script:tabsBmsCommandModes.TabPages.Add($script:tabBmsCommandRun)

$script:lblBmsCommandName = New-Object System.Windows.Forms.Label
$script:lblBmsCommandName.Text = 'Command:'
$script:lblBmsCommandName.Location = New-Object System.Drawing.Point(14, 14)
$script:lblBmsCommandName.Size = New-Object System.Drawing.Size(460, 22)
$script:lblBmsCommandName.ForeColor = $colorText
$script:lblBmsCommandName.Font = $fontSection
$script:tabBmsCommandRun.Controls.Add($script:lblBmsCommandName)

$script:lblBmsCommandMeta = New-Object System.Windows.Forms.Label
$script:lblBmsCommandMeta.Text = 'Category: | Type: | Admin: | Confirm:'
$script:lblBmsCommandMeta.Location = New-Object System.Drawing.Point(14, 40)
$script:lblBmsCommandMeta.Size = New-Object System.Drawing.Size(460, 22)
$script:lblBmsCommandMeta.ForeColor = $colorMuted
$script:lblBmsCommandMeta | Add-Member -NotePropertyName ThemeTone -NotePropertyValue 'Muted' -Force
$script:tabBmsCommandRun.Controls.Add($script:lblBmsCommandMeta)

$script:tabBmsCommandRun.Controls.Add((New-Label -Text 'Description' -X 14 -Y 72 -Width 160))
$script:txtBmsCommandDescriptionView = New-Object System.Windows.Forms.TextBox
$script:txtBmsCommandDescriptionView.Location = New-Object System.Drawing.Point(14, 96)
$script:txtBmsCommandDescriptionView.Size = New-Object System.Drawing.Size(458, 72)
$script:txtBmsCommandDescriptionView.Multiline = $true
$script:txtBmsCommandDescriptionView.ScrollBars = 'Vertical'
$script:txtBmsCommandDescriptionView.Font = $fontMain
$script:txtBmsCommandDescriptionView.ReadOnly = $true
$script:txtBmsCommandDescriptionView.BackColor = [System.Drawing.Color]::White
$script:tabBmsCommandRun.Controls.Add($script:txtBmsCommandDescriptionView)

$script:tabBmsCommandRun.Controls.Add((New-Label -Text 'Command buttons' -X 14 -Y 180 -Width 160))
$script:pnlBmsCommandButtons = New-Object System.Windows.Forms.FlowLayoutPanel
$script:pnlBmsCommandButtons.Location = New-Object System.Drawing.Point(14, 204)
$script:pnlBmsCommandButtons.Size = New-Object System.Drawing.Size(458, 116)
$script:pnlBmsCommandButtons.Anchor = 'Top, Left, Right'
$script:pnlBmsCommandButtons.AutoScroll = $true
$script:pnlBmsCommandButtons.WrapContents = $true
$script:tabBmsCommandRun.Controls.Add($script:pnlBmsCommandButtons)

$script:btnRunSelectedBmsCommand = New-Button -Text 'Run Selected Command' -OnClick { Run-SelectedBmsCommand } -X 14 -Y 330 -Width 170 -Height 34 -BackColor ([System.Drawing.Color]::FromArgb(45, 130, 80)) -HoverColor ([System.Drawing.Color]::FromArgb(35, 102, 62))
$script:tabBmsCommandRun.Controls.Add($script:btnRunSelectedBmsCommand)
$script:tabBmsCommandRun.Controls.Add((New-Button -Text 'Refresh Commands' -OnClick { Refresh-BmsCommandsUi } -X 196 -Y 330 -Width 140 -Height 34 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))

$script:tabBmsCommandRun.Controls.Add((New-Label -Text 'Command preview' -X 14 -Y 376 -Width 160))
$script:txtBmsCommandPreview = New-Object System.Windows.Forms.TextBox
$script:txtBmsCommandPreview.Location = New-Object System.Drawing.Point(14, 400)
$script:txtBmsCommandPreview.Size = New-Object System.Drawing.Size(458, 82)
$script:txtBmsCommandPreview.Multiline = $true
$script:txtBmsCommandPreview.ScrollBars = 'Vertical'
$script:txtBmsCommandPreview.Font = $fontMain
$script:txtBmsCommandPreview.ReadOnly = $true
$script:txtBmsCommandPreview.BackColor = [System.Drawing.Color]::White
$script:tabBmsCommandRun.Controls.Add($script:txtBmsCommandPreview)

$script:tabBmsCommandEditor = New-Object System.Windows.Forms.TabPage
$script:tabBmsCommandEditor.Text = 'Command Editor'
$script:tabBmsCommandEditor.BackColor = $colorPanel

$script:pnlBmsCommandEditor = New-Object System.Windows.Forms.Panel
$script:pnlBmsCommandEditor.Dock = 'Fill'
$script:pnlBmsCommandEditor.AutoScroll = $true
$script:tabBmsCommandEditor.Controls.Add($script:pnlBmsCommandEditor)

$script:pnlBmsCommandEditor.Controls.Add((New-Label -Text 'Button name' -X 14 -Y 16 -Width 160))
$script:txtBmsCommandName = New-TextBox -X 14 -Y 40 -Width 214 -Text ''
$script:pnlBmsCommandEditor.Controls.Add($script:txtBmsCommandName)
$script:btnNewBmsCommand = New-Button -Text 'New' -OnClick { New-BmsCommandItem } -X 242 -Y 36 -Width 70
$script:pnlBmsCommandEditor.Controls.Add($script:btnNewBmsCommand)
$script:btnSaveBmsCommand = New-Button -Text 'Save' -OnClick { Save-BmsCommandItem } -X 318 -Y 36 -Width 70
$script:pnlBmsCommandEditor.Controls.Add($script:btnSaveBmsCommand)
$script:btnDeleteBmsCommand = New-Button -Text 'Delete' -OnClick { Delete-BmsCommandItem } -X 394 -Y 36 -Width 78 -BackColor ([System.Drawing.Color]::FromArgb(95, 105, 120)) -HoverColor ([System.Drawing.Color]::FromArgb(70, 80, 95))
$script:pnlBmsCommandEditor.Controls.Add($script:btnDeleteBmsCommand)

$script:pnlBmsCommandEditor.Controls.Add((New-Label -Text 'Category' -X 14 -Y 86 -Width 160))
$script:txtBmsCommandCategory = New-TextBox -X 14 -Y 110 -Width 214 -Text 'General'
$script:pnlBmsCommandEditor.Controls.Add($script:txtBmsCommandCategory)

$script:pnlBmsCommandEditor.Controls.Add((New-Label -Text 'Command type' -X 242 -Y 86 -Width 120))
$script:cboBmsCommandType = New-Object System.Windows.Forms.ComboBox
$script:cboBmsCommandType.Location = New-Object System.Drawing.Point(242, 110)
$script:cboBmsCommandType.Size = New-Object System.Drawing.Size(130, 24)
$script:cboBmsCommandType.DropDownStyle = 'DropDownList'
[void]$script:cboBmsCommandType.Items.Add('PowerShell')
[void]$script:cboBmsCommandType.Items.Add('CMD')
[void]$script:cboBmsCommandType.Items.Add('Open')
$script:cboBmsCommandType.SelectedIndex = 0
$script:pnlBmsCommandEditor.Controls.Add($script:cboBmsCommandType)

$script:pnlBmsCommandEditor.Controls.Add((New-Label -Text 'Description for technicians' -X 14 -Y 154 -Width 220))
$script:txtBmsCommandDescription = New-Object System.Windows.Forms.TextBox
$script:txtBmsCommandDescription.Location = New-Object System.Drawing.Point(14, 178)
$script:txtBmsCommandDescription.Size = New-Object System.Drawing.Size(458, 92)
$script:txtBmsCommandDescription.Multiline = $true
$script:txtBmsCommandDescription.ScrollBars = 'Vertical'
$script:txtBmsCommandDescription.Font = $fontMain
$script:pnlBmsCommandEditor.Controls.Add($script:txtBmsCommandDescription)

$script:pnlBmsCommandEditor.Controls.Add((New-Label -Text 'Command or target' -X 14 -Y 286 -Width 180))
$script:txtBmsCommandText = New-Object System.Windows.Forms.TextBox
$script:txtBmsCommandText.Location = New-Object System.Drawing.Point(14, 310)
$script:txtBmsCommandText.Size = New-Object System.Drawing.Size(458, 118)
$script:txtBmsCommandText.Multiline = $true
$script:txtBmsCommandText.ScrollBars = 'Vertical'
$script:txtBmsCommandText.Font = $fontMain
$script:pnlBmsCommandEditor.Controls.Add($script:txtBmsCommandText)

$script:chkBmsCommandRequiresAdmin = New-Object System.Windows.Forms.CheckBox
$script:chkBmsCommandRequiresAdmin.Text = 'Requires admin'
$script:chkBmsCommandRequiresAdmin.Location = New-Object System.Drawing.Point(14, 442)
$script:chkBmsCommandRequiresAdmin.Size = New-Object System.Drawing.Size(130, 24)
$script:chkBmsCommandRequiresAdmin.ForeColor = $colorText
$script:pnlBmsCommandEditor.Controls.Add($script:chkBmsCommandRequiresAdmin)

$script:chkBmsCommandConfirm = New-Object System.Windows.Forms.CheckBox
$script:chkBmsCommandConfirm.Text = 'Confirm before run'
$script:chkBmsCommandConfirm.Location = New-Object System.Drawing.Point(156, 442)
$script:chkBmsCommandConfirm.Size = New-Object System.Drawing.Size(150, 24)
$script:chkBmsCommandConfirm.Checked = $true
$script:chkBmsCommandConfirm.ForeColor = $colorText
$script:pnlBmsCommandEditor.Controls.Add($script:chkBmsCommandConfirm)

$script:chkBmsCommandKeepWindowOpen = New-Object System.Windows.Forms.CheckBox
$script:chkBmsCommandKeepWindowOpen.Text = 'Keep terminal open'
$script:chkBmsCommandKeepWindowOpen.Location = New-Object System.Drawing.Point(316, 442)
$script:chkBmsCommandKeepWindowOpen.Size = New-Object System.Drawing.Size(156, 24)
$script:chkBmsCommandKeepWindowOpen.Checked = $true
$script:chkBmsCommandKeepWindowOpen.ForeColor = $colorText
$script:pnlBmsCommandEditor.Controls.Add($script:chkBmsCommandKeepWindowOpen)

$script:pnlBmsCommandEditor.Controls.Add((New-Label -Text 'Use PowerShell for scripts, CMD for command lines, and Open for URLs, files, or consoles.' -X 14 -Y 474 -Width 458))

# Important links tab
$tabLinks = New-Object System.Windows.Forms.TabPage
$tabLinks.Text = 'Important Links'
$tabLinks.BackColor = $colorPanel
$script:tabLinks = $tabLinks
$tabs.TabPages.Add($tabLinks)

$tabLinks.Controls.Add((New-Label -Text 'Saved important links' -X 18 -Y 18 -Width 180))
$script:lstImportantLinks = New-Object System.Windows.Forms.ListBox
$script:lstImportantLinks.Location = New-Object System.Drawing.Point(18, 44)
$script:lstImportantLinks.Size = New-Object System.Drawing.Size(260, 378)
$script:lstImportantLinks.Font = $fontMain
$script:lstImportantLinks.Add_SelectedIndexChanged({ Load-LinkItem })
$tabLinks.Controls.Add($script:lstImportantLinks)

$script:btnOpenLink = New-Button -Text 'Open' -OnClick { Open-LinkItem } -X 18 -Y 434 -Width 80 -BackColor ([System.Drawing.Color]::FromArgb(45, 130, 80)) -HoverColor ([System.Drawing.Color]::FromArgb(35, 102, 62))
$tabLinks.Controls.Add($script:btnOpenLink)
$script:btnNewLink = New-Button -Text 'New' -OnClick { New-LinkItem } -X 108 -Y 434 -Width 80
$tabLinks.Controls.Add($script:btnNewLink)
$script:btnDeleteLink = New-Button -Text 'Delete' -OnClick { Delete-LinkItem } -X 198 -Y 434 -Width 80 -BackColor ([System.Drawing.Color]::FromArgb(95, 105, 120)) -HoverColor ([System.Drawing.Color]::FromArgb(70, 80, 95))
$tabLinks.Controls.Add($script:btnDeleteLink)

$tabLinks.Controls.Add((New-Label -Text 'Category' -X 314 -Y 18 -Width 140))
$script:txtLinkCategory = New-TextBox -X 314 -Y 42 -Width 200 -Text 'General'
$tabLinks.Controls.Add($script:txtLinkCategory)

$tabLinks.Controls.Add((New-Label -Text 'Title' -X 314 -Y 84 -Width 140))
$script:txtLinkTitle = New-TextBox -X 314 -Y 108 -Width 392 -Text ''
$tabLinks.Controls.Add($script:txtLinkTitle)

$tabLinks.Controls.Add((New-Label -Text 'URL or file path' -X 314 -Y 150 -Width 180))
$script:txtLinkTarget = New-TextBox -X 314 -Y 174 -Width 392 -Text ''
$tabLinks.Controls.Add($script:txtLinkTarget)

$tabLinks.Controls.Add((New-Label -Text 'Notes' -X 314 -Y 216 -Width 140))
$script:txtLinkNotes = New-Object System.Windows.Forms.TextBox
$script:txtLinkNotes.Location = New-Object System.Drawing.Point(314, 240)
$script:txtLinkNotes.Size = New-Object System.Drawing.Size(392, 182)
$script:txtLinkNotes.Multiline = $true
$script:txtLinkNotes.ScrollBars = 'Vertical'
$script:txtLinkNotes.Font = $fontMain
$tabLinks.Controls.Add($script:txtLinkNotes)

$script:btnSaveLink = New-Button -Text 'Save Link' -OnClick { Save-LinkItem } -X 314 -Y 434 -Width 150
$tabLinks.Controls.Add($script:btnSaveLink)

# AI Assistant tab
$tabAi = New-Object System.Windows.Forms.TabPage
$tabAi.Text = 'AI Assistant'
$tabAi.BackColor = $colorPanel
$tabAi.AutoScroll = $true
$script:tabAi = $tabAi
$tabs.TabPages.Add($tabAi)

$lblAiTitle = New-Object System.Windows.Forms.Label
$lblAiTitle.Text = 'Online Troubleshooting'
$lblAiTitle.Location = New-Object System.Drawing.Point(18, 16)
$lblAiTitle.Size = New-Object System.Drawing.Size(220, 24)
$lblAiTitle.Font = $fontSection
$lblAiTitle.ForeColor = $colorText
$tabAi.Controls.Add($lblAiTitle)

$lblAiInfo = New-Object System.Windows.Forms.Label
$lblAiInfo.Text = 'Type an issue and let the toolkit search online through your backend, then return an explanation, fix steps, commands to try, and source links.'
$lblAiInfo.Location = New-Object System.Drawing.Point(18, 44)
$lblAiInfo.Size = New-Object System.Drawing.Size(688, 22)
$lblAiInfo.ForeColor = $colorMuted
$lblAiInfo | Add-Member -NotePropertyName ThemeTone -NotePropertyValue 'Muted' -Force
$tabAi.Controls.Add($lblAiInfo)

$script:lblAiKeyStatus = New-Object System.Windows.Forms.Label
$script:lblAiKeyStatus.Text = 'Ask a question for online troubleshooting, or search your saved toolkit knowledge.'
$script:lblAiKeyStatus.Location = New-Object System.Drawing.Point(18, 68)
$script:lblAiKeyStatus.Size = New-Object System.Drawing.Size(520, 18)
$script:lblAiKeyStatus.ForeColor = $colorMuted
$script:lblAiKeyStatus | Add-Member -NotePropertyName ThemeTone -NotePropertyValue 'Muted' -Force
$tabAi.Controls.Add($script:lblAiKeyStatus)

$script:lblAiAdminHint = New-Object System.Windows.Forms.Label
$script:lblAiAdminHint.Text = 'Service: Checking online troubleshooting backend...'
$script:lblAiAdminHint.Location = New-Object System.Drawing.Point(490, 68)
$script:lblAiAdminHint.Size = New-Object System.Drawing.Size(216, 18)
$script:lblAiAdminHint.TextAlign = 'MiddleRight'
$script:lblAiAdminHint.ForeColor = $colorMuted
$script:lblAiAdminHint | Add-Member -NotePropertyName ThemeTone -NotePropertyValue 'Muted' -Force
$tabAi.Controls.Add($script:lblAiAdminHint)

$script:txtAiConversation = New-Object System.Windows.Forms.TextBox
$script:txtAiConversation.Location = New-Object System.Drawing.Point(18, 94)
$script:txtAiConversation.Size = New-Object System.Drawing.Size(688, 190)
$script:txtAiConversation.Multiline = $true
$script:txtAiConversation.ScrollBars = 'Vertical'
$script:txtAiConversation.Font = $fontMain
$script:txtAiConversation.ReadOnly = $true
$script:txtAiConversation.BackColor = [System.Drawing.Color]::White
$tabAi.Controls.Add($script:txtAiConversation)

$tabAi.Controls.Add((New-Label -Text 'Troubleshooting question' -X 18 -Y 296 -Width 180))
$script:txtAiPrompt = New-Object System.Windows.Forms.TextBox
$script:txtAiPrompt.Location = New-Object System.Drawing.Point(18, 320)
$script:txtAiPrompt.Size = New-Object System.Drawing.Size(300, 26)
$script:txtAiPrompt.Font = $fontMain
$tabAi.Controls.Add($script:txtAiPrompt)
$tabAi.Controls.Add((New-Button -Text 'Ask Assistant' -OnClick { Ask-AiCopilot } -X 330 -Y 316 -Width 108))
$tabAi.Controls.Add((New-Button -Text 'Check Service' -OnClick { Refresh-OnlineTroubleshootingStatus } -X 446 -Y 316 -Width 118 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))
$tabAi.Controls.Add((New-Button -Text 'Open Web Search' -OnClick { Open-AiGoogleSearch -Query $script:txtAiPrompt.Text.Trim() } -X 572 -Y 316 -Width 134 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))

$tabAi.Controls.Add((New-Label -Text 'Quick prompts' -X 18 -Y 360 -Width 120))
$tabAi.Controls.Add((New-Button -Text 'Ping / reachability' -OnClick { Ask-AiQuick -Prompt 'ping server unreachable' } -X 18 -Y 386 -Width 150 -Height 34 -BackColor ([System.Drawing.Color]::FromArgb(45, 130, 80)) -HoverColor ([System.Drawing.Color]::FromArgb(35, 102, 62))))
$tabAi.Controls.Add((New-Button -Text 'IP profiles' -OnClick { Ask-AiQuick -Prompt 'ip profile adapter gateway dns' } -X 178 -Y 386 -Width 120 -Height 34))
$tabAi.Controls.Add((New-Button -Text 'Current BMS topic' -OnClick { Ask-AiQuick -Prompt 'current bms topic' } -X 308 -Y 386 -Width 150 -Height 34))
$tabAi.Controls.Add((New-Button -Text 'Recent log' -OnClick { Ask-AiQuick -Prompt 'recent log' } -X 468 -Y 386 -Width 110 -Height 34 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))
$tabAi.Controls.Add((New-Button -Text 'RDP help' -OnClick { Ask-AiQuick -Prompt 'rdp not working' } -X 588 -Y 386 -Width 90 -Height 34 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))))

# IP Shifter tab
$tabIp = New-Object System.Windows.Forms.TabPage
$tabIp.Text = 'IP Shifter'
$tabIp.BackColor = $colorPanel
$script:tabIp = $tabIp
$tabs.TabPages.Add($tabIp)

$script:lvProfiles = New-Object System.Windows.Forms.ListView
$script:lvProfiles.Location = New-Object System.Drawing.Point(18, 18)
$script:lvProfiles.Size = New-Object System.Drawing.Size(340, 382)
$script:lvProfiles.View = 'Details'
$script:lvProfiles.FullRowSelect = $true
$script:lvProfiles.GridLines = $true
[void]$script:lvProfiles.Columns.Add('Profile', 110)
[void]$script:lvProfiles.Columns.Add('Adapter', 110)
[void]$script:lvProfiles.Columns.Add('IP', 120)
[void]$script:lvProfiles.Columns.Add('Mask', 120)
[void]$script:lvProfiles.Columns.Add('Gateway', 120)
$script:lvProfiles.Add_DoubleClick({ Load-SelectedProfile })
$tabIp.Controls.Add($script:lvProfiles)

$tabIp.Controls.Add((New-Label -Text 'Adapter' -X 380 -Y 18 -Width 140))
$script:cboAdapter = New-Object System.Windows.Forms.ComboBox
$script:cboAdapter.Location = New-Object System.Drawing.Point(380, 42)
$script:cboAdapter.Size = New-Object System.Drawing.Size(330, 24)
$script:cboAdapter.DropDownStyle = 'DropDownList'
$tabIp.Controls.Add($script:cboAdapter)

$tabIp.Controls.Add((New-Button -Text 'Refresh' -OnClick { Refresh-Adapters; Add-Log -Area 'Adapter' -Level 'OK' -Message 'Adapter list refreshed.' } -X 380 -Y 78 -Width 100))
$tabIp.Controls.Add((New-Button -Text 'Details' -OnClick { Show-AdapterDetails } -X 496 -Y 78 -Width 100))

$tabIp.Controls.Add((New-Label -Text 'Profile name' -X 380 -Y 128 -Width 140))
$script:txtProfileName = New-TextBox -X 380 -Y 152 -Width 200 -Text ''
$tabIp.Controls.Add($script:txtProfileName)

$tabIp.Controls.Add((New-Label -Text 'IP address' -X 380 -Y 188 -Width 120))
$script:txtIpAddress = New-TextBox -X 380 -Y 212 -Width 140 -Text ''
$tabIp.Controls.Add($script:txtIpAddress)

$tabIp.Controls.Add((New-Label -Text 'Subnet mask' -X 540 -Y 188 -Width 120))
$script:txtSubnetMask = New-TextBox -X 540 -Y 212 -Width 150 -Text '255.255.255.0'
$tabIp.Controls.Add($script:txtSubnetMask)

$tabIp.Controls.Add((New-Label -Text 'Gateway' -X 380 -Y 248 -Width 120))
$script:txtGateway = New-TextBox -X 380 -Y 272 -Width 140 -Text ''
$tabIp.Controls.Add($script:txtGateway)

$tabIp.Controls.Add((New-Label -Text 'DNS 1' -X 540 -Y 248 -Width 120))
$script:txtDns1 = New-TextBox -X 540 -Y 272 -Width 150 -Text ''
$tabIp.Controls.Add($script:txtDns1)

$tabIp.Controls.Add((New-Label -Text 'DNS 2' -X 380 -Y 308 -Width 120))
$script:txtDns2 = New-TextBox -X 380 -Y 332 -Width 140 -Text ''
$tabIp.Controls.Add($script:txtDns2)

$tabIp.Controls.Add((New-Button -Text 'Load Profile' -OnClick { Load-SelectedProfile } -X 18 -Y 418 -Width 130))
$tabIp.Controls.Add((New-Button -Text 'Save Profile' -OnClick { Save-IpProfile } -X 164 -Y 418 -Width 130))
$tabIp.Controls.Add((New-Button -Text 'Delete Profile' -OnClick { Remove-IpProfile } -X 18 -Y 458 -Width 130 -BackColor ([System.Drawing.Color]::FromArgb(95, 105, 120)) -HoverColor ([System.Drawing.Color]::FromArgb(70, 80, 95))))
$btnApply = New-Button -Text 'Apply Static IP' -OnClick { Apply-StaticIp } -X 380 -Y 418 -Width 150 -BackColor ([System.Drawing.Color]::FromArgb(185, 95, 35)) -HoverColor ([System.Drawing.Color]::FromArgb(150, 70, 25))
$tabIp.Controls.Add($btnApply)
$btnDhcp = New-Button -Text 'Set DHCP' -OnClick { Apply-Dhcp } -X 546 -Y 418 -Width 120 -BackColor ([System.Drawing.Color]::FromArgb(80, 100, 125)) -HoverColor ([System.Drawing.Color]::FromArgb(60, 78, 98))
$tabIp.Controls.Add($btnDhcp)

# IP scanner and field diagnostics
$tabScan = New-Object System.Windows.Forms.TabPage
$tabScan.Text = 'IP Scanner'
$tabScan.BackColor = $colorPanel
$script:tabScan = $tabScan
$tabScan.AutoScroll = $true

$tabScan.Controls.Add((New-Label -Text 'Network adapter' -X 18 -Y 16 -Width 220))
$script:cboScanAdapter = New-Object System.Windows.Forms.ComboBox
$script:cboScanAdapter.Location = New-Object System.Drawing.Point(18, 38)
$script:cboScanAdapter.Size = New-Object System.Drawing.Size(690, 24)
$script:cboScanAdapter.DropDownStyle = 'DropDownList'
$script:cboScanAdapter.Add_SelectedIndexChanged({ Select-ScannerAdapter })
$tabScan.Controls.Add($script:cboScanAdapter)
$script:lblScanScope = New-Label -Text 'Select an adapter to fill the local network range.' -X 18 -Y 68 -Width 690
$tabScan.Controls.Add($script:lblScanScope)
$tabScan.Controls.Add((New-Label -Text 'Start IP' -X 18 -Y 98 -Width 120))
$script:txtScanStart = New-TextBox -X 18 -Y 122 -Width 156 -Text ''
$tabScan.Controls.Add($script:txtScanStart)
$tabScan.Controls.Add((New-Label -Text 'End IP' -X 190 -Y 98 -Width 120))
$script:txtScanEnd = New-TextBox -X 190 -Y 122 -Width 156 -Text ''
$tabScan.Controls.Add($script:txtScanEnd)
$script:btnScan = New-Button -Text 'Scan' -OnClick { Start-LanScan } -X 362 -Y 120 -Width 88
$tabScan.Controls.Add($script:btnScan)
$tabScan.Controls.Add((New-Button -Text 'Stop' -OnClick { Stop-LanScan } -X 458 -Y 120 -Width 74 -BackColor ([System.Drawing.Color]::FromArgb(95, 105, 120)) -HoverColor ([System.Drawing.Color]::FromArgb(70, 80, 95))))
$tabScan.Controls.Add((New-Button -Text 'Export CSV' -OnClick { Invoke-UiAction -Name 'Export IP Scan' -Action { Export-LanScan } } -X 540 -Y 120 -Width 110))
$script:lblScanStatus = New-Label -Text 'Ready | Ping and common TCP services' -X 18 -Y 164 -Width 660
$script:scanProcessed = 0
$tabScan.Controls.Add($script:lblScanStatus)
$script:lvScan = New-Object System.Windows.Forms.ListView
$script:lvScan.Location = New-Object System.Drawing.Point(18, 194)
$script:lvScan.Size = New-Object System.Drawing.Size(690, 326)
$script:lvScan.Anchor = 'Top, Bottom, Left, Right'
$script:lvScan.View = 'Details'
$script:lvScan.FullRowSelect = $true
$script:lvScan.GridLines = $true
[void]$script:lvScan.Columns.Add('IP address', 126)
[void]$script:lvScan.Columns.Add('Hostname', 198)
[void]$script:lvScan.Columns.Add('Ping', 70)
[void]$script:lvScan.Columns.Add('MAC address', 128)
[void]$script:lvScan.Columns.Add('Open TCP ports', 132)
$tabScan.Controls.Add($script:lvScan)
$scanMenu = New-Object System.Windows.Forms.ContextMenuStrip
$scanResultsList = $script:lvScan
foreach ($action in @(
    @{ Text = 'Ping'; Prefix = 'ping.exe' },
    @{ Text = 'Remote Desktop'; Prefix = 'mstsc.exe' },
    @{ Text = 'Open HTTP'; Prefix = 'http://' },
    @{ Text = 'Open HTTPS'; Prefix = 'https://' },
    @{ Text = 'Open Share'; Prefix = '\\' }
)) {
    $item = New-Object System.Windows.Forms.ToolStripMenuItem($action.Text)
    $prefix = [string]$action.Prefix
    $item.Add_Click({
        if ($scanResultsList.SelectedItems.Count -eq 0) { return }
        $ip = $scanResultsList.SelectedItems[0].Text
        if ($prefix -eq 'ping.exe') { Start-Process 'cmd.exe' -ArgumentList @('/k', "ping $ip -t") }
        elseif ($prefix -eq 'mstsc.exe') { Start-Process 'mstsc.exe' -ArgumentList @('/v:' + $ip) }
        else { Start-Process ($prefix + $ip) }
    }.GetNewClosure())
    [void]$scanMenu.Items.Add($item)
}
$script:lvScan.ContextMenuStrip = $scanMenu
$script:scanJob = $null
$script:scanTimer = New-Object System.Windows.Forms.Timer
$script:scanTimer.Interval = 500
$script:scanTimer.Add_Tick({ Complete-LanScan })

$tabNetwork.Controls.Add((New-Label -Text 'VLAN clues' -X 18 -Y 608 -Width 220))
$tabNetwork.Controls.Add((New-Button -Text 'Inspect Selected Adapter' -OnClick { Show-VlanClues } -X 18 -Y 634 -Width 240))
$script:txtVlanClues = New-Object System.Windows.Forms.TextBox
$script:txtVlanClues.Location = New-Object System.Drawing.Point(18, 674)
$script:txtVlanClues.Size = New-Object System.Drawing.Size(688, 112)
$script:txtVlanClues.Multiline = $true
$script:txtVlanClues.ScrollBars = 'Vertical'
$script:txtVlanClues.ReadOnly = $true
$script:txtVlanClues.Text = 'Select an adapter in IP Shifter, then inspect it here. The switch port VLAN must be confirmed at the managed switch.'
$tabNetwork.Controls.Add($script:txtVlanClues)

$tabNetwork.Controls.Add((New-Label -Text 'Support bundle' -X 376 -Y 608 -Width 220))
$script:cboBundleType = New-Object System.Windows.Forms.ComboBox
$script:cboBundleType.Location = New-Object System.Drawing.Point(376, 638)
$script:cboBundleType.Size = New-Object System.Drawing.Size(190, 24)
$script:cboBundleType.DropDownStyle = 'DropDownList'
[void]$script:cboBundleType.Items.Add('Network')
[void]$script:cboBundleType.Items.Add('Windows')
[void]$script:cboBundleType.Items.Add('Both')
$script:cboBundleType.SelectedIndex = 0
$tabNetwork.Controls.Add($script:cboBundleType)
$tabNetwork.Controls.Add((New-Button -Text 'Export ZIP' -OnClick { Export-ToolkitSupportBundle } -X 574 -Y 636 -Width 130))
$tabNetwork.Controls.Add((New-Label -Text 'Includes logs and diagnostic snapshots. Review before sharing.' -X 376 -Y 676 -Width 330))

# Preferred tab order
$tabs.TabPages.Clear()
[void]$tabs.TabPages.Add($tabTrouble)
[void]$tabs.TabPages.Add($tabIp)
[void]$tabs.TabPages.Add($tabNetwork)
[void]$tabs.TabPages.Add($tabScan)
$tabFeedback = New-Object System.Windows.Forms.TabPage
$tabFeedback.Text = 'Feedback'
$tabFeedback.BackColor = $colorPanel
$tabFeedback.AutoScroll = $true
$tabFeedback.Controls.Add((New-Label -Text 'Feedback for TEC Systems IT' -X 18 -Y 22 -Width 300))
$tabFeedback.Controls.Add((New-Label -Text 'Category' -X 18 -Y 64 -Width 130))
$script:cboFeedbackCategory = New-Object System.Windows.Forms.ComboBox
$script:cboFeedbackCategory.Location = New-Object System.Drawing.Point(18, 88)
$script:cboFeedbackCategory.Size = New-Object System.Drawing.Size(240, 24)
$script:cboFeedbackCategory.DropDownStyle = 'DropDownList'
foreach ($name in @('Idea', 'Bug', 'Usability', 'Other')) { [void]$script:cboFeedbackCategory.Items.Add($name) }
$script:cboFeedbackCategory.SelectedIndex = 0
$tabFeedback.Controls.Add($script:cboFeedbackCategory)
$tabFeedback.Controls.Add((New-Label -Text 'Your feedback' -X 18 -Y 132 -Width 180))
$script:txtFeedback = New-Object System.Windows.Forms.TextBox
$script:txtFeedback.Location = New-Object System.Drawing.Point(18, 158)
$script:txtFeedback.Size = New-Object System.Drawing.Size(680, 160)
$script:txtFeedback.Multiline = $true
$script:txtFeedback.ScrollBars = 'Vertical'
$script:txtFeedback.Anchor = 'Top, Left, Right'
$tabFeedback.Controls.Add($script:txtFeedback)
$tabFeedback.Controls.Add((New-Label -Text 'Screenshot (optional)' -X 18 -Y 334 -Width 220))
$script:txtFeedbackScreenshot = New-TextBox -X 18 -Y 360 -Width 484 -Text ''
$script:txtFeedbackScreenshot.ReadOnly = $true
$tabFeedback.Controls.Add($script:txtFeedbackScreenshot)
$tabFeedback.Controls.Add((New-Button -Text 'Choose File' -OnClick { Select-FeedbackScreenshot } -X 510 -Y 356 -Width 92))
$tabFeedback.Controls.Add((New-Button -Text 'Capture' -OnClick { Capture-Screenshot } -X 610 -Y 356 -Width 88))
$tabFeedback.Controls.Add((New-Button -Text 'Clear Screenshot' -OnClick { $script:txtFeedbackScreenshot.Clear() } -X 18 -Y 396 -Width 140 -BackColor ([System.Drawing.Color]::FromArgb(95, 105, 120)) -HoverColor ([System.Drawing.Color]::FromArgb(70, 80, 95))))
$tabFeedback.Controls.Add((New-Label -Text 'Classic Outlook attaches the image. Other mail apps open a draft; attach it there before sending.' -X 18 -Y 442 -Width 680))
$tabFeedback.Controls.Add((New-Button -Text 'Send Feedback' -OnClick { Submit-ToolkitFeedback } -X 18 -Y 474 -Width 156 -BackColor ([System.Drawing.Color]::FromArgb(45, 130, 80)) -HoverColor ([System.Drawing.Color]::FromArgb(35, 102, 62))))
[void]$tabs.TabPages.Add($tabFeedback)
$tabs.Add_SelectedIndexChanged({
    if ($tabs.SelectedTab -eq $tabNetwork) {
        $tabNetwork.AutoScrollPosition = New-Object System.Drawing.Point(0, 0)
    }
})

# Always-visible log side panel
$lblLogTitle = New-Object System.Windows.Forms.Label
$lblLogTitle.Text = 'Technician Log'
$lblLogTitle.Location = New-Object System.Drawing.Point(14, 14)
$lblLogTitle.Size = New-Object System.Drawing.Size(220, 24)
$lblLogTitle.Font = $fontSection
$lblLogTitle.ForeColor = $colorText
$script:rightPanel.Controls.Add($lblLogTitle)

$script:lvLog = New-Object System.Windows.Forms.ListView
$script:lvLog.Location = New-Object System.Drawing.Point(14, 46)
$script:lvLog.Size = New-Object System.Drawing.Size(334, 454)
$script:lvLog.Anchor = 'Top, Bottom, Left, Right'
$script:lvLog.View = 'Details'
$script:lvLog.FullRowSelect = $true
$script:lvLog.GridLines = $true
[void]$script:lvLog.Columns.Add('Time', 70)
[void]$script:lvLog.Columns.Add('Level', 60)
[void]$script:lvLog.Columns.Add('Area', 100)
[void]$script:lvLog.Columns.Add('Message', 520)
$script:rightPanel.Controls.Add($script:lvLog)

$script:pnlLogActions = New-Object System.Windows.Forms.Panel
$script:pnlLogActions.Location = New-Object System.Drawing.Point(10, 510)
$script:pnlLogActions.Size = New-Object System.Drawing.Size(340, 40)
$script:pnlLogActions.Anchor = 'Left, Right, Bottom'
$script:rightPanel.Controls.Add($script:pnlLogActions)

$script:pnlLogActions.Controls.Add((New-Button -Text 'Copy Path' -OnClick { [System.Windows.Forms.Clipboard]::SetText($script:LogFile); Add-Log -Area 'Log' -Level 'OK' -Message 'Copied log path.' } -X 0 -Y 2 -Width 96 -Height 30))
$script:btnExportLog = New-Button -Text 'Export Log' -OnClick { Export-ToolkitLog } -X 104 -Y 2 -Width 104 -Height 30 -BackColor ([System.Drawing.Color]::FromArgb(45, 130, 80)) -HoverColor ([System.Drawing.Color]::FromArgb(35, 102, 62))
$script:pnlLogActions.Controls.Add($script:btnExportLog)
$script:btnClearLog = New-Button -Text 'Clear Log' -OnClick { Clear-VisibleLog } -X 216 -Y 2 -Width 104 -Height 30 -BackColor ([System.Drawing.Color]::FromArgb(95, 105, 120)) -HoverColor ([System.Drawing.Color]::FromArgb(70, 80, 95))
$script:pnlLogActions.Controls.Add($script:btnClearLog)

# Footer
$footer = New-Object System.Windows.Forms.Panel
$footer.Location = New-Object System.Drawing.Point(18, 698)
$footer.Size = New-Object System.Drawing.Size(1226, 34)
$footer.Anchor = 'Bottom, Left, Right'
$footer.BackColor = $colorBackground
$form.Controls.Add($footer)

$script:lblStatus = New-Object System.Windows.Forms.Label
$script:lblStatus.Text = 'Ready'
$script:lblStatus.Location = New-Object System.Drawing.Point(0, 6)
$script:lblStatus.Size = New-Object System.Drawing.Size(300, 22)
$script:lblStatus.AutoEllipsis = $true
$script:lblStatus.ForeColor = [System.Drawing.Color]::FromArgb(45, 130, 80)
$footer.Controls.Add($script:lblStatus)

$script:btnToolkitUpdates = New-Button -Text 'Check Updates' -OnClick { Check-ToolkitUpdates } -X 320 -Y 2 -Width 132 -Height 30
$footer.Controls.Add($script:btnToolkitUpdates)
$script:toolTip.SetToolTip($script:btnToolkitUpdates, 'Check GitHub Releases for a newer toolkit installer.')

$script:progress = New-Object System.Windows.Forms.ProgressBar
$script:progress.Location = New-Object System.Drawing.Point(640, 8)
$script:progress.Size = New-Object System.Drawing.Size(160, 18)
$script:progress.Anchor = 'Top, Right'
$script:progress.Style = 'Blocks'
$footer.Controls.Add($script:progress)

$script:lblInternet = New-Object System.Windows.Forms.Label
$script:lblInternet.Text = 'Internet: Checking'
$script:lblInternet.Location = New-Object System.Drawing.Point(810, 4)
$script:lblInternet.Size = New-Object System.Drawing.Size(416, 24)
$script:lblInternet.Anchor = 'Top, Right'
$script:lblInternet.TextAlign = 'MiddleRight'
$script:lblInternet.Font = $fontSection
$script:lblInternet.ForeColor = [System.Drawing.Color]::FromArgb(95, 105, 120)
$footer.Controls.Add($script:lblInternet)

$script:internetTimer = New-Object System.Windows.Forms.Timer
$script:internetTimer.Interval = 30000
$script:internetTimer.Add_Tick({ Update-InternetStatus })

$script:adminStatusTimer = New-Object System.Windows.Forms.Timer
$script:adminStatusTimer.Interval = 1000
$script:adminStatusTimer.Add_Tick({
    if (-not $script:IsAdminMode) {
        Update-AdminModeUi
    }
})

$script:statusResetTimer = New-Object System.Windows.Forms.Timer
$script:statusResetTimer.Interval = 3500
$script:statusResetTimer.Add_Tick({
    $script:statusResetTimer.Stop()
    Set-MainStatus -Text 'Ready' -Color ([System.Drawing.Color]::FromArgb(45, 130, 80))
})

$script:updateTimer = New-Object System.Windows.Forms.Timer
$script:updateTimer.Interval = 500
$script:updateTimer.Add_Tick({ Complete-ToolkitUpdateCheck })
$script:updateJob = $null

# -------------------------------
# Start Application
# -------------------------------
$form.Add_Shown({
    $script:trayIcon.Visible = $true
    Refresh-Adapters
    Refresh-Profiles
    Restore-PendingNetworkDraft
    Update-AdminModeUi
    Apply-Theme -DarkMode ([bool]$script:Config.DarkMode)
    Apply-TroubleshootingButtonToolTips
    Add-Log -Area 'Startup' -Level 'OK' -Message $script:AppName
    Add-Log -Area 'Startup' -Level 'INFO' -Message $script:ManagedBy
    Add-Log -Area 'Startup' -Level 'INFO' -Message ('Hostname: {0}' -f $env:COMPUTERNAME)
    Add-Log -Area 'Startup' -Level 'INFO' -Message ('Log file: {0}' -f $script:LogFile)
    Update-InternetStatus
    $script:internetTimer.Start()
    $script:adminStatusTimer.Start()
    Start-ToolkitUpdateCheck
})

$form.Add_FormClosing({
    param($sender, $eventArgs)
    if (-not $script:ExitRequested -and $eventArgs.CloseReason -eq [System.Windows.Forms.CloseReason]::UserClosing) {
        $eventArgs.Cancel = $true
        $script:form.Hide()
        return
    }
    if ($script:trayIcon) { $script:trayIcon.Visible = $false; $script:trayIcon.Dispose() }
    if ($script:trayMenu) { $script:trayMenu.Dispose() }
    if ($script:internetTimer) {
        $script:internetTimer.Stop()
        $script:internetTimer.Dispose()
    }
    if ($script:adminStatusTimer) {
        $script:adminStatusTimer.Stop()
        $script:adminStatusTimer.Dispose()
    }
    if ($script:statusResetTimer) {
        $script:statusResetTimer.Stop()
        $script:statusResetTimer.Dispose()
    }
    if ($script:updateTimer) { $script:updateTimer.Stop(); $script:updateTimer.Dispose() }
    if ($script:updateJob) { Stop-Job -Job $script:updateJob -ErrorAction SilentlyContinue; Remove-Job -Job $script:updateJob -Force -ErrorAction SilentlyContinue; $script:updateJob = $null }
    if ($script:scanTimer) { Stop-LanScan; $script:scanTimer.Dispose() }
})

if (-not $TestMode) { [System.Windows.Forms.Application]::Run($form) }
