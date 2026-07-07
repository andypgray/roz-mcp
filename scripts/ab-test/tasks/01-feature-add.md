---
name: 01-feature-add
fixture: nopcommerce
verification:
  - {type: build, expected_exit: 0}
---
nopCommerce lets customers leave product reviews, but not order-level feedback. Add a new `OrderFeedback` feature: an entity storing a 1-5 star rating + optional comment, linked to `Order` and `Customer`.

Follow existing patterns — the new code should look like it was always there.

Scope: entity class, fluent entity mapping, service interface + implementation, DI registration. You do NOT need to write controllers, UI, or database migrations.

Stop when the solution compiles cleanly.
