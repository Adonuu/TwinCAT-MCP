using System.Runtime.InteropServices;

namespace TwinCatMcp.Automation;

internal interface IConfigurationDispatch
{
    object? Invoke(object target, string member, params object?[] arguments);
    object? Get(object target, string member);
    object? GetIndexed(object target, string member, int index);
}

public sealed partial class XaeShellSession
{
    [ComImport, Guid("DA31B44A-2FE9-4011-A8DD-0706CD662244"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPlcCheckObjects
    {
        void ReservedNestedProject();
        void ReservedProjectInstance();
        void ReservedSaveAsLibrary();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool CheckAllObjects();
    }
    // Raw IUnknown interfaces cannot be invoked through the tree item's default IDispatch.
    // IIDs and vtable order checked against installed TCatSysManager.tlb 3.4. Reserved methods
    // only occupy unused slots and are never called. No installed interop assembly is required.
    [ComImport, Guid("2CB19F38-0DC6-4A65-8847-2B450248185E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPlcLibraryManager
    {
        void AddLibrary([MarshalAs(UnmanagedType.BStr)] string name, [MarshalAs(UnmanagedType.BStr)] string version, [MarshalAs(UnmanagedType.BStr)] string company);
        void RemoveReference([MarshalAs(UnmanagedType.BStr)] string name, [MarshalAs(UnmanagedType.BStr)] string version, [MarshalAs(UnmanagedType.BStr)] string company);
        void ReservedReferences();
        void ReservedInsertRepository();
        void ReservedRemoveRepository();
        void ReservedMoveRepository();
        void ReservedRepositories();
        void ReservedInstallLibrary();
        void ReservedUninstallLibrary();
        void ReservedScanLibraries();
        void AddPlaceholder([MarshalAs(UnmanagedType.BStr)] string placeholder, [MarshalAs(UnmanagedType.BStr)] string name,
            [MarshalAs(UnmanagedType.BStr)] string version, [MarshalAs(UnmanagedType.BStr)] string company);
    }

    [ComImport, Guid("47CD2055-314B-4F10-81C5-39E5BA2ED985"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPlcBootProject
    {
        void GenerateBootProject([MarshalAs(UnmanagedType.VariantBool)] bool activate);
        bool BootProjectAutostart
        {
            [return: MarshalAs(UnmanagedType.VariantBool)] get;
            [param: MarshalAs(UnmanagedType.VariantBool)] set;
        }
    }

    private sealed class NativeConfigurationDispatch : IConfigurationDispatch
    {
        public object? Invoke(object target, string member, params object?[] arguments)
        {
            switch (member)
            {
                case "Library.AddLibrary":
                    RetryIfComBusy(() => ((IPlcLibraryManager)target).AddLibrary((string)arguments[0]!, (string)arguments[1]!, (string)arguments[2]!));
                    return null;
                case "Library.AddPlaceholder":
                    RetryIfComBusy(() => ((IPlcLibraryManager)target).AddPlaceholder((string)arguments[0]!, (string)arguments[1]!, (string)arguments[2]!, (string)arguments[3]!));
                    return null;
                case "Library.RemoveReference":
                    RetryIfComBusy(() => ((IPlcLibraryManager)target).RemoveReference((string)arguments[0]!, (string)arguments[1]!, (string)arguments[2]!));
                    return null;
                case "Plc.SetBootProject":
                    RetryIfComBusy(() => ((IPlcBootProject)target).BootProjectAutostart = (bool)arguments[0]!);
                    RetryIfComBusy(() => ((IPlcBootProject)target).GenerateBootProject(true));
                    return null;
                case "Plc.CheckAllObjects":
                    return RetryIfComBusy(() => ((IPlcCheckObjects)target).CheckAllObjects());
                case "Configuration.SetTargetPlatform":
                    var manager = ComGet(target, "ConfigurationManager")!;
                    try { return ComMember(manager, "ActiveTargetPlatform", 0x4, arguments); }
                    finally { RunningXaeShells.Release(manager); }
                default: return ComInvoke(target, member, arguments);
            }
        }
        public object? Get(object target, string member) => ComGet(target, member);
        public object? GetIndexed(object target, string member, int index) => ComGetIndexed(target, member, index);
    }
}
