import { http, HttpResponse, delay } from 'msw';
import type {
  AuthHistorySortBy,
  AuthenticationSession,
  DirectoryGroup,
  DirectoryMember,
  DirectoryUser,
} from '@aj-boilerplate/data-access/api-types';

/**
 * Demo directory, sign-in history and portal list for the offline build. In-memory (reload
 * resets it). Mirrors the documented statuses of the real endpoints: a blank `q` answers an
 * empty list, a bad domain is 422, an unknown subject is 404.
 */
const LATENCY_MS = 150;

const ok = <T>(data: T) => ({
  success: true,
  data,
  message: null,
  errors: null,
  code: null,
  timestamp: new Date().toISOString(),
  traceId: crypto.randomUUID(),
});
const fail = (message: string, code: string) => ({ ...ok(null), success: false, message, errors: [message], code });

const GROUPS: DirectoryGroup[] = [
  { path: '/portal/admins', name: 'Administrators' },
  { path: '/portal/editors', name: 'Editors' },
  { path: '/portal/viewers', name: 'Viewers' },
];
const ALLOWED_DOMAIN = '@example.org';

const FIRST = ['Amal', 'Basil', 'Chitra', 'Dev', 'Elif', 'Farid', 'Gita', 'Hana', 'Idris', 'Jun', 'Kai', 'Lena'];
const LAST = ['Abbas', 'Bose', 'Cole', 'Diaz', 'Evans', 'Farah', 'Grant', 'Hale'];

function seedMembers(): DirectoryMember[] {
  return Array.from({ length: 24 }, (_, i) => {
    const firstName = FIRST[i % FIRST.length] as string;
    const lastName = LAST[i % LAST.length] as string;
    const username = `${firstName}.${lastName}${i}`.toLowerCase();
    return {
      user: {
        subject: `sub-${String(i + 1).padStart(3, '0')}`,
        username,
        email: `${username}${ALLOWED_DOMAIN}`,
        firstName,
        lastName,
        enabled: i % 7 !== 3,
      },
      groups: [(GROUPS[i % GROUPS.length] as DirectoryGroup).path],
    };
  });
}

function seedHistory(members: DirectoryMember[]): AuthenticationSession[] {
  const reasons = ['logged-out', 'session-expired', 'signed-out-by-system'] as const;
  return Array.from({ length: 57 }, (_, i) => {
    const m = (members[i % members.length] as DirectoryMember).user;
    const start = Date.UTC(2026, 10, 20, 8, 0, 0) - i * 5_400_000;
    const open = i % 9 === 0;
    const secs = 120 + ((i * 937) % 14_000);
    const reason = reasons[i % 3] as (typeof reasons)[number];
    return {
      id: `00000000-0000-4000-8000-${String(i).padStart(12, '0')}`,
      userId: m.subject,
      userDisplayName: `${m.firstName} ${m.lastName}`,
      signedInAtUtc: new Date(start).toISOString(),
      endedAtUtc: open ? null : new Date(start + secs * 1000).toISOString(),
      endReason: open ? null : reason,
      endReasonDetail: null,
      result: open ? 'Success' : reason === 'signed-out-by-system' ? 'Failure' : 'Success',
      durationSeconds: open ? null : secs,
    };
  });
}

let members = seedMembers();
const history = seedHistory(members);

const SORTERS: Record<AuthHistorySortBy, (s: AuthenticationSession) => string | number> = {
  signedIn: (s) => s.signedInAtUtc,
  user: (s) => (s.userDisplayName ?? '').toLowerCase(),
  sessionEnded: (s) => s.endedAtUtc ?? '9999',
  duration: (s) => s.durationSeconds ?? -1,
  result: (s) => s.result ?? '',
  endReason: (s) => s.endReason ?? '',
};

const find = (subject: string | readonly string[] | undefined) => members.find((m) => m.user.subject === subject);
const noSeat = () => HttpResponse.json(fail('No seat for that person.', 'NOT_FOUND'), { status: 404 });
const groupsResult = (groups: string[], added: string[] = [], removed: string[] = []) =>
  HttpResponse.json(ok({ groups, added, removed, portals: [] }));

export const adminHandlers = [
  http.get('/api/v1/me/portals', async () => {
    await delay(LATENCY_MS);
    return HttpResponse.json(
      ok({
        portalKeys: ['usp', 'cad', 'ebudgeting'],
        portals: [
          { key: 'usp', name: 'Unified portal', url: '/' },
          { key: 'cad', name: 'CAD', url: '/cad/' },
          { key: 'ebudgeting', name: 'E-Budgeting', url: '/ebudgeting/' },
        ],
      }),
    );
  }),

  http.get('/api/v1/admin/groups', async () => {
    await delay(LATENCY_MS);
    return HttpResponse.json(ok({ groups: GROUPS, portals: [] }));
  }),

  http.get('/api/v1/admin/members', async () => {
    await delay(LATENCY_MS);
    return HttpResponse.json(ok({ members, portals: [] }));
  }),

  http.get('/api/v1/admin/users', async ({ request }) => {
    await delay(LATENCY_MS);
    const q = (new URL(request.url).searchParams.get('q') ?? '').trim().toLowerCase();
    const users: DirectoryUser[] =
      q === ''
        ? []
        : members
            .map((m) => m.user)
            .filter((u) => `${u.firstName} ${u.lastName} ${u.email}`.toLowerCase().includes(q));
    return HttpResponse.json(ok({ users }));
  }),

  http.post('/api/v1/admin/users', async ({ request }) => {
    await delay(LATENCY_MS);
    const b = (await request.json()) as { email?: string; firstName?: string; lastName?: string; groupPath?: string };
    if (!b.email || !b.groupPath) return HttpResponse.json(fail('Email and group are required.', 'VALIDATION_ERROR'), { status: 400 });
    if (!b.email.toLowerCase().endsWith(ALLOWED_DOMAIN)) {
      return HttpResponse.json(fail('Email domain not allowed.', 'EMAIL_DOMAIN_NOT_ALLOWED'), { status: 422 });
    }
    const user: DirectoryUser = {
      subject: `sub-${String(members.length + 1).padStart(3, '0')}`,
      username: b.email,
      email: b.email,
      firstName: b.firstName ?? null,
      lastName: b.lastName ?? null,
      enabled: true,
    };
    members = [{ user, groups: [b.groupPath] }, ...members];
    return HttpResponse.json(ok({ user, groups: [b.groupPath] }));
  }),

  http.get('/api/v1/admin/users/:subject/groups', async ({ params }) => {
    await delay(LATENCY_MS);
    const m = find(params['subject']);
    return m ? groupsResult(m.groups) : noSeat();
  }),

  http.put('/api/v1/admin/users/:subject/groups', async ({ params, request }) => {
    await delay(LATENCY_MS);
    const m = find(params['subject']);
    if (!m) return noSeat();
    const next = ((await request.json()) as { groups?: string[] }).groups ?? [];
    const added = next.filter((g) => !m.groups.includes(g));
    const removed = m.groups.filter((g) => !next.includes(g));
    members = members.map((x) => (x === m ? { ...x, groups: next } : x));
    return groupsResult(next, added, removed);
  }),

  ...(['disable', 'enable'] as const).map((verb) =>
    http.post(`/api/v1/admin/users/:subject/${verb}`, async ({ params }) => {
      await delay(LATENCY_MS);
      const m = find(params['subject']);
      if (!m) return noSeat();
      members = members.map((x) => (x === m ? { ...x, user: { ...x.user, enabled: verb === 'enable' } } : x));
      return new HttpResponse(null, { status: 204 });
    }),
  ),

  http.post('/api/v1/admin/users/:subject/refresh', async ({ params }) => {
    await delay(LATENCY_MS);
    return find(params['subject']) ? new HttpResponse(null, { status: 204 }) : noSeat();
  }),

  http.get('/api/v1/admin/authentication-history/users', async () => {
    await delay(LATENCY_MS);
    return HttpResponse.json(
      ok(members.map((m) => ({ userId: m.user.subject, userDisplayName: `${m.user.firstName} ${m.user.lastName}` }))),
    );
  }),

  http.get('/api/v1/admin/authentication-history', async ({ request }) => {
    await delay(LATENCY_MS);
    const p = new URL(request.url).searchParams;
    const page = Number(p.get('page') ?? '1');
    const pageSize = Number(p.get('pageSize') ?? '20');
    const sortBy = (p.get('sortBy') ?? 'signedIn') as AuthHistorySortBy;
    const dir = p.get('sortDir') === 'asc' ? 1 : -1;
    const [user, from, to] = [p.get('userId'), p.get('from'), p.get('to')];
    const sorter = SORTERS[sortBy] ?? SORTERS.signedIn;
    const matched = history
      .filter((s) => (!user || s.userId === user) && (!from || s.signedInAtUtc >= from) && (!to || s.signedInAtUtc < to))
      .sort((a, b) => (sorter(a) < sorter(b) ? -dir : sorter(a) > sorter(b) ? dir : 0));
    const start = (page - 1) * pageSize;
    return HttpResponse.json(ok({ items: matched.slice(start, start + pageSize), total: matched.length, page, pageSize }));
  }),
];
