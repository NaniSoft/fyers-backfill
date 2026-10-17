# Final offline pipeline: compact the F&O parts, organize by ISIN, verify, collate.
# Launched through WMI (not the agent shell) because a plain child process gets
# killed whenever the agent server restarts -- that is what silently killed two
# earlier F&O runs mid-flight.
$root = "C:\Users\dpven\fyers-data"
$py   = "C:\Users\dpven\source\repos\trading\.venv\Scripts\python.exe"
$log  = "$root\_tools\finish4.log"

function Say($m) { "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $m" | Tee-Object -FilePath $log -Append }

Say "final pipeline start"
Say ("parts before: " + (Get-ChildItem "$root\_parts" -Recurse -Filter *.parquet -ErrorAction SilentlyContinue | Measure-Object).Count)

Say "compacting..."
& $py "$root\_tools\compact_parts2.py" 2>&1 | Select-Object -Last 2 | ForEach-Object { Say "  $_" }
Say ("parts after: " + (Get-ChildItem "$root\_parts" -Recurse -Filter *.parquet -ErrorAction SilentlyContinue | Measure-Object).Count)
Say ("1min files: " + (Get-ChildItem "$root\1min" -Filter *.parquet -ErrorAction SilentlyContinue | Measure-Object).Count)

Say "organizing by ISIN..."
& $py "$root\_tools\organize_by_isin.py" --apply 2>&1 | Select-Object -Last 3 | ForEach-Object { Say "  $_" }

Say "verifying coverage..."
& $py "$root\_tools\verify_by_isin2.py" 2>&1 | Select-Object -First 8 | ForEach-Object { Say "  $_" }

Say ("by-isin files: " + (Get-ChildItem "$root\by-isin" -Recurse -Filter *.parquet -ErrorAction SilentlyContinue | Measure-Object).Count)
Say ("free C: " + [math]::Round((Get-PSDrive C).Free/1GB,1) + " GB")
Say "final pipeline done"
