# Runs the cash full-history backfill detached from the agent shell, logging to
# _tools/cash-full.log, so a server restart does not kill it. Re-runnable.
$b = "C:\Users\dpven\source\repos\fyers-backfill"
$dll = "$b\Fyers.Backfill\bin\Release\net10.0\Fyers.Backfill.dll"
$root = "C:\Users\dpven\fyers-data"
$cfg = "C:\Users\dpven\AppData\Local\Temp\opencode\fb-cash-full.yaml"
$log = "$root\_tools\cash-full.log"

$args = @($dll, "backfill", "--config", $cfg, "--root", $root, "--env", "$b\.env", "--data-dir", "$b\data")
$p = Start-Process -FilePath "dotnet" -ArgumentList $args -RedirectStandardOutput $log `
    -RedirectStandardError "$log.err" -WindowStyle Hidden -PassThru
"started PID $($p.Id); log=$log"
