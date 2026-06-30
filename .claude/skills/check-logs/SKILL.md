# check-logs — AI Agent Error Diagnosis Skill

## When to use
Load this skill when:
- The user reports unexpected behaviour and you need to check for runtime errors
- After making code changes that could cause server or frontend crashes
- Before claiming "all tests pass" — runtime errors may not be caught by unit tests
- When asked to "check the logs" or "diagnose recent errors"

## How to use

### Quick check (last 15 minutes)
```powershell
.\scripts\check-errors.ps1
```

### Check since a specific timeframe
```powershell
.\scripts\check-errors.ps1 -Minutes 60
```

### Check only new errors since the last check
```powershell
.\scripts\check-errors.ps1 -SinceLastCheck
```

## Log file locations

All paths are relative to the repo root (`C:\Code\Shamshir\`).

| Source | Absolute path | Format |
|--------|------|--------|
| Backend (Web) | `C:\Code\Shamshir\src\TradingEngine.Web\logs\web-*.log` | Serilog, daily rolling, text |
| Backend (Host) | `C:\Code\Shamshir\src\TradingEngine.Host\logs\engine-*.log` | Serilog, daily rolling, text |
| Frontend errors | `C:\Code\Shamshir\logs\frontend-errors.jsonl` | JSON-lines, append-only |

The frontend JSONL file is written at the **repo root** via `AppContext.BaseDirectory/../../../../../logs/` (5 levels up from `bin/Debug/net10.0/`).

## The error pipeline

1. **Backend**: Serilog is bootstrapped in `Program.cs` via `builder.Host.UseSerilog()`. All `ILogger<T>` calls in controllers, orchestrators, and middleware route through Serilog → Console + File sink. Unhandled exceptions are caught by the middleware in `MiddlewarePipeline.cs` and logged as `[ERR]`.

2. **Frontend**: `AppErrorHandler` (global Angular `ErrorHandler`) catches:
   - Angular template/directive/DI errors
   - `window.onerror` (uncaught JS exceptions)
   - `window.onunhandledrejection` (Promise rejections)
   - `console.error` calls (intercepted)
   
   Batched every 5s or 20 errors → `POST /api/log/frontend` → Serilog + JSON-lines file.

## Interpreting results

- Backend `[ERR]` / `[FTL]` lines: server-side crashes, unhandled exceptions, critical failures.
- Backend `[WRN]` lines: warnings that may indicate degraded functionality.
- Frontend errors: look for `[angular]` (framework), `[unhandled]` (JS exceptions), `[promise]` (rejected promises), `[console]` (explicit `console.error` calls).

## Common patterns and their fixes

| Pattern | Likely cause | Fix |
|---------|-------------|-----|
| `[ERR] SignalR {Method} failed` | SignalR group has no members or hub context failed | Check server restart, WebSocket middleware |
| `NG0203` in frontend errors | Angular DI missing provider | Check `providedIn: 'root'` or `app.config.ts` providers |
| `ERR_CONNECTION_REFUSED` to SignalR negotiate | Server not running or proxy misconfigured | Restart server, check `proxy.conf.json` |
| `[FTL] Web host terminated unexpectedly` | Server crash during startup | Check Serilog output, fix startup code |
