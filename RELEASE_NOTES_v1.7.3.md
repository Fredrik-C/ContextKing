## Highlights

- Improved query grounding toward `ck get-keyword-map` evidence and session keyword atlas.
- Reduced noisy broad-scope friction by softening disruptive guard behavior into guidance.

## Improvements

- `ck find-scope`
  - Added grounding score output based on query-term overlap with matched scope terms and atlas high-value terms.
  - Added low-grounding rewrite suggestion when query grounding is weak.
  - Added no-match grounding hints and rewrite suggestions for faster recovery.
  - Tuned broad/ambiguous detection to require multiple ambiguity signals, reducing false positives.
  - Updated narrowing guidance to use a top-5 shortlist mindset.

- `ck get-keyword-map`
  - Improved session atlas high-value term curation by prioritizing role-grounded terms (`anchor`, `discriminator`, `workflow`) and filtering high broad-risk terms.
  - Continued surfacing keyword-role/evidence outputs to guide better query composition.

- Ranking and query quality internals
  - Added/expanded token normalization, specificity modeling, sparse-term resolution, corpus token statistics, and scope concentration reranking support in source-map internals.
  - Added coverage tests for new source-map grounding/ranking components.

- Guard and workflow behavior
  - Converted key hard blocks to allow-with-guidance flows to reduce session disruption while preserving navigation quality guidance.
  - Reduced repetitive “blocked” patterns for common refinement loops.
