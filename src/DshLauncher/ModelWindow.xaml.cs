using System.Windows;
using DshLauncher.Models;
using DshLauncher.Services;

namespace DshLauncher;

public partial class ModelWindow : Window
{
    private readonly ManagerInstance _instance;
    private readonly ModelService _service;

    public ModelWindow(ManagerInstance instance, ModelService service)
    {
        _instance = instance;
        _service = service;
        InitializeComponent();
        InstanceText.Text = $"当前实例：{instance.Name} · 配置文件：{_service.GetSettingsPath(instance)}";
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e) => LoadValues();

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadValues();

    private void LoadValues()
    {
        try
        {
            var values = _service.Read(_instance);
            var deepseek = values.FirstOrDefault(value => value.SettingsNamespace == "llm-deepseek");
            DeepSeekKey.Text = deepseek?.ApiKeyEnvironment ?? "DEEPSEEK_API_KEY";
            DeepSeekBaseUrl.Text = deepseek?.BaseUrl ?? string.Empty;
            DeepSeekModels.Text = deepseek is null ? string.Empty : string.Join(Environment.NewLine, deepseek.Models);

            var compatible = values.FirstOrDefault(value => value.SettingsNamespace == "llm-pi-ai");
            CompatibleProvider.Text = compatible?.Provider ?? string.Empty;
            CompatibleKey.Text = compatible?.ApiKeyEnvironment ?? string.Empty;
            CompatibleBaseUrl.Text = compatible?.BaseUrl ?? string.Empty;
            CompatibleModels.Text = compatible is null ? string.Empty : string.Join(", ", compatible.Models);
            StatusText.Text = $"已读取 {values.Count} 个 Provider。";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _service.SaveDeepSeekAsync(
                _instance,
                DeepSeekKey.Text,
                DeepSeekBaseUrl.Text,
                ParseModels(DeepSeekModels.Text));

            if (!string.IsNullOrWhiteSpace(CompatibleProvider.Text))
            {
                await _service.SaveOpenAiCompatibleAsync(
                    _instance,
                    CompatibleProvider.Text,
                    CompatibleKey.Text,
                    CompatibleBaseUrl.Text,
                    ParseModels(CompatibleModels.Text));
            }

            StatusText.Text = "模型配置已保存。重新启动实例后，DSh 会读取新的 settings.yaml。";
            LoadValues();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static IReadOnlyList<string> ParseModels(string text) =>
        text.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void ShowError(Exception ex)
    {
        StatusText.Text = ex.Message;
        System.Windows.MessageBox.Show(this, ex.Message, "模型配置失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
