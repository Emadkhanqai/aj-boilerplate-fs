import { DestroyRef, Injectable, Injector, effect, inject, signal, untracked } from '@angular/core';
import { MessageService } from 'primeng/api';
import { AuthService, classifyRoleChange } from '@aj-boilerplate/auth';
import type { RoleRecheck } from '@aj-boilerplate/auth';

/** How often the roles-version signal is polled while the tab is visible. */
export const ROLES_VERSION_POLL_INTERVAL_MS = 15_000;

/** After a recheck that could not complete, wait this long before trying the same version again. */
export const ROLE_RECHECK_RETRY_AFTER_MS = 30_000;

/** A burst of 403s triggers one check, not one per request. */
export const FORBIDDEN_CHECK_MIN_INTERVAL_MS = 10_000;

/**
 * Notices that the signed-in user's roles changed while the app is open, and reacts:
 * capabilities LOST -> a blocking reload dialog (`RoleChangeDialogComponent`); only GAINED -> a toast.
 *
 * The signal is `GET /auth/roles-version` — an opaque fingerprint of the session's roles, which is
 * cheap and never touches the API. It is polled every {@link ROLES_VERSION_POLL_INTERVAL_MS} while
 * the tab is visible (timers stop when hidden and one poll fires on return), and immediately after
 * any 403 (`checkNow`, bound to `FORBIDDEN_OBSERVER`). A change in the fingerprint is only a prompt:
 * the watcher then re-reads `/api/v1/me` and compares the real capability sets, because the
 * fingerprint can move for reasons that do not change what the user may do.
 *
 * The first answer is the BASELINE, not a change. It uses raw `fetch`, not `HttpClient`, so a 401
 * here can never run the session-expired redirect: a poll that fails (401, 403, 503, network) is
 * simply ignored and tried again next tick. Gateway mode only.
 */
@Injectable({ providedIn: 'root' })
export class RoleChangeWatcher {
  private readonly injector = inject(Injector);
  private readonly messages = inject(MessageService);
  private readonly destroyRef = inject(DestroyRef);

  private baseline: string | null = null;
  private checking = false;
  private retryNotBefore = 0;
  private forbiddenCheckNotBefore = 0;

  private readonly blockingSignal = signal(false);
  /** True once capabilities were lost; the dialog renders from this and never clears it (reload is the exit). */
  readonly blocking = this.blockingSignal.asReadonly();

  private started = false;
  private listening = false;
  private polling = false;
  private interval: ReturnType<typeof setInterval> | null = null;

  /** Starts watching once a session is confirmed. Idempotent; a no-op on the server and in dev mode. */
  start(): void {
    if (this.started || typeof document === 'undefined' || this.injector.get(AuthService).mode !== 'gateway') {
      return;
    }
    this.started = true;
    effect(
      () => {
        if (this.injector.get(AuthService).sessionConfirmed()) {
          untracked(() => this.begin());
        }
      },
      { injector: this.injector },
    );
  }

  private begin(): void {
    if (this.listening) {
      return;
    }
    this.listening = true;
    const onVisibility = (): void => {
      if (document.visibilityState !== 'visible') {
        this.stopTimer();
        return;
      }
      void this.poll();
      this.interval ??= setInterval(() => void this.poll(), ROLES_VERSION_POLL_INTERVAL_MS);
    };
    document.addEventListener('visibilitychange', onVisibility);
    window.addEventListener('focus', onVisibility);
    this.destroyRef.onDestroy(() => {
      document.removeEventListener('visibilitychange', onVisibility);
      window.removeEventListener('focus', onVisibility);
      this.stopTimer();
    });
    onVisibility();
  }

  private stopTimer(): void {
    if (this.interval !== null) {
      clearInterval(this.interval);
      this.interval = null;
    }
  }

  /** A 403 just happened: check now rather than at the next tick. Throttled, and idle off a confirmed session. */
  checkNow(): void {
    const auth = this.injector.get(AuthService);
    if (!this.listening || !auth.sessionConfirmed() || auth.onSignInPage() || Date.now() < this.forbiddenCheckNotBefore) {
      return;
    }
    this.forbiddenCheckNotBefore = Date.now() + FORBIDDEN_CHECK_MIN_INTERVAL_MS;
    void this.poll();
  }

  private async poll(): Promise<void> {
    if (this.polling || !this.injector.get(AuthService).sessionConfirmed()) {
      return;
    }
    this.polling = true;
    try {
      const res = await fetch('/auth/roles-version', {
        credentials: 'same-origin',
        cache: 'no-store',
        headers: { Accept: 'application/json' },
      });
      if (!res.ok) {
        return;
      }
      const body = (await res.json()) as { rolesVersion?: unknown };
      const version = typeof body.rolesVersion === 'string' ? body.rolesVersion.trim() : '';
      // `null` means "no roles on this session": the access-revoked path already owns that case.
      if (version !== '') {
        this.observe(version);
      }
    } catch {
      // Network blip or a body that is not JSON: nothing learned, try again next tick.
    } finally {
      this.polling = false;
    }
  }

  /** Feeds one observed roles-version in. Public so a test can drive it without the network. */
  observe(version: string): void {
    if (this.blockingSignal()) {
      return;
    }
    if (this.baseline === null) {
      this.baseline = version;
      return;
    }
    if (version === this.baseline || this.checking || Date.now() < this.retryNotBefore) {
      return;
    }
    this.checking = true;
    void this.recheck(version).finally(() => {
      this.checking = false;
    });
  }

  private async recheck(version: string): Promise<void> {
    let outcome: RoleRecheck | null;
    try {
      outcome = await this.injector.get(AuthService).recheckCapabilities();
    } catch {
      outcome = null;
    }
    if (outcome === null) {
      // Could not read `/api/v1/me`: leave the baseline alone so the SAME change is retried.
      this.retryNotBefore = Date.now() + ROLE_RECHECK_RETRY_AFTER_MS;
      return;
    }
    this.baseline = version;
    const kind = classifyRoleChange(outcome.before, outcome.after);
    if (kind === 'reduction') {
      this.blockingSignal.set(true);
    } else if (kind === 'increase') {
      this.messages.add({
        severity: 'info',
        summary: 'Your access has changed',
        detail: 'New options are now available.',
        life: 8000,
      });
    }
  }
}
