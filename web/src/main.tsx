import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "./index.css";
import App from "./App.tsx";
import DriverApp from "./DriverApp.tsx";

const isDriver = window.location.pathname.startsWith("/driver");

createRoot(document.getElementById("root")!).render(
  <StrictMode>{isDriver ? <DriverApp /> : <App />}</StrictMode>
);