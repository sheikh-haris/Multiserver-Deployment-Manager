# Real end-to-end deployment on Windows: portal (Development mode) + installed agent service + real IIS.
# Run by CI after the agent is installed and Online. Needs $env:SERVER_ID and two IIS sites created by the caller:
#   ci-app  (C:\Websites\ci-app,  http port 8081, pool ci-pool)   health path /health.txt  -> deployments must SUCCEED
#   ci-bad  (C:\Websites\ci-bad,  http port 8082, pool ci-bad)    health path /missing.txt -> health check fails, must ROLL BACK
$ErrorActionPreference = 'Stop'
$portal = 'https://portal.test:8443'
$root = 'C:\Websites'

function Assert($cond, $msg) { if (-not $cond) { throw "ASSERT FAILED: $msg" } else { Write-Host "ok - $msg" } }

function New-Zip($path, [hashtable]$files) {
    Add-Type -AssemblyName System.IO.Compression
    if (Test-Path $path) { Remove-Item $path -Force }
    $fs = [System.IO.File]::Open($path, 'Create')
    $zip = New-Object System.IO.Compression.ZipArchive($fs, 'Create')
    foreach ($k in $files.Keys) {
        $e = $zip.CreateEntry($k)
        $w = New-Object System.IO.StreamWriter($e.Open())
        $w.Write($files[$k]); $w.Dispose()
    }
    $zip.Dispose(); $fs.Dispose()
}

function Login($persona) {
    $null = Invoke-WebRequest "$portal/dev-login/${persona}?returnUrl=/" -SessionVariable s -UseBasicParsing
    return $s
}

# The same chunked HTTP calls the browser makes.
function Upload-Package($session, $componentId, $env_, $file) {
    $h = @{ 'X-ICC-Request' = '1' }
    $bytes = [System.IO.File]::ReadAllBytes($file)
    $start = Invoke-RestMethod -Method Post "$portal/api/uploads" -Headers $h -WebSession $session -ContentType 'application/json' `
        -Body (@{ componentId = $componentId; environment = $env_; fileName = (Split-Path $file -Leaf); size = $bytes.Length } | ConvertTo-Json)
    for ($i = 0; $i -lt $start.totalChunks; $i++) {
        $off = $i * $start.chunkSize; $len = [Math]::Min($start.chunkSize, $bytes.Length - $off)
        $part = New-Object byte[] $len; [Array]::Copy($bytes, $off, $part, 0, $len)
        $sha = [BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($part)).Replace('-', '')
        $null = Invoke-RestMethod -Method Put "$portal/api/uploads/$($start.sessionId)/chunks/$i" -Headers ($h + @{ 'X-Chunk-Sha256' = $sha }) -WebSession $session -Body $part -ContentType 'application/octet-stream'
    }
    $null = Invoke-RestMethod -Method Post "$portal/api/uploads/$($start.sessionId)/complete" -Headers $h -WebSession $session
    for ($n = 0; $n -lt 120; $n++) {
        $info = Invoke-RestMethod "$portal/api/uploads/$($start.sessionId)" -WebSession $session
        if ($info.status -eq 3) { return $info.packageId }
        if ($info.status -in 4, 5) { throw "Upload refused: $($info.error) $($info.errors | ConvertTo-Json -Compress)" }
        Start-Sleep -Seconds 1
    }
    throw 'Upload did not finish.'
}

function Wait-Request($id, $seconds = 180) {
    for ($n = 0; $n -lt $seconds; $n++) {
        $r = Invoke-RestMethod "$portal/dev/e2e/request?id=$id"
        if ($r.status -notin 'Submitted', 'Approved', 'Running', 'Scheduled') { return $r }
        Start-Sleep -Seconds 1
    }
    throw "Request $id did not finish."
}

function New-Project($name, $health, $siteName) {
    $p = Invoke-RestMethod -Method Post "$portal/dev/e2e/project?name=$name&health=$health"
    $site = [int](& "$env:windir\system32\inetsrv\appcmd.exe" list site $siteName /text:id)
    $t = Invoke-RestMethod -Method Post "$portal/dev/e2e/link?componentId=$($p.componentId)&serverId=$env:SERVER_ID&siteId=$site"
    foreach ($g in @(@('deployer', 'Deployer'), @('approver', 'Approver'))) {
        Invoke-RestMethod -Method Post "$portal/dev/e2e/grant?persona=$($g[0])&projectId=$($p.projectId)&role=$($g[1])&env=Dev" | Out-Null
    }
    return @{ ComponentId = $p.componentId; TargetId = $t.targetId }
}

$dep = Login 'deployer'
$tmp = Join-Path $env:RUNNER_TEMP 'icc-e2e'; New-Item -ItemType Directory -Path $tmp -Force | Out-Null

# ---------- good deployment on the healthy site ----------
Set-Content "$root\ci-app\index.html" 'original' -NoNewline
Set-Content "$root\ci-app\health.txt" 'ok' -NoNewline
Set-Content "$root\ci-app\web.config" '<?xml version="1.0"?><configuration><!-- original --></configuration>' -NoNewline
New-Item -ItemType Directory -Path "$root\ci-app\uploads" -Force | Out-Null
Set-Content "$root\ci-app\uploads\user.txt" 'user data' -NoNewline

$app = New-Project 'CI-App' '/health.txt' 'ci-app'
New-Zip "$tmp\v1.zip" @{ 'index.html' = 'version 1'; 'health.txt' = 'ok'; 'web.config' = '<?xml version="1.0"?><configuration><!-- PACKAGE --></configuration>'; 'bin/app.dll' = 'dll-1'; 'uploads/injected.txt' = 'must not be written' }
$pkg = Upload-Package $dep $app.ComponentId 'Dev' "$tmp\v1.zip"
Assert ($pkg) 'package uploaded, validated and stored through the chunked API'

# the portal refuses a deployer approving their own request, and a stranger submitting
$sub = Invoke-RestMethod -Method Post "$portal/dev/e2e/submit?persona=deployer&componentId=$($app.ComponentId)&env=Dev&packageId=$pkg&targetId=$($app.TargetId)&source=CI-1"
$refused = $false
try { Invoke-RestMethod -Method Post "$portal/dev/e2e/approve-request?persona=deployer&id=$($sub.requestId)" } catch { $refused = $true }
Assert $refused 'the requester cannot approve their own request'
$refused = $false
try { Invoke-RestMethod -Method Post "$portal/dev/e2e/submit?persona=nobody&componentId=$($app.ComponentId)&env=Dev&packageId=$pkg&targetId=$($app.TargetId)&source=x" } catch { $refused = $true }
Assert $refused 'a user without a role cannot submit'

Invoke-RestMethod -Method Post "$portal/dev/e2e/approve-request?persona=approver&id=$($sub.requestId)" | Out-Null
$r = Wait-Request $sub.requestId
$r | ConvertTo-Json -Depth 5 | Write-Host
Assert ($r.status -eq 'Succeeded') 'deployment to the real IIS site succeeded'
Assert ((Get-Content "$root\ci-app\index.html" -Raw) -eq 'version 1') 'index.html was replaced'
Assert (Test-Path "$root\ci-app\bin\app.dll") 'new file was added'
Assert ((Get-Content "$root\ci-app\web.config" -Raw) -match 'original') 'protected web.config was NOT overwritten'
Assert (-not (Test-Path "$root\ci-app\uploads\injected.txt")) 'files inside a protected data folder were NOT written'
Assert ((Get-Content "$root\ci-app\uploads\user.txt" -Raw) -eq 'user data') 'user data is untouched'
Assert (-not (Test-Path "$root\ci-app\app_offline.htm")) 'app_offline.htm was removed'
Assert ((Invoke-WebRequest 'http://localhost:8081/health.txt' -UseBasicParsing).StatusCode -eq 200) 'the site answers after the deployment'
Assert (Test-Path "$env:ProgramData\IisControlCenter\backups") 'a backup exists on the server'
Assert ($r.runs[0].backupId) 'the portal recorded the backup id'

# ---------- failing health check on the second site -> automatic rollback ----------
Set-Content "$root\ci-bad\index.html" 'original-bad' -NoNewline
$bad = New-Project 'CI-Bad' '/missing.txt' 'ci-bad'
New-Zip "$tmp\bad.zip" @{ 'index.html' = 'broken release' }
$pkg2 = Upload-Package $dep $bad.ComponentId 'Dev' "$tmp\bad.zip"
$sub2 = Invoke-RestMethod -Method Post "$portal/dev/e2e/submit?persona=deployer&componentId=$($bad.ComponentId)&env=Dev&packageId=$pkg2&targetId=$($bad.TargetId)&source=CI-2"
Invoke-RestMethod -Method Post "$portal/dev/e2e/approve-request?persona=approver&id=$($sub2.requestId)" | Out-Null
$r2 = Wait-Request $sub2.requestId 240
$r2 | ConvertTo-Json -Depth 5 | Write-Host
Assert ($r2.status -eq 'RolledBack') 'a failed health check ends in RolledBack'
Assert ((Get-Content "$root\ci-bad\index.html" -Raw) -eq 'original-bad') 'the previous files were restored'
Assert (-not (Test-Path "$root\ci-bad\app_offline.htm")) 'app_offline.htm removed after rollback'

# ---------- the agent refuses a path outside its local roots even if the portal asks ----------
# (covered by unit tests; here we only prove the root was fixed at install time)
$cfg = Get-Content "$env:ProgramData\IisControlCenter\agent.config.json" -Raw | ConvertFrom-Json
Assert ($cfg.AllowedRoots -contains 'c:\websites') 'allowed roots come from the local config written by the installer'
Write-Host 'Windows deployment end-to-end passed.'
