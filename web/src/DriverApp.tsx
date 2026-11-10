import { useEffect, useState } from "react";
import * as signalR from "@microsoft/signalr";

type Offer = {
  tripId: string;
  driverId: string;
  pickupLat: number;
  pickupLng: number;
  expiresAt: string;
};

type ActiveTrip = { id: string; status: string };

export default function DriverApp() {
  const [driverId, setDriverId] = useState("all");
  const [active, setActive] = useState("");
  const [offer, setOffer] = useState<Offer | null>(null);
  const [trip, setTrip] = useState<ActiveTrip | null>(null);
  const [secondsLeft, setSecondsLeft] = useState(0);
  const [log, setLog] = useState<string[]>([]);

  useEffect(() => {
    if (!active) return;

    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`http://localhost:5002/hubs/ride?driverId=${active}`)
      .withAutomaticReconnect()
      .build();

    connection.on("RideOffered", (o: Offer) => setOffer(o));
    connection.start().then(() => setLog((l) => [`Online as ${active}`, ...l]));

    return () => {
      connection.stop();
    };
  }, [active]);

  useEffect(() => {
    if (!offer) return;
    const timer = setInterval(() => {
      const left = Math.ceil(
        (new Date(offer.expiresAt + "Z").getTime() - Date.now()) / 1000
      );
      if (left <= 0) {
        setOffer(null);
        setLog((l) => ["Offer expired", ...l]);
      } else {
        setSecondsLeft(left);
      }
    }, 250);
    return () => clearInterval(timer);
  }, [offer]);

  const respond = async (accepted: boolean) => {
    if (!offer) return;
    if (accepted) {
      await fetch(`http://localhost:5004/offers/${offer.tripId}/accept`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ driverId: offer.driverId }),
      });
      setTrip({ id: offer.tripId, status: "DriverAssigned" });
      setLog((l) => [`Accepted trip ${offer.tripId.slice(0, 8)}`, ...l]);
    } else {
      setLog((l) => ["Declined", ...l]);
    }
    setOffer(null);
  };

  const step = async (action: "arrived" | "start" | "complete") => {
    if (!trip) return;
    const res = await fetch(
      `http://localhost:5003/trips/${trip.id}/${action}`,
      { method: "POST" }
    );
    const body = await res.json().catch(() => null);
    if (res.ok && body) {
      setLog((l) => [`${action} -> ${body.status}`, ...l]);
      setTrip(action === "complete" ? null : { id: trip.id, status: body.status });
    } else {
      setLog((l) => [`${action} failed`, ...l]);
    }
  };

  return (
    <div style={{ fontFamily: "sans-serif", padding: 24, maxWidth: 420 }}>
      <h2>Driver app</h2>
      <input value={driverId} onChange={(e) => setDriverId(e.target.value)} />{" "}
      <button onClick={() => setActive(driverId)}>Go online</button>

      {offer && (
        <div
          style={{
            marginTop: 24,
            padding: 16,
            border: "2px solid #16a34a",
            borderRadius: 8,
          }}
        >
          <h3>New ride offer</h3>
          <p>
            Pickup: {offer.pickupLat}, {offer.pickupLng}
          </p>
          <p style={{ fontSize: 32 }}>{secondsLeft}s</p>
          <button onClick={() => respond(true)}>Accept</button>{" "}
          <button onClick={() => respond(false)}>Decline</button>
        </div>
      )}

      {trip && (
        <div style={{ marginTop: 16 }}>
          <p>
            Active trip: {trip.id.slice(0, 8)} ({trip.status})
          </p>
          <button onClick={() => step("arrived")}>Arrived</button>{" "}
          <button onClick={() => step("start")}>Start trip</button>{" "}
          <button onClick={() => step("complete")}>Complete</button>
        </div>
      )}

      <ul>
        {log.map((l, i) => (
          <li key={i}>{l}</li>
        ))}
      </ul>
    </div>
  );
}
