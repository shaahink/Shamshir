import { ErrorHandler, Injectable } from '@angular/core';

interface FrontendErrorReport {
  kind: string;
  message?: string;
  stack?: string;
  url?: string;
  line?: number;
  col?: number;
  timestamp: string;
}

const MAX_BATCH = 20;
const FLUSH_INTERVAL_MS = 5000;

/**
 * Global Angular error handler + console-error interceptor.
 *
 * Captures:
 *   - Unhandled Angular errors (via ErrorHandler)
 *   - window.onerror (uncaught JS exceptions)
 *   - unhandled Promise rejections
 *   - console.error calls (intercepted)
 *
 * Batches reports and POSTs them to POST /api/log/frontend, which writes them
 * to the Serilog pipeline + a JSON-lines file at logs/frontend-errors.jsonl.
 * One log file for full-stack troubleshooting; scripts/check-errors.ps1 reads it.
 */
@Injectable({ providedIn: 'root' })
export class AppErrorHandler implements ErrorHandler {
  private buffer: FrontendErrorReport[] = [];
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    this.interceptConsoleError();
    this.interceptWindowErrors();
  }

  // Angular's ErrorHandler — catches template/directive errors and unhandled DI errors.
  handleError(error: Error): void {
    this.report('angular', error.message, error.stack, '', 0, 0);
    console.error('[Angular]', error);
  }

  // -- dispatch helpers -------------------------------------------------

  private report(
    kind: string, message?: string, stack?: string, url?: string, line?: number, col?: number,
  ): void {
    this.buffer.push({
      kind,
      message: (message ?? '').substring(0, 500),
      stack: (stack ?? '').substring(0, 2000),
      url: (url ?? location.href).substring(0, 300),
      line,
      col,
      timestamp: new Date().toISOString(),
    });
    this.scheduleFlush();
  }

  private scheduleFlush(): void {
    if (this.timer) return;
    if (this.buffer.length >= MAX_BATCH) return void this.flush();
    this.timer = setTimeout(() => this.flush(), FLUSH_INTERVAL_MS);
  }

  private async flush(): Promise<void> {
    this.timer = null;
    if (this.buffer.length === 0) return;

    const batch = this.buffer.splice(0, MAX_BATCH);
    try {
      // Fire-and-forget: don't block the error path on network.
      for (const r of batch) {
        fetch('/api/log/frontend', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(r),
        }).catch(() => { /* backend may be down — we already logged to console */ });
      }
    } catch { /* ignore network errors in error handler */ }
  }

  // -- interceptors -----------------------------------------------------

  private interceptConsoleError(): void {
    const orig = console.error;
    console.error = (...args: unknown[]) => {
      // Apply the original first so DevTools still shows the message.
      orig.apply(console, args);
      try {
        const msg = args.map(a => (a instanceof Error ? a.message : String(a))).join(' ');
        this.report('console', msg);
      } catch { /* never throw from the interceptor */ }
    };
  }

  private interceptWindowErrors(): void {
    window.addEventListener('error', (e: ErrorEvent) => {
      if (e.error instanceof Error) {
        this.report('unhandled', e.error.message, e.error.stack, e.filename, e.lineno, e.colno);
      } else {
        this.report('unhandled', e.message, undefined, e.filename, e.lineno, e.colno);
      }
    });
    window.addEventListener('unhandledrejection', (e: PromiseRejectionEvent) => {
      this.report('promise', e.reason?.message ?? String(e.reason), e.reason?.stack);
    });
  }
}
