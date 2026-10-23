// NKeeper's types for the parts of noVNC (@novnc/novnc) it uses; the package ships plain JavaScript.

declare module '@novnc/novnc' {
  export interface RfbCredentials {
    username?: string
    password?: string
    target?: string
  }

  export interface RfbOptions {
    shared?: boolean
    credentials?: RfbCredentials
    repeaterID?: string
    wsProtocols?: string[]
  }

  export interface RfbEventMap {
    connect: CustomEvent<Record<string, never>>
    /** clean is false when the connection was lost or refused. */
    disconnect: CustomEvent<{ clean: boolean }>
    credentialsrequired: CustomEvent<{ types: (keyof RfbCredentials)[] }>
    securityfailure: CustomEvent<{ status: number; reason?: string }>
    desktopname: CustomEvent<{ name: string }>
  }

  /** A VNC (RFB) connection drawn into `target`. */
  export default class RFB extends EventTarget {
    constructor(target: HTMLElement, urlOrChannel: string | WebSocket, options?: RfbOptions)
    /** Scale the remote screen to fit the target (keeping its aspect ratio). */
    scaleViewport: boolean
    /** Ask the server to resize the remote screen to the target. */
    resizeSession: boolean
    focusOnClick: boolean
    viewOnly: boolean
    background: string
    disconnect(): void
    sendCredentials(credentials: RfbCredentials): void
    sendCtrlAltDel(): void
    /** Presses (or releases, with `down` false) the key with this X keysym; without `down`, both. */
    sendKey(keysym: number, code: string | null, down?: boolean): void
    focus(options?: FocusOptions): void
    addEventListener<K extends keyof RfbEventMap>(type: K, listener: (event: RfbEventMap[K]) => void): void
    removeEventListener<K extends keyof RfbEventMap>(type: K, listener: (event: RfbEventMap[K]) => void): void
  }
}
