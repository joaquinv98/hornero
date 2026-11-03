using System.Collections.Concurrent;
using BillingOss.App.UseCases;

namespace BillingOss.App.Tests;

/// <summary>
/// VIES de mentira: valida los números que la prueba da por buenos y devuelve un número de consulta. El real
/// (<see cref="ViesClient"/>) corre solo en <see cref="ViesLiveTests"/>, con su variable de entorno.
/// </summary>
internal sealed class FakeVies : IVatNumberValidator
{
    private int requests;

    public ConcurrentDictionary<string, (string Name, string Address)> Valid { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Checked { get; } = [];

    public Task<VatNumberCheck> Check(string countryCode, string vatNumber, CancellationToken ct)
    {
        var key = countryCode + vatNumber;
        Checked.Add(key);
        var id = $"WAPIAAAAZ{Interlocked.Increment(ref requests):D6}";
        return Task.FromResult(Valid.TryGetValue(key, out var v)
            ? new VatNumberCheck(true, v.Name, v.Address, id, DateTimeOffset.UtcNow)
            : new VatNumberCheck(false, null, null, id, DateTimeOffset.UtcNow));
    }
}
