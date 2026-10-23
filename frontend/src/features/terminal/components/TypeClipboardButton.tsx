import { useEffect, useRef, useState } from 'react'
import { Alert } from '../../../components/ui/Alert'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { TextArea } from '../../../components/ui/TextArea'
import { MAX_TYPED_LENGTH, type TypeResult } from '../typeText'

interface TypeClipboardButtonProps {
  /** Types the text into the console, stopping when `signal` is aborted. */
  type: (text: string, signal: AbortSignal) => Promise<TypeResult>
  /** Gives the console the keyboard back afterwards. */
  onDone: () => void
  /** Says that some characters can't be typed (SPICE sends US keyboard keys). */
  usKeyboardOnly?: boolean
}

/**
 * "Type clipboard": types the clipboard's text into a VM console key by key, since VM consoles
 * have no shared clipboard. When the browser won't hand over the clipboard, it asks for the text
 * to be pasted instead.
 */
export function TypeClipboardButton({ type, onDone, usKeyboardOnly = false }: TypeClipboardButtonProps) {
  const [asking, setAsking] = useState(false)
  const [text, setText] = useState('')
  const [typing, setTyping] = useState<AbortController | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const typingRef = useRef<AbortController | null>(null)

  // Closing the console stops the typing.
  useEffect(() => () => typingRef.current?.abort(), [])

  async function start(value: string) {
    if (value.length === 0) {
      setNotice('The clipboard has no text to type.')
      return
    }
    if (value.length > MAX_TYPED_LENGTH) {
      setNotice(`That’s ${value.length.toLocaleString()} characters; NKeeper types at most ${MAX_TYPED_LENGTH.toLocaleString()} at once.`)
      return
    }
    const controller = new AbortController()
    typingRef.current = controller
    setTyping(controller)
    setNotice(null)
    onDone()
    try {
      const { skipped } = await type(value, controller.signal)
      if (skipped > 0) {
        setNotice(
          `${skipped} character${skipped === 1 ? '' : 's'} couldn’t be typed${usKeyboardOnly ? ' (only what’s on a US keyboard can be)' : ''}.`,
        )
      }
    } catch (error) {
      setNotice(error instanceof Error ? error.message : 'Typing stopped.')
    } finally {
      typingRef.current = null
      setTyping(null)
      onDone()
    }
  }

  async function typeClipboard() {
    let clipboard: string | null = null
    try {
      clipboard = (await navigator.clipboard?.readText()) ?? null
    } catch {
      clipboard = null
    }
    if (clipboard === null) {
      // No access (permission, an insecure context, or a browser without it): paste it instead.
      setText('')
      setAsking(true)
      return
    }
    await start(clipboard)
  }

  return (
    <>
      {typing ? (
        <Button variant="ghost" size="sm" onClick={() => typing.abort()}>
          Stop typing
        </Button>
      ) : (
        <Button variant="ghost" size="sm" onClick={() => void typeClipboard()} title="Types the clipboard’s text into the console">
          Type clipboard
        </Button>
      )}
      {notice ? (
        <Dialog title="Type clipboard" onClose={() => setNotice(null)} footer={<Button onClick={() => setNotice(null)}>OK</Button>}>
          <Alert variant="info">{notice}</Alert>
        </Dialog>
      ) : null}
      {asking ? (
        <Dialog
          title="Type text"
          description="The browser didn’t let NKeeper read the clipboard. Paste the text here, and it’s typed into the console."
          onClose={() => setAsking(false)}
          footer={
            <>
              <Button variant="secondary" onClick={() => setAsking(false)}>
                Cancel
              </Button>
              <Button
                disabled={text.length === 0}
                onClick={() => {
                  setAsking(false)
                  void start(text)
                }}
              >
                Type
              </Button>
            </>
          }
        >
          <TextArea label="Text to type" value={text} onChange={(event) => setText(event.target.value)} rows={6} data-autofocus />
        </Dialog>
      ) : null}
    </>
  )
}
