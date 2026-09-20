# CodeRankEmbed code model pack

Context King v1.8.14 includes this model in its standard release archives and enables method reranking by default. Set `findFiles.methodRerank=false` in `.ck.json` to opt out. The full held-out quality evaluation described in the specification remains outstanding.

For source checkouts, install the model separately (PowerShell 7, any supported OS):

```powershell
pwsh -File scripts/install-code-model.ps1
```

The installer pins the ONNX export revision, verifies SHA-256 for every asset, includes the upstream MIT license, and installs outside the executable at `~/.ck/models/code-reranker`. Release installers copy the bundled pack without another model download. Re-running against a matching installation works offline. `CK_CODE_MODEL_DIR` can point to a separate test pack. Normal searches do not download anything.

Provenance:

- Base retrieval model: [nomic-ai/CodeRankEmbed](https://huggingface.co/nomic-ai/CodeRankEmbed/tree/3c4b60807d71f79b43f3c4363786d9493691f8b1).
- ONNX conversion and reduced-range INT8 quantization: [mrsladoje/CodeRankEmbed-onnx-int8](https://huggingface.co/mrsladoje/CodeRankEmbed-onnx-int8/tree/e74f446dc6e67e29fcee77213472c142f73a6bbb), derived from jalipalo's ONNX export.
- Research attribution: Tarun Suresh, Revanth Gangi Reddy, Yifei Xu, Zach Nussbaum, Andriy Mulyar, Brandon Duderstadt, and Heng Ji, [CoRNStack: High-Quality Contrastive Data for Better Code Retrieval and Reranking](https://arxiv.org/abs/2412.01007).

The upstream model uses uncased BERT tokenization, pooled 768-dimensional output, and a required retrieval-query prefix. This export exposes the pooled vector as `sentence_embedding`; CK normalizes it locally. The quantized pack declares a minimum batch/single cosine agreement of `0.95`, measured against upstream Python and CK CPU runtimes; the smoke test records the individual values. The runtime also supports explicitly declared CLS or mean pooling for token-level outputs. No Python remote model code is executed by CK.
