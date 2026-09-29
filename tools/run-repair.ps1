# Detached: apply CandleSanitizer rules to every existing cash 1-min parquet.
$root = "C:\Users\dpven\fyers-data"
$py   = "C:\Users\dpven\source\repos\trading\.venv\Scripts\python.exe"
$log  = "$root\_tools\repair.log"
$p = Start-Process -FilePath $py -ArgumentList "$root\_tools\repair_candles.py" `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -WindowStyle Hidden -PassThru
"started repair PID $($p.Id); log=$log"
