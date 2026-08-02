import { getTestBed } from '@angular/core/testing';
import { BrowserTestingModule, platformBrowserTesting } from '@angular/platform-browser/testing';

// This lib is a plain @nx/js library (not @nx/angular), so unlike libs/shared/ui — which gets
// Angular test bootstrapping for free from the @angular/build:unit-test executor — its inferred
// @nx/vitest test target runs raw Vitest with no Angular TestBed environment initialized. Any spec
// here that uses TestBed (e.g. document-title.service.spec.ts) needs that environment set up once,
// which this file does.
getTestBed().initTestEnvironment(BrowserTestingModule, platformBrowserTesting());
