## Method reranking enabled by default

`ck find-files` now reranks bounded lexical candidates using live method evidence from C#, TypeScript/TSX, Kotlin, and Python. The local CodeRankEmbed INT8 model is included in every platform archive and installed automatically at `~/.ck/models/code-reranker` alongside the existing BGE model.

- New and existing repositories use method reranking unless `.ck.json` explicitly sets `findFiles.methodRerank` to `false`.
- `--explain` adds method scores, the best member, and up to three structural evidence identifiers. Default stdout remains `<score>\t<path>`.
- Model assets are SHA-256 verified. Inference stays local, search never downloads models, and method embeddings are not persisted.
- Missing models, parse failures, and inference failures retain lexical/metadata fallback.
- Write `--task` as positive retrieval intent. Negative phrases receive a verbose advisory warning and do not act as exclusion filters.

The larger 100-task/five-repository held-out quality and performance evaluation is still outstanding. This release enables the feature by request; it does not claim those acceptance benchmarks have passed. The quantized model's batch/single smoke contract is cosine similarity greater than 0.95.

## Install or upgrade

macOS / Linux:

```bash
curl -fsSL https://github.com/Fredrik-C/ContextKing/releases/latest/download/install-global.sh | bash
```

Windows (PowerShell 7+):

```powershell
irm https://github.com/Fredrik-C/ContextKing/releases/latest/download/install-global.ps1 | iex
```

Platform archives now include the ~139 MB code model. Download a complete archive when installing offline. Bare binary downloads require the corresponding model pack from an archive or source checkout.

For a new repository, run `ck init`. Existing configuration is preserved. To disable method reranking, set `findFiles.methodRerank` to `false` in `.ck.json`.
