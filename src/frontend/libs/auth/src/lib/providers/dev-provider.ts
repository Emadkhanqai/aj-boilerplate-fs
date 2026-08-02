import { capabilitiesForRoles, isRole } from '../roles';
import type { AuthProviderApi, AuthSession, DevUser, UserProfile } from '../auth.types';

/**
 * Dev auth provider — the role picker used for local/offline development. There is no identity
 * provider: the operator chooses one of the sample users, and the chosen session is persisted to
 * localStorage so a refresh keeps you signed in. Real deployments use the OIDC provider.
 *
 * This is a DEVELOPMENT convenience and is selected only when `__APP_AUTH_MODE__` is `dev`
 * (see `provider-factory.ts`). It grants no real access: the tokens are synthetic markers and
 * the backend will reject them.
 */

/** localStorage key holding the picked dev session. Exported so the offline mock layer can
 * resolve the current user + role and project responses to match the picker. */
export const DEV_SESSION_STORAGE_KEY = 'app.session.v1';
const STORAGE_KEY = DEV_SESSION_STORAGE_KEY;

/** Sample users — one role each. Replace with names that suit your product. */
export const DEV_USERS: readonly DevUser[] = [
  { userId: 'u-admin', name: 'Avery Admin', email: 'avery.admin@example.com', role: 'admin' },
  { userId: 'u-editor', name: 'Evan Editor', email: 'evan.editor@example.com', role: 'editor' },
  { userId: 'u-viewer', name: 'Vera Viewer', email: 'vera.viewer@example.com', role: 'viewer' },
];

function toSession(devUser: DevUser): AuthSession {
  return {
    user: { userId: devUser.userId, name: devUser.name, email: devUser.email },
    roles: [devUser.role],
    accessToken: `dev.${devUser.userId}`,
  };
}

function readStored(): AuthSession | null {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (raw === null) {
      return null;
    }
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) {
      return null;
    }
    const candidate = parsed as Record<string, unknown>;
    const user = candidate['user'] as Record<string, unknown> | undefined;
    const roles = candidate['roles'];
    if (
      typeof user !== 'object' ||
      user === null ||
      typeof user['userId'] !== 'string' ||
      typeof user['name'] !== 'string' ||
      typeof user['email'] !== 'string' ||
      !Array.isArray(roles) ||
      typeof candidate['accessToken'] !== 'string'
    ) {
      return null;
    }
    const validRoles = roles.filter((r): r is string => typeof r === 'string').filter(isRole);
    if (validRoles.length === 0) {
      return null;
    }
    return {
      user: { userId: user['userId'], name: user['name'], email: user['email'] },
      roles: validRoles,
      accessToken: candidate['accessToken'],
    };
  } catch {
    return null;
  }
}

export function createDevProvider(): AuthProviderApi {
  return {
    mode: 'dev',
    devUsers: DEV_USERS,
    restore(): AuthSession | null {
      return readStored();
    },
    signIn(userId?: string): Promise<AuthSession> {
      const devUser = DEV_USERS.find((u) => u.userId === userId);
      if (devUser === undefined) {
        return Promise.reject(new Error(`Unknown dev user "${userId ?? ''}".`));
      }
      const session = toSession(devUser);
      window.localStorage.setItem(STORAGE_KEY, JSON.stringify(session));
      return Promise.resolve(session);
    },
    signOut(): void {
      window.localStorage.removeItem(STORAGE_KEY);
    },
    fetchProfile(session: AuthSession): Promise<UserProfile> {
      const profile: UserProfile = {
        userId: session.user.userId,
        displayName: session.user.name,
        email: session.user.email,
        roles: session.roles,
        capabilities: capabilitiesForRoles(session.roles),
      };
      return Promise.resolve(profile);
    },
  };
}
