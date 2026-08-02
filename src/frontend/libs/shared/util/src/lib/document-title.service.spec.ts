import { TestBed } from '@angular/core/testing';
import { Title } from '@angular/platform-browser';
import { describe, expect, it } from 'vitest';
import { DocumentTitleService } from './document-title.service';

describe('DocumentTitleService', () => {
  it('sets the document title via Angular\'s Title service', () => {
    TestBed.configureTestingModule({});
    const service = TestBed.inject(DocumentTitleService);
    service.set('Items · Application');
    expect(TestBed.inject(Title).getTitle()).toBe('Items · Application');
  });
});
