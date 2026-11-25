using Orkeon.Studio.Wpf.ViewModels.Mvvm;

namespace Orkeon.Studio.Wpf.ViewModels.Config;

/// <summary>One model picked in the guided setup: its identifier and the alias its setting is named by.</summary>
public sealed class GuidedModelViewModel : ObservableObject
{
    private readonly GuidedSetupViewModel _owner;
    private string _model;
    private string _alias;
    private bool _isAliasTyped;

    internal GuidedModelViewModel(GuidedSetupViewModel owner, string model)
    {
        _owner = owner;
        _model = model;
        _alias = ModelProfileEditorViewModel.AliasFor(model) ?? "";
        RemoveCommand = new RelayCommand(() => _owner.RemoveModel(this));
    }

    /// <summary>The identifier the provider knows; the alias follows it until one is typed.</summary>
    public string Model
    {
        get => _model;
        set
        {
            if (!SetProperty(ref _model, value ?? ""))
                return;
            if (!_isAliasTyped)
            {
                _alias = ModelProfileEditorViewModel.AliasFor(_model) ?? "";
                OnPropertyChanged(nameof(Alias));
            }

            _owner.OnModelsChanged();
        }
    }

    /// <summary>The name of the model among the connection's; not shown while the connection has one model.</summary>
    public string Alias
    {
        get => _alias;
        set
        {
            if (!SetProperty(ref _alias, value ?? ""))
                return;
            _isAliasTyped = true;
            _owner.OnModelsChanged();
        }
    }

    /// <summary>Takes the model out.</summary>
    public RelayCommand RemoveCommand { get; }
}

/// <summary>One rung of the ladder the guided setup proposes, cheapest first.</summary>
public sealed class GuidedRungViewModel
{
    internal GuidedRungViewModel(GuidedSetupViewModel owner, int index, int count, string name)
    {
        Name = name;
        MoveUpCommand = new RelayCommand(() => owner.MoveRung(index, -1), () => index > 0);
        MoveDownCommand = new RelayCommand(() => owner.MoveRung(index, 1), () => index < count - 1);
    }

    /// <summary>The setting's name.</summary>
    public string Name { get; }

    /// <summary>Moves the rung one up.</summary>
    public RelayCommand MoveUpCommand { get; }

    /// <summary>Moves the rung one down.</summary>
    public RelayCommand MoveDownCommand { get; }
}
