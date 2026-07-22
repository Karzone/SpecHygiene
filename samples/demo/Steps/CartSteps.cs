using Reqnroll;

namespace Demo.Steps;

[Binding]
public class CartSteps
{
    // --- used by scenarios ---
    [Given("the cart is empty")]
    public void GivenTheCartIsEmpty() { }

    [When("I add {string} to the cart")]
    public void WhenIAddToTheCart(string item) { }

    [Then("the cart contains {int} item")]
    public void ThenTheCartContains(int count) { }

    [Then("the cart is empty again")]
    public void ThenTheCartIsEmptyAgain() { }

    // --- unused: no scenario references these bindings ---
    [Then("the cart is sorted alphabetically")]
    public void ThenTheCartIsSortedAlphabetically() { }

    [Given("the user is a premium member")]
    public void GivenTheUserIsAPremiumMember() { }

    [When("the session times out")]
    public void WhenTheSessionTimesOut() { }
}
