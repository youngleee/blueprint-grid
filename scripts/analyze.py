#!/usr/bin/env python3
"""Turn experiment run directories into the report tables (docs/PLAN.md §9.4-§9.6).

    python3 scripts/analyze.py runs/protocol [--out runs/protocol/results.md]
    python3 scripts/analyze.py runs/a runs/b ...            (any run directories)

Reads every directory that contains metrics.csv and run.json. Standard library only.
Writes Markdown: calibration table and D_MAX recommendation, held-out success with Wilson
95 % intervals, paired exact McNemar tests (same seeds), cost, library growth during training,
skill use, induction acceptance, and variance across repeats.
"""
import argparse
import csv
import json
import math
import os
import sys
from collections import defaultdict


def load_run(path):
    with open(os.path.join(path, "run.json")) as f:
        info = json.load(f)
    with open(os.path.join(path, "metrics.csv")) as f:
        rows = list(csv.DictReader(f))
    library = None
    lib_path = os.path.join(path, "library.json")
    if os.path.exists(lib_path):
        with open(lib_path) as f:
            library = json.load(f)
    induction = []
    ind_path = os.path.join(path, "induction.jsonl")
    if os.path.exists(ind_path):
        with open(ind_path) as f:
            induction = [json.loads(line) for line in f if line.strip()]
    return {"name": os.path.basename(os.path.normpath(path)), "path": path, "info": info, "rows": rows,
            "library": library, "induction": induction}


def find_runs(paths):
    runs = []
    for p in paths:
        if os.path.exists(os.path.join(p, "metrics.csv")):
            runs.append(load_run(p))
            continue
        for entry in sorted(os.listdir(p)):
            if ".failed-" in entry:  # earlier attempts kept by run_protocol.sh, not results
                continue
            d = os.path.join(p, entry)
            if os.path.isdir(d) and os.path.exists(os.path.join(d, "metrics.csv")) and os.path.exists(os.path.join(d, "run.json")):
                runs.append(load_run(d))
    return runs


def wilson(k, n, z=1.96):
    if n == 0:
        return (0.0, 0.0)
    p = k / n
    denom = 1 + z * z / n
    centre = (p + z * z / (2 * n)) / denom
    half = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / denom
    return (max(0.0, centre - half), min(1.0, centre + half))


def mcnemar_exact(only_a, only_b):
    """Two-sided exact McNemar p-value (binomial test on discordant pairs)."""
    n = only_a + only_b
    if n == 0:
        return 1.0
    k = min(only_a, only_b)
    tail = sum(math.comb(n, i) for i in range(k + 1)) / 2 ** n
    return min(1.0, 2 * tail)


def success_by_seed(run):
    return {r["seed"]: r["success"] == "1" for r in run["rows"]}


def num(rows, key):
    return [float(r[key]) for r in rows if r.get(key, "") != ""]


def mean(xs):
    return sum(xs) / len(xs) if xs else 0.0


def pct(x):
    return f"{100 * x:.1f} %"


def fmt_p(p):
    return "< 0.0001" if p < 0.0001 else f"{p:.4f}"


def summary_row(run):
    rows = run["rows"]
    n = len(rows)
    k = sum(r["success"] == "1" for r in rows)
    lo, hi = wilson(k, n)
    return {
        "n": n, "k": k, "rate": k / n if n else 0, "lo": lo, "hi": hi,
        "decisions": mean(num(rows, "decisions")),
        "ticks": mean(num(rows, "ticks")),
        "llm": mean(num(rows, "llm_calls")),
        "tok_in": sum(num(rows, "tokens_in")) + sum(num(rows, "induce_tokens_in")),
        "tok_out": sum(num(rows, "tokens_out")) + sum(num(rows, "induce_tokens_out")),
        "invalid": mean(num(rows, "invalid_actions")),
        "skill_calls": mean(num(rows, "skill_invocations")),
        "skill_ok": sum(num(rows, "skill_successes")),
        "skill_total": sum(num(rows, "skill_invocations")),
        "induce": sum(num(rows, "induce_calls")),
        "induced": sum(num(rows, "skills_induced")),
        "library": int(max(num(rows, "library_size"), default=0)),
    }


def label(run):
    i = run["info"]
    bits = [i.get("condition", "?")]
    if i.get("freeze"):
        bits.append("frozen")
    if i.get("admission") == "valid":
        bits.append("valid-admission")
    return " ".join(bits)


def end_reasons(run):
    c = defaultdict(int)
    for r in run["rows"]:
        if r["success"] != "1":
            reason = r["end_reason"]
            c["caught" if reason.startswith("caught") else reason] += 1
    return ", ".join(f"{v} {k}" for k, v in sorted(c.items(), key=lambda kv: -kv[1])) or "—"


def table(headers, rows):
    out = ["| " + " | ".join(headers) + " |", "|" + "|".join("---" for _ in headers) + "|"]
    out += ["| " + " | ".join(str(c) for c in r) + " |" for r in rows]
    return "\n".join(out)


def section_calibration(runs):
    cal = [r for r in runs if r["name"].startswith("cal_")]
    if not cal:
        return None
    lines = ["## Calibration (choose D_MAX)", "",
             "Success if the episode had been cut off after d decisions (computed from the decisions each "
             "successful episode actually used, so one long run answers every budget)."]
    budgets = [10, 15, 20, 25, 30, 40, 50, 60, 80]
    rows = []
    for r in cal:
        used = [int(x["decisions"]) for x in r["rows"] if x["success"] == "1"]
        n = len(r["rows"])
        rows.append([label(r)] + [pct(sum(u <= d for u in used) / n) if n else "—" for d in budgets])
    lines += ["", table(["condition"] + [f"d={d}" for d in budgets], rows)]
    a = next((r for r in cal if r["info"].get("condition") == "A"), None)
    if a and a["rows"]:
        used = sorted(int(x["decisions"]) for x in a["rows"] if x["success"] == "1")
        n = len(a["rows"])
        cap = int(a["info"].get("max_decisions", 0))
        final = len(used) / n
        if final <= 0.40:
            lines += ["", f"A succeeds on {pct(final)} even with {cap} decisions, so every budget up to {cap} satisfies "
                          "the ≤ 40 % rule (PLAN §9.3). Pick D_MAX from where R and RD level off in the table, "
                          "and report that A is weak at any budget."]
        else:
            # Largest budget d with success(A, d) <= 40 %.
            d_max = max(d for d in range(1, cap + 1) if sum(u <= d for u in used) / n <= 0.40)
            lines += ["", f"Largest budget at which A succeeds on ≤ 40 % (PLAN §9.3): **D_MAX = {d_max}**."]
    return "\n".join(lines)


def section_heldout(runs):
    test = [r for r in runs if r["name"].startswith("test_")]
    if not test:
        return None
    lines = ["## Held-out results (tier 3)", ""]
    rows = []
    for r in test:
        s = summary_row(r)
        rows.append([r["name"], label(r), f"{s['k']}/{s['n']}", pct(s["rate"]), f"{pct(s['lo'])} – {pct(s['hi'])}",
                     f"{s['decisions']:.1f}", f"{s['llm']:.1f}", int(s["induce"]), s["library"], end_reasons(r)])
    lines.append(table(["run", "condition", "success", "rate", "Wilson 95 %", "decisions", "LLM calls/ep",
                        "induce calls", "library", "failures"], rows))

    by_name = {r["name"]: r for r in test}
    pairs = [("test_C_frozen", "test_A"), ("test_C_frozen", "test_R"), ("test_C_frozen", "test_E_frozen"),
             ("test_C_frozen", "test_B"), ("test_C_induce", "test_A"), ("test_C_valid_frozen", "test_E_valid_frozen"),
             ("test_RD", "test_R"), ("test_D", "test_A"), ("test_C_frozen", "test_D")]
    prow = []
    for a, b in pairs:
        if a in by_name and b in by_name:
            sa, sb = success_by_seed(by_name[a]), success_by_seed(by_name[b])
            seeds = sorted(set(sa) & set(sb), key=int)
            only_a = sum(sa[x] and not sb[x] for x in seeds)
            only_b = sum(sb[x] and not sa[x] for x in seeds)
            prow.append([a, b, len(seeds), only_a, only_b, fmt_p(mcnemar_exact(only_a, only_b))])
    if prow:
        lines += ["", "Paired comparison on the same seeds (exact McNemar, two-sided):", "",
                  table(["A", "B", "paired seeds", "only A wins", "only B wins", "p"], prow)]
        lines += ["", "Hypotheses (PLAN §9.7): H1 C_frozen vs A · H2 C_frozen vs E_frozen (and the valid-admission pair) · "
                      "H4 C_frozen vs R. H3 is in the training section."]
    return "\n".join(lines)


def section_training(runs):
    train = sorted([r for r in runs if r["name"].startswith("train_")], key=lambda r: r["name"])
    if not train:
        return None
    lines = ["## Training (curriculum tier 1 → 2 → 3)", ""]
    rows = []
    for r in train:
        s = summary_row(r)
        ind = r["induction"]
        attempts = [a for req in ind for a in req["attempts"]]
        accepted = sum(a["accepted"] for a in attempts)
        rows.append([r["name"], label(r), r["info"].get("tier"), f"{s['k']}/{s['n']}", pct(s["rate"]),
                     f"{s['decisions']:.1f}", int(s["induce"]), f"{accepted}/{len(attempts)}", s["library"]])
    lines.append(table(["run", "condition", "tier", "success", "rate", "decisions", "induce calls",
                        "attempts accepted", "library at end"], rows))

    lines += ["", "LLM calls per episode over training (H3: should fall in C as the library grows). "
                  "Mean per block of 5 episodes:", ""]
    for prefix in sorted({r["name"].rsplit("_t", 1)[0] for r in train}):
        seq = []
        for r in sorted([r for r in train if r["name"].startswith(prefix + "_t")], key=lambda r: r["name"]):
            seq += [float(x["llm_calls"]) + float(x.get("induce_calls", 0) or 0) for x in r["rows"]]
        blocks = [mean(seq[i:i + 5]) for i in range(0, len(seq), 5)]
        lines.append(f"- `{prefix}`: " + " → ".join(f"{b:.1f}" for b in blocks))

    lines += ["", "Final libraries:"]
    for r in train:
        if r["name"].endswith("_t3") and r["library"] is not None:
            names = ", ".join(f"`{s['name']}({', '.join(p['name'] + ': ' + p['type'] for p in s['params'])})`" for s in r["library"]) or "empty"
            lines.append(f"- `{r['name']}`: {names}")
    return "\n".join(lines)


def section_skills(runs):
    rows = []
    for r in runs:
        uses = defaultdict(lambda: [0, 0])
        for seed_file in os.listdir(os.path.join(r["path"], "traces")) if os.path.isdir(os.path.join(r["path"], "traces")) else []:
            with open(os.path.join(r["path"], "traces", seed_file)) as f:
                for line in f:
                    t = json.loads(line)
                    if t.get("kind") == "skill":
                        uses[t["action"]][0] += 1
                        uses[t["action"]][1] += t.get("status") == "success"
        for name, (n, ok) in sorted(uses.items()):
            rows.append([r["name"], name, n, f"{ok}/{n}"])
    if not rows:
        return None
    return "## Skill use\n\n" + table(["run", "skill", "calls", "succeeded"], rows)


def section_variance(runs):
    var = [r for r in runs if r["name"].startswith("var_")]
    if not var:
        return None
    rates = [summary_row(r)["rate"] for r in var]
    lines = ["## Live variance", "",
             table(["run", "success"], [[r["name"], pct(summary_row(r)["rate"])] for r in var]), "",
             f"Mean {pct(mean(rates))}, range {pct(min(rates))} – {pct(max(rates))} over {len(var)} repeats."]
    return "\n".join(lines)


def section_cost(runs):
    rows = []
    total_in = total_out = 0
    for r in runs:
        s = summary_row(r)
        if s["tok_in"] == 0:
            continue
        cached = sum(num(r["rows"], "cache_hits"))
        calls = sum(num(r["rows"], "llm_calls"))
        rows.append([r["name"], r["info"].get("provider"), r["info"].get("model"), int(calls), int(cached),
                     int(s["tok_in"]), int(s["tok_out"])])
        total_in += s["tok_in"]
        total_out += s["tok_out"]
    if not rows:
        return None
    return ("## Tokens\n\nTokens as reported per call (cached calls repeat the recorded counts and cost nothing).\n\n"
            + table(["run", "provider", "model", "decision calls", "cached", "tokens in", "tokens out"], rows)
            + f"\n\nTotal: {int(total_in)} in, {int(total_out)} out.")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("paths", nargs="+")
    ap.add_argument("--out")
    args = ap.parse_args()

    runs = find_runs(args.paths)
    if not runs:
        print("no run directories found", file=sys.stderr)
        sys.exit(1)
    aborted = [r["name"] for r in runs if (r["info"].get("summary") or {}).get("aborted_reason")]
    stub = any(r["info"].get("provider") == "stub" for r in runs)

    parts = ["# Experiment results", ""]
    if stub:
        parts += ["> **Stub provider:** at least one run used the offline stub. Its numbers test the pipeline, "
                  "they are not research results.", ""]
    if aborted:
        parts += [f"> **Aborted runs (incomplete):** {', '.join(aborted)}", ""]
    for section in (section_calibration, section_training, section_heldout, section_skills, section_variance, section_cost):
        text = section(runs)
        if text:
            parts += [text, ""]
    if len(parts) <= 4:
        parts += ["## Runs", "", table(["run", "condition", "tier", "success"],
                                       [[r["name"], label(r), r["info"].get("tier"),
                                         f"{summary_row(r)['k']}/{summary_row(r)['n']}"] for r in runs])]
    report = "\n".join(parts).rstrip() + "\n"
    if args.out:
        with open(args.out, "w") as f:
            f.write(report)
        print(f"wrote {args.out}")
    else:
        print(report)


if __name__ == "__main__":
    main()
