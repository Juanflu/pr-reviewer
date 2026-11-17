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

HERE = os.path.dirname(os.path.abspath(__file__))
# Primero el CSV que genera export/ExportPullRequests.cs en la raíz del repo; si no, el de Descargas.
CANDIDATES = [os.path.join(HERE, "PullRequests_Metrics.csv"),
              os.path.expanduser("~/Downloads/PullRequests_Metrics.csv")]
OUT = os.path.join(HERE, "data.js")


def to_float(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def to_int(value):
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


def main():
    src = sys.argv[1] if len(sys.argv) > 1 else next((c for c in CANDIDATES if os.path.isfile(c)), CANDIDATES[-1])
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
                # Columnas opcionales (tamaño de la PR); null si el CSV no las trae
                "commits": to_int(r.get("CommitCount")),
                "files": to_int(r.get("FilesChanged")),
                "added": to_int(r.get("LinesAdded")),
                "deleted": to_int(r.get("LinesDeleted")),
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

    with_size = sum(1 for r in rows if r["added"] is not None)
    print(f"{len(rows)} PRs escritas en {OUT} (leído {src}; {with_size} con tamaño)")


if __name__ == "__main__":
    main()
