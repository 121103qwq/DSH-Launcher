# 与 dsh 的契约清单（DSH Contract Inventory）

> 目的：把启动器对 dsh 的**全部依赖点**写成一张表，并为每条配一个**哨兵测试**。
> 背景：本仓已被 dsh 升级打断过多次（`--no-open`、token 401、会话格式 0→3），
> 靠"升完再发现"代价很高；这张表是止血工具（借鉴清单 #23）。
>
> - 上游权威来源优先级：**已安装运行时**（`%ProgramFiles(x86)%\dsh_launcher\dsh_runtime`，用户实际在跑的东西）
> ＞ 本地上游克隆 `deepseek-harness/`（最新源码，用于提前发现漂移）。
> - 哨兵实现在 `_verify-p0/Program.cs` 的「契约哨兵」段，命名前缀 `contract:`。
> - 最近核对：上游 `c291e7961a`（2026-09-10，dsh-v0.1.5-rc.2-139-g…）、安装运行时 0.1.2-rc.1 与 0.1.5-rc.1（2026-09-11）。

## 一、契约表

| # | 契约点 | dsh 侧权威定义 | 启动器落点 | 破坏时的症状 | 哨兵 |
|---|---|---|---|---|---|
| **C1** | 会话格式版本号 | `dsh-session/lib/index.js` `SESSION_FORMAT_VERSION = 3`（0.1.2-rc.1 无此包；标签 0.1.2 为 `0`） | `SessionFileNames.KnownMaxFormatVersion`、`ConversationService`（宽容接受任意版本） | 高版本会话读不出（对话页"已读取 0/0"） | `contract: 会话格式版本未超出已核对范围` |
| **C2** | 会话文件名 canonical 规则 | `dsh-session-format` `CANONICAL_LOG_FILENAME = /^session(?:\.v([1-9][0-9]*))?\.jsonl$/u`；v0 不带标签，v1+ 为 `session.vN.jsonl`（小写 v、无前导零） | `SessionFileNames.TryParse/Build` | 导入/同步写出 dsh 不认识的文件名 → 会话"消失" | `contract: canonical 文件名规则与上游一致` |
| **C3** | 会话头字段 | `dsh-session-persistence-jsonl` `parseHeader`/`toHeaderLine`：必需 `type/version/id/createdAt/isSeeded/delegationDepth`，可选 `cwd/parentSession/origin/agentPreset`，白名单拒绝 `sandboxMode`/`approvalPolicy` | `ConversationService` 头部读取（**故意比 dsh 宽松**：`isSeeded` 缺失也接受） | 导入被判"corrupt session log"；或写出 dsh 拒收的头 | `contract: 会话头必需字段仍被 dsh 要求`、`contract: 真实样本头部可被启动器解析` |
| **C4** | 会话/项目目录名编码 | `projectKey`（`/ \ :`→单 `-`；不安全码元 `~XXXX` 大写四位十六进制；去前导 `-`；空→`root`；截断 251；包 `--…--`）与 `encodeSegment`（安全集 `[A-Za-z0-9._-]`，`~` 自身转义，`.`/`..` 特例） | `ConversationService.ProjectKey/EncodeSegment`（同源重实现） | 导入落到陌生项目目录、会话在 UI 里"丢了" | `contract: 目录编码向量与上游测试一致` |
| **C5** | 会话根布局 | `<DSH_HOME>/sessions/<projectKey>/<encodeSegment(sessionId)>/session.vN.jsonl[.zstd]` | `ConversationService.GetSessionsRoot` 等 | 全部会话不可见 | `contract: 会话根仍位于 DSH_HOME/sessions`（源码级） |
| **C6** | 压缩编码 | `DEFAULT_COMPRESSION = "zstd"`；**同一 sessions 根混用两种编码 → `encodingMismatch` 硬报错** | `SessionFileNames.ResolveCompression`、`ConversationService.ResolveTargetSessionCompression` | dsh 启动即报错 | `contract: canonical 文件名规则与上游一致`（含编码后缀矩阵） |
| **C7** | CLI 启动参数 | `apps/cli/src/args.ts`：`--profile`/`--from-default-profile`/`--patch`（启动器自己的旗标，第一个不认识的 token 之后全部交给 app）；`packages/bundle/web-app/src/startup.ts`：`--host`/`--port`/`--no-open`/`--trusted-host` | `DshInstanceRunner.BuildArguments` | 启动失败，或参数被 app 吞掉当成自己的参数 | `contract: CLI 旗标仍在上游参数解析里` |
| **C8** | 就绪输出行（含 launch token） | `packages/bundle/web-app/src/index.ts`：`console.log(\`dsh web: ${authenticatedUrl}…\`)`，URL 带 `?token=…`，可能追加 `(LAN: …)` | `DshInstanceRunner.AuthenticatedUrlPattern = @"dsh\s+web:\s*(https?://\S+)"` | Chat 窗口 401 白屏；Web 重开拿不到 token | `contract: dsh web: 输出行仍匹配启动器正则` |
| **C9** | Node 引擎范围 | 上游根 `package.json` `engines.node = "^22.19.0 \|\| >=24.0.0"` | `PortableNodeService.MinimumVersion = "22.19.0"` | 启动早停；zstd / AbortSignal.timeout 缺失 | `contract: Node 引擎下限与上游一致` |
| **C10** | profile 结构与 bundles | `profiles/<name>/package.json` 的 `dsh.profile.bundles`（上游 `profile_kind` 同口径） | `DshEnvironmentScanner`、`ExtensionService`、`SafeProfileService`、`PluginBisectService` | 扫描分类错；安全模式剥错包 | `contract: dsh.profile.bundles 仍在上游使用` |
| **C11** | 插件 peerDependencies | 如 `@deepseek-ai/dsh-settings`（核心包随 dsh 版本走） | `PluginCompatibility`（变更集 70） | 插件装上即崩（工具调用全失败） | 既有插件兼容性用例 |
| **C12** | `--no-open` 引入版本 | 历史事实：0.1.0-rc.8 起；**无法从代码验证**，只能靠实测 | `DshInstanceRunner.SupportsNoOpen` | 旧版启动弹浏览器（有浏览器守卫兜底） | 文档记录（不设哨兵） |
| **C13** | DSH_HOME 内固定文件名 | `settings.yaml`、`.credentials.yaml`、`profiles/`、`sessions/`、`storages/` | 快照/导出/同步/设置写入 | 快照缺内容、导出不完整 | 部分由真实端到端覆盖 |

| **C14** | 权限模型键位：`sandbox-policy.config.mode` / `workspaceRoot`、`approval.config.policy`、`permission.config.presets`（三档含 `danger-full-access` + `approval: never`） | 已安装运行时出厂层：`<version>/node_modules/@deepseek-ai/dsh/node_modules/@deepseek-ai/dsh-base/cordis.patch.yml` | `DangerousConfigAuditService`（#20 增量 5） | **静默失效**：危险配置检查永远"未发现"，用户误以为体检通过 | `contract: 权限模型键位仍在出厂层（C14）` |
| **C15** | 安全体检依赖的环境变量名：`DSH_PERMISSION_MODE`、`DSH_TELEMETRY_MODE`、`DSH_TELEMETRY_OTLP_URL` | 同上（出厂层里的 `!!js process.env.…` 表达式） | `DangerousConfigAuditService` | 环境变量类风险不再被检出（同样是静默失效） | `contract: 安全体检依赖的环境变量名仍在出厂层（C15）` |
| **C16** | 遥测默认导出口域名 `harness-telemetry.deepseeksvc.com`（官方域判据） | 同上 | `DangerousConfigAuditService`（非官方域 → 危险） | **假报警**：上游换域后官方域会被当成"第三方外发" | `contract: 遥测默认域仍是 *.deepseeksvc.com（C16）` |
| **C17** | 凭据文件名 `.credentials.yaml`（含 `.yml` 变体）与"只报元数据、不回显值"策略 | C13 的固定文件名 + 本仓安全契约（work-log/73） | `CredentialAuditService`、`DangerousConfigAuditService`（凭据可写检查） | 清单为空 → 看起来"本机没有凭据文件" | 由 SelfTest 断言覆盖（凭据文件清单 + 4 组安全反证；未单独设 harness 哨兵） |

## 二、降级方向（0.1.5 → 0.1.2）为什么是单向的

**两个已安装运行时的实物证据（2026-09-11 核对）**：

| 能力 | 0.1.2-rc.1 | 0.1.5-rc.1 |
|---|---|---|
| 会话文件名构造 | `logPath()` 恒为 `session.jsonl[.zstd]`（`dsh-session-persistence-jsonl/lib/index.js:165`），写出的头文件名固定 `session.jsonl`（同文件 `:948`） | `sessionFormatLogFilename(v)` → v0 = `session.jsonl`，v1+ = `session.vN.jsonl`（`dsh-session-format/lib/index.js:474`） |
| 格式目录/迁移链 | **不存在**（无 `dsh-session-format`、`-format-catalog`、`-format-v0-to-v1/-v1-to-v2/-v2-to-v3`） | 五个包齐全（`SESSION_FORMAT_VERSION = 3`） |
| 读 v3 会话 | 按 `session.jsonl.zstd` 找文件 → **找不到**（文件叫 `session.v3.jsonl.zstd`），会话表现为缺失/空 | 正常读取 |

推论（已用真实文件核对，见 work-log/53）：
1. **0.1.5 下新建的会话在 0.1.2 里读不出来**（不是报错，而是"文件不存在"式的静默缺失，用户观感最差）。
2. **已迁移会话保留历史代际**（上游：`migrated historical generation retained until an explicit write open publishes it`）→ 若目录里同时存在 `session.jsonl`（v0）与 `session.v3.jsonl`，降级后 0.1.2 会读到 **v0 那份**（内容止于迁移点），0.1.5 读 v3 → **同会话两个分叉**。
3. 因此启动器对降级的策略是**警告 + 一键导出会话备份**，**不做格式转换**（迁移交给 dsh 官方链，见 work-log/51）。

## 二之二、已知分歧与未决项

| 项 | 现状 | 影响 |
|---|---|---|
| 多代际会话目录在**对话页**列多行 | `ConversationService.List` 按文件列（同目录 v2+v3 → 2 行）；跨实例同步服务已按"会话目录归并、取最高代际" | 从 0.1.2 升级上来的实例会看到重复会话行；纯 v3 实例不受影响。见 work-log/53 F1（待决定） |
| 启动器对会话头的校验比 dsh **宽松** | dsh 在 v3 要求 `isSeeded`；启动器缺失也接受（保证旧格式与半成品文件可读） | 有意为之：宽松读、按头部版本命名写，宁可多读不误判"损坏" |

### 二之三、已确认的上游行为事实（实现选择的依据）

- **`dsh --dump-config` 会写盘**：在空 `DSH_HOME` 里执行 `dsh --profile web --dump-config`（"不启动"），
  仍会生成 `profiles/web/{cordis.yml, cordis.patch.yml, package.json, pnpm-workspace.yaml}`（work-log/74 实测）。
  → 因此**安全体检一律直接读文件**（profile 层 + `settings.yaml` + 运行时出厂层），
  绝不调用 CLI 取配置；若上游哪天改成不写盘，可再评估是否简化为调 CLI。

## 三、哨兵怎么跑、失败了怎么办

```powershell
# 在 _verify-p0 下（先停 Launcher，避免共用 launcher.log 干扰 diagnose 用例）
dotnet bin\Release\net8.0-windows\win-x64\VerifyP0.dll --ui
```

- 输出里 `contract:` 开头的 PASS/FAIL 即哨兵；上游克隆缺失时整段 SKIP（不会误报失败）。
- **FAIL 的处理流程**：先看该条对应的 C# 编号 → 在上游找到新定义 → 判断是"启动器要跟改"还是"记录新版本" → 改代码/常量 → 更新本表"最近核对" → 重跑。
- 哨兵只覆盖**可从源码静态核对**的契约；`C12`/`C13` 与真实启动行为仍靠端到端用例（`--ui` 的 UI 段 + `func-check/` 探针）。

---

## 整合包格式（#24，work-log/70 §E；规范来源 `repo-review-dsh-plugins/docs/PACK_MANIFEST.md`）

### 容器

| 容器 | 结构 | 我方 |
|---|---|---|
| `.dspack`（pack-structure **v2**） | ZIP：根 `dspack.json` = `{"format":"dspack","version":2}` + `manifest.json` + 可选 `package.json`/`pnpm-workspace.yaml`/`pnpm-lock.yaml` + `overrides/` | **读写**（导出用 v2） |
| `.dspack` **v3** | 同上，`dspack.json.version = 3`；**manifestVersion 5 必须配 v3** | **读**（配对校验） |
| 旧 `.tgz`（pack-structure v1） | gzip+tar，扁平 `manifest.json` / `package.json` / `cordis.patch.yml` / 可选 lock/workspace | **读** |

### manifest 兼容矩阵

| 版本 | 关键差异 | 我方 |
|---|---|---|
| **v2** | `displayName`/`description` 仅字符串；`dshVersion` 是**范围**（导入取**下限**）；`dependencies` 为 pnpm 原始 spec **原样透传** | 读 |
| **v3** | 依赖坐标钉死（npm 精确版本 / git commit sha）；`dshVersion` 精确；内联 `patch` | 读 |
| **v4** | 新增 `type`（`profile`；`collection` 报"暂未支持"）与 `files[]`（`{path,sha256,size,urls[]}`，与 `overrides/` 同路径时 **files[] 胜**） | **读 + 写（导出用 v4）** |
| **v5** | `type: profile`（可带 `home/` 覆盖 `$DSH_HOME/xxx`）与 `type: dshhome`（整个 HOME 快照：`defaultProfile` + `profiles`（≥1，**不得含 web/headless**）+ 可选 `presets`/`skills`/`instructions`）；仅支持**新建实例** | 读 |

### 依赖坐标三条转换规则

| manifest 坐标 | package.json 条目 |
|---|---|
| `dsh-pet: "0.2.0"` | `"dsh-pet": "0.2.0"` |
| `github:owner/repo: "<sha>"` | `"repo": "github:owner/repo#<sha>"` |
| `github:owner/repo#path:/pkg: "<sha>"` | `"pkg": "github:owner/repo#<sha>&path:pkg"` |

### 我方实现与安全边界

| 关注点 | 实现 |
|---|---|
| 读取 | `Services/DshPackArchive.cs`：条目数 4096 / 单条 32 MB / 总 256 MB；**任一条目路径非法（zip-slip）即整体拒载** |
| 解析 | `Services/DshPackFormat.cs`：容器标记、manifest v2–v5、`files[]` 校验（64 位小写 sha256、正整数 size、http(s) urls）、路径安全、配对校验 |
| 导入 | `Services/DshPackImportService.cs`：**一律新建实例**（HOME 强制在 Launcher 的 `instances/` 下）；`package.json` 由 manifest **权威重建**；失败**整体回滚**（注销实例 + 删 HOME + 清空目录） |
| `files[]` 下载 | `Services/DshPackFileDownloader.cs`：**默认拒绝**（须显式同意）；按 `urls[]` 试镜像；**sha256 + size 双校验通过才落位**；单文件上限 64 MB |
| 导出 | `Services/DshPackWriter.cs`（规范写盘）+ `Services/PackExportService.cs`（从实例 profile 组装）；自家 v1 导出保留为旧格式 |
| 未做 | `pnpm install` 与按 `dshVersion` 自动安装（版本不符时**明确拒绝**，见 work-log/70 §G）；真实第三方样本包验证（仍缺） |
