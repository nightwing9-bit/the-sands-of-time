import { StrictMode } from "react";
import { createRoot } from "react-dom/client";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <main data-testid="app-shell"><h1>Ghost Lap</h1><p>Bootstrap shell. No domain behavior yet.</p></main>
  </StrictMode>,
);
