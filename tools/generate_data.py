# -*- coding: utf-8 -*-
"""Generate bundled A-share symbol DB and Chinese market-day calendar.

Run from anywhere: `python tools/generate_data.py`.
Writes into src/AStockWidget/Data/.
"""
import csv
import io
import json
from pathlib import Path

import requests

ROOT = Path(__file__).resolve().parent.parent
DATA_DIR = ROOT / "src" / "AStockWidget" / "Data"
DATA_DIR.mkdir(parents=True, exist_ok=True)

base = "https://raw.githubusercontent.com/irachex/open-stock-data/main/symbols/"

# ---- 1. A-share symbols ----
stocks = []
for exch, market in [("SSE.csv", "SH"), ("SZSE.csv", "SZ"), ("BSE.csv", "BJ")]:
    r = requests.get(base + exch, timeout=30)
    r.raise_for_status()
    # Strip UTF-8 BOM
    text = r.text.lstrip("\ufeff")
    reader = csv.DictReader(io.StringIO(text))
    for row in reader:
        code = row["code"].strip()
        name = row["name"].strip()
        typ = row["type"].strip()
        # Keep common stock only; skip indexes/funds/bonds if any appear.
        if typ != "stock":
            continue
        if not code or not name:
            continue
        stocks.append(
            {
                "code": code,
                "name": name,
                "market": market,
                "exchange": row["exchange"].strip(),
            }
        )

# Deduplicate by code (prefer first market occurrence)
seen = set()
unique = []
for s in stocks:
    if s["code"] not in seen:
        seen.add(s["code"])
        unique.append(s)

unique.sort(key=lambda s: s["code"])
print("A-share symbols:", len(unique))

out_stocks = DATA_DIR / "ashare_stocks.json"
with open(out_stocks, "w", encoding="utf-8") as f:
    json.dump(unique, f, ensure_ascii=False, separators=(",", ":"))
print("Wrote", out_stocks)

# ---- 2. Market days (holiday + make-up workdays) ----
holiday_urls = [
    "https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/2024.json",
    "https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/2025.json",
    "https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/2026.json",
]
# day -> isOffDay
calendar = {}
for u in holiday_urls:
    r = requests.get(u, timeout=30)
    r.raise_for_status()
    data = r.json()
    for d in data.get("days", []):
        calendar[d["date"]] = bool(d.get("isOffDay", True))

out_cal = DATA_DIR / "market_days.json"
with open(out_cal, "w", encoding="utf-8") as f:
    json.dump(calendar, f, ensure_ascii=False, separators=(",", ":"))
print("Calendar days:", len(calendar), "->", out_cal)