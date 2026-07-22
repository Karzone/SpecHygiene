namespace Demo.Helpers;

// Never referenced anywhere — SpecHygiene reports it as an unused class.
internal class OrphanReportBuilder
{
    public string Build() => "report";
}

// No implementations, never referenced — reported as an unused interface.
internal interface IUnusedValidator
{
    bool Validate(string input);
}
