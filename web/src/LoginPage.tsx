import { useState } from "react";
import { login, register } from "./auth";

export default function LoginPage() {
  const [mode, setMode] = useState<"login" | "register">("login");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [name, setName] = useState("");
  const [role, setRole] = useState("Rider");
  const [error, setError] = useState("");

  const submit = async () => {
    setError("");
    try {
      if (mode === "register") await register(email, password, name, role);
      const s = await login(email, password);
      window.location.href = s.role === "Admin" ? "/admin" : s.role === "Driver" ? "/driver" : "/rider";
    } catch (e) {
      setError((e as Error).message);
    }
  };

  return (
    <div style={{ fontFamily: "sans-serif", maxWidth: 320, margin: "80px auto" }}>
      <h2>RideFlow {mode === "login" ? "login" : "sign up"}</h2>
      <input placeholder="Email" value={email} onChange={(e) => setEmail(e.target.value)} style={{ width: "100%", marginBottom: 8 }} />
      <input placeholder="Password" type="password" value={password} onChange={(e) => setPassword(e.target.value)} style={{ width: "100%", marginBottom: 8 }} />
      {mode === "register" && (
        <>
          <input placeholder="Your name" value={name} onChange={(e) => setName(e.target.value)} style={{ width: "100%", marginBottom: 8 }} />
          <select value={role} onChange={(e) => setRole(e.target.value)} style={{ width: "100%", marginBottom: 8 }}>
            <option>Rider</option>
            <option>Driver</option>
          </select>
        </>
      )}
      <button onClick={submit}>{mode === "login" ? "Log in" : "Create account"}</button>{" "}
      <button onClick={() => setMode(mode === "login" ? "register" : "login")}>
        {mode === "login" ? "Need an account?" : "Have an account?"}
      </button>
      {error && <p style={{ color: "#dc2626" }}>{error}</p>}
    </div>
  );
}
