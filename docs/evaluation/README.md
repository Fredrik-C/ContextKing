# Method reranking evaluation harness

The method reranker cannot be enabled by default from unit tests alone. This directory defines the reproducible query/label format and a small offline scorer for the acceptance gates in the specification.

`queries.jsonl` is intentionally a template: evaluation data is repository-specific and must be reviewed for licensing and secrets before it is committed. Each line has this shape:

```json
{"id":"refund-retry-001","repo":"fixtures/payments","query":"adyen terminal refund retry transient","task":"Find terminal refund handling that retries after transient provider errors.","top":20,"relevant":[{"path":"src/TerminalRefund.cs","grade":3}],"systems":["lexical","metadata-bge","metadata-code","signature-method","structured-method","body-method","full-fusion"]}
```

Use at least 100 tasks across five repositories, with 50 lexical candidates per task, primary/supporting/irrelevant/misleading grades, and development/held-out splits. Never place source bodies or secrets in labels. Keep the primary acceptance task positive; test negative wording separately as an advisory-warning regression.

The required systems are explicit in each record so the same labels can compare lexical baseline, current metadata reranking, code-model metadata, signature-only cards, structured cards without bodies, structured cards with bounded bodies, and full fusion. A run records Recall@5, Recall@10, MRR, nDCG@10, first-result success, warm/cold latency, peak memory, parse/embedding failures, and fallback frequency. Weights must be selected on the development split and frozen before reading held-out results.

Run the scorer after building the CLI:

```powershell
pwsh -File scripts/evaluate-method-reranking.ps1 `
  -Dataset docs/evaluation/queries.jsonl `
  -CommandPath src/ContextKing.Cli/bin/Release/net10.0/ck.exe `
  -Output docs/evaluation/results.json
```

The harness invokes the real command, preserves stderr diagnostics, validates that returned paths are relative, and emits a machine-readable report. It does not download models or transmit source. For model smoke tests use `CK_CODE_MODEL_TEST_DIR` and run the dedicated xUnit test separately.
