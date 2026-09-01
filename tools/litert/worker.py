import argparse
import json
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


def main():
    args = parse_args()
    litert_lm.set_min_log_severity(litert_lm.LogSeverity.SILENT)
    with litert_lm.Engine(
        args.model,
        backend=backend_for(args.backend),
        max_num_tokens=args.max_num_tokens,
    ) as engine:
        for line in sys.stdin:
            started = time.perf_counter()
            try:
                request = json.loads(line)
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
                    response = conversation.send_message(request["prompt"])
                result = {
                    "ok": True,
                    "text": response_text(response),
                    "elapsedMilliseconds": (time.perf_counter() - started) * 1000,
                }
            except Exception as error:
                result = {
                    "ok": False,
                    "error": f"{type(error).__name__}: {error}",
                    "elapsedMilliseconds": (time.perf_counter() - started) * 1000,
                }

            print(json.dumps(result, separators=(",", ":")), flush=True)


if __name__ == "__main__":
    main()
