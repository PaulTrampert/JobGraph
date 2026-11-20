namespace JobGraph;

public sealed class DependencyCycleException(string message) : Exception(message);
