# Method reranking configuration

Method reranking defaults to enabled, including existing `.ck.json` files that omit the setting. Explicit `methodRerank: false` opts out. Missing or invalid fields use defaults; numeric caps bound work. Malformed configuration warnings appear only with `--verbose`.

```json
{
  "findFiles": {
    "semanticRerank": true,
    "methodRerank": true,
    "methodRerankModel": "code-reranker",
    "methodCandidateFiles": 50,
    "maxMethodsPerFile": 8,
    "maxMethodsTotal": 500,
    "maxMethodCardChars": 6000,
    "maxBodyChars": 3500,
    "lexicalWeight": 0.45,
    "metadataSemanticWeight": 0.15,
    "methodSemanticWeight": 0.35,
    "structuralBoostMax": 0.08,
    "flatMethodThreshold": 0.03
  }
}
```

Standard installations include the [model pack](../models/code-reranker/README.md). No model is downloaded during search. `methodRerankModel` names a directory under the installation's `models/` directory or `~/.ck/models`; it accepts letters, digits, underscores and hyphens. `CK_CODE_MODEL_DIR` overrides the directory for local testing.

| Setting | Default | Hard bounds |
|---|---:|---:|
| `methodCandidateFiles` | 50 | 1–100 |
| `maxMethodsPerFile` | 8 | 1–100 |
| `maxMethodsTotal` | 500 | 1–2000 |
| `maxMethodCardChars` | 6000 | 128–16000 |
| `maxBodyChars` | 3500 | 0–12000 |
| Fusion weights | shown above | 0–1 |
| `structuralBoostMax` | 0.08 | 0–0.08 |
| `flatMethodThreshold` | 0.03 | 0–1 |

With methods enabled, default lexical overfetch is 50–100 candidates; metadata-only mode retains its existing maximum of 200. Explicit `minOverfetch` and `maxOverfetch` are capped at 1000. A larger requested `--top` is retained, while extraction remains capped independently. Each candidate source file has a 2 MB parser safety ceiling.

The legacy metadata-only `lexicalWeight` default remains 0.65 and `semanticWeight` remains 0.30. Method fusion defaults to lexical 0.45, metadata 0.15, and method 0.35. An explicit `lexicalWeight` applies to both stages. Available components are normalized; absent method scores never remove a lexical candidate. Flat method scores reduce the method weight. Structural bonuses are capped independently of member count.

Explain output retains existing metadata fields and adds `metadata`, `method`, `best_member`, and `evidence`. `method=unavailable` indicates a stage failure; `method=-` means disabled or no executable member score. Bodies and string literals are never explanation evidence. Negative task wording produces at most one verbose advisory warning; no task rewriting or exclusion filtering occurs.

Default enablement for v1.8.14 follows the requested release configuration. Passing ordinary unit tests does not establish the spec's held-out acceptance criteria; the full evaluation remains outstanding.
