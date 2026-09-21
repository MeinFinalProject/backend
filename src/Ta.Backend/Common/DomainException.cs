namespace Ta.Backend.Common;

public sealed class DomainException(string code, int status = 400) : Exception(code)
{
    public int Status { get; } = status;
    public static void Require(bool condition, string code, int status = 400)
    {
        if (!condition) throw new DomainException(code, status);
    }
}
