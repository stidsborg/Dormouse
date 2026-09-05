namespace Dormouse;

public class DormouseContext
{
    internal FlowsCache FlowsCache { get; } = new();

    // The names flows write their types under, and the types those names resolve back to. Owned
    // here rather than held process-wide, so the caches live and die with the context.
    internal TypeHelper TypeHelper { get; } = new();
}