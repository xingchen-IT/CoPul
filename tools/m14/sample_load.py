import csv, time, psutil

OUT = r"D:\CoPul开发\tools\m14\load.csv"
DUR = 270.0
STEP = 3.0

psutil.cpu_percent(interval=None)
prev = {}
for p in psutil.process_iter(['pid']):
    try:
        t = p.cpu_times()
        prev[p.pid] = t.user + t.system
    except Exception:
        pass
prev_t = time.time()

rows = []
end = prev_t + DUR
while time.time() < end:
    time.sleep(STEP)
    now = time.time()
    dt = now - prev_t
    cur = {}
    for p in psutil.process_iter(['pid', 'name']):
        try:
            t = p.cpu_times()
            cur[p.pid] = (p.info['name'], t.user + t.system)
        except Exception:
            pass
    busy = []
    for pid, (name, c) in cur.items():
        if pid in prev and pid != 0:
            d = c - prev[pid]
            if d > 0:
                busy.append((d / dt * 100.0, name))
    busy.sort(reverse=True)
    whole = psutil.cpu_percent(interval=None)
    top = "; ".join(f"{n}={c:.0f}%" for c, n in busy[:4])
    rows.append([time.strftime('%H:%M:%S'), f"{whole:.1f}", f"{sum(c for c, _ in busy):.0f}", top])
    prev = {pid: v[1] for pid, v in cur.items()}
    prev_t = now

with open(OUT, 'w', newline='', encoding='utf-8') as f:
    w = csv.writer(f)
    w.writerow(['time', 'machine_pct', 'sum_singlecore_pct', 'top4'])
    w.writerows(rows)
print("samples", len(rows), "->", OUT)
