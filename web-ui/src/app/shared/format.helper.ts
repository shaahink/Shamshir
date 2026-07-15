export function parseJson<T = unknown>(x: string | null | undefined): T | null {
  if (!x) return null;
  try { return JSON.parse(x) as T; } catch { return null; }
}

export function formatDuration(seconds: number): string {
  const s = Math.floor(Math.abs(seconds));
  if (s < 60) return s + 's';
  if (s < 3600) return Math.floor(s / 60) + 'm';
  return Math.floor(s / 3600) + 'h ' + Math.floor((s % 3600) / 60) + 'm';
}
