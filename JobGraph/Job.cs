namespace JobGraph;

/// <summary>
/// A unit of work. <see cref="DependsOn"/> names the jobs that must complete successfully
/// before this one may start.
/// </summary>
public sealed record Job(string Name, IReadOnlyCollection<string> DependsOn, Func<CancellationToken, Task> Run);
