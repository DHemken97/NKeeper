import { useState } from 'react'
import { Button } from '../../../components/ui/Button'
import { Dialog } from '../../../components/ui/Dialog'
import { ChangePasswordForm } from './ChangePasswordForm'

/** Change password from the account menu. Other sessions of the account are signed out. */
export function ChangePasswordDialog({ onClose, onChanged }: { onClose: () => void; onChanged: () => void }) {
  const [submitting, setSubmitting] = useState(false)
  return (
    <Dialog
      title="Change password"
      description="You stay signed in here; your other sessions will be signed out."
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={submitting}>
            Cancel
          </Button>
          <Button type="submit" form="change-password" loading={submitting}>
            Change password
          </Button>
        </>
      }
    >
      <ChangePasswordForm id="change-password" onChanged={onChanged} onSubmittingChange={setSubmitting} />
    </Dialog>
  )
}
