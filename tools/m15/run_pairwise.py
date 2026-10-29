"""M2 性能复测：配对交替测量（A-26 重启条件）。

为什么不用单次绝对测量：本轮机器上《异环》(HTGame.exe) 常驻吃 6 个核心、
整机 CPU 长期 60~98%，绝对数字没有意义（m14 那次整机 29.9% 都判了"未判定"）。
改用配对交替：同一时段内交替跑各条件，环境噪声对各条件近似相同，做差即可
得到"新增渲染面本身的代价"。条件是时间相邻的，漂移影响小。

条件：
  hidden        无窗口            —— 纯采集器基线
  main          大窗口            —— 对照 m14 的 2.603%
  float         悬浮窗（含投影）   —— 本轮新增的渲染面
  float-noshadow 悬浮窗（关投影）  —— 隔离 DWM 系统阴影的代价

序列反平衡：A B C D / D C B A / A B C D，每个条件 3 次取中位数。
"""

import os
import re
import subprocess
import sys
import threading
import time

RUN_DIR = r"D:\CoPulRun"
OUT_DIR = r"D:\CoPul开发\tools\m15"
DOTNET = os.path.expandvars(r"%USERPROFILE%\.workbuddy\binaries\dotnet-sdk\8.0.404\dotnet.exe")

SECONDS = 40
WARMUP = 8

CONDITIONS = {
    "hidden": ["--hidden"],
    "main": [],
    "float": ["--float", "--glass-level", "2"],
    "float-noshadow": ["--float", "--glass-level", "2", "--no-shadow"],
}

# 反平衡序列
ROUNDS = [
    ["hidden", "main", "float", "float-noshadow"],
    ["float-noshadow", "float", "main", "hidden"],
    ["hidden", "main", "float", "float-noshadow"],
]

_load = []          # (timestamp, machine_pct)
_stop = threading.Event()


def load_sampler():
    """每 2 秒记一次整机 CPU，用来标注每次测量的环境。"""
    import psutil  # noqa: F401  (pyinstaller 无关，本机有)
    psutil.cpu_percent(interval=None)
    while not _stop.is_set():
        time.sleep(2.0)
        _load.append((time.time(), psutil.cpu_percent(interval=None)))


def machine_load_between(t0, t1):
    vals = [v for (t, v) in _load if t0 <= t <= t1]
    if not vals:
        return None
    vals.sort()
    return vals[len(vals) // 2]


def parse(path):
    txt = open(path, encoding="utf-8", errors="replace").read()
    out = {}
    for key, pat in [
        ("single", r"single-core\s*:\s*([\d.]+)%"),
        ("machine", r"machine\s*:\s*([\d.]+)%"),
        ("window", r"window\s*:\s*(.+)"),
        ("frames", r"frames\s*:\s*(\d+)\s+平均 ApplySnapshot ([\d.]+) ms\s+平均 Layout ([\d.]+) ms"),
        ("tier", r"render tier\s*:\s*(\d)"),
        ("threads", r"threads\s*:\s*(\d+)"),
    ]:
        m = re.search(pat, txt)
        if m:
            out[key] = m.groups() if len(m.groups()) > 1 else m.group(1)
    return out


def run_one(name, extra, tag):
    out = os.path.join(OUT_DIR, f"{tag}-{name}.txt")
    if os.path.exists(out):
        os.remove(out)
    args = [DOTNET, "CoPul.dll", "--stats", out,
            "--seconds", str(SECONDS), "--warmup", str(WARMUP)] + extra
    t0 = time.time()
    p = subprocess.Popen(args, cwd=RUN_DIR,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    p.wait(timeout=SECONDS + WARMUP + 60)
    t1 = time.time()
    if not os.path.exists(out):
        print(f"  !! {name} 没有产出 {out}")
        return None
    d = parse(out)
    d["machine_env"] = machine_load_between(t0, t1)
    d["wall"] = t1 - t0
    return d


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    threading.Thread(target=load_sampler, daemon=True).start()
    time.sleep(2)

    results = {}
    for ri, seq in enumerate(ROUNDS, 1):
        for ci, name in enumerate(seq, 1):
            tag = f"r{ri}c{ci}"
            print(f"[{time.strftime('%H:%M:%S')}] 轮{ri} 第{ci}次 {name} ...", flush=True)
            d = run_one(name, CONDITIONS[name], tag)
            if d:
                print(f"    single={d.get('single')}%  整机中位={d.get('machine_env')}%  "
                      f"wall={d['wall']:.0f}s  {d.get('window','')}  frames={d.get('frames')}",
                      flush=True)
                results.setdefault(name, []).append(d)

    _stop.set()

    print("\n===== 汇总（每个条件取单核中位数）=====")
    med = {}
    for name, runs in results.items():
        vals = sorted(float(r["single"]) for r in runs)
        med[name] = vals[len(vals) // 2]
        env = [r.get("machine_env") for r in runs]
        print(f"{name:16s} n={len(vals)}  中位 {med[name]:.3f}%  "
              f"各次 {[f'{v:.3f}' for v in vals]}  整机中位 {env}")

    print("\n===== 差值（同一时段的配对结论）=====")
    if "hidden" in med:
        base = med["hidden"]
        for name in ("main", "float", "float-noshadow"):
            if name in med:
                print(f"{name:16s} - hidden = {med[name] - base:+.3f} pp 单核")
    if "float" in med and "float-noshadow" in med:
        print(f"投影的代价      float - float-noshadow = {med['float'] - med['float-noshadow']:+.3f} pp 单核")

    with open(os.path.join(OUT_DIR, "summary.txt"), "w", encoding="utf-8") as f:
        f.write(f"测量时间 {time.strftime('%Y-%m-%d %H:%M:%S')}\n")
        f.write(f"口径 {SECONDS}s 测量 + {WARMUP}s 预热，反平衡 3 轮\n\n")
        for name, runs in results.items():
            f.write(f"== {name} ==\n")
            for r in runs:
                f.write(f"  single={r.get('single')}%  machine_env={r.get('machine_env')}%  "
                        f"window={r.get('window')}  frames={r.get('frames')}\n")
            f.write("\n")
    print("\n原始文件在", OUT_DIR)


if __name__ == "__main__":
    main()
