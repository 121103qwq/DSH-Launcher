using System.Windows;
using System.Windows.Controls;
using DshLauncher.Controls;
using DshLauncher.Models;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfControl = System.Windows.Controls.Control;
using WpfPanel = System.Windows.Controls.Panel;
using WpfRadioButton = System.Windows.Controls.RadioButton;

namespace DshLauncher;

public partial class MainWindow
{
    private void AddVisualEffectsSection(StackPanel panel)
    {
        VisualEffectsSettings current;
        try
        {
            current = _versionSettingsService.ReadLauncherSettings().VisualEffects?.Clone()
                ?? new VisualEffectsSettings();
        }
        catch (Exception ex)
        {
            current = new VisualEffectsSettings();
            ShowNotice($"读取视觉效果设置失败：{ex.Message}");
        }

        var heading = new TextBlock
        {
            Text = "华丽视觉",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 0)
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        panel.Children.Add(heading);

        var content = new StackPanel();
        var card = new VisualSurface
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 14, 0, 0),
            Child = content
        };
        var enabled = new WpfCheckBox
        {
            Content = "启用华丽视觉",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            IsChecked = current.Enabled
        };
        enabled.SetResourceReference(WpfControl.ForegroundProperty, "TextBrush");
        var description = new TextBlock
        {
            Text = "仅作用于 Launcher 应用内；玻璃效果是轻量的半透明叠加，不使用真实折射，也不影响 Chat 或 Windows。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };
        description.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        content.Children.Add(enabled);
        content.Children.Add(description);

        var materialTitle = new TextBlock
        {
            Text = "材质",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 0)
        };
        materialTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        content.Children.Add(materialTitle);

        var solid = CreateVisualMaterialRadio(
            "原始实体",
            VisualMaterial.Solid,
            current.Material,
            "无玻璃叠加，保持原始实体色块。",
            "VisualMaterial");
        var frosted = CreateVisualMaterialRadio(
            "毛玻璃",
            VisualMaterial.FrostedGlass,
            current.Material,
            "使用轻量半透明模糊层，保持清晰和低资源占用。",
            "VisualMaterial");
        var liquid = CreateVisualMaterialRadio(
            "液态玻璃（轻量）",
            VisualMaterial.LiquidGlass,
            current.Material,
            "使用轻量流体高光和透明层，不模拟真实折射。",
            "VisualMaterial");
        var materialPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        AddMaterialOption(materialPanel, solid);
        AddMaterialOption(materialPanel, frosted);
        AddMaterialOption(materialPanel, liquid);
        content.Children.Add(materialPanel);

        var effectsPanel = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        var effectsTitle = new TextBlock
        {
            Text = "效果开关",
            FontWeight = FontWeights.SemiBold
        };
        effectsTitle.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        effectsPanel.Children.Add(effectsTitle);
        var ambientMotion = CreateVisualEffectCheckBox("流体背景", "低速移动的背景光晕", current.AmbientMotion);
        var particles = CreateVisualEffectCheckBox("漂浮光粒子", "少量低对比度粒子点", current.Particles);
        var pointerHalo = CreateVisualEffectCheckBox("鼠标光晕", "指针附近的柔和光晕", current.PointerHalo);
        var pointerTrail = CreateVisualEffectCheckBox("短光轨拖尾", "指针移动时的短暂光轨", current.PointerTrail);
        var clickRipples = CreateVisualEffectCheckBox("点击波纹", "点击位置的短暂波纹反馈", current.ClickRipples);
        var parallax = CreateVisualEffectCheckBox("层次视差", "轻微的内容层次位移", current.Parallax);
        effectsPanel.Children.Add(ambientMotion);
        effectsPanel.Children.Add(particles);
        effectsPanel.Children.Add(pointerHalo);
        effectsPanel.Children.Add(pointerTrail);
        effectsPanel.Children.Add(clickRipples);
        effectsPanel.Children.Add(parallax);
        content.Children.Add(effectsPanel);

        var status = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
            Text = _visualEffects?.CapabilityNotice ?? "设置会在修改后立即应用。"
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, "BlueBrush");
        content.Children.Add(status);
        panel.Children.Add(card);

        var options = new FrameworkElement[]
        {
            materialPanel,
            effectsPanel
        };
        SetVisualEffectsOptionsEnabled(current.Enabled);

        var restoring = false;

        void SetVisualEffectsOptionsEnabled(bool isEnabled)
        {
            foreach (var option in options)
            {
                option.IsEnabled = isEnabled;
            }
        }

        VisualEffectsSettings ReadVisualEffectsFromUi() => new()
        {
            Enabled = enabled.IsChecked == true,
            Material = solid.Radio.IsChecked == true
                ? VisualMaterial.Solid
                : frosted.Radio.IsChecked == true
                    ? VisualMaterial.FrostedGlass
                    : VisualMaterial.LiquidGlass,
            AmbientMotion = ambientMotion.IsChecked == true,
            Particles = particles.IsChecked == true,
            PointerHalo = pointerHalo.IsChecked == true,
            PointerTrail = pointerTrail.IsChecked == true,
            ClickRipples = clickRipples.IsChecked == true,
            Parallax = parallax.IsChecked == true
        };

        void RestoreVisualEffectsUi(VisualEffectsSettings value)
        {
            restoring = true;
            try
            {
                enabled.IsChecked = value.Enabled;
                solid.Radio.IsChecked = value.Material == VisualMaterial.Solid;
                frosted.Radio.IsChecked = value.Material == VisualMaterial.FrostedGlass;
                liquid.Radio.IsChecked = value.Material == VisualMaterial.LiquidGlass;
                ambientMotion.IsChecked = value.AmbientMotion;
                particles.IsChecked = value.Particles;
                pointerHalo.IsChecked = value.PointerHalo;
                pointerTrail.IsChecked = value.PointerTrail;
                clickRipples.IsChecked = value.ClickRipples;
                parallax.IsChecked = value.Parallax;
                SetVisualEffectsOptionsEnabled(value.Enabled);
            }
            finally
            {
                restoring = false;
            }
        }

        void ApplyAndSaveVisualEffects()
        {
            if (restoring)
            {
                return;
            }

            var desired = ReadVisualEffectsFromUi();
            VisualEffectsSettings? previous = null;
            try
            {
                // Read at event time so edits made by another Launcher page are
                // retained; only this optional field is changed before saving.
                var latest = _versionSettingsService.ReadLauncherSettings();
                previous = latest.VisualEffects?.Clone() ?? new VisualEffectsSettings();
                _visualEffects?.Apply(desired);
                latest.VisualEffects = desired.Clone();
                _versionSettingsService.SaveLauncherSettings(latest);
                SetVisualEffectsOptionsEnabled(desired.Enabled);
                status.Text = _visualEffects?.CapabilityNotice ?? "视觉效果已应用并自动保存。";
            }
            catch (Exception ex)
            {
                if (previous is not null)
                {
                    try
                    {
                        _visualEffects?.Apply(previous);
                    }
                    catch (Exception restoreException)
                    {
                        ShowNotice($"视觉效果回退失败：{restoreException.Message}");
                    }
                }

                if (previous is not null)
                {
                    RestoreVisualEffectsUi(previous);
                }

                status.Text = $"保存失败，已恢复之前的视觉效果：{ex.Message}";
                ShowNotice($"视觉效果设置保存失败：{ex.Message}");
            }
        }

        enabled.Checked += (_, _) => ApplyAndSaveVisualEffects();
        enabled.Unchecked += (_, _) => ApplyAndSaveVisualEffects();
        solid.Radio.Checked += (_, _) => ApplyAndSaveVisualEffects();
        frosted.Radio.Checked += (_, _) => ApplyAndSaveVisualEffects();
        liquid.Radio.Checked += (_, _) => ApplyAndSaveVisualEffects();
        ambientMotion.Checked += (_, _) => ApplyAndSaveVisualEffects();
        ambientMotion.Unchecked += (_, _) => ApplyAndSaveVisualEffects();
        particles.Checked += (_, _) => ApplyAndSaveVisualEffects();
        particles.Unchecked += (_, _) => ApplyAndSaveVisualEffects();
        pointerHalo.Checked += (_, _) => ApplyAndSaveVisualEffects();
        pointerHalo.Unchecked += (_, _) => ApplyAndSaveVisualEffects();
        pointerTrail.Checked += (_, _) => ApplyAndSaveVisualEffects();
        pointerTrail.Unchecked += (_, _) => ApplyAndSaveVisualEffects();
        clickRipples.Checked += (_, _) => ApplyAndSaveVisualEffects();
        clickRipples.Unchecked += (_, _) => ApplyAndSaveVisualEffects();
        parallax.Checked += (_, _) => ApplyAndSaveVisualEffects();
        parallax.Unchecked += (_, _) => ApplyAndSaveVisualEffects();
    }

    private static (WpfRadioButton Radio, TextBlock Hint) CreateVisualMaterialRadio(
        string label,
        VisualMaterial material,
        VisualMaterial selected,
        string hint,
        string groupName)
    {
        var radio = new WpfRadioButton
        {
            GroupName = groupName,
            Content = label,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            IsChecked = material == selected
        };
        radio.SetResourceReference(WpfControl.ForegroundProperty, "TextBrush");
        var text = new TextBlock
        {
            Text = hint,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24, 3, 0, 0)
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return (radio, text);
    }

    private static void AddMaterialOption(
        WpfPanel panel,
        (WpfRadioButton Radio, TextBlock Hint) option)
    {
        if (panel.Children.Count > 0)
        {
            option.Radio.Margin = new Thickness(0, 12, 0, 0);
        }

        panel.Children.Add(option.Radio);
        panel.Children.Add(option.Hint);
    }

    private static WpfCheckBox CreateVisualEffectCheckBox(
        string label,
        string hint,
        bool isChecked)
    {
        var labelText = new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold
        };
        labelText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        var hintText = new TextBlock
        {
            Text = hint,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        };
        hintText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        var content = new StackPanel();
        content.Children.Add(labelText);
        content.Children.Add(hintText);
        var checkBox = new WpfCheckBox
        {
            Content = content,
            IsChecked = isChecked,
            Margin = new Thickness(0, 10, 0, 0),
            ToolTip = hint
        };
        checkBox.SetResourceReference(WpfControl.ForegroundProperty, "TextBrush");
        return checkBox;
    }
}
