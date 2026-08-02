# Standard: TypeScript

**Applies to:** the Angular frontend and any TypeScript tooling in the repository.

## Compiler

- **`strict: true`**, with every strict sub-flag left on.
- Also enable: `noUncheckedIndexedAccess`, `noImplicitOverride`,
  `exactOptionalPropertyTypes`, `noFallthroughCasesInSwitch`, `noImplicitReturns`,
  `noUnusedLocals`, `noUnusedParameters`.
- `"module": "ESNext"`, `"moduleResolution": "Bundler"`, a current `"target"`.
- `isolatedModules: true`; use `import type` for type-only imports.
- Path aliases mirror the Nx library structure (`@aj-boilerplate/shared/util`), declared once
  in `tsconfig.base.json`.

## Rules

- **No `any`.** Use `unknown` plus narrowing, a generic, or a precise type. `any` is a review
  blocker.
- **No non-null assertions (`!`)** except with a documented, provable reason on the same line.
- **No `as` casts to silence the compiler.** A cast is a claim you are making about runtime
  reality; if you cannot justify it, the type is wrong.
- Prefer `type` aliases for unions and DTO shapes; `interface` for extendable object shapes.
- **Discriminated unions for state machines** (request status, form mode) — never a bag of
  optional booleans.
- `as const` for literal tuples and string-enum-like sets; prefer union literals over TS
  `enum`.
- **All exported functions declare an explicit return type.**
- Validate at the boundary: data crossing into the app from the network or storage is
  `unknown` until it has been checked, even when a generated type claims otherwise.

## DTOs & backend sync

- **Never hand-write a type that duplicates a backend contract.** The API DTOs are the source
  of truth; generated types live in `libs/data-access/api-types` and come from the backend
  OpenAPI document.
- **Never edit a generated file** — regenerate it with `/sync`.
- See [`api-design.md`](api-design.md) and [`angular.md`](angular.md).

## Related

[`angular.md`](angular.md) · [`api-design.md`](api-design.md) · [`testing.md`](testing.md)
