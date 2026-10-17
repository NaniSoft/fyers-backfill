# Supervisor: keeps the September F&O pull alive.
#
# Two F&O runs died silently with an empty stderr and no Application-log crash
# entry -- they were being killed along with the agent shell's process tree.
# Win32_Process.Create spawns through the WMI service instead, so the backfill is
# not a descendant of the shell that launches it. If it dies anyway, the ledger
# makes a restart cheap: completed windows are skipped, not refetched.
$repo = "C:\Users\dpven\source\repos\fyers-backfill"
$root = "C:\Users\dpven\fyers-data"
$cfg  = "C:\Users\dpven\AppData\Local\Temp\opencode\fb-fno-sep.yaml"
$dll  = "$repo\Fyers.Backfill\bin\Release\net10.0\Fyers.Backfill.dll"
$log  = "$root\_tools\fno-supervisor.log"
$cmd  = "cmd.exe /c cd /d `"$root`" && `"dotnet`" `"$dll`" backfill --config `"$cfg`" --root `"$root`" --env `"$repo\.env`" --data-dir `"$repo\data`" >> `"$root\_tools\fno-sep.log`" 2>> `"$root\_tools\fno-sep.err`""

function Say($m) { "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m" | Tee-Object -FilePath $log -Append }

# The access token is valid until 06:00 IST; stop launching new runs after that.
$deadline = (Get-Date).Date.AddHours(6).AddMinutes(5)
Say "supervisor start; launching until $($deadline.ToString('yyyy-MM-dd HH:mm'))"

$starts = 0
while ((Get-Date) -lt $deadline) {
    $running = @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
                 Where-Object { $_.CommandLine -like '*Fyers.Backfill*backfill*' })
    if ($running.Count -eq 0) {
        $starts++
        $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd }
        Say "launched F&O pull #$starts (pid $($r.ProcessId), return $($r.ReturnValue))"
        Start-Sleep -Seconds 90
    } else {
        Start-Sleep -Seconds 30
    }
}
Say "supervisor finished: $starts launches"
