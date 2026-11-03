# vLLM GPU memory sizing

All numbers are estimates. Confirm by starting vLLM and reading its log line that reports the KV cache size, the number of GPU blocks, and the maximum concurrency for the configured `max_model_len`.

## 1. Weights
`weight_bytes ≈ parameters × bytes_per_param`

| Precision | bytes/param |
|---|---|
| fp32 | 4 |
| bf16 / fp16 | 2 |
| fp8 / int8 | 1 |
| int4 (AWQ/GPTQ) | ~0.5, plus about 5–10% for scales and zero points |

With tensor parallelism of N, each GPU holds about `weight_bytes / N`.
For MoE models, all experts must be resident: count **total** parameters, not active ones.

## 2. KV cache per token
`kv_bytes_per_token = 2 × num_layers × num_kv_heads × head_dim × kv_bytes`

- 2 = key and value.
- `num_kv_heads` = `num_key_value_heads` from config.json (GQA models have far fewer KV heads than attention heads).
- `head_dim` = `head_dim` from config.json, or `hidden_size / num_attention_heads`.
- `kv_bytes` = 2 for fp16/bf16, 1 for `--kv-cache-dtype fp8`.
- With TP of N, each GPU holds `num_kv_heads / N` heads (at least 1 if kv_heads < N, in which case heads are replicated).

Worked example (an 8B GQA model: 32 layers, 8 KV heads, head_dim 128, fp16 KV):
`2 × 32 × 8 × 128 × 2 = 131,072 bytes ≈ 128 KiB per token`
- one 8K-token sequence ≈ 1 GiB
- 32 concurrent sequences × 8K ≈ 32 GiB (if all are full; vLLM pages blocks, so the real use is for tokens actually present)

## 3. Budget per GPU
`usable = VRAM × gpu_memory_utilization`
`kv_budget = usable − weights_per_gpu − activation/overhead (reserve ~1–3 GiB; more with large max-num-batched-tokens or without --enforce-eager for CUDA graphs)`
`max_tokens_in_cache ≈ kv_budget / kv_bytes_per_token_per_gpu`

Then choose:
- `max_model_len` ≤ `max_tokens_in_cache` (vLLM refuses to start otherwise)
- expected concurrency ≈ `max_tokens_in_cache / typical_tokens_per_request`

## 4. Levers, from cheapest to most expensive
1. Lower `--max-model-len` to what requests really need. Agent and coding workloads may need 32K–128K; plain chat often needs less.
2. `--kv-cache-dtype fp8` (½ KV).
3. A quantized checkpoint (fp8, AWQ/GPTQ int4) for the weights.
4. Raise `--gpu-memory-utilization`, only if nothing else uses the GPU.
5. More tensor parallelism (splits weights and KV across GPUs; needs head-count divisibility and fast interconnect).
6. Several small specialized models instead of one large one, each sized to its own context and traffic (route by task).

## 5. Agent clients (for example MATE Builder) specifics
- Long system prompts and tool schemas repeat on every request, so prefix caching is a major saving.
- Agents send `tools`: the server needs `--enable-auto-tool-choice --tool-call-parser <parser>`.
- The client reads `max_model_len` from `/v1/models` to plan context compaction. Set it to the real usable value, not the model's theoretical maximum.
