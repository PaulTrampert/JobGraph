namespace JobGraph;

public sealed class UnknownDependencyException(string job, string dependency)
    : Exception($"Job '{job}' depends on '{dependency}', which is not in the graph.")
{
    public string Job { get; } = job;
    public string Dependency { get; } = dependency;
}
