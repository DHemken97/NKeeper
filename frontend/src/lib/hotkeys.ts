import { useEffect, useRef } from 'react'

export interface Hotkey {
  /** One or more combinations, e.g. `'Ctrl+E'`, `'Alt+Shift+N'`, `'Alt+PageUp'`, `'?'`. Ctrl also matches ⌘. */
  keys: string | string[]
  run: () => void
  /** Off when false, e.g. while nothing is selected. */
  enabled?: boolean
  /**
   * Also inside a remote session (terminal, desktop, console), whose keys are otherwise its own.
   * Kept to a few combinations no shell or desktop needs, so there's always a way out of one.
   */
  inSessions?: boolean
}

/** Whether keys typed here are text: a text field, a select or an editable element. */
export function isTyping(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) {
    return false
  }
  if (target.isContentEditable || target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement) {
    return true
  }
  return target instanceof HTMLInputElement && !['button', 'checkbox', 'radio', 'submit', 'reset', 'file', 'range', 'color'].includes(target.type)
}

/** Whether the target is inside a session tab, where keys belong to the remote side. */
function inSession(target: EventTarget | null): boolean {
  return target instanceof Element && target.closest('[role="tabpanel"]') !== null
}

/** A modal dialog or a menu is open: it owns the keyboard. */
function overlayOpen(): boolean {
  return document.querySelector('[aria-modal="true"], [data-context-menu]') !== null
}

/** Whether a key event is the combination, e.g. `'Ctrl+Shift+K'`. */
export function matchesKeys(event: KeyboardEvent, combination: string): boolean {
  const parts = combination.split('+')
  // "Ctrl++" would be the plus key; none of ours use it.
  const key = parts[parts.length - 1]
  const modifiers = new Set(parts.slice(0, -1))
  if ((event.ctrlKey || event.metaKey) !== modifiers.has('Ctrl') || event.altKey !== modifiers.has('Alt')) {
    return false
  }
  if (/^[A-Za-z0-9]$/.test(key)) {
    // By physical key, so Alt combinations work on a Mac (where Option types other characters) and other layouts.
    const code = /\d/.test(key) ? `Digit${key}` : `Key${key.toUpperCase()}`
    return event.shiftKey === modifiers.has('Shift') && (event.code === code || event.key.toLowerCase() === key.toLowerCase())
  }
  if (key.length === 1) {
    // A symbol such as ? or /: whichever Shift state the layout needs to type it.
    return event.key === key
  }
  return event.shiftKey === modifiers.has('Shift') && event.key === key
}

/**
 * Keyboard shortcuts for as long as the component is mounted. They stay out of the way of typing
 * (keys without Ctrl or Alt), of open dialogs and menus, of anything that already handled the key,
 * and of remote sessions unless marked `inSessions`.
 */
export function useHotkeys(hotkeys: Hotkey[]) {
  const hotkeysRef = useRef(hotkeys)
  useEffect(() => {
    hotkeysRef.current = hotkeys
  })

  useEffect(() => {
    function handle(event: KeyboardEvent, sessionPhase: boolean) {
      if (event.defaultPrevented || event.isComposing || overlayOpen()) {
        return
      }
      const plain = !event.ctrlKey && !event.metaKey && !event.altKey
      for (const hotkey of hotkeysRef.current) {
        if (hotkey.enabled === false || Boolean(hotkey.inSessions) !== sessionPhase) {
          continue
        }
        if (!sessionPhase && inSession(event.target)) {
          continue
        }
        if (plain && isTyping(event.target)) {
          continue
        }
        if ((Array.isArray(hotkey.keys) ? hotkey.keys : [hotkey.keys]).some((combination) => matchesKeys(event, combination))) {
          event.preventDefault()
          // Keep it from a terminal or remote desktop, which listen on their own elements.
          event.stopPropagation()
          hotkey.run()
          return
        }
      }
    }
    // Session-proof shortcuts are caught on the way down, before a terminal or desktop sees the key;
    // the rest on the way up, after the focused element had its chance.
    const capture = (event: KeyboardEvent) => handle(event, true)
    const bubble = (event: KeyboardEvent) => handle(event, false)
    window.addEventListener('keydown', capture, true)
    window.addEventListener('keydown', bubble)
    return () => {
      window.removeEventListener('keydown', capture, true)
      window.removeEventListener('keydown', bubble)
    }
  }, [])
}
