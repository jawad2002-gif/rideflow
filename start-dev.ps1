docker compose up -d
$svc = @("src\Simulator","src\Services\Realtime","src\Services\Trip","src\Services\Matching","src\Services\Payment","src\Services\Analytics","src\Services\Pricing")
foreach ($s in $svc) {
  Start-Process powershell -ArgumentList "-NoExit","-Command","cd C:\Projects\rideflow; dotnet run --project $s"
}
Start-Process powershell -ArgumentList "-NoExit","-Command","cd C:\Projects\rideflow\web; npm run dev"


