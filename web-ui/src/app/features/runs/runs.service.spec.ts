import { setupZoneTestEnv } from 'jest-preset-angular/setup-env/zone';

setupZoneTestEnv();

import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { RunsApiService } from './runs.service';

describe('RunsApiService', () => {
  let service: RunsApiService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(RunsApiService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('should fetch runs', async () => {
    const mockRuns = [
      {
        runId: 'abc', status: 'completed', symbol: 'EURUSD', period: 'h1',
        startedAtUtc: '2024-01-01', completedAtUtc: null,
        netProfit: 100, maxDrawdownPct: 0.02, totalTrades: 5,
        winningTrades: 3, winRatePct: 0.6, errorMessage: null,
      },
    ];
    const promise = service.getRuns();
    const req = httpMock.expectOne('/api/runs');
    expect(req.request.method).toBe('GET');
    req.flush(mockRuns);
    const result = await promise;
    expect(result.length).toBe(1);
    expect(result[0].runId).toBe('abc');
  });

  it('should start a run', async () => {
    const mockResponse = { runId: 'xyz', status: 'started' };
    const promise = service.startRun({
      symbol: 'EURUSD', period: 'h1', start: '2024-01-01', end: '2024-01-31',
      balance: 100000, commissionPerMillion: 30, spreadPips: 1,
    });
    const req = httpMock.expectOne('/api/runs');
    expect(req.request.method).toBe('POST');
    expect(req.request.body.balance).toBe(100000);
    req.flush(mockResponse);
    const result = await promise;
    expect(result.runId).toBe('xyz');
  });

  it('should get run trades', async () => {
    const mockResponse = {
      totalCount: 1,
      trades: [{
        id: '1', positionId: 'p1', orderId: 'o1',
        symbol: 'EURUSD', direction: 'Long', lots: 0.1,
        entryPrice: 1.1, exitPrice: 1.11,
        openedAtUtc: '2024-01-01', closedAtUtc: '2024-01-02',
        grossPnLAmount: 100, commissionAmount: 7, swapAmount: -1,
        netPnLAmount: 92, pnLPips: 10, rMultiple: 1.5,
        maxAdverseExcursion: -5, maxFavorableExcursion: 15,
        exitReason: 'Take Profit', strategyId: 'test', durationSeconds: 3600,
      }],
    };
    const promise = service.getRunTrades('abc');
    const req = httpMock.expectOne('/api/runs/abc/trades');
    expect(req.request.method).toBe('GET');
    req.flush(mockResponse);
    const result = await promise;
    expect(result.length).toBe(1);
    expect(result[0].netPnLAmount).toBe(92);
  });
});
