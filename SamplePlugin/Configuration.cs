using Dalamud.Configuration;
using System;
using System.Numerics;

namespace FROG;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;
    public bool SomePropertyToBeSavedAndWithADefault { get; set; } = true;

    public bool EnableRetainerRowHighlight { get; set; } = true;

    public bool EnableInventoryExecutionHighlight { get; set; } = true;

    public bool EnableExecutionQuantityOverlay { get; set; } = true;

    public bool EnableFreeCompanyObservationProbe { get; set; } = false;

    public Vector4 RetainerRowHighlightColor { get; set; } =
        new(1.0f, 0.72f, 0.10f, 0.90f);

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
