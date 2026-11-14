using System.Runtime.InteropServices;

namespace PadSwap;

/// <summary>Thin wrapper over the Windows configuration manager (cfgmgr32) device tree.</summary>
internal static class DeviceTree
{
    private const uint Success = 0;
    private const int MaxDeviceIdLength = 200;

    /// <summary>Returns the device interface paths currently present for an interface class.</summary>
    public static List<string> GetInterfacePaths(Guid interfaceClass)
    {
        var paths = new List<string>();
        if (CM_Get_Device_Interface_List_SizeW(out uint length, in interfaceClass, null, 0) != Success || length <= 1)
            return paths;

        var buffer = new char[length];
        if (CM_Get_Device_Interface_ListW(in interfaceClass, null, buffer, length, 0) != Success)
            return paths;

        // The buffer is a double-null-terminated list of null-terminated strings.
        foreach (string path in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            paths.Add(path);
        return paths;
    }

    /// <summary>
    /// Converts "\\?\USB#VID_045E&amp;PID_028E#SERIAL#{guid}" into "USB\VID_045E&amp;PID_028E\SERIAL".
    /// </summary>
    public static string InstanceIdFromInterfacePath(string interfacePath)
    {
        string trimmed = interfacePath.StartsWith(@"\\?\", StringComparison.Ordinal) ? interfacePath[4..] : interfacePath;
        int guidStart = trimmed.LastIndexOf("#{", StringComparison.Ordinal);
        if (guidStart >= 0) trimmed = trimmed[..guidStart];
        return trimmed.Replace('#', '\\').ToUpperInvariant();
    }

    public static string? GetParentInstanceId(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint node, instanceId, 0) != Success) return null;
        if (CM_Get_Parent(out uint parent, node, 0) != Success) return null;
        return GetInstanceId(parent);
    }

    /// <summary>Returns the instance ids of every device below the given one (children, grandchildren...).</summary>
    public static List<string> GetDescendantInstanceIds(string instanceId)
    {
        var result = new List<string>();
        if (CM_Locate_DevNodeW(out uint node, instanceId, 0) == Success)
            CollectDescendants(node, result);
        return result;
    }

    private static void CollectDescendants(uint node, List<string> result)
    {
        if (CM_Get_Child(out uint child, node, 0) != Success) return;
        do
        {
            string? id = GetInstanceId(child);
            if (id != null) result.Add(id);
            CollectDescendants(child, result);
        }
        while (CM_Get_Sibling(out child, child, 0) == Success);
    }

    private static string? GetInstanceId(uint node)
    {
        var buffer = new char[MaxDeviceIdLength + 1];
        if (CM_Get_Device_IDW(node, buffer, (uint)buffer.Length, 0) != Success) return null;
        return new string(buffer).TrimEnd('\0').ToUpperInvariant();
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_List_SizeW(out uint length, in Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_Interface_ListW(in Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint node, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Child(out uint child, uint node, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Sibling(out uint sibling, uint node, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint node, [Out] char[] buffer, uint length, uint flags);
}
