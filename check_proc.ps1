$procs = Get-Process | Where-Object { $_.ProcessName -like '*Shredder*' }
if ($procs) {
    $procs | Select-Object Id, ProcessName, MainWindowTitle, @{N='Mem_MB';E={[int]($_.WorkingSet64/1MB)}}, StartTime | Format-Table -AutoSize
} else {
    Write-Output 'NO_SHREDDER_PROCESS'
}
