// Newest-request-wins gate (R04.04 stale/cancelled, design "Seek cancellation"). Starting a request aborts the one before
// it, and only the newest request may deliver a result: a slow earlier answer that still arrives is reported as `stale`
// and must not be rendered. Generic over the call, so the same gate serves REST and hub invokes.
import type { ApiResult } from './failure.ts';

export class LatestRequestGate {
  private generation = 0;
  private cancelledUpTo = 0;
  private controller: AbortController | null = null;

  /** True while the newest started request has not finished. */
  get busy(): boolean {
    return this.controller !== null;
  }

  /**
   * Runs `operation` as the newest request. The previous request's signal is aborted. If another `run` or `cancel`
   * happens before this one settles, its outcome is `stale` (superseded) or `cancelled` (explicit cancel).
   */
  async run<T>(operation: (signal: AbortSignal) => Promise<ApiResult<T>>): Promise<ApiResult<T>> {
    this.controller?.abort();
    const mine = ++this.generation;
    const controller = new AbortController();
    this.controller = controller;
    let result: ApiResult<T>;
    try {
      result = await operation(controller.signal);
    } catch (error) {
      // An operation must return failures as values; an exception is a programming error and is surfaced as one.
      if (mine === this.generation) this.controller = null;
      throw error;
    }
    if (mine <= this.cancelledUpTo) return { ok: false, failure: { kind: 'cancelled', by: 'caller' } };
    if (mine !== this.generation) return { ok: false, failure: { kind: 'stale' } };
    this.controller = null;
    return result;
  }

  /** Aborts the in-flight request, if any. Its caller receives a `cancelled` failure. */
  cancel(): void {
    this.controller?.abort();
    this.controller = null;
    this.cancelledUpTo = this.generation;
  }
}
