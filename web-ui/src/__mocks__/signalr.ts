// Mock for @microsoft/signalr
export class HubConnectionBuilder {
  withUrl() { return this; }
  withAutomaticReconnect() { return this; }
  build() {
    return {
      start: () => Promise.resolve(),
      stop: () => Promise.resolve(),
      on: () => {},
      off: () => {},
      invoke: () => Promise.resolve(),
      onclose: () => {},
      state: 'Connected',
    };
  }
}

export const HubConnectionState = { Connected: 'Connected', Disconnected: 'Disconnected' };
export const LogLevel = { None: 0, Information: 1 };
