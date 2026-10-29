import { Fragment } from 'react'
import { Button } from '../ui/Button'
import { Dialog } from '../ui/Dialog'
import styles from './ShortcutsDialog.module.css'

interface Shortcut {
  /** Alternatives, each a combination such as "Ctrl+E". */
  keys: string[]
  what: string
}

const SECTIONS: { title: string; note?: string; shortcuts: Shortcut[] }[] = [
  {
    title: 'Anywhere',
    shortcuts: [
      { keys: ['?', 'Ctrl+/'], what: 'Show these shortcuts' },
      { keys: ['Alt+Shift+H'], what: 'Hosts, in the host tree' },
      { keys: ['Alt+Shift+R'], what: 'Reports' },
      { keys: ['Alt+Shift+U'], what: 'Users (administrators)' },
      { keys: ['Alt+Shift+S'], what: 'Settings (administrators)' },
    ],
  },
  {
    title: 'Hosts',
    shortcuts: [
      { keys: ['Ctrl+K', '/'], what: 'Search (↓ or Enter then goes to the tree)' },
      { keys: ['Ctrl+Enter'], what: 'Connect to the selected host (first SSH service and login, like a double-click)' },
      { keys: ['Shift+F10', 'Menu'], what: 'Its menu: connect, edit, scan, tasks…' },
      { keys: ['Ctrl+E'], what: 'Edit it' },
      { keys: ['Ctrl+D'], what: 'Clone it' },
      { keys: ['Ctrl+S'], what: 'Save, while editing' },
      { keys: ['Alt+Shift+N'], what: 'New host' },
      { keys: ['Alt+Shift+I'], what: 'Import hosts' },
    ],
  },
  {
    title: 'Host tree',
    shortcuts: [
      { keys: ['↑', '↓', 'Home', 'End'], what: 'Move' },
      { keys: ['←', '→'], what: 'Collapse or expand a group' },
      { keys: ['a–z'], what: 'Jump to a host by name' },
      { keys: ['Enter'], what: 'Select the host, or toggle a group' },
      { keys: ['Shift+↑', 'Shift+↓'], what: 'Select a range' },
      { keys: ['Ctrl+Space'], what: 'Add to or remove from the selection' },
      { keys: ['Ctrl+A'], what: 'Select every host shown' },
      { keys: ['Esc'], what: 'Clear the selection' },
      { keys: ['Delete'], what: 'Delete the host (asks first)' },
      { keys: ['Tab'], what: 'On to the details or session' },
    ],
  },
  {
    title: 'Tabs and sessions',
    note: 'These also work inside a terminal or remote desktop, so there’s always a way back out.',
    shortcuts: [
      { keys: ['Alt+PageUp', 'Alt+PageDown'], what: 'Previous or next tab' },
      { keys: ['Alt+Shift+W'], what: 'Close the session tab' },
      { keys: ['Alt+Shift+H'], what: 'Back to the host tree' },
      { keys: ['Esc (held)'], what: 'Leave full screen' },
    ],
  },
]

/** Every keyboard shortcut, opened with ? from anywhere. */
export function ShortcutsDialog({ onClose }: { onClose: () => void }) {
  return (
    <Dialog
      title="Keyboard shortcuts"
      description="Ctrl is ⌘ on a Mac. Inside a session, Ctrl+W, Ctrl+T and Ctrl+N belong to the browser; use the session’s Full screen to send them to the host."
      onClose={onClose}
      size="lg"
      footer={
        <Button variant="secondary" onClick={onClose}>
          Close
        </Button>
      }
    >
      <div className={styles.sections}>
        {SECTIONS.map((section) => (
          <section key={section.title} className={styles.section}>
            <h3 className={styles.title}>{section.title}</h3>
            {section.note ? <p className={styles.note}>{section.note}</p> : null}
            <dl className={styles.list}>
              {section.shortcuts.map((shortcut) => (
                <div key={shortcut.what} className={styles.row}>
                  <dt className={styles.keys}>
                    {shortcut.keys.map((combination, index) => (
                      <Fragment key={combination}>
                        {index > 0 ? <span className={styles.or}>/</span> : null}
                        <kbd className={styles.kbd}>{combination}</kbd>
                      </Fragment>
                    ))}
                  </dt>
                  <dd className={styles.what}>{shortcut.what}</dd>
                </div>
              ))}
            </dl>
          </section>
        ))}
      </div>
    </Dialog>
  )
}
