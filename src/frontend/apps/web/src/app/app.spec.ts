import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideLocationMocks } from '@angular/common/testing';
import { describe, expect, it } from 'vitest';
import { MessageService } from 'primeng/api';
import { App } from './app';

describe('App', () => {
  let fixture: ComponentFixture<App>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([]), provideLocationMocks(), MessageService],
    }).compileComponents();

    fixture = TestBed.createComponent(App);
    await fixture.whenStable();
  });

  it('creates the root component', () => {
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('renders the router outlet with no leftover scaffold content', () => {
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('router-outlet')).toBeTruthy();
    expect(compiled.querySelector('app-nx-welcome')).toBeNull();
  });

  it('mounts a single p-toast at the root, positioned bottom-right, so it is present on every route', () => {
    const compiled = fixture.nativeElement as HTMLElement;
    const toasts = compiled.querySelectorAll('p-toast');
    expect(toasts.length).toBe(1);
    expect(toasts[0].getAttribute('position')).toBe('bottom-right');
  });
});
