docker compose up -d
Start-Process powershell -ArgumentList "-NoExit","-Command","cd C:\Projects\rideflow; dotnet run --project src\Services\Realtime"
Start-Process powershell -ArgumentList "-NoExit","-Command","cd C:\Projects\rideflow; dotnet run --project src\Simulator"
Start-Process powershell -ArgumentList "-NoExit","-Command","cd C:\Projects\rideflow\web; npm run dev"
