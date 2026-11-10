import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";

// Dev server forwards the host endpoints so the browser stays same-origin (the auth cookies are Secure and __Host- scoped).
// Override with VITE_API_ORIGIN / VITE_REALTIME_ORIGIN; the defaults are the Compose host ports.
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, ".", "VITE_");
  const api = env.VITE_API_ORIGIN ?? "http://localhost:8081";
  const realtime = env.VITE_REALTIME_ORIGIN ?? "http://localhost:8082";
  return {
    plugins: [react()],
    server: {
      port: 5174,
      strictPort: true,
      // changeOrigin stays false on purpose: the API's CSRF/origin check compares the browser's Origin with the Host it receives, and
      // Vite's string shorthand would rewrite Host to the API's (every cookie-authenticated POST would then be origin_rejected).
      proxy: {
        "/meta": { target: api, changeOrigin: false },
        "/auth": { target: api, changeOrigin: false },
        "/timeline": { target: api, changeOrigin: false },
        "/hubs": { target: realtime, ws: true, changeOrigin: false },
      },
    },
  };
});
