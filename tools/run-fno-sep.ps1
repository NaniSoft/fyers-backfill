# Detached launcher for the September-2026 F&O re-pull (futures + options, 1min).
$b   = "C:\Users\dpven\source\repos\fyers-backfill"
$dll = "$b\Fyers.Backfill\bin\Release\net10.0\Fyers.Backfill.dll"
$root = "C:\Users\dpven\fyers-data"
$cfg  = "C:\Users\dpven\AppData\Local\Temp\opencode\fb-fno-sep.yaml"
$log  = "$root\_tools\fno-sep.log"

$args = @($dll, "backfill", "--config", $cfg, "--root", $root,
          "--env", "$b\.env", "--data-dir", "$b\data")
$p = Start-Process -FilePath "dotnet" -ArgumentList $args -RedirectStandardOutput $log `
        -RedirectStandardError "$log.err" -WindowStyle Hidden -PassThru
"started fno-sep PID $($p.Id); log=$log"
