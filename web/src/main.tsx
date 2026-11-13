import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "./index.css";
import App from "./App.tsx";
import DriverApp from "./DriverApp.tsx";
import RiderApp from "./RiderApp.tsx";
import AdminApp from "./AdminApp.tsx";
import LoginPage from "./LoginPage.tsx";
import { getSession, logout } from "./auth";

const path = window.location.pathname;
const session = getSession();

function pick() {
  if (path.startsWith("/login")) return LoginPage;
  const need = path.startsWith("/driver") ? "Driver" : path.startsWith("/rider") ? "Rider" : path.startsWith("/admin") ? "Admin" : null;
  if (need && session?.role !== need) {
    window.location.replace("/login");
    return () => null;
  }
  if (path.startsWith("/driver")) return DriverApp;
  if (path.startsWith("/rider")) return RiderApp;
  if (path.startsWith("/admin")) return AdminApp;
  return App;
}
const Page = pick();

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    {session && !path.startsWith("/login") && (
      <button onClick={logout} style={{ position: "fixed", bottom: 12, right: 12, zIndex: 2000 }}>
        Log out ({session.name})
      </button>
    )}
    <Page />
  </StrictMode>
);
