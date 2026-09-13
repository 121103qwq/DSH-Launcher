using System.Windows;
using DshLauncher.Models;
using DshLauncher.Services;

namespace DshLauncher;

public partial class MainWindow
{
    private async Task<string> InstallMarketPackAsync(ModPackMarketEntry entry, CancellationToken pageToken)
    {
        var template = GetVersionTemplate();
        if (template is null)
            return "没有可用的 DSh 运行目录，请先在设置中准备或检测运行环境。";
        if (!TryBeginLifecycleOperation())
            return "当前有实例或安装操作正在进行，请稍后重试。";

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(pageToken, _windowCancellation.Token);
        var token = cancellation.Token;
        string? packagePath = null;
        ManagerInstance? created = null;
        RuntimeProgressWindow? progressWindow = null;
        try
        {
            progressWindow = new RuntimeProgressWindow(this, cancellation, "安装社区整合包",
                "下载后会显示内容预览。离开整合包页面可取消；已创建的版本会保留。 ");
            progressWindow.SetIndeterminate(true);
            progressWindow.SetStatus($"正在下载 {entry.Name}…");
            progressWindow.Show();
            var progress = new Progress<NodeDownloadProgress>(item =>
            {
                if (!token.IsCancellationRequested && progressWindow.IsVisible)
                    progressWindow.SetDownloadProgress(item, entry.Name);
            });
            packagePath = await _modPackMarketService.DownloadPackageAsync(entry, progress, token);
            progressWindow.SetIndeterminate(true);
            progressWindow.SetStatus("正在检查整合包内容…");
            var preview = await Task.Run(() => _versionPackageService.PreviewPackage(packagePath), token);
            token.ThrowIfCancellationRequested();
            var warnings = preview.Warnings.Count == 0 ? string.Empty
                : $"\n注意：\n- {string.Join("\n- ", preview.Warnings)}\n";
            if (System.Windows.MessageBox.Show(this,
                    $"{preview.Name}\n{preview.Description}\n\n"
                    + $"格式：{preview.PackageKindText}\nDSh 要求：{preview.DshVersion ?? "未标记"}\n"
                    + $"Plugins：{preview.PluginCount} · Skills：{preview.SkillCount} · Agent Presets：{preview.AgentPresetCount}\n"
                    + $"Providers：{preview.ProviderCount} · Workflow：{preview.Workflow ?? "无"}\n"
                    + $"来源：{entry.DownloadUrl}\n"
                    + "已按市场提供的 SHA-256 和文件大小校验下载内容。\n"
                    + warnings
                    + "\n这将创建新的独立版本，并通过 DSh 官方 CLI 安装包内声明的第三方 Plugin 依赖。"
                    + "这些插件可以执行代码，请只安装你信任的内容。不会覆盖已有版本，也不会自动启动。\n\n确认安装吗？",
                    "社区整合包安装预览", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                progressWindow.CancelTask("用户取消了整合包安装。");
                return "已取消安装，没有创建版本。";
            }

            token.ThrowIfCancellationRequested();
            progressWindow.SetStatus("正在创建独立版本…");
            created = await Task.Run(() => _versionPackageService.ImportPackage(packagePath, template), token);
            // Import returns a registered version. Always
            // surface it, even if the page was left while the file operation finished.
            AddCreatedVersion(created);
            token.ThrowIfCancellationRequested();
            if (preview.PluginCount > 0)
            {
                progressWindow.SetStatus($"已创建 {created.Name}，正在通过 DSh 官方 CLI 恢复依赖…");
                await _extensionService.RestoreProfileDependenciesAsync(created, _nodeRuntime, token);
            }
            var message = $"整合包已安装为“{created.Name}”，可回到启动页使用。原版本没有被覆盖。";
            progressWindow.CompleteTask(message);
            ShowNotice(message);
            return message;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            var message = created is null ? "已取消安装，没有创建版本。"
                : $"已取消后续操作；版本“{created.Name}”已保留，依赖可能尚未恢复完成。请在该版本的 DSH_HOME 环境中通过 DSh 官方 Plugin CLI 恢复依赖。";
            progressWindow?.CancelTask(message);
            if (created is not null) ShowNotice(message);
            return message;
        }
        catch (Exception ex)
        {
            var message = created is null ? $"安装整合包失败：{ex.Message}"
                : $"版本“{created.Name}”已保留，但后续配置或依赖恢复失败：{ex.Message}。请在该版本的 DSH_HOME 环境中通过 DSh 官方 Plugin CLI 恢复依赖。";
            progressWindow?.FailTask(message);
            if (created is not null) ShowNotice(message);
            return message;
        }
        finally
        {
            try
            {
                if (progressWindow?.IsVisible == true) progressWindow.Close();
                ModPackMarketService.CleanupDownload(packagePath);
            }
            finally
            {
                EndLifecycleOperation();
            }
        }
    }
}
