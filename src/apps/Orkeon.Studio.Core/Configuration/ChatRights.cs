namespace Orkeon.Studio.Core.Configuration;

/// <summary>
/// What an agent may do with a chat account: the six rights the engine's <c>ChatRights</c>
/// declares, with the same values. Written by name, as the engine reads it,
/// <c>"Read, Send"</c>: the engine refuses a number (Orkeon #464).
/// </summary>
[Flags]
public enum ChatRights
{
    /// <summary>Nothing granted: the engine refuses the account.</summary>
    None = 0,

    /// <summary>List the places, read, search, and list the members.</summary>
    Read = 1,

    /// <summary>Post messages and replies.</summary>
    Send = 2,

    /// <summary>Change the account's own messages.</summary>
    Edit = 4,

    /// <summary>Delete the account's own messages.</summary>
    Delete = 8,

    /// <summary>Add and remove reactions.</summary>
    React = 16,

    /// <summary>List meetings and read their transcripts.</summary>
    Meetings = 32,
}
