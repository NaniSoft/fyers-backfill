# Waits for a fresh Fyers access token, then runs the remaining backfill passes
# (September F&O remainder, then expired-futures full history) and finishes the
# offline pipeline. A token is valid until 06:00 IST the day after it was issued,
# and a new one requires an interactive browser login -- so this just waits.
$root = "C:\Users\dpven\source\repos\fyers-backfill"
$data = "C:\Users\dpven\fyers-data"
$repo = $root
$dll  = "$repo\Fyers.Backfill\bin\Release\net10.0\Fyers.Backfill.dll"
$py   = "C:\Users\dpven\source\repos\trading\.venv\Scripts\python.exe"
$log  = "$data\_tools\resume.log"
$tmp  = "C:\Users\dpven\AppData\Local\Temp\opencode"
$tok  = "$repo\data\fyers_access_token.json"

function Say($m) { "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m" | Tee-Object -FilePath $log -Append }

# $true when the token file holds a token that has not yet passed its 06:00 IST expiry.
function Token-Fresh {
    try {
        $j = Get-Content $tok -Raw | ConvertFrom-Json
        $saved = [DateTimeOffset]::FromUnixTimeSeconds([int64]$j.saved_at).UtcDateTime
        $ist = [System.TimeZoneInfo]::FindSystemTimeZoneById('India Standard Time')
        $savedIst = [System.TimeZoneInfo]::ConvertTimeFromUtc($saved, $ist)
        $expiry = $savedIst.Date.AddHours(6).AddMinutes(5)
        if ($expiry -le $savedIst) { $expiry = $expiry.AddDays(1) }
        $nowIst = [System.TimeZoneInfo]::ConvertTimeFromUtc((Get-Date).ToUniversalTime(), $ist)
        return $nowIst -lt $expiry
    } catch { return $false }
}

$passes = @(
    @{ name = "fno-sep-remainder"; cfg = "$tmp\fb-fno-sep.yaml" },
    @{ name = "futures-full";      cfg = "$tmp\fb-futures-full.yaml" }
)

Say "resume supervisor started; waiting for a fresh token in $tok"
while (-not (Token-Fresh)) { Start-Sleep -Seconds 60 }
Say "fresh token detected"

foreach ($p in $passes) {
    Say "=== pass: $($p.name)"
    while (Token-Fresh) {
        $running = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
                     Where-Object { $_.CommandLine -like '*Fyers.Backfill*backfill*' })
        if ($running.Count -eq 0) {
            $out = "$data\_tools\$($p.name).log"
            $cmd = "cmd.exe /c cd /d `"$data`" && `"dotnet`" `"$dll`" backfill --config `"$($p.cfg)`" " +
                   "--root `"$data`" --env `"$repo\.env`" --data-dir `"$repo\data`" >> `"$out`" 2>> `"$out.err`""
            $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd }
            Say "launched $($p.name) (pid $($r.ProcessId))"
            Start-Sleep -Seconds 120
        } else {
            Start-Sleep -Seconds 30
        }
    }
    Say "$($p.name) stopped (token expired)"
    Start-Sleep -Seconds 30
}

Say "=== offline pipeline"
& $py "$data\_tools\compact_parts2.py" 2>&1 | Select-Object -Last 1 | ForEach-Object { Say "  $_" }
& $py "$data\_tools\organize_by_isin.py" --apply 2>&1 | Select-Object -Last 2 | ForEach-Object { Say "  $_" }
& $py "$data\_tools\verify_by_isin2.py" 2>&1 | Select-Object -First 7 | ForEach-Object { Say "  $_" }
& $py "$data\_tools\build_our_eod.py" 2>&1 | Select-Object -Last 1 | ForEach-Object { Say "  $_" }
& $py "$data\_tools\validate_eod.py" 2>&1 | Select-Object -Last 8 | ForEach-Object { Say "  $_" }
& $py "$data\_tools\eod_report_final.py" 2>&1 | Select-Object -First 1 | ForEach-Object { Say "  $_" }
Say "resume pipeline done"
