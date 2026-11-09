import { useEffect, useRef, useState } from "react";
import { CircleMarker, Tooltip } from "react-leaflet";

type Props = { id: string; lat: number; lng: number; speed: number };

const DURATION = 1000; // ms, same as the server's push interval

export default function AnimatedMarker({ id, lat, lng, speed }: Props) {
  const [pos, setPos] = useState<[number, number]>([lat, lng]);
  const current = useRef<[number, number]>([lat, lng]);

  useEffect(() => {
    const from = current.current;
    const to: [number, number] = [lat, lng];
    const start = performance.now();
    let raf = 0;

    const tick = (now: number) => {
      const t = Math.min((now - start) / DURATION, 1);
      const next: [number, number] = [
        from[0] + (to[0] - from[0]) * t,
        from[1] + (to[1] - from[1]) * t,
      ];
      current.current = next;
      setPos(next);
      if (t < 1) raf = requestAnimationFrame(tick);
    };

    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [lat, lng]);

  return (
    <CircleMarker
      center={pos}
      radius={7}
      pathOptions={{ color: "#1d4ed8", fillColor: "#3b82f6", fillOpacity: 0.9 }}
    >
      <Tooltip>
        {id} · {speed} m/s
      </Tooltip>
    </CircleMarker>
  );
}