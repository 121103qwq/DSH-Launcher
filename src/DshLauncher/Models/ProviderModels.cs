using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;

namespace DshLauncher.Models;

public sealed record ProviderDiagnosticResult(
    bool IsHealthy,
    string StatusText,
    string Summary,
    string ProblemText,
    string ResolutionText,
    int? DiscoveredModelCount,
    string ThinkingText)
{
    public bool IsChecking => StatusText == "检测中…";

    public bool HasIssue => !IsHealthy && !IsChecking;

    public static ProviderDiagnosticResult Checking() => new(
        false,
        "检测中…",
        "正在检查连接、模型列表和思考能力。",
        string.Empty,
        string.Empty,
        null,
        "等待检测");

    public static ProviderDiagnosticResult Healthy(
        string summary,
        int? discoveredModelCount,
        string thinkingText) => new(
        true,
        "正常",
        summary,
        string.Empty,
        string.Empty,
        discoveredModelCount,
        thinkingText);

    public static ProviderDiagnosticResult Problem(
        string statusText,
        string summary,
        string problemText,
        string resolutionText,
        int? discoveredModelCount = null,
        string thinkingText = "未声明") => new(
        false,
        statusText,
        summary,
        problemText,
        resolutionText,
        discoveredModelCount,
        thinkingText);
}

public sealed class ProviderCardViewModel : INotifyPropertyChanged
{
    private bool _isEnabled;
    private ProviderDiagnosticResult _diagnostic = ProviderDiagnosticResult.Checking();

    public ProviderCardViewModel(ModelProviderInfo provider, bool isEnabled)
    {
        Provider = provider;
        _isEnabled = isEnabled;
    }

    public ModelProviderInfo Provider { get; }

    public string ProviderKey => Provider.Provider;

    public string DisplayName => Provider.DisplayName;

    public string EndpointText => string.IsNullOrWhiteSpace(Provider.BaseUrl)
        ? Provider.Provider == "deepseek-official" ? "DeepSeek 官方默认端点" : "使用 DSh 内置 catalog"
        : Provider.BaseUrl;

    public string ConfiguredModelsText => Provider.Models.Count == 0
        ? "模型：使用默认或内置 catalog"
        : $"模型：{string.Join("、", Provider.Models.Take(3))}{(Provider.Models.Count > 3 ? "…" : string.Empty)}";

    public string CredentialText => string.IsNullOrWhiteSpace(Provider.ApiKeyEnvironment)
        ? "凭据：未指定环境变量"
        : $"凭据：{Provider.ApiKeyEnvironment}";

    public bool IsEnabled
    {
        get => _isEnabled;
        private set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ToggleBrush));
            OnPropertyChanged(nameof(ToggleStatusText));
            OnPropertyChanged(nameof(ToggleToolTip));
        }
    }

    public string ToggleStatusText => IsEnabled ? "已启用" : "已禁用";

    public string ToggleToolTip => IsEnabled
        ? "点击禁用此 Provider"
        : "点击启用此 Provider";

    public WpfBrush ToggleBrush => IsEnabled
        ? new SolidColorBrush(WpfColor.FromRgb(37, 135, 90))
        : new SolidColorBrush(WpfColor.FromRgb(190, 75, 55));

    public string StatusText => _diagnostic.StatusText;

    public string DiagnosticSummary => _diagnostic.Summary;

    public string ThinkingText => $"思考：{_diagnostic.ThinkingText}";

    public string ModelCountText => _diagnostic.DiscoveredModelCount is { } count
        ? $"接口模型：{count} 个"
        : "接口模型：未检测";

    public Visibility IssueVisibility => _diagnostic.HasIssue
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string IssueToolTip => "查看问题与解决方法";

    public string IssueDetails => string.IsNullOrWhiteSpace(_diagnostic.ProblemText)
        ? _diagnostic.Summary
        : $"问题：{_diagnostic.ProblemText}\n\n解决方法：{_diagnostic.ResolutionText}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetEnabled(bool value) => IsEnabled = value;

    public void SetDiagnostic(ProviderDiagnosticResult diagnostic)
    {
        _diagnostic = diagnostic;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DiagnosticSummary));
        OnPropertyChanged(nameof(ThinkingText));
        OnPropertyChanged(nameof(ModelCountText));
        OnPropertyChanged(nameof(IssueVisibility));
        OnPropertyChanged(nameof(IssueDetails));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
