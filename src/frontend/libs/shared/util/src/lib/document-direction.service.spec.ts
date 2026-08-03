import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { beforeEach, describe, expect, it } from 'vitest';
import { LanguageService } from './language.service';
import { DocumentDirectionService, directionFor, provideDocumentDirection } from './document-direction.service';

describe('directionFor', () => {
  it('maps Arabic to rtl and English to ltr', () => {
    expect(directionFor('ar')).toBe('rtl');
    expect(directionFor('en')).toBe('ltr');
  });
});

describe('DocumentDirectionService', () => {
  let language: LanguageService;
  let root: HTMLElement;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
    root = TestBed.inject(DOCUMENT).documentElement;
    // jsdom's shared document persists between tests in a file; start from a known state so a
    // passing assertion can never be a leftover from the previous test.
    root.removeAttribute('dir');
    root.removeAttribute('lang');
    language = TestBed.inject(LanguageService);
  });

  it('sets dir="ltr" and lang="en" as soon as it is constructed', () => {
    TestBed.inject(DocumentDirectionService);
    TestBed.tick();

    expect(root.getAttribute('dir')).toBe('ltr');
    expect(root.getAttribute('lang')).toBe('en');
  });

  it('switches the document to dir="rtl" lang="ar" when the language becomes Arabic', () => {
    TestBed.inject(DocumentDirectionService);
    TestBed.tick();

    language.set('ar');
    TestBed.tick();

    // This is the whole bug: before this service existed, `LanguageService.set('ar')` changed
    // which half of a bilingual field rendered and left the layout in LTR.
    expect(root.getAttribute('dir')).toBe('rtl');
    expect(root.getAttribute('lang')).toBe('ar');
  });

  it('switches back to ltr when the language returns to English', () => {
    TestBed.inject(DocumentDirectionService);
    TestBed.tick();

    language.set('ar');
    TestBed.tick();
    language.set('en');
    TestBed.tick();

    expect(root.getAttribute('dir')).toBe('ltr');
    expect(root.getAttribute('lang')).toBe('en');
  });

  it('sets lang alongside dir, so a screen reader switches pronunciation with the layout', () => {
    TestBed.inject(DocumentDirectionService);
    TestBed.tick();
    language.set('ar');
    TestBed.tick();

    // A mirrored page still claiming lang="en" is a screen-reader defect, not a cosmetic one.
    expect(root.getAttribute('lang')).not.toBe('en');
  });

  it('exposes the current direction as a signal for templates to read', () => {
    const service = TestBed.inject(DocumentDirectionService);
    TestBed.tick();

    expect(service.direction()).toBe('ltr');

    language.set('ar');
    TestBed.tick();

    expect(service.direction()).toBe('rtl');
  });
});

describe('provideDocumentDirection', () => {
  it('constructs the service at bootstrap without anything having to inject it', () => {
    TestBed.configureTestingModule({
      providers: [provideZonelessChangeDetection(), provideDocumentDirection()],
    });
    // The first `inject` runs the app initializers, exactly as bootstrap does. That constructs
    // the service, which SCHEDULES its effect — so clearing `dir` here is still a clean slate.
    const root = TestBed.inject(DOCUMENT).documentElement;
    root.removeAttribute('dir');

    // `tick` flushes the scheduled effect. Nothing in this test injects DocumentDirectionService
    // by name: if the initializer were missing, no service would exist, the effect would never
    // run, and `dir` would stay null. That is the regression guard for the failure mode where
    // the service exists, is fully tested, and is never once constructed.
    TestBed.tick();

    expect(root.getAttribute('dir')).toBe('ltr');
  });
});
