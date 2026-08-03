/**
 * SINGLE SOURCE OF TRUTH for role -> capability gating on the frontend.
 *
 * Replace `ROLES` and `Capabilities` with your product's own; keep the shape. No component may
 * hard-code a role check — every gate (nav visibility, route guards, feature toggles) must derive
 * from `capabilitiesFor` / `capabilitiesForRoles`, so there is exactly one place to change when
 * the permission model moves.
 *
 * IMPORTANT: this is a UX convenience only. It is NEVER a security boundary — the backend
 * enforces every permission on every request. Hiding a nav item here does not protect the
 * underlying API, and a user who types the URL directly still gets a 403 from the server.
 */

/** The authoritative roles. Mirror whatever your identity provider / API actually issues. */
export const ROLES = ['admin', 'editor', 'viewer'] as const;

export type Role = (typeof ROLES)[number];

export function isRole(value: string): value is Role {
  return (ROLES as readonly string[]).includes(value);
}

/**
 * Normalises a role name coming from outside this file — an API response, a token claim, a
 * stored session — into one of `ROLES`, or `null` when it is not a role we know.
 *
 * **Use this, not `isRole`, on anything that crossed the wire.** The two are not
 * interchangeable and the difference is a real defect we shipped once: the API returns
 * *canonical* role names (`"Admin"`), while `ROLES` is lower-case, so an `isRole` filter
 * silently discarded every role the server sent and the UI rendered "no role" for users who
 * plainly had one. Capabilities kept working — they come from `/me` — which is exactly what
 * made it hard to spot.
 *
 * Note it must return the normalised value rather than being a case-insensitive type guard.
 * A guard that let `"Admin"` through would be worse than the bug it replaced: the value would
 * type as `Role` but miss every `CAPABILITIES_BY_ROLE` lookup, turning a wrong label into an
 * undefined dereference.
 */
export function toRole(value: string): Role | null {
  const normalised = value.trim().toLowerCase();
  return (ROLES as readonly string[]).includes(normalised) ? (normalised as Role) : null;
}

/** Normalises a list of wire role names, dropping any this application does not know. */
export function toRoles(values: readonly string[]): Role[] {
  return values.map(toRole).filter((role): role is Role => role !== null);
}

/**
 * The capability flags the UI gates on. When your API exposes a `/me` endpoint returning the
 * same shape, alias this type to the generated response type instead of declaring it here, so
 * the client and server can never drift.
 */
export interface Capabilities {
  canView: boolean;
  canCreate: boolean;
  canEdit: boolean;
  canDelete: boolean;
  canAdminister: boolean;
}

/** All-false baseline — the unauthenticated state, and the fail-safe default everywhere. */
export const NO_CAPABILITIES: Capabilities = {
  canView: false,
  canCreate: false,
  canEdit: false,
  canDelete: false,
  canAdminister: false,
};

const CAPABILITIES_BY_ROLE: Record<Role, Capabilities> = {
  viewer: {
    canView: true,
    canCreate: false,
    canEdit: false,
    canDelete: false,
    canAdminister: false,
  },
  editor: {
    canView: true,
    canCreate: true,
    canEdit: true,
    canDelete: false,
    canAdminister: false,
  },
  admin: {
    canView: true,
    canCreate: true,
    canEdit: true,
    canDelete: true,
    canAdminister: true,
  },
};

/** Capabilities for a single role. */
export function capabilitiesFor(role: Role): Capabilities {
  return CAPABILITIES_BY_ROLE[role];
}

/**
 * Capabilities for a set of roles — the OR-union across every role the user holds. A user with
 * any role granting a capability gets it.
 */
export function capabilitiesForRoles(roles: readonly Role[]): Capabilities {
  return roles.reduce<Capabilities>((acc, role) => {
    const caps = CAPABILITIES_BY_ROLE[role];
    return {
      canView: acc.canView || caps.canView,
      canCreate: acc.canCreate || caps.canCreate,
      canEdit: acc.canEdit || caps.canEdit,
      canDelete: acc.canDelete || caps.canDelete,
      canAdminister: acc.canAdminister || caps.canAdminister,
    };
  }, NO_CAPABILITIES);
}

/** A capability key — used by route guards to name the required capability. */
export type Capability = keyof Capabilities;
