"""M4.42 验收：磨砂的着色是不是**跟着底色走**。

M4.41 的 tint 是内置的 #0F1216 / #F6F8FB（按字色深浅反向挑）。本轮改成直接用
`TaskbarBackdropColor`。判据只能是像素：

  同浓度、只换底色 → 条内均值必须跟着底色跑（浅色底色 = 条内更亮），
  而"条外任务栏"在同一次采样里不变 → 差值方向由我们控制，不是环境噪声。

另外验两件事：
  ① 浓度 0 时不同底色的条内均值应当**收敛**（tint alpha=0，只剩亚克力 17% 基底）；
  ② 诊断串里的 `ink=` 要跟着反转 —— 浅底色配深字，否则白字糊在浅磨砂上。

用法：python m442-tint.py          跑全部
      python m442-tint.py 1 2      只跑指定编号
"""
import json
import os
import re
import subprocess
import sys
import time

PROBE = r"D:\CoPulProbe"
CFG = os.path.expandvars(r"%APPDATA%\CoPul\config.json")
sys.path.insert(0, PROBE)
from detail import read_png, region_stats


def case(name, color, opacity, mode="transparent"):
    return (name, dict(TaskbarBackdrop=mode, TaskbarBackdropColor=color,
                       TaskbarBackdropOpacity=40, TaskbarGlass=True,
                       TaskbarGlassOpacity=opacity, TaskbarInk="Auto"))


CASES = [
    case("1-dark-60", "#1B2127", 60),
    case("2-light-60", "#D8DEE7", 60),
    case("3-brown-60", "#7A3F1F", 60),
    case("4-light-100", "#D8DEE7", 100),
    case("5-light-0", "#D8DEE7", 0),
    case("6-dark-0", "#1B2127", 0),
]


def load_cfg():
    with open(CFG, encoding="utf-8") as f:
        return json.load(f)


def save_cfg(d):
    with open(CFG, "w", encoding="utf-8") as f:
        json.dump(d, f, ensure_ascii=False, indent=2)


def run_case(name, overrides):
    d = load_cfg()
    d.update(overrides)
    save_cfg(d)

    png = os.path.join(PROBE, f"m442-{name}.png")
    for stale in (png, png + ".info.txt"):
        if os.path.exists(stale):
            os.remove(stale)

    p = subprocess.Popen([os.path.join(PROBE, "CoPul.exe"), "--tray", "--shot-screen", png],
                         cwd=PROBE)
    try:
        for _ in range(80):
            time.sleep(0.5)
            if os.path.exists(png) and os.path.exists(png + ".info.txt"):
                break
        time.sleep(0.6)
        if not os.path.exists(png):
            print(f"{name}: FAIL 没出图")
            return
        info = open(png + ".info.txt", encoding="utf-8", errors="replace").read()
        o = re.search(r"bar\s*:\s*(\d+)×(\d+) px at \((\d+),(\d+)\)", info)
        m = re.search(r"strip = \((\d+),(\d+)\)-\((\d+),(\d+)\)", info)
        if not (m and o):
            print(f"{name}: FAIL 没解析到 strip\n{info}")
            return
        ox, oy = int(o.group(3)), int(o.group(4))
        sx0, sy0, sx1, sy1 = (int(v) for v in m.groups())
        x0, y0, x1, y1 = sx0 - ox, sy0 - oy, sx1 - ox, sy1 - oy

        w, h, bpp, buf = read_png(png)
        inside = (x0 + 10, y0 + 8, x1 - 10, y1 - 8)
        outside = (x1 + 20, y0 + 8, min(x1 + 320, w - 10), y1 - 8)

        mi, gi = region_stats(w, h, bpp, buf, *inside)
        mo, go = region_stats(w, h, bpp, buf, *outside)
        print(f"{name:14s} strip=({x0},{y0})-({x1},{y1})")
        print(f"  in : mean=({mi[0]:6.1f},{mi[1]:6.1f},{mi[2]:6.1f}) grad={gi:5.2f}")
        print(f"  out: mean=({mo[0]:6.1f},{mo[1]:6.1f},{mo[2]:6.1f}) grad={go:5.2f}")
        for line in info.splitlines():
            s = line.strip()
            if s.startswith("backdrop=") or "taskbarglass" in s:
                print("  " + s)
    finally:
        p.terminate()
        try:
            p.wait(timeout=5)
        except Exception:
            p.kill()
        time.sleep(0.4)


def main():
    import shutil
    backup = os.path.join(PROBE, "config.bak.json")
    if not os.path.exists(backup):
        shutil.copy(CFG, backup)

    picks = [a for a in sys.argv[1:]]
    try:
        for i, (name, ov) in enumerate(CASES, 1):
            if picks and str(i) not in picks:
                continue
            run_case(name, ov)
    finally:
        subprocess.run(["taskkill", "/F", "/IM", "CoPul.exe"], capture_output=True)
        shutil.copy(backup, CFG)
        print("配置已还原：", CFG)


main()
