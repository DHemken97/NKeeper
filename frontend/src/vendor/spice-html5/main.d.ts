// NKeeper's types for the parts of spice-html5 it uses (the library itself is plain JavaScript).

export interface SpiceConnectionOptions {
  /** ws(s):// URL every SPICE channel connects to (with the "binary" subprotocol). */
  uri: string
  /** id of the element the display canvases are added to. */
  screen_id: string
  /** The VM's SPICE password, if any. */
  password?: string
  message_id?: string
  onerror?: (error: Error | Event | string) => void
  onsuccess?: () => void
  onagent?: (connection: SpiceMainConn) => void
}

export class SpiceMainConn {
  constructor(options: SpiceConnectionOptions)
  /** Closes every channel. */
  stop(message?: string): void
  /** Asks the guest agent (spice-vdagent) to resize the display. */
  resize_window(flags: number, width: number, height: number, depth: number, x: number, y: number): void
  state: string
}

export function sendCtrlAltDel(connection: SpiceMainConn): void
