# Selector DSL

A **Selector** is a small JSON predicate that picks a slice of a
`ProjectedDocument`. Selectors are produced by the authoring agent and
are **always evaluated by pure C# code** — no LLM at runtime.

## Tagged-union form

Every selector is an object with a `kind` discriminator:

```json
{ "kind": "path", "path": "$.sections[?(@.category == 'payment_terms')]" }
```

## Kinds

### `path`
JSONPath subset over the projected graph.

```json
{ "kind": "path", "path": "$.sections[?(@.heading_path =~ '/Payment Terms/.*')]" }
```

Supported: `$`, `.field`, `[index]`, `[?(predicate)]` with `==`, `!=`,
`<`, `<=`, `>`, `>=`, `=~`, `&&`, `||`, `!`, string and integer
literals, and the special `@` reference.

### `regex`
Match a string field against a regex.

```json
{ "kind": "regex", "field": "$.sections[*].heading", "pattern": "^Section\\s+\\d+\\s+" }
```

### `hasField`
Asserts a field exists (and is not null).

```json
{ "kind": "hasField", "field": "$.governing_law" }
```

### `valueIn`
Asserts a field's value is one of an enumeration.

```json
{ "kind": "valueIn", "field": "$.term_unit", "values": ["months", "years"] }
```

### `all`, `any`, `not`
Boolean composition. `all` is conjunction, `any` is disjunction,
`not` is negation. They take a `selectors` array (or `selector` for
`not`).

```json
{
  "kind": "all",
  "selectors": [
    { "kind": "path", "path": "$.parties[?(@.role == 'Vendor')]" },
    { "kind": "path", "path": "$.sections[?(@.category == 'payment_terms')]" }
  ]
}
```

## Output of a match

The matcher returns `IReadOnlyList<MatchedSection>` where each item
carries:
- the matched JSON sub-graph (typed against the rule's
  `appliesToSchema`),
- the path it was matched at,
- the `SourceSpan` it maps to (looked up via the projection's
  `SpanMap`).

The `SourceSpan` is what drives the markup engine — it's how a
`Verdict` becomes a tracked-change comment anchored to the right place
in the original .docx.

## Authoring constraints

Selectors must be:
- **Pure** — no side effects, no network calls, no time sources.
- **Total** — the matcher's behavior is defined for any well-formed
  projection.
- **Deterministic** — the same projection always yields the same
  ordered list of matches.

The authoring agent is constrained by JSON schema; selectors that do
not validate are rejected before publish.
