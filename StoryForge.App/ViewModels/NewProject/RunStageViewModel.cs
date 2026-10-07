using CommunityToolkit.Mvvm.ComponentModel;
using StoryForge.Client;

namespace StoryForge.App.ViewModels.NewProject;

/// <summary>One stage in the run plan, with its gate.</summary>
public sealed partial class RunStageViewModel(PipelineStage stage, string name, string via, bool gate) : ObservableObject
{
    public PipelineStage Stage { get; } = stage;

    public int Number { get; } = (int)stage + 1;

    public string Name { get; } = name;

    /// <summary>What the stage does and with what, e.g. "Claude CLI · sources + fact sheet".</summary>
    public string Via { get; } = via;

    /// <summary>The storyboard and references gates cannot be switched off.</summary>
    public bool IsRequired { get; } = ProjectSetup.RequiredGates.Contains(stage);

    public bool CanChangeGate => !IsRequired;

    private bool _isGated = gate || ProjectSetup.RequiredGates.Contains(stage);

    public bool IsGated
    {
        get => _isGated;
        set
        {
            if (!IsRequired)
            {
                SetProperty(ref _isGated, value);
            }
        }
    }
}
