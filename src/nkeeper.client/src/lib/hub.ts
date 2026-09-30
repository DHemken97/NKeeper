import { HubConnectionBuilder, LogLevel, type HubConnection } from '@microsoft/signalr';
import { session } from './api.ts';

/**
 * Creates a SignalR connection to a module hub. By convention hubs live at
 * `/api/{moduleId}/hubs/{name}` so the server accepts the token from the query string.
 */
export function createHubConnection(moduleId: string, hubName: string): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(`/api/${moduleId}/hubs/${hubName}`, {
      accessTokenFactory: () => session.get()?.accessToken ?? '',
    })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();
}
