import { defineConfig, type Connect } from "vite";
import react from "@vitejs/plugin-react";
import { staffLogin } from "./src/r8/demo/fixtures.ts";
import { createDemoFetch } from "./src/r8/demo/transport.ts";

const host = "127.0.0.1";

function fixtureApi(): Connect.NextHandleFunction {
  const demoFetch = createDemoFetch();

  return async (req, res, next) => {
    const path = req.url?.split("?", 1)[0] ?? "";
    if (!/^\/(auth|revenant|timeline|meta|hubs)(\/|$)/.test(path)) return next();
    if (req.headers.origin && req.headers.origin !== `http://${req.headers.host}`) {
      res.writeHead(403, { "Content-Type": "application/json", "Cache-Control": "no-store" });
      res.end(JSON.stringify({ title: "Use the local preview origin" }));
      return;
    }

    try {
      let body = "";
      for await (const chunk of req) body += chunk;
      const response = await demoFetch(path, { method: req.method, body: body || undefined });
      res.writeHead(response.status, { "Content-Type": "application/json", "Cache-Control": "no-store" });
      res.end(await response.text());
    } catch (error) {
      next(error);
    }
  };
}

export default defineConfig(({ command, isPreview }) => {
  if (command !== "serve" || isPreview) throw new Error("The R8 fixture preview requires the Vite development server.");
  return {
    cacheDir: "../../node_modules/.vite/revenant-r8-preview",
    define: { "import.meta.env.R8_PREVIEW_TOKEN": JSON.stringify(staffLogin.accessToken) },
    plugins: [react(), {
      name: "r8-visual-preview",
      configureServer(server) {
        if (server.config.server.host !== host) throw new Error("The R8 fixture preview is loopback-only.");
        server.middlewares.use(fixtureApi());
      },
    }],
    server: { host, port: 5173, strictPort: true, cors: false },
  };
});
