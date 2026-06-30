import { Component, OnInit, OnDestroy, ChangeDetectionStrategy, signal, effect, inject } from '@angular/core';
import { DatePipe } from '@angular/common';
import { interval, Subscription } from 'rxjs';
import { RunHubService, type ConnectionState } from '../signalr/run-hub.service';

@Component({
  selector: 'app-status-bar',
  standalone: true,
  imports: [DatePipe],
  template: `
    <div class="flex items-center gap-4 text-xs">
      <!-- Server ping -->
      <span class="flex items-center gap-1" [title]="serverMsg()">
        <span
          class="inline-block h-2 w-2 rounded-full"
          [class.bg-emerald-500]="serverUp()"
          [class.bg-red-500]="!serverUp()"
        ></span>
        <span class="text-gray-500">{{ serverUp() ? 'Server' : 'Server ✗' }}</span>
      </span>

      <!-- SignalR state -->
      <span class="flex items-center gap-1" [title]="signalrError() || ''">
        <span
          class="inline-block h-2 w-2 rounded-full"
          [class.bg-emerald-500]="signalrConnected()"
          [class.bg-yellow-500]="signalrState() === 'connecting' || signalrState() === 'reconnecting'"
          [class.bg-red-500]="signalrState() === 'disconnected'"
        ></span>
        <span class="text-gray-500">
          {{ signalrLabel() }}
          @if (signalrError()) {
            <span class="ml-1 text-red-400">⚠ {{ signalrError() }}</span>
          }
        </span>
      </span>

      @if (serverUp() && !signalrConnected()) {
        <span class="text-yellow-400/70">
          Restart server after build (Ctrl+C → dotnet run)
        </span>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppStatusComponent implements OnInit, OnDestroy {
  private hub = inject(RunHubService);
  private pingSub?: Subscription;

  serverUp = signal(false);
  serverMsg = signal('');
  signalrState = signal<ConnectionState>('disconnected');
  signalrError = signal<string | null>(null);
  signalrConnected = signal(false);
  signalrLabel = signal('Offline');

  constructor() {
    effect(() => {
      const diag = this.hub.state();
      this.signalrState.set(diag.state);
      this.signalrError.set(diag.error);
      this.signalrConnected.set(diag.state === 'connected');
      this.signalrLabel.set(
        diag.state === 'connected' ? 'Live' :
        diag.state === 'connecting' ? 'Connecting...' :
        diag.state === 'reconnecting' ? 'Reconnecting...' :
        'Offline',
      );
    });
  }

  async ngOnInit(): Promise<void> {
    this.pingSub = interval(5000).subscribe(async () => {
      try {
        const resp = await fetch('/health');
        if (resp.ok) {
          this.serverUp.set(true);
          this.serverMsg.set('Server reachable');
        } else {
          this.serverUp.set(false);
          this.serverMsg.set(`Server returned ${resp.status}`);
        }
      } catch {
        this.serverUp.set(false);
        this.serverMsg.set('Server unreachable. Is the app running?');
      }
    });

    // Establish the SignalR connection early so the status bar shows state immediately.
    // If the server hasn't restarted with the UseWebSockets fix, the error surfaces here.
    try {
      await this.hub.start();
    } catch {
      // Diagnostic already captured in hub.state() via start()'s catch block.
    }
  }

  ngOnDestroy(): void {
    this.pingSub?.unsubscribe();
  }
}
