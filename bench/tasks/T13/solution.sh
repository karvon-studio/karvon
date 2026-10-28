#!/usr/bin/env bash
set -euo pipefail
cat > CONTRIBUTING.md <<'MD'
# Contributing

Thanks for helping with the shop. This page says how to build it, test it and send a change.

## What you need

The .NET 10 SDK. Packages come from nuget.org only (see `nuget.config`).

## Build and test

```sh
dotnet build Shop.sln
dotnet test Shop.sln
```

To run the parts:

- the orders API: `dotnet run --project Shop.Api` (http://localhost:5290/api/orders/7)
- the checkout: `dotnet run --project Shop.Cli`

## The projects

- `Shop.Core`: orders, prices and the checkout. Keep it free of ASP.NET Core.
- `Shop.Api`: the minimal API over the orders.
- `Shop.Cli`: prices a cart at the checkout.
- `Web` and `Components`: the Blazor front end.
- `Tests`: xunit tests for `Shop.Core` and the components.

## Sending a change

1. Make a branch from `main`.
2. Keep the change small and about one thing; add or update tests for what it changes.
3. Make sure `dotnet build` has no warnings and `dotnet test` passes.
4. Write a commit message that says what changed and why, then open a pull request.

Prices are `decimal` everywhere; format them with `PriceFormatter`, not by hand.
MD
