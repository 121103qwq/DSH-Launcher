# Codex Review → Windows ntfy 通知

本仓库的 `.github/workflows/codex-review-ntfy.yml` 支持两种 Codex Review 结果来源：

- Codex 以 GitHub Review 身份提交 `approved` review；需要配置 `CODEX_REVIEW_ACTOR`。
- Codex 产生 GitHub Check Run；需要配置准确的 `CODEX_REVIEW_CHECK_NAME`，且结论必须为 `success`。

没有配置对应的账号或检查名时，workflow 不会把普通人工批准当成 Codex 通过。

## GitHub 配置

在仓库 `Settings → Secrets and variables → Actions` 中配置：

| 类型                 | 名称                      | 用途                                               |
| -------------------- | ------------------------- | -------------------------------------------------- |
| Secret               | `NTFY_TOPIC`              | ntfy topic 名称；不要提交到仓库                    |
| Secret，可选         | `NTFY_TOKEN`              | 使用受保护 topic 或自建 ntfy 服务时的 Bearer token |
| Variable，可选       | `NTFY_SERVER`             | ntfy 服务地址，默认 `https://ntfy.sh`              |
| Variable，按事件需要 | `CODEX_REVIEW_ACTOR`      | Codex Review 机器人的 GitHub 登录名                |
| Variable，按事件需要 | `CODEX_REVIEW_CHECK_NAME` | Codex Check Run 的准确名称                         |

`NTFY_TOPIC` 应使用不可猜测的随机名称。公开 ntfy 服务的 topic 名称本身就是订阅入口，不要使用 `dsh-launcher` 这类简单名称；如果服务支持访问控制，建议同时配置 `NTFY_TOKEN`。

Windows PowerShell 中可通过 GitHub CLI 配置变量。GitHub 网络操作使用本机安全包装脚本：

```powershell
$ghSafe = "C:\Users\121103qwq\.codex\scripts\github-safe.ps1"
$repo = "121103qwq/DSH-Launcher"

& $ghSafe variable set CODEX_REVIEW_CHECK_NAME --repo $repo --body "Codex Review"
& $ghSafe variable set NTFY_SERVER --repo $repo --body "https://ntfy.sh"
& $ghSafe secret set NTFY_TOPIC --repo $repo
```

最后一条命令会隐藏输入内容；输入随机 topic 后按 Enter。若 Codex 使用 Review 身份而不是 Check Run，再配置实际登录名：

```powershell
& $ghSafe variable set CODEX_REVIEW_ACTOR --repo $repo --body "实际的 Codex GitHub 登录名"
```

当前仓库尚未出现 Codex Check Run 或 Review，因此不能凭空填入最后两个值。第一次 Codex 审查完成后，从 PR 的 Checks 或 Reviews 页面复制准确名称/登录名再配置。

## Windows 桌面接收

ntfy 官方目前推荐 Windows 使用 Web/PWA，而不是假设存在一个官方原生桌面客户端：

1. 在 Edge 打开 `https://ntfy.sh/app`。
2. 订阅与 GitHub Secret 完全相同的 topic。
3. 开启后台通知；需要常驻桌面入口时，把 ntfy 安装为 Edge PWA。
4. 在 Windows 通知设置中允许 Edge/ntfy 通知。

## 测试

workflow 文件进入默认分支且 Secrets/Variables 配置完成后，可手动发送测试通知：

```powershell
& $ghSafe workflow run codex-review-ntfy.yml --repo $repo
& $ghSafe run list --repo $repo --workflow codex-review-ntfy.yml --limit 3
```

实际 Codex Review 通过时，workflow 会把 PR 链接放入 ntfy 的点击动作。此实现只负责通知，不会自动 Merge，也不会修改分支保护规则。
