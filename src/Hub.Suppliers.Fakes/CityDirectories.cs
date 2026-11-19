using Hub.Abstractions;

namespace Hub.Suppliers.Fakes;

internal sealed partial class SupplierAClient : ICityDirectory
{
    public ValueTask<Result<SupplierDirectory>> GetDirectoryAsync(CancellationToken cancellationToken) =>
        FakeCityDirectoryCall.Answer(FakeCityDirectories.A, cancellationToken);
}

internal sealed partial class SupplierBClient : ICityDirectory
{
    public ValueTask<Result<SupplierDirectory>> GetDirectoryAsync(CancellationToken cancellationToken) =>
        FakeCityDirectoryCall.Answer(FakeCityDirectories.B, cancellationToken);
}

internal sealed partial class SupplierCClient : ICityDirectory
{
    public ValueTask<Result<SupplierDirectory>> GetDirectoryAsync(CancellationToken cancellationToken) =>
        FakeCityDirectoryCall.Answer(FakeCityDirectories.C, cancellationToken);
}

internal static class FakeCityDirectoryCall
{
    public static ValueTask<Result<SupplierDirectory>> Answer(SupplierDirectory directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Result.Ok(directory));
    }
}
