using System.ComponentModel;

namespace PadSwap;

/// <summary>The USB personality the physical controller is currently using.</summary>
internal enum PadKind
{
    Xbox,
    PlayStation,
}

/// <summary>
/// Controller state in Xbox 360 terms. Buttons use the <see cref="PadButton"/> bit layout,
/// sticks are signed 16 bit with Y pointing up.
/// </summary>
internal readonly record struct PadState(
    ushort Buttons, byte LeftTrigger, byte RightTrigger, short LeftX, short LeftY, short RightX, short RightY);

internal interface IPhysicalPad : IDisposable
{
    PadKind Kind { get; }

    /// <summary>Device instance id of the node that was opened.</summary>
    string InstanceId { get; }

    /// <summary>Top node of the controller in the device tree; hiding it and everything below hides the controller.</summary>
    string RootInstanceId { get; }

    /// <summary>Waits for the next state. Returns false when the controller is gone or the wait was interrupted.</summary>
    bool TryReadNext(out PadState state);

    /// <summary>Makes a pending <see cref="TryReadNext"/> return promptly. Safe to call from another thread.</summary>
    void Interrupt();

    void SetVibration(byte largeMotor, byte smallMotor);
}

internal static class PhysicalPads
{
    /// <summary>Opens the first physical controller found, in either personality.</summary>
    public static IPhysicalPad? OpenFirst(Action<IEnumerable<string>>? beforeOpen = null)
    {
        var candidates = new List<(PadKind Kind, string Path)>();
        foreach (string path in XusbDevice.FindPhysicalInterfacePaths()) candidates.Add((PadKind.Xbox, path));
        foreach (string path in Ds4Device.FindPhysicalInterfacePaths()) candidates.Add((PadKind.PlayStation, path));

        foreach ((PadKind kind, string path) in candidates)
        {
            string instanceId = DeviceTree.InstanceIdFromInterfacePath(path);
            string root = kind == PadKind.Xbox ? instanceId : DeviceTree.GetParentInstanceId(instanceId) ?? instanceId;

            // The root node and every node below it are separate entry points to the same
            // controller (XInput, DirectInput, raw HID): all of them have to be hidden.
            var nodes = new List<string> { root };
            nodes.AddRange(DeviceTree.GetDescendantInstanceIds(root));
            beforeOpen?.Invoke(nodes);

            try
            {
                return kind == PadKind.Xbox ? XusbDevice.Open(path) : Ds4Device.Open(path, root);
            }
            catch (Win32Exception exception)
            {
                Log.Write($"Could not open {instanceId}: {exception.Message}");
            }
        }
        return null;
    }

    /// <summary>
    /// True for controllers created by software (ViGEm and similar): their bus is a
    /// root-enumerated or software device instead of a real USB hub.
    /// </summary>
    public static bool IsVirtual(string instanceId)
    {
        string? ancestor = instanceId;
        for (int level = 0; level < 2; level++)
        {
            ancestor = DeviceTree.GetParentInstanceId(ancestor);
            if (ancestor == null) return true;
            if (ancestor.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) ||
                ancestor.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
