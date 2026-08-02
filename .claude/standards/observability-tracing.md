# Standard: Observability & Tracing

Logs, traces, and metrics are correlated end to end. Every response carries a `traceId` the
user can quote to support, and that same id ties together the server-side telemetry.

## Correlation

- **One correlation id per request.** Accept an inbound `X-Correlation-ID` from a trusted
  caller; otherwise use `Activity.Current?.Id ?? HttpContext.TraceIdentifier`. Attach it to
  the logging scope and return it as `traceId` in the `ApiResponse` envelope (see
  [`api-response-format.md`](api-response-format.md)).
- Propagate **W3C Trace Context** (`traceparent`) on every outbound call — identity provider,
  integrations, email, and the database.

## Tracing & metrics — OpenTelemetry

- Instrument with **OpenTelemetry**: ASP.NET Core, `HttpClient`, and EF Core instrumentation
  at minimum. Export via OTLP to the provider's backend (see [`cloud.md`](cloud.md)) — the
  exporter is configuration, the instrumentation is not.
- **Custom domain telemetry on business events** — resource created, state transitioned,
  integration call completed. Emit as spans and counters with the resource id and the actor's
  role. Never put a restricted field value in a span attribute.
- Track the golden signals per endpoint: request rate, latency (p50/p95/p99), error rate,
  saturation — plus cache hit ratio and database call count per request.

## Structured logging

- **Structured properties, never string concatenation.** Standard fields: `traceId`,
  `actorId`, `actorRole`, `route`, `statusCode`, `elapsedMs`, and the primary resource id
  where relevant.
- Levels: business 4xx → `Information` / `Warning`; unexpected 5xx → `Error` with full detail;
  security events → a dedicated category.
- **Never log** secrets, tokens, authorization headers, connection strings, full PII, or any
  field the API itself would not return to that caller.
- In Production, the logs hold the detail deliberately kept out of client responses (see
  [`error-handling.md`](error-handling.md)).
- Sampling is acceptable for high-volume success paths; **errors and security events are never
  sampled out.**

## Health

- `/health/live` — the process is running.
- `/health/ready` — dependencies are reachable (database, distributed cache, identity
  provider). The platform's readiness probe uses it; a failing readiness check must take the
  instance out of rotation, not crash it.

## Audit vs telemetry

The **audit log** is a compliance record and a first-class business output; telemetry is
operational and disposable. They have different retention, different access control, and
different schemas. Business-critical actions go to **both**.

## Related

[`error-handling.md`](error-handling.md) · [`api-response-format.md`](api-response-format.md) · [`middleware.md`](middleware.md) · [`cloud.md`](cloud.md)
