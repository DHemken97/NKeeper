import { useState } from 'react'
import { Button } from '../../../components/ui/Button'
import styles from './Form.module.css'

interface DeleteButtonProps {
  /** What the confirmation asks, e.g. "Delete this host?" */
  question: string
  onConfirm: () => void
  loading: boolean
  disabled?: boolean
}

/** Two-step delete for dialog footers: the first click asks, the second deletes. */
export function DeleteButton({ question, onConfirm, loading, disabled }: DeleteButtonProps) {
  const [confirming, setConfirming] = useState(false)

  if (!confirming) {
    return (
      <Button variant="danger" className={styles.footerStart} onClick={() => setConfirming(true)} disabled={disabled}>
        Delete
      </Button>
    )
  }

  return (
    <div className={styles.confirm} role="group" aria-label="Confirm delete">
      <span>{question}</span>
      <Button variant="danger" size="sm" onClick={onConfirm} loading={loading} autoFocus>
        Yes, delete
      </Button>
      <Button variant="ghost" size="sm" onClick={() => setConfirming(false)} disabled={loading}>
        Keep
      </Button>
    </div>
  )
}
