namespace Grains;

public class CounterGrain([PersistentState("count", "Default")] IPersistentState<CounterState> state)
    : Grain, ICounterGrain
{
    public async ValueTask<int> Increment()
    {
        state.State.Count++;
        await state.WriteStateAsync();
        return state.State.Count;
    }

    public ValueTask<int> GetCount() =>
        ValueTask.FromResult(state.State.Count);
}

[GenerateSerializer]
[Alias("Grains.CounterState")]
public class CounterState
{
    [Id(0)]
    public int Count { get; set; }
}