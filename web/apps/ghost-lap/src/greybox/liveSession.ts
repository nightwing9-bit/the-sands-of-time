// Live solo session client (spec 06, task 6.5): polls the server's committed frames and sends keyboard intents. It owns no game
// state: the tick it aims an input at is an estimate from the last frame, the server's admission policy decides (and answers with
// its real next tick, which re-syncs the estimate). Movement is resent while held, because ghost-v1 holds a vector for at most six
// ticks without a newer input; Space is sent as its pressed state and the server Bursts only on the rising edge.
import { useEffect, useRef, useState } from "react";
import type { TransportFailure } from "@sands/contracts";
import type { GreyboxWire } from "./greyboxWire.ts";
import type { InputAnswer, Intent, LiveFrame } from "./types.ts";

const POLL_GAP_MS = 30;
const RESEND_MS = 90;
const TERMINAL = new Set(["Complete", "Failed", "Abandoned"]);

export const failureOf = (error: unknown): TransportFailure =>
  typeof error === "object" && error !== null && "failure" in error ? (error as { failure: TransportFailure }).failure : { kind: "server", status: 0 };

/** Polls frames until the session is terminal. Returns the newest frame and the last transport failure (cleared on success). */
export function useLiveFrames(wire: GreyboxWire, sessionId: string, initial: LiveFrame | null) {
  const [frame, setFrame] = useState<LiveFrame | null>(initial);
  const [failure, setFailure] = useState<TransportFailure | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      try {
        const next = await wire.frame(sessionId, controller.signal);
        if (controller.signal.aborted) return;
        setFrame(next);
        setFailure(null);
        if (TERMINAL.has(next.status)) return;
      } catch (error) {
        if (controller.signal.aborted) return;
        setFailure(failureOf(error));
      }
      timer = setTimeout(() => void poll(), POLL_GAP_MS);
    };
    void poll();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [wire, sessionId]);
  return { frame, failure };
}

const KEYS: Record<string, "up" | "down" | "left" | "right" | "burst"> = {
  ArrowUp: "up", KeyW: "up", ArrowDown: "down", KeyS: "down", ArrowLeft: "left", KeyA: "left", ArrowRight: "right", KeyD: "right", Space: "burst",
};

/**
 * Keyboard intents for a running session. `frame` is the latest server frame (for the tick estimate and sequence floor).
 * Returns the last server answer so the HUD can show a rejected input instead of hiding it.
 */
export function useKeyboardIntents(wire: GreyboxWire, sessionId: string, frame: LiveFrame | null, active: boolean) {
  const [lastAnswer, setLastAnswer] = useState<InputAnswer | null>(null);
  const held = useRef(new Set<string>());
  const sequence = useRef(-1);
  const clock = useRef<{ tick: number; at: number; rate: number }>({ tick: 0, at: performance.now(), rate: 30 });

  // Re-anchor the tick estimate and the sequence floor on every server frame.
  useEffect(() => {
    if (!frame) return;
    clock.current = { tick: frame.world.tick, at: performance.now(), rate: frame.tickRate };
    sequence.current = Math.max(sequence.current, frame.world.players[0]?.lastSequence ?? -1);
  }, [frame]);

  useEffect(() => {
    if (!active) return;
    const intent = (): Intent => {
      const h = held.current;
      const x = (h.has("right") ? 1 : 0) - (h.has("left") ? 1 : 0);
      const y = (h.has("down") ? 1 : 0) - (h.has("up") ? 1 : 0);
      return { moveX: x as Intent["moveX"], moveY: y as Intent["moveY"], requestBurst: h.has("burst") };
    };
    const estimateNextTick = () => {
      const c = clock.current;
      return c.tick + 1 + Math.floor(((performance.now() - c.at) * c.rate) / 1000);
    };
    const send = (i: Intent) => {
      sequence.current += 1;
      void wire.input(sessionId, sequence.current, estimateNextTick(), i).then(
        (answer) => {
          setLastAnswer(answer);
          sequence.current = Math.max(sequence.current, answer.lastSequence);
          if (!answer.accepted && (answer.rejection === "late_tick" || answer.rejection === "future_tick")) {
            clock.current = { ...clock.current, tick: answer.nextTick - 1, at: performance.now() };
          }
        },
        () => undefined,
      );
    };
    const onKey = (down: boolean) => (event: KeyboardEvent) => {
      const key = KEYS[event.code];
      if (!key) return;
      event.preventDefault();
      if (event.repeat) return;
      if (down) held.current.add(key);
      else held.current.delete(key);
      send(intent());
    };
    const onDown = onKey(true);
    const onUp = onKey(false);
    // Blur/hidden: release everything; the server neutralizes the player from the next tick.
    const release = () => {
      held.current.clear();
      void wire.neutral(sessionId).then((a) => {
        setLastAnswer(a);
        sequence.current = Math.max(sequence.current, a.lastSequence);
      }, () => undefined);
    };
    const onVisibility = () => document.visibilityState === "hidden" && release();
    const resend = setInterval(() => {
      const i = intent();
      if (i.moveX !== 0 || i.moveY !== 0 || i.requestBurst) send(i);
    }, RESEND_MS);
    window.addEventListener("keydown", onDown);
    window.addEventListener("keyup", onUp);
    window.addEventListener("blur", release);
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      clearInterval(resend);
      window.removeEventListener("keydown", onDown);
      window.removeEventListener("keyup", onUp);
      window.removeEventListener("blur", release);
      document.removeEventListener("visibilitychange", onVisibility);
      held.current.clear();
    };
  }, [wire, sessionId, active]);

  return lastAnswer;
}
