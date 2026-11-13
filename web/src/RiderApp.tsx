import { freshToken } from "./auth";
import { useEffect, useState } from "react";
import { MapContainer, TileLayer, CircleMarker, Tooltip, useMapEvents } from "react-leaflet";
import type { LeafletMouseEvent } from "leaflet";
import * as signalR from "@microsoft/signalr";
import "leaflet/dist/leaflet.css";
import AnimatedMarker from "./AnimatedMarker";

type Point = { lat: number; lng: number };
type TripView = { id: string; status: string; driverId: string | null; estimatedFare: number; surge?: number };
type DriverPos = { driverId: string; lat: number; lng: number; heading: number; speed: number };
type Quote = { distanceKm: number; fare: number; surge: number };

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

function ClickHandler({ onPick }: { onPick: (p: Point) => void }) {
  useMapEvents({
    click(e: LeafletMouseEvent) {
      onPick({ lat: e.latlng.lat, lng: e.latlng.lng });
    },
  });
  return null;
}

export default function RiderApp() {
  const [pickup, setPickup] = useState<Point | null>(null);
  const [dropoff, setDropoff] = useState<Point | null>(null);
  const [quote, setQuote] = useState<Quote | null>(null);
  const [trip, setTrip] = useState<TripView | null>(null);
  const [drivers, setDrivers] = useState<Record<string, DriverPos>>({});
  const [error, setError] = useState("");

  const finished = trip ? TERMINAL.includes(trip.status) : false;
  const locked = trip !== null && !finished;

  useEffect(() => {
    const c = new signalR.HubConnectionBuilder()
      .withUrl("http://localhost:5002/hubs/ride", { accessTokenFactory: async () => (await freshToken()) ?? "" })
      .withAutomaticReconnect()
      .build();
    c.on("DriverLocationUpdated", (d: DriverPos) =>
      setDrivers((prev) => ({ ...prev, [d.driverId]: d }))
    );
    c.start().catch(() => {});
    return () => {
      c.stop();
    };
  }, []);

  // price quote (with surge) from the Pricing service
  useEffect(() => {
    if (!pickup || !dropoff) {
      setQuote(null);
      return;
    }
    fetch(
      `http://localhost:5006/quote?pickupLat=${pickup.lat}&pickupLng=${pickup.lng}&dropoffLat=${dropoff.lat}&dropoffLng=${dropoff.lng}`
    )
      .then((r) => r.json())
      .then(setQuote)
      .catch(() => setQuote(null));
  }, [pickup, dropoff]);

  useEffect(() => {
    if (!trip || finished) return;
    const timer = setInterval(async () => {
      try {
        const res = await fetch(`http://localhost:5003/trips/${trip.id}`);
        if (res.ok) setTrip(await res.json());
      } catch {
        /* retry next tick */
      }
    }, 1000);
    return () => clearInterval(timer);
  }, [trip?.id, finished]);

  const pick = (p: Point) => {
    if (locked) return;
    if (!pickup || dropoff) {
      setPickup(p);
      setDropoff(null);
      setTrip(null);
    } else {
      if (Math.abs(p.lat - pickup.lat) + Math.abs(p.lng - pickup.lng) < 0.003) return;
      setDropoff(p);
    }
  };

  const request = async () => {
    if (!pickup || !dropoff) return;
    setError("");
    try {
      const res = await fetch("http://localhost:5003/trips", {
        method: "POST",
        headers: { "Content-Type": "application/json", Authorization: `Bearer ${await freshToken()}` },
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
      setTrip({ id: created.id, status: created.status, driverId: null, estimatedFare: created.estimatedFare, surge: created.surge });
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

  const tooClose = quote !== null && quote.distanceKm < 0.1;

  return (
    <div style={{ height: "100%", position: "relative" }}>
      <div style={{ position: "absolute", top: 12, left: 56, zIndex: 1000, background: "white", padding: 16, borderRadius: 8, width: 300, fontFamily: "sans-serif", boxShadow: "0 2px 8px rgba(0,0,0,0.25)" }}>
        <h3 style={{ margin: "0 0 8px" }}>Book a ride</h3>

        {!pickup && <p>Click the map to set your pickup.</p>}
        {pickup && !dropoff && <p>Now click a different spot for your drop-off.</p>}

        {pickup && dropoff && !trip && (
          <>
            {quote ? (
              <p>
                Distance: {quote.distanceKm.toFixed(2)} km
                <br />
                Estimated fare: <b>{quote.fare.toFixed(2)}</b>
                {quote.surge > 1 && (
                  <span style={{ background: "#f59e0b", color: "white", borderRadius: 4, padding: "1px 6px", marginLeft: 6 }}>
                    {quote.surge}x surge
                  </span>
                )}
              </p>
            ) : (
              <p style={{ color: "#dc2626" }}>Could not get a price. Is the Pricing service running (port 5006)?</p>
            )}
            {tooClose && <p style={{ color: "#dc2626" }}>Pickup and drop-off are the same spot. Click Clear and pick two spots further apart.</p>}
            <button onClick={request} disabled={!quote}>Request ride</button>{" "}
            <button onClick={reset}>Clear</button>
          </>
        )}

        {trip && (
          <>
            <p style={{ fontSize: 18, margin: "8px 0" }}><b>{LABELS[trip.status] ?? trip.status}</b></p>
            {trip.driverId && <p>Driver: {trip.driverId}</p>}
            <p>
              Fare: {trip.estimatedFare}
              {trip.surge && trip.surge > 1 ? ` (${trip.surge}x surge)` : ""}
            </p>
            {finished && <button onClick={reset}>New ride</button>}
          </>
        )}

        {error && <p style={{ color: "#dc2626" }}>{error}</p>}
      </div>

      <MapContainer center={[52.52, 13.405]} zoom={13} style={{ height: "100%", width: "100%" }}>
        <TileLayer attribution="&copy; OpenStreetMap contributors" url="https://tile.openstreetmap.org/{z}/{x}/{y}.png" />
        <ClickHandler onPick={pick} />
        {pickup && (
          <CircleMarker center={[pickup.lat, pickup.lng]} radius={10} pathOptions={{ color: "#15803d", fillColor: "#22c55e", fillOpacity: 1 }}>
            <Tooltip permanent>Pickup</Tooltip>
          </CircleMarker>
        )}
        {dropoff && (
          <CircleMarker center={[dropoff.lat, dropoff.lng]} radius={10} pathOptions={{ color: "#b91c1c", fillColor: "#ef4444", fillOpacity: 1 }}>
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





