# tools/m14 原始输出索引

M1.4b（2026-10-28，00:16–00:24）。所有数字都是本机 `dotnet CoPul.dll` 直接跑的原始输出，未做手工修改。
结论与判定见 `docs/M1.4b-UI增量与性能复测报告.md`。

**环境：整机 CPU 中位 29.9%（区间 21.5–55.9%），不满足 A-26 的独占前提 → 本轮判定为「未判定」（R24 第三次复现）。**

## 整窗（窗口显示，820×1230 全展开，60s / 预热 10s）

| 文件 | 单核 | 备注 |
| --- | --- | --- |
| `perf-1.txt` | **2.603%** | Layout 4.200 ms/帧，115 帧 |
| `perf-2.txt` | **2.550%** | Layout 4.365 ms/帧，115 帧 |
| `perf-3.txt` | 4.048% | 噪声轮：同分钟整机 33–36%，`DeepSeek Harness` 三进程并起；窗口被钳到 820×1019 |

## 对照（隐藏窗口，拆成本）

| 文件 | 配置 | 时长 | 单核 |
| --- | --- | --- | --- |
| `diag-hidden.txt` | 全展开 | 60 s | **1.171%** |
| `diag-hidden-all.txt` | 全展开 | 40 s | 1.562% |
| `diag-hidden-noproc.txt` | 进程区块折叠 | 40 s | **0.742%** |
| `diag-hidden-noui.txt` | 趋势 + 温度折叠 | 40 s | 1.015% |

## 环境与其他

| 文件 | 内容 |
| --- | --- |
| `load.csv` | 测量期间每 3 秒一次的整机 CPU + 前四名进程（56 个样本） |
| `sample_load.py` | 负载采样脚本（psutil，双采样差值法） |
| `probe-smoke.txt` | 20 s 冒烟，确认 `--stats` 可用（此轮 3.434% 不作数：预热仅 5 s） |

## 复跑方式

```
cd src/CoPul.App/bin/Release/net8.0-windows
dotnet CoPul.dll --stats <out.txt> --seconds 60 --warmup 10
```

折叠对照改 `%APPDATA%\CoPul\config.json` 的 `CollapsedBlocks`（如 `["proc"]`）；
**跑完记得还原成 `[]`**，否则下次启动会少一个区块。
