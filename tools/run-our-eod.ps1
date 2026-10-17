# Detached: aggregate every by-isin cash 1-min file into daily bars.
$root = "C:\Users\dpven\fyers-data"
$py   = "C:\Users\dpven\source\repos\trading\.venv\Scripts\python.exe"
$log  = "$root\_tools\our_eod.log"
$p = Start-Process -FilePath $py -ArgumentList "$root\_tools\build_our_eod.py" `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err" -WindowStyle Hidden -PassThru
"started build_our_eod PID $($p.Id); log=$log"
