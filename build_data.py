#!/usr/bin/env python3
"""Convierte el CSV de métricas de PRs en data.js para index.html.

Uso:
    python3 build_data.py [ruta/al/PullRequests_Metrics.csv]

Solo usa la librería estándar de Python.
"""
import csv
import json
import os
import sys
from datetime import datetime

DEFAULT_CSV = os.path.expanduser("~/Downloads/PullRequests_Metrics.csv")
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data.js")


def to_float(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_CSV
    if not os.path.isfile(src):
        sys.exit(f"No se encuentra el CSV: {src}")

    rows = []
    with open(src, encoding="utf-8-sig", newline="") as f:
        for r in csv.DictReader(f):
            rows.append({
                "project": r.get("Project", "").strip(),
                "repo": r.get("Repository", "").strip(),
                "id": r.get("PullRequestId", "").strip(),
                "title": r.get("Title", "").strip(),
                "status": r.get("Status", "").strip(),
                "author": r.get("Author", "").strip(),
                "branch": r.get("TargetBranch", "").strip(),
                "created": r.get("CreationDate", "").strip() or None,
                "closed": r.get("ClosedDate", "").strip() or None,
                "hours": to_float(r.get("HoursOpen")),
                "url": r.get("Url", "").strip(),
            })

    payload = {
        "generatedAt": datetime.now().strftime("%Y-%m-%d %H:%M"),
        "source": os.path.basename(src),
        "rows": rows,
    }
    with open(OUT, "w", encoding="utf-8") as f:
        f.write("// Generado por build_data.py — no editar a mano.\n")
        f.write("window.PR_DATA = ")
        json.dump(payload, f, ensure_ascii=False, separators=(",", ":"))
        f.write(";\n")

    print(f"{len(rows)} PRs escritas en {OUT}")


if __name__ == "__main__":
    main()
