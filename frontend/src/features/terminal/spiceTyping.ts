import { Constants } from '../../vendor/spice-html5/enums.js'
import type { SpiceMainConn } from '../../vendor/spice-html5/main.js'
import { SpiceMiniData, SpiceMsgcKeyDown, SpiceMsgcKeyUp } from '../../vendor/spice-html5/spicemsg.js'
import { characters, KEY_DELAY_MS, SCANCODE_SHIFT, sleep, spiceScancode, type TypeResult } from './typeText'

// Kept apart from typeText.ts: spice-html5's messages are only loaded with a SPICE console.

/**
 * Types text into a SPICE console, key by key, as a US keyboard would: SPICE sends physical keys,
 * so the guest's keyboard layout decides what appears. Stops early when `signal` is aborted.
 */
export async function typeIntoSpice(connection: SpiceMainConn, text: string, signal?: AbortSignal): Promise<TypeResult> {
  const result: TypeResult = { typed: 0, skipped: 0 }
  const send = (code: number, down: boolean) => {
    const inputs = connection.inputs
    if (inputs?.state !== 'ready') {
      throw new Error('The console isn’t ready for keys.')
    }
    const key = down ? new SpiceMsgcKeyDown() : new SpiceMsgcKeyUp()
    key.code = down ? code : 0x80 | code
    const message = new SpiceMiniData()
    message.build_msg(down ? Constants.SPICE_MSGC_INPUTS_KEY_DOWN : Constants.SPICE_MSGC_INPUTS_KEY_UP, key)
    inputs.send_msg(message)
  }
  for (const character of characters(text)) {
    if (signal?.aborted) {
      break
    }
    const key = spiceScancode(character)
    if (!key) {
      result.skipped++
      continue
    }
    if (key.shift) {
      send(SCANCODE_SHIFT, true)
    }
    send(key.code, true)
    send(key.code, false)
    if (key.shift) {
      send(SCANCODE_SHIFT, false)
    }
    result.typed++
    await sleep(KEY_DELAY_MS)
  }
  return result
}
