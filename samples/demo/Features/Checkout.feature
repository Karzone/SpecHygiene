Feature: Checkout

  Scenario: Successful checkout
    Given the cart is empty
    When I add "Widget" to the cart
    Then checkout succeeds

  # Declared as a Scenario but carries an Examples table -> should be a Scenario Outline.
  Scenario: Tax applied by region
    Given the cart is empty
    Then tax is applied for "<region>"
    Examples:
      | region |
      | UK     |
      | DE     |

  # Scenario Outline using <redeemAmount> and <remaining>, neither an Examples column -> missing values.
  Scenario Outline: Redeem loyalty points
    Given a member with <points> points
    When they redeem <redeemAmount> points
    Then the balance is <remaining>
    Examples:
      | points |
      | 100    |
      | 250    |
