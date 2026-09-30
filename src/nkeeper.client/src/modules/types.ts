import type { ComponentType, LazyExoticComponent } from 'react';
import type { IconComponent } from '../components/icons.tsx';

/**
 * Opens a connection profile (SSH terminal, RDP session, ...). Protocol modules
 * contribute these; the inventory offers a Connect button for every profile whose
 * protocol has a handler.
 */
export interface ConnectionHandler {
  /** Matches ConnectionProtocol on the server: 'Ssh', 'Rdp', 'Vnc', ... */
  protocol: string;
  label: string;
  /** Route to navigate to for the given connection profile id. */
  route(profileId: string): string;
}

/**
 * A client-side feature module. To add one:
 *   1. create `src/modules/<id>/index.ts` exporting a ClientModule,
 *   2. add it to `registry.ts`.
 * If it has a backend counterpart (an INKeeperModule with the same id), set `serverModule`
 * so it's hidden when the server has that module disabled.
 */
export interface ClientModule {
  id: string;
  title: string;
  /** Base route. '/' marks the home module. Sub-routes are the module's own business. */
  path: string;
  icon: IconComponent;
  /** Lazily loaded so each module ships as its own chunk. */
  Component: LazyExoticComponent<ComponentType>;
  /** Sidebar placement; lower `order` sorts first. */
  nav: { section: 'main' | 'footer'; order: number } | false;
  serverModule?: boolean;
  adminOnly?: boolean;
  connectionHandlers?: ConnectionHandler[];
}
