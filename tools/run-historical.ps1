# Detached: pull the former tickers of renamed equities (era-bounded windows).
$b   = "C:\Users\dpven\source\repos\fyers-backfill"
$dll = "$b\Fyers.Backfill\bin\Release\net10.0\Fyers.Backfill.dll"
$root = "C:\Users\dpven\fyers-data"
$cfg  = "C:\Users\dpven\AppData\Local\Temp\opencode\fb-historical.yaml"
$log  = "$root\_tools\historical.log"

$args = @($dll, "backfill", "--config", $cfg, "--root", $root,
          "--env", "$b\.env", "--data-dir", "$b\data")
$p = Start-Process -FilePath "dotnet" -ArgumentList $args -RedirectStandardOutput $log `
        -RedirectStandardError "$log.err" -WindowStyle Hidden -PassThru
"started historical PID $($p.Id); log=$log"
