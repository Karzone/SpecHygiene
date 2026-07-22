using Reqnroll;

namespace Demo.Steps;

[Binding]
public class CheckoutSteps
{
    // --- used by scenarios ---
    [Then("checkout succeeds")]
    public void ThenCheckoutSucceeds() { }

    // --- unused: no scenario references these bindings ---
    [Then("an audit entry is written")]
    public void ThenAnAuditEntryIsWritten() { }

    [Given("the store is closed for maintenance")]
    public void GivenTheStoreIsClosedForMaintenance() { }

    [When("the order is archived after 30 days")]
    public void WhenTheOrderIsArchived() { }
}
