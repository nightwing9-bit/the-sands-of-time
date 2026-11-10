// The one shared Timeline scrubber (spec 04, task 4.2). Controlled: the parent owns the last verified `position`.
// Seeks carry a monotonic operation ID, abort the request they replace, and only the newest verified answer is reported
// through `onVerified`. Positions are decimal strings; every comparison and step goes through BigInt (via @sands/contracts).
// No game or bank rule lives here: labels, markers, previews and fork rules all come from the domain adapter.
import {
  LatestRequestGate,
  clampPosition,
  describeFailure,
  formatPosition,
  parsePosition,
  stepPosition,
  viewStateOf,
  type ApiResult,
  type TimelinePosition,
  type TransportFailure,
  type TransportViewState,
} from '@sands/contracts';
import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent, type PointerEvent } from 'react';
import type { TimelineAdapter, VerifiedPreview } from './adapter.ts';

export interface TimelineScrubberProps<TPreview = unknown> {
  adapter: TimelineAdapter<TPreview>;
  /** Last verified position, owned by the parent. */
  position: TimelinePosition;
  /** Called only with the newest verified preview. */
  onVerified: (preview: VerifiedPreview<TPreview>) => void;
  /** Called after the server accepted a fork. The source history is untouched. */
  onForked?: (result: { branchId: string }, position: TimelinePosition) => void;
  /** Domain-specific fork options, passed through untouched. */
  forkOptions?: unknown;
  /** Arrow-key and play step as a decimal string. Default "1". */
  step?: TimelinePosition;
  /** PageUp/PageDown step. Default "10". */
  pageStep?: TimelinePosition;
  /** Wait before sending a keyboard or drag seek, so a burst sends one request. Default 0. */
  seekDebounceMs?: number;
  /** Delay between play steps. Default 500. */
  playIntervalMs?: number;
  /** Overrides the `prefers-reduced-motion` media query. */
  reducedMotion?: boolean;
  createIdempotencyKey?: () => string;
}

const REDUCED_MOTION_MIN_PLAY_MS = 1000;

function useReducedMotion(override: boolean | undefined): boolean {
  const query = () =>
    typeof window !== 'undefined' && typeof window.matchMedia === 'function'
      ? window.matchMedia('(prefers-reduced-motion: reduce)').matches
      : false;
  const [value, setValue] = useState(query);
  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return;
    const list = window.matchMedia('(prefers-reduced-motion: reduce)');
    const on = () => setValue(list.matches);
    list.addEventListener?.('change', on);
    return () => list.removeEventListener?.('change', on);
  }, []);
  return override ?? value;
}

function toFailure(error: unknown): TransportFailure {
  const candidate =
    typeof error === 'object' && error !== null && 'failure' in error ? (error as { failure: unknown }).failure : error;
  if (typeof candidate === 'object' && candidate !== null && typeof (candidate as { kind?: unknown }).kind === 'string') {
    return candidate as TransportFailure;
  }
  return { kind: 'server', status: 0 };
}

function safeLabel<T>(adapter: TimelineAdapter<T>, position: TimelinePosition): string {
  try {
    return adapter.getLabel(position);
  } catch {
    return position;
  }
}

export type ForkBlockReason = 'invalid_position' | 'end_of_timeline' | 'read_only' | 'pending';

const FORK_REASON_TEXT: Record<ForkBlockReason, string> = {
  invalid_position: 'Fork is unavailable: the position is not a valid point on this timeline.',
  end_of_timeline: 'Fork is unavailable at the last position: there is nothing after it to change.',
  read_only: 'Fork is unavailable: this timeline is read-only.',
  pending: 'Fork is unavailable while a request is in progress.',
};

export function TimelineScrubber<TPreview = unknown>(props: TimelineScrubberProps<TPreview>) {
  const { adapter, position, onVerified, onForked, forkOptions } = props;
  const stepSize = parsePosition(props.step ?? '1') ?? 1n;
  const pageSize = parsePosition(props.pageStep ?? '10') ?? 10n;
  const debounceMs = props.seekDebounceMs ?? 0;
  const reduced = useReducedMotion(props.reducedMotion);
  const playMs = Math.max(props.playIntervalMs ?? 500, reduced ? REDUCED_MOTION_MIN_PLAY_MS : 0);

  const bounds = adapter.bounds;
  const start = parsePosition(bounds.start);
  const end = parsePosition(bounds.end);
  const boundsValid = start !== null && end !== null && start <= end;
  const verified = parsePosition(position);
  const verifiedValid = boundsValid && verified !== null && verified >= start && verified <= end;

  const [draft, setDraft] = useState<TimelinePosition>(position);
  const [inflight, setInflight] = useState(false);
  const [scheduled, setScheduled] = useState(false);
  const [forking, setForking] = useState(false);
  const [playing, setPlaying] = useState(false);
  const [failure, setFailure] = useState<TransportFailure | null>(null);

  const gate = useRef(new LatestRequestGate());
  const operation = useRef(0);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const alive = useRef(true);
  const fork = useRef<{ position: TimelinePosition; options: unknown; key: string } | null>(null);
  const latest = useRef({ adapter, onVerified });
  latest.current = { adapter, onVerified };

  useEffect(() => {
    alive.current = true;
    const g = gate.current;
    return () => {
      alive.current = false;
      operation.current += 1;
      if (timer.current) clearTimeout(timer.current);
      g.cancel();
    };
  }, []);

  // Follow the parent when it moves the verified position and nothing of ours is outstanding.
  useEffect(() => {
    if (!inflight && !scheduled) setDraft(position);
  }, [position, inflight, scheduled]);

  const runSeek = useCallback(async (target: TimelinePosition) => {
    const id = ++operation.current;
    setScheduled(false);
    setInflight(true);
    setFailure(null);
    const result: ApiResult<VerifiedPreview<TPreview>> = await gate.current.run(async (signal) => {
      try {
        const preview = await latest.current.adapter.seek(target, { signal, operationId: id });
        return { ok: true, value: preview, status: 200 };
      } catch (error) {
        return signal.aborted ? { ok: false, failure: { kind: 'cancelled', by: 'caller' } } : { ok: false, failure: toFailure(error) };
      }
    });
    // A newer operation (or unmount) owns the display now; this answer, success or failure, must not touch it.
    if (!alive.current || id !== operation.current) return;
    setInflight(false);
    if (!result.ok) {
      setFailure(result.failure);
      setPlaying(false);
      setDraft(position);
      return;
    }
    if (result.value.position !== target) {
      // The server verified a different point than was asked for: not a usable preview.
      setFailure({ kind: 'malformed', message: 'Preview position does not match the request.' });
      setPlaying(false);
      setDraft(position);
      return;
    }
    latest.current.onVerified(result.value);
  }, [position]);

  const requestSeek = useCallback(
    (target: TimelinePosition, debounce: boolean, fromUser = true) => {
      if (fromUser) setPlaying(false);
      setDraft(target);
      if (timer.current) clearTimeout(timer.current);
      if (debounce && debounceMs > 0) {
        setScheduled(true);
        timer.current = setTimeout(() => void runSeek(target), debounceMs);
      } else {
        timer.current = null;
        void runSeek(target);
      }
    },
    [debounceMs, runSeek],
  );

  // Play: one step after each verified position, never overlapping requests.
  useEffect(() => {
    if (!playing) return;
    if (!verifiedValid || verified === end || inflight || scheduled) {
      if (verifiedValid && verified === end) setPlaying(false);
      return;
    }
    const handle = setTimeout(() => {
      requestSeek(stepPosition(position, stepSize, bounds), false, false);
    }, playMs);
    return () => clearTimeout(handle);
  }, [playing, position, verifiedValid, verified, end, inflight, scheduled, playMs, stepSize, bounds, requestSeek]);

  const draftValue = parsePosition(draft);
  const draftValid = boundsValid && draftValue !== null && draftValue >= start && draftValue <= end;
  const shown = draftValid ? draft : verifiedValid ? position : bounds.start;
  const shownValue = parsePosition(shown) ?? 0n;
  const span = boundsValid ? end - start : 0n;
  const percent = span === 0n ? 0 : Number(((shownValue - start!) * 10000n) / span) / 100;

  const markers = useMemo(() => {
    if (!boundsValid) return [];
    return adapter.getMarkers().flatMap((marker) => {
      const value = parsePosition(marker.position);
      if (value === null || value < start || value > end) return [];
      const pct = span === 0n ? 0 : Number(((value - start) * 10000n) / span) / 100;
      return [{ ...marker, pct }];
    });
    // adapter identity covers marker changes; bounds are read through it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [adapter, boundsValid, start, end, span]);

  const busy = inflight || scheduled || forking;
  let forkBlock: ForkBlockReason | null = null;
  if (!verifiedValid) forkBlock = 'invalid_position';
  else if (verified === end) forkBlock = 'end_of_timeline';
  else if (adapter.readOnly) forkBlock = 'read_only';
  else if (busy) forkBlock = 'pending';

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    if (!boundsValid) return;
    const from = draftValid ? draft : bounds.start;
    let next: TimelinePosition | null = null;
    switch (event.key) {
      case 'ArrowRight':
      case 'ArrowUp':
        next = stepPosition(from, stepSize, bounds);
        break;
      case 'ArrowLeft':
      case 'ArrowDown':
        next = stepPosition(from, -stepSize, bounds);
        break;
      case 'PageUp':
        next = stepPosition(from, pageSize, bounds);
        break;
      case 'PageDown':
        next = stepPosition(from, -pageSize, bounds);
        break;
      case 'Home':
        next = bounds.start;
        break;
      case 'End':
        next = bounds.end;
        break;
    }
    if (next === null) return;
    event.preventDefault();
    requestSeek(next, true);
  };

  const fromPointer = (event: PointerEvent<HTMLDivElement>): TimelinePosition | null => {
    if (!boundsValid) return null;
    const rect = event.currentTarget.getBoundingClientRect();
    const width = BigInt(Math.max(Math.round(rect.width), 1));
    const offset = BigInt(Math.min(Math.max(Math.round(event.clientX - rect.left), 0), Number(width)));
    return formatPosition(start + (span * offset) / width);
  };
  const dragging = useRef(false);

  const doFork = async () => {
    if (forkBlock !== null || !verifiedValid) return;
    if (!fork.current || fork.current.position !== position || fork.current.options !== forkOptions) {
      fork.current = { position, options: forkOptions, key: (props.createIdempotencyKey ?? (() => crypto.randomUUID()))() };
    }
    const attempt = fork.current;
    setForking(true);
    setPlaying(false);
    setFailure(null);
    try {
      const result = await adapter.fork(position, forkOptions, attempt.key);
      fork.current = null; // success: the next fork is a new intent
      if (alive.current) onForked?.(result, position);
    } catch (error) {
      // The key is kept so an identical retry is idempotent.
      if (alive.current) setFailure(toFailure(error));
    } finally {
      if (alive.current) setForking(false);
    }
  };

  const viewState: TransportViewState = busy ? 'loading' : failure ? viewStateOf({ ok: false, failure }) : 'ready';
  const label = safeLabel(adapter, shown);
  const endLabel = boundsValid ? safeLabel(adapter, bounds.end) : '';
  const forkReasonId = 'timeline-fork-reason';

  return (
    <section
      aria-label="Timeline scrubber"
      aria-busy={busy}
      data-testid="timeline-scrubber"
      data-view-state={viewState}
      data-reduced-motion={reduced ? 'true' : 'false'}
      data-source-id={adapter.sourceId}
      data-branch-id={adapter.branchId}
      data-read-only={adapter.readOnly ? 'true' : 'false'}
    >
      <div>
        <button type="button" onClick={() => setPlaying((p) => !p)} disabled={!verifiedValid || verified === end}>
          {playing ? 'Pause' : 'Play'}
        </button>
        <output data-testid="timeline-label" aria-live="polite">
          {label}
          {endLabel ? ` / ${endLabel}` : ''}
        </output>
      </div>
      <div
        role="slider"
        tabIndex={0}
        aria-label="Timeline position"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(percent)}
        aria-valuetext={label}
        aria-disabled={!boundsValid}
        data-position={shown}
        onKeyDown={onKeyDown}
        onPointerDown={(event) => {
          const target = fromPointer(event);
          if (target === null) return;
          dragging.current = true;
          event.currentTarget.setPointerCapture?.(event.pointerId);
          requestSeek(target, true);
        }}
        onPointerMove={(event) => {
          if (!dragging.current) return;
          const target = fromPointer(event);
          if (target !== null) requestSeek(target, true);
        }}
        onPointerUp={() => {
          dragging.current = false;
        }}
        onPointerCancel={() => {
          dragging.current = false;
        }}
        style={{ position: 'relative', height: 24, touchAction: 'none', background: 'rgba(128,128,128,0.3)' }}
      >
        <div style={{ position: 'absolute', left: 0, top: 0, bottom: 0, width: `${percent}%`, background: 'currentColor', opacity: 0.4 }} />
        {markers.map((marker) => (
          <button
            key={`${marker.position}:${marker.label}`}
            type="button"
            title={marker.label}
            aria-label={`Marker: ${marker.label}`}
            tabIndex={-1}
            onPointerDown={(event) => event.stopPropagation()}
            onClick={() => requestSeek(marker.position, false)}
            style={{ position: 'absolute', left: `${marker.pct}%`, top: 0, bottom: 0, width: 8, padding: 0 }}
          />
        ))}
      </div>
      <div>
        <button type="button" onClick={() => void doFork()} disabled={forkBlock !== null} aria-describedby={forkBlock ? forkReasonId : undefined}>
          Fork from here
        </button>
        {forkBlock ? (
          <span id={forkReasonId} data-testid="fork-reason" data-reason={forkBlock}>
            {FORK_REASON_TEXT[forkBlock]}
          </span>
        ) : null}
      </div>
      {adapter.readOnly ? <p data-testid="read-only-note">Read-only: this history cannot be changed.</p> : null}
      {failure && failure.kind !== 'stale' ? (
        <p role="alert" data-testid="timeline-error" data-kind={failure.kind}>
          {describeFailure(failure)}
        </p>
      ) : null}
    </section>
  );
}
