# bge-small-en-v1.5 local bundle

> **License note:** the `LICENSE` file in this directory covers the BGE model files only
> (© 2022 staoxiao / FlagOpen, MIT). It does not apply to the Context King source code.

Place local model assets in this folder so `search-method` can run hybrid lexical + semantic reranking without external hosting.

Expected files (at least):

- `model.onnx` or `model_quantized.onnx` (or under `onnx/`)
- `vocab.txt`

You can fetch a compatible bundle with:

```bash
./scripts/bootstrap-bge-small-en-v1.5.sh
```

If files are missing, CallGraph automatically falls back to lexical-only ranking.
