# RideFlow

A real-time, event-driven ride-hailing platform (Uber/Bolt style), built as a learning and portfolio project. Riders request trips, the system offers each one to the nearest driver, both sides follow the trip live on a map, prices adapt to demand with surge pricing, and an admin dashboard shows the marketplace in real time.

Everything runs locally with Docker and is free: no cloud accounts, no paid licenses.

![Admin dashboard](docs/admin.png)

## Features

- **Rider**: pick pickup and drop-off on a map, see the fare and surge badge, request a ride, follow the trip status live.
- **Driver**: go online, receive an offer with a 15-second countdown, accept or decline, then Arrived, Start trip, Complete.
- **Matching**: nearest driver within 5 km (Redis GEO), one offer at a time, up to 3 attempts, then `NoDriverFound`.
- **Pricing**: per-zone surge multiplier from 1.0x to 3.0x based on open requests versus available drivers, plus a manual override.
- **Payment (mock)**: charges on trip completion, with a configurable failure rate to test `PaymentFailed`.
- **Auth**: ASP.NET Identity, JWT access tokens (15 min), rotating refresh tokens, roles Rider / Driver / Admin.
- **Admin dashboard**: live KPIs (active drivers, trips per minute, average wait, match rate, revenue), live chart and global map.

## Architecture

```
 React (Rider / Driver / Admin)
      |  REST            ^ WebSocket (SignalR)
      v                  |
 Auth :5001   Trip :5003   Matching :5004   Pricing :5006   Realtime :5002   Analytics :5005
      \          |             |               |                 ^               |
       +---------+------ Apache Kafka (KRaft) -+-----------------+---------------+
                       |            |
                  PostgreSQL      Redis (GEO index of drivers)

 Simulator --> driver.location (Kafka)        Payment (worker) <-- trip.completed
```

| Service | Port | Responsibility |
|---|---|---|
| Auth | 5001 | Register, login, refresh, JWT |
| Realtime | 5002 | SignalR hub, pushes locations and offers to the right users |
| Trip | 5003 | Trip state machine, transactional outbox, saga reactions |
| Matching | 5004 | Nearest-driver search, offers, timeouts, accept/decline |
| Analytics | 5005 | Live KPIs, SignalR push to the dashboard |
| Pricing | 5006 | Quotes and zone surge |
| Payment | worker | Mock charge, publishes `payment.processed` / `payment.failed` |
| Simulator | worker | Fake drivers moving around Berlin |
| web | 5173 | React + TypeScript + Vite app |

Main Kafka topics: `driver.location`, `ride.requested`, `ride.offered`, `ride.matched`, `ride.match.failed`, `trip.started`, `trip.completed`, `payment.processed`, `payment.failed`, `surge.updated`.

## Patterns used

- Event-driven microservices with a database per service
- Transactional outbox (trip and event saved in one transaction)
- Saga with a state machine and compensation (no driver found, payment failed)
- At-least-once delivery with manual offset commits
- Optimistic concurrency on trips (Postgres `xmin`)
- JWT authentication with refresh-token rotation and role-based authorization
- Real-time push with SignalR groups per user and role
- Graceful degradation (Trip falls back to 1.0x surge if Pricing is down)

## Tech stack

ASP.NET Core 8, Apache Kafka, PostgreSQL 16, Redis 7, SignalR, EF Core, React 19, TypeScript, Vite, Leaflet (OpenStreetMap), Recharts, xUnit, GitHub Actions.

## Running it

Requirements: Docker Desktop, .NET 8 SDK, Node.js 20+, Git.

```powershell
git clone https://github.com/jawad2002-gif/rideflow.git
cd rideflow
cd web; npm install; cd ..

# create the Kafka topics once (see docs/topics.md or the list above), then:
.\start-dev.ps1     # starts Docker, all services, and the web app in separate windows
.\check.ps1         # prints OK / MISSING for every port
```

Each service runs in its own window. Do not type in those windows or close them while testing.

### Demo accounts

Create them once while Auth is running:

```powershell
foreach ($u in @(
  @{ email="rider1@test.com";  displayName="Rider One";  role="Rider"  },
  @{ email="driver1@test.com"; displayName="Driver One"; role="Driver" },
  @{ email="admin@test.com";   displayName="Admin";      role="Rider"  })) {
  $u.password = "Passw0rd!"
  try { Invoke-RestMethod -Method Post -Uri http://localhost:5001/auth/register -ContentType "application/json" -Body ($u | ConvertTo-Json) } catch {}
}
# promote the admin
docker exec rideflow-postgres psql -U rideflow -d auth -c "UPDATE \"AspNetUserRoles\" SET \"RoleId\" = (SELECT \"Id\" FROM \"AspNetRoles\" WHERE \"Name\"='Admin') WHERE \"UserId\" = (SELECT \"Id\" FROM \"AspNetUsers\" WHERE \"Email\"='admin@test.com');"
```

| Page | Address | Account | Password |
|---|---|---|---|
| Login | http://localhost:5173/login | | |
| Rider | http://localhost:5173/rider | rider1@test.com | Passw0rd! |
| Driver | http://localhost:5173/driver | driver1@test.com | Passw0rd! |
| Admin | http://localhost:5173/admin | admin@test.com | Passw0rd! |

The browser keeps one login at a time, so open the driver in an Incognito window and the admin in another browser.

### Making the real driver receive offers

The Simulator reads `src/Simulator/real-drivers.txt` (one driver user id per line) and simulates those drivers at the city centre, so offers reach a real login. Write the id after logging in once:

```powershell
$d = Invoke-RestMethod -Method Post -Uri http://localhost:5001/auth/login -ContentType "application/json" -Body (@{ email="driver1@test.com"; password="Passw0rd!" } | ConvertTo-Json)
Set-Content -Encoding ascii src\Simulator\real-drivers.txt $d.userId
```

Set `FakeDrivers` in `src/Simulator/Worker.cs` to 20 for a busy map. Click the pickup near the city centre so the real driver is the nearest car.

### Try a full ride

1. Rider: log in, click a pickup and a drop-off about 2 to 3 km apart, **Request ride**.
2. Driver: **Go online**, **Accept** within 15 seconds, then **Arrived**, **Start trip**, **Complete**.
3. The rider page ends on **Paid**. Watch Completed and Revenue move on the admin page.
4. Force surge: `Invoke-RestMethod -Method Post -Uri http://localhost:5006/surge/override -ContentType "application/json" -Body '{"multiplier":2}'`

## Tests and CI

```powershell
dotnet test tests\RideFlow.Tests
cd web; npm run build
```

GitHub Actions runs both on every push (`.github/workflows/ci.yml`).

## Known limitations

- The JWT signing key is a hardcoded **development value**. Move it to user-secrets or environment variables before any real deployment.
- Services call each other directly; there is no API gateway yet.
- Pricing surge override and Matching offers are kept in memory and reset on restart.
- Schemas are created with `EnsureCreated`; EF migrations are not set up.
- Trip lifecycle endpoints (arrived, start, complete) are not yet restricted to the assigned driver.

## Roadmap

API gateway (YARP), ratings, driver approval, cancellation fees, rider history, Testcontainers integration tests, Prometheus and Grafana, Docker images per service.
