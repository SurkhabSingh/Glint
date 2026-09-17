"""LiteRT-LM worker: loads the model once, then serves newline-delimited
JSON requests on stdin until stdin closes or a shutdown request arrives.

Protocol (one JSON object per line, both directions):
  ready notice   {"event":"ready","loadMs":...,"pid":...,"maxNumTokens":...}
  request        {"id":1,"op":"generate","prompt":"...","temperature":0,...}
                 op defaults to "generate"; "ping" and "shutdown" are also
                 accepted.
  response       {"id":1,"ok":true,"text":"...","elapsedMilliseconds":...}
                 {"id":1,"ok":false,"error":"...","elapsedMilliseconds":...}

The ready notice is the only line carrying "event"; clients skip such lines
when matching a response. Responses echo the request's "id" so a long-lived
client can pair them.

Note max_num_tokens is the whole budget, input plus output: values that are
too small make send_message fail outright rather than truncate.
"""

import argparse
import json
import os
import sys
import time

import litert_lm


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--backend", choices=("cpu", "gpu", "npu"), default="cpu")
    parser.add_argument("--max-num-tokens", type=int, default=2048)
    return parser.parse_args()


def backend_for(name):
    return {
        "cpu": litert_lm.interfaces.CPU,
        "gpu": litert_lm.interfaces.GPU,
        "npu": litert_lm.interfaces.NPU,
    }[name]()


def response_text(response):
    return "".join(
        item.get("text", "")
        for item in response.get("content", [])
        if item.get("type") == "text"
    )


def emit(payload):
    print(json.dumps(payload, separators=(",", ":")), flush=True)


def generate(engine, request):
    sampler = litert_lm.SamplerConfig(
        top_k=request.get("topK"),
        top_p=request.get("topP"),
        temperature=request.get("temperature"),
        seed=request.get("seed"),
    )
    with engine.create_conversation(
        sampler_config=sampler,
        system_message=request.get("systemPrompt"),
    ) as conversation:
        return conversation.send_message(request["prompt"])


def main():
    args = parse_args()
    litert_lm.set_min_log_severity(litert_lm.LogSeverity.SILENT)
    load_started = time.perf_counter()
    with litert_lm.Engine(
        args.model,
        backend=backend_for(args.backend),
        max_num_tokens=args.max_num_tokens,
    ) as engine:
        emit(
            {
                "event": "ready",
                "loadMs": (time.perf_counter() - load_started) * 1000,
                "pid": os.getpid(),
                "maxNumTokens": args.max_num_tokens,
            }
        )
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            started = time.perf_counter()
            request_id = None
            try:
                request = json.loads(line)
                request_id = request.get("id")
                op = request.get("op") or "generate"
                if op == "shutdown":
                    emit({"id": request_id, "ok": True, "op": "shutdown",
                          "elapsedMilliseconds": 0.0})
                    break
                if op == "ping":
                    emit({"id": request_id, "ok": True, "op": "ping",
                          "elapsedMilliseconds":
                              (time.perf_counter() - started) * 1000})
                    continue
                if op != "generate":
                    raise ValueError(f"Unknown op: {op}")
                response = generate(engine, request)
                emit(
                    {
                        "id": request_id,
                        "ok": True,
                        "text": response_text(response),
                        "elapsedMilliseconds": (time.perf_counter() - started) * 1000,
                    }
                )
            except Exception as error:  # noqa: BLE001 - reported to the client
                emit(
                    {
                        "id": request_id,
                        "ok": False,
                        "error": f"{type(error).__name__}: {error}",
                        "elapsedMilliseconds": (time.perf_counter() - started) * 1000,
                    }
                )


if __name__ == "__main__":
    main()
