import type { UTCTimestamp } from 'lightweight-charts';

export function toUtcTimestamp(ms: number): UTCTimestamp {
  return (ms / 1000) as UTCTimestamp;
}

export function fromUtcTimestamp(s: UTCTimestamp): number {
  return (s * 1000) as number;
}
