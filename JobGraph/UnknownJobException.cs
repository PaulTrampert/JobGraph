namespace JobGraph;

public class UnknownJobException(string jobName) : Exception($"Unrecognized job: {jobName}");