# 安装包

`CoPul.iss` 是 [Inno Setup](https://jrsoftware.org/isinfo.php) 7 的安装脚本。不要直接双击它——
用仓库根的脚本打包，版本号才会跟 csproj 一致：

```powershell
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
```

产物：`artifacts\CoPul-<版本>-setup-x64.exe` + `.sha256`。发布目录不存在时脚本会先跑
`tools\publish.ps1 -NoZip`。

## 装编译器

从 <https://jrsoftware.org/isdl.php> 下安装程序，用 `/CURRENTUSER` 静默装到用户目录，不需要管理员：

```bat
innosetup-6.x.x.exe /VERYSILENT /CURRENTUSER
```

装完 `ISCC.exe` 在 `%LOCALAPPDATA%\Programs\Inno Setup 7\`（或 `Inno Setup 6\`），脚本按这个路径优先找，
其次才找 `C:\Program Files`。中文界面用 Inno 自带的 `Languages\ChineseSimplified.isl`，不用额外下载语言包。

## 安装行为

| 项 | 值 |
| --- | --- |
| 安装范围 | 仅当前用户，默认 `%LOCALAPPDATA%\Programs\CoPul`，全程不弹 UAC |
| 安装路径 | **可自定义**（向导第二页「选择目标位置」带「浏览」）。系统目录、盘符根、超长路径会被拦下 |
| 开始菜单 | `CoPul 芯脉` + `卸载 CoPul 芯脉` |
| 桌面快捷方式 | 默认不创建（安装向导弹窗里可勾） |
| 许可协议 | 安装向导**不显示**，由程序首启时弹一次 |
| 正在运行的旧版本 | 交给 Restart Manager 关闭，不会拷到一半失败 |

装到别处：

```bat
CoPul-0.1.0-setup-x64.exe /DIR="D:\Programs\CoPul"
```

`/DIR` 指向 `C:\Program Files` 这类需要管理员的位置会被**直接拒绝**（`exit=1`，日志写明原因）——
本安装是 `PrivilegesRequired=lowest`，没有权限写系统目录。要装那儿得先改产品决策、加 UAC 提权。

静默安装 / 卸载：

```bat
CoPul-0.1.0-setup-x64.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
%LOCALAPPDATA%\Programs\CoPul\unins000.exe /VERYSILENT
```

卸载时交互模式会**问**要不要连配置和日志一起删（默认「否」）。静默卸载弹不了窗，
改为看环境变量：

```bat
set COPUL_PURGE=1
%LOCALAPPDATA%\Programs\CoPul\unins000.exe /VERYSILENT
```

要删的两处：`%APPDATA%\CoPul`（`config.json` 与备份）、`%LOCALAPPDATA%\CoPul`（日志）。

## 几个必须记住的坑

- **`SolidCompression` 必须是 `no`**。实测 `yes` 时安装器启动 37 秒后 `exit=1`、目标目录一个文件都没有、
  连 `/LOG` 指定的日志都没创建。关掉后 11.7 秒装完，代价只是产物大 6 MB。
- **产物所在的目录带「低完整性标签」时，双击会报「错误 5：拒绝访问」**。本仓库的工作目录就是这种：
  `icacls` 里能看到 `Mandatory Label\Low Mandatory Level`，里面生成的 exe 会继承它，双击出来的进程是
  Low IL，写不进 `%TEMP%`。打包脚本现在会自动检测并额外复制一份到 `Downloads`（复制按目标目录取标签）；
  手动处理可以对工作目录执行 `icacls <目录> /setintegritylevel (OI)(CI)M`。细节见 `docs/M4.48`。
- **`DisableDirPage` 别用默认值 `auto`**。它会在检测到「本机已装过」时隐藏整个目录页，用户就再也改不了安装路径。
  显式写 `no`。
- **`PrepareToInstall` 里返回错误会让静默安装挂死**。那个错误框 `/SUPPRESSMSGBOXES` 抑制不掉，实测
  超时 300 秒。路径校验要么放 `NextButtonClick`，要么放 `InitializeSetup` 并且静默时只 `Log` + `return False`。
  顺带一提：`WizardSilent` 在 `InitializeSetup` 阶段还没初始化（恒返回 False），得自己看 `GetCmdTail()`。
- **Pascal 注释不嵌套**。`{ ... {pf} ... }` 会在 `{pf}` 的右花括号处提前结束，后半句变成代码、编译报语法错。
  注释里提 Inno 常量请写文字。

验证记录（四条卸载路径、耗时、残留）见 `docs/M4.48-安装器与卸载器.md`，
自定义路径的校验规则与验收见 `docs/M4.49-自定义安装路径.md`。
