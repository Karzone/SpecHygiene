Feature: Shopping cart

  Scenario: Add an item to the cart
    Given the cart is empty
    When I add "Widget" to the cart
    Then the cart contains 1 item

  Scenario: Adding an item to the basket
    Given the cart is empty
    When I add "Widget" to the cart
    Then the cart contains 1 item

  Scenario: Checkout shows the total
    Given the cart is empty
    When I add "Widget" to the cart
    Then the total is <total>
    Examples:
      | total |
      | 9.99  |
