namespace Pishkhan.SharedKernel;

/// <summary>
/// Strongly typed IDs are <c>readonly record struct XxxId(Guid Value)</c> implementing this interface.
/// New values are UUID v7 (time ordered), which keeps B-tree inserts local.
/// </summary>
public interface IStronglyTypedId
{
    Guid Value { get; }
}

public static class Uuid
{
    public static Guid NewV7() => Guid.CreateVersion7();

    public static Guid NewV7(DateTimeOffset at) => Guid.CreateVersion7(at);
}
