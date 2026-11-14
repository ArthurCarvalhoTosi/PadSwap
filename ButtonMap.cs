using System.Text.Json;

namespace PadSwap;

/// <summary>Button bits as they appear in the XUSB report (same layout as XINPUT_GAMEPAD plus Guide).</summary>
[Flags]
internal enum PadButton : ushort
{
    DpadUp = 0x0001,
    DpadDown = 0x0002,
    DpadLeft = 0x0004,
    DpadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    Guide = 0x0400,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

/// <summary>
/// Maps each physical button to the button reported on the virtual controller.
/// Loaded from padswap.json next to the executable, which has one section per controller
/// personality ("xbox", "playstation"); buttons not listed pass through unchanged.
/// </summary>
internal sealed class ButtonMap
{
    private const string FileName = "padswap.json";

    private static readonly Dictionary<string, string> FaceButtonSwap = new()
    {
        ["A"] = "B",
        ["B"] = "A",
        ["X"] = "Y",
        ["Y"] = "X",
    };

    // Index = bit position in the physical report, value = bit mask to set on the virtual report.
    private readonly ushort[] _targetMaskByBit = new ushort[16];

    private ButtonMap(IReadOnlyDictionary<string, string> buttons)
    {
        for (int bit = 0; bit < 16; bit++)
            _targetMaskByBit[bit] = (ushort)(1 << bit);

        foreach ((string source, string target) in buttons)
        {
            if (!Enum.TryParse(source, ignoreCase: true, out PadButton sourceButton) ||
                !Enum.TryParse(target, ignoreCase: true, out PadButton targetButton))
            {
                Log.Write($"Ignoring unknown button mapping \"{source}\" -> \"{target}\".");
                continue;
            }
            _targetMaskByBit[int.TrailingZeroCount((ushort)sourceButton)] = (ushort)targetButton;
        }
    }

    public static ButtonMap Load(PadKind kind)
    {
        string section = kind == PadKind.Xbox ? "xbox" : "playstation";
        string path = Path.Combine(AppContext.BaseDirectory, FileName);
        try
        {
            if (!File.Exists(path))
            {
                // In PlayStation mode the controller is expected to report by position already.
                var defaults = new { xbox = FaceButtonSwap, playstation = new Dictionary<string, string>() };
                File.WriteAllText(path, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            var buttons = new Dictionary<string, string>();
            if (document.RootElement.TryGetProperty(section, out JsonElement element))
            {
                foreach (JsonProperty property in element.EnumerateObject())
                    buttons[property.Name] = property.Value.GetString() ?? property.Name;
            }
            return new ButtonMap(buttons);
        }
        catch (Exception exception)
        {
            Log.Write($"Could not read {FileName}, using the default map: {exception.Message}");
            return new ButtonMap(kind == PadKind.Xbox ? FaceButtonSwap : new Dictionary<string, string>());
        }
    }

    public ushort Apply(ushort physicalButtons)
    {
        ushort result = 0;
        while (physicalButtons != 0)
        {
            int bit = int.TrailingZeroCount(physicalButtons);
            result |= _targetMaskByBit[bit];
            physicalButtons &= (ushort)(physicalButtons - 1);
        }
        return result;
    }
}
