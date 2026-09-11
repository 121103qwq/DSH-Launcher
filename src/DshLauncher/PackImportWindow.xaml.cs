using System.IO;
using System.Windows;
using System.Windows.Controls;
using DshLauncher.Models;
using DshLauncher.Services;

namespace DshLauncher;

/// <summary>
/// 导入整合包的窗口内确认面板（批次 A③）：取代原来的系统消息框，
/// 让「实例名 / profile / 文件清单与体积 / 下载清单」在同一张面板里可读。
///
/// 下载同意（Q2）用**默认不勾选**的复选框表达：`files[]` 非空时未勾选则「导入」按钮禁用，
/// 勾选后才允许按包内 https 地址下载（导入侧仍会强制 sha256 + size 双校验，失败整体回滚）。
/// </summary>
public partial class PackImportWindow : Window
{
    private readonly int _downloadCount;

    public PackImportWindow(
        Window? owner,
        PackArchive archive,
        DshPackImportPlan plan,
        ManagerInstance template)
    {
        InitializeComponent();
        if (owner is not null)
        {
            Owner = owner;
        }

        var manifest = archive.Manifest;
        var isHomeSnapshot = manifest.Type == PackManifestType.DshHome;
        Title = $"导入整合包：{manifest.ResolveDisplayName(null)}";
        SummaryText.Text = $"{manifest.ResolveDisplayName(null)} {manifest.PackVersion}"
            + $"（manifest v{(int)manifest.Version}）· "
            + (isHomeSnapshot ? "整个 DSH_HOME 快照" : "profile 整合包");

        InstanceText.Text = $"实例名：{plan.InstanceName}\nprofile：{plan.ProfileName}\n"
            + $"要求 DSh：{plan.RequiredDshVersion ?? "未声明"}"
            + $"（当前模板 {template.DetectedVersion ?? "未知"}：{(plan.TemplateVersionMatches ? "匹配" : "不匹配")}）";

        FilesText.Text = $"profile 文件 {plan.ProfileFiles.Count} 个 · home 文件 {plan.HomeFiles.Count} 个 · "
            + $"bundles {manifest.Bundles.Count} 个 · 依赖 {manifest.Dependencies.Count} 条";

        foreach (var path in plan.ProfileFiles.Take(50))
        {
            FileList.Items.Add(new TextBlock
            {
                Text = "· " + path,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush")
            });
        }

        if (plan.ProfileFiles.Count > 50)
        {
            FileList.Items.Add(new TextBlock
            {
                Text = $"· …共 {plan.ProfileFiles.Count} 个",
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush")
            });
        }

        foreach (var path in plan.HomeFiles.Take(50))
        {
            FileList.Items.Add(new TextBlock
            {
                Text = "· [home] " + path,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush")
            });
        }

        if (plan.Warnings.Count > 0)
        {
            WarningText.Text = "提示：" + string.Join("；", plan.Warnings);
        }

        _downloadCount = manifest.Files.Count;
        if (_downloadCount == 0)
        {
            DownloadText.Text = "需联网下载：无（全部载荷都在包内）";
            DownloadText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        }
        else
        {
            var totalMb = manifest.Files.Sum(file => file.Size) / 1024.0 / 1024.0;
            DownloadText.Text = $"需联网下载：{_downloadCount} 个文件（合计 {totalMb:F1} MB）";
            DownloadText.Foreground = (System.Windows.Media.Brush)FindResource("WarningTextBrush");
            foreach (var file in manifest.Files.Take(20))
            {
                DownloadList.Items.Add(new TextBlock
                {
                    Text = $"· {file.Path}（{file.Size / 1024.0:F0} KB，sha256 {file.Sha256[..Math.Min(12, file.Sha256.Length)]}…）",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush")
                });
            }

            if (_downloadCount > 20)
            {
                DownloadList.Items.Add(new TextBlock
                {
                    Text = $"· …共 {_downloadCount} 个",
                    FontSize = 11,
                    Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush")
                });
            }

            DownloadConsentPanel.Visibility = Visibility.Visible;
            ImportButton.IsEnabled = false;
            StatusText.Text = "该包包含需要联网下载的文件：勾选上面的同意项后才能导入。";
        }
    }

    /// <summary>用户勾选了"允许下载"（无下载项时该值无意义，导入侧仍按 files[] 判断）。</summary>
    public bool AllowDownloads => AllowDownloadsCheck.IsChecked == true;

    private void AllowDownloads_Changed(object sender, RoutedEventArgs e)
    {
        ImportButton.IsEnabled = _downloadCount == 0 || AllowDownloads;
        StatusText.Text = ImportButton.IsEnabled
            ? "将按包内声明的 https 地址下载并强制 sha256 校验；校验不通过会整体回滚。"
            : "该包包含需要联网下载的文件：勾选上面的同意项后才能导入。";
    }

    private void Import_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
