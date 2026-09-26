using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using IsTranscribe.Application.Diagnostics;
using Interop.UIAutomationClient;

namespace IsTranscribe.Platform.Windows;

internal interface IWindowsUiAutomationControlReader
{
    IReadOnlyList<string> ReadButtonNames(IntPtr windowHandle);
}

/// <summary>
/// Queries only button names below a known application window and runs on the provider's worker thread.
/// </summary>
/// <remarks>
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#signal-model
/// @spec spec://modules/app/FEAT-011-meeting-detection-v2#privacy
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsUiAutomationControlReader(BootstrapFileLogger logger) : IWindowsUiAutomationControlReader
{
    private const int MaxButtonsPerWindow = 256;

    private readonly BootstrapFileLogger _logger = logger;
    private bool _unavailableLogged;

    public IReadOnlyList<string> ReadButtonNames(IntPtr windowHandle)
    {
        CUIAutomation8? automation = null;
        IUIAutomationElement? root = null;
        IUIAutomationCondition? condition = null;
        IUIAutomationElementArray? elements = null;
        try
        {
            automation = new CUIAutomation8();
            root = automation.ElementFromHandle(windowHandle);
            condition = automation.CreatePropertyCondition(
                UIA_PropertyIds.UIA_ControlTypePropertyId,
                UIA_ControlTypeIds.UIA_ButtonControlTypeId);
            elements = root.FindAll(TreeScope.TreeScope_Descendants, condition);

            var count = Math.Min(elements.Length, MaxButtonsPerWindow);
            var names = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                IUIAutomationElement? element = null;
                try
                {
                    element = elements.GetElement(index);
                    var name = element.CurrentName;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name.Trim());
                    }
                }
                finally
                {
                    ReleaseComObject(element);
                }
            }

            return names;
        }
        catch (Exception exception)
        {
            if (!_unavailableLogged)
            {
                _unavailableLogged = true;
                _logger.LogEvent(
                    "Warning",
                    "DETECTION_UIA_DEGRADED",
                    "UI Automation meeting-control evidence is unavailable.",
                    metadata: new Dictionary<string, object?>
                    {
                        ["exception_type"] = exception.GetType().Name
                    });
            }

            return [];
        }
        finally
        {
            ReleaseComObject(elements);
            ReleaseComObject(condition);
            ReleaseComObject(root);
            ReleaseComObject(automation);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }
}
