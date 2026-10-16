import { api } from '../../../lib/api'

export const accountApi = {
  /** Changes the signed-in user's password; their other sessions are signed out. */
  changePassword: (currentPassword: string, newPassword: string) =>
    api.post<void>('/api/account/password', { body: { currentPassword, newPassword } }),
}
