namespace NKeeper.Infrastructure.Access;

/// <summary>
/// Gives a (non-administrator) user access to hosts: either one specific host, or every host in
/// a group. Groups are tags, as in the tree's "group by": <c>site: Datacenter A</c>, or
/// <c>site</c> with no value for any host that has a site tag. Group grants follow the tags, so
/// hosts tagged later are included automatically. Administrators can access every host.
/// </summary>
public sealed class HostAccessGrant
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Set for a single-host grant; otherwise <see cref="TagName"/> is set.</summary>
    public Guid? HostId { get; set; }

    /// <summary>Set for a group grant. Matched case-insensitively.</summary>
    public string? TagName { get; set; }

    /// <summary>For group grants: the tag value to match, or null for any value.</summary>
    public string? TagValue { get; set; }

    public HostAccessLevel Level { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public Guid? CreatedByUserId { get; set; }
}

public enum HostAccessLevel
{
    /// <summary>See the host and connect to it with its credentials.</summary>
    Use,

    /// <summary>Also edit and delete the host.</summary>
    Manage,
}
