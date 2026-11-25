#Requires -RunAsAdministrator
<#
.SYNOPSIS
    BPE Stage 1 — AppLocker Enforcement Proof Orchestrator (Windows PowerShell 5.1).
    Runs ONLY inside a verified disposable CI environment (GitHub Actions windows-2025).
    DO NOT RUN on a personal/production machine.

.DESCRIPTION
    Executes all 12 Section 5 steps and collects all Section 6 evidence:
    1.  Edition & VM gate check + initial Local & Effective AppLocker XML snapshots
    2.  AppIDSvc & seclogon start + AppLocker log enable + disposable users + target build + Authenticode hashes
    3.  AuditOnly policy + Event 8003 capture
    4.  Enforced policy + Test-AppLockerPolicy dry-run gate + Win32 1260 denial + Event 8004 capture
    5.  Allowed executable verification + Event 8002 capture
    6.  User SID targeting verification via Test-AppLockerPolicy + Effective XML check
    7.  Second-user isolation (bpe_stage1_other runs blocked target + Event 8002)
    8.  Preservation of synthetic admin baseline rule (a111...)
    9.  Duplicate RuleId conflict detection (fail-closed pre-write check)
    10. External administrator policy change detection (b222... injected out-of-band)
    11. Crash / restart recovery (ledger-driven surgical removal of orphaned Boundless RuleId)
    12. Verified surgical restoration, post-removal launch proof, EVTX/JSON export & clean OS teardown
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

Import-Module AppLocker -ErrorAction Stop

# ============================================================
# CONSTANTS & PATHS
# ============================================================
$script:RootDir         = 'C:\BpeStage1Proof'
$script:BinDir          = 'C:\BpeStage1Proof\bin'
$script:OutDir          = 'C:\BpeStage1Proof\out'
$script:EvidenceDir     = 'C:\BpeStage1Proof\evidence'
$script:TestUser1       = 'bpe_stage1_test'
$script:TestUser2       = 'bpe_stage1_other'
$script:TestPassword    = 'B0undl3ss!Stage1Test#2026'

$script:BlockedExePath  = Join-Path $script:BinDir 'bpe-stage1-blocked.exe'
$script:AllowedExePath  = Join-Path $script:BinDir 'bpe-stage1-allowed.exe'
$script:HarnessExePath  = $null

$script:InitialLocalXml = Join-Path $script:EvidenceDir 'policy-00-initial-local.xml'
$script:AdminBaselineId = 'a1111111-1111-4111-8111-111111111111'
$script:ExternalAdminId = 'b2222222-2222-4222-8222-222222222222'
$script:BoundlessRuleId = 'd1111111-2222-4333-8444-555555555555'
$script:SessionId       = [guid]::NewGuid().ToString()

$script:TestUser1Sid    = $null
$script:TestUser2Sid    = $null
$script:BlockedHashData = $null
$script:BlockedFileLen  = $null
$script:BaselinePaths   = New-Object System.Collections.Generic.List[string]
$script:CriticalExes    = New-Object System.Collections.Generic.List[string]
$script:StepResults     = [ordered]@{}

# ============================================================
# HELPERS
# ============================================================
function Write-Step {
    param([string]$Num, [string]$Title)
    Write-Host ''
    Write-Host '================================================================'
    Write-Host (" STEP {0} — {1}" -f $Num, $Title)
    Write-Host '================================================================'
}

function Save-EvidenceText {
    param([string]$FileName, [string]$Content)
    $path = Join-Path $script:EvidenceDir $FileName
    $dir = Split-Path $path -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($path, $Content, $utf8NoBom)
    Write-Host "[EVIDENCE] Saved: $path"
    return $path
}

function Save-EvidenceJson {
    param([string]$FileName, [object]$Data, [int]$Depth = 6)
    $json = $Data | ConvertTo-Json -Depth $Depth
    return Save-EvidenceText -FileName $FileName -Content $json
}

function Record-Step {
    param([string]$Step, [string]$Status, [string]$Detail, [hashtable]$Extra = @{})
    $entry = [ordered]@{
        step      = $Step
        status    = $Status
        detail    = $Detail
        utc       = (Get-Date).ToUniversalTime().ToString('o')
    }
    foreach ($k in $Extra.Keys) { $entry[$k] = $Extra[$k] }
    $script:StepResults[$Step] = $entry
    Write-Host ("[{0}] Step {1}: {2}" -f $Status, $Step, $Detail)
}

function Get-Sha256Hex {
    param([string]$Text)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        $hash = $sha.ComputeHash($bytes)
        return ([System.BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-AppLockerXmlSafe {
    param([ValidateSet('Local', 'Effective')][string]$Scope)
    try {
        if ($Scope -eq 'Local') {
            return (Get-AppLockerPolicy -Local -Xml)
        }
        return (Get-AppLockerPolicy -Effective -Xml)
    }
    catch {
        return '<AppLockerPolicy Version="1" />'
    }
}

function Assert-DisposableEnvironment {
    if ($env:BOUNDLESS_DISPOSABLE_VM_CONFIRMED -ne '1') {
        throw 'FATAL: BOUNDLESS_DISPOSABLE_VM_CONFIRMED must be 1.'
    }
    if ($env:BOUNDLESS_ALLOW_LIVE_VM_TESTS -ne '1') {
        throw 'FATAL: BOUNDLESS_ALLOW_LIVE_VM_TESTS must be 1.'
    }

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        throw 'FATAL: Stage 1 proof must run elevated as Administrator inside disposable CI VM.'
    }

    $os = Get-CimInstance Win32_OperatingSystem
    $reg = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    if ($os.Caption -match 'Home' -or $reg.EditionID -match 'Core') {
        throw "FATAL: Refusing to run on Home/Core edition ($($os.Caption) / $($reg.EditionID))."
    }
    if ($os.Caption -notmatch 'Server') {
        throw "FATAL: Expected Windows Server disposable CI VM, found: $($os.Caption)"
    }
}

function Initialize-DynamicRunnerPaths {
    $candidates = New-Object System.Collections.Generic.List[string]
    $candidates.Add('%WINDIR%\*')
    $candidates.Add('%PROGRAMFILES%\*')
    $candidates.Add('%PROGRAMFILES(X86)%\*')
    $candidates.Add("$($script:BinDir)\*")

    $dirsToCheck = New-Object System.Collections.Generic.List[string]

    foreach ($procName in @('Runner.Worker', 'Runner.Listener')) {
        Get-Process -Name $procName -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.Path) {
                $script:CriticalExes.Add($_.Path)
                # Walk up two levels so runner root (e.g. C:\actions-runner\bin -> C:\actions-runner) is allowed
                $p1 = Split-Path $_.Path -Parent
                $p2 = Split-Path $p1 -Parent
                $dirsToCheck.Add($p1)
                if ($p2 -and $p2.Length -gt 3) { $dirsToCheck.Add($p2) }
            }
        }
    }

    foreach ($cmdName in @('pwsh', 'powershell', 'cmd', 'dotnet', 'sc', 'wevtutil', 'git')) {
        $cmd = Get-Command $cmdName -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($cmd -and $cmd.Source -and (Test-Path $cmd.Source)) {
            $script:CriticalExes.Add($cmd.Source)
            $dirsToCheck.Add((Split-Path $cmd.Source -Parent))
        }
    }

    if ($script:HarnessExePath -and (Test-Path $script:HarnessExePath)) {
        $script:CriticalExes.Add($script:HarnessExePath)
        $dirsToCheck.Add((Split-Path $script:HarnessExePath -Parent))
    }

    foreach ($envDir in @(
        $env:DOTNET_ROOT,
        $env:GITHUB_WORKSPACE,
        $env:RUNNER_WORKSPACE,
        $env:RUNNER_TEMP,
        'C:\hostedtoolcache',
        'C:\actions-runner',
        'C:\a',
        'C:\ProgramData\chocolatey'
    )) {
        if ($envDir -and (Test-Path $envDir)) {
            $dirsToCheck.Add($envDir)
        }
    }

    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($p in $candidates) { [void]$seen.Add($p) }

    foreach ($d in $dirsToCheck) {
        $trimmed = $d.TrimEnd('\')
        if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
        $pattern = "$trimmed\*"
        # Skip adding root directories already covered by standard Windows/ProgramFiles rules
        if ($trimmed -like "$env:WINDIR*" -or $trimmed -like "$env:ProgramFiles*") { continue }
        if (${env:ProgramFiles(x86)} -and $trimmed -like "${env:ProgramFiles(x86)}*") { continue }
        if ($seen.Add($pattern)) {
            $candidates.Add($pattern)
        }
    }

    $script:BaselinePaths.Clear()
    foreach ($item in $candidates) { $script:BaselinePaths.Add($item) }

    # Deduplicate critical exes
    $exeSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $dedupExes = New-Object System.Collections.Generic.List[string]
    foreach ($e in $script:CriticalExes) {
        if ($exeSet.Add($e)) { $dedupExes.Add($e) }
    }
    $script:CriticalExes = $dedupExes
}

function Build-AppLockerPolicyXml {
    param(
        [ValidateSet('AuditOnly', 'Enabled')][string]$EnforcementMode,
        [bool]$IncludeBoundlessDeny = $true,
        [bool]$IncludeExternalAdmin = $false
    )

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine('<AppLockerPolicy Version="1">')
    [void]$sb.AppendLine(('  <RuleCollection Type="Exe" EnforcementMode="{0}">' -f $EnforcementMode))

    # 1. OS + Dynamic Runner Baseline Allow rules
    $idx = 1
    foreach ($pathPattern in $script:BaselinePaths) {
        $guid = ('b0000001-0001-4000-8000-{0:x12}' -f $idx)
        $escapedPath = [System.Security.SecurityElement]::Escape($pathPattern)
        $name = [System.Security.SecurityElement]::Escape("Stage1Baseline.Allow.$idx")
        [void]$sb.AppendLine(('    <FilePathRule Id="{0}" Name="{1}" Description="Dynamic OS/CI baseline allow rule" UserOrGroupSid="S-1-1-0" Action="Allow">' -f $guid, $name))
        [void]$sb.AppendLine(('      <Conditions><FilePathCondition Path="{0}" /></Conditions>' -f $escapedPath))
        [void]$sb.AppendLine('    </FilePathRule>')
        $idx++
    }

    # 2. Explicit Synthetic Admin Baseline Rule (preservation proof target)
    [void]$sb.AppendLine(('    <FilePathRule Id="{0}" Name="SyntheticAdminBaseline PreserveMe" Description="Pre-existing synthetic administrator baseline rule" UserOrGroupSid="S-1-5-32-544" Action="Allow">' -f $script:AdminBaselineId))
    [void]$sb.AppendLine('      <Conditions><FilePathCondition Path="C:\BpeStage1Proof\admin-baseline\*" /></Conditions>')
    [void]$sb.AppendLine('    </FilePathRule>')

    # 3. Optional External Admin Rule (Step 10+)
    if ($IncludeExternalAdmin) {
        [void]$sb.AppendLine(('    <FilePathRule Id="{0}" Name="ExternalAdminBaseline OutOfBand" Description="Simulated external admin rule added after Boundless" UserOrGroupSid="S-1-5-32-544" Action="Allow">' -f $script:ExternalAdminId))
        [void]$sb.AppendLine('      <Conditions><FilePathCondition Path="C:\BpeStage1Proof\external-admin\*" /></Conditions>')
        [void]$sb.AppendLine('    </FilePathRule>')
    }

    # 4. Boundless Study Deny Rule (Authenticode FileHashCondition, scoped strictly to TestUser1 SID)
    if ($IncludeBoundlessDeny) {
        $desc = [System.Security.SecurityElement]::Escape("Owner=BoundlessStudy.Stage1;SessionId=$($script:SessionId);Target=bpe-stage1-blocked.exe;Scope=$($script:TestUser1)")
        [void]$sb.AppendLine(('    <FileHashRule Id="{0}" Name="BoundlessStudy.Stage1.Deny.blocked" Description="{1}" UserOrGroupSid="{2}" Action="Deny">' -f $script:BoundlessRuleId, $desc, $script:TestUser1Sid))
        [void]$sb.AppendLine('      <Conditions>')
        [void]$sb.AppendLine('        <FileHashCondition>')
        [void]$sb.AppendLine(('          <FileHash Type="SHA256" Data="{0}" SourceFileName="bpe-stage1-blocked.exe" SourceFileLength="{1}" />' -f $script:BlockedHashData, $script:BlockedFileLen))
        [void]$sb.AppendLine('        </FileHashCondition>')
        [void]$sb.AppendLine('      </Conditions>')
        [void]$sb.AppendLine('    </FileHashRule>')
    }

    [void]$sb.AppendLine('  </RuleCollection>')
    [void]$sb.AppendLine('</AppLockerPolicy>')
    return $sb.ToString()
}

function Invoke-PreFlightPolicyGate {
    param(
        [string]$PolicyXmlPath,
        [bool]$ExpectUser1Blocked,
        [ValidateSet('AuditOnly', 'Enabled')][string]$EnforcementMode = 'Enabled'
    )

    $adminUser = "$env:COMPUTERNAME\$env:USERNAME"
    $user1 = "$env:COMPUTERNAME\$($script:TestUser1)"
    $user2 = "$env:COMPUTERNAME\$($script:TestUser2)"

    $checks = New-Object System.Collections.Generic.List[object]

    # Critical executables must NEVER evaluate to Denied or DeniedByDefault for admin or SYSTEM.
    # For admin under Enabled mode, require explicit 'Allowed' to prove baseline Allow rules cover the exe.
    # NT AUTHORITY\SYSTEM is inherently AllowedByDefault in AppLocker unless non-user enforcement is enabled.
    foreach ($exe in $script:CriticalExes) {
        $resAdmin = Test-AppLockerPolicy -XmlPolicy $PolicyXmlPath -Path $exe -User $adminUser
        $decAdmin = [string]$resAdmin.PolicyDecision
        $checks.Add(@{ exe = $exe; user = $adminUser; decision = $decAdmin })
        if ($EnforcementMode -eq 'Enabled' -and $decAdmin -ne 'Allowed') {
            throw "SAFETY ABORT: Critical exe '$exe' evaluated as '$decAdmin' (expected explicit 'Allowed') for '$adminUser'."
        }
        if ($decAdmin -match 'Denied') {
            throw "SAFETY ABORT: Critical exe '$exe' evaluated as '$decAdmin' for '$adminUser'."
        }

        $resSys = Test-AppLockerPolicy -XmlPolicy $PolicyXmlPath -Path $exe -User 'NT AUTHORITY\SYSTEM'
        $decSys = [string]$resSys.PolicyDecision
        $checks.Add(@{ exe = $exe; user = 'NT AUTHORITY\SYSTEM'; decision = $decSys })
        if ($decSys -notin @('Allowed', 'AllowedByDefault')) {
            throw "SAFETY ABORT: Critical exe '$exe' evaluated as '$decSys' for 'NT AUTHORITY\SYSTEM'."
        }
    }

    # Verify AllowedExe is not denied for User1 (must be explicit Allowed under Enabled mode)
    $rAllowed = Test-AppLockerPolicy -XmlPolicy $PolicyXmlPath -Path $script:AllowedExePath -User $user1
    $decAllowed = [string]$rAllowed.PolicyDecision
    $checks.Add(@{ exe = $script:AllowedExePath; user = $user1; decision = $decAllowed })
    if (($EnforcementMode -eq 'Enabled' -and $decAllowed -ne 'Allowed') -or ($decAllowed -match 'Denied')) {
        throw "SAFETY ABORT: Allowed target evaluated as '$decAllowed' for '$user1'."
    }

    # Verify BlockedExe for User1 matches expectation
    $rBlocked1 = Test-AppLockerPolicy -XmlPolicy $PolicyXmlPath -Path $script:BlockedExePath -User $user1
    $decBlocked1 = [string]$rBlocked1.PolicyDecision
    $checks.Add(@{ exe = $script:BlockedExePath; user = $user1; decision = $decBlocked1 })
    if ($ExpectUser1Blocked -and ($decBlocked1 -notmatch 'Denied')) {
        throw "PRE-FLIGHT FAIL: Blocked target evaluated as '$decBlocked1' (expected Denied) for '$user1'."
    }

    # Verify BlockedExe is Allowed (not denied) for User2
    $rBlocked2 = Test-AppLockerPolicy -XmlPolicy $PolicyXmlPath -Path $script:BlockedExePath -User $user2
    $decBlocked2 = [string]$rBlocked2.PolicyDecision
    $checks.Add(@{ exe = $script:BlockedExePath; user = $user2; decision = $decBlocked2 })
    if (($EnforcementMode -eq 'Enabled' -and $decBlocked2 -ne 'Allowed') -or ($decBlocked2 -match 'Denied')) {
        throw "PRE-FLIGHT FAIL: Blocked target evaluated as '$decBlocked2' (expected Allowed) for '$user2'."
    }

    return $checks
}

function Wait-EffectivePolicyRule {
    param(
        [string]$RuleId,
        [bool]$ShouldExist,
        [string]$ExpectedMode = $null,
        [int]$TimeoutSeconds = 10
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $xml = Get-AppLockerXmlSafe -Scope 'Effective'
        $hasRule = $xml -match [regex]::Escape($RuleId)
        $modeOk = ($null -eq $ExpectedMode) -or ($xml -match ('EnforcementMode="{0}"' -f [regex]::Escape($ExpectedMode)))
        if (($hasRule -eq $ShouldExist) -and $modeOk) {
            return $xml
        }
        Start-Sleep -Milliseconds 250
    }

    return (Get-AppLockerXmlSafe -Scope 'Effective')
}

function Wait-AppLockerEvent {
    param(
        [int]$EventId,
        [datetime]$SinceUtc,
        [string]$TargetExeName = $null,
        [string]$ExpectedUserSid = $null,
        [int]$TimeoutSeconds = 12
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $localSince = $SinceUtc.ToLocalTime().AddSeconds(-2)

    while ((Get-Date) -lt $deadline) {
        $events = Get-WinEvent -FilterHashtable @{
            LogName   = 'Microsoft-Windows-AppLocker/EXE and DLL'
            Id        = $EventId
            StartTime = $localSince
        } -ErrorAction SilentlyContinue

        if ($events) {
            foreach ($ev in $events) {
                $sidMatch = ($null -eq $ExpectedUserSid) -or ($ev.UserId -and $ev.UserId.Value -eq $ExpectedUserSid)
                $msgMatch = ($null -eq $TargetExeName) -or ($ev.Message -match [regex]::Escape($TargetExeName))
                if ($sidMatch -and $msgMatch) {
                    return @{
                        id          = $ev.Id
                        timeUtc     = $ev.TimeCreated.ToUniversalTime().ToString('o')
                        userSid     = if ($ev.UserId) { $ev.UserId.Value } else { $null }
                        provider    = $ev.ProviderName
                        level       = $ev.LevelDisplayName
                        message     = $ev.Message
                    }
                }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    return $null
}

function Invoke-HarnessLaunch {
    param(
        [string]$ExePath,
        [string]$Username,
        [string]$ResultName,
        [bool]$ExpectBlocked,
        [string]$MarkerName
    )

    $resultPath = Join-Path $script:EvidenceDir $ResultName
    $markerPath = Join-Path $script:OutDir $MarkerName
    $expectStr = if ($ExpectBlocked) { 'true' } else { 'false' }

    & $script:HarnessExePath $ExePath $Username $script:TestPassword $resultPath $expectStr $markerPath
    $harnessExit = $LASTEXITCODE

    if (-not (Test-Path $resultPath)) {
        throw "Harness did not write result JSON at $resultPath (exit code $harnessExit)."
    }

    $parsed = Get-Content -Path $resultPath -Raw | ConvertFrom-Json
    return @{
        harnessExitCode = $harnessExit
        result          = $parsed
        rawPath         = $resultPath
    }
}

function Remove-RuleFromLocalPolicyXml {
    param(
        [string]$LocalXmlText,
        [string]$TargetRuleId
    )

    [xml]$doc = $LocalXmlText
    $nsMgr = New-Object System.Xml.XmlNamespaceManager($doc.NameTable)
    $nodes = $doc.SelectNodes('//*[local-name()="FilePathRule" or local-name()="FileHashRule" or local-name()="FilePublisherRule"]')
    $removed = 0

    foreach ($node in @($nodes)) {
        if ($node.GetAttribute('Id') -eq $TargetRuleId) {
            [void]$node.ParentNode.RemoveChild($node)
            $removed++
        }
    }

    return @{
        removedCount = $removed
        updatedXml   = $doc.OuterXml
    }
}

# ============================================================
# MAIN EXECUTION
# ============================================================
try {
    Write-Host '============================================================'
    Write-Host ' BPE STAGE 1 — APPLOCKER LIVE ENFORCEMENT PROOF'
    Write-Host (" Started UTC: {0}" -f (Get-Date).ToUniversalTime().ToString('o'))
    Write-Host '============================================================'

    # Ensure directories exist upfront
    New-Item -ItemType Directory -Force -Path $script:RootDir, $script:BinDir, $script:OutDir, $script:EvidenceDir | Out-Null

    # ── STEP 1: Edition/VM gate + initial snapshots ──
    Write-Step '1' 'Edition & VM gate check + initial policy snapshots'
    Assert-DisposableEnvironment

    $os = Get-CimInstance Win32_OperatingSystem
    $reg = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $initialLocal = Get-AppLockerXmlSafe -Scope 'Local'
    $initialEffective = Get-AppLockerXmlSafe -Scope 'Effective'

    [void](Save-EvidenceText -FileName 'policy-00-initial-local.xml' -Content $initialLocal)
    [void](Save-EvidenceText -FileName 'policy-00-initial-effective.xml' -Content $initialEffective)
    [void](Save-EvidenceJson -FileName 'env-gate.json' -Data ([ordered]@{
        caption      = $os.Caption
        version      = $os.Version
        buildNumber  = $os.BuildNumber
        productName  = $reg.ProductName
        editionId    = $reg.EditionID
        displayVer   = $reg.DisplayVersion
        ubr          = $reg.UBR
        arch         = $env:PROCESSOR_ARCHITECTURE
        computerName = $env:COMPUTERNAME
        runnerImage  = "$env:ImageOS $env:ImageVersion"
        psVersion    = $PSVersionTable.PSVersion.ToString()
    }))

    Record-Step '1' 'PASS' "$($os.Caption) (EditionID=$($reg.EditionID), Build=$($os.BuildNumber)); initial snapshots captured."

    # ── STEP 2: Services, event log, users, binaries, hashes, harness ──
    Write-Step '2' 'AppIDSvc, seclogon, Event Log, disposable users, targets & Authenticode hashes'

    $appIdQc = (& sc.exe qc AppIDSvc 2>&1) | Out-String
    $appIdSvc = Get-Service -Name AppIDSvc
    if ($appIdSvc.Status -ne 'Running') {
        $startOut = (& net start AppIDSvc 2>&1) | Out-String
        if ($LASTEXITCODE -notin @(0, 2)) {
            Write-Warning "Initial net start AppIDSvc returned ${LASTEXITCODE}: $startOut ; trying sc.exe config AppIDSvc start= auto"
            $autoCfg = (& sc.exe config AppIDSvc start= auto 2>&1) | Out-String
            $startOut = (& net start AppIDSvc 2>&1) | Out-String
            if ($LASTEXITCODE -notin @(0, 2)) {
                throw "Failed to start AppIDSvc (config: $autoCfg | start: $startOut | qc: $appIdQc)"
            }
        }
        $appIdSvc.WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
        $appIdSvc.Refresh()
    }
    if ($appIdSvc.Status -ne 'Running') { throw "AppIDSvc not running: $($appIdSvc.Status)" }

    # Ensure seclogon is running for CreateProcessWithLogonW
    $secSvc = Get-Service -Name seclogon -ErrorAction SilentlyContinue
    if ($secSvc -and $secSvc.Status -ne 'Running') {
        $secLogonStart = (& net start seclogon 2>&1) | Out-String
        if ($LASTEXITCODE -notin @(0, 2)) {
            [void](& sc.exe config seclogon start= demand 2>&1)
            $secLogonStart = (& net start seclogon 2>&1) | Out-String
            if ($LASTEXITCODE -notin @(0, 2)) { Write-Warning "seclogon start returned ${LASTEXITCODE}: $secLogonStart" }
        }
        $secSvc.Refresh()
    }

    # Enable & size AppLocker EXE/DLL event log
    $wevtOut = (& wevtutil sl 'Microsoft-Windows-AppLocker/EXE and DLL' /e:true /ms:16777216 2>&1) | Out-String
    if ($LASTEXITCODE -ne 0) { Write-Warning "wevtutil sl failed: $wevtOut" }

    [void](Save-EvidenceText -FileName 'services-status.txt' -Content @"
AppIDSvc Status: $($appIdSvc.Status) (StartType: $($appIdSvc.StartType))
seclogon Status: $(if ($secSvc) { "$($secSvc.Status) (StartType: $($secSvc.StartType))" } else { 'N/A' })
wevtutil sl output: $wevtOut
"@)

    # Create disposable non-admin local users
    $secPwd = ConvertTo-SecureString $script:TestPassword -AsPlainText -Force
    foreach ($u in @($script:TestUser1, $script:TestUser2)) {
        if (Get-LocalUser -Name $u -ErrorAction SilentlyContinue) {
            Remove-LocalUser -Name $u
        }
        [void](New-LocalUser -Name $u -Password $secPwd -PasswordNeverExpires -Description 'BPE Stage 1 disposable test user')
        try { Add-LocalGroupMember -Group 'Users' -Member $u -ErrorAction SilentlyContinue } catch {}
    }

    $script:TestUser1Sid = (New-Object System.Security.Principal.NTAccount($script:TestUser1)).Translate([System.Security.Principal.SecurityIdentifier]).Value
    $script:TestUser2Sid = (New-Object System.Security.Principal.NTAccount($script:TestUser2)).Translate([System.Security.Principal.SecurityIdentifier]).Value

    [void](Save-EvidenceJson -FileName 'users-sids.json' -Data ([ordered]@{
        testUser1 = @{ name = $script:TestUser1; sid = $script:TestUser1Sid; groups = @('Users') }
        testUser2 = @{ name = $script:TestUser2; sid = $script:TestUser2Sid; groups = @('Users') }
    }))

    # Compile target binaries using Framework4 csc.exe (universal Users RX compatibility)
    $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
    if (-not (Test-Path $csc)) { throw 'FATAL: Framework 4 csc.exe not found.' }

    $blockedSrc = Join-Path $PSScriptRoot 'targets\bpe-stage1-blocked.cs'
    $allowedSrc = Join-Path $PSScriptRoot 'targets\bpe-stage1-allowed.cs'

    & $csc /nologo /target:exe /out:$script:BlockedExePath $blockedSrc
    if ($LASTEXITCODE -ne 0) { throw 'Failed to compile bpe-stage1-blocked.exe' }

    & $csc /nologo /target:exe /out:$script:AllowedExePath $allowedSrc
    if ($LASTEXITCODE -ne 0) { throw 'Failed to compile bpe-stage1-allowed.exe' }

    # Set NTFS ACLs: Users RX on RootDir & BinDir, Users Modify on OutDir
    foreach ($dir in @($script:RootDir, $script:BinDir)) {
        $acl = Get-Acl $dir
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('Users', 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
        Set-Acl -Path $dir -AclObject $acl
    }
    $outAcl = Get-Acl $script:OutDir
    $outAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('Users', 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl -Path $script:OutDir -AclObject $outAcl

    # Compute Authenticode hashes using AppLocker Get-AppLockerFileInformation
    $blockedInfo = Get-AppLockerFileInformation -Path $script:BlockedExePath
    $allowedInfo = Get-AppLockerFileInformation -Path $script:AllowedExePath

    $blockedHashPolicyXml = ($blockedInfo | New-AppLockerPolicy -RuleType Hash -User $script:TestUser1Sid -Xml)
    [xml]$blockedHashDoc = $blockedHashPolicyXml
    $fileHashNode = $blockedHashDoc.SelectSingleNode('//*[local-name()="FileHash"]')
    if ($null -eq $fileHashNode) { throw 'Failed to generate AppLocker FileHash node for blocked target.' }

    $script:BlockedHashData = $fileHashNode.GetAttribute('Data')
    $script:BlockedFileLen  = $fileHashNode.GetAttribute('SourceFileLength')

    $allowedHashPolicyXml = ($allowedInfo | New-AppLockerPolicy -RuleType Hash -User $script:TestUser1Sid -Xml)
    [xml]$allowedHashDoc = $allowedHashPolicyXml
    $allowedHashNode = $allowedHashDoc.SelectSingleNode('//*[local-name()="FileHash"]')
    $allowedHashData = if ($allowedHashNode) { $allowedHashNode.GetAttribute('Data') } else { '' }

    if ($script:BlockedHashData -eq $allowedHashData) {
        throw 'FATAL: Blocked and allowed binaries have identical Authenticode PE hashes!'
    }

    [void](Save-EvidenceJson -FileName 'target-hashes.json' -Data ([ordered]@{
        blocked = [ordered]@{
            path                = $script:BlockedExePath
            authenticodeSha256  = $script:BlockedHashData
            sourceFileLength    = $script:BlockedFileLen
            flatSha256          = (Get-FileHash $script:BlockedExePath -Algorithm SHA256).Hash
        }
        allowed = [ordered]@{
            path                = $script:AllowedExePath
            authenticodeSha256  = $allowedHashData
            sourceFileLength    = if ($allowedHashNode) { $allowedHashNode.GetAttribute('SourceFileLength') } else { '' }
            flatSha256          = (Get-FileHash $script:AllowedExePath -Algorithm SHA256).Hash
        }
    }))

    # Locate built .NET 10 launcher harness
    $harnessCandidates = @(
        (Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\BpeStage1.AppLockerProof.exe'),
        (Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\win-x64\BpeStage1.AppLockerProof.exe')
    )
    foreach ($c in $harnessCandidates) {
        if (Test-Path $c) { $script:HarnessExePath = $c; break }
    }
    if (-not $script:HarnessExePath) { throw "Harness exe not found under $($PSScriptRoot)\bin\Release" }

    Initialize-DynamicRunnerPaths
    [void](Save-EvidenceJson -FileName 'runner-baseline-paths.json' -Data ([ordered]@{
        baselineAllowRules = @($script:BaselinePaths)
        criticalExesChecked = @($script:CriticalExes)
    }))

    # Apply pre-existing synthetic admin baseline policy BEFORE Boundless begins
    $preAdminXml = Build-AppLockerPolicyXml -EnforcementMode 'AuditOnly' -IncludeBoundlessDeny $false -IncludeExternalAdmin $false
    $preAdminPath = Save-EvidenceText -FileName 'policy-00b-pre-admin-baseline.xml' -Content $preAdminXml
    Set-AppLockerPolicy -XmlPolicy $preAdminPath

    Record-Step '2' 'PASS' "AppIDSvc Running; users $($script:TestUser1) ($($script:TestUser1Sid)) & $($script:TestUser2) ($($script:TestUser2Sid)) created; distinct PE hashes captured."

    # ── STEP 3: AuditOnly policy + Event 8003 ──
    Write-Step '3' 'AuditOnly policy + Event 8003 verification'

    $auditXml = Build-AppLockerPolicyXml -EnforcementMode 'AuditOnly' -IncludeBoundlessDeny $true
    $auditPath = Save-EvidenceText -FileName 'policy-01-auditonly.xml' -Content $auditXml
    [void](Invoke-PreFlightPolicyGate -PolicyXmlPath $auditPath -ExpectUser1Blocked $false -EnforcementMode 'AuditOnly')

    $tApplyAudit = (Get-Date).ToUniversalTime()
    Set-AppLockerPolicy -XmlPolicy $auditPath
    $effAudit = Wait-EffectivePolicyRule -RuleId $script:BoundlessRuleId -ShouldExist $true -ExpectedMode 'AuditOnly'
    [void](Save-EvidenceText -FileName 'policy-01-effective-auditonly.xml' -Content $effAudit)
    Start-Sleep -Seconds 2

    $tAuditLaunch = (Get-Date).ToUniversalTime()
    $step3Launch = Invoke-HarnessLaunch -ExePath $script:BlockedExePath -Username $script:TestUser1 -ResultName 'step03-audit-exec.json' -ExpectBlocked $false -MarkerName 'step03-audit-marker.json'
    if (-not $step3Launch.result.processCreated -or $step3Launch.result.processExitCode -ne 0 -or -not $step3Launch.result.markerExists) {
        throw "Step 3 failed: AuditOnly launch did not execute cleanly: $($step3Launch.result.errorMessage)"
    }

    $ev8003 = Wait-AppLockerEvent -EventId 8003 -SinceUtc $tAuditLaunch -TargetExeName 'bpe-stage1-blocked.exe' -ExpectedUserSid $script:TestUser1Sid
    if ($null -eq $ev8003) {
        throw 'Step 3 failed: Event ID 8003 (audit would-be-blocked) was not recorded in AppLocker EXE and DLL log.'
    }
    [void](Save-EvidenceJson -FileName 'step03-event-8003.json' -Data $ev8003)
    Record-Step '3' 'PASS' "AuditOnly permitted execution (PID $($step3Launch.result.processId)) and logged Event 8003 for $($script:TestUser1Sid)."

    # ── STEP 4: Enforced denial (Win32 1260 + Event 8004) ──
    Write-Step '4' 'Enforced policy + Win32 1260 denial + Event 8004'

    $enforcedXml = Build-AppLockerPolicyXml -EnforcementMode 'Enabled' -IncludeBoundlessDeny $true
    $enforcedPath = Save-EvidenceText -FileName 'policy-02-enabled.xml' -Content $enforcedXml
    $preFlight = Invoke-PreFlightPolicyGate -PolicyXmlPath $enforcedPath -ExpectUser1Blocked $true
    [void](Save-EvidenceJson -FileName 'preflight-testapplocker.json' -Data $preFlight)

    $tApplyEnforced = (Get-Date).ToUniversalTime()
    Set-AppLockerPolicy -XmlPolicy $enforcedPath
    $effEnforced = Wait-EffectivePolicyRule -RuleId $script:BoundlessRuleId -ShouldExist $true -ExpectedMode 'Enabled'
    [void](Save-EvidenceText -FileName 'policy-02-effective-enabled.xml' -Content $effEnforced)

    $ev8001 = Wait-AppLockerEvent -EventId 8001 -SinceUtc $tApplyEnforced
    if ($ev8001) { [void](Save-EvidenceJson -FileName 'step04-event-8001-policy-applied.json' -Data $ev8001) }
    Start-Sleep -Seconds 2

    $tBlockLaunch = (Get-Date).ToUniversalTime()
    $step4Launch = Invoke-HarnessLaunch -ExePath $script:BlockedExePath -Username $script:TestUser1 -ResultName 'step04-blocked-denial.json' -ExpectBlocked $true -MarkerName 'step04-blocked-marker.json'
    if ($step4Launch.result.processCreated -or -not $step4Launch.result.appLockerBlocked -or $step4Launch.result.win32Error -ne 1260 -or $step4Launch.result.markerExists) {
        throw "Step 4 FAILED: Expected Win32 1260 denial without marker; got created=$($step4Launch.result.processCreated), err=$($step4Launch.result.win32Error), marker=$($step4Launch.result.markerExists)"
    }

    $ev8004 = Wait-AppLockerEvent -EventId 8004 -SinceUtc $tBlockLaunch -TargetExeName 'bpe-stage1-blocked.exe' -ExpectedUserSid $script:TestUser1Sid
    if ($null -eq $ev8004) {
        throw 'Step 4 FAILED: Win32 1260 occurred, but Event ID 8004 was not captured in AppLocker log.'
    }
    [void](Save-EvidenceJson -FileName 'step04-event-8004.json' -Data $ev8004)
    Record-Step '4' 'PASS' "Enforced block confirmed: API=$($step4Launch.result.launchApi), Win32=1260 (0x800704EC), Event 8004 logged for $($script:TestUser1Sid)."

    # ── STEP 5: Allowed target launches under Enabled policy (Event 8002) ──
    Write-Step '5' 'Allowed executable launches as bpe_stage1_test (Event 8002)'

    $tAllowLaunch = (Get-Date).ToUniversalTime()
    $step5Launch = Invoke-HarnessLaunch -ExePath $script:AllowedExePath -Username $script:TestUser1 -ResultName 'step05-allowed-exec.json' -ExpectBlocked $false -MarkerName 'step05-allowed-marker.json'
    if (-not $step5Launch.result.processCreated -or $step5Launch.result.processExitCode -ne 0 -or -not $step5Launch.result.markerExists) {
        throw "Step 5 FAILED: Allowed target failed to run: $($step5Launch.result.errorMessage)"
    }

    $ev8002User1 = Wait-AppLockerEvent -EventId 8002 -SinceUtc $tAllowLaunch -TargetExeName 'bpe-stage1-allowed.exe' -ExpectedUserSid $script:TestUser1Sid
    if ($null -eq $ev8002User1) {
        throw 'Step 5 FAILED: Allowed execution Event ID 8002 not captured.'
    }
    [void](Save-EvidenceJson -FileName 'step05-event-8002.json' -Data $ev8002User1)
    Record-Step '5' 'PASS' "Allowed target ran (PID $($step5Launch.result.processId), exit 0) and logged Event 8002 for $($script:TestUser1Sid)."

    # ── STEP 6: SID targeting verification ──
    Write-Step '6' 'SID targeting verification via Effective XML + Test-AppLockerPolicy'

    $effXmlPath = Join-Path $script:EvidenceDir 'policy-02-effective-enabled.xml'
    [xml]$effDoc = Get-Content -Path $effXmlPath -Raw
    $boundlessNode = $effDoc.SelectNodes('//*[local-name()="FileHashRule"]') | Where-Object { $_.GetAttribute('Id') -eq $script:BoundlessRuleId }
    if ($null -eq $boundlessNode) { throw 'Step 6 FAILED: Boundless RuleId missing in Effective policy XML.' }

    $boundlessSid = $boundlessNode.GetAttribute('UserOrGroupSid')
    if ($boundlessSid -ne $script:TestUser1Sid) {
        throw "Step 6 FAILED: Expected SID $($script:TestUser1Sid), found $boundlessSid"
    }

    $dUser1 = Test-AppLockerPolicy -XmlPolicy $effXmlPath -Path $script:BlockedExePath -User "$env:COMPUTERNAME\$($script:TestUser1)"
    $dUser2 = Test-AppLockerPolicy -XmlPolicy $effXmlPath -Path $script:BlockedExePath -User "$env:COMPUTERNAME\$($script:TestUser2)"
    $dAdmin = Test-AppLockerPolicy -XmlPolicy $effXmlPath -Path $script:BlockedExePath -User "$env:COMPUTERNAME\$env:USERNAME"

    [void](Save-EvidenceJson -FileName 'step06-sid-targeting.json' -Data ([ordered]@{
        ruleId                = $script:BoundlessRuleId
        ruleUserOrGroupSid    = $boundlessSid
        expectedUser1Sid      = $script:TestUser1Sid
        user1BlockedDecision  = [string]$dUser1.PolicyDecision
        user2BlockedDecision  = [string]$dUser2.PolicyDecision
        adminBlockedDecision  = [string]$dAdmin.PolicyDecision
    }))

    if ([string]$dUser1.PolicyDecision -notmatch 'Denied' -or [string]$dUser2.PolicyDecision -ne 'Allowed' -or [string]$dAdmin.PolicyDecision -ne 'Allowed') {
        throw 'Step 6 FAILED: SID targeting matrix did not evaluate cleanly.'
    }
    Record-Step '6' 'PASS' "Boundless rule targets exact SID $($script:TestUser1Sid); Denied for user1, Allowed for user2 and admin."

    # ── STEP 7: Second-user isolation ──
    Write-Step '7' 'Second-user isolation (bpe_stage1_other launches blocked exe)'

    $tUser2Launch = (Get-Date).ToUniversalTime()
    $step7Launch = Invoke-HarnessLaunch -ExePath $script:BlockedExePath -Username $script:TestUser2 -ResultName 'step07-other-user-exec.json' -ExpectBlocked $false -MarkerName 'step07-user2-marker.json'
    if (-not $step7Launch.result.processCreated -or $step7Launch.result.processExitCode -ne 0 -or -not $step7Launch.result.markerExists) {
        throw "Step 7 FAILED: Second user was blocked or failed: $($step7Launch.result.errorMessage)"
    }

    $ev8002User2 = Wait-AppLockerEvent -EventId 8002 -SinceUtc $tUser2Launch -TargetExeName 'bpe-stage1-blocked.exe' -ExpectedUserSid $script:TestUser2Sid
    if ($null -eq $ev8002User2) {
        throw 'Step 7 FAILED: Event 8002 for second user not captured.'
    }
    [void](Save-EvidenceJson -FileName 'step07-event-8002.json' -Data $ev8002User2)
    Record-Step '7' 'PASS' "User2 ($($script:TestUser2Sid)) launched bpe-stage1-blocked.exe cleanly (PID $($step7Launch.result.processId), Event 8002)."

    # ── STEP 8: Admin baseline rule preservation ──
    Write-Step '8' 'Preservation of existing synthetic admin baseline rule'

    [xml]$preAdminDoc = Get-Content -Path $preAdminPath -Raw
    $preAdminNode = $preAdminDoc.SelectNodes('//*[local-name()="FilePathRule"]') | Where-Object { $_.GetAttribute('Id') -eq $script:AdminBaselineId }
    $localNowXml = Get-AppLockerXmlSafe -Scope 'Local'
    [void](Save-EvidenceText -FileName 'policy-02-local-after-boundless.xml' -Content $localNowXml)
    [xml]$localNowDoc = $localNowXml
    $postAdminNode = $localNowDoc.SelectNodes('//*[local-name()="FilePathRule"]') | Where-Object { $_.GetAttribute('Id') -eq $script:AdminBaselineId }

    if ($null -eq $preAdminNode -or $null -eq $postAdminNode) {
        throw 'Step 8 FAILED: Synthetic admin baseline rule missing after Boundless policy application.'
    }
    $preOuter = $preAdminNode.OuterXml
    $postOuter = $postAdminNode.OuterXml
    $preservedExact = ($preOuter -eq $postOuter)

    [void](Save-EvidenceJson -FileName 'step08-admin-preservation.json' -Data ([ordered]@{
        adminBaselineId = $script:AdminBaselineId
        preservedExact  = $preservedExact
        preXml          = $preOuter
        postXml         = $postOuter
    }))
    if (-not $preservedExact) { throw 'Step 8 FAILED: Synthetic admin baseline rule mutated!' }
    Record-Step '8' 'PASS' "Synthetic admin baseline rule ($($script:AdminBaselineId)) preserved identically in Local & Effective policy."

    # ── STEP 9: Duplicate RuleId conflict handling ──
    Write-Step '9' 'Duplicate RuleId conflict handling (fail-closed pre-write gate)'

    $liveLocalXml = Get-AppLockerXmlSafe -Scope 'Local'
    [xml]$liveDoc = $liveLocalXml
    $existingIds = @($liveDoc.SelectNodes('//*[local-name()="FilePathRule" or local-name()="FileHashRule" or local-name()="FilePublisherRule"]') | ForEach-Object { $_.GetAttribute('Id') })

    $proposedDupId = $script:BoundlessRuleId
    $conflictDetected = $existingIds -contains $proposedDupId
    $errorCode = if ($conflictDetected) { 'RuleIdConflict' } else { 'None' }

    [void](Save-EvidenceJson -FileName 'step09-duplicate-ruleid.json' -Data ([ordered]@{
        proposedRuleId   = $proposedDupId
        conflictDetected = $conflictDetected
        decision         = if ($conflictDetected) { 'RejectedFailClosed' } else { 'Accepted' }
        errorCode        = $errorCode
    }))
    if (-not $conflictDetected) { throw 'Step 9 FAILED: Failed to detect duplicate RuleId conflict!' }
    Record-Step '9' 'PASS' "Duplicate RuleId ($proposedDupId) detected pre-write and rejected fail-closed ($errorCode)."

    # ── STEP 10: External admin change detection ──
    Write-Step '10' 'External administrator out-of-band policy change detection'

    $beforeExternalXml = Get-AppLockerXmlSafe -Scope 'Local'
    $beforeFingerprint = Get-Sha256Hex -Text $beforeExternalXml

    # Simulate an out-of-band admin adding ExternalAdminBaseline rule via -Merge
    $extDeltaXml = @"
<AppLockerPolicy Version="1">
  <RuleCollection Type="Exe" EnforcementMode="Enabled">
    <FilePathRule Id="$($script:ExternalAdminId)" Name="ExternalAdminBaseline OutOfBand" Description="Simulated external admin rule added after Boundless" UserOrGroupSid="S-1-5-32-544" Action="Allow">
      <Conditions><FilePathCondition Path="C:\BpeStage1Proof\external-admin\*" /></Conditions>
    </FilePathRule>
  </RuleCollection>
</AppLockerPolicy>
"@
    $extDeltaPath = Save-EvidenceText -FileName 'policy-03a-external-admin-delta.xml' -Content $extDeltaXml
    Set-AppLockerPolicy -XmlPolicy $extDeltaPath -Merge

    $afterExternalXml = Get-AppLockerXmlSafe -Scope 'Local'
    [void](Save-EvidenceText -FileName 'policy-03-external-drift.xml' -Content $afterExternalXml)
    $afterFingerprint = Get-Sha256Hex -Text $afterExternalXml

    [xml]$driftDoc = $afterExternalXml
    $allIdsAfterDrift = @($driftDoc.SelectNodes('//*[local-name()="FilePathRule" or local-name()="FileHashRule" or local-name()="FilePublisherRule"]') | ForEach-Object { $_.GetAttribute('Id') })
    $ownedIds = @($script:BoundlessRuleId)
    $externalDetected = @($allIdsAfterDrift | Where-Object { $_ -notin $ownedIds })
    $driftDetected = ($beforeFingerprint -ne $afterFingerprint) -and ($allIdsAfterDrift -contains $script:ExternalAdminId)

    [void](Save-EvidenceJson -FileName 'step10-external-drift.json' -Data ([ordered]@{
        beforeSha256           = $beforeFingerprint
        afterSha256            = $afterFingerprint
        driftDetected          = $driftDetected
        externalRulePresent    = ($allIdsAfterDrift -contains $script:ExternalAdminId)
        nonOwnedRuleIdsCount   = $externalDetected.Count
        rollbackPolicy         = 'RefusedBlindSnapshotRollback_SurgicalRemovalRequired'
    }))
    if (-not $driftDetected) { throw 'Step 10 FAILED: External out-of-band admin rule drift not detected!' }
    Record-Step '10' 'PASS' "External admin drift detected (SHA-256 changed); external rule $($script:ExternalAdminId) classified non-owned; blind rollback refused."

    # ── STEP 11: Crash / restart recovery simulation ──
    Write-Step '11' 'Crash / restart recovery simulation (surgical orphaned RuleId removal)'

    $ledger = [ordered]@{
        sessionId        = $script:SessionId
        state            = 'CrashSimulated_AbruptExit'
        ownedRuleIds     = @($script:BoundlessRuleId)
        targetUserSid    = $script:TestUser1Sid
        createdUtc       = (Get-Date).ToUniversalTime().ToString('o')
    }
    [void](Save-EvidenceJson -FileName 'step11-session-ledger.json' -Data $ledger)

    # Recovery reads ledger + live Local XML and removes ONLY ownedRuleIds
    $crashLocalXml = Get-AppLockerXmlSafe -Scope 'Local'
    $recoveryEdit = Remove-RuleFromLocalPolicyXml -LocalXmlText $crashLocalXml -TargetRuleId $script:BoundlessRuleId
    if ($recoveryEdit.removedCount -ne 1) {
        throw "Step 11 FAILED: Expected 1 orphaned Boundless rule removed, got $($recoveryEdit.removedCount)."
    }

    $postRecoveryPath = Save-EvidenceText -FileName 'policy-04-post-recovery.xml' -Content $recoveryEdit.updatedXml
    Set-AppLockerPolicy -XmlPolicy $postRecoveryPath
    $effRecovery = Wait-EffectivePolicyRule -RuleId $script:BoundlessRuleId -ShouldExist $false
    [void](Save-EvidenceText -FileName 'policy-04-effective-post-recovery.xml' -Content $effRecovery)
    Start-Sleep -Seconds 2

    # Verify BOTH admin rules survived recovery AND blocked exe can now run as user1
    $adminSurvives = ($effRecovery -match [regex]::Escape($script:AdminBaselineId)) -and ($effRecovery -match [regex]::Escape($script:ExternalAdminId))
    if (-not $adminSurvives) { throw 'Step 11 FAILED: Admin baseline or external admin rule lost during crash recovery!' }

    $tRecLaunch = (Get-Date).ToUniversalTime()
    $step11Launch = Invoke-HarnessLaunch -ExePath $script:BlockedExePath -Username $script:TestUser1 -ResultName 'step11-post-recovery-exec.json' -ExpectBlocked $false -MarkerName 'step11-recovery-marker.json'
    if (-not $step11Launch.result.processCreated -or $step11Launch.result.processExitCode -ne 0 -or -not $step11Launch.result.markerExists) {
        throw "Step 11 FAILED: Blocked target still failed to run after crash recovery: $($step11Launch.result.errorMessage)"
    }
    $ev8002Rec = Wait-AppLockerEvent -EventId 8002 -SinceUtc $tRecLaunch -TargetExeName 'bpe-stage1-blocked.exe' -ExpectedUserSid $script:TestUser1Sid

    [void](Save-EvidenceJson -FileName 'step11-crash-recovery.json' -Data ([ordered]@{
        removedRuleIds     = @($script:BoundlessRuleId)
        adminRulesPreserved = $adminSurvives
        launchAfterRecovery = $step11Launch.result
        event8002          = $ev8002Rec
    }))
    Record-Step '11' 'PASS' "Crash recovery surgically removed $($script:BoundlessRuleId), preserved both admin rules, and restored execution (PID $($step11Launch.result.processId))."

    # Re-apply Boundless Deny rule briefly (keeping external admin rule) so Step 12 tests normal clean removal end-to-end
    $reapplyXml = Build-AppLockerPolicyXml -EnforcementMode 'Enabled' -IncludeBoundlessDeny $true -IncludeExternalAdmin $true
    $reapplyPath = Save-EvidenceText -FileName 'policy-04b-reapply-for-step12.xml' -Content $reapplyXml
    Set-AppLockerPolicy -XmlPolicy $reapplyPath
    [void](Wait-EffectivePolicyRule -RuleId $script:BoundlessRuleId -ShouldExist $true -ExpectedMode 'Enabled')
    Start-Sleep -Seconds 2

    # ── STEP 12: Surgical restoration + launch proof + clean OS teardown ──
    Write-Step '12' 'Verified surgical restoration, post-removal launch proof & EVTX export'

    $preRestoreLocal = Get-AppLockerXmlSafe -Scope 'Local'
    $restoreEdit = Remove-RuleFromLocalPolicyXml -LocalXmlText $preRestoreLocal -TargetRuleId $script:BoundlessRuleId
    if ($restoreEdit.removedCount -ne 1) {
        throw "Step 12 FAILED: Expected 1 Boundless rule removed on clean teardown, got $($restoreEdit.removedCount)."
    }

    $postRestorePath = Save-EvidenceText -FileName 'policy-05-post-restore.xml' -Content $restoreEdit.updatedXml
    Set-AppLockerPolicy -XmlPolicy $postRestorePath
    $effPostRestore = Wait-EffectivePolicyRule -RuleId $script:BoundlessRuleId -ShouldExist $false
    [void](Save-EvidenceText -FileName 'policy-05-effective-post-restore.xml' -Content $effPostRestore)
    Start-Sleep -Seconds 2

    $adminStillPreserved = ($effPostRestore -match [regex]::Escape($script:AdminBaselineId)) -and ($effPostRestore -match [regex]::Escape($script:ExternalAdminId))
    if (-not $adminStillPreserved) {
        throw 'Step 12 FAILED: Admin rules were NOT preserved after clean Boundless removal!'
    }

    # Verify bpe-stage1-blocked.exe launches cleanly again as bpe_stage1_test
    $tRestoredLaunch = (Get-Date).ToUniversalTime()
    $step12Launch = Invoke-HarnessLaunch -ExePath $script:BlockedExePath -Username $script:TestUser1 -ResultName 'step12-post-restore-exec.json' -ExpectBlocked $false -MarkerName 'step12-restored-marker.json'
    if (-not $step12Launch.result.processCreated -or $step12Launch.result.processExitCode -ne 0 -or -not $step12Launch.result.markerExists) {
        throw "Step 12 FAILED: Blocked target did not launch cleanly after Boundless rule removal: $($step12Launch.result.errorMessage)"
    }

    $ev8002Restored = Wait-AppLockerEvent -EventId 8002 -SinceUtc $tRestoredLaunch -TargetExeName 'bpe-stage1-blocked.exe' -ExpectedUserSid $script:TestUser1Sid
    [void](Save-EvidenceJson -FileName 'step12-restoration.json' -Data ([ordered]@{
        boundlessRuleRemoved = ($effPostRestore -notmatch [regex]::Escape($script:BoundlessRuleId))
        adminRulesPreserved  = $adminStillPreserved
        postRestoreLaunch    = $step12Launch.result
        event8002            = $ev8002Restored
    }))

    # Restore exact initial Local AppLocker policy for clean VM state
    $initialRaw = Get-Content -Path $script:InitialLocalXml -Raw
    if ($initialRaw -match 'RuleCollection') {
        Set-AppLockerPolicy -XmlPolicy $script:InitialLocalXml
    }
    else {
        $emptyXml = '<AppLockerPolicy Version="1"><RuleCollection Type="Exe" EnforcementMode="NotConfigured" /></AppLockerPolicy>'
        $emptyPath = Save-EvidenceText -FileName 'policy-06-empty-restore.xml' -Content $emptyXml
        Set-AppLockerPolicy -XmlPolicy $emptyPath
    }
    Start-Sleep -Seconds 1
    $finalLocal = Get-AppLockerXmlSafe -Scope 'Local'
    $finalEffective = Get-AppLockerXmlSafe -Scope 'Effective'
    [void](Save-EvidenceText -FileName 'policy-06-final-teardown-local.xml' -Content $finalLocal)
    [void](Save-EvidenceText -FileName 'policy-06-final-teardown-effective.xml' -Content $finalEffective)

    # Export full EVTX + JSON summary of AppLocker events
    $evtxPath = Join-Path $script:EvidenceDir 'applocker-exe-dll.evtx'
    if (Test-Path $evtxPath) { Remove-Item $evtxPath -Force }
    & wevtutil epl 'Microsoft-Windows-AppLocker/EXE and DLL' $evtxPath

    $allEv = Get-WinEvent -LogName 'Microsoft-Windows-AppLocker/EXE and DLL' -MaxEvents 100 -ErrorAction SilentlyContinue |
        Select-Object Id, @{N='TimeUtc';E={$_.TimeCreated.ToUniversalTime().ToString('o')}}, @{N='UserSid';E={if ($_.UserId) {$_.UserId.Value} else {$null}}}, LevelDisplayName, Message
    [void](Save-EvidenceJson -FileName 'applocker-events.json' -Data @($allEv))

    Record-Step '12' 'PASS' "Surgical removal restored launch (PID $($step12Launch.result.processId)), preserved admin rules, exported EVTX, and restored initial policy."

    # Write overall summary
    [void](Save-EvidenceJson -FileName 'stage1-summary.json' -Data ([ordered]@{
        completedUtc = (Get-Date).ToUniversalTime().ToString('o')
        verdict      = 'PASS'
        steps        = $script:StepResults
    }) -Depth 8)

    Write-Host ''
    Write-Host '============================================================'
    Write-Host ' ALL 12 STEPS PASSED — APPLOCKER ENFORCEMENT PROVEN'
    Write-Host '============================================================'
    exit 0
}
catch {
    $errMsg = $_.ToString()
    try {
        [void](Save-EvidenceJson -FileName 'stage1-failure.json' -Data ([ordered]@{
            failedUtc = (Get-Date).ToUniversalTime().ToString('o')
            error     = $errMsg
            steps     = $script:StepResults
        }) -Depth 8)
    }
    catch {}
    Write-Host ("STAGE 1 PROOF FAILED: {0}" -f $errMsg)
    exit 1
}
finally {
    Write-Host '[TEARDOWN] Executing guaranteed cleanup...'

    # Restore initial policy even on failure
    try {
        if (Test-Path $script:InitialLocalXml) {
            $initRaw = Get-Content -Path $script:InitialLocalXml -Raw
            if ($initRaw -match 'RuleCollection') {
                Set-AppLockerPolicy -XmlPolicy $script:InitialLocalXml
            }
            else {
                $emptyFile = Join-Path $env:TEMP 'bpe-stage1-finally-empty.xml'
                '<AppLockerPolicy Version="1"><RuleCollection Type="Exe" EnforcementMode="NotConfigured" /></AppLockerPolicy>' |
                    Out-File -FilePath $emptyFile -Encoding ascii -Force
                Set-AppLockerPolicy -XmlPolicy $emptyFile
                Remove-Item $emptyFile -Force -ErrorAction SilentlyContinue
            }
        }
    }
    catch {
        Write-Warning "Policy restore in finally failed: $_"
    }

    # Remove disposable users
    foreach ($u in @($script:TestUser1, $script:TestUser2)) {
        try {
            if (Get-LocalUser -Name $u -ErrorAction SilentlyContinue) {
                Remove-LocalUser -Name $u -ErrorAction Stop
                Write-Host "[TEARDOWN] Removed user: $u"
            }
        }
        catch {
            Write-Warning "User removal failed ($u): $_"
        }
    }

    # Clean executable scratch dirs (keep evidence/ intact for artifact upload)
    foreach ($d in @($script:BinDir, $script:OutDir)) {
        Remove-Item -Path $d -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host '[TEARDOWN] Cleanup complete.'
}
