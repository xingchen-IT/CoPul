"""补测：DWM 侧的合成成本（毛玻璃 / 投影的真实开销所在）。

`--stats` 量的是 CoPul 进程自身的 CPU，而 accent acrylic 的模糊是 DWM 在合成阶段
做的 —— 这部分完全不计入 CoPul。只报 CoPul 的数字会把毛玻璃说成"免费"。

做法：外部起 CoPul，同时用 psutil 采 CoPul + dwm.exe 的 CPU 时间，按墙钟换算单核。
条件交替（hidden → float → float-noshadow → hidden ...），环境噪声靠配对消掉。

用法：python dwm_side.py [每条件秒数]
"""

import os
import subprocess
import sys
import time

import psutil

RUN_DIR = r"D:\CoPulRun"
OUT_DIR = r"D:\CoPul开发\tools\m15"
DOTNET = os.path.expandvars(r"%USERPROFILE%\.workbuddy\binaries\dotnet-sdk\8.0.404\dotnet.exe")

WARMUP = 10
SECONDS = int(sys.argv[1]) if len(sys.argv) > 1 else 30

CONDITIONS = {
    "hidden": ["--hidden"],
    "main": [],
    "float": ["--float", "--glass-level", "2"],
    "float-noshadow": ["--float", "--glass-level", "2", "--no-shadow"],
}

SEQUENCE = ["hidden", "float", "float-noshadow", "main",
            "main", "float-noshadow", "float", "hidden"]


def cpu_snapshot(pids):
    out = {}
    for pid in pids:
        try:
            p = psutil.Process(pid)
            t = p.cpu_times()
            out[pid] = t.user + t.system
        except Exception:
            out[pid] = None
    return out


def run_one(name, extra):
    """起一次 CoPul，跑满 warmup+seconds，采 CoPul 与 dwm 的 CPU 时间差。

    这里不带 `--stats`：那个参数会让进程自己在计时结束时退出，而我们要在同一段
    墙钟里同时量 dwm.exe —— 由外部掐表、结束后 terminate 更直接。
    """
    args = [DOTNET, "CoPul.dll"] + extra

    p = subprocess.Popen(args, cwd=RUN_DIR,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        time.sleep(WARMUP)
        if p.poll() is not None:
            return None

        pids = [p.pid]
        dwm_ids = [d.pid for d in psutil.process_iter(['name'])
                   if d.info['name'] == 'dwm.exe']

        a = cpu_snapshot(pids + dwm_ids)
        t0 = time.time()
        time.sleep(SECONDS)
        b = cpu_snapshot(pids + dwm_ids)
        dt = time.time() - t0

        res = {"wall": dt}
        # CoPul 自身
        if a[p.pid] is not None and b[p.pid] is not None:
            res["copul"] = (b[p.pid] - a[p.pid]) / dt * 100.0
        # 各 dwm 实例（通常是 1~2 个）
        dwm_vals = []
        for pid in dwm_ids:
            if a.get(pid) is not None and b.get(pid) is not None:
                dwm_vals.append((b[pid] - a[pid]) / dt * 100.0)
        res["dwm"] = sum(dwm_vals) if dwm_vals else None
        res["dwm_n"] = len(dwm_vals)
        return res
    finally:
        try:
            p.terminate()
            p.wait(timeout=10)
        except Exception:
            try:
                p.kill()
            except Exception:
                pass
        time.sleep(1.5)


def main():
    print(f"每条件 预热{WARMUP}s + 测量{SECONDS}s，共 {len(SEQUENCE)} 次\n", flush=True)
    results = {}
    for i, name in enumerate(SEQUENCE, 1):
        print(f"[{time.strftime('%H:%M:%S')}] {i}/{len(SEQUENCE)} {name} ...", flush=True)
        r = run_one(name, CONDITIONS[name])
        if r is None:
            print("    启动失败，跳过", flush=True)
            continue
        print(f"    CoPul={r.get('copul') or 0:.2f}%  dwm={r.get('dwm') or 0:.2f}%"
              f"（{r['dwm_n']} 个实例）  wall={r['wall']:.0f}s", flush=True)
        results.setdefault(name, []).append(r)

    print("\n===== 汇总（中位数）=====")
    med = {}
    for name, runs in results.items():
        cs = sorted(r.get("copul") or 0 for r in runs)
        ds = sorted(r.get("dwm") or 0 for r in runs)
        med[name] = (cs[len(cs) // 2], ds[len(ds) // 2])
        print(f"{name:16s} n={len(cs)}  CoPul {med[name][0]:.3f}%  dwm {med[name][1]:.3f}%  "
              f"合计 {med[name][0] + med[name][1]:.3f}%")

    print("\n===== 相对 hidden 的增量 =====")
    if "hidden" in med:
        b = med["hidden"]
        for name in ("main", "float", "float-noshadow"):
            if name in med:
                m = med[name]
                print(f"{name:16s} CoPul {m[0] - b[0]:+.3f} pp   dwm {m[1] - b[1]:+.3f} pp   "
                      f"合计 {m[0] + m[1] - b[0] - b[1]:+.3f} pp")

    with open(os.path.join(OUT_DIR, "dwm-side.txt"), "w", encoding="utf-8") as f:
        f.write(f"测量时间 {time.strftime('%Y-%m-%d %H:%M:%S')}\n")
        f.write(f"每条件 预热{WARMUP}s + 测量{SECONDS}s\n\n")
        for name, runs in results.items():
            f.write(f"== {name} ==\n")
            for r in runs:
                f.write(f"  CoPul={r.get('copul')}%  dwm={r.get('dwm')}%  n={r['dwm_n']}\n")
            f.write("\n")
    print("\n原始结果 ->", os.path.join(OUT_DIR, "dwm-side.txt"))


if __name__ == "__main__":
    main()
