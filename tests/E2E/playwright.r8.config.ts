import { randomBytes } from "node:crypto";
import { resolve } from "node:path";
import { defineConfig } from "@playwright/test";

const root = resolve(import.meta.dirname, "../..");
const port = 18181;
const token = randomBytes(24).toString("hex");
process.env.E2E_R8_PASSWORD ??= "R8fixture!" + randomBytes(12).toString("hex");
const backend = process.env.E2E_R8_BACKEND_COMMAND ?? "dotnet tests/E2E/backend/bin/Debug/net10.0/E2E.Backend.dll";
export default defineConfig({
  testDir: ".", testMatch:"revenant-r8.spec.ts", workers:1, timeout:120_000,
  expect:{timeout:15_000}, reporter:"list",
  use:{baseURL:"http://localhost:5175", viewport:{width:390,height:844}, hasTouch:true, isMobile:true},
  webServer:[
    {command:backend,cwd:root,url:`http://localhost:${port}/__e2e/info`,timeout:60_000,reuseExistingServer:false, stderr:"pipe",
      env:{E2E_R8:"1",E2E_FIXTURE_TOKEN:token,E2E_BACKEND_PORT:String(port),E2E_BACKEND_MAX_SECONDS:"180",ASPNETCORE_ENVIRONMENT:"Testing",
        E2E_R8_PASSWORD:process.env.E2E_R8_PASSWORD}},
    {command:"npm run dev -w @sands/revenant",cwd:resolve(root,"web"),url:"http://localhost:5175",timeout:30_000,reuseExistingServer:false,
      env:{VITE_API_ORIGIN:`http://127.0.0.1:${port}`}},
  ],
});
