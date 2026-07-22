Feature: Shopping cart

  Scenario: Add an item to the cart
    Given the cart is empty
    When I add "Widget" to the cart
    Then the cart contains 1 item

  Scenario: Empty the cart
    Given the cart is empty
    When I add "Widget" to the cart
    Then the cart is empty again

  # Declared as a Scenario but carries an Examples table -> should be a Scenario Outline.
  Scenario: Add several items
    Given the cart is empty
    When I add "<item>" to the cart
    Then the cart contains <count> item
    Examples:
      | item   | count |
      | Widget | 1     |
      | Gadget | 2     |

  # Scenario Outline whose step uses <expectedTotal>, which is not an Examples column -> missing value.
  Scenario Outline: Checkout total after coupon
    Given the cart has <itemCount> items
    When I apply coupon "<coupon>"
    Then the total is <expectedTotal>
    Examples:
      | itemCount | coupon |
      | 3         | SAVE10 |
      | 5         | HALF   |
