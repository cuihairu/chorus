// Reconnect delay for the attach transport: exponential, capped, reset on a
// successful attach. No jitter — the devtools plane has exactly one attached
// IDE per socket by design, so there is no thundering herd to spread.

export interface BackoffOptions {
  initialMs?: number;
  maxMs?: number;
  multiplier?: number;
}

export class ReconnectBackoff {
  private readonly initialMs: number;
  private readonly maxMs: number;
  private readonly multiplier: number;
  private currentMs: number;

  constructor(options?: BackoffOptions) {
    this.initialMs = options?.initialMs ?? 500;
    this.maxMs = options?.maxMs ?? 8000;
    this.multiplier = options?.multiplier ?? 2;
    if (!(this.initialMs > 0) || !(this.multiplier >= 1)) {
      throw new Error("backoff needs a positive initial delay and a multiplier >= 1");
    }
    this.currentMs = this.initialMs;
  }

  /** The next delay, and advances the sequence. */
  next(): number {
    const delay = this.currentMs;
    this.currentMs = Math.min(this.maxMs, Math.round(this.currentMs * this.multiplier));
    return delay;
  }

  /** A successful attach rewinds the sequence. */
  reset(): void {
    this.currentMs = this.initialMs;
  }
}
