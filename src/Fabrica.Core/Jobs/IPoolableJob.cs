namespace Fabrica.Core.Jobs;

/// <summary>
/// Static factory interface for jobs that can be allocated by <see cref="JobPool{TJob}"/>.
/// Replaces the <c>new()</c> constraint so that the pool can pass the owning
/// <see cref="JobScheduler"/> at creation time, binding the job to its scheduler once and
/// for all.
/// </summary>
public interface IPoolableJob<TSelf> where TSelf : Job
{
    static abstract TSelf Create(JobScheduler scheduler);
}
