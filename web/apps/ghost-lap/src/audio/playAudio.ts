// Maps AUTHORITATIVE play events to sounds (Spec 13, task 13.7). It decides nothing about the game; it only chooses which confirmed event owns
// which cue and hands the director a stable key. Source: PlaySession.onEvent, i.e. the server's event keys after the session's own de-duplication.
//
//   CandleCollected   -> candle_eat.wav   only for the LOCAL player's pickup (a 4-player room would otherwise be a wall of pings). A spectator hears none.
//   ContactDrain      -> player_hit.wav   only when the LOCAL player is the weaker victim (event.slot is the victim, as the feed and the fear face read it)
//   BurstManual/Auto  -> blast.wav        any emitter: a Burst is a room-wide event; one per server event key (a key press alone never sounds)
//   BurstHit and every other event        no sound (a Burst hit is not the weaker-contact rule; the Burst itself already sounded)
//
// Historical ghosts never arrive here: PlayFrame.ghost is a position, not an event, and cannot create a PlayEvent.
import type { PlayEvent, PlayFrame } from "../play/playTypes.ts";
import type { PlaySession } from "../play/PlaySession.ts";
import type { SfxEvent, SfxKind } from "./AudioDirector.ts";

/** An event delivered more than this many ticks after it happened (resync/backlog) is history, not a moment to sound. 30 ticks = 1 s at 30 Hz. */
export const STALE_EVENT_TICKS = 30;

export function sfxKindFor(event: PlayEvent, frame: PlayFrame): SfxKind | null {
  const me = frame.you.slot;
  switch (event.kind) {
    case "CandleCollected": return me >= 0 && event.slot === me ? "candle" : null;
    case "ContactDrain": return me >= 0 && event.slot === me ? "hit" : null;
    case "BurstManual":
    case "BurstAuto": return "burst";
    default: return null;
  }
}

interface EventSink { playEvent(event: SfxEvent): boolean; resetEvents(): void }

/** Starts sounding a session's events. Returns the detach function (call it with the session's other teardown). */
export function attachPlayAudio(session: PlaySession, audio: EventSink): () => void {
  let identity: string | null = null;
  return session.onEvent((event, frame) => {
    if (frame.identity !== identity) {
      identity = frame.identity;
      audio.resetEvents(); // a rematch / new run: nothing remembered from the old identity may suppress the new one
    }
    const kind = sfxKindFor(event, frame);
    if (!kind) return;
    if (frame.tick - event.tick > STALE_EVENT_TICKS) return;
    audio.playEvent({ key: `${frame.identity}|${event.key}`, kind, origin: "living" });
  });
}
