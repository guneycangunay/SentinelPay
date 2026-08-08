# Contributing

## Development workflow

1. Create a focused branch from `main`.
2. Keep payment invariants in the domain project and external details in infrastructure.
3. Add a domain test for every new state rule.
4. Add an integration test for every public HTTP behavior.
5. Run `make verify` before opening a pull request.

## Commit style

Use concise imperative commits, for example:

```text
feat(payments): add idempotent partial refunds
fix(outbox): back off after publisher failure
test(webhooks): cover duplicate provider events
docs(architecture): explain provider retry boundary
```

## Pull requests

Describe the failure mode being addressed, the invariant that protects it, and the test proving the behavior. Call out schema and API compatibility changes explicitly.
