import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "./index.css";
import App from "./App.tsx";
import DriverApp from "./DriverApp.tsx";
import RiderApp from "./RiderApp.tsx";
import AdminApp from "./AdminApp.tsx";

const path = window.location.pathname;
const Page = path.startsWith("/driver")
  ? DriverApp
  : path.startsWith("/rider")
  ? RiderApp
  : path.startsWith("/admin")
  ? AdminApp
  : App;

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <Page />
  </StrictMode>
);
