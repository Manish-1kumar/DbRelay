namespace DbRelay;

/// <summary>
/// Sets the logical message type name stored with messages of the decorated type.
/// </summary>
/// <remarks>
/// Without this attribute (or an explicit mapping via <c>MapMessageType&lt;T&gt;()</c>), the type's full CLR name is used.
/// The full CLR name changes when the class is renamed or moved, so set an explicit, stable name for messages consumed by other systems.
/// The name is a label only; it is never used to create types.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class OutboxMessageTypeAttribute : Attribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="name">The logical name (1 to 256 characters), for example <c>orders.created</c>.</param>
    public OutboxMessageTypeAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>The logical message type name.</summary>
    public string Name { get; }
}
