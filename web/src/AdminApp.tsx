import { freshToken } from "./auth";
import { useEffect, useState } from "react";
import { MapContainer, TileLayer } from "react-leaflet";
import { LineChart, Line, XAxis, YAxis, Tooltip as ChartTooltip, ResponsiveContainer } from "recharts";
import * as signalR from "@microsoft/signalr";
import "leaflet/dist/leaflet.css";
import AnimatedMarker from "./AnimatedMarker";

type Metrics = {
  activeDrivers: number;
  activeTrips: number;
  tripsPerMinute: number;
  avgWaitSeconds: number;
  matchRate: number;
  requested: number;
  completed: number;
  noDriver: number;
  paymentFailed: number;
  revenue: number;
};
type DriverPos = { driverId: string; lat: number; lng: number; heading: number; speed: number };
type Point = { t: string; trips: number; drivers: number };

function Card({ label, value }: { label: string; value: string | number }) {
  return (
    <div style={{ background: "white", padding: "10px 14px", borderRadius: 8, minWidth: 120, boxShadow: "0 1px 4px rgba(0,0,0,0.2)" }}>
      <div style={{ fontSize: 12, color: "#555" }}>{label}</div>
      <div style={{ fontSize: 24, fontWeight: 700 }}>{value}</div>
    </div>
  );
}

export default function AdminApp() {
  const [m, setM] = useState<Metrics | null>(null);
  const [history, setHistory] = useState<Point[]>([]);
  const [drivers, setDrivers] = useState<Record<string, DriverPos>>({});
  const [status, setStatus] = useState("connecting");

  useEffect(() => {
    const metrics = new signalR.HubConnectionBuilder()
      .withUrl("http://localhost:5005/hubs/metrics")
      .withAutomaticReconnect()
      .build();
    metrics.on("DashboardMetrics", (d: Metrics) => {
      setM(d);
      setHistory((h) => [...h.slice(-59), { t: new Date().toLocaleTimeString(), trips: d.tripsPerMinute, drivers: d.activeDrivers }]);
    });
    metrics.start().then(() => setStatus("connected")).catch(() => {});

    const live = new signalR.HubConnectionBuilder()
      .withUrl("http://localhost:5002/hubs/ride", { accessTokenFactory: async () => (await freshToken()) ?? "" })
      .withAutomaticReconnect()
      .build();
    live.on("DriverLocationUpdated", (d: DriverPos) =>
      setDrivers((prev) => ({ ...prev, [d.driverId]: d }))
    );
    live.start().catch(() => {});

    return () => {
      metrics.stop();
      live.stop();
    };
  }, []);

  return (
    <div style={{ height: "100%", display: "flex", flexDirection: "column", fontFamily: "sans-serif", background: "#f1f5f9" }}>
      <div style={{ padding: 12, display: "flex", gap: 12, flexWrap: "wrap", alignItems: "center" }}>
        <h2 style={{ margin: 0, marginRight: 12 }}>Admin <small style={{ fontSize: 12, color: status === "connected" ? "#16a34a" : "#dc2626" }}>{status}</small></h2>
        <Card label="Active drivers" value={m?.activeDrivers ?? 0} />
        <Card label="Active trips" value={m?.activeTrips ?? 0} />
        <Card label="Trips / min" value={m?.tripsPerMinute ?? 0} />
        <Card label="Avg wait (s)" value={m?.avgWaitSeconds ?? 0} />
        <Card label="Match rate %" value={m?.matchRate ?? 0} />
        <Card label="Completed" value={m?.completed ?? 0} />
        <Card label="No driver" value={m?.noDriver ?? 0} />
        <Card label="Payment failed" value={m?.paymentFailed ?? 0} />
        <Card label="Revenue" value={(m?.revenue ?? 0).toFixed(2)} />
      </div>

      <div style={{ display: "flex", flex: 1, minHeight: 0, gap: 12, padding: "0 12px 12px" }}>
        <div style={{ flex: 1, background: "white", borderRadius: 8, padding: 12, minWidth: 0 }}>
          <b>Trips per minute and active drivers</b>
          <ResponsiveContainer width="100%" height="90%">
            <LineChart data={history}>
              <XAxis dataKey="t" hide />
              <YAxis allowDecimals={false} />
              <ChartTooltip />
              <Line type="monotone" dataKey="trips" stroke="#2563eb" dot={false} isAnimationActive={false} />
              <Line type="monotone" dataKey="drivers" stroke="#16a34a" dot={false} isAnimationActive={false} />
            </LineChart>
          </ResponsiveContainer>
        </div>
        <div style={{ flex: 2, borderRadius: 8, overflow: "hidden", minWidth: 0 }}>
          <MapContainer center={[52.52, 13.405]} zoom={12} style={{ height: "100%", width: "100%" }}>
            <TileLayer attribution="&copy; OpenStreetMap contributors" url="https://tile.openstreetmap.org/{z}/{x}/{y}.png" />
            {Object.values(drivers).map((d) => (
              <AnimatedMarker key={d.driverId} id={d.driverId} lat={d.lat} lng={d.lng} speed={d.speed} />
            ))}
          </MapContainer>
        </div>
      </div>
    </div>
  );
}


