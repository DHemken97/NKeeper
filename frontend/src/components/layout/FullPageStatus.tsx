import { Button } from '../ui/Button'
import { Logo } from '../ui/Logo'
import { Spinner } from '../ui/Spinner'
import styles from './FullPageStatus.module.css'

interface FullPageStatusProps {
  kind: 'loading' | 'error'
  message: string
  onRetry?: () => void
}

export function FullPageStatus({ kind, message, onRetry }: FullPageStatusProps) {
  return (
    <main className={styles.page}>
      <Logo size={36} />
      <div className={styles.status} role={kind === 'error' ? 'alert' : 'status'}>
        {kind === 'loading' ? <Spinner size="sm" /> : null}
        <p>{message}</p>
      </div>
      {kind === 'error' && onRetry ? (
        <Button variant="secondary" onClick={onRetry}>
          Try again
        </Button>
      ) : null}
    </main>
  )
}
