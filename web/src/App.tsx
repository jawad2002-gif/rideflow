import { useEffect, useState } from "react";
import { MapContainer, TileLayer, CircleMarker, Tooltip } from "react-leaflet";
import * as signalR from "@microsoft/signalr";
import "leaflet/dist/leaflet.css";

type Driver = {
  driverId: string;
  lat: number;
  lng: number;
  heading: number;
  speed: number;
};

type Status = "connecting" | "connected" | "reconnecting" | "offline";

export default function App() {
  const [drivers, setDrivers] = useState<Record<string, Driver>>({});
  const [status, setStatus] = useState<Status>("connecting");

  useEffect(() => {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl("http://localhost:5002/hubs/ride")
      .withAutomaticReconnect()
      .build();

    connection.on("DriverLocationUpdated", (d: Driver) => {
      setDrivers((prev) => ({ ...prev, [d.driverId]: d }));
    });

    connection.onreconnecting(() => setStatus("reconnecting"));
    connection.onreconnected(() => setStatus("connected"));
    connection.onclose(() => setStatus("offline"));

    connection
      .start()
      .then(() => setStatus("connected"))
      .catch(() => setStatus("offline"));

    return () => {
      connection.stop();
    };
  }, []);

  const list = Object.values(drivers);

  return (
    <div style={{ height: "100%", position: "relative" }}>
      <div className={`banner ${status}`}>
        {status} · {list.length} drivers
      </div>
      <MapContainer
        center={[52.52, 13.405]}
        zoom={13}
        style={{ height: "100%", width: "100%" }}
      >
        <TileLayer
          attribution="&copy; OpenStreetMap contributors"
          url="https://tile.openstreetmap.org/{z}/{x}/{y}.png"
        />
        {list.map((d) => (
          <CircleMarker
            key={d.driverId}
            center={[d.lat, d.lng]}
            radius={7}
            pathOptions={{ color: "#1d4ed8", fillColor: "#3b82f6", fillOpacity: 0.9 }}
          >
            <Tooltip>
              {d.driverId} · {d.speed} m/s
            </Tooltip>
          </CircleMarker>
        ))}
      </MapContainer>
    </div>
  );
}