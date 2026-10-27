import type RFB from '@novnc/novnc'

/** The most text typed at once: typing is slow (a few hundred characters a second at most). */
export const MAX_TYPED_LENGTH = 10_000

/** A pause after each key, so the guest doesn't drop keys typed faster than a person could. */
export const KEY_DELAY_MS = 8

export interface TypeResult {
  typed: number
  /** Characters the console can't type (not on a US keyboard, for SPICE). */
  skipped: number
}

export const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

/** Line breaks become Enter (a Windows CRLF is one Enter). */
export function characters(text: string): string[] {
  return [...text.replace(/\r\n?/g, '\n')]
}

/** The X keysym of the left Shift key. */
export const KEYSYM_SHIFT = 0xffe1

/**
 * The X keysym for a character, as VNC sends keys: Latin-1 characters are their own code, Enter
 * and Tab have function keysyms, and other Unicode characters are 0x01000000 + code point.
 */
export function vncKeysym(character: string): number | null {
  const code = character.codePointAt(0)!
  if (character === '\n') {
    return 0xff0d
  }
  if (character === '\t') {
    return 0xff09
  }
  if (code < 0x20 || code === 0x7f) {
    return null
  }
  return code <= 0xff ? code : 0x01000000 + code
}

/**
 * Types text into a VNC console, key by key. The VNC server (QEMU) turns a keysym into its key
 * without pressing Shift, so Shift is held for characters that need it on a US keyboard (or an
 * uppercase 'A' would arrive as 'a'). Stops early when `signal` is aborted.
 */
export async function typeIntoVnc(rfb: RFB, text: string, signal?: AbortSignal): Promise<TypeResult> {
  const result: TypeResult = { typed: 0, skipped: 0 }
  for (const character of characters(text)) {
    if (signal?.aborted) {
      break
    }
    const keysym = vncKeysym(character)
    if (keysym === null) {
      result.skipped++
      continue
    }
    const shift = spiceScancode(character)?.shift ?? false
    if (shift) {
      rfb.sendKey(KEYSYM_SHIFT, null, true)
    }
    rfb.sendKey(keysym, null, true)
    rfb.sendKey(keysym, null, false)
    if (shift) {
      rfb.sendKey(KEYSYM_SHIFT, null, false)
    }
    result.typed++
    await sleep(KEY_DELAY_MS)
  }
  return result
}

// PC (set 1) scancodes of a US keyboard: [unshifted, shifted] characters for each key.
const US_KEYS: [number, string, string][] = [
  [0x29, '`', '~'], [0x02, '1', '!'], [0x03, '2', '@'], [0x04, '3', '#'], [0x05, '4', '$'], [0x06, '5', '%'],
  [0x07, '6', '^'], [0x08, '7', '&'], [0x09, '8', '*'], [0x0a, '9', '('], [0x0b, '0', ')'], [0x0c, '-', '_'],
  [0x0d, '=', '+'], [0x10, 'q', 'Q'], [0x11, 'w', 'W'], [0x12, 'e', 'E'], [0x13, 'r', 'R'], [0x14, 't', 'T'],
  [0x15, 'y', 'Y'], [0x16, 'u', 'U'], [0x17, 'i', 'I'], [0x18, 'o', 'O'], [0x19, 'p', 'P'], [0x1a, '[', '{'],
  [0x1b, ']', '}'], [0x2b, '\\', '|'], [0x1e, 'a', 'A'], [0x1f, 's', 'S'], [0x20, 'd', 'D'], [0x21, 'f', 'F'],
  [0x22, 'g', 'G'], [0x23, 'h', 'H'], [0x24, 'j', 'J'], [0x25, 'k', 'K'], [0x26, 'l', 'L'], [0x27, ';', ':'],
  [0x28, "'", '"'], [0x2c, 'z', 'Z'], [0x2d, 'x', 'X'], [0x2e, 'c', 'C'], [0x2f, 'v', 'V'], [0x30, 'b', 'B'],
  [0x31, 'n', 'N'], [0x32, 'm', 'M'], [0x33, ',', '<'], [0x34, '.', '>'], [0x35, '/', '?'],
]
export const SCANCODE_SHIFT = 0x2a
const SCANCODES = new Map<string, { code: number; shift: boolean }>([
  ...US_KEYS.flatMap(([code, plain, shifted]): [string, { code: number; shift: boolean }][] => [
    [plain, { code, shift: false }],
    [shifted, { code, shift: true }],
  ]),
  [' ', { code: 0x39, shift: false }],
  ['\n', { code: 0x1c, shift: false }],
  ['\t', { code: 0x0f, shift: false }],
])

/** The scancode (and Shift) that types a character on a US keyboard, as SPICE sends keys; null if there's none. */
export function spiceScancode(character: string): { code: number; shift: boolean } | null {
  return SCANCODES.get(character) ?? null
}
