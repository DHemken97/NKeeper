import { createContext, useContext } from 'react';
import type { AuthDriverType, UserInfo } from '../lib/types.ts';

export type SignInResult =
  | { status: 'Authenticated' }
  | { status: 'MfaRequired'; mfaToken: string; methods: string[] };

export interface AuthContextValue {
  user: UserInfo | null;
  /** True until the stored session (if any) has been checked. */
  loading: boolean;
  signIn(username: string, password: string, driver: AuthDriverType): Promise<SignInResult>;
  verifyTotp(mfaToken: string, code: string): Promise<void>;
  signOut(): void;
  refreshUser(): Promise<void>;
}

export const AuthContext = createContext<AuthContextValue | null>(null);

export function useAuth(): AuthContextValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used inside <AuthProvider>.');
  return value;
}
