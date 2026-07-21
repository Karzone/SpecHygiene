using Reqnroll;

namespace Demo.Steps;

[Binding]
public class CartSteps
{
    [Given("the cart is empty")]
    public void GivenTheCartIsEmpty() { }

    [When("I add {string} to the cart")]
    public void WhenIAddToTheCart(string item) { }

    [Then("the cart contains {int} item")]
    public void ThenTheCartContains(int count) { }

    // No scenario ever uses this step — SpecHygiene reports it as an unused step definition.
    [Then("the cart is sorted alphabetically")]
    public void ThenTheCartIsSortedAlphabetically() { }
}
