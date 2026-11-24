using System.Windows.Controls;

namespace Orkeon.Studio.Wpf.Views;

/// <summary>The Microsoft Teams settings tab. XAML wiring only; every behaviour lives in the ViewModels.</summary>
public partial class ChatAccountsView : UserControl
{
    /// <summary>Loads the XAML; picking another account brings its form on screen (R24).</summary>
    public ChatAccountsView()
    {
        InitializeComponent();
        SelectionReveal.Attach(AccountList, AccountForm);
    }
}
