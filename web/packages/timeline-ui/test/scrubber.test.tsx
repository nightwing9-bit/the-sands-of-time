// Validates: spec-04 R04.02 (newest verified seek wins), exact decimal positions above 2^53, fork gating, keyboard.
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { TimelineScrubber, type SeekOptions, type TimelineAdapter, type VerifiedPreview } from '../src/index.ts';

afterEach(cleanup);

interface Deferred {
  position: string;
  options: SeekOptions | undefined;
  resolve: () => void;
  reject: (error: unknown) => void;
}

function makeAdapter(overrides: Partial<TimelineAdapter<string>> = {}) {
  const calls: Deferred[] = [];
  const fork = vi.fn(async (_p: string, _o: unknown, _k: string) => ({ branchId: 'b1' }));
  const adapter: TimelineAdapter<string> = {
    sourceId: 'src',
    bounds: { start: '0', end: '9007199254740999' },
    readOnly: false,
    getLabel: (p) => `T:${p}`,
    getMarkers: () => [
      { position: '100', label: 'early' },
      { position: '200', label: 'late' },
    ],
    seek: (position, options) =>
      new Promise<VerifiedPreview<string>>((resolve, reject) => {
        calls.push({ position, options, resolve: () => resolve({ position, state: `state-${position}` }), reject });
      }),
    fork,
    ...overrides,
  };
  return { adapter, calls, fork };
}

function Harness(props: { adapter: TimelineAdapter<string>; initial?: string; painted: string[]; onForked?: () => void }) {
  const [position, setPosition] = useState(props.initial ?? '0');
  return (
    <TimelineScrubber
      adapter={props.adapter}
      position={position}
      createIdempotencyKey={() => 'key-1'}
      onForked={props.onForked}
      onVerified={(preview) => {
        props.painted.push(preview.state);
        setPosition(preview.position);
      }}
    />
  );
}

const slider = () => screen.getByRole('slider');
const label = () => screen.getByTestId('timeline-label').textContent;

describe('seek race', () => {
  it('a slow earlier response never overwrites a newer one', async () => {
    const { adapter, calls } = makeAdapter();
    const painted: string[] = [];
    render(<Harness adapter={adapter} painted={painted} />);

    fireEvent.click(screen.getByLabelText('Marker: early'));
    fireEvent.click(screen.getByLabelText('Marker: late'));
    expect(calls.map((c) => c.position)).toEqual(['100', '200']);
    expect(calls[0]!.options!.signal.aborted).toBe(true);
    expect(calls[1]!.options!.operationId).toBeGreaterThan(calls[0]!.options!.operationId);

    await act(async () => calls[1]!.resolve());
    await act(async () => calls[0]!.resolve()); // slow, late, obsolete

    expect(painted).toEqual(['state-200']);
    expect(slider().getAttribute('data-position')).toBe('200');
    expect(label()).toContain('T:200');
  });

  it('a failed older request does not show an error over a newer success', async () => {
    const { adapter, calls } = makeAdapter();
    const painted: string[] = [];
    render(<Harness adapter={adapter} painted={painted} />);
    fireEvent.click(screen.getByLabelText('Marker: early'));
    fireEvent.click(screen.getByLabelText('Marker: late'));
    await act(async () => calls[1]!.resolve());
    await act(async () => calls[0]!.reject(new Error('boom')));
    expect(screen.queryByTestId('timeline-error')).toBeNull();
    expect(painted).toEqual(['state-200']);
  });

  it('shows a typed error and paints nothing when the newest seek fails', async () => {
    const { adapter, calls } = makeAdapter();
    const painted: string[] = [];
    render(<Harness adapter={adapter} painted={painted} />);
    fireEvent.click(screen.getByLabelText('Marker: early'));
    await act(async () => calls[0]!.reject({ kind: 'denied', reason: 'forbidden', status: 403 }));
    expect(screen.getByRole('alert').textContent).toContain('permission');
    expect(painted).toEqual([]);
    expect(slider().getAttribute('data-position')).toBe('0');
  });

  it('rejects a preview that verified a different position', async () => {
    const { adapter } = makeAdapter({ seek: async () => ({ position: '7', state: 'x' }) });
    const painted: string[] = [];
    render(<Harness adapter={adapter} painted={painted} />);
    await act(async () => {
      fireEvent.click(screen.getByLabelText('Marker: early'));
    });
    expect(painted).toEqual([]);
    expect(screen.getByTestId('timeline-error').getAttribute('data-kind')).toBe('malformed');
  });
});

describe('positions above 2^53', () => {
  it('steps and labels exactly by keyboard and ends on the last requested position', async () => {
    const { adapter, calls } = makeAdapter();
    const painted: string[] = [];
    render(<Harness adapter={adapter} initial="9007199254740993" painted={painted} />);
    expect(label()).toContain('T:9007199254740993');

    fireEvent.keyDown(slider(), { key: 'ArrowRight' });
    fireEvent.keyDown(slider(), { key: 'ArrowRight' });
    // Number('9007199254740993') === 9007199254740992, so a double would repeat or skip a value here.
    expect(calls.map((c) => c.position)).toEqual(['9007199254740994', '9007199254740995']);
    await act(async () => calls[1]!.resolve());
    await act(async () => calls[0]!.resolve());
    expect(label()).toContain('T:9007199254740995');
    expect(painted).toEqual(['state-9007199254740995']);
  });

  it('Home and End jump to exact bounds', async () => {
    const { adapter, calls } = makeAdapter();
    render(<Harness adapter={adapter} initial="5" painted={[]} />);
    fireEvent.keyDown(slider(), { key: 'End' });
    expect(calls.at(-1)!.position).toBe('9007199254740999');
    await act(async () => calls.at(-1)!.resolve());
    expect(label()).toContain('T:9007199254740999 / T:9007199254740999');
  });
});

describe('fork gating', () => {
  const reason = () => screen.queryByTestId('fork-reason')?.getAttribute('data-reason');
  const forkButton = () => screen.getByRole('button', { name: 'Fork from here' }) as HTMLButtonElement;

  it('is disabled at the end tick with a reason', () => {
    const { adapter } = makeAdapter();
    render(<Harness adapter={adapter} initial="9007199254740999" painted={[]} />);
    expect(forkButton().disabled).toBe(true);
    expect(reason()).toBe('end_of_timeline');
  });

  it('is disabled for an invalid position and for read-only histories', () => {
    const invalid = makeAdapter();
    const { unmount } = render(<Harness adapter={invalid.adapter} initial="01" painted={[]} />);
    expect(reason()).toBe('invalid_position');
    unmount();
    const ro = makeAdapter({ readOnly: true });
    render(<Harness adapter={ro.adapter} initial="5" painted={[]} />);
    expect(forkButton().disabled).toBe(true);
    expect(reason()).toBe('read_only');
  });

  it('is disabled while a seek is pending, then forks with a stable idempotency key', async () => {
    const { adapter, calls, fork } = makeAdapter();
    const onForked = vi.fn();
    render(<Harness adapter={adapter} initial="5" painted={[]} onForked={onForked} />);
    expect(forkButton().disabled).toBe(false);
    fireEvent.click(screen.getByLabelText('Marker: early'));
    expect(reason()).toBe('pending');
    await act(async () => calls[0]!.resolve());
    expect(forkButton().disabled).toBe(false);
    await act(async () => {
      fireEvent.click(forkButton());
    });
    expect(fork).toHaveBeenCalledWith('100', undefined, 'key-1');
    expect(onForked).toHaveBeenCalledOnce();
  });
});

describe('accessibility', () => {
  it('exposes slider semantics with the adapter label as value text and honours reduced motion', () => {
    const { adapter } = makeAdapter();
    const { container } = render(
      <TimelineScrubber adapter={adapter} position="5" onVerified={() => {}} reducedMotion />,
    );
    expect(slider().getAttribute('aria-valuetext')).toBe('T:5');
    expect(slider().tabIndex).toBe(0);
    expect(container.querySelector('[data-reduced-motion="true"]')).not.toBeNull();
  });
});

describe('playback and reduced motion (observable timing, not just the attribute)', () => {
  afterEach(() => vi.useRealTimers());

  function instant() {
    const seeks: string[] = [];
    const adapter: TimelineAdapter<string> = {
      ...makeAdapter().adapter,
      bounds: { start: '0', end: '50' },
      seek: async (position) => {
        seeks.push(position);
        return { position, state: `state-${position}` };
      },
    };
    return { adapter, seeks };
  }

  async function advance(ms: number) {
    await act(async () => {
      await vi.advanceTimersByTimeAsync(ms);
    });
  }

  it('plays one step per interval and never issues overlapping seeks', async () => {
    vi.useFakeTimers();
    const { adapter, seeks } = instant();
    render(<TimelineScrubber adapter={adapter} position="5" onVerified={() => {}} playIntervalMs={500} reducedMotion={false} />);
    // Controlled component: the parent has not moved the verified position, so play waits at one request per interval.
    fireEvent.click(screen.getByRole('button', { name: 'Play' }));
    await advance(499);
    expect(seeks).toEqual([]);
    await advance(1);
    expect(seeks).toEqual(['6']);
  });

  it('reduced motion stretches the play interval to at least one second', async () => {
    vi.useFakeTimers();
    const reduced = instant();
    render(<TimelineScrubber adapter={reduced.adapter} position="5" onVerified={() => {}} playIntervalMs={500} reducedMotion />);
    fireEvent.click(screen.getByRole('button', { name: 'Play' }));
    await advance(999);
    expect(reduced.seeks).toEqual([]);
    await advance(1);
    expect(reduced.seeks).toEqual(['6']);
  });

  it('follows the prefers-reduced-motion media query when no override is given', async () => {
    vi.useFakeTimers();
    const original = window.matchMedia;
    window.matchMedia = ((query: string) => ({
      matches: query.includes('prefers-reduced-motion'),
      media: query,
      addEventListener: () => {},
      removeEventListener: () => {},
    })) as unknown as typeof window.matchMedia;
    try {
      const { adapter, seeks } = instant();
      const { container } = render(<TimelineScrubber adapter={adapter} position="5" onVerified={() => {}} playIntervalMs={200} />);
      expect(container.querySelector('[data-reduced-motion="true"]')).not.toBeNull();
      fireEvent.click(screen.getByRole('button', { name: 'Play' }));
      await advance(900);
      expect(seeks).toEqual([]);
      await advance(100);
      expect(seeks).toEqual(['6']);
    } finally {
      window.matchMedia = original;
    }
  });

  it('pausing cancels the pending step, and a user seek stops playback', async () => {
    vi.useFakeTimers();
    const { adapter, seeks } = instant();
    render(<TimelineScrubber adapter={adapter} position="5" onVerified={() => {}} playIntervalMs={500} reducedMotion={false} />);
    fireEvent.click(screen.getByRole('button', { name: 'Play' }));
    await advance(300);
    fireEvent.click(screen.getByRole('button', { name: 'Pause' }));
    await advance(2000);
    expect(seeks).toEqual([]);
    fireEvent.click(screen.getByRole('button', { name: 'Play' }));
    fireEvent.keyDown(slider(), { key: 'End' });
    await advance(0);
    expect(screen.getByRole('button', { name: 'Play' })).toBeTruthy(); // playback stopped by the user seek
    expect(seeks).toEqual(['50']);
  });
});

describe('seek debounce and pointer input', () => {
  afterEach(() => vi.useRealTimers());

  function recording(end = '1000') {
    const seeks: string[] = [];
    const adapter: TimelineAdapter<string> = {
      ...makeAdapter().adapter,
      bounds: { start: '0', end },
      getMarkers: () => [],
      seek: async (position) => {
        seeks.push(position);
        return { position, state: `state-${position}` };
      },
    };
    return { adapter, seeks };
  }

  it('a burst of keyboard steps sends one request for the final position after the debounce', async () => {
    vi.useFakeTimers();
    const { adapter, seeks } = recording();
    render(<TimelineScrubber adapter={adapter} position="0" onVerified={() => {}} seekDebounceMs={120} />);
    for (let i = 0; i < 3; i++) fireEvent.keyDown(slider(), { key: 'ArrowRight' });
    expect(seeks).toEqual([]);
    expect(screen.getByTestId('timeline-scrubber').getAttribute('aria-busy')).toBe('true');
    await act(async () => {
      await vi.advanceTimersByTimeAsync(119);
    });
    expect(seeks).toEqual([]);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1);
    });
    expect(seeks).toEqual(['3']);
  });

  it('pointer down and drag map the x offset to an exact position and stop after pointer up', async () => {
    const { adapter, seeks } = recording();
    render(<TimelineScrubber adapter={adapter} position="0" onVerified={() => {}} />);
    const track = slider();
    track.getBoundingClientRect = () => ({ left: 0, top: 0, right: 200, bottom: 24, width: 200, height: 24, x: 0, y: 0, toJSON: () => ({}) });
    await act(async () => {
      fireEvent.pointerDown(track, { clientX: 100, pointerId: 1 });
    });
    await act(async () => {
      fireEvent.pointerMove(track, { clientX: 150, pointerId: 1 });
    });
    await act(async () => {
      fireEvent.pointerUp(track, { clientX: 150, pointerId: 1 });
      fireEvent.pointerMove(track, { clientX: 20, pointerId: 1 });
    });
    expect(seeks).toEqual(['500', '750']);
  });
});
