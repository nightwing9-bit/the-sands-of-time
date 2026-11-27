// Entry/menu. Six destinations in the order fixed by skill ref 03. A destination without a supported outcome is an honest "unavailable"
// button: focusable, announces its reason, and reports it in the one live region. No fake room, run or history appears.
// 13.8: Multiplayer (create / join) and Spectate (join as spectator) open the real multiplayer journey. 13.9: Solo Run and My Histories open the solo journey.
import type { KeyboardEvent, ReactNode } from "react";
import { SHAPE_BY_SLOT } from "../config/assetRegistry.ts";
import { GameButton } from "../ui/primitives.tsx";
import type { ScreenId } from "../presentation/types.ts";
import type { MultiplayerEntry } from "../mp/MultiplayerScreen.tsx";

const DESTINATIONS = [
  { id: "multiplayer", label: "Multiplayer", primary: true, go: "multiplayer", entry: "create" },
  /** 13.9: a real 90-second Original with a real save, and the guest's own saved histories. */
  { id: "solo", label: "Solo Run", go: "solo" },
  { id: "histories", label: "My Histories", go: "histories" },
  { id: "spectate", label: "Spectate", go: "multiplayer", entry: "spectate" },
  { id: "howto", label: "How to Play", go: "howto" },
  /** 13.7: Settings opens the sound controls (music and effects volume and mute). Reduced Motion and controls help arrive with later tasks. */
  { id: "settings", label: "Settings", go: "settings" },
] as const satisfies readonly { readonly id: string; readonly label: string; readonly primary?: boolean; readonly reason?: string; readonly go?: ScreenId; readonly entry?: MultiplayerEntry }[];

export function HomeScreen({ notice, onNotice, onGo, status }: { notice: string; onNotice: (message: string) => void; onGo: (screen: ScreenId, entry?: MultiplayerEntry) => void; status: ReactNode }) {
  // Arrow keys move between menu rows; Tab still walks them in visual order.
  const onKeyDown = (event: KeyboardEvent<HTMLElement>) => {
    if (event.key !== "ArrowDown" && event.key !== "ArrowUp") return;
    const buttons = Array.from(event.currentTarget.querySelectorAll<HTMLButtonElement>("button"));
    const at = buttons.indexOf(document.activeElement as HTMLButtonElement);
    const next = buttons[(at + (event.key === "ArrowDown" ? 1 : buttons.length - 1)) % buttons.length];
    if (next) {
      event.preventDefault();
      next.focus();
    }
  };
  return (
    <div className="gl-home" data-testid="home-screen">
      <header className="gl-home-title">
        <p className="gl-eyebrow">The Sands of Time</p>
        <h1>Ghost Lap</h1>
        <p className="gl-tagline">Every run leaves a ghost behind.</p>
        <div className="gl-shapes" aria-hidden="true" data-testid="shape-marks">
          {SHAPE_BY_SLOT.map((s) => <img key={s.slot} src={s.file} alt="" width={30} height={30} data-shape={s.shape} data-status={s.status} />)}
        </div>
      </header>
      <nav className="gl-menu" aria-label="Main menu" onKeyDown={onKeyDown}>
        {DESTINATIONS.map((d) => (
          <GameButton key={d.id} variant={"primary" in d ? "primary" : "quiet"} onClick={() => onGo(d.go, "entry" in d ? d.entry : undefined)} data-testid={`menu-${d.id}`}>{d.label}</GameButton>
        ))}
      </nav>
      <p className="gl-notice" role="status" aria-live="polite" data-testid="menu-notice">{notice}</p>
      <div className="gl-home-status">{status}</div>
    </div>
  );
}
