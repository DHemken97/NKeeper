import { api } from '../../../lib/api'
import type { ColorSchemePreferences } from '../../appearance/colorSchemes'

export const accountApi = {
  /** Changes the signed-in user's password; their other sessions are signed out. */
  changePassword: (currentPassword: string, newPassword: string) =>
    api.post<void>('/api/account/password', { body: { currentPassword, newPassword } }),
  /** Saves this user's default host-tree grouping and sort. */
  updateWorkspacePreferences: (groupBy: string[], sort: 'name' | 'address' | 'updated') =>
    api.put<{ groupBy: string[]; sort: 'name' | 'address' | 'updated' }>('/api/account/workspace-preferences', { body: { groupBy, sort } }),
  /** Saves this user's color scheme and the schemes they built. */
  updateColorSchemes: (preferences: ColorSchemePreferences) => api.put<ColorSchemePreferences>('/api/account/color-schemes', { body: preferences }),
}
