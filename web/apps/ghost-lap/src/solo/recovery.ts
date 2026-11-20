// Recovers the save of a run that finished before the page was refreshed or closed (Spec 13, task 13.9).
// The only thing remembered in the browser is the run's durable operation key (model.ts PendingRun). The server's FinishRun is idempotent for
// that key. Check the recorded key even when the page closed before it could receive the terminal server notification.
import { useCallback, useEffect, useRef, useState } from "react";
import { clearPending, interpretSave, interpretSaveError, readPending, type SaveState } from "./model.ts";
import { finishRunByKey } from "./SoloTransport.ts";
import { hubFailureText, SoloHubError, type SoloOriginalWire } from "./soloWire.ts";

export type Recovery =
  | { readonly kind: "none" }
  | { readonly kind: "interrupted" }
  | { readonly kind: "checking" }
  | { readonly kind: "saved"; readonly original: SoloOriginalWire }
  | { readonly kind: "pending"; readonly reason: string | null }
  | { readonly kind: "failed"; readonly text: string };

const fromSave = (s: SaveState): Recovery => {
  if (s.kind === "saved") return { kind: "saved", original: s.original };
  if (s.kind === "interrupted") return { kind: "interrupted" };
  if (s.kind === "pending") return { kind: "pending", reason: s.reason };
  if (s.kind === "failed") return { kind: "failed", text: `The server answered "${s.code}".` };
  return { kind: "pending", reason: null }; // "saving" after one answer: the server is still confirming
};

export function useRecovery(onSaved?: () => void): { recovery: Recovery; retry: () => void; dismiss: () => void } {
  const [recovery, setRecovery] = useState<Recovery>({ kind: "none" });
  const saved = useRef(onSaved);
  saved.current = onSaved;
  const abort = useRef<AbortController | null>(null);

  const check = useCallback(() => {
    const pending = readPending();
    if (!pending) return;
    abort.current?.abort();
    const controller = new AbortController();
    abort.current = controller;
    setRecovery({ kind: "checking" });
    void finishRunByKey(pending.key, "", controller.signal).then(
      (view) => {
        if (controller.signal.aborted) return;
        const state = interpretSave(view, 99); // a lone check never auto-retries: SavePending is shown as pending with a Retry
        if (state.kind === "saved" || state.kind === "interrupted") clearPending();
        if (state.kind === "saved") saved.current?.();
        setRecovery(fromSave(state));
      },
      (error: unknown) => {
        if (controller.signal.aborted) return;
        const state = error instanceof SoloHubError ? interpretSaveError(error.code, hubFailureText(error)) : interpretSaveError(null, hubFailureText(error));
        if (state.kind === "interrupted") clearPending();
        setRecovery(fromSave(state));
      },
    );
  }, []);

  useEffect(() => { check(); return () => abort.current?.abort(); }, [check]);
  return { recovery, retry: check, dismiss: () => setRecovery({ kind: "none" }) };
}
