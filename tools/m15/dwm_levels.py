"""验证 DWM 的增量是否由「模糊」造成：对比极透档（无模糊）与极实档（重模糊）。

M2 的浓度档位表里第 1 档是 BlurOnly（tint=0、不模糊），第 2–7 档才带模糊。
如果 dwm 的增量来自模糊，第 1 档应当接近 hidden；如果来自"窗口存在本身"
（DWM 给带 accent 的窗口单独建合成层），第 1 档也会明显高于 hidden。

序列 hidden / lv1 / lv7 / lv7 / lv1 / hidden —— 首尾各一次 hidden 用来看漂移。
"""

import os
import subprocess
import sys
import time

import psutil

RUN_DIR = r"D:\CoPulRun"
DOTNET = os.path.expandvars(r"%USERPROFILE%\.workbuddy\binaries\dotnet-sdk\8.0.404\dotnet.exe")

WARMUP = 10
SECONDS = int(sys.argv[1]) if len(sys.argv) > 1 else 22

CONDITIONS = {
    "hidden": ["--hidden"],
    "lv1-noBlur": ["--float", "--glass-level", "1"],
    "lv7-strongBlur": ["--float", "--glass-level", "7"],
}

SEQUENCE = ["hidden", "lv1-noBlur", "lv7-strongBlur", "lv7-strongBlur", "lv1-noBlur", "hidden"]


def cpu(pid):
    try:
        t = psutil.Process(pid).cpu_times()
        return t.user + t.system
    except Exception:
        return None


def run(name):
    p = subprocess.Popen([DOTNET, "CoPul.dll"] + CONDITIONS[name], cwd=RUN_DIR,
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        time.sleep(WARMUP)
        if p.poll() is not None:
            return None
        dwm = [d.pid for d in psutil.process_iter(['name']) if d.info['name'] == 'dwm.exe']
        a_c, a_d = cpu(p.pid), {i: cpu(i) for i in dwm}
        t0 = time.time()
        time.sleep(SECONDS)
        dt = time.time() - t0
        b_c, b_d = cpu(p.pid), {i: cpu(i) for i in dwm}
        return {
            "copul": (b_c - a_c) / dt * 100 if None not in (a_c, b_c) else None,
            "dwm": sum(b_d[i] - a_d[i] for i in dwm if a_d.get(i) is not None) / dt * 100,
        }
    finally:
        try:
            p.terminate(); p.wait(timeout=10)
        except Exception:
            try: p.kill()
            except Exception: pass
        time.sleep(1.5)


def main():
    print(f"每条件 预热{WARMUP}s + 测量{SECONDS}s\n", flush=True)
    res = {}
    for i, name in enumerate(SEQUENCE, 1):
        print(f"[{time.strftime('%H:%M:%S')}] {i}/{len(SEQUENCE)} {name} ...", flush=True)
        r = run(name)
        if r:
            print(f"    CoPul={r['copul']:.2f}%  dwm={r['dwm']:.2f}%", flush=True)
            res.setdefault(name, []).append(r)

    print("\n===== 汇总 =====")
    med = {}
    for k, v in res.items():
        c = sorted(x["copul"] for x in v); d = sorted(x["dwm"] for x in v)
        med[k] = (c[len(c)//2], d[len(d)//2])
        print(f"{k:16s} n={len(v)}  CoPul {med[k][0]:.2f}%  dwm {med[k][1]:.2f}%")
    if "hidden" in med:
        b = med["hidden"]
        print("\n相对 hidden：")
        for k in ("lv1-noBlur", "lv7-strongBlur"):
            if k in med:
                print(f"  {k:16s} CoPul {med[k][0]-b[0]:+.2f} pp   dwm {med[k][1]-b[1]:+.2f} pp")

    with open(r"D:\CoPul开发\tools\m15\dwm-levels.txt", "w", encoding="utf-8") as f:
        f.write(f"{time.strftime('%Y-%m-%d %H:%M:%S')} 预热{WARMUP}s 测量{SECONDS}s\n\n")
        for k, v in res.items():
            f.write(f"== {k} ==\n")
            for r in v:
                f.write(f"  CoPul={r['copul']:.2f}%  dwm={r['dwm']:.2f}%\n")
            f.write("\n")


if __name__ == "__main__":
    main()
