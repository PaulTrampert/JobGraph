namespace JobGraph;

/// <summary>What happened to every job in a run.</summary>
/// <param name="Completed">Jobs whose Run finished without throwing.</param>
/// <param name="Failed">Jobs whose Run threw, with the exception it threw.</param>
/// <param name="Skipped">Jobs never started because something they depend on (directly or transitively) failed.</param>
public sealed record ScheduleResult(
    IReadOnlyCollection<string> Completed,
    IReadOnlyDictionary<string, Exception> Failed,
    IReadOnlyCollection<string> Skipped);
