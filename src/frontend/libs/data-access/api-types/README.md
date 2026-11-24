# data-access/api-types

The API contract, as TypeScript. **Generated — never hand-edited.**

```sh
npm run generate:api   # openapi-typescript ../../docs/api/openapi.json -o src/lib/generated.ts
```

The script reads the committed `docs/api/openapi.json` (regenerated from the compiled API by
`scripts/generate-openapi.sh`), so it needs no running server. Run it after every backend contract
change, and commit the result — a diff here is the review signal that an API contract moved.

`generated.ts` is the generated file. `types.ts` beside it is **hand-written on purpose**: the
generated shapes are all optional and lose the `status` union, so it holds the `ApiResponse<T>`
envelope and the sample `Item` types until the OpenAPI document declares required properties and
enums. Replace a type there with `components['schemas'][...]` from `generated.ts` once it can.

## Rules

- No other file in the workspace may re-declare a backend DTO. Import from here.
- No business logic, no classes, no runtime code beyond plain constants for enum values.
- Consume versioned endpoints only (`/api/v1/...`).
