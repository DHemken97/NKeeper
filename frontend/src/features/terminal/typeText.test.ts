import { describe, expect, it } from 'vitest'
import { spiceScancode, vncKeysym } from './typeText'

describe('typing text into consoles', () => {
  it('turns characters into VNC keysyms', () => {
    expect(['a', 'A', ' ', '~', '\n', '\t', 'é', '€', '\u0007'].map(vncKeysym)).toEqual([0x61, 0x41, 0x20, 0x7e, 0xff0d, 0xff09, 0xe9, 0x010020ac, null])
  })

  it('turns characters into the scancodes of a US keyboard for SPICE, with Shift where needed', () => {
    expect(spiceScancode('a')).toEqual({ code: 0x1e, shift: false })
    expect(spiceScancode('A')).toEqual({ code: 0x1e, shift: true })
    expect(spiceScancode('@')).toEqual({ code: 0x03, shift: true })
    expect(spiceScancode('|')).toEqual({ code: 0x2b, shift: true })
    expect(spiceScancode('\n')).toEqual({ code: 0x1c, shift: false })
    expect(spiceScancode('é')).toBeNull()
  })
})
