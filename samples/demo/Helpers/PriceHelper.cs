namespace Demo.Helpers;

public class PriceHelper
{
    public decimal ApplyTax(decimal amount) => amount * 1.2m;

    // Nothing calls these private helpers — SpecHygiene reports them as dead code.
    private decimal LegacyDiscount(decimal amount) => amount * 0.9m;
    private decimal ObsoleteRounding(decimal amount) => System.Math.Round(amount, 0);
    private string FormatDeprecated(decimal amount) => $"${amount}";
}
