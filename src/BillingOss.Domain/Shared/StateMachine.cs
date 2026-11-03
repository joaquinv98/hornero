namespace BillingOss.Domain.Shared;

/// <summary>Transiciones permitidas entre estados. Quedarse en el mismo estado siempre vale: un aviso repetido no es un error.</summary>
public sealed class StateMachine<TState>
    where TState : struct, Enum
{
    private readonly HashSet<(TState From, TState To)> allowed;

    public StateMachine(params (TState From, TState To)[] transitions) => allowed = [.. transitions];

    public bool CanMove(TState from, TState to) => EqualityComparer<TState>.Default.Equals(from, to) || allowed.Contains((from, to));

    public TState Move(TState from, TState to) =>
        CanMove(from, to) ? to : throw new InvalidOperationException($"Transición no permitida: {from} a {to}.");
}
