/** Shapes returned by the core (non-module) API. Keep in sync with NKeeper.Server/Controllers/AuthContracts.cs. */

export type AuthDriverType = 'LocalDatabase' | 'ActiveDirectory' | 'LinuxPam' | 'OpenIdConnect';

export interface AuthDriverInfo {
  type: AuthDriverType;
  displayName: string;
}

export interface MfaCredentialInfo {
  id: string;
  type: 'Totp' | 'Fido2';
  name: string;
  createdAtUtc: string;
  lastUsedAtUtc: string | null;
}

export interface UserInfo {
  id: string;
  username: string;
  displayName: string | null;
  email: string | null;
  authSource: AuthDriverType;
  isAdmin: boolean;
  mfaCredentials: MfaCredentialInfo[];
}

export interface LoginResponse {
  status: 'Authenticated' | 'MfaRequired';
  accessToken: string | null;
  expiresAtUtc: string | null;
  user: UserInfo | null;
  mfaToken: string | null;
  mfaMethods: string[] | null;
}

export interface ServerModule {
  id: string;
  displayName: string;
  description: string;
}
