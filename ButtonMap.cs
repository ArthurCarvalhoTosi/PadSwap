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
/// Stored in padswap.json next to the executable, which has one section per controller
/// personality ("xbox", "playstation"); buttons not listed pass through unchanged.
/// </summary>
internal sealed class ButtonMap
{
    private const string FileName = "padswap.json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static readonly IReadOnlyDictionary<PadButton, PadButton> FaceButtonSwap = new Dictionary<PadButton, PadButton>
    {
        [PadButton.A] = PadButton.B,
        [PadButton.B] = PadButton.A,
        [PadButton.X] = PadButton.Y,
        [PadButton.Y] = PadButton.X,
    };

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    // Index = bit position in the physical report, value = bit mask to set on the virtual report.
    private readonly ushort[] _targetMaskByBit = new ushort[16];

    private ButtonMap(IReadOnlyDictionary<PadButton, PadButton> entries)
    {
        for (int bit = 0; bit < 16; bit++)
            _targetMaskByBit[bit] = (ushort)(1 << bit);
        foreach ((PadButton source, PadButton target) in entries)
            _targetMaskByBit[int.TrailingZeroCount((ushort)source)] = (ushort)target;
    }

    public static ButtonMap Load(PadKind kind) => new(ReadEntries(kind));

    /// <summary>Default map: Xbox mode needs the face button swap, PlayStation mode already reports by position.</summary>
    public static IReadOnlyDictionary<PadButton, PadButton> DefaultEntries(PadKind kind) =>
        kind == PadKind.Xbox ? FaceButtonSwap : new Dictionary<PadButton, PadButton>();

    /// <summary>Returns the remapped buttons for one controller personality (identity entries are omitted).</summary>
    public static IReadOnlyDictionary<PadButton, PadButton> ReadEntries(PadKind kind)
    {
        try
        {
            if (!File.Exists(FilePath))
                return DefaultEntries(kind);

            var entries = new Dictionary<PadButton, PadButton>();
            if (ReadFile().TryGetValue(SectionName(kind), out Dictionary<string, string>? section))
            {
                foreach ((string source, string target) in section)
                {
                    if (Enum.TryParse(source, ignoreCase: true, out PadButton sourceButton) &&
                        Enum.TryParse(target, ignoreCase: true, out PadButton targetButton))
                        entries[sourceButton] = targetButton;
                    else
                        Log.Write($"Ignoring unknown button mapping \"{source}\" -> \"{target}\".");
                }
            }
            return entries;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write($"Could not read {FileName}, using the default map: {exception.Message}");
            return DefaultEntries(kind);
        }
    }

    /// <summary>Writes one personality's section, keeping the other one as it is.</summary>
    public static void Save(PadKind kind, IReadOnlyDictionary<PadButton, PadButton> entries)
    {
        var file = new Dictionary<string, Dictionary<string, string>>();
        foreach (PadKind each in Enum.GetValues<PadKind>())
        {
            IReadOnlyDictionary<PadButton, PadButton> source = each == kind ? entries : ReadEntries(each);
            file[SectionName(each)] = source
                .Where(entry => entry.Key != entry.Value)
                .ToDictionary(entry => entry.Key.ToString(), entry => entry.Value.ToString());
        }
        File.WriteAllText(FilePath, JsonSerializer.Serialize(file, WriteOptions));
    }

    private static Dictionary<string, Dictionary<string, string>> ReadFile() =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(FilePath)) ?? [];

    private static string SectionName(PadKind kind) => kind == PadKind.Xbox ? "xbox" : "playstation";

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
