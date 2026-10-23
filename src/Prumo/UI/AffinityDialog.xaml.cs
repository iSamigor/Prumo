using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Prumo.Core;

namespace Prumo.UI;

/// <summary>Per-process CPU affinity picker; optionally saved as a permanent rule.</summary>
public partial class AffinityDialog : Window
{
    private readonly ToggleButton[] _toggles;
    private readonly int _cpuCount;

    /// <summary>Selected logical CPUs. Never 0 once the dialog is confirmed.</summary>
    public ulong Mask { get; private set; }

    /// <summary>True = also save the selection as the program's rule.</summary>
    public bool Always { get; private set; }

    public AffinityDialog(string exe, ulong currentMask, string? ruleAffinity)
    {
        InitializeComponent();

        ExeText.Text = exe;
        _cpuCount = Math.Min(Environment.ProcessorCount, 64); // the mask is a ulong
        _toggles = new ToggleButton[_cpuCount];

        ulong rule = Affinity.Parse(ruleAffinity);
        ulong initial = (rule != 0 ? rule : currentMask) & Affinity.AllCpus;
        if (initial == 0) initial = Affinity.AllCpus;

        var grid = new UniformGrid { Columns = 8 };
        for (int i = 0; i < _cpuCount; i++)
        {
            var toggle = new ToggleButton
            {
                Content = i.ToString(),
                Style = (Style)FindResource("CpuToggle"),
                IsChecked = (initial & (1UL << i)) != 0,
            };
            toggle.Checked += (_, _) => UpdateMask();
            toggle.Unchecked += (_, _) => UpdateMask();
            _toggles[i] = toggle;
            grid.Children.Add(toggle);
        }
        CpuHost.Children.Add(grid);

        AlwaysSwitch.IsChecked = ruleAffinity is not null;
        UpdateMask();
    }

    private void UpdateMask()
    {
        ulong mask = 0;
        for (int i = 0; i < _toggles.Length; i++)
            if (_toggles[i].IsChecked == true) mask |= 1UL << i;

        Mask = mask;
        ApplyButton.IsEnabled = mask != 0;

        if (mask == 0)
        {
            MaskText.Text = "Nenhuma CPU selecionada";
            MaskText.Foreground = (Brush)FindResource("Danger");
        }
        else
        {
            MaskText.Text = $"{BitOperations.PopCount(mask)} de {_cpuCount} selecionadas · {Affinity.Format(mask)}";
            MaskText.Foreground = (Brush)FindResource("MutedBrush");
        }
    }

    private void Confirm()
    {
        UpdateMask();
        if (Mask == 0) return;
        Always = AlwaysSwitch.IsChecked == true;
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Confirm();
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Quick_Click(object sender, RoutedEventArgs e)
    {
        string mode = (sender as FrameworkElement)?.Tag as string ?? "";
        for (int i = 0; i < _toggles.Length; i++)
        {
            bool? on = mode switch
            {
                "all" => true,
                "none" => false,
                "even" => i % 2 == 0,
                "odd" => i % 2 != 0,
                "no0" => i != 0,
                _ => null,
            };
            if (on is bool value) _toggles[i].IsChecked = value;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => Confirm();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
