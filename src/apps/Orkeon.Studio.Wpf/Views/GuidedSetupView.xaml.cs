using System.Windows.Controls;

namespace Orkeon.Studio.Wpf.Views;

/// <summary>The guided setup dialog. XAML wiring only; every behaviour lives in the ViewModels.</summary>
public partial class GuidedSetupView : UserControl
{
    /// <summary>Loads the XAML.</summary>
    public GuidedSetupView() => InitializeComponent();
}
