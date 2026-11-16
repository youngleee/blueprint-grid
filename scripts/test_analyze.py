#!/usr/bin/env python3
"""Checks for scripts/analyze.py.  Run: python3 scripts/test_analyze.py"""
import csv
import json
import os
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(__file__))
import analyze  # noqa: E402

HEADER = ("condition,tier,seed,success,end_reason,decisions,ticks,invalid_actions,parse_errors,llm_calls,cache_hits,"
          "tokens_in,tokens_out,action_set_size,library_size,skill_invocations,skill_successes,skill_effect_fail,"
          "skill_pre_fail,induce_calls,skills_induced,skills_rejected,induce_tokens_in,induce_tokens_out").split(",")


def write_run(root, name, condition, successes, decisions=None, freeze=None, max_decisions=40):
    d = os.path.join(root, name)
    os.makedirs(os.path.join(d, "traces"))
    with open(os.path.join(d, "run.json"), "w") as f:
        json.dump({"condition": condition, "tier": "tier3", "provider": "openai", "model": "m",
                   "max_decisions": max_decisions, "freeze": freeze, "summary": {}}, f)
    with open(os.path.join(d, "metrics.csv"), "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=HEADER)
        w.writeheader()
        for i, ok in enumerate(successes):
            row = {k: "0" for k in HEADER}
            row.update(condition=condition, tier="tier3", seed=str(1001 + i), success="1" if ok else "0",
                       end_reason="exit" if ok else "caught by guard_1",
                       decisions=str(decisions[i] if decisions else 10), llm_calls="10", tokens_in="7000")
            w.writerow(row)


class Stats(unittest.TestCase):
    def test_wilson_matches_known_values(self):
        lo, hi = analyze.wilson(8, 10)
        self.assertAlmostEqual(lo, 0.4902, places=3)
        self.assertAlmostEqual(hi, 0.9433, places=3)
        self.assertEqual(analyze.wilson(0, 0), (0.0, 0.0))

    def test_mcnemar_exact(self):
        # 7 vs 23 discordant pairs: same values as the week-6 R vs RD comparison (p = 0.0052).
        self.assertAlmostEqual(analyze.mcnemar_exact(7, 23), 0.0052, places=4)
        self.assertEqual(analyze.mcnemar_exact(0, 0), 1.0)
        self.assertAlmostEqual(analyze.mcnemar_exact(5, 5), 1.0)


class Report(unittest.TestCase):
    def test_paired_comparison_and_calibration(self):
        with tempfile.TemporaryDirectory() as root:
            write_run(root, "test_A", "A", [True] * 2 + [False] * 8)
            write_run(root, "test_C_frozen", "C", [True] * 9 + [False], freeze=True)
            write_run(root, "test_R", "R", [True] * 8 + [False] * 2)
            write_run(root, "cal_A", "A", [True] * 6 + [False] * 4, decisions=[5, 8, 12, 20, 30, 50, 80, 80, 80, 80],
                      max_decisions=80)
            out = os.path.join(root, "results.md")
            subprocess.run([sys.executable, os.path.join(os.path.dirname(__file__), "analyze.py"), root, "--out", out],
                           check=True, capture_output=True)
            text = open(out).read()
            self.assertIn("| test_C_frozen | test_A | 10 | 7 | 0 |", text)
            self.assertIn("9/10", text)
            # Successes used 5, 8, 12, 20, 30, 50 decisions: 4/10 = 40 % from d=20, 50 % from d=30.
            self.assertIn("**D_MAX = 29**", text)
            self.assertNotIn("Stub provider", text)


if __name__ == "__main__":
    unittest.main()
