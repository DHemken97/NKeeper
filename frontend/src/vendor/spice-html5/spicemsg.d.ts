// NKeeper's types for the parts of spice-html5's messages it uses (to type text into a console).

export interface SpiceMessageBody {
  buffer_size(): number
}

export class SpiceMiniData {
  build_msg(type: number, body: SpiceMessageBody): void
}

/** A key pressed: `code` is its PC (set 1) scancode. */
export class SpiceMsgcKeyDown implements SpiceMessageBody {
  code: number
  buffer_size(): number
}

/** A key released: `code` is its scancode with 0x80 set. */
export class SpiceMsgcKeyUp implements SpiceMessageBody {
  code: number
  buffer_size(): number
}
