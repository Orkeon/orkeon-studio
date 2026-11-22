namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// What an agent may do with a drive account: the six rights the engine's <c>DriveRights</c>
/// declares, with the same values. Written by name, as the engine reads it,
/// <c>"Read, Write, Organize"</c>: the engine refuses a number (Orkeon #464).
/// </summary>
[Flags]
public enum DriveRights
{
    /// <summary>Nothing granted: the engine refuses the account.</summary>
    None = 0,

    /// <summary>List, search, read and download files.</summary>
    Read = 1,

    /// <summary>Upload files, replace their content and create folders.</summary>
    Write = 2,

    /// <summary>Move and rename files and folders.</summary>
    Organize = 4,

    /// <summary>Share files with the allowed recipients, and create public links when allowed.</summary>
    Share = 8,

    /// <summary>Move files and folders to the trash.</summary>
    Delete = 16,

    /// <summary>Delete files and folders permanently.</summary>
    Purge = 32,
}
