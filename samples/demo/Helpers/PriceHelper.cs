namespace Demo.Helpers;

public class PriceHelper
{
    public decimal ApplyTax(decimal amount) => amount * 1.2m;

    // Nothing calls this private method — SpecHygiene reports it as dead code.
    private decimal LegacyDiscount(decimal amount) => amount * 0.9m;
}
