using Orkeon.Studio.Core.Tools;

namespace Orkeon.Studio.Core.Localization;

/// <summary>The string key of a tool family's label, shared by the Settings screen and the team editors.</summary>
public static class ToolFamilyLabels
{
    /// <summary>
    /// The key of <paramref name="familyKey"/>'s label; the family key itself for a family no
    /// label is written for yet, which the catalogue lookup then shows as it is (a test catches
    /// it), rather than borrowing another family's label.
    /// </summary>
    public static string KeyFor(string familyKey) => familyKey switch
    {
        ToolCatalog.WebFamily => StudioStringKeys.ToolFamilyWeb,
        ToolCatalog.SearchFamily => StudioStringKeys.ToolFamilySearch,
        ToolCatalog.FilesFamily => StudioStringKeys.ToolFamilyFiles,
        ToolCatalog.DataFamily => StudioStringKeys.ToolFamilyData,
        ToolCatalog.EmailFamily => StudioStringKeys.ToolFamilyEmail,
        ToolCatalog.DriveFamily => StudioStringKeys.ToolFamilyDrive,
        ToolCatalog.ChatFamily => StudioStringKeys.ToolFamilyChat,
        ToolCatalog.CodeFamily => StudioStringKeys.ToolFamilyCode,
        ToolCatalog.SessionFamily => StudioStringKeys.ToolFamilySession,
        ToolCatalog.EventsFamily => StudioStringKeys.ToolFamilyEvents,
        ToolCatalog.AnalysisFamily => StudioStringKeys.ToolFamilyAnalysis,
        ToolCatalog.CollaborationFamily => StudioStringKeys.ToolFamilyCollaboration,
        ToolCatalog.DecisionFamily => StudioStringKeys.ToolFamilyDecision,
        ToolCatalog.McpFamily => StudioStringKeys.ToolFamilyMcp,
        ToolCatalog.MountsFamily => StudioStringKeys.ToolFamilyMounts,
        _ => familyKey,
    };
}
