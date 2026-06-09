# Seed EURUSD H1 bars from CSV into trading.db
$csv   = Import-Csv "tests\data\eurusd-h1-bull-2024.csv"
$db    = "data\trading.db"
$tf    = "H1"
$sym   = "EURUSD"

$sb = [System.Text.StringBuilder]::new()
foreach ($row in $csv) {
    $id  = [System.Guid]::NewGuid().ToString()
    $dt  = [DateTime]::Parse($row.DateTime).ToString("yyyy-MM-dd HH:mm:ss")
    [void]$sb.AppendLine("INSERT OR IGNORE INTO Bars (Id, Symbol, Timeframe, OpenTimeUtc, Open, High, Low, Close, Volume) VALUES ('$id','$sym','$tf','$dt','$($row.Open)','$($row.High)','$($row.Low)','$($row.Close)',$($row.Volume));")
}

$count = $csv.Count
Write-Host "Seeding $count bars..."
$sb.ToString() | sqlite3 $db
Write-Host "Seeded $count bars."
