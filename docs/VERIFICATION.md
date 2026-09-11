# 验证指南

三层验证，覆盖不同粒度。**每次改动的最低要求是 ①+②；发布前必须再跑 ③。**

## ① 构建（0 警告 0 错误）

```powershell
$env:DOTNET_ROOT = 'D:\Program Files (x86)\dsh_from_github\dotnet-sdk'   # 本机自备 SDK
$env:PATH = $env:DOTNET_ROOT + ';' + $env:PATH
cd src\DshLauncher
dotnet build -c Release
```

## ② 仓库自测（`tests/DshLauncher.SelfTest`）

随仓库走、不联网、不碰用户真实数据、不开窗口；任何人 clone 后都能跑：

```powershell
dotnet run --project tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release
```

覆盖：会话文件命名与代际、版本语义化比较、dsh profile 解析、任务台账（含崩溃遗留归一化）、
存储清理边界（**安全反证：会话/凭据/手动快照必须存活**）、会话代际归并、长文本收敛、核心 bundle 常量。
加断言的方式：在 `Program.cs` 里照 `Check("分组/说明", 条件, 细节)` 追加一行即可。

## ③ 工作区 harness（`_verify-p0`，端到端 + 契约哨兵）

依赖本机环境（已安装的 dsh 运行时、上游源码目录），因此**不进仓库**、不进 CI：

```powershell
# 先停掉运行中的启动器（diagnose 用例会写真实 launcher.log，运行中跑会偶发 FAIL）
Stop-Process -Name 'DSH Launcher' -Force -ErrorAction SilentlyContinue
cd _verify-p0
dotnet build -c Release
dotnet bin\Release\net8.0-windows\win-x64\VerifyP0.dll --ui 2>&1 | Tee-Object logs\verify-<变更集>.txt
```

- 含 UI 冒烟（反射触发 `Loaded`，**不显示窗口**）。
- 含 **dsh 契约哨兵**（`contract:` 前缀）：与上游源码 + 已安装运行时双源比对；上游缺失时整段 SKIP。
  契约清单见 [`DSH_CONTRACT_INVENTORY.md`](DSH_CONTRACT_INVENTORY.md)。
- 期望结果：`N PASS / 0 FAIL`。

## 发布 SOP

```powershell
# 1) 必须先停启动器，否则 dist\DSH Launcher.exe 被锁 → MSB4018
Stop-Process -Name 'DSH Launcher' -Force -ErrorAction SilentlyContinue
# 2) 构建 + 自测 + 发布
dotnet build -c Release
dotnet run --project tests\DshLauncher.SelfTest\DshLauncher.SelfTest.csproj -c Release
dotnet publish src\DshLauncher\DshLauncher.csproj -c Release -o dist
# 3) 复制到安装目录并与 dist 核对 MD5（两者必须一致）
```

## 边界与约定

- **测试不得触碰真实数据**：日志用 `DSH_LAUNCHER_LOG_ROOT` 隔离；存储清理类功能一律用临时数据根做行为测试，
  UI 侧只做"存在性"断言（详见 `docs/../lessons/04-verification-and-safety.md`）。
- **破坏性功能必须配安全反证**：写明"绝不能被删/改的东西必须还在"。
- **能从源码静态核对的契约才做哨兵**，不做猜测式断言。
