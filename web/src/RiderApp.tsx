import { useEffect, useState } from "react";
import {
  MapContainer,
  TileLayer,
  CircleMarker,
  Tooltip,
  useMapEvents,
} from "react-leaflet";
import * as signalR from "@microsoft/signalr";
import "leaflet/dist/leaflet.css";
import AnimatedMarker from "./AnimatedMarker";

type Point = { lat: number; lng: number };
type TripView = { id: string; status: string; driverId: string | null; estimatedFare: number };
type DriverPos = { driverId: string; lat: number; lng: number; heading: number; speed: number };

const TERMINAL = ["Paid", "PaymentFailed", "NoDriverFound", "CancelledByRider", "CancelledByDriver"];

const LABELS: Record<string, string> = {
  Requested: "Looking for a driver...",
  Matching: "Looking for a driver...",
  DriverAssigned: "Driver is on the way",
  DriverArrived: "Your driver has arrived",
  InProgress: "Trip in progress",
  Completed: "Trip completed, processing payment...",
  Paid: "Paid. Thank you for riding!",
  PaymentFailed: "Payment failed",
  NoDriverFound: "No driver found",
  CancelledByRider: "Cancelled",
  CancelledByDriver: "Cancelled by driver",
};

function distanceKm(a: Point, b: Point) {
  const R = 6371;
  const rad = (d: number) => (d * Math.PI) / 180;
  const dLat = rad(b.lat - a.lat);
  const dLng = rad(b.lng - a.lng);
  const h =
    Math.sin(dLat / 2) ** 2 +
    Math.cos(rad(a.lat)) * Math.cos(rad(b.lat)) * Math.sin(dLng / 2) ** 2;
  return R * 2 * Math.atan2(Math.sqrt(h), Math.sqrt(1 - h));
}

function ClickHandler({ onPick }: { onPick: (p: Point) => void }) {
  useMapEvents({
    click(e) {
      onPick({ lat: e.latlng.lat, lng: e.latlng.lng });
    },
  });
  return null;
}

export default function RiderApp() {
  const [pickup, setPickup] = useState<Point | null>(null);
  const [dropoff, setDropoff] = useState<Point | null>(null);
  const [trip, setTrip] = useState<TripView | null>(null);
  const [drivers, setDrivers] = useState<Record<string, DriverPos>>({});
  const [error, setError] = useState("");

  const finished = trip ? TERMINAL.includes(trip.status) : false;
  const locked = trip !== null && !finished;

  // live driver positions from SignalR
  useEffect(() => {
    const connection = new signalR.HubConnectionBuilder()
      .withUrl("http://localhost:5002/hubs/ride")
      .withAutomaticReconnect()
      .build();
    connection.on("DriverLocationUpdated", (d: DriverPos) =>
      setDrivers((prev) => ({ ...prev, [d.driverId]: d }))
    );
    connection.start().catch(() => setError("Live updates offline"));
    return () => {
      connection.stop();
    };
  }, []);

  // poll the trip status until it reaches a final state
  useEffect(() => {
    if (!trip || finished) return;
    const timer = setInterval(async () => {
      try {
        const res = await fetch(`http://localhost:5003/trips/${trip.id}`);
        if (res.ok) setTrip(await res.json());
      } catch {
        /* ignore, retry on next tick */
      }
    }, 1000);
    return () => clearInterval(timer);
  }, [trip?.id, finished]);

  const pick = (p: Point) => {
    if (locked) return;
    if (!pickup || (pickup && dropoff)) {
      setPickup(p);
      setDropoff(null);
      setTrip(null);
    } else {
      setDropoff(p);
    }
  };

  const km = pickup && dropoff ? distanceKm(pickup, dropoff) : 0;
  const fare = pickup && dropoff ? Math.max(4, Math.round((2.5 + 1.2 * km) * 100) / 100) : 0;

  const request = async () => {
    if (!pickup || !dropoff) return;
    setError("");
    try {
      const res = await fetch("http://localhost:5003/trips", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          riderId: "rider-web",
          pickupLat: pickup.lat,
          pickupLng: pickup.lng,
          dropoffLat: dropoff.lat,
          dropoffLng: dropoff.lng,
        }),
      });
      if (!res.ok) throw new Error(String(res.status));
      const created = await res.json();
      setTrip({ id: created.id, status: created.status, driverId: null, estimatedFare: created.estimatedFare });
    } catch {
      setError("Could not request the ride. Is the Trip service running?");
    }
  };

  const reset = () => {
    setTrip(null);
    setPickup(null);
    setDropoff(null);
  };

  const visibleDrivers = trip?.driverId
    ? Object.values(drivers).filter((d) => d.driverId === trip.driverId)
    : trip
    ? []
    : Object.values(drivers);

  return (
    <div style={{ height: "100%", position: "relative" }}>
      <div
        style={{
          position: "absolute",
          top: 12,
          left: 56,
          zIndex: 1000,
          background: "white",
          padding: 16,
          borderRadius: 8,
          width: 280,
          fontFamily: "sans-serif",
          boxShadow: "0 2px 8px rgba(0,0,0,0.25)",
        }}
      >
        <h3 style={{ margin: "0 0 8px" }}>Book a ride</h3>

        {!pickup && <p>Click the map to set your pickup.</p>}
        {pickup && !dropoff && <p>Now click the map to set your drop-off.</p>}

        {pickup && dropoff && !trip && (
          <>
            <p>
              Distance: {km.toFixed(2)} km
              <br />
              Estimated fare: <b>{fare.toFixed(2)}</b>
            </p>
            <button onClick={request}>Request ride</button>{" "}
            <button onClick={reset}>Clear</button>
          </>
        )}

        {trip && (
          <>
            <p style={{ fontSize: 18, margin: "8px 0" }}>
              <b>{LABELS[trip.status] ?? trip.status}</b>
            </p>
            {trip.driverId && <p>Driver: {trip.driverId}</p>}
            <p>Fare: {trip.estimatedFare}</p>
            {finished && <button onClick={reset}>New ride</button>}
          </>
        )}

        {error && <p style={{ color: "#dc2626" }}>{error}</p>}
      </div>

      <MapContainer center={[52.52, 13.405]} zoom={13} style={{ height: "100%", width: "100%" }}>
        <TileLayer
          attribution="&copy; OpenStreetMap contributors"
          url="https://tile.openstreetmap.org/{z}/{x}/{y}.png"
        />
        <ClickHandler onPick={pick} />

        {pickup && (
          <CircleMarker center={[pickup.lat, pickup.lng]} radius={10}
            pathOptions={{ color: "#15803d", fillColor: "#22c55e", fillOpacity: 1 }}>
            <Tooltip permanent>Pickup</Tooltip>
          </CircleMarker>
        )}
        {dropoff && (
          <CircleMarker center={[dropoff.lat, dropoff.lng]} radius={10}
            pathOptions={{ color: "#b91c1c", fillColor: "#ef4444", fillOpacity: 1 }}>
            <Tooltip permanent>Drop-off</Tooltip>
          </CircleMarker>
        )}

        {visibleDrivers.map((d) => (
          <AnimatedMarker key={d.driverId} id={d.driverId} lat={d.lat} lng={d.lng} speed={d.speed} />
        ))}
      </MapContainer>
    </div>
  );
}
