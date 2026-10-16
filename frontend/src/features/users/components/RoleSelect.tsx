import type { UserRole } from '../types'
import styles from './RoleSelect.module.css'

const ROLES: { value: UserRole; label: string; description: string }[] = [
  { value: 'User', label: 'User', description: 'Sees only the hosts and groups granted to them.' },
  { value: 'Administrator', label: 'Administrator', description: 'Sees and manages every host, and manages users.' },
]

interface RoleSelectProps {
  value: UserRole
  onChange: (role: UserRole) => void
  error?: string
  disabled?: boolean
}

/** Role as two clear choices rather than a bare dropdown. */
export function RoleSelect({ value, onChange, error, disabled }: RoleSelectProps) {
  return (
    <fieldset className={styles.fieldset} disabled={disabled}>
      <legend className={styles.legend}>Role</legend>
      <div className={styles.options}>
        {ROLES.map((role) => (
          <label key={role.value} className={`${styles.option} ${value === role.value ? styles.selected : ''}`}>
            <input type="radio" name="role" value={role.value} checked={value === role.value} onChange={() => onChange(role.value)} />
            <span>
              <span className={styles.label}>{role.label}</span>
              <span className={styles.description}>{role.description}</span>
            </span>
          </label>
        ))}
      </div>
      {error ? <p className={styles.error}>{error}</p> : null}
    </fieldset>
  )
}
