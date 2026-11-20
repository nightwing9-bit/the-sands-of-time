// Entry. The Ghost Lap game shell is the default. The test-host shell (generic ?timeline= inspector and the spec 06 greybox) is kept
// intact for its browser tests and loads only on its own query parameters; the two never share global CSS.
const params = new URLSearchParams(window.location.search);
const testHost = ["timeline", "greybox", "original", "branch"].some((key) => params.has(key));

async function start() {
  if (testHost) {
    (await import("./testHostShell.tsx")).mountTestHostShell();
    return;
  }
  // Self-hosted Source Serif 4 and Source Sans 3 (Fontsource, WOFF2, SIL OFL 1.1), latin subset, only the weights the design uses.
  await Promise.all([
    import("@fontsource/source-serif-4/latin-400.css"),
    import("@fontsource/source-serif-4/latin-500.css"),
    import("@fontsource/source-sans-3/latin-400.css"),
    import("@fontsource/source-sans-3/latin-500.css"),
    import("@fontsource/source-sans-3/latin-600.css"),
    import("@fontsource/source-sans-3/latin-700.css"),
    import("./styles/ghost-lap-tokens.css"),
    import("./styles/stage.css"),
    import("./styles/primitives.css"),
    import("./styles/screens.css"),
    import("./styles/multiplayer.css"),
    import("./styles/solo.css"),
    import("./styles/history.css"),
    import("./styles/race.css"),
  ]);
  // Measure the first screen with the real faces, but never hold the menu hostage to a slow or failed font request.
  await Promise.race([
    Promise.all([document.fonts.load('500 1em "Source Serif 4"'), document.fonts.load('600 1em "Source Sans 3"'), document.fonts.load('400 1em "Source Sans 3"')]),
    new Promise((resolve) => setTimeout(resolve, 1500)),
  ]).catch(() => undefined);
  if (import.meta.env.DEV && params.get("screen") === "multiplayerShowcase") {
    const [{ createRoot }, { MultiplayerShowcase }] = await Promise.all([
      import("react-dom/client"), import("./showcase/MultiplayerShowcase.tsx"),
    ]);
    createRoot(document.getElementById("root")!).render(<MultiplayerShowcase />);
    return;
  }
  const [{ StrictMode }, { createRoot }, { GhostLapApp }] = await Promise.all([import("react"), import("react-dom/client"), import("./app/GhostLapApp.tsx")]);
  createRoot(document.getElementById("root")!).render(
    <StrictMode>
      <GhostLapApp />
    </StrictMode>,
  );
}

void start();
