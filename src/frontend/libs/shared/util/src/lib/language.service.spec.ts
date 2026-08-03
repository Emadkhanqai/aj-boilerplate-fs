import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { LanguageService } from './language.service';

describe('LanguageService', () => {
  let lang: LanguageService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    lang = TestBed.inject(LanguageService);
  });

  it('defaults to English', () => {
    expect(lang.current()).toBe('en');
  });

  it('picks the English value while the language is English', () => {
    expect(lang.pick('Hello', 'مرحبا')).toBe('Hello');
  });

  it('picks the Arabic value once the language is Arabic', () => {
    lang.set('ar');
    expect(lang.pick('Hello', 'مرحبا')).toBe('مرحبا');
  });

  it('falls back to English when the Arabic value is missing', () => {
    lang.set('ar');
    expect(lang.pick('Hello', null)).toBe('Hello');
  });

  it('falls back to Arabic when the English value is missing', () => {
    expect(lang.pick(null, 'مرحبا')).toBe('مرحبا');
  });

  it('returns an empty string when both values are missing', () => {
    expect(lang.pick(null, undefined)).toBe('');
  });

  it('treats an empty string as a real value, not a missing one', () => {
    // `??` only falls through on null/undefined — an author who deliberately blanked a field
    // gets a blank, not the other language leaking in.
    expect(lang.pick('', 'مرحبا')).toBe('');
  });
});
