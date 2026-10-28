using Shop.Api;
using Shop.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(OrderStore.Seeded());

var app = builder.Build();

app.MapGet("/api/orders", (OrderStore store) =>
    store.All.Select(order => OrderResponse.From(order, store.TotalOf(order.Id))));

app.MapGet("/api/orders/{id:int}", (int id, OrderStore store) =>
    store.Find(id) is { } order
        ? Results.Ok(OrderResponse.From(order, store.TotalOf(id)))
        : Results.NotFound());

app.MapPost("/api/orders/{id:int}/lines", (int id, OrderLine line, OrderStore store) =>
{
    if (store.Find(id) is null)
        return Results.NotFound();
    if (line.Quantity <= 0)
        return Results.BadRequest("quantity must be positive");
    var order = store.AddLine(id, line);
    return Results.Ok(OrderResponse.From(order, store.TotalOf(id)));
});

app.Run();
