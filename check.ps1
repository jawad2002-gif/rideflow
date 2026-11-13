foreach ($p in 5001,5002,5003,5004,5005,5006,5173) {
  if (Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue) { Write-Host "$p OK" -ForegroundColor Green }
  else { Write-Host "$p MISSING" -ForegroundColor Red }
}
