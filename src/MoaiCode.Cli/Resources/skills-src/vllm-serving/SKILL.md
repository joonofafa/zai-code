---
name: vllm-serving
description: Use when deploying, sizing, tuning, or troubleshooting a vLLM model server (standalone or on OpenShift AI / KServe) — GPU memory and context-length sizing, quantization choice, tensor parallelism, tool-calling flags for agents, offline model loading, and reading vLLM errors and metrics.
license: MATE Builder original — no third-party content
---

# vLLM model serving

Flags change between vLLM releases. Before you recommend one, check the installed version with `vllm --version` or `python3 -c "import vllm; print(vllm.__version__)"` and its own help, `vllm serve --help`, which is local and authoritative. Use `references/sizing.md` for the memory arithmetic.

## Procedure
1. **Collect the facts:** the model (local path, parameter count, architecture from `config.json`: `num_hidden_layers`, `num_attention_heads`, `num_key_value_heads`, `hidden_size`/`head_dim`, `max_position_embeddings`, `torch_dtype`), any existing quantization (`quantization_config` in config.json), the GPU model and count (`nvidia-smi --query-gpu=name,memory.total,memory.used --format=csv`), the expected concurrency, and the context length that is actually needed.
2. **Size it** (`references/sizing.md`): weights + KV cache for the target `max_model_len × max_num_seqs` + overhead must fit in `gpu_memory_utilization × VRAM` per GPU after tensor-parallel splitting.
3. **Write the command or manifest** with every non-default flag explained in one line.
4. **Verify locally:** `curl -s localhost:8000/v1/models` (shows `max_model_len`), a one-shot chat completion, a tool-call request if agents will use it, and `/metrics`.

## Flags that matter
| Flag | Use |
|---|---|
| `--tensor-parallel-size N` | Split one model over N GPUs in one node. It must divide the attention head count |
| `--pipeline-parallel-size N` | Split by layers (across nodes); use only when TP alone is not enough |
| `--gpu-memory-utilization 0.90` | Fraction of each GPU vLLM may take (weights + KV cache). Lower it if other processes share the GPU |
| `--max-model-len L` | Max tokens (prompt + output) per request. Lowering it is the main lever when the KV cache does not fit |
| `--max-num-seqs S` | Max concurrent sequences per step; more raises throughput and KV pressure |
| `--max-num-batched-tokens T` | Tokens per scheduler step (prefill chunking); affects latency vs throughput |
| `--quantization awq\|gptq\|fp8\|...` | Usually auto-detected from the checkpoint's `quantization_config`. `fp8` can quantize an fp16 checkpoint on the fly on supported GPUs (Hopper/Ada and newer) |
| `--kv-cache-dtype fp8` | Halves KV cache memory, with a small quality cost; check GPU support |
| `--enable-prefix-caching` | Reuses the KV cache of shared prompt prefixes. Large win for agents with long fixed system prompts (default on in recent versions; check `--help`) |
| `--served-model-name name` | The model id that clients send (keep it stable across model file changes) |
| `--enable-auto-tool-choice --tool-call-parser <parser>` | **Required for agent clients that send `tools`** (MATE Builder, other coding agents). The parser must match the model family's tool-call format (for example `hermes` for Qwen-style, `llama3_json`, `mistral`; list them in `--help`). Without these flags, tool calls come back as plain text or the request fails |
| `--reasoning-parser <name>` | For reasoning models, separates the thinking text from the answer (check `--help` for the supported names) |
| `--api-key KEY` / env `VLLM_API_KEY` | Require a bearer token. Keep the key in a Secret, not in args visible in `ps` |
| `--enforce-eager` | Disables CUDA graphs: less memory and slower. Use it for debugging or very tight memory |
| `--dtype auto\|bfloat16\|float16` | `auto` follows the checkpoint; use float16 on GPUs without bf16 |

## Offline (air-gapped) loading
- Point at a local directory, not a hub id: `vllm serve /models/<name> --served-model-name <name>`.
- Set `HF_HUB_OFFLINE=1` and `TRANSFORMERS_OFFLINE=1` so nothing tries to reach the internet. A startup that hangs or fails on DNS resolution usually means one of these is missing, or the tokenizer files are not in the model directory.
- The model directory must contain `config.json`, the tokenizer files (`tokenizer.json` / `tokenizer_config.json`, plus a chat template inside the config or a separate `--chat-template` file), and the weight shards (`*.safetensors` with the index).

## OpenShift AI / KServe
- A `ServingRuntime` (vLLM image + args) and an `InferenceService` that references it. The model comes from a PVC (`storageUri: pvc://<pvc>/<path>`) or an internal object store or OCI registry.
- Put vLLM flags in the ServingRuntime container `args` (or the InferenceService `predictor.model.args` if your version supports overriding them). Set GPU `limits` (`nvidia.com/gpu: N`), matching `--tensor-parallel-size N`.
- Shared memory: tensor parallelism needs a large `/dev/shm`. Mount an `emptyDir` with `medium: Memory` at `/dev/shm`, or NCCL errors appear.
- Diagnose with `oc get inferenceservice`, `oc describe inferenceservice <n>`, and the predictor pod logs. See the `openshift-diagnostics` skill for pod-level failures.

## Reading failures
| Symptom | Likely cause → fix |
|---|---|
| `CUDA out of memory` at startup / `No available memory for the cache blocks` | Weights + KV do not fit → lower `--max-model-len` or `--max-num-seqs`, use `--kv-cache-dtype fp8`, a quantized checkpoint, or more TP |
| "The model's max seq len (X) is larger than the maximum number of tokens that can be stored in KV cache (Y)" | Set `--max-model-len` ≤ Y, or free memory as above |
| `Total number of attention heads must be divisible by tensor parallel size` | Pick a TP that divides the head count |
| NCCL errors / hang with TP > 1 | `/dev/shm` too small, GPUs not visible (`CUDA_VISIBLE_DEVICES`), P2P issues → check shm, `nvidia-smi topo -m` |
| Tool calls returned as text | Missing `--enable-auto-tool-choice` / wrong `--tool-call-parser`, or a chat template without tool support |
| First token slow under load | Long prefills: enable prefix caching, tune `--max-num-batched-tokens`, check `vllm:num_requests_waiting` |
| Throughput low, GPUs idle | `--max-num-seqs` too low, client concurrency too low, CPU tokenization bottleneck |

## Metrics (`/metrics`, Prometheus format)
Watch `vllm:num_requests_running`, `vllm:num_requests_waiting`, the KV cache usage gauge (`vllm:gpu_cache_usage_perc` in older versions; check `/metrics` for the current name), `vllm:time_to_first_token_seconds`, `vllm:e2e_request_latency_seconds`, and the preemption counters. Waiting > 0 together with KV usage near 100% means you are memory-bound. Lower the context or add capacity.
