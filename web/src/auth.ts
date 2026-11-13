export type Session = { accessToken: string; refreshToken: string; role: string; userId: string; name: string };

const KEY = "rideflow.session";

export function getSession(): Session | null {
  try { return JSON.parse(localStorage.getItem(KEY) ?? "null"); } catch { return null; }
}
export function saveSession(s: Session | null) {
  try { s ? localStorage.setItem(KEY, JSON.stringify(s)) : localStorage.removeItem(KEY); } catch { /* ignore */ }
}

export async function login(email: string, password: string): Promise<Session> {
  const res = await fetch("http://localhost:5001/auth/login", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, password }),
  });
  if (!res.ok) throw new Error("Wrong email or password");
  const s = await res.json();
  saveSession(s);
  return s;
}

export async function register(email: string, password: string, displayName: string, role: string) {
  const res = await fetch("http://localhost:5001/auth/register", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ email, password, displayName, role }),
  });
  if (!res.ok) throw new Error((await res.json().catch(() => null))?.error ?? "Registration failed");
}

// returns a token that is still valid, refreshing it when it is about to expire
export async function freshToken(): Promise<string | null> {
  const s = getSession();
  if (!s) return null;
  try {
    const exp = JSON.parse(atob(s.accessToken.split(".")[1])).exp * 1000;
    if (exp - Date.now() > 30_000) return s.accessToken;
  } catch { /* fall through to refresh */ }
  const res = await fetch("http://localhost:5001/auth/refresh", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ refreshToken: s.refreshToken }),
  });
  if (!res.ok) { saveSession(null); return null; }
  const n = await res.json();
  saveSession(n);
  return n.accessToken;
}

export function logout() { saveSession(null); window.location.href = "/login"; }
