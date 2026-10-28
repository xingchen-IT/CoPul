# FakePresentMon —— FPS 采集链路的离线自测夹具

不属于产品代码，不随包分发。存在的理由只有一个：

> 真实 `PresentMon.exe` 必须有管理员权限才能开 ETW 会话（否则 `access denied`），
> 而 agent 侧没有 UAC 提权通路。没有它，「读 CSV 流 → 解析列 → 统计六项 → 填格
> → 画折线」这条链路在开发机上根本没法验证。

## 它是怎么顶替真品的

按 PresentMon 2.x `--v1_metrics` 的表头逐帧输出 CSV 到 stdout，帧模型固定：

- 基础 60 FPS（16.667 ms）
- 每 300 帧插一次 50 ms 的长帧

这样 1% Low 应该明显低于平均帧率，能一眼看出 1% Low 有没有被算成平均值。

表头里的 `msBetweenPresents` 是**小写 m**——与 `CsvOutput.cpp` 的
`WriteCsvHeader<FrameMetrics1>` 一致，也是 `PresentMonSession` 按列名定位（大小写
不敏感）的原因。

## 用法

```bash
# 1. 编译
dotnet build -c Release -o out

# 2. 顶替真品（apphost 按 exe 名找同名 dll/runtimeconfig，所以要复制成三份）
cd <运行目录>/presentmon
cp <repo>/tools/fps-selftest/out/FakePresentMon.dll  PresentMon.dll
cp <repo>/tools/fps-selftest/out/FakePresentMon.runtimeconfig.json PresentMon.runtimeconfig.json
cp <repo>/tools/fps-selftest/out/FakePresentMon.deps.json        PresentMon.deps.json
cp <repo>/tools/fps-selftest/out/FakePresentMon.exe  PresentMon.exe

# 3. 绕过提权检查（真品需要提权，夹具不需要）
#    —— 把 FpsProvider.Initialize 里的 IsAdministrator 分支临时短路成 false，
#       并把 StartSession(...) 改成 StartSession(allowUnelevated: true)

# 4. 跑并截图
CoPul.dll --shot out.png --wait 18000
```

## 已验证的期望值（20 秒、60 FPS、每 300 帧一次 50 ms 长帧）

| 指标 | 实测 | 说明 |
| --- | --- | --- |
| 当前帧率 | 59 | 接收时间戳有百微秒级抖动 |
| 最低/最高/平均 | 58 / 61 / 60 | 逐秒桶的极值与算术平均 |
| 1% Low | 36 | 1080 帧里 3 次卡顿，1% 取最慢 10 帧 =(3×50+7×16.7)/10 → 1000/26.7 |
| 帧时间 | 16.9 ms | 1000 / 当前帧率 |

> 1% Low 那个 36 恰好说明了口径：卡顿率只有 0.33%，1% 里必然混进正常帧。
> 想要更"难看"的 1% Low，把 `HitchEvery` 改小即可。
