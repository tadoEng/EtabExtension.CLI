namespace EtabExtension.CLI.Shared.Infrastructure.Etabs;

/// <summary>
/// Reports safe cancellation/progress boundaries around ETABS calls. Implementations
/// must never abort a delegate after it has started; cancellation is checked only
/// before and after each step.
/// </summary>
public interface IEtabsOperationProgress
{
    Task<T> RunStepAsync<T>(
        int index,
        int total,
        string csiOperation,
        Func<Task<T>> action);
}
