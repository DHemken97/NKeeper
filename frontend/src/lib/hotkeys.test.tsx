import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it, vi } from 'vitest'
import { useHotkeys, type Hotkey } from './hotkeys'

function Harness({ hotkeys, onTerminalKey }: { hotkeys: Hotkey[]; onTerminalKey: () => void }) {
  useHotkeys(hotkeys)
  return (
    <>
      <input aria-label="Search" />
      <button type="button">Plain</button>
      <div role="tabpanel" aria-label="Session">
        {/* Like xterm.js: handles keys on its own element and stops them there. */}
        <textarea
          aria-label="Terminal"
          onKeyDown={(event) => {
            onTerminalKey()
            event.stopPropagation()
          }}
        />
      </div>
    </>
  )
}

describe('useHotkeys', () => {
  it('runs Ctrl combinations anywhere outside sessions, ⌘ included, and keeps the browser from them', () => {
    const edit = vi.fn()
    render(<Harness hotkeys={[{ keys: 'Ctrl+E', run: edit }]} onTerminalKey={() => {}} />)

    const event = new KeyboardEvent('keydown', { key: 'e', code: 'KeyE', ctrlKey: true, bubbles: true, cancelable: true })
    screen.getByRole('button', { name: 'Plain' }).dispatchEvent(event)
    fireEvent.keyDown(screen.getByRole('textbox', { name: 'Search' }), { key: 'e', code: 'KeyE', metaKey: true })

    expect(edit).toHaveBeenCalledTimes(2)
    expect(event.defaultPrevented).toBe(true)
  })

  it('leaves plain keys to text fields', () => {
    const help = vi.fn()
    render(<Harness hotkeys={[{ keys: '?', run: help }]} onTerminalKey={() => {}} />)

    fireEvent.keyDown(screen.getByRole('textbox', { name: 'Search' }), { key: '?', shiftKey: true })
    expect(help).not.toHaveBeenCalled()
    fireEvent.keyDown(screen.getByRole('button', { name: 'Plain' }), { key: '?', shiftKey: true })
    expect(help).toHaveBeenCalledOnce()
  })

  it('leaves a session its keys, except the few marked to work there, which it never sees', () => {
    const edit = vi.fn()
    const nextTab = vi.fn()
    const terminalKey = vi.fn()
    render(
      <Harness
        hotkeys={[
          { keys: 'Ctrl+E', run: edit },
          { keys: 'Alt+PageDown', run: nextTab, inSessions: true },
        ]}
        onTerminalKey={terminalKey}
      />,
    )
    const terminal = screen.getByRole('textbox', { name: 'Terminal' })

    fireEvent.keyDown(terminal, { key: 'e', code: 'KeyE', ctrlKey: true })
    expect(edit).not.toHaveBeenCalled()
    expect(terminalKey).toHaveBeenCalledOnce()

    fireEvent.keyDown(terminal, { key: 'PageDown', altKey: true })
    expect(nextTab).toHaveBeenCalledOnce()
    expect(terminalKey).toHaveBeenCalledOnce()
  })

  it('matches modifiers exactly, and skips disabled shortcuts', () => {
    const newHost = vi.fn()
    const off = vi.fn()
    render(
      <Harness
        hotkeys={[
          { keys: 'Alt+Shift+N', run: newHost },
          { keys: 'Ctrl+D', run: off, enabled: false },
        ]}
        onTerminalKey={() => {}}
      />,
    )
    const plain = screen.getByRole('button', { name: 'Plain' })

    fireEvent.keyDown(plain, { key: 'n', code: 'KeyN', altKey: true })
    fireEvent.keyDown(plain, { key: 'D', code: 'KeyD', ctrlKey: true })
    expect(newHost).not.toHaveBeenCalled()
    // On a Mac, Option+Shift+N types another character: the physical key still matches.
    fireEvent.keyDown(plain, { key: '˜', code: 'KeyN', altKey: true, shiftKey: true })
    expect(newHost).toHaveBeenCalledOnce()
    expect(off).not.toHaveBeenCalled()
  })
})
