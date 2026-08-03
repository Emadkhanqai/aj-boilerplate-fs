import { http, HttpResponse, delay } from 'msw';
import type {
  ApiResponse,
  CreateItemRequest,
  ItemResponse,
  ItemStatus,
  PagedResponse,
  UpdateItemRequest,
} from '@aj-boilerplate/data-access/api-types';
import type { FeatureAnnouncement } from '@aj-boilerplate/data-access/api-client';

/**
 * Offline API mock for the `demo` build configuration.
 *
 * This exists so the app (and the Playwright suite) can run with NO backend, deterministically.
 * It is NOT a second implementation of the domain: keep it thin, keep it honest about status
 * codes, and let the real API be the source of truth for behaviour. Delete these handlers along
 * with the sample feature and write your own.
 *
 * Every response is wrapped in the same `ApiResponse<T>` envelope the real API returns, because
 * `envelopeInterceptor` unwraps it — a handler that returns a bare DTO would break the app in a
 * way the real backend never would, and you would debug the wrong layer.
 */

/** Small artificial latency so loading states are actually visible while developing. */
const LATENCY_MS = 180;

function envelope<T>(data: T): ApiResponse<T> {
  return {
    success: true,
    data,
    message: null,
    errors: null,
    code: null,
    timestamp: new Date().toISOString(),
    traceId: crypto.randomUUID(),
  };
}

function failure(message: string, code: string): ApiResponse<null> {
  return {
    success: false,
    data: null,
    message,
    errors: [message],
    code,
    timestamp: new Date().toISOString(),
    traceId: crypto.randomUUID(),
  };
}

function nextRowVersion(): string {
  return btoa(String(Date.now() + Math.random()));
}

function seed(): ItemResponse[] {
  const statuses: ItemStatus[] = ['Draft', 'Active', 'Archived'];
  return Array.from({ length: 37 }, (_, i) => {
    const created = new Date(Date.UTC(2026, 0, 1 + i, 9, 0, 0)).toISOString();
    return {
      id: String(i + 1),
      name: `Sample item ${String(i + 1).padStart(2, '0')}`,
      description: i % 3 === 0 ? null : `A short description for sample item ${i + 1}.`,
      status: statuses[i % statuses.length] as ItemStatus,
      createdAt: created,
      updatedAt: i % 4 === 0 ? null : created,
      rowVersion: nextRowVersion(),
    };
  });
}

/** In-memory store. Resets on page reload, which is what makes the demo build deterministic. */
let items: ItemResponse[] = seed();

export function resetItems(): void {
  items = seed();
}

/* ---------------------------------------------------------------------------------------------
 * "What's new" feature announcements.
 *
 * ONE sample announcement so the demo build actually shows the spotlight modal. This is mock
 * data, not seed data — the real announcements are rows the backend owns. The body deliberately
 * exercises BOTH authoring conventions the modal's parser understands: a plain paragraph, and
 * `- <emoji> <title> — <description>` bullet lines that become tinted benefit cards.
 * ------------------------------------------------------------------------------------------ */

function seedAnnouncements(): FeatureAnnouncement[] {
  return [
    {
      id: '3f1a6c7e-2b44-4d1e-9f0a-77c2d8e5b910',
      key: 'saved-views-v1',
      titleEn: 'Saved views are here 🔖',
      titleAr: 'العروض المحفوظة متاحة الآن 🔖',
      bodyEn: [
        'Set up a list the way you like it once, then jump straight back to it from the sidebar.',
        '',
        '- 🔖 Saved views — pin any combination of filters and sorting under a name you choose',
        '- ⚡ Instant search — results narrow as you type, no page reload in between',
        '- 🌙 Dark mode — follows your system setting automatically, or pick one and stay there',
      ].join('\n'),
      bodyAr: [
        'اضبط القائمة كما تحب مرة واحدة، ثم عد إليها مباشرة من الشريط الجانبي.',
        '',
        '- 🔖 العروض المحفوظة — ثبّت أي مجموعة من عوامل التصفية والترتيب تحت اسم تختاره',
        '- ⚡ بحث فوري — تتقلّص النتائج أثناء الكتابة دون إعادة تحميل الصفحة',
        '- 🌙 الوضع الداكن — يتبع إعداد نظامك تلقائيًا، أو اختر واحدًا والزمه',
      ].join('\n'),
      displayOrder: 0,
      createdAt: new Date(Date.UTC(2026, 0, 15, 9, 0, 0)).toISOString(),
    },
  ];
}

const announcements: FeatureAnnouncement[] = seedAnnouncements();

/**
 * Ids this browser session has acknowledged. In-memory, so a reload replays the announcement —
 * which is what makes the demo deterministic, exactly like the item store above. The real API
 * persists acknowledgement per user, server-side, so it survives reloads and other devices.
 */
const acknowledged = new Set<string>();

export function resetAnnouncements(): void {
  acknowledged.clear();
}

interface AcknowledgeFeaturesRequest {
  featureIds: string[];
}

export const handlers = [
  http.get('/api/v1/items', async ({ request }) => {
    await delay(LATENCY_MS);
    const url = new URL(request.url);
    const page = Number(url.searchParams.get('page') ?? '1');
    const pageSize = Number(url.searchParams.get('pageSize') ?? '20');
    const search = (url.searchParams.get('search') ?? '').trim().toLowerCase();

    const matched = search === '' ? items : items.filter((i) => i.name.toLowerCase().includes(search));
    const start = (page - 1) * pageSize;
    const body: PagedResponse<ItemResponse> = {
      items: matched.slice(start, start + pageSize),
      total: matched.length,
      page,
      pageSize,
    };
    return HttpResponse.json(envelope(body));
  }),

  http.get('/api/v1/items/:id', async ({ params }) => {
    await delay(LATENCY_MS);
    const item = items.find((i) => i.id === params['id']);
    if (item === undefined) {
      return HttpResponse.json(failure('Item not found.', 'NOT_FOUND'), { status: 404 });
    }
    return HttpResponse.json(envelope(item));
  }),

  http.post('/api/v1/items', async ({ request }) => {
    await delay(LATENCY_MS);
    const body = (await request.json()) as CreateItemRequest;
    if (body.name.trim() === '') {
      return HttpResponse.json(failure('Name is required.', 'VALIDATION_ERROR'), { status: 400 });
    }
    const created: ItemResponse = {
      id: String(Math.max(0, ...items.map((i) => Number(i.id))) + 1),
      name: body.name,
      description: body.description ?? null,
      status: body.status ?? 'Draft',
      createdAt: new Date().toISOString(),
      updatedAt: null,
      rowVersion: nextRowVersion(),
    };
    items = [created, ...items];
    return HttpResponse.json(envelope(created), { status: 201 });
  }),

  http.put('/api/v1/items/:id', async ({ params, request }) => {
    await delay(LATENCY_MS);
    const index = items.findIndex((i) => i.id === params['id']);
    if (index === -1) {
      return HttpResponse.json(failure('Item not found.', 'NOT_FOUND'), { status: 404 });
    }
    const body = (await request.json()) as UpdateItemRequest;
    const current = items[index] as ItemResponse;
    // The concurrency check the whole boilerplate is demonstrating: a write carrying a rowVersion
    // that is no longer current is rejected, not silently applied over someone else's change.
    if (body.rowVersion !== current.rowVersion) {
      return HttpResponse.json(
        failure('This item was changed by someone else while you were editing it.', 'CONFLICT'),
        { status: 409 },
      );
    }
    const updated: ItemResponse = {
      ...current,
      name: body.name,
      description: body.description ?? null,
      status: body.status,
      updatedAt: new Date().toISOString(),
      rowVersion: nextRowVersion(),
    };
    items = items.map((i, idx) => (idx === index ? updated : i));
    return HttpResponse.json(envelope(updated));
  }),

  http.delete('/api/v1/items/:id', async ({ params }) => {
    await delay(LATENCY_MS);
    const exists = items.some((i) => i.id === params['id']);
    if (!exists) {
      return HttpResponse.json(failure('Item not found.', 'NOT_FOUND'), { status: 404 });
    }
    items = items.filter((i) => i.id !== params['id']);
    return new HttpResponse(null, { status: 204 });
  }),

  // The sample announcement carries no page restriction, so it matches every route — the real
  // API compares the normalised `path` against each announcement's registered prefixes.
  http.get('/api/v1/features/unack', async () => {
    await delay(LATENCY_MS);
    const pending = announcements.filter((a) => !acknowledged.has(a.id));
    return HttpResponse.json(envelope(pending));
  }),

  http.post('/api/v1/features/ack', async ({ request }) => {
    await delay(LATENCY_MS);
    const body = (await request.json()) as AcknowledgeFeaturesRequest;
    // Idempotent: acknowledging an id twice is a no-op, exactly as server-side.
    for (const id of body.featureIds ?? []) {
      acknowledged.add(id);
    }
    return new HttpResponse(null, { status: 204 });
  }),
];
