<#
.SYNOPSIS
  Installs the IIS Control Center agent on this server as a Windows service.

.DESCRIPTION
  What it does:
    1. Checks that you are Administrator and that the settings are safe.
    2. Copies the agent to the install folder (and checks its SHA-256 if you give one).
    3. Locks down the data folder (only Administrators and SYSTEM).
    4. Registers the agent with the portal using the one-time token. The agent creates its own
       client certificate; the private key never leaves this server.
    5. Creates the Windows service under a dedicated account, in the Administrators group.
    6. Starts the service. The server then shows as "Pending" in the portal until an admin approves it.

  The Environment and AllowedRoots you pass are saved ON THIS SERVER. The portal cannot change them.
  To change them later, run this script again with a new token.

  Service account:
    Domain server      : -Gmsa 'CORP\svc-icc-prod$'   (use a different gMSA per environment)
    Non-domain / DMZ   : no account parameter. A local account 'svc-icc-<environment>' is created with
                         a random password that is never shown or stored by this script.

.EXAMPLE
  .\Install-Agent.ps1 -PortalUrl https://agents.portal.example.local -Token <token> -Environment Prod `
      -AllowedRoots 'D:\Websites' -AgentExePath .\IisControlCenter.Agent.exe -Gmsa 'CORP\svc-icc-prod$'

.EXAMPLE
  # Check everything without changing anything:
  .\Install-Agent.ps1 -PortalUrl https://agents.portal.example.local -Token x -Environment Dev `
      -AllowedRoots 'D:\Websites' -AgentExePath .\IisControlCenter.Agent.exe -ValidateOnly

.NOTES
  Works on Windows PowerShell 5.1 and PowerShell 7. Script version 0.1 (Phase 1).
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Install')]
    [string]$PortalUrl,

    [Parameter(Mandatory, ParameterSetName = 'Install')]
    [string]$Token,

    [Parameter(Mandatory, ParameterSetName = 'Install')]
    [ValidateSet('Dev', 'Staging', 'Prod')]
    [string]$Environment,

    [Parameter(Mandatory, ParameterSetName = 'Install')]
    [string[]]$AllowedRoots,

    [Parameter(Mandatory, ParameterSetName = 'Install')]
    [string]$AgentExePath,

    [Parameter(ParameterSetName = 'Install')]
    [string]$ExpectedSha256,

    [Parameter(ParameterSetName = 'Install')]
    [string]$Gmsa,

    [Parameter(ParameterSetName = 'Install')]
    [switch]$AllowManaged,

    [Parameter(ParameterSetName = 'Install')]
    [switch]$ValidateOnly,

    [Parameter(ParameterSetName = 'Install')]
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'IisControlCenter\Agent'),

    [Parameter(ParameterSetName = 'Install')]
    [string]$DataDir = (Join-Path $env:ProgramData 'IisControlCenter'),

    [Parameter(Mandatory, ParameterSetName = 'Uninstall')]
    [switch]$Uninstall
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$ServiceName = 'IisControlCenterAgent'

function Write-Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Fail([string]$m) { throw "Install-Agent: $m" }

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Same rules as the agent itself (PathGuard). The agent checks again when it registers and when it starts.
function Test-Root([string]$Root) {
    if ($Root -notmatch '^[A-Za-z]:\\') { return "must be an absolute local path like D:\Websites" }
    if ($Root -match '(^|\\)\.\.(\\|$)') { return "must not contain .." }
    if ($Root.IndexOf(':', 2) -ge 0) { return "must not contain a second colon" }
    $n = $Root.TrimEnd('\')
    if ($n.Length -le 2) { return "a whole drive is not allowed" }
    $after = $n.Substring(2).ToLowerInvariant()
    foreach ($f in '\windows', '\program files', '\program files (x86)', '\programdata', '\users', '\system volume information') {
        if ($after -eq $f -or $after.StartsWith($f + '\')) { return "system folders are not allowed" }
    }
    return $null
}

function Grant-LogonAsService([string]$Account) {
    # Adds SeServiceLogonRight (and denies interactive logon) using secedit. Used for the local account only.
    $sid = (New-Object Security.Principal.NTAccount($Account)).Translate([Security.Principal.SecurityIdentifier]).Value
    $tmp = Join-Path $env:TEMP ("icc-sec-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tmp | Out-Null
    try {
        $inf = Join-Path $tmp 'in.inf'; $db = Join-Path $tmp 'sec.sdb'
        & secedit.exe /export /cfg $inf /areas USER_RIGHTS | Out-Null
        $lines = Get-Content $inf
        function Add-Right([string[]]$Lines, [string]$Right, [string]$Sid) {
            $found = $false
            $out = foreach ($l in $Lines) {
                if ($l -match "^$Right\s*=") { $found = $true; if ($l -notmatch [regex]::Escape($Sid)) { "$l,*$Sid" } else { $l } } else { $l }
            }
            if (-not $found) {
                $out = $out | ForEach-Object { $_; if ($_ -eq '[Privilege Rights]') { "$Right = *$Sid" } }
            }
            $out
        }
        $lines = Add-Right $lines 'SeServiceLogonRight' $sid
        $lines = Add-Right $lines 'SeDenyInteractiveLogonRight' $sid
        $lines = Add-Right $lines 'SeDenyRemoteInteractiveLogonRight' $sid
        Set-Content -Path $inf -Value $lines -Encoding Unicode
        & secedit.exe /configure /db $db /cfg $inf /areas USER_RIGHTS | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "secedit failed with exit code $LASTEXITCODE" }
    } finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
}

function New-LocalServiceCredential {
    # The password is random, exists only in this process, and is handed straight to Windows. Nobody ever sees it.
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingConvertToSecureStringWithPlainText', '', Justification = 'Random one-time password that never leaves this process.')]
    param([string]$Account)
    $secure = ConvertTo-SecureString (New-RandomPassword) -AsPlainText -Force
    New-Object Management.Automation.PSCredential($Account, $secure)
}

function New-RandomPassword {
    $bytes = New-Object byte[] 36
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    # Letters, digits and symbols so it meets any complexity policy.
    'Aa1!' + [Convert]::ToBase64String($bytes).Replace('/', 'x').Replace('+', 'y')
}

# ------------------------------------------------------------------ uninstall
if ($PSCmdlet.ParameterSetName -eq 'Uninstall' -and $Uninstall) {
    if (-not (Test-Admin)) { Fail 'Run this script as Administrator.' }
    Write-Step 'Stopping and removing the service'
    if (Get-Service $ServiceName -ErrorAction SilentlyContinue) {
        Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName | Out-Null
    }
    Write-Host 'Service removed. Files, the config and the certificate were left in place.'
    Write-Host 'IMPORTANT: also revoke or remove this server in the portal so its certificate stops working.'
    return
}

# ------------------------------------------------------------------ validate
Write-Step 'Checking settings'
if (-not (Test-Admin)) { Fail 'Run this script as Administrator.' }

$uri = $null
if (-not [Uri]::TryCreate($PortalUrl, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') { Fail 'PortalUrl must be an https:// address.' }
if ($Token.Length -lt 8) { Fail 'Token looks too short.' }

$roots = @()
foreach ($r in $AllowedRoots) {
    $problem = Test-Root $r
    if ($problem) { Fail "AllowedRoots '$r': $problem" }
    $roots += $r.TrimEnd('\')
}

if (-not (Test-Path -LiteralPath $AgentExePath -PathType Leaf)) { Fail "Agent file not found: $AgentExePath" }
if ($ExpectedSha256) {
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $AgentExePath).Hash
    if ($actual -ne $ExpectedSha256.Trim().ToUpperInvariant()) { Fail "SHA-256 of the agent file does not match. Expected $ExpectedSha256 but got $actual." }
    Write-Host 'Agent file hash matches.'
} else {
    Write-Warning 'No -ExpectedSha256 given. Compare the file hash with the one published with the build before running this in Production.'
}

if ($Gmsa) {
    if ($Gmsa -notmatch '^[^\\]+\\[^\\]+\$$') { Fail "Gmsa must look like DOMAIN\name$ (ends with a dollar sign)." }
    if (Get-Command Test-ADServiceAccount -ErrorAction SilentlyContinue) {
        $short = $Gmsa.Split('\')[1].TrimEnd('$')
        try { if (-not (Test-ADServiceAccount -Identity $short)) { Fail "This server cannot use the gMSA $Gmsa. Check PrincipalsAllowedToRetrieveManagedPassword and run Install-ADServiceAccount." } }
        catch { Write-Warning "Could not verify the gMSA: $($_.Exception.Message)" }
    } else { Write-Warning 'ActiveDirectory module not present, so the gMSA could not be verified.' }
}

try {
    $tcp = New-Object Net.Sockets.TcpClient
    $iar = $tcp.BeginConnect($uri.Host, $uri.Port, $null, $null)
    if (-not $iar.AsyncWaitHandle.WaitOne(5000)) { Write-Warning "Could not reach $($uri.Host):$($uri.Port) within 5 seconds. Check DNS and firewall (outbound 443 to the portal)." }
    else { $tcp.EndConnect($iar); Write-Host "Portal address $($uri.Host):$($uri.Port) is reachable." }
    $tcp.Close()
} catch { Write-Warning "Could not reach $($uri.Host):$($uri.Port): $($_.Exception.Message)" }

if (Get-Service $ServiceName -ErrorAction SilentlyContinue) { Fail "The service $ServiceName already exists. Run with -Uninstall first." }

Write-Host ("Environment : {0}" -f $Environment)
Write-Host ("Allowed root: {0}" -f ($roots -join ', '))
Write-Host ("Managed mode: {0}" -f $(if ($AllowManaged) { 'allowed on this server' } else { 'NOT allowed (ReadOnly only)' }))
Write-Host ("Run as      : {0}" -f $(if ($Gmsa) { $Gmsa } else { "local account svc-icc-$($Environment.ToLowerInvariant())" }))

if ($ValidateOnly) { Write-Host 'Validation finished. Nothing was changed.' -ForegroundColor Green; return }

# ------------------------------------------------------------------ install
Write-Step 'Copying files'
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
$exe = Join-Path $InstallDir 'IisControlCenter.Agent.exe'
Copy-Item -LiteralPath $AgentExePath -Destination $exe -Force

Write-Step 'Locking down folders'
foreach ($d in $InstallDir, $DataDir) {
    & icacls.exe $d /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' | Out-Null   # Administrators, SYSTEM
    if ($LASTEXITCODE -ne 0) { Fail "icacls failed on $d" }
}

Write-Step 'Registering with the portal'
$configPath = Join-Path $DataDir 'agent.config.json'
$env:ICC_AGENT_CONFIG = $configPath
$env:ICC_TOKEN = $Token                      # passed by environment variable so it is not on a command line
try {
    $argsList = @('register', '--portal', $PortalUrl, '--environment', $Environment, '--roots', ($roots -join ';'))
    if ($AllowManaged) { $argsList += '--allow-managed' }
    & $exe @argsList
    if ($LASTEXITCODE -ne 0) { Fail "Registration failed (exit code $LASTEXITCODE). See the message above. Nothing else was installed." }
} finally {
    Remove-Item Env:\ICC_TOKEN -ErrorAction SilentlyContinue
    Remove-Item Env:\ICC_AGENT_CONFIG -ErrorAction SilentlyContinue
}

try {
    Write-Step 'Preparing the service account'
    $cred = $null
    if ($Gmsa) {
        $account = $Gmsa
    } else {
        $name = "svc-icc-$($Environment.ToLowerInvariant())"
        $account = ".\$name"
        $cred = New-LocalServiceCredential $account
        $secure = $cred.Password
        if (Get-LocalUser -Name $name -ErrorAction SilentlyContinue) {
            Set-LocalUser -Name $name -Password $secure
        } else {
            New-LocalUser -Name $name -Password $secure -PasswordNeverExpires -UserMayNotChangePassword -AccountNeverExpires `
                -Description "IIS Control Center agent ($Environment)" | Out-Null   # Windows limits this to 48 characters
        }
        Grant-LogonAsService "$env:COMPUTERNAME\$name"
    }
    # The agent needs administrator rights to manage IIS (Microsoft.Web.Administration).
    $member = if ($Gmsa) { $Gmsa } else { "$env:COMPUTERNAME\svc-icc-$($Environment.ToLowerInvariant())" }
    $adminsGroup = (New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')).Translate([Security.Principal.NTAccount]).Value.Split('\')[1]
    try { Add-LocalGroupMember -Group $adminsGroup -Member $member -ErrorAction Stop }
    catch [Microsoft.PowerShell.Commands.MemberExistsException] { Write-Verbose 'Already a member of the Administrators group.' }

    Write-Step 'Creating the service'
    if (-not [System.Diagnostics.EventLog]::SourceExists($ServiceName)) { [System.Diagnostics.EventLog]::CreateEventSource($ServiceName, 'Application') }   # works in Windows PowerShell 5.1 and 7
    $binPath = "`"$exe`""
    $env:ICC_AGENT_CONFIG = $null
    if ($Gmsa) {
        & sc.exe create $ServiceName binPath= $binPath obj= $Gmsa start= delayed-auto DisplayName= 'IIS Control Center Agent' | Out-Null
        if ($LASTEXITCODE -ne 0) { Fail "sc.exe create failed ($LASTEXITCODE)" }
    } else {
        New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName 'IIS Control Center Agent' -StartupType Automatic -Credential $cred | Out-Null
        & sc.exe config $ServiceName start= delayed-auto | Out-Null
    }
    & sc.exe description $ServiceName 'Connects out to the IIS Control Center portal and runs allow-listed IIS tasks on this server.' | Out-Null
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
    # The service reads its config from the default location (%ProgramData%\IisControlCenter) unless this variable is set machine-wide.
    if ($DataDir -ne (Join-Path $env:ProgramData 'IisControlCenter')) {
        $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
        New-ItemProperty -Path $key -Name Environment -PropertyType MultiString -Value @("ICC_AGENT_CONFIG=$configPath") -Force | Out-Null
    }

    Write-Step 'Starting the service'
    Start-Service $ServiceName
    Start-Sleep -Seconds 3
    $svc = Get-Service $ServiceName
    Write-Host ("Service status: {0}" -f $svc.Status)
    if ($svc.Status -ne 'Running') { Write-Warning 'The service is not running. Check the Application event log (source IisControlCenterAgent).' }

} catch {
    Write-Warning "Setup failed AFTER the agent registered: $($_.Exception.Message)"
    Write-Warning 'The one-time token is used up. In the portal, open the server and create a new registration token (or remove the server), fix the problem above, then run this script again.'
    throw
}

Write-Host ''
Write-Host 'Done. Next steps:' -ForegroundColor Green
Write-Host '  1. In the portal, open Servers and approve this server (confirm the environment).'
Write-Host '  2. It starts in ReadOnly mode. Discovery runs automatically after approval.'
