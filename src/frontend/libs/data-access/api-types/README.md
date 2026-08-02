# data-access/api-types

The API contract, as TypeScript. **Generated — never hand-edited.**

```sh
npm run generate:api   # openapi-typescript <swagger url> -o src/lib/types.ts
```

Point the `generate:api` script in the root `package.json` at your backend's OpenAPI document.
Run it after every backend contract change, and commit the result — a diff here is the review
signal that an API contract moved.

## Rules

- No other file in the workspace may re-declare a backend DTO. Import from here.
- No business logic, no classes, no runtime code beyond plain constants for enum values.
- Consume versioned endpoints only (`/api/v1/...`).
