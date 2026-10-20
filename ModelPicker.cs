using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Omniwand;

/// <summary>
/// Phosphor Shell model dropdown shared by Wiz and the HUD. The choice lives on
/// <see cref="ArkStationClient.SelectedModel"/>, so every chat surface uses the same model;
/// each picker re-syncs when its window is summoned.
/// </summary>
internal sealed class ModelPicker : PhosphorComboBox
{
    private readonly ArkStationClient _ark;
    private bool _syncing;

    public ModelPicker(ArkStationClient ark)
    {
        _ark = ark;
        DropDownWidth = 300;

        Items.Add(_ark.SelectedModel);
        SelectedIndex = 0;

        SelectedIndexChanged += (_, _) =>
        {
            if (_syncing || SelectedItem is not string model) return;
            _ark.SelectedModel = model;
        };
    }

    /// <summary>Reloads the model list (gateway models when reachable) and selects the shared choice.</summary>
    public async Task RefreshAsync()
    {
        var models = (await _ark.GetAvailableModelsAsync()).ToList();
        if (!models.Contains(_ark.SelectedModel, StringComparer.OrdinalIgnoreCase))
            models.Insert(0, _ark.SelectedModel);

        _syncing = true;
        try
        {
            BeginUpdate();
            Items.Clear();
            foreach (var m in models) Items.Add(m);
            int idx = models.FindIndex(m => string.Equals(m, _ark.SelectedModel, StringComparison.OrdinalIgnoreCase));
            SelectedIndex = Math.Max(idx, 0);
        }
        finally
        {
            EndUpdate();
            _syncing = false;
            if (DroppedDown) DroppedDown = false;
        }
    }
}
